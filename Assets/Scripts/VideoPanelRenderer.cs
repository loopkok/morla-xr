using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MORLA.XR
{

public class VideoPanelRenderer : MonoBehaviour
{
    [SerializeField] private GameObject panelRoot;
    [SerializeField] private RawImage targetRawImage;
    [SerializeField] private Renderer targetRenderer;
    [SerializeField] private TextMeshProUGUI placeholderText;

    [Header("FOV-based panel sizing (fixes magnification)")]
    [Tooltip("Distance from the eye to the panel, in meters. Used to convert camera FOV into a panel world size so the streamed image appears at natural (1:1) scale.")]
    [SerializeField] private float referenceDistance = 1.8f;
    [Tooltip("Extra scale multiplier applied on top of the FOV-derived size. 1 = natural size, <1 = smaller, >1 = larger.")]
    [SerializeField] private float sizeMultiplier = 1f;
    [Tooltip("Fallback horizontal FOV in degrees when the host does not send video_config.")]
    [SerializeField] private float defaultFovH = 69f;

    private RectTransform _canvasRect;
    private float _appliedFovH = -1f;
    private int _appliedWidth;
    private int _appliedHeight;

    public GameObject PanelRoot => panelRoot;
    public RawImage TargetRawImage => targetRawImage;

    private void Awake()
    {
        if (panelRoot != null)
        {
            _canvasRect = panelRoot.GetComponent<RectTransform>();
        }
        // Apply a sensible default until the host sends video_config.
        ApplyFov(defaultFovH, 1280, 720);
    }

    public void SetTexture(Texture texture)
    {
        if (targetRawImage != null) targetRawImage.texture = texture;
        if (targetRenderer != null) targetRenderer.material.mainTexture = texture;
        if (placeholderText != null) placeholderText.text = texture == null ? "No Video" : string.Empty;
    }

    public void SetStatus(string text)
    {
        if (placeholderText != null) placeholderText.text = text;
    }

    public void Clear()
    {
        SetTexture(null);
    }

    public void SetVisible(bool visible)
    {
        GameObject target = panelRoot != null ? panelRoot : gameObject;
        if (target != null)
        {
            target.SetActive(visible);
        }
    }

    /// <summary>Reparent the panel to `parent` and set its local position
    /// (meters, relative to the eye) and viewing distance. Call before/after
    /// ApplyFov; the distance is used to convert FOV into world size.</summary>
    public void SetAnchor(Transform parent, Vector3 localPosition, float distance)
    {
        if (panelRoot != null && parent != null)
        {
            panelRoot.transform.SetParent(parent, false);
        }
        if (panelRoot != null)
        {
            panelRoot.transform.localPosition = localPosition;
        }
        referenceDistance = Mathf.Max(0.1f, distance);
        if (_appliedFovH > 0f)
        {
            ApplyFov(_appliedFovH, _appliedWidth, _appliedHeight);
        }
    }

    /// <summary>
    /// Size the display panel so its angular width matches the source camera's
    /// horizontal FOV, making the streamed image appear at natural scale.
    /// </summary>
    public void ApplyFov(float fovHDeg, int srcWidth, int srcHeight)
    {
        // Lazily resolve the canvas rect (cloned panels add this component via
        // AddComponent, so Awake ran before panelRoot was wired).
        if (_canvasRect == null && panelRoot != null)
        {
            _canvasRect = panelRoot.GetComponent<RectTransform>();
        }
        if (_canvasRect == null) return;
        if (fovHDeg <= 1f) fovHDeg = defaultFovH;
        if (srcWidth <= 0) srcWidth = 1280;
        if (srcHeight <= 0) srcHeight = 720;

        float worldWidth = 2f * referenceDistance * Mathf.Tan(Mathf.Deg2Rad * fovHDeg * 0.5f) * sizeMultiplier;
        // Match the RawImage rect to the source aspect (no stretching), then
        // uniformly scale the canvas so the rect's world width equals worldWidth.
        if (targetRawImage != null)
        {
            targetRawImage.rectTransform.sizeDelta = new Vector2(srcWidth, srcHeight);
        }
        float scale = worldWidth / (float)srcWidth;
        _canvasRect.localScale = new Vector3(scale, scale, scale);

        _appliedFovH = fovHDeg;
        _appliedWidth = srcWidth;
        _appliedHeight = srcHeight;
    }

    public void SetSizeMultiplier(float multiplier)
    {
        sizeMultiplier = Mathf.Max(0.05f, multiplier);
        if (_appliedFovH > 0f)
        {
            ApplyFov(_appliedFovH, _appliedWidth, _appliedHeight);
        }
    }
}
}
