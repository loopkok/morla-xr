using UnityEngine;
using UnityEngine.XR;

namespace MORLA.XR
{
    [DefaultExecutionOrder(-8000)]
    public sealed class TrackingCoordinator : MonoBehaviour
    {
        private HandLandmarkStreamer[] _hands;
        private static readonly string[] RequiredKeys = { "left", "right", "head", "body" };
        private readonly System.Collections.Generic.HashSet<string> _marked = new System.Collections.Generic.HashSet<string>();
        private void Start() { _hands = FindObjectsByType<HandLandmarkStreamer>(FindObjectsSortMode.None); }
        public void ResetSources()
        { foreach (var source in AppManager.Instance.Status.sources.Values) { source.valid = false; source.sampledUs = 0; } }
        public bool HandTracked(HandLandmarkStreamer.HandSide side)
        {
            foreach (var hand in _hands ?? System.Array.Empty<HandLandmarkStreamer>())
                if (hand != null && hand.Side == side && hand.Hand != null && hand.Hand.IsTrackedDataValid) return true;
            return false;
        }
        public bool ControllerActive(bool left)
        {
            var app = AppManager.Instance;
            var side = left ? HandLandmarkStreamer.HandSide.Left : HandLandmarkStreamer.HandSide.Right;
            var controller = left ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
            if (app.MixedInputEnabled) return AppManager.IsTouchPoseValid(side) && OVRInput.GetControllerPositionValid(controller);
            return !HandTracked(side) && OVRInput.GetControllerPositionValid(controller);
        }
        private void Update()
        {
            var app = AppManager.Instance;
            if (!app.TrackingEnabled) { ResetSources(); return; }
            ObserveSide(true); ObserveSide(false);
            var device = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            bool tracked = device.TryGetFeatureValue(CommonUsages.isTracked, out bool value) && value;
#if UNITY_EDITOR
            tracked = Camera.main != null;
#endif
            app.Status.sources["head"].Observe("头部", tracked);
            foreach (var source in app.Status.sources)
                if (!_marked.Contains(source.Key) && source.Value.Fresh(QuestPacketEnvelope.MonotonicUs()))
                { _marked.Add(source.Key); app.MarkStartup("first_valid_" + source.Key); }
        }
        private void ObserveSide(bool left)
        {
            var app = AppManager.Instance; string key = left ? "left" : "right";
            var side = left ? HandLandmarkStreamer.HandSide.Left : HandLandmarkStreamer.HandSide.Right;
            if (ControllerActive(left)) app.Status.sources[key].Observe("手柄", true);
            else if (!HandTracked(side)) app.Status.sources[key].Observe("未检测", false);
            else if (app.Status.sources[key].source != "手部") app.Status.sources[key].valid = false;
        }
        public bool Ready(out string reason, long minimumSampleUs = 0)
        {
            var app = AppManager.Instance; long now = QuestPacketEnvelope.MonotonicUs();
            foreach (string key in RequiredKeys)
            {
                if (key == "left" && app.SelectedHandMode == 2 || key == "right" && app.SelectedHandMode == 1 ||
                    key == "head" && !app.TrackHeadPose || key == "body" && !app.TrackIobt) continue;
                if (!app.Status.sources[key].Fresh(now) || app.Status.sources[key].sampledUs < minimumSampleUs)
                { reason = "等待有效追踪：" + (key == "left" ? "左侧" : key == "right" ? "右侧" : key == "head" ? "头部" : "身体 / 身体权限"); return false; }
            }
            reason = ""; return true;
        }
    }
}
