using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Text;

namespace MORLA.XR
{

public class LogDisplayNetwork : MonoBehaviour
{
    [SerializeField] private TMP_Text logText;
    [SerializeField] private Text legacyLogText;
    [SerializeField] private int maxDisplayedMessages = 1;
    // New: the source of logs this display should show
    [SerializeField] private string logSource;
    private readonly StringBuilder _sb = new StringBuilder(1024);

    private void Update()
    {
        DisplayLog();
    }

    private void DisplayLog()
    {
        if (LogManager.Instance == null)
        {
            SetText(string.Empty);
            return;
        }

        var pinned = LogManager.Instance.GetPinnedMessages(logSource);
        var logMessages = LogManager.Instance.GetLogMessages(logSource);
        int startIdx = logMessages != null ? Mathf.Max(0, logMessages.Count - maxDisplayedMessages) : 0;
        _sb.Clear();
        if (pinned != null)
        {
            for (int i = 0; i < pinned.Count; i++)
            {
                _sb.AppendLine(pinned[i]);
            }
        }
        if (logMessages != null)
        {
            for (int i = startIdx; i < logMessages.Count; i++)
            {
                _sb.AppendLine(logMessages[i]);
            }
        }
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
