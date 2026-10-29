using UnityEngine;

namespace MORLA.XR
{
    /// <summary>
    /// Quest-style carry for the Touch controllers: hold the grip (either hand) or hold ☰ on the
    /// left controller and the whole workspace comes with your hand — pull it in, push it out,
    /// no need to aim at the title bar first. It keeps facing you and holds a constant angular
    /// size, the same motion bare hands get by pinching the status bar.
    ///
    /// ☰ is shared with the settings toggle (a tap opens the settings), so a hold of
    /// <see cref="MenuHoldSeconds"/> turns the press into a grab and swallows the tap.
    /// </summary>
    [DefaultExecutionOrder(-8500)]
    public sealed class WorkspaceGrab : MonoBehaviour
    {
        // High enough that a finger resting on the grip, or a trigger press with a relaxed
        // squeeze, does not start a carry on top of a ray drag.
        private const float GripThreshold = 0.7f;
        private const float MenuHoldSeconds = 0.45f;
        // Same reach limits as WorkspaceWindowDrag: arm's length to a couple of steps, never below
        // the floor by more than a metre (that is also what WorkspaceUIController.Reachable allows).
        private const float MinDistance = 0.45f, MaxDistance = 2.4f, MaxDropBelowEyes = 1f;
        private int _side = -1;                 // -1 idle, 0 left, 1 right
        private bool _byMenu, _menuUsedForGrab, _menuWasDown;
        private float _menuHeld;
        private Vector3 _pivotStart, _handStart, _flatForward;
        /// <summary>True while a grip/☰ carry owns the workspace pose; ray drags must stand down.</summary>
        public static bool Carrying { get; private set; }
        private AppManager App => AppManager.Instance;

        private void Update()
        {
            var app = App;
            if (app == null || app.Workspace == null || app.Workspace.Canvas == null) return;
            var cam = Camera.main;
            if (cam == null) return;

            bool menuDown = OVRInput.Get(OVRInput.Button.Start, OVRInput.Controller.LTouch);
            if (menuDown) _menuHeld += Time.unscaledDeltaTime;

            if (_side >= 0)
            {
                if (!SourceHeld()) End();
                else if (!Move(cam)) End();
            }
            if (_side < 0 && !WorkspacePointer.IsDragging)
            {
                if (menuDown && _menuHeld >= MenuHoldSeconds && Usable(0)) Begin(0, true);
                else if (GripHeld(0) && Usable(0)) Begin(0, false);
                else if (GripHeld(1) && Usable(1)) Begin(1, false);
            }
            // A tap on ☰ still opens the settings; the press that grabbed the workspace does not.
            if (!menuDown && _menuWasDown)
            {
                if (!_menuUsedForGrab) { app.Session.EnterUiInteraction(); app.Workspace.ToggleSettings(); }
                _menuHeld = 0f; _menuUsedForGrab = false;
            }
            _menuWasDown = menuDown;
        }

        private bool GripHeld(int side) => OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger,
            side == 0 ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch) > GripThreshold;

        private bool SourceHeld() => _byMenu
            ? OVRInput.Get(OVRInput.Button.Start, OVRInput.Controller.LTouch)
            : GripHeld(_side);

        private void Begin(int side, bool byMenu)
        {
            if (!ControllerPosition(side, out Vector3 position)) return;
            _side = side; _byMenu = byMenu; Carrying = true;
            if (byMenu) _menuUsedForGrab = true;
            _pivotStart = transform.position; _handStart = position;
            _flatForward = Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up);
            if (_flatForward.sqrMagnitude < 0.04f) _flatForward = Vector3.forward;
            _flatForward.Normalize();
            // Grabbing is UI intent: sending pauses and does not resume on release (invariant 8).
            App.Session.EnterUiInteraction();
        }

        private void End()
        {
            _side = -1; _byMenu = false; Carrying = false;
            if (App.Workspace != null) App.Workspace.SaveCurrentPose();
        }

        // Never leave the flag standing if this component goes away mid-carry: it gates every ray drag.
        private void OnDisable() { _side = -1; _byMenu = false; Carrying = false; }

        private bool Move(Camera cam)
        {
            if (!ControllerPosition(_side, out Vector3 hand)) return false;
            var canvas = App.Workspace.Canvas.transform;
            Vector3 head = cam.transform.position;
            // Same depth gain as the ray drag: an arm's length cannot cover 0.45–2.4 m, so motion
            // toward/away from the body is amplified while sideways stays one-to-one.
            Vector3 delta = hand - _handStart;
            delta += _flatForward * (Vector3.Dot(delta, _flatForward) * (WorkspaceWindowDrag.DepthGain - 1f));
            Vector3 pivot = _pivotStart + delta;
            Vector3 offset = pivot - head;
            float distance = offset.magnitude;
            if (distance < 0.01f) return false;
            float limited = Mathf.Clamp(distance, MinDistance, MaxDistance);
            if (limited != distance) pivot = head + offset / distance * limited;
            // Turn to look at the user from where the panel now sits: carried above them it tips
            // down at them, carried to the right it turns back toward them.
            Quaternion rotation = WorkspaceWindowDrag.FaceHead(head, pivot, transform.rotation);
            float floor = head.y - MaxDropBelowEyes;
            if (pivot.y < floor) pivot.y = floor;
            transform.SetPositionAndRotation(pivot, rotation);
            // Constant angular size: the same rule WorkspaceUIController uses after placement.
            canvas.localScale = Vector3.one * WorkspaceUIController.ScaleForDistance(limited, App.Settings.angularWindowScale);
            return true;
        }

        private bool ControllerPosition(int side, out Vector3 position)
        {
            position = default;
            if (!Usable(side)) return false;
            var controller = side == 0 ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
            var local = new OVRPose
            {
                position = OVRInput.GetLocalControllerPosition(controller),
                orientation = OVRInput.GetLocalControllerRotation(controller)
            };
            position = local.ToWorldSpacePose(Camera.main).position;
            return true;
        }

        // Only the controller that is actually the active input for that side may carry the panel;
        // a controller resting on the table must not.
        private bool Usable(int side) => App.Tracking != null && App.TrackingEnabled && App.Tracking.ControllerActive(side == 0);
    }
}
