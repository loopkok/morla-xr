using UnityEngine;

namespace MORLA.XR
{
    [DefaultExecutionOrder(-7000)]
    public sealed class SessionCoordinator : MonoBehaviour
    {
        public bool Requested { get; private set; }
        public int Generation { get; private set; }
        private volatile bool _canSend;
        public bool CanSend => _canSend;
        public bool VoiceAllowed => _voiceRequested && !_uiLocked && AppManager.Instance.TrackingEnabled;
        private bool _uiLocked, _voiceRequested;
        private float _releaseSince = -1, _requestedAt, _openedAt;
        private long _resumeAfterUs;
        private bool _established;
        private bool _wasSending;
        public void RequestStart()
        {
            var app = AppManager.Instance; if (Requested) return;
            string error = app.Settings.Validate();
            if (error != null) { app.Notify(error); app.Workspace.ShowSettings(1); return; }
            if (app.Workspace.SettingsVisible) { app.Notify("请先关闭设置，再恢复发送"); return; }
            _uiLocked = false; _releaseSince = -1; _established = false; Requested = true; _requestedAt = Time.unscaledTime;
            app.Status.error = ""; app.Status.telemetry = TelemetryState.Connecting; app.Notify("释放扳机 / 捏合，等待有效追踪");
            if (app.Settings.videoEnabled && app.Status.video == MediaState.Off) app.StartVideo();
            _voiceRequested = app.Settings.voiceEnabled;
        }
        public void SetVoiceRequested(bool requested) { _voiceRequested = requested; if (requested) _uiLocked = false; }
        public void EnterUiInteraction()
        {
            if (Requested || _canSend) Pause("设置操作中：位姿发送已暂停");
            _uiLocked = true; FindFirstObjectByType<VoiceAudioStreamer>()?.StopForSession();
        }
        public void Pause(string reason)
        {
            _canSend = false; Requested = false; _wasSending = false; Generation++; _voiceRequested = false; _uiLocked = true;
            var app = AppManager.Instance; app.CloseTelemetry(); app.Status.telemetry = TelemetryState.Paused; app.Status.error = ""; app.Notify(reason);
            app.Robot?.PauseIfFollowing();
        }
        public void Fail(string reason)
        { Pause(reason); var status = AppManager.Instance.Status; status.telemetry = TelemetryState.Faulted; status.error = reason; }
        public void Disconnect() { _voiceRequested = false; Requested = false; _canSend = false; }
        private void Update()
        {
            var app = AppManager.Instance; if (!Requested) return;
            if (!app.TrackingEnabled) { Pause("应用不在前台"); return; }
            if (!_wasSending)
            {
                if (!WorkspacePointer.InputsReleased()) { _releaseSince = -1; return; }
                if (_releaseSince < 0) { _releaseSince = Time.unscaledTime; _resumeAfterUs = QuestPacketEnvelope.MonotonicUs(); }
                if (Time.unscaledTime - _releaseSince < 0.15f) return;
            }
            bool ready = app.Tracking.Ready(out string reason, _resumeAfterUs);
            bool videoReady = !app.Settings.videoEnabled || !app.Settings.criticalVideo || app.Status.video == MediaState.Playing && app.Settings.videoLayout != VideoLayout.Hidden;
            if (!ready || !videoReady)
            {
                if (_canSend || _wasSending) { Pause(!ready ? reason : "必要相机画面不可用"); return; }
                app.Status.notice = !ready ? reason : "等待必要相机首帧；可在显示设置中调整策略";
                if (Time.unscaledTime - _requestedAt > 15) Fail("准备超时：" + app.Status.notice);
                return;
            }
            if (!_canSend) _openedAt = Time.unscaledTime;
            _canSend = true; _wasSending = true;
            if (app.HasSentData) { _established = true; app.MarkStartup("telemetry_first_required_write"); }
            else if (Time.unscaledTime - _openedAt > (_established ? 0.5f : 2f))
            { Fail("必要数据流没有持续写入；请检查主机与输入源"); return; }
            app.Status.telemetry = app.HasSentData ? TelemetryState.Sending : TelemetryState.Connecting;
            if (app.HasSentData) app.Status.notice = app.SelectedProtocol == 0 ? "UDP 正在发送 · 接收未确认" : "正在发送 · 主机接收以 MORLA 状态为准";
        }
    }
}
