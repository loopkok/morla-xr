using System.Collections.Generic;
using Oculus.Interaction.Input;
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
            public RectTransform cursor;
            public Image core;
        }
        private const int HandCount = 2;
        private readonly List<RaycastResult> _hits = new List<RaycastResult>();
        private readonly Pointer[] _controllers = { new Pointer(), new Pointer() };
        private readonly Pointer[] _handPointers = { new Pointer(), new Pointer() };
        private static HandLandmarkStreamer[] _landmarks;
        private static bool _released = true;
        // A drag carried through uGUI only delivers the intersection with the canvas plane, which
        // loses how far the hand really is. Handlers that move things in depth (WorkspaceWindowDrag)
        // read the live pointer ray instead, keyed by pointer id so two hands cannot mix rays.
        private static readonly Dictionary<int, Ray> _dragRays = new Dictionary<int, Ray>();
        private AppManager App => AppManager.Instance;
        public static bool InputsReleased() => _released;
        public static bool TryGetDragRay(int pointerId, out Ray ray) => _dragRays.TryGetValue(pointerId, out ray);
        private void Start()
        {
            _landmarks = FindObjectsByType<HandLandmarkStreamer>(FindObjectsSortMode.None);
            for (int i = 0; i < _controllers.Length; i++) Initialize(_controllers[i], -100 - i);
            for (int i = 0; i < HandCount; i++) Initialize(_handPointers[i], -200 - i);
        }
        private void Initialize(Pointer pointer, int id)
        {
            pointer.data = new PointerEventData(EventSystem.current) { pointerId = id, button = PointerEventData.InputButton.Left };
            var go = new GameObject("UI Ray " + id); go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>(); line.positionCount = 2;
            line.useWorldSpace = true; line.alignment = LineAlignment.View; line.numCapVertices = 4;
            line.startWidth = 0.0035f; line.endWidth = 0.0015f;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; line.receiveShadows = false;
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
            if (shader != null) { line.material = new Material(shader); line.material.color = new Color(0.33f, 0.72f, 1f, 0.95f); }
            line.enabled = false;
            pointer.line = line;
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
            for (int i = 0; i < HandCount; i++)
                over |= ProcessHand(i == 0 ? HandLandmarkStreamer.HandSide.Left : HandLandmarkStreamer.HandSide.Right, _handPointers[i]);
            App.Workspace.PointerOverUi = over;
        }
        /// <summary>Hand ray from the Interaction SDK hand: world-space pointer pose, pivoted at the pinch point.</summary>
        private bool ProcessHand(HandLandmarkStreamer.HandSide side, Pointer pointer)
        {
            var streamer = Landmark(side);
            IHand hand = streamer != null ? streamer.Hand : null;
            if (hand == null) { Clear(pointer); return false; }
            bool down = hand.GetIndexFingerIsPinching();
            if (down) _released = false;
            Pose pose = default;
            // The Interaction SDK returns world-space poses (tracking space already converted),
            // unlike OVRHand.PointerPose, whose root is unparented in this scene.
            bool valid = App.TrackingEnabled && hand.IsTrackedDataValid && hand.IsPointerPoseValid && hand.GetPointerPose(out pose);
            if (!valid) { Clear(pointer); return false; }
            // Meta's system hand ray pivots at the pinch point between thumb and index tips. Using
            // the raw pointer pose (near the wrist) makes our ray start far below the system one.
            Vector3 origin = pose.position;
            if (hand.GetJointPose(HandJointId.HandThumbTip, out Pose thumb) &&
                hand.GetJointPose(HandJointId.HandIndexTip, out Pose index))
            {
                Vector3 pinch = (thumb.position + index.position) * 0.5f;
                if ((pinch - origin).sqrMagnitude < 0.09f) origin = pinch;
            }
            return Process(pointer, new Ray(origin, pose.rotation * Vector3.forward), down, 0);
        }
        private static HandLandmarkStreamer Landmark(HandLandmarkStreamer.HandSide side)
        {
            if (_landmarks == null) _landmarks = FindObjectsByType<HandLandmarkStreamer>(FindObjectsSortMode.None);
            for (int i = 0; i < _landmarks.Length; i++)
                if (_landmarks[i] != null && _landmarks[i].Side == side) return _landmarks[i];
            return null;
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
            // Hovering is not UI intent: the workspace sits in front of the user, so sweeping the
            // ray across a button must not pause sending. Only a press (or a scroll) locks the gate —
            // except on the send button, whose click decides start/pause itself (SessionControl).
            if (hit != null && hit.GetComponentInParent<SessionControl>() == null &&
                (down && !pointer.down || Mathf.Abs(scroll) > 0.5f)) App.Session.EnterUiInteraction();
            if (hit != pointer.hover)
            {
                if (pointer.hover != null) ExecuteEvents.ExecuteHierarchy(pointer.hover, data, ExecuteEvents.pointerExitHandler);
                pointer.hover = hit;
                if (hit != null) ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.pointerEnterHandler);
            }
            Aim(pointer, ray, worldPoint, hit != null, down);
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
                _dragRays[data.pointerId] = ray;
                if (!data.dragging && (data.position - data.pressPosition).sqrMagnitude > 64)
                { data.dragging = true; data.eligibleForClick = false; ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.beginDragHandler); }
                if (data.dragging) ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.dragHandler);
            }
            if (!down && pointer.down) Release(pointer, true);
            if (hit != null && Mathf.Abs(scroll) > 0.15f)
            { data.scrollDelta = new Vector2(0, scroll * Time.unscaledDeltaTime * 100); ExecuteEvents.ExecuteHierarchy(hit, data, ExecuteEvents.scrollHandler); }
            pointer.down = down; return hit != null;
        }
        /// <summary>Ray from the hand to the canvas plus a cursor at the exact landing point.</summary>
        private void Aim(Pointer pointer, Ray ray, Vector3 worldPoint, bool over, bool down)
        {
            if (pointer.line != null)
            {
                pointer.line.enabled = true;
                float gap = Mathf.Min(0.05f, Mathf.Max(0.005f, (worldPoint - ray.origin).magnitude * 0.08f));
                pointer.line.SetPosition(0, ray.origin + ray.direction * gap);
                pointer.line.SetPosition(1, worldPoint);
            }
            var cursor = Cursor(pointer);
            if (cursor == null) return;
            Vector3 local = App.Workspace.Canvas.transform.InverseTransformPoint(worldPoint);
            // Lift the dot ~4.5 mm toward the viewer: the panels are coplanar with the canvas, so a
            // dot left at z=0 z-fights with the very surface it is pointing at.
            cursor.localPosition = new Vector3(local.x, local.y, -5f);
            if (!cursor.gameObject.activeSelf) cursor.gameObject.SetActive(true);
            cursor.SetAsLastSibling();
            if (pointer.core == null) return;
            pointer.core.color = down ? UiKit.Blue : over ? Color.white : new Color(1f, 1f, 1f, 0.62f);
            float size = down ? 11f : over ? 17f : 13f;
            if (!Mathf.Approximately(pointer.core.rectTransform.sizeDelta.x, size))
                pointer.core.rectTransform.sizeDelta = new Vector2(size, size);
        }
        private RectTransform Cursor(Pointer pointer)
        {
            if (pointer.cursor != null) return pointer.cursor;
            var canvas = App.Workspace != null ? App.Workspace.Canvas : null;
            if (canvas == null) return null;
            pointer.cursor = UiKit.Rect(canvas.transform, "PointerCursor", Vector2.zero, new Vector2(34, 34));
            UiKit.Disc(pointer.cursor, "Halo", 25, new Color(0f, 0f, 0f, 0.42f));
            pointer.core = UiKit.Disc(pointer.cursor, "Core", 13, Color.white);
            pointer.cursor.SetAsLastSibling();
            return pointer.cursor;
        }
        private void ReleaseOutside(Pointer pointer, bool down)
        {
            _dragRays.Remove(pointer.data.pointerId);
            if (!down && pointer.down) Release(pointer, false);
            if (pointer.hover != null) ExecuteEvents.ExecuteHierarchy(pointer.hover, pointer.data, ExecuteEvents.pointerExitHandler);
            pointer.hover = null; pointer.down = down; HideVisual(pointer);
        }
        private static void Release(Pointer pointer, bool click)
        {
            var data = pointer.data;
            _dragRays.Remove(data.pointerId);
            if (data.pointerPress != null)
            {
                ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerUpHandler);
                if (click && data.eligibleForClick && !data.dragging)
                    ExecuteEvents.Execute(data.pointerPress, data, ExecuteEvents.pointerClickHandler);
            }
            if (data.dragging && data.pointerDrag != null) ExecuteEvents.Execute(data.pointerDrag, data, ExecuteEvents.endDragHandler);
            data.pointerPress = null; data.rawPointerPress = null; data.pointerDrag = null; data.dragging = false; data.eligibleForClick = false;
        }
        private static void HideVisual(Pointer pointer)
        {
            if (pointer.line != null) pointer.line.enabled = false;
            if (pointer.cursor != null) pointer.cursor.gameObject.SetActive(false);
        }
        private void Clear(Pointer pointer)
        {
            if (pointer.data == null) return;
            Release(pointer, false);
            if (pointer.hover != null) ExecuteEvents.ExecuteHierarchy(pointer.hover, pointer.data, ExecuteEvents.pointerExitHandler);
            pointer.hover = null; pointer.down = false; HideVisual(pointer);
        }
        private void OnDisable()
        {
            foreach (var pointer in _controllers) Clear(pointer);
            foreach (var pointer in _handPointers) Clear(pointer);
            _released = false;
        }
        private void OnDestroy()
        {
            foreach (var pointer in _controllers) if (pointer.line != null) Destroy(pointer.line.material);
            foreach (var pointer in _handPointers) if (pointer.line != null) Destroy(pointer.line.material);
        }
    }
}
