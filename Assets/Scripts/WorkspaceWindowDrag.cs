using UnityEngine;
using UnityEngine.EventSystems;

namespace MORLA.XR
{
    /// <summary>
    /// Moves the whole workspace in 3D the way the Quest shell does: the part you grabbed stays
    /// under the pointer while the panel follows your hand in depth — pull the hand in to bring it
    /// closer, push it out to send it away — instead of sliding on one fixed plane.
    ///
    /// The panel always faces the user (yaw only, so it never tips toward whichever end you grabbed)
    /// and its size follows the distance according to <see cref="AppSettings.angularWindowScale"/>.
    /// </summary>
    public sealed class WorkspaceWindowDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public Transform target;
        public WorkspaceUIController workspace;
        // Arm's length to a couple of steps: closer than this the panel swallows the view, farther
        // the auto-recenter would drag it back anyway.
        private const float MinDistance = 0.45f, MaxDistance = 2.4f, MaxDropBelowEyes = 1f;
        private Vector3 _start, _point, _grabLocal;
        private float _grabDistance;
        private Plane _plane;
        private bool _hasHand;
        private int _pointerId;
        private Camera Cam => Camera.main;

        public void OnBeginDrag(PointerEventData data)
        {
            if (target == null || data.pressEventCamera == null) return;
            // A grip carry (WorkspaceGrab) and a ray drag must never run at the same time: both
            // write the workspace pose every frame and the panel would jitter between them.
            if (WorkspaceGrab.Carrying) return;
            AppManager.Instance.Session.EnterUiInteraction();
            _pointerId = data.pointerId;
            // Screen-space fallback (mouse in the editor): the panel slides on its own plane.
            _start = target.position;
            _plane = new Plane(target.forward, _start);
            var screenRay = data.pressEventCamera.ScreenPointToRay(data.position);
            if (_plane.Raycast(screenRay, out float distance)) _point = screenRay.GetPoint(distance);
            var canvas = workspace != null ? workspace.Canvas : null;
            Ray hand = default;
            _hasHand = canvas != null && WorkspacePointer.TryGetDragRay(_pointerId, out hand);
            if (!_hasHand) return;
            // The grabbed point lives on the pointer ray, so remember how far along that ray it is:
            // keeping that distance constant keeps the point under the cursor for the whole drag.
            Vector3 grabWorld = data.pointerCurrentRaycast.worldPosition;
            _grabDistance = Mathf.Max(0.1f, (grabWorld - hand.origin).magnitude);
            _grabLocal = canvas.transform.InverseTransformPoint(grabWorld);
        }

        public void OnDrag(PointerEventData data)
        {
            if (target == null) return;
            if (!_hasHand) { DragOnPlane(data); return; }
            var canvas = workspace != null ? workspace.Canvas : null; var cam = Cam;
            if (canvas == null || cam == null || !WorkspacePointer.TryGetDragRay(_pointerId, out Ray hand)) return;
            Vector3 head = cam.transform.position;
            Vector3 anchor = hand.GetPoint(_grabDistance);
            // Stay within reach: never push the shell through the user's face or off to the horizon.
            Vector3 offset = anchor - head;
            float distance = offset.magnitude;
            if (distance < 0.01f) return;
            float limited = Mathf.Clamp(distance, MinDistance, MaxDistance);
            if (limited != distance) anchor = head + offset / distance * limited;
            // Face the user, upright and independent of where the panel sits: deriving the rotation
            // from the grab point (an earlier attempt) made the panel tip toward whichever end of
            // the bar you grabbed — right controller tipped it right, left tipped it left.
            Quaternion rotation = FaceUser(head, cam.transform.forward);
            float scale = WorkspaceUIController.ScaleForDistance(limited, AppManager.Instance.Settings.angularWindowScale);
            // Put the grabbed point back under the pointer at the new scale, so scaling the panel
            // never slides the part you are holding out from under the cursor.
            Vector3 position = anchor - rotation * (_grabLocal * scale);
            float floor = head.y - MaxDropBelowEyes;
            if (position.y < floor) position.y = floor;
            target.SetPositionAndRotation(position, rotation);
            canvas.transform.localScale = Vector3.one * scale;
        }

        public void OnEndDrag(PointerEventData data)
        {
            _hasHand = false;
            if (workspace != null) workspace.SaveCurrentPose();
        }

        /// <summary>Yaw only: the panel is parallel to the horizon and square to the user's view.</summary>
        internal static Quaternion FaceUser(Vector3 head, Vector3 forward)
        {
            Vector3 flat = Vector3.ProjectOnPlane(forward, Vector3.up);
            return flat.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(flat.normalized, Vector3.up) : Quaternion.identity;
        }

        private void DragOnPlane(PointerEventData data)
        {
            if (data.pressEventCamera == null) return;
            var ray = data.pressEventCamera.ScreenPointToRay(data.position);
            if (_plane.Raycast(ray, out float distance))
                target.position = _start + Vector3.ClampMagnitude(ray.GetPoint(distance) - _point, 0.7f);
        }
    }
}
