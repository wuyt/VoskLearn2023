using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class OnceDemo : MonoBehaviour
{
    public VoskSpeechToTextOnce voskSpeechToText;

    public List<string> keyPhrases = new List<string>();

    private void Start()
    {
        voskSpeechToText.OnStatusUpdated += OnStatusUpdated;
        voskSpeechToText.OnTranscriptionResult += OnTranscriptionResult;
    }

    private void OnTranscriptionResult(string value)
    {
        Debug.Log(value);
    }

    private void OnStatusUpdated(string value)
    {
        Debug.Log("Status changed=>" + value);
    }


    public void StartRecognition()
    {
        voskSpeechToText.StartRecognition();
    }

    public void StartRecognitionWithKeyPhrases()
    {
        if (keyPhrases.Count == 0)
        {
            Debug.LogError("��Ҫ���ùؼ���");
            return;
        }
        voskSpeechToText.StartRecognition(keyPhrases);
    }

    public void StopRecognition()
    {
        voskSpeechToText.StopRecognition();
    }
}
