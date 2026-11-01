using UnityEngine;

namespace MORLA.XR
{
    /// <summary>
    /// Quest-style carry for the Touch controllers: hold ☰ (left controller) and the whole
    /// workspace comes with your hand — pull it in, push it out, no need to aim at the title bar
    /// first. It keeps facing you and holds a constant angular size, the same motion bare hands
    /// get by pinching the status bar.
    ///
    /// Only ☰ is used. The grip (side trigger), the index trigger, the stick and the face buttons
    /// are all sent downstream in the controller packet (`grip`, `trigger`, `stickX/Y`, `mask`),
    /// so none of them may be spent on moving the panel: holding the grip used to start a carry,
    /// which paused sending and — because grip stays held — also kept the release gate shut.
    ///
    /// ☰ is shared with the settings toggle (a tap opens the settings), so a hold of
    /// <see cref="MenuHoldSeconds"/> turns the press into a grab and swallows the tap.
    /// </summary>
    [DefaultExecutionOrder(-8500)]
    public sealed class WorkspaceGrab : MonoBehaviour
    {
        private const float MenuHoldSeconds = 0.45f;
        // Same reach limits as WorkspaceWindowDrag: arm's length to a couple of steps, never below
        // the floor by more than a metre (that is also what WorkspaceUIController.Reachable allows).
        private const float MinDistance = 0.45f, MaxDistance = 2.4f, MaxDropBelowEyes = 1f;
        private bool _menuUsedForGrab, _menuWasDown;
        private float _menuHeld;
        private Vector3 _pivotStart, _handStart, _flatForward;
        /// <summary>True while a ☰ carry owns the workspace pose; ray drags must stand down.</summary>
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

            if (Carrying)
            {
                if (!menuDown) End();
                else if (!Move(cam)) End();
            }
            if (!Carrying && !WorkspacePointer.IsDragging && menuDown &&
                _menuHeld >= MenuHoldSeconds && Usable()) Begin();
            // A tap on ☰ still opens the settings; the press that grabbed the workspace does not.
            if (!menuDown && _menuWasDown)
            {
                if (!_menuUsedForGrab) { app.Session.EnterUiInteraction(); app.Workspace.ToggleSettings(); }
                _menuHeld = 0f; _menuUsedForGrab = false;
            }
            _menuWasDown = menuDown;
        }

        private void Begin()
        {
            if (!ControllerPosition(out Vector3 position)) return;
            Carrying = true;
            _menuUsedForGrab = true;
            _pivotStart = transform.position; _handStart = position;
            _flatForward = Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up);
            if (_flatForward.sqrMagnitude < 0.04f) _flatForward = Vector3.forward;
            _flatForward.Normalize();
            // Grabbing is UI intent: sending pauses and does not resume on release (invariant 8).
            App.Session.EnterUiInteraction();
        }

        private void End()
        {
            Carrying = false;
            if (App.Workspace != null) App.Workspace.SaveCurrentPose();
        }

        // Never leave the flag standing if this component goes away mid-carry: it gates every ray drag.
        private void OnDisable() { Carrying = false; }

        private bool Move(Camera cam)
        {
            if (!ControllerPosition(out Vector3 hand)) return false;
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

        private bool ControllerPosition(out Vector3 position)
        {
            position = default;
            if (!Usable()) return false;
            var local = new OVRPose
            {
                position = OVRInput.GetLocalControllerPosition(OVRInput.Controller.LTouch),
                orientation = OVRInput.GetLocalControllerRotation(OVRInput.Controller.LTouch)
            };
            position = local.ToWorldSpacePose(Camera.main).position;
            return true;
        }

        // Only the left controller (the one that owns ☰) may carry the panel, and only while it is
        // really the active input for that side; a controller resting on the table must not.
        private bool Usable() => App.Tracking != null && App.TrackingEnabled && App.Tracking.ControllerActive(false);
    }
}
