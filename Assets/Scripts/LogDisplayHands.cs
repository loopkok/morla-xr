using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Text;
using System.Collections.Generic;

namespace MORLA.XR
{

public class LogDisplayHands : MonoBehaviour
{
    [SerializeField] private TMP_Text logText;
    [SerializeField] private Text legacyLogText;

    [SerializeField] private string logSource; 

    // We don't need to serialize this anymore since we overwrite it in code,
    // but keeping it as a private variable to store the current limit.
    private int _currentMaxMessages = 1;

    // Optimization: Cache StringBuilder to avoid memory garbage
    private StringBuilder _sb = new StringBuilder(1000);

    private float _nextRefresh;
    private void Update()
    {
        if (AppManager.Instance == null || !AppManager.Instance.ShowDiagnostics)
        {
            if (logText != null) logText.enabled = false;
            if (legacyLogText != null) legacyLogText.enabled = false;
            return;
        }
        if (Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + 0.15f;
        if (logText != null) logText.enabled = true;
        if (legacyLogText != null) legacyLogText.enabled = true;
        UpdateDisplayLimit();
        DisplayLog();
    }

    private void UpdateDisplayLimit()
    {
        // Each panel now shows only its own hand's latest block. Head / IOBT /
        // controller are pinned separately, so a single message avoids clutter.
        _currentMaxMessages = 1;
    }

    private void DisplayLog()
    {
        if (LogManager.Instance == null)
        {
            SetText(string.Empty);
            return;
        }

        // 1. Pinned live poses (controller / IOBT / head) stay on screen.
        var pinned = LogManager.Instance.GetPinnedMessages(logSource);
        var messages = LogManager.Instance.GetLogMessages(logSource);

        if ((pinned == null || pinned.Count == 0) && (messages == null || messages.Count == 0))
        {
            SetText(string.Empty);
            return;
        }

        // 2. Clear the builder
        _sb.Clear();

        if (pinned != null)
        {
            for (int i = 0; i < pinned.Count; i++)
            {
                _sb.AppendLine(pinned[i]);
            }
        }

        if (messages != null && messages.Count > 0)
        {
            int startIdx = Mathf.Max(0, messages.Count - _currentMaxMessages);
            for (int i = startIdx; i < messages.Count; i++)
            {
                _sb.AppendLine(messages[i]);
            }
        }

        // 5. Update UI
        SetText(_sb.ToString());
    }

    private void SetText(string text)
    {
        if (logText != null)
        {
            logText.text = text;
        }
        if (legacyLogText != null)
        {
            legacyLogText.text = text;
        }
    }
}
}
