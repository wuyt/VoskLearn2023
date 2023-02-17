/*
 * 
 * 代码根据 https://github.com/alphacep/vosk-unity-asr 修改
 * Vosk更多内容，请查看 https://github.com/alphacep/vosk-unity-asr
 * 
 * 生活在他方
 * 5140075@qq.com
 * https://space.bilibili.com/17442179
 * 
 */
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Profiling;
using UnityEngine;
using Vosk;

/// <summary>
/// Vosk语音识别，每次识别
/// </summary>
public class VoskSpeechToTextOnce : MonoBehaviour
{
    /// <summary>
    /// 模型路径，相对于StreamingAssets目录。
    /// </summary>
    /// <remarks>模型下载：https://alphacephei.com/vosk/models 。下载后解压到StreamingAssets目录</remarks>
    public string ModelPath = "vosk-model-small-cn-0.22";

    /// <summary>
    /// 语音输入
    /// </summary>
    public VoiceProcessor VoiceProcessor;

    /// <summary>
    /// 最大备选数
    /// </summary>
    public int MaxAlternatives = 3;

    /// <summary>
    /// 最长录音长度
    /// </summary>
    public float MaxRecordLength = 15;

    /// <summary>
    /// 关键词列表
    /// </summary>
    List<string> _keyPhrases = new List<string>();

    /// <summary>
    /// Vosk模型
    /// </summary>
    Model _model;

    /// <summary>
    /// 识别器
    /// </summary>
    VoskRecognizer _recognizer;

    /// <summary>
    /// 识别器就绪，中途需要更改识别器设置，例如修改关键词，用到这个变量
    /// </summary>
    bool _recognizerReady;

    /// <summary>
    /// 存储收到的语音信息直到停止录音
    /// </summary>
    readonly List<short> _buffer = new List<short>();

    /// <summary>
    /// 状态变更事件
    /// </summary>
    public Action<string> OnStatusUpdated;

    /// <summary>
    /// 识别完成事件
    /// </summary>
    public Action<string> OnTranscriptionResult;

    /// <summary>
    /// 解压后的目录
    /// </summary>
    string _decompressedModelPath;

    /// <summary>
    /// 识别语法，用识别关键词转换的json
    /// </summary>
    string _grammar = "";

    /// <summary>
    /// 始化标识
    /// </summary>
    bool _isInitializing;

    /// <summary>
    /// 完成初始化
    /// </summary>
    bool _didInit;

    /// <summary>
    /// 开始录音时间
    /// </summary>
    float _startRecordTime;

    /// <summary>
    /// 字符串结果锁
    /// </summary>
    readonly object _resultLock = new object();

    /// <summary>
    /// Vosk返回的Json字符串
    /// </summary>
    string _threadedRecognitionResult;

    /// <summary>
    /// 识别器返回的字符串
    /// </summary>
    string _result;

    /// <summary>
    /// 麦克风数据
    /// </summary>
    readonly ConcurrentQueue<short[]> _threadedBufferQueue = new ConcurrentQueue<short[]>();

    /// <summary>
    /// 处理过程锁
    /// </summary>
    int _threadSafeBoolBackValue = 0;

    /// <summary>
    /// 正在处理数据的标识
    /// </summary>
    public bool StreamingIsBusy
    {
        get => (Interlocked.CompareExchange(ref _threadSafeBoolBackValue, 1, 1) == 1);
        set
        {
            if (value) Interlocked.CompareExchange(ref _threadSafeBoolBackValue, 1, 0);
            else Interlocked.CompareExchange(ref _threadSafeBoolBackValue, 0, 1);
        }
    }

    static readonly ProfilerMarker voskRecognizerCreateMarker = new ProfilerMarker("VoskRecognizer.Create");
    static readonly ProfilerMarker voskRecognizerReadMarker = new ProfilerMarker("VoskRecognizer.AcceptWaveform");

    /// <summary>
    /// 开始识别
    /// </summary>
    public void StartRecognition()
    {
        _keyPhrases = new List<string>();
        VoiceProcessor.StartRecording();
    }

    /// <summary>
    /// 开始识别
    /// </summary>
    /// <param name="keyPhrases">词组列表</param>
    public void StartRecognition(List<string> keyPhrases)
    {
        _keyPhrases = keyPhrases;
        VoiceProcessor.StartRecording();
    }

    /// <summary>
    /// 停止识别
    /// </summary>
    public void StopRecognition()
    {
        VoiceProcessor.StopRecording();
        _recognizerReady = false;
    }

    void Start()
    {
        StartVoskStt();
    }

    void Update()
    {
        lock (_resultLock)
        {
            if (_result != _threadedRecognitionResult)
            {
                OnStatusUpdated?.Invoke("Received Result");
                _result = _threadedRecognitionResult;
                OnTranscriptionResult?.Invoke(_result);
            }
        }
    }

