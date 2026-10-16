using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MORLA.XR
{

/// <summary>
/// Always-on overlay for mix / IOBT / controller / passthrough.
/// Independent of Debug Info and not overwritten by 100 Hz hand landmark logs.
/// </summary>
public class StatusHud : MonoBehaviour
{
    private TextMeshProUGUI _text;
    private readonly System.Text.StringBuilder _sb = new System.Text.StringBuilder(512);

    public static StatusHud Ensure()
    {
        StatusHud existing = FindFirstObjectByType<StatusHud>();
        if (existing != null)
        {
            return existing;
        }
        GameObject go = new GameObject("StatusHud");
        return go.AddComponent<StatusHud>();
    }

    private void Awake()
    {
        Camera cam = Camera.main;
        Canvas canvas = gameObject.GetComponent<Canvas>();
        if (canvas == null)
        {
            canvas = gameObject.AddComponent<Canvas>();
        }
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = cam;
        canvas.planeDistance = 0.85f;
        canvas.sortingOrder = 250;

        CanvasScaler scaler = gameObject.GetComponent<CanvasScaler>();
        if (scaler == null)
        {
            scaler = gameObject.AddComponent<CanvasScaler>();
        }
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        Transform existing = transform.Find("StatusText");
        GameObject textGo = existing != null ? existing.gameObject : new GameObject("StatusText");
        textGo.transform.SetParent(transform, false);
        RectTransform rt = textGo.GetComponent<RectTransform>();
        if (rt == null)
        {
            rt = textGo.AddComponent<RectTransform>();
        }
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0.55f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(36f, -36f);
        rt.sizeDelta = new Vector2(0f, 420f);

        _text = textGo.GetComponent<TextMeshProUGUI>();
        if (_text == null)
        {
            _text = textGo.AddComponent<TextMeshProUGUI>();
        }
        // Find a font from an existing TMP text that isn't ours.
        foreach (TextMeshProUGUI candidate in FindObjectsByType<TextMeshProUGUI>(FindObjectsSortMode.None))
        {
            if (candidate == _text) continue;
            if (candidate.font != null)
            {
                _text.font = candidate.font;
                break;
            }
        }
        _text.fontSize = 22;
        _text.alignment = TextAlignmentOptions.TopLeft;
        _text.color = Color.white;
        _text.textWrappingMode = TextWrappingModes.Normal;
        _text.raycastTarget = false;
        _text.outlineWidth = 0.2f;
        _text.outlineColor = new Color(0f, 0f, 0f, 0.85f);
    }

    private void LateUpdate()
    {
        Canvas canvas = GetComponent<Canvas>();
        if (canvas != null && canvas.worldCamera == null)
        {
            canvas.worldCamera = Camera.main;
        }
        if (_text == null)
        {
            return;
        }

        AppManager app = AppManager.Instance;
        _sb.Clear();
        _sb.Append("PT=").Append(PassthroughOn() ? "on" : "OFF");
        _sb.Append("  SHAC=").Append(ShacOn() ? "on" : "off");
        if (app != null)
        {
            _sb.Append("  Mixed=").Append(app.MixedInputEnabled ? "on" : "off");
            _sb.Append("  stream=").Append(app.isStreaming ? "on" : "menu");
        }
        _sb.Append('\n');
        // ControllerPoseStreamer.HudLine is multi-line: "Mix L=.. R=.." then the
        // detailed === Controller === / Pos / Rot block. Put the button line right
        // under the Mix line, before the detailed pose block.
        string ctrlHud = ControllerPoseStreamer.HudLine;
        int nl = ctrlHud.IndexOf('\n');
        string mixLine = nl >= 0 ? ctrlHud.Substring(0, nl) : ctrlHud;
        string ctrlRest = nl >= 0 ? ctrlHud.Substring(nl + 1) : "";
        _sb.Append(mixLine).Append('\n');
        _sb.Append(ControllerPoseStreamer.ButtonHudLine).Append('\n');
        _sb.Append("ray: ").Append(ControllerMenuPointer.DebugLine).Append('\n');
        if (!string.IsNullOrEmpty(ctrlRest))
        {
            _sb.Append(ctrlRest).Append('\n');
        }
        _sb.Append(BodyPoseStreamer.HudLine);
        _text.text = _sb.ToString();
    }

    private static bool PassthroughOn()
    {
        return OVRManager.instance != null && OVRManager.instance.isInsightPassthroughEnabled;
    }

    private static bool ShacOn()
    {
        return OVRManager.instance != null && OVRManager.instance.SimultaneousHandsAndControllersEnabled;
    }
}
}
