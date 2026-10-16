using UnityEngine;
using System.Collections.Generic;

namespace MORLA.XR
{

public class LogManager : MonoBehaviour
{
    public static LogManager Instance { get; private set; }

    // Dictionary mapping a source name to its log messages
    private Dictionary<string, List<string>> logMessages = new Dictionary<string, List<string>>();
    private readonly Dictionary<string, Dictionary<string, string>> pinned =
        new Dictionary<string, Dictionary<string, string>>();

    private static readonly string[] PinOrder = { "controller", "iobt", "head" };

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    // Log a message to a specific source
    public void Log(string source, string message)
    {
        if (!logMessages.ContainsKey(source))
        {
            logMessages[source] = new List<string>();
        }
        List<string> list = logMessages[source];
        list.Add(message);
        // Cap history to avoid unbounded growth from 100 Hz landmark logs.
        if (list.Count > 512)
        {
            list.RemoveRange(0, list.Count - 256);
        }
        Debug.Log($"[{source}] {message}");
    }

    /// <summary>
    /// Keep a live pose block on the HUD (controller / IOBT / head) so 100 Hz
    /// landmark logs cannot scroll it away.
    /// </summary>
    public void Pin(string source, string slot, string message)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(slot))
        {
            return;
        }
        if (!pinned.TryGetValue(source, out Dictionary<string, string> slots))
        {
            slots = new Dictionary<string, string>();
            pinned[source] = slots;
        }
        if (string.IsNullOrEmpty(message))
        {
            slots.Remove(slot);
        }
        else
        {
            slots[slot] = message;
        }
    }

    public List<string> GetPinnedMessages(string source)
    {
        var result = new List<string>();
        if (!pinned.TryGetValue(source, out Dictionary<string, string> slots))
        {
            return result;
        }
        for (int i = 0; i < PinOrder.Length; i++)
        {
            if (slots.TryGetValue(PinOrder[i], out string msg) && !string.IsNullOrEmpty(msg))
            {
                result.Add(msg);
            }
        }
        return result;
    }

    // Get log messages for a specific source
    public List<string> GetLogMessages(string source)
    {
        if (logMessages.ContainsKey(source))
        {
            return logMessages[source];
        }
        return new List<string>();
    }
}
}
