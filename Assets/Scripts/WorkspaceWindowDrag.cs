using UnityEngine;
using UnityEngine.EventSystems;

namespace MORLA.XR
{
    /// <summary>
    /// Moves the whole workspace in 3D the way the Quest shell does: the panel is glued to the
    /// hand that grabbed it, so pulling the hand in brings the workspace closer and pushing it out
    /// sends it away — not just sideways on one fixed plane. The panel keeps facing the user and
    /// holds a constant angular size (see <see cref="WorkspaceUIController.ScaleForDistance"/>),
    /// so text stays readable at any distance without a separate zoom control.
    /// </summary>
    public sealed class WorkspaceWindowDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public Transform target;
        public WorkspaceUIController workspace;
        // Arm's length to a couple of steps: closer than this the panel swallows the view, farther
        // the auto-recenter would drag it back anyway.
        private const float MinDistance = 0.45f, MaxDistance = 2.4f, MaxDropBelowEyes = 1f;
        private Vector3 _start, _point, _grabOffset, _grabLocal;
        private Plane _plane;
        private bool _hasHand;
        private int _pointerId;
        private Camera Cam => Camera.main;

        public void OnBeginDrag(PointerEventData data)
        {
            if (target == null || data.pressEventCamera == null) return;
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
            // Keep the grabbed point exactly where the hand holds it. The world-space grab point
            // comes from the raycast we already made; the hand offset is what makes depth work.
            Vector3 grabWorld = data.pointerCurrentRaycast.worldPosition;
            _grabOffset = grabWorld - hand.origin;
            _grabLocal = canvas.transform.InverseTransformPoint(grabWorld);
        }

        public void OnDrag(PointerEventData data)
        {
            if (target == null) return;
            if (!_hasHand) { DragOnPlane(data); return; }
            var canvas = workspace != null ? workspace.Canvas : null; var cam = Cam;
            if (canvas == null || cam == null || !WorkspacePointer.TryGetDragRay(_pointerId, out Ray hand)) return;
            Vector3 head = cam.transform.position;
            Vector3 anchor = hand.origin + _grabOffset;
            // Stay within reach: never push the shell through the user's face or off to the horizon.
            Vector3 offset = anchor - head;
            float distance = offset.magnitude;
            if (distance < 0.01f) return;
            float limited = Mathf.Clamp(distance, MinDistance, MaxDistance);
            if (limited != distance) anchor = head + offset / distance * limited;
            // Upright billboard: the workspace turns to face the user instead of keeping its old tilt.
            Vector3 flat = Vector3.ProjectOnPlane(anchor - head, Vector3.up);
            Quaternion rotation = flat.sqrMagnitude > 0.0016f ? Quaternion.LookRotation(flat.normalized, Vector3.up) : target.rotation;
            // Constant angular size: put the grabbed point back under the hand at the new scale, so
            // growing the panel never slides the part you are holding out from under your fingers.
            float scale = WorkspaceUIController.ScaleForDistance(limited);
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

        private void DragOnPlane(PointerEventData data)
        {
            if (data.pressEventCamera == null) return;
            var ray = data.pressEventCamera.ScreenPointToRay(data.position);
            if (_plane.Raycast(ray, out float distance))
                target.position = _start + Vector3.ClampMagnitude(ray.GetPoint(distance) - _point, 0.7f);
        }
    }
}
