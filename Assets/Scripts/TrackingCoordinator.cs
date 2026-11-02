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
        // A hand only counts while data is actually arriving: IsTrackedDataValid stays true for a
        // while after the hand leaves the view, and a stale wrist pose would then out-vote a
        // controller that is right there in the hand (2026-11-02: right controller stuck at 20 Hz).
        private const float HandFreshSeconds = 0.25f;
        public bool HandTracked(HandLandmarkStreamer.HandSide side)
        {
            float now = Time.unscaledTime;
            foreach (var hand in _hands ?? System.Array.Empty<HandLandmarkStreamer>())
                if (hand != null && hand.Side == side && hand.Hand != null && hand.Hand.IsTrackedDataValid &&
                    now - hand.LastUpdateAt < HandFreshSeconds) return true;
            return false;
        }
        // The runtime briefly drops "connected" while a controller is being picked up or put down.
        // Measured on
        // 2026-11-02: right controller 9–20 Hz while the left held 52–74 Hz, with zero packet loss —
        // the app itself skipped those frames. Riding through half a second keeps the source fresh
        // instead of tripping the 250 ms watchdog that pauses the whole session.
        private const float PoseGraceSeconds = 0.5f;
        private readonly float[] _lastValidTime = { -10f, -10f };
        public bool ControllerActive(bool left)
        {
            var app = AppManager.Instance;
            var side = left ? HandLandmarkStreamer.HandSide.Left : HandLandmarkStreamer.HandSide.Right;
            var controller = left ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
            int index = left ? 0 : 1;

            // Only "connected" can be trusted. Measured 2026-11-02 with both controllers left on a
            // table and the user turned away: GetControllerPositionValid still returned true and
            // the pose sat exactly on the wrist (dist 0.000) — the runtime synthesises a controller
            // pose from hand tracking when the controller is not really tracked. So neither the
            // "position valid" flag nor a wrist-distance test can tell a held controller from an
            // idle one; GetConnectedControllers() can (conn=0 on the table, conn=1 in the hand).
            float now = Time.unscaledTime;
            bool connected = (OVRInput.GetConnectedControllers() & controller) != 0;
            if (connected) _lastValidTime[index] = now;
            else if (now - _lastValidTime[index] > PoseGraceSeconds) return false;

            if (app.MixedInputEnabled) return AppManager.IsTouchPoseValid(side);
            // Connected means the controller is genuinely in use on this side: it wins over a hand
            // the tracker still sees (that is the "拿着手柄却识别成手" case).
            return true;
        }
        private bool WristPosition(HandLandmarkStreamer.HandSide side, out Vector3 position)
        {
            foreach (var hand in _hands ?? System.Array.Empty<HandLandmarkStreamer>())
                if (hand != null && hand.Side == side && hand.Hand != null && hand.Hand.IsTrackedDataValid &&
                    hand.Hand.GetRootPose(out Pose pose)) { position = pose.position; return true; }
            position = default; return false;
        }
        private static Vector3 ControllerPosition(OVRInput.Controller controller)
        {
            var pose = new OVRPose
            {
                position = OVRInput.GetLocalControllerPosition(controller),
                orientation = OVRInput.GetLocalControllerRotation(controller)
            };
            var camera = Camera.main;
            return camera != null ? pose.ToWorldSpacePose(camera).position : pose.position;
        }
        private void Update()
        {
            var app = AppManager.Instance;
            if (!app.TrackingEnabled) { ResetSources(); return; }
            ObserveSide(true); ObserveSide(false);
            LogDiag(app);
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
        // 每秒一条诊断，只在 logcat 里看（adb logcat -s Unity:V | grep XRDIAG）。
        // 现场问题（拿起手柄后左侧 10 秒没有任何数据）靠猜是猜不出来的：这条把两个 OVRInput
        // 信号、手部新鲜度和最终归属一次性打出来。
        private float _lastDiag = -10f;
        private void LogDiag(AppManager app)
        {
            float now = Time.unscaledTime;
            if (now - _lastDiag < 1f) return;
            _lastDiag = now;
            var masks = OVRInput.GetConnectedControllers();
            var text = new System.Text.StringBuilder("[XRDIAG] ");
            text.Append(app.MixedInputEnabled ? "mixed" : "standard");
            for (int i = 0; i < 2; i++)
            {
                bool left = i == 0;
                var controller = left ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
                var side = left ? HandLandmarkStreamer.HandSide.Left : HandLandmarkStreamer.HandSide.Right;
                bool fresh = HandTracked(side);
                float distance = -1f;
                if (fresh && WristPosition(side, out Vector3 wrist))
                    distance = (ControllerPosition(controller) - wrist).magnitude;
                text.Append(" | ").Append(left ? "L" : "R")
                    .Append(" conn=").Append((masks & controller) != 0 ? 1 : 0)
                    .Append(" pv=").Append(OVRInput.GetControllerPositionValid(controller) ? 1 : 0)
                    .Append(" hand=").Append(fresh ? 1 : 0)
                    .Append(" dist=").Append(distance.ToString("F3"))
                    .Append(" active=").Append(ControllerActive(left) ? 1 : 0);
            }
            Debug.Log(text.ToString());
        }
        // 交接瞬间会闪出"手还在、这一侧却暂时不归手柄"或"两边都没有"的几帧。以前前者直接把
        // source.valid 置 false，后者立刻 Observe("未检测", false)：Ready() 当场失败，而暂停之后
        // 永不自动恢复，一次抖动就是一次必须手动「恢复发送」（2026-11-02 真机：拿起手柄必现）。
        private const float SourceLossGrace = 0.5f;
        private readonly float[] _missingSince = { -1f, -1f };
        private void ObserveSide(bool left)
        {
            var app = AppManager.Instance; string key = left ? "left" : "right";
            var side = left ? HandLandmarkStreamer.HandSide.Left : HandLandmarkStreamer.HandSide.Right;
            int index = left ? 0 : 1;
            float now = Time.unscaledTime;
            var status = app.Status.sources[key];

            if (ControllerActive(left)) { _missingSince[index] = -1f; status.Observe("手柄", true); return; }
            // 手在追踪：状态由 HandLandmarkStreamer 自己上报（它会 Observe("手部")），
            // 这里不要再判失效，否则交接那一帧就把源打掉。
            if (HandTracked(side)) { _missingSince[index] = -1f; return; }

            // 两边都没有：给一个宽限窗口，期间保持上一次的有效状态，过了才判未检测。
            if (_missingSince[index] < 0) _missingSince[index] = now;
            if (now - _missingSince[index] > SourceLossGrace) status.Observe("未检测", false);
            else if (status.valid) status.Observe(status.source, true);
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
