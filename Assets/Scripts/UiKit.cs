using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MORLA.XR
{
    /// <summary>Shared uGUI controls. Uses one bundled Chinese TMP font and sliced panel sprite.</summary>
    public static class UiKit
    {
        public static readonly Color Surface = new Color32(244, 245, 247, 248);
        public static readonly Color Subtle = new Color32(232, 234, 238, 255);
        public static readonly Color Selected = new Color32(210, 229, 252, 255);
        public static readonly Color Ink = new Color32(29, 29, 31, 255);
        public static readonly Color Blue = new Color32(10, 102, 217, 255);
        private static TMP_FontAsset _font;
        private static Sprite _round;
        public static TMP_FontAsset Font
        {
            get
            {
                if (_font == null) _font = Resources.Load<TMP_FontAsset>("MorlaFont") ?? TMP_Settings.defaultFontAsset;
                return _font;
            }
        }
        private static Sprite Round
        {
            get
            {
                if (_round != null) return _round;
                _round = Resources.Load<Sprite>("MorlaPanel");
                if (_round != null) return _round;
                var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
                {
                    float dx = Mathf.Max(Mathf.Abs(x - 31.5f) - 15.5f, 0), dy = Mathf.Max(Mathf.Abs(y - 31.5f) - 15.5f, 0);
                    texture.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(16f - Mathf.Sqrt(dx * dx + dy * dy))));
                }
                texture.Apply();
                _round = Sprite.Create(texture, new Rect(0, 0, 64, 64), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, new Vector4(20, 20, 20, 20));
                return _round;
            }
        }
        public static RectTransform Rect(Transform parent, string name, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform; rect.anchoredPosition = position; rect.sizeDelta = size; return rect;
        }
        public static RectTransform Panel(Transform parent, string name, Vector2 position, Vector2 size, Color? color = null)
        {
            var rect = Rect(parent, name, position, size); var image = rect.gameObject.AddComponent<Image>();
            image.sprite = Round; image.type = Image.Type.Sliced; image.color = color ?? Surface;
            image.raycastTarget = false; return rect;
        }
        public static TMP_Text Text(Transform parent, string name, string value, float fontSize, Vector2 position, Vector2 size)
        {
            var rect = Rect(parent, name, position, size); var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = Font; text.fontSize = fontSize * (AppManager.Instance != null ? AppManager.Instance.Settings.textScale : 1);
            rect.gameObject.AddComponent<MorlaTextStyle>().baseSize = fontSize;
            text.text = value; text.color = Ink; text.raycastTarget = false; text.richText = false;
            text.alignment = TextAlignmentOptions.MidlineLeft; text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Ellipsis; return text;
        }
        public static Button Button(Transform parent, string name, string label, Vector2 position, Vector2 size, Action action, bool primary = false)
        {
            var rect = Panel(parent, name, position, size, primary ? Blue : Surface);
            var image = rect.GetComponent<Image>(); image.raycastTarget = true;
            var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image;
            var colors = button.colors; colors.highlightedColor = new Color(0.88f, 0.93f, 1); colors.pressedColor = new Color(0.72f, 0.82f, 0.95f);
            colors.fadeDuration = AppManager.Instance != null && AppManager.Instance.Settings.reduceMotion ? 0 : 0.12f; button.colors = colors;
            var text = Text(rect, "Label", label, 27, Vector2.zero, size - new Vector2(20, 4));
            text.alignment = TextAlignmentOptions.Center; if (primary) text.color = Color.white;
            button.onClick.AddListener(() => action()); return button;
        }
        public static WorkspaceIcon AddToolbarIcon(Button button, WorkspaceIcon.Kind kind)
        {
            var rect = Rect(button.transform, "Icon", new Vector2(-button.GetComponent<RectTransform>().sizeDelta.x * 0.5f + 32, 0), new Vector2(36, 40));
            var icon = rect.gameObject.AddComponent<WorkspaceIcon>(); icon.kind = kind; icon.raycastTarget = false;
            icon.color = button.name == "Send" ? Color.white : Ink;
            var text = button.GetComponentInChildren<TMP_Text>();
            text.rectTransform.anchoredPosition = new Vector2(22, 0);
            text.rectTransform.sizeDelta -= new Vector2(42, 0);
            text.GetComponent<MorlaTextStyle>().baseSize = 24;
            text.fontSize = 24 * (AppManager.Instance != null ? AppManager.Instance.Settings.textScale : 1);
            return icon;
        }
        public static void ApplyTheme(GameObject root)
        {
            if (root == null || AppManager.Instance == null) return;
            var settings = AppManager.Instance.Settings;
            foreach (var text in root.GetComponentsInChildren<MorlaTextStyle>(true)) text.Apply(settings.textScale);
            foreach (var button in root.GetComponentsInChildren<Button>(true))
            { var colors = button.colors; colors.fadeDuration = settings.reduceMotion ? 0 : 0.12f; button.colors = colors; }
            foreach (var image in root.GetComponentsInChildren<Image>(true))
            { var color = image.color; if (color.a > 0.8f) { color.a = settings.highContrast ? 1 : 0.97f; image.color = color; } }
        }
        public static RectTransform FlowRect(Transform parent, string name, float height)
        {
            var rect = Rect(parent, name, Vector2.zero, new Vector2(650, height));
            var layout = rect.gameObject.AddComponent<LayoutElement>(); layout.minHeight = height; layout.preferredHeight = height; return rect;
        }
        public static TMP_Text FlowText(Transform parent, string value, float fontSize, float height)
        {
            var row = FlowRect(parent, "TextRow", height); return Text(row, "Text", value, fontSize, Vector2.zero, new Vector2(650, height));
        }
        public static Button FlowButton(Transform parent, string value, Action action, bool primary = false)
        { var row = FlowRect(parent, "ButtonRow", 72); return Button(row, "Button", value, Vector2.zero, new Vector2(650, 66), action, primary); }
        public static TMP_InputField Input(Transform parent, string value, Action<string> change, bool numeric)
        {
            var row = FlowRect(parent, "InputRow", 70);
            var background = Panel(row, "Input", Vector2.zero, new Vector2(650, 66), Color.white);
            background.GetComponent<Image>().raycastTarget = true;
            var field = background.gameObject.AddComponent<TMP_InputField>(); field.targetGraphic = background.GetComponent<Image>();
            var viewport = Rect(background, "Viewport", Vector2.zero, new Vector2(610, 60)); viewport.gameObject.AddComponent<RectMask2D>();
            var text = Text(viewport, "Text", "", 28, Vector2.zero, new Vector2(610, 60));
            field.textViewport = viewport; field.textComponent = (TextMeshProUGUI)text;
            field.contentType = numeric ? TMP_InputField.ContentType.IntegerNumber : TMP_InputField.ContentType.Standard;
            field.characterLimit = numeric ? 5 : 253; field.text = value ?? ""; field.onValueChanged.AddListener(textValue => change(textValue));
            field.caretColor = Blue; field.customCaretColor = true; return field;
        }
        public static RectTransform ScrollPage(Transform parent, Vector2 position, Vector2 size)
        {
            var rect = Rect(parent, "PageScroll", position, size); var scroll = rect.gameObject.AddComponent<ScrollRect>();
            var viewport = Panel(rect, "Viewport", Vector2.zero, size, new Color(1, 1, 1, 0.01f));
            viewport.GetComponent<Image>().raycastTarget = true; viewport.gameObject.AddComponent<RectMask2D>();
            var content = Rect(viewport, "Content", Vector2.zero, new Vector2(size.x, 0));
            content.anchorMin = new Vector2(0.5f, 1); content.anchorMax = new Vector2(0.5f, 1); content.pivot = new Vector2(0.5f, 1);
            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>(); layout.spacing = 12;
            layout.childControlHeight = true; layout.childControlWidth = true; layout.childForceExpandHeight = false; layout.childForceExpandWidth = true;
            layout.padding = new RectOffset(12, 12, 8, 24);
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>(); fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = viewport; scroll.content = content; scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped;
            return content;
        }
        public static void SetReadable(TMP_Text text, bool highContrast)
        { text.color = Color.white; text.outlineWidth = highContrast ? 0.25f : 0.16f; text.outlineColor = Color.black; }
    }
}
