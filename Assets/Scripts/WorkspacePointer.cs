using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MORLA.XR
{
    /// <summary>Early UI intent gate and uGUI pointer events, shared by tracked hands and Touch controllers.</summary>
    [DefaultExecutionOrder(-9000)]
    public sealed class WorkspacePointer : MonoBehaviour
    {
        private sealed class Pointer
        {
            public PointerEventData data;
            public GameObject hover;
            public bool down;
            public Vector2 previous;
            public LineRenderer line;
        }
        private readonly List<RaycastResult> _hits = new List<RaycastResult>();
        private readonly Pointer[] _controllers = { new Pointer(), new Pointer() };
        private Pointer[] _handPointers;
        private static OVRHand[] _hands;
        private static bool _released = true;
        private AppManager App => AppManager.Instance;
        public static bool InputsReleased() => _released;
        private void Start()
        {
            _hands = FindObjectsByType<OVRHand>(FindObjectsSortMode.None);
            _handPointers = new Pointer[_hands.Length];
            for (int i = 0; i < _controllers.Length; i++) Initialize(_controllers[i], -100 - i);
            for (int i = 0; i < _hands.Length; i++) { _handPointers[i] = new Pointer(); Initialize(_handPointers[i], -200 - i); }
        }
        private void Initialize(Pointer pointer, int id)
        {
            pointer.data = new PointerEventData(EventSystem.current) { pointerId = id, button = PointerEventData.InputButton.Left };
            var go = new GameObject("UI Ray " + id); go.transform.SetParent(transform, false);
            pointer.line = go.AddComponent<LineRenderer>(); pointer.line.positionCount = 2;
            pointer.line.startWidth = 0.002f; pointer.line.endWidth = 0.001f;
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
            if (shader != null) { pointer.line.material = new Material(shader); pointer.line.material.color = new Color(0.1f, 0.5f, 1f, 0.7f); }
            pointer.line.enabled = false;
        }
        private void Update()
        {
            if (App == null || App.Workspace == null || App.Workspace.Canvas == null || EventSystem.current == null) return;
            _released = true; bool over = false;
            for (int i = 0; i < 2; i++)
            {
                var controller = i == 0 ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
                bool down = OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, controller) > 0.2f;
                if (down || OVRInput.Get(OVRInput.Button.Any, controller) ||
                    OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, controller) > 0.2f) _released = false;
                bool valid = App.Tracking != null && App.Tracking.ControllerActive(i == 0) && App.TrackingEnabled;
                if (valid && Camera.main != null)
                {
                    var local = new OVRPose { position = OVRInput.GetLocalControllerPosition(controller), orientation = OVRInput.GetLocalControllerRotation(controller) };
                    var world = local.ToWorldSpacePose(Camera.main);
                    over |= Process(_controllers[i], new Ray(world.position, world.orientation * Vector3.forward), down,
                        OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, controller).y);
                }
                else Clear(_controllers[i]);
            }
            for (int i = 0; i < (_hands?.Length ?? 0); i++)
            {
                var hand = _hands[i]; bool down = hand != null && hand.IsTracked && hand.GetFingerIsPinching(OVRHand.HandFinger.Index);
                if (down) _released = false;
                if (hand != null && hand.IsTracked && hand.IsPointerPoseValid && hand.PointerPose != null && App.TrackingEnabled)
                {
                    var pointer = hand.PointerPose;
                    // SDK 72 leaves _pointerPoseRoot null in this scene, so its unparented pose is tracking-local.
                    var pose = new OVRPose { position = pointer.position, orientation = pointer.rotation };
                    if (pointer.parent == null && Camera.main != null) pose = pose.ToWorldSpacePose(Camera.main);
                    over |= Process(_handPointers[i], new Ray(pose.position, pose.orientation * Vector3.forward), down, 0);
                }
                else Clear(_handPointers[i]);
            }
            App.Workspace.PointerOverUi = over;
        }
        private bool Process(Pointer pointer, Ray ray, bool down, float scroll)
        {
            var canvas = App.Workspace.Canvas; var camera = canvas.worldCamera;
            if (camera == null) { Clear(pointer); return false; }
            var plane = new Plane(canvas.transform.forward, canvas.transform.position);
            if (!plane.Raycast(ray, out float distance) || distance <= 0 || distance > 6) { ReleaseOutside(pointer, down); return false; }
            Vector3 worldPoint = ray.GetPoint(distance);
            var data = pointer.data;
            data.position = camera.WorldToScreenPoint(worldPoint); data.delta = data.position - pointer.previous; pointer.previous = data.position;
            _hits.Clear(); canvas.GetComponent<GraphicRaycaster>().Raycast(data, _hits);
            var hit = _hits.Count > 0 ? _hits[0].gameObject : null;
            data.pointerCurrentRaycast = _hits.Count > 0 ? _hits[0] : default;
            if (hit != null && (hit != pointer.hover || down && !pointer.down)) App.Session.EnterUiInteraction();
            if (hit != pointer.hover)
            {
                if (pointer.hover != null) ExecuteEvents.ExecuteHierarchy(pointer.hover, data, ExecuteEvents.pointerExitHandler);
                pointer.hover = hit;
                if (hit != null) ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.pointerEnterHandler);
            }
            pointer.line.enabled = hit != null || down;
            pointer.line.SetPosition(0, ray.origin); pointer.line.SetPosition(1, worldPoint);
            if (down && !pointer.down && hit != null)
            {
                data.pressPosition = data.position; data.pointerPressRaycast = data.pointerCurrentRaycast;
                data.eligibleForClick = true; data.dragging = false; data.clickCount = 1; data.clickTime = Time.unscaledTime;
                data.pointerPress = ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.pointerDownHandler)
                    ?? ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit);
                data.rawPointerPress = hit; data.pointerDrag = ExecuteEvents.GetEventHandler<IDragHandler>(hit);
                if (data.pointerDrag != null) ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.initializePotentialDrag);
            }
            if (down && pointer.down && data.pointerDrag != null)
            {
                if (!data.dragging && (data.position - data.pressPosition).sqrMagnitude > 64)
                { data.dragging = true; data.eligibleForClick = false; ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.beginDragHandler); }
                if (data.dragging) ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.dragHandler);
            }
            if (!down && pointer.down) Release(pointer, true);
            if (hit != null && Mathf.Abs(scroll) > 0.15f)
            { data.scrollDelta = new Vector2(0, scroll * Time.unscaledDeltaTime * 100); ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.scrollHandler); }
            pointer.down = down; return hit != null;
        }
        private void ReleaseOutside(Pointer pointer, bool down)
        {
            if (!down && pointer.down) Release(pointer, false);
            if (pointer.hover != null) ExecuteEvents.ExecuteHierarchy(pointer.hover, pointer.data, ExecuteEvents.pointerExitHandler);
            pointer.hover = null; pointer.down = down; pointer.line.enabled = false;
        }
        private static void Release(Pointer pointer, bool click)
        {
            var data = pointer.data;
            if (data.pointerPress != null)
            {
                ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
                if (click && data.eligibleForClick && !data.dragging)
                    ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerClickHandler);
            }
            if (data.dragging && data.pointerDrag != null) ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.endDragHandler);
            data.pointerPress = null; data.rawPointerPress = null; data.pointerDrag = null; data.dragging = false; data.eligibleForClick = false;
        }
        private void Clear(Pointer pointer)
        {
            if (pointer.data == null) return;
            Release(pointer, false);
            if (pointer.hover != null) ExecuteEvents.ExecuteHierarchy(pointer.hover, pointer.data, ExecuteEvents.pointerExitHandler);
            pointer.hover = null; pointer.down = false; if (pointer.line != null) pointer.line.enabled = false;
        }
        private void OnDisable()
        {
            foreach (var pointer in _controllers) Clear(pointer);
            if (_handPointers != null) foreach (var pointer in _handPointers) Clear(pointer);
            _released = false;
        }
        private void OnDestroy()
        {
            foreach (var pointer in _controllers) if (pointer.line != null) Destroy(pointer.line.material);
            if (_handPointers != null) foreach (var pointer in _handPointers) if (pointer.line != null) Destroy(pointer.line.material);
        }
    }
}