    /// <summary>
    /// 接收 VoiceProcessor传过来的语音数据
    /// </summary>
    /// <param name="samples"></param>
    private void VoiceProcessorOnOnFrameCaptured(short[] samples)
    {
        //Only change the state if we are starting fresh
        if (StreamingIsBusy == false && _buffer.Count == 0)
        {
            _startRecordTime = Time.time;
            OnStatusUpdated?.Invoke("Listening");
        }

        if (Time.time - _startRecordTime > MaxRecordLength)
        {
            VoiceProcessorOnOnRecordingStop();
            return;
        }
        else
        {
            _buffer.AddRange(samples);
        }
    }

    /// <summary>
    /// 停止录音响应事件，在这里开始识别
    /// </summary>
    private void VoiceProcessorOnOnRecordingStop()
    {
        if (StreamingIsBusy)
            return;

        OnStatusUpdated?.Invoke("Fetching Result");
        StreamingIsBusy = true;
        _threadedBufferQueue.Enqueue(_buffer.ToArray());
        Task.Run(ThreadedWork).ConfigureAwait(false);

        _buffer.Clear();
    }

    /// <summary>
    /// 开始初始化Vosk
    /// </summary>
    /// <param name="modelPath">模型路径</param>
    /// <param name="maxAlternatives">最大备选数</param>
    public void StartVoskStt(string modelPath = default, int maxAlternatives = 3)
    {
        if (_isInitializing)
        {
            Debug.LogError("Initializing in progress!");
            return;
        }
        if (_didInit)
        {
            Debug.LogError("Vosk has already been initialized!");
            return;
        }

        if (!string.IsNullOrEmpty(modelPath))
        {
            ModelPath = modelPath;
        }

        MaxAlternatives = maxAlternatives;
        StartCoroutine(DoStartVoskStt());
    }

    /// <summary>
    /// 继续准备Vosk
    /// </summary>
    /// <returns></returns>
    private IEnumerator DoStartVoskStt()
    {
        _isInitializing = true;
        yield return WaitForMicrophoneInput();

        //如果模型是.zip，需要解压到特定目录，原脚本是在这里执行
        //这里不用解压，我把解压过程省去了。如果在安卓或者苹果下，需要解压，因为StreamingAssets目录权限不足。

        _decompressedModelPath = string.Format("{0}/{1}", Application.streamingAssetsPath, ModelPath);

        OnStatusUpdated?.Invoke("Loading Model from: " + _decompressedModelPath);
        //Vosk.Vosk.SetLogLevel(0);
        _model = new Model(_decompressedModelPath);

        yield return null;

        OnStatusUpdated?.Invoke("Initialized");
        VoiceProcessor.OnFrameCaptured += VoiceProcessorOnOnFrameCaptured;
        VoiceProcessor.OnRecordingStop += VoiceProcessorOnOnRecordingStop;

        _isInitializing = false;
        _didInit = true;
    }

    /// <summary>
    /// 跟新识别语法，[unk]用于代替其他词。
    /// </summary>
    private void UpdateGrammar()
    {
        _grammar = "";

        if (_keyPhrases.Count > 0)
        {
            foreach (string keyphrase in _keyPhrases)
            {
                _grammar = string.Format("{0},\"{1}\"", _grammar, keyphrase.ToLower());
            }
            //如果这里添加了[unk]，当识别的语音中不包括 _keyPhrases 中的词的时候，会添加[unk]。
            //如果注释这行不添加[unk]，会强行用 _keyPhrases 中的词来代替。
            _grammar = string.Format("{0},\"{1}\"", _grammar, "[unk]");

            _grammar = string.Format("[{0}]", _grammar.Remove(0, 1));
        }
    }

    /// <summary>
    /// 等待麦克风可用
    /// </summary>
    /// <returns></returns>
    private IEnumerator WaitForMicrophoneInput()
    {
        while (Microphone.devices.Length <= 0)
            yield return null;
    }

    /// <summary>
    /// 在线程中识别语音
    /// </summary>
    /// <returns></returns>
    private async Task ThreadedWork()
    {
        StreamingIsBusy = true;
        voskRecognizerCreateMarker.Begin();
        if (!_recognizerReady)
        {
            UpdateGrammar();

            //是否设置识别的词语
            if (string.IsNullOrEmpty(_grammar))
            {
                _recognizer = new VoskRecognizer(_model, 16000.0f);
            }
            else
            {
                _recognizer = new VoskRecognizer(_model, 16000.0f, _grammar);
            }

            _recognizer.SetMaxAlternatives(MaxAlternatives);
            _recognizer.SetWords(true);//注释这行则不会显示没个词的识别情况，只显示整句内容
            _recognizerReady = true;

            await Task.Delay(100);
        }

        voskRecognizerCreateMarker.End();

        voskRecognizerReadMarker.Begin();

        while (_threadedBufferQueue.Count > 0)
        {
            if (_threadedBufferQueue.TryDequeue(out short[] voiceResult))
            {
                _recognizer.AcceptWaveform(voiceResult, voiceResult.Length);
                lock (_resultLock)
                {
                    _threadedRecognitionResult = _recognizer.Result();
                }
            }
        }

        voskRecognizerReadMarker.End();

        //We wait 2seconds to avoid getting a partial result when processing audio immediately after.
        await Task.Delay(2000);
        StreamingIsBusy = false;

    }
}
