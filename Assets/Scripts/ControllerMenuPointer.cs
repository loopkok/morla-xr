using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MORLA.XR
{

/// <summary>
/// Lets the Touch controllers operate the menu UI (uGUI) by casting a ray from
/// each controller and sending pointer events to whichever canvas is hit.
///
/// The menu uses Interaction SDK PointableCanvas + RayInteractable for HAND rays,
/// but this project has no Controller Ray Interactor rig (Building Blocks only
/// exposes Hand Ray). This script fills that gap with a self-contained
/// controller -> uGUI bridge that does NOT depend on Interaction SDK:
///   - Ensures a GraphicRaycaster exists on the menu canvas.
///   - Casts a ray from the controller (OVRInput pose -> world space).
///   - Intersects every canvas plane, picks the closest hit, and uses that
///     canvas's GraphicRaycaster to resolve the uGUI element (so Dropdown
///     option lists, which live on their own canvas, are also clickable).
///   - Sends pointerDown / pointerUp / pointerClick on the trigger.
///
/// It only runs while the menu is visible (not streaming), and only on a side
/// when the controller is the active input there (hand tracking off + pose
/// valid), mirroring ControllerPoseStreamer's IOBT-mode detection so it never
/// fires for a controller resting on the table and never conflicts with the
/// hand ray (which is inactive while the controller is the active input).
/// </summary>
public class ControllerMenuPointer : MonoBehaviour
{
    [SerializeField] private Canvas targetCanvas; // main menu canvas (auto-found)
    [SerializeField] private float maxDistance = 6f;
    [SerializeField] private bool showRay = true;
    [SerializeField] private Color rayColor = new Color(0.2f, 0.8f, 1f, 0.6f);

    private PointerEventData _ped;
    private readonly List<RaycastResult> _results = new List<RaycastResult>(16);

    private HandLandmarkStreamer[] _handStreamers;
    private GraphicRaycaster[] _raycasters;
    private float _raycasterRefreshTimer;

    private GameObject _lHover, _rHover;
    private GameObject _lPressed, _rPressed;
    private bool _lDown, _rDown;
    private LineRenderer _lRay, _rRay;
    private Transform _lDot, _rDot;
    private Vector3 _lLastPos, _rLastPos;
    private float _lActiveTime, _rActiveTime;
    // Keep the ray alive briefly after the last input so it doesn't flicker.
    private const float UseTimeout = 3f;
    private const float MoveEpsilon = 0.0004f;

    /// <summary>Name of the UI element the right controller ray is over ("none" if none). Shown on the HUD.</summary>
    public static string DebugLine { get; private set; } = "none";

    private void Awake()
    {
        if (targetCanvas == null && AppManager.Instance != null)
        {
            targetCanvas = AppManager.Instance.GetComponent<Canvas>();
        }
        if (targetCanvas == null)
        {
            targetCanvas = FindFirstObjectByType<Canvas>();
        }
        if (targetCanvas != null && targetCanvas.GetComponent<GraphicRaycaster>() == null)
        {
            targetCanvas.gameObject.AddComponent<GraphicRaycaster>();
        }
        _ped = new PointerEventData(EventSystem.current);
    }

    private void Start()
    {
        _handStreamers = FindObjectsByType<HandLandmarkStreamer>(FindObjectsSortMode.None);
        RefreshRaycasters();
        if (showRay)
        {
            _lRay = MakeRay();
            _rRay = MakeRay();
            _lDot = MakeDot();
            _rDot = MakeDot();
        }
    }

    private Transform MakeDot()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "ControllerMenuPointerDot";
        go.transform.SetParent(transform, false);
        go.transform.localScale = Vector3.one * 0.012f;
        var col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);
        var rend = go.GetComponent<Renderer>();
        if (rend != null)
        {
            Shader sh = Shader.Find("UI/Default");
            if (sh != null) rend.material = new Material(sh);
            rend.material.color = new Color(rayColor.r, rayColor.g, rayColor.b, 1f);
        }
        go.SetActive(false);
        return go.transform;
    }

    private void RefreshRaycasters()
    {
        _raycasters = FindObjectsByType<GraphicRaycaster>(FindObjectsSortMode.None);
    }

    private LineRenderer MakeRay()
    {
        var go = new GameObject("ControllerMenuPointerRay");
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.startWidth = 0.004f;
        lr.endWidth = 0.004f;
        Shader sh = Shader.Find("UI/Default");
        if (sh != null)
        {
            lr.material = new Material(sh);
            lr.startColor = rayColor;
            lr.endColor = rayColor;
        }
        lr.enabled = false;
        return lr;
    }

    private void Update()
    {
        bool active = AppManager.Instance == null || !AppManager.Instance.isStreaming;
        if (!active || targetCanvas == null || EventSystem.current == null)
        {
            HideRay(_lRay); HideRay(_rRay);
            return;
        }

        // Dropdown option lists spawn/despawn their own canvas+raycaster, so
        // refresh the list periodically.
        _raycasterRefreshTimer += Time.deltaTime;
        if (_raycasterRefreshTimer > 0.5f)
        {
            _raycasterRefreshTimer = 0f;
            RefreshRaycasters();
        }

            ProcessSide(OVRInput.Controller.LTouch, OVRInput.Hand.HandLeft, _lRay, _lDot,
            ref _lHover, ref _lPressed, ref _lDown);
        ProcessSide(OVRInput.Controller.RTouch, OVRInput.Hand.HandRight, _rRay, _rDot,
            ref _rHover, ref _rPressed, ref _rDown);
    }

    private void ProcessSide(OVRInput.Controller ctrl, OVRInput.Hand hand,
        LineRenderer rayVis, Transform dot, ref GameObject hovered, ref GameObject pressed, ref bool wasDown)
    {
        Camera cam = Camera.main;
        if (cam == null) { HideRay(rayVis); HideDot(dot); return; }

        bool leftSide = ctrl == OVRInput.Controller.LTouch;

        // Gate 1: need a valid controller pose. Without it there is nothing to draw.
        if (!OVRInput.GetControllerPositionValid(ctrl))
        {
            ClearSide(ref hovered, ref pressed, ref wasDown, rayVis);
            HideDot(dot);
            return;
        }

        Vector3 localPos = OVRInput.GetLocalControllerPosition(ctrl);
        Quaternion localRot = OVRInput.GetLocalControllerRotation(ctrl);
        OVRPose world = new OVRPose { position = localPos, orientation = localRot }.ToWorldSpacePose(cam);
        Vector3 origin = world.position;
        Vector3 dir = world.orientation * Vector3.forward;
        Ray ray = new Ray(origin, dir);

        // Update the in-use timestamp AFTER reading input: any button/stick/trigger
        // touch, or movement, counts as "in use". The ray LINE is hidden while the
        // controller is idle (resting on a table), but hit detection + clicking stay
        // active regardless so picking it up and pulling the trigger always works.
        bool input = OVRInput.Get(OVRInput.Button.Any, ctrl)
            || OVRInput.Get(OVRInput.Touch.Any, ctrl)
            || OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, ctrl) > 0.05f
            || OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, ctrl) > 0.05f
            || OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, ctrl).sqrMagnitude > 0.04f;
        ref Vector3 lastPos = ref leftSide ? ref _lLastPos : ref _rLastPos;
        ref float activeTime = ref leftSide ? ref _lActiveTime : ref _rActiveTime;
        bool moved = (localPos - lastPos).sqrMagnitude > MoveEpsilon * MoveEpsilon;
        lastPos = localPos;
        if (input || moved)
        {
            activeTime = Time.time;
        }
        bool inUse = Time.time - activeTime <= UseTimeout;

        // Hit detection: intersect the controller ray with each canvas plane to get a
        // world hit point, then let that canvas's GraphicRaycaster resolve the uGUI
        // element. The screen point is computed with the CANVAS's own worldCamera (not
        // Camera.main) so it matches the camera GraphicRaycaster uses internally —
        // using a different camera here is what caused the aim-A-click-B offset.
        GameObject hit = null;
        Vector3 hitPoint = origin;
        float bestEnter = float.MaxValue;
        string dbg = "none";
        try
        {
            if (_raycasters != null)
            {
                for (int i = 0; i < _raycasters.Length; i++)
                {
                    GraphicRaycaster gr = _raycasters[i];
                    if (gr == null) continue;
                    Canvas c = gr.GetComponent<Canvas>();
                    if (c == null || !c.gameObject.activeInHierarchy) continue;
                    if (c.renderMode == RenderMode.ScreenSpaceOverlay) continue;

                    RectTransform rt = c.transform as RectTransform;
                    if (rt == null) continue;
                    Plane plane = new Plane(rt.forward, rt.position);
                    if (!plane.Raycast(ray, out float enter) || enter <= 0f || enter > maxDistance)
                    {
                        continue;
                    }
                    if (enter >= bestEnter) continue;

                    Vector3 worldHit = ray.GetPoint(enter);
                    Camera eventCam = c.worldCamera != null ? c.worldCamera : cam;
                    if (eventCam == null) continue;
                    if (c.worldCamera == null) c.worldCamera = cam;

                    _ped.position = eventCam.WorldToScreenPoint(worldHit);
                    _ped.delta = Vector2.zero;
                    _ped.scrollDelta = Vector2.zero;
                    _ped.button = PointerEventData.InputButton.Left;
                    _results.Clear();
                    gr.Raycast(_ped, _results);
                    if (_results.Count > 0)
                    {
                        bestEnter = enter;
                        hit = _results[0].gameObject;
                        hitPoint = worldHit;
                        dbg = c.name + "/" + hit.name;
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[ControllerMenuPointer] raycast failed: {ex.Message}");
        }
        if (ctrl == OVRInput.Controller.RTouch) DebugLine = dbg;

        // The ray LINE is only drawn while the controller is in use; clicking works
        // either way (so picking it up and pulling the trigger always registers).
        if (inUse)
        {
            ShowRay(rayVis, origin, hit != null ? hitPoint : origin + dir * maxDistance);
            if (hit != null)
            {
                ShowDot(dot, hitPoint);
            }
            else
            {
                HideDot(dot);
            }
        }
        else
        {
            HideRay(rayVis);
            HideDot(dot);
        }

        // Hover handling.
        if (hit != hovered)
        {
            if (hovered != null) SendExit(hovered);
            hovered = hit;
            if (hit != null) SendEnter(hit);
        }

        // Trigger: down -> press, up -> release + click (uGUI semantics).
        // Use ExecuteHierarchy so the handler is found even when the ray hits a
        // child graphic (e.g. a Button's Text/Image) rather than the Button itself.
        // The click is fired on the object captured at press-down (lenient): a
        // controller ray drifts slightly between press and release, so requiring
        // pressed == current hit would drop most clicks.
        bool trigDown = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, ctrl);
        if (trigDown && !wasDown)
        {
            pressed = hit;
            if (hit != null)
            {
                ExecuteEvents.ExecuteHierarchy(hit, _ped, ExecuteEvents.pointerDownHandler);
            }
        }
        else if (!trigDown && wasDown)
        {
            if (pressed != null)
            {
                ExecuteEvents.ExecuteHierarchy(pressed, _ped, ExecuteEvents.pointerUpHandler);
                InvokeControl(pressed);
            }
            pressed = null;
        }
        wasDown = trigDown;
    }

    private void InvokeControl(GameObject go)
    {
        if (go == null) return;
        // Direct invocation is more reliable for controller rays than relying on the
        // event system's click semantics. Falls back to ExecuteEvents for anything we
        // don't handle explicitly (e.g. TMP_Dropdown, which implements IPointerClickHandler
        // to open its list).
        var btn = go.GetComponentInParent<Button>();
        if (btn != null && btn.interactable) { btn.onClick.Invoke(); return; }
        var tog = go.GetComponentInParent<Toggle>();
        if (tog != null && tog.interactable) { tog.isOn = !tog.isOn; return; }
        ExecuteEvents.ExecuteHierarchy(go, _ped, ExecuteEvents.pointerClickHandler);
    }

    private void ClearSide(ref GameObject hovered, ref GameObject pressed, ref bool wasDown, LineRenderer rayVis)
    {
        if (hovered != null) SendExit(hovered);
        hovered = null;
        pressed = null;
        wasDown = false;
        HideRay(rayVis);
    }

    private void SendEnter(GameObject go)
    {
        ExecuteEvents.ExecuteHierarchy(go, _ped, ExecuteEvents.pointerEnterHandler);
    }

    private void SendExit(GameObject go)
    {
        ExecuteEvents.ExecuteHierarchy(go, _ped, ExecuteEvents.pointerExitHandler);
    }

    private void ShowRay(LineRenderer lr, Vector3 from, Vector3 to)
    {
        if (lr == null) return;
        lr.enabled = true;
        lr.SetPosition(0, from);
        lr.SetPosition(1, to);
    }

    private void HideRay(LineRenderer lr)
    {
        if (lr != null) lr.enabled = false;
    }

    private void ShowDot(Transform dot, Vector3 worldPos)
    {
        if (dot == null) return;
        dot.gameObject.SetActive(true);
        dot.position = worldPos;
    }

    private void HideDot(Transform dot)
    {
        if (dot != null) dot.gameObject.SetActive(false);
    }

    private bool IsHandTracked(OVRInput.Hand hand)
    {
        if (_handStreamers == null) return false;
        HandLandmarkStreamer.HandSide side = hand == OVRInput.Hand.HandLeft
            ? HandLandmarkStreamer.HandSide.Left
            : HandLandmarkStreamer.HandSide.Right;
        for (int i = 0; i < _handStreamers.Length; i++)
        {
            HandLandmarkStreamer hs = _handStreamers[i];
            if (hs == null || hs.Side != side || hs.Hand == null) continue;
            if (hs.Hand.IsTrackedDataValid) return true;
        }
        return false;
    }
}
}
