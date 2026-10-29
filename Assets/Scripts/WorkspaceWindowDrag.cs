using UnityEngine;
using UnityEngine.EventSystems;

namespace MORLA.XR
{
    /// <summary>
    /// Moves the whole workspace in 3D the way the Quest shell does: the part you grabbed stays
    /// under the pointer while the panel follows your hand in depth — pull the hand in to bring it
    /// closer, push it out to send it away — instead of sliding on one fixed plane.
    ///
    /// The panel turns to face the user from wherever it now sits (above you it tips down at you,
    /// to your right it turns back toward you), and its size follows the distance according to
    /// <see cref="AppSettings.angularWindowScale"/>.
    /// </summary>
    public sealed class WorkspaceWindowDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        public Transform target;
        public WorkspaceUIController workspace;
        // Arm's length to a couple of steps: closer than this the panel swallows the view, farther
        // the auto-recenter would drag it back anyway.
        private const float MinDistance = 0.45f, MaxDistance = 2.4f, MaxDropBelowEyes = 1f;
        private Vector3 _start, _point, _grabLocal, _grabOrigin, _flatForward;
        private float _grabDistance;
        private Plane _plane;
        private bool _hasHand;
        private int _pointerId;
        private Camera Cam => Camera.main;

        public void OnBeginDrag(PointerEventData data)
        {
            if (target == null || data.pressEventCamera == null || Cam == null) return;
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
            _grabOrigin = hand.origin;
            _flatForward = Vector3.ProjectOnPlane(Cam.transform.forward, Vector3.up);
            if (_flatForward.sqrMagnitude < 0.04f) _flatForward = Vector3.forward;
            _flatForward.Normalize();
        }

        public void OnDrag(PointerEventData data)
        {
            if (target == null) return;
            if (!_hasHand) { DragOnPlane(data); return; }
            var canvas = workspace != null ? workspace.Canvas : null; var cam = Cam;
            if (canvas == null || cam == null || !WorkspacePointer.TryGetDragRay(_pointerId, out Ray hand)) return;
            Vector3 head = cam.transform.position;
            // Hand travel toward/away from the body moves the panel further than the hand itself,
            // because an arm's length is nowhere near the 0.45–2.4 m the workspace can use. The
            // depth part of the motion is amplified; sideways stays one-to-one.
            float depth = Vector3.Dot(hand.origin - _grabOrigin, _flatForward);
            Vector3 anchor = hand.GetPoint(_grabDistance + depth * (DepthGain - 1f));
            // Stay within reach: never push the shell through the user's face or off to the horizon.
            Vector3 offset = anchor - head;
            float distance = offset.magnitude;
            if (distance < 0.01f) return;
            float limited = Mathf.Clamp(distance, MinDistance, MaxDistance);
            if (limited != distance) anchor = head + offset / distance * limited;
            float scale = WorkspaceUIController.ScaleForDistance(limited, AppManager.Instance.Settings.angularWindowScale);
            // Solve pose and orientation together: the panel's centre depends on the rotation and the
            // rotation must look at the user from that centre. Two passes converge (metres vs mm);
            // deriving the rotation from the grabbed point instead tipped the panel toward whichever
            // end of the bar was grabbed, which is why the right controller tipped it right.
            Quaternion rotation = target.rotation;
            Vector3 position = anchor;
            for (int pass = 0; pass < 2; pass++)
            {
                position = anchor - rotation * (_grabLocal * scale);
                rotation = FaceHead(head, position, rotation);
            }
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

        /// <summary>Depth gain shared with <see cref="WorkspaceGrab"/>.</summary>
        internal const float DepthGain = 2f;

        /// <summary>Face the head from the given centre, upright; keeps the previous up axis when vertical.</summary>
        internal static Quaternion FaceHead(Vector3 head, Vector3 centre, Quaternion previous)
        {
            Vector3 dir = centre - head;
            if (dir.sqrMagnitude < 1e-5f) return previous;
            dir.Normalize();
            Vector3 up = Vector3.ProjectOnPlane(dir, Vector3.up).sqrMagnitude > 4e-4f ? Vector3.up : previous * Vector3.up;
            return Quaternion.LookRotation(dir, up);
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
