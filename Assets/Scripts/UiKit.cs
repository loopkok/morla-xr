using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MORLA.XR
{
    /// <summary>
    /// Marks a control whose click explicitly starts/pauses the session (工具栏的发送按钮).
    /// Pressing it must NOT go through <see cref="SessionCoordinator.EnterUiInteraction"/>:
    /// the press happens before the click, so a pre-pause would flip the very state the click
    /// handler reads — 点「暂停发送」变成先暂停再立刻恢复，"一直暂停不了"。
    /// </summary>
    public sealed class SessionControl : MonoBehaviour { }

    /// <summary>
    /// Shared uGUI design system: dark vibrancy surfaces, hairline strokes, soft shadows,
    /// segmented controls and switches. All shapes are generated from signed-distance fields,
    /// so nothing depends on imported art.
    /// </summary>
    public static class UiKit
    {
        public static readonly Color Window = new Color(0.098f, 0.098f, 0.106f, 0.88f);
        public static readonly Color Surface = new Color(1f, 1f, 1f, 0.06f);
        public static readonly Color Subtle = new Color(1f, 1f, 1f, 0.10f);
        public static readonly Color Selected = new Color(0.04f, 0.52f, 1f, 0.26f);
        public static readonly Color Ink = new Color(1f, 1f, 1f, 0.96f);
        public static readonly Color Secondary = new Color(1f, 1f, 1f, 0.60f);
        public static readonly Color Blue = new Color(0.04f, 0.52f, 1f, 1f);
        public static readonly Color Danger = new Color(1f, 0.27f, 0.23f, 1f);
        public static readonly Color Success = new Color(0.19f, 0.82f, 0.35f, 1f);
        public static readonly Color Warning = new Color(1f, 0.84f, 0.04f, 1f);
        public static readonly Color Stroke = new Color(1f, 1f, 1f, 0.14f);
        public static readonly Color Hairline = new Color(1f, 1f, 1f, 0.09f);
        public static readonly Color Field = new Color(0f, 0f, 0f, 0.32f);
        public static readonly Color Clear = new Color(1f, 1f, 1f, 0f);
        public const int WindowRadius = 26, CardRadius = 20, ControlRadius = 14;

        private static TMP_FontAsset _font;
        private static Sprite _shadow;
        private static readonly Dictionary<int, Sprite> Fills = new Dictionary<int, Sprite>();
        private static readonly Dictionary<int, Sprite> Rings = new Dictionary<int, Sprite>();
        private static float TextScale => AppManager.Instance != null ? AppManager.Instance.Settings.textScale : 1f;
        private static bool ReduceMotion => AppManager.Instance != null && AppManager.Instance.Settings.reduceMotion;
        public static TMP_FontAsset Font
        {
            get
            {
                if (_font == null) _font = Resources.Load<TMP_FontAsset>("MorlaFont") ?? TMP_Settings.defaultFontAsset;
                return _font;
            }
        }
        private static Sprite Fill(int radius)
        {
            radius = Mathf.Clamp(radius, 1, 64);
            if (Fills.TryGetValue(radius, out Sprite cached) && cached != null) return cached;
            Sprite sprite = Sdf(radius, false); Fills[radius] = sprite; return sprite;
        }
        private static Sprite Ring(int radius)
        {
            radius = Mathf.Clamp(radius, 1, 64);
            if (Rings.TryGetValue(radius, out Sprite cached) && cached != null) return cached;
            Sprite sprite = Sdf(radius, true); Rings[radius] = sprite; return sprite;
        }
        /// <summary>Anti-aliased rounded rectangle: filled, or a 1-unit hairline along its boundary.</summary>
        private static Sprite Sdf(int radius, bool ring)
        {
            int size = radius * 2 + 8;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            { name = "MORLA " + (ring ? "Ring " : "Fill ") + radius, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var pixels = new Color32[size * size];
            float half = size * 0.5f, straight = half - radius - 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Max(Mathf.Abs(x + 0.5f - half) - straight, 0f);
                    float dy = Mathf.Max(Mathf.Abs(y + 0.5f - half) - straight, 0f);
                    float distance = Mathf.Sqrt(dx * dx + dy * dy) - radius;
                    float alpha = ring ? Mathf.Clamp01(1.3f - Mathf.Abs(distance)) : Mathf.Clamp01(0.5f - distance);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
                }
            texture.SetPixels32(pixels); texture.Apply();
            float border = radius + 1;
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100, 0,
                SpriteMeshType.FullRect, new Vector4(border, border, border, border));
        }
        private static Sprite ShadowSprite
        {
            get
            {
                if (_shadow != null) return _shadow;
                const int size = 160, blur = 26, shapeRadius = 20;
                float half = size * 0.5f, straight = half - blur - shapeRadius - 0.5f;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
                { name = "MORLA Shadow", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                var pixels = new Color32[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float dx = Mathf.Max(Mathf.Abs(x + 0.5f - half) - straight, 0f);
                        float dy = Mathf.Max(Mathf.Abs(y + 0.5f - half) - straight, 0f);
                        float outside = Mathf.Sqrt(dx * dx + dy * dy) - shapeRadius;
                        float alpha = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(outside / blur));
                        pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255));
                    }
                texture.SetPixels32(pixels); texture.Apply();
                float border = blur + shapeRadius;
                _shadow = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100, 0,
                    SpriteMeshType.FullRect, new Vector4(border, border, border, border));
                return _shadow;
            }
        }
        public static RectTransform Rect(Transform parent, string name, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform; rect.anchoredPosition = position; rect.sizeDelta = size; return rect;
        }
        /// <summary>Rounded surface with an optional 1-unit hairline stroke.</summary>
        public static RectTransform Panel(Transform parent, string name, Vector2 position, Vector2 size, Color? color = null, int radius = CardRadius, bool stroke = true)
        {
            var rect = Rect(parent, name, position, size); var image = rect.gameObject.AddComponent<Image>();
            image.sprite = Fill(radius); image.type = Image.Type.Sliced; image.color = color ?? Window;
            image.raycastTarget = false;
            if (stroke) AddStroke(rect, radius);
            return rect;
        }
        private static void AddStroke(RectTransform rect, int radius)
        {
            var line = Rect(rect, "Stroke", Vector2.zero, Vector2.zero);
            line.anchorMin = Vector2.zero; line.anchorMax = Vector2.one;
            line.offsetMin = Vector2.zero; line.offsetMax = Vector2.zero;
            var image = line.gameObject.AddComponent<Image>(); image.sprite = Ring(radius); image.type = Image.Type.Sliced;
            image.color = Stroke; image.raycastTarget = false;
        }
        /// <summary>Soft drop shadow drawn as a sibling behind the panel.</summary>
        public static void Shadow(RectTransform panel, float lift = 16f)
        {
            if (panel == null || panel.parent == null) return;
            var rect = Rect(panel.parent, "Shadow", Vector2.zero, Vector2.zero);
            rect.anchorMin = panel.anchorMin; rect.anchorMax = panel.anchorMax; rect.pivot = panel.pivot;
            rect.sizeDelta = panel.sizeDelta + new Vector2(54, 54);
            rect.anchoredPosition = panel.anchoredPosition + new Vector2(0, -lift);
            rect.SetSiblingIndex(panel.GetSiblingIndex());
            var image = rect.gameObject.AddComponent<Image>(); image.sprite = ShadowSprite; image.type = Image.Type.Sliced;
            image.color = new Color(0f, 0f, 0f, 0.5f); image.raycastTarget = false;
        }
        public static RectTransform Dot(Transform parent, string name, Vector2 position, float size, Color color)
        {
            return Panel(parent, name, position, new Vector2(size, size), color, Mathf.RoundToInt(size * 0.5f), false);
        }
        /// <summary>Filled circle (round, not rounded) used for the pointer cursor.</summary>
        public static Image Disc(Transform parent, string name, float size, Color color)
        {
            var rect = Rect(parent, name, Vector2.zero, new Vector2(size, size));
            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = Fill(Mathf.Max(1, Mathf.RoundToInt(size * 0.5f)));
            image.type = Image.Type.Simple; image.color = color; image.raycastTarget = false;
            return image;
        }
        public static void Divider(Transform parent, float x, float y, float length, bool vertical = false)
        {
            var rect = Rect(parent, "Divider", new Vector2(x, y), vertical ? new Vector2(1, length) : new Vector2(length, 1));
            var image = rect.gameObject.AddComponent<Image>(); image.color = Hairline; image.raycastTarget = false;
        }
        public static TMP_Text Text(Transform parent, string name, string value, float fontSize, Vector2 position, Vector2 size,
            Color? color = null, TextAlignmentOptions alignment = TextAlignmentOptions.MidlineLeft, FontStyles style = FontStyles.Normal)
        {
            var rect = Rect(parent, name, position, size); var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = Font; text.fontSize = fontSize * TextScale;
            var textStyle = rect.gameObject.AddComponent<MorlaTextStyle>();
            textStyle.baseSize = fontSize; textStyle.secondary = color.HasValue && color.Value == Secondary;
            text.text = value; text.color = color ?? Ink; text.fontStyle = style;
            text.raycastTarget = false; text.richText = false;
            text.alignment = alignment; text.textWrappingMode = TextWrappingModes.Normal;
            text.overflowMode = TextOverflowModes.Ellipsis; return text;
        }
        public static Button Button(Transform parent, string name, string label, Vector2 position, Vector2 size, Action action, bool primary = false, bool sessionControl = false)
        {
            var rect = Panel(parent, name, position, size, primary ? Blue : Subtle, ControlRadius);
            var image = rect.GetComponent<Image>(); image.raycastTarget = true;
            var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image;
            if (sessionControl) rect.gameObject.AddComponent<SessionControl>();
            var colors = button.colors;
            // Color tint multiplies the graphic colour, so idle is a dimmed version of the hover surface.
            colors.normalColor = new Color(0.82f, 0.82f, 0.82f, 1f);
            colors.highlightedColor = Color.white;
            colors.pressedColor = new Color(0.65f, 0.65f, 0.65f, 1f);
            colors.selectedColor = Color.white;
            colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.4f);
            colors.fadeDuration = ReduceMotion ? 0 : 0.1f; button.colors = colors;
            var text = Text(rect, "Label", label, 27, Vector2.zero, size - new Vector2(24, 4),
                primary ? Color.white : Ink, TextAlignmentOptions.Center);
            button.onClick.AddListener(() => action()); return button;
        }
        /// <summary>Borderless list row (sidebar page): faint on rest, brighter on hover, accent when chosen.</summary>
        public static Button QuietButton(Transform parent, string name, string label, Vector2 position, Vector2 size, Action action, float fontSize = 24)
        {
            var rect = Panel(parent, name, position, size, new Color(1f, 1f, 1f, 0.10f), ControlRadius, false);
            var image = rect.GetComponent<Image>(); image.raycastTarget = true;
            var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image;
            var colors = button.colors;
            colors.normalColor = new Color(0.30f, 0.30f, 0.30f, 1f);
            colors.highlightedColor = Color.white; colors.pressedColor = new Color(0.78f, 0.78f, 0.78f, 1f);
            colors.fadeDuration = ReduceMotion ? 0 : 0.1f; button.colors = colors;
            button.onClick.AddListener(() => action());
            Text(rect, "Label", label, fontSize, new Vector2(8, 0), new Vector2(size.x - 40, size.y - 6), Secondary, TextAlignmentOptions.MidlineLeft);
            return button;
        }
        /// <summary>Marks a selectable row (sidebar page, list item) without rebuilding it.</summary>
        public static void SetSelected(Button button, bool selected)
        {
            if (button == null) return;
            var image = button.GetComponent<Image>();
            if (image != null) image.color = selected ? Selected : new Color(1f, 1f, 1f, 0.10f);
            var colors = button.colors;
            colors.normalColor = selected ? new Color(0.92f, 0.92f, 0.92f, 1f) : new Color(0.30f, 0.30f, 0.30f, 1f);
            button.colors = colors;
            var text = button.GetComponentInChildren<TMP_Text>(); if (text != null) text.color = selected ? Ink : Secondary;
            var style = button.GetComponentInChildren<MorlaTextStyle>(); if (style != null) style.secondary = !selected;
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
            text.fontSize = 24 * TextScale;
            return icon;
        }
        /// <summary>macOS-style switch row: label on the left, toggle on the right, whole row tappable.</summary>
        public static void SwitchRow(Transform parent, string label, bool value, Action<bool> change)
        {
            var row = FlowRect(parent, "SwitchRow", 64);
            var hit = row.gameObject.AddComponent<Image>(); hit.sprite = Fill(CardRadius); hit.type = Image.Type.Sliced;
            hit.color = Clear; hit.raycastTarget = true;
            var button = row.gameObject.AddComponent<Button>(); button.targetGraphic = hit;
            var colors = button.colors; colors.fadeDuration = ReduceMotion ? 0 : 0.1f; colors.normalColor = Color.white; button.colors = colors;
            button.onClick.AddListener(() => change(!value));
            Text(row, "Label", label, 26, new Vector2(-57, 0), new Vector2(520, 60), Ink);
            var track = Panel(row, "Switch", new Vector2(275, 0), new Vector2(74, 40), value ? Blue : new Color(1f, 1f, 1f, 0.18f), 20, false);
            track.GetComponent<Image>().raycastTarget = false;
            var knob = Panel(track, "Knob", new Vector2(value ? 17 : -17, 0), new Vector2(32, 32), Color.white, 16, false);
            knob.GetComponent<Image>().raycastTarget = false;
        }
        /// <summary>Segmented control in a flow row (full width).</summary>
        public static void SegmentedRow(Transform parent, string[] labels, int selected, Action<int> change)
        {
            var row = FlowRect(parent, "Segments", 56);
            Segmented(row, Vector2.zero, new Vector2(650, 56), labels, selected, change);
        }
        public static RectTransform Segmented(Transform parent, Vector2 position, Vector2 size, string[] labels, int selected, Action<int> change)
        {
            var track = Panel(parent, "Segments", position, size, new Color(1f, 1f, 1f, 0.07f), ControlRadius);
            float width = size.x / labels.Length;
            for (int i = 0; i < labels.Length; i++)
            {
                int value = i;
                bool chosen = i == selected;
                var segment = Panel(track, "Segment" + i, new Vector2(-size.x * 0.5f + width * (i + 0.5f), 0),
                    new Vector2(width - 8, size.y - 12), chosen ? new Color(1f, 1f, 1f, 0.24f) : Clear, 10, false);
                var image = segment.GetComponent<Image>(); image.raycastTarget = true;
                var button = segment.gameObject.AddComponent<Button>(); button.targetGraphic = image;
                var colors = button.colors;
                colors.normalColor = new Color(0.82f, 0.82f, 0.82f, 1f); colors.highlightedColor = Color.white;
                colors.pressedColor = new Color(0.65f, 0.65f, 0.65f, 1f); colors.fadeDuration = ReduceMotion ? 0 : 0.1f;
                button.colors = colors; button.onClick.AddListener(() => change(value));
                Text(segment, "Label", labels[i], 24, Vector2.zero, new Vector2(width - 20, size.y - 14),
                    chosen ? Ink : Secondary, TextAlignmentOptions.Center);
            }
            return track;
        }
        public static void ApplyTheme(GameObject root)
        {
            if (root == null || AppManager.Instance == null) return;
            var settings = AppManager.Instance.Settings;
            float secondary = settings.highContrast ? 0.82f : 0.60f;
            float windowAlpha = settings.highContrast ? 0.97f : 0.88f;
            foreach (var style in root.GetComponentsInChildren<MorlaTextStyle>(true))
            {
                style.Apply(settings.textScale);
                if (!style.secondary) continue;
                var label = style.GetComponent<TMP_Text>();
                if (label != null) { var color = Secondary; color.a = secondary; label.color = color; }
            }
            foreach (var image in root.GetComponentsInChildren<Image>(true))
                if (Mathf.Abs(image.color.r - Window.r) < 0.01f && Mathf.Abs(image.color.g - Window.g) < 0.01f &&
                    Mathf.Abs(image.color.b - Window.b) < 0.01f)
                { var color = Window; color.a = windowAlpha; image.color = color; }
            foreach (var button in root.GetComponentsInChildren<Button>(true))
            { var colors = button.colors; colors.fadeDuration = settings.reduceMotion ? 0 : 0.1f; button.colors = colors; }
        }
        public static RectTransform FlowRect(Transform parent, string name, float height)
        {
            var rect = Rect(parent, name, Vector2.zero, new Vector2(650, height));
            var layout = rect.gameObject.AddComponent<LayoutElement>(); layout.minHeight = height; layout.preferredHeight = height; return rect;
        }
        public static TMP_Text FlowText(Transform parent, string value, float fontSize, float height, Color? color = null, FontStyles style = FontStyles.Normal)
        {
            var row = FlowRect(parent, "TextRow", height);
            return Text(row, "Text", value, fontSize, Vector2.zero, new Vector2(650, height), color, TextAlignmentOptions.MidlineLeft, style);
        }
        public static Button FlowButton(Transform parent, string value, Action action, bool primary = false)
        {
            var row = FlowRect(parent, "ButtonRow", 70);
            var button = Button(row, "Button", value, Vector2.zero, new Vector2(650, 62), action, primary);
            if (!primary)
            {
                // Settings-style rows read left-aligned; only the default action stays centred.
                var text = button.GetComponentInChildren<TMP_Text>();
                text.alignment = TextAlignmentOptions.MidlineLeft;
                text.rectTransform.anchoredPosition = new Vector2(-8, 0);
                text.rectTransform.sizeDelta = new Vector2(610, 56);
            }
            return button;
        }
        public static TMP_InputField Input(Transform parent, string value, Action<string> change, bool numeric)
        {
            var row = FlowRect(parent, "InputRow", 74);
            var background = Panel(row, "Input", Vector2.zero, new Vector2(650, 64), Field, 12);
            var image = background.GetComponent<Image>(); image.raycastTarget = true;
            var field = background.gameObject.AddComponent<TMP_InputField>(); field.targetGraphic = image;
            var viewport = Rect(background, "Viewport", Vector2.zero, new Vector2(624, 56));
            viewport.gameObject.AddComponent<RectMask2D>();
            var text = Text(viewport, "Text", "", 27, Vector2.zero, new Vector2(600, 56));
            text.alignment = TextAlignmentOptions.MidlineLeft;
            field.textViewport = viewport; field.textComponent = (TextMeshProUGUI)text;
            field.contentType = numeric ? TMP_InputField.ContentType.IntegerNumber : TMP_InputField.ContentType.Standard;
            field.characterLimit = numeric ? 5 : 253; field.text = value ?? ""; field.onValueChanged.AddListener(textValue => change(textValue));
            field.caretColor = Blue; field.customCaretColor = true; field.caretWidth = 2;
            field.selectionColor = new Color(0.04f, 0.52f, 1f, 0.35f);
            return field;
        }
        public static RectTransform ScrollPage(Transform parent, Vector2 position, Vector2 size)
        {
            var rect = Rect(parent, "PageScroll", position, size); var scroll = rect.gameObject.AddComponent<ScrollRect>();
            var viewport = Panel(rect, "Viewport", Vector2.zero, size, new Color(1f, 1f, 1f, 0.01f), CardRadius, false);
            viewport.GetComponent<Image>().raycastTarget = true; viewport.gameObject.AddComponent<RectMask2D>();
            var content = Rect(viewport, "Content", Vector2.zero, new Vector2(size.x, 0));
            content.anchorMin = new Vector2(0.5f, 1); content.anchorMax = new Vector2(0.5f, 1); content.pivot = new Vector2(0.5f, 1);
            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>(); layout.spacing = 10;
            layout.childControlHeight = true; layout.childControlWidth = true; layout.childForceExpandHeight = false; layout.childForceExpandWidth = true;
            layout.padding = new RectOffset(14, 14, 10, 26);
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>(); fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.viewport = viewport; scroll.content = content; scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped;
            return content;
        }
        public static void SetReadable(TMP_Text text, bool highContrast)
        { text.color = Color.white; text.outlineWidth = highContrast ? 0.25f : 0.16f; text.outlineColor = Color.black; }
    }
}
