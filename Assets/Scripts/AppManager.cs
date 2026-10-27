using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MORLA.XR
{
    [DefaultExecutionOrder(-10000)]
    public class AppManager : MonoBehaviour
    {
        public static AppManager Instance { get; private set; }
        // Serialized references are kept solely for migration from Scene.unity.
        public TMP_Dropdown protocolDropdown, handDropdown;
        public TMP_InputField ipInputField, portInputField;
        public GameObject menuPanel;
        public TextMeshProUGUI networkStatusText, versionText;
        public Button btnStart;
        public Toggle visualizationToggle, debugInfoToggle, videoStreamToggle, headPoseToggle;
        public Toggle iobtToggle, mixedInputToggle, refresh90Toggle, voiceControlToggle;
        public GameObject[] rayInteractors;
        public GameObject syntheticHandLeft, syntheticHandRight;
        public string targetLogSource = "Left";
        public AppSettings Settings { get; private set; }
        public SessionStatus Status { get; } = new SessionStatus();
        public SessionCoordinator Session { get; private set; }
        public TrackingCoordinator Tracking { get; private set; }
        public WorkspaceUIController Workspace { get; private set; }
        public MorlaRobotClient Robot { get; private set; }
        public bool ShowLandmarks => Settings != null && Settings.showLandmarks;
        public bool ShowDebugInfo => Settings != null && Settings.protocolDebug;
        public bool ShowDiagnostics => Settings != null && Settings.showDiagnostics;
        public bool ShowVideoStream => Settings != null && Settings.videoEnabled;
        public bool TrackHeadPose => Settings != null && Settings.headPose;
        public bool VoiceControlEnabled => Settings != null && Settings.voiceEnabled;
        public bool MixedInputEnabled => Settings != null && Settings.trackingMode == TrackingMode.Mixed;
        public bool WantIobt => Settings != null && Settings.trackingMode == TrackingMode.FullBody;
        public bool TrackIobt => WantIobt;
        public bool TrackingEnabled { get; private set; }
        public bool isStreaming => Session != null && Session.CanSend;
        public string ServerIP => Settings.host;
        public int ServerPort => Settings.posePort;
        public int SelectedProtocol => Settings.protocol;
        public int SelectedHandMode => Settings.outputSide;
        public bool VoiceSessionEnabled => Session != null && Session.VoiceAllowed;
        public bool UiInteractionEnabled => Workspace != null && Workspace.isActiveAndEnabled;
        private readonly ConcurrentQueue<Action> _events = new ConcurrentQueue<Action>();
        private readonly List<TelemetryChannel> _channels = new List<TelemetryChannel>();
        private VideoStreamManager _video;
        private bool _focused = true, _paused, _vrFocused = true, _inputFocused = true, _hmdMounted = true;
        private long _createdUs;
        private readonly HashSet<string> _startupMarks = new HashSet<string>();
        public void MarkStartup(string name)
        {
            if (_startupMarks.Add(name)) Debug.Log("[MORLA Startup] " + name + "_us=" + (QuestPacketEnvelope.MonotonicUs() - _createdUs));
        }
        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this; _createdUs = QuestPacketEnvelope.MonotonicUs();
            ApplyPassthrough(); MarkStartup("passthrough_requested");
            Settings = SettingsStore.Load(debugInfoToggle == null || debugInfoToggle.isOn,
                headPoseToggle == null || headPoseToggle.isOn, videoStreamToggle != null && videoStreamToggle.isOn);
            if (menuPanel != null) menuPanel.SetActive(false);
            foreach (var ray in rayInteractors ?? Array.Empty<GameObject>()) if (ray != null) ray.SetActive(false);
            var oldPointer = FindFirstObjectByType<ControllerMenuPointer>();
            if (oldPointer != null) oldPointer.enabled = false;
            var recenter = GetComponent<MenuRecenter>();
            if (recenter != null) recenter.enabled = false;
        }
        private void Start()
        {
            ApplyPassthrough();
            OVRManager.HMDMounted += OnHmdMounted; OVRManager.HMDUnmounted += OnHmdUnmounted;
            OVRManager.VrFocusAcquired += OnVrFocusAcquired; OVRManager.VrFocusLost += OnVrFocusLost;
            OVRManager.InputFocusAcquired += OnInputFocusAcquired; OVRManager.InputFocusLost += OnInputFocusLost;
            var services = new GameObject("MORLA App Services");
            Session = services.AddComponent<SessionCoordinator>();
            Tracking = services.AddComponent<TrackingCoordinator>();
            Robot = services.AddComponent<MorlaRobotClient>();
            Ensure<BodyPoseStreamer>(services); Ensure<ControllerPoseStreamer>(services); Ensure<VoiceAudioStreamer>(services);
            _video = FindFirstObjectByType<VideoStreamManager>(); _video?.SetVideoUiVisible(false);
            TrackingEnabled = _focused && !_paused && _vrFocused && _inputFocused && _hmdMounted;
            ApplyTrackingMode(); ApplyRefreshRate();
            var prefab = Resources.Load<GameObject>("MorlaWorkspace");
            var root = prefab != null ? Instantiate(prefab) : new GameObject("MORLA Workspace");
            Workspace = root.GetComponent<WorkspaceUIController>() ?? root.AddComponent<WorkspaceUIController>();
            if (root.GetComponent<WorkspacePointer>() == null) root.AddComponent<WorkspacePointer>();
            if (!string.IsNullOrEmpty(SettingsStore.LoadWarning)) Notify(SettingsStore.LoadWarning);
            Debug.Log($"[MORLA Startup] services_ready_us={QuestPacketEnvelope.MonotonicUs() - _createdUs}");
        }
        private static void Ensure<T>(GameObject root) where T : Component
        { if (FindFirstObjectByType<T>() == null) root.AddComponent<T>(); }
        private void Update()
        {
            while (_events.TryDequeue(out var action)) action();
            if (Session != null && OVRInput.GetDown(OVRInput.Button.Start, OVRInput.Controller.LTouch))
            { Session.EnterUiInteraction(); Workspace.ToggleSettings(); }
        }
        public void Enqueue(Action action) => _events.Enqueue(action);
        public TelemetryChannel CreateChannel(string source)
        {
            int generation = Session.Generation;
            var channel = new TelemetryChannel(ServerIP, ServerPort, SelectedProtocol,
                () => Session != null && Session.CanSend && generation == Session.Generation,
                error => Enqueue(() => { if (Session != null && generation == Session.Generation) HandleDisconnection(source + ": " + error); }), source);
            _channels.Add(channel); return channel;
        }
        public bool HasSentData
        {
            get
            {
                if (SelectedHandMode != 2 && !SourceSent("left")) return false;
                if (SelectedHandMode != 1 && !SourceSent("right")) return false;
                if (TrackHeadPose && !ChannelSent("head")) return false;
                if (TrackIobt && !ChannelSent("body")) return false;
                return true;
            }
        }
        private bool SourceSent(string side) => ChannelSent(Status.sources[side].source == "手柄" ? "controller" : side);
        private bool ChannelSent(string name)
        {
            long now = QuestPacketEnvelope.MonotonicUs();
            foreach (var channel in _channels)
                if (channel.Name == name && channel.Connected && channel.Sent > 0 && now - channel.LastSentUs < 500000) return true;
            return false;
        }
        public string TransportDiagnostics()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _channels.Count; i++) sb.Append(_channels[i].Name).Append(": ")
                .Append(_channels[i].Connected ? "连接" : "等待").Append(" · 包 ").Append(_channels[i].Sent).Append(" · ").Append(_channels[i].WritesHz.ToString("F1")).Append(" 包/s").AppendLine();
            return sb.ToString();
        }
        public void CloseTelemetry()
        {
            foreach (var channel in _channels) channel.Dispose(); _channels.Clear();
            foreach (var item in FindObjectsByType<HandLandmarkStreamer>(FindObjectsSortMode.None)) item.CloseTransport();
            FindFirstObjectByType<ControllerPoseStreamer>()?.CloseTransport();
            FindFirstObjectByType<HeadPoseStreamer>()?.CloseTransport(); FindFirstObjectByType<BodyPoseStreamer>()?.CloseTransport();
        }
        public void OnStartStreaming() => Session?.RequestStart();
        public void StopStreaming() => Session?.Pause("位姿发送已暂停");
        public void HandleDisconnection(string error) => Session?.Fail("位姿连接异常：" + error);
        public void Notify(string message) { Status.notice = message; Debug.Log("[MORLA] " + message); }
        public void SaveSettings()
        { try { SettingsStore.Save(Settings); } catch (Exception ex) { Notify("配置保存失败：" + ex.Message); } }
        public bool ApplySettings(AppSettings draft)
        {
            string error = draft.Validate(); if (error != null) { Notify(error); return false; }
            Session.Pause("配置已应用；请明确恢复发送");
            bool mediaChanged = Settings.VideoHost != draft.VideoHost || Settings.videoPort != draft.videoPort;
            bool trackingChanged = Settings.trackingMode != draft.trackingMode;
            Settings = draft.Copy(); Settings.RememberEndpoint(); Tracking.ResetSources();
            if (trackingChanged) { BodyPoseStreamer.ResetReference(); ApplyTrackingMode(); }
            ApplyRefreshRate(); FindFirstObjectByType<VoiceAudioStreamer>()?.StopForSession();
            if (mediaChanged || !Settings.videoEnabled) StopVideo();
            if (!Settings.robotStatusEnabled) Robot.Disconnect();
            SaveSettings(); Workspace.RefreshSettings(); return true;
        }
        public void ApplyTrackingMode()
        {
            bool mixed = TrackingEnabled && MixedInputEnabled;
            if (OVRManager.instance != null) OVRManager.instance.SimultaneousHandsAndControllersEnabled = mixed;
            OVRPlugin.SetSimultaneousHandsAndControllersEnabled(mixed);
            if (!TrackIobt) BodyPoseStreamer.ResetReference();
        }
        public static bool IsTouchPoseValid(HandLandmarkStreamer.HandSide side)
        {
            if (Instance == null || !Instance.MixedInputEnabled) return false;
            var hand = side == HandLandmarkStreamer.HandSide.Left ? OVRInput.Hand.HandLeft : OVRInput.Hand.HandRight;
            return OVRInput.GetControllerIsInHandState(hand) == OVRInput.ControllerInHandState.ControllerInHand;
        }
        private void ApplyRefreshRate()
        {
            float desired = Settings.refresh90 ? 90 : 72, selected = desired;
            var available = OVRPlugin.systemDisplayFrequenciesAvailable;
            if (available != null && available.Length > 0)
            { selected = available[0]; foreach (float value in available) if (Mathf.Abs(value - desired) < Mathf.Abs(selected - desired)) selected = value; }
            if (Mathf.Abs(OVRPlugin.systemDisplayFrequency - selected) > 0.1f) OVRPlugin.systemDisplayFrequency = selected;
        }
        private static void ApplyPassthrough()
        {
            if (OVRManager.instance != null) OVRManager.instance.isInsightPassthroughEnabled = true;
            var layer = FindFirstObjectByType<OVRPassthroughLayer>(); if (layer != null) { layer.enabled = true; layer.hidden = false; }
        }
        public async void StartVideo()
        {
            if (_video == null) { VideoFault("场景缺少视频服务"); return; }
            if (!AppSettings.ValidHost(Settings.VideoHost)) { VideoFault("请为视频配置单播主机地址"); return; }
            Settings.videoEnabled = true; SaveSettings(); Status.video = MediaState.Connecting;
            try { await _video.StartVideoSession(Settings.VideoHost, Settings.videoPort, "720p30", ShowDiagnostics); }
            catch (Exception ex) { await _video.StopVideoSession("start_error"); VideoFault(ex.Message); }
        }
        public async void StopVideo()
        {
            if (_video != null) await _video.StopVideoSession("user_stop"); Status.video = MediaState.Off;
        }
        public void VideoFault(string error)
        {
            Status.video = MediaState.Faulted; Status.videoMessage = error;
            if (Settings.criticalVideo && Session != null && Session.Requested) Session.Pause("必要相机不可用：" + error);
        }
        public void DisconnectHost()
        {
            Session.Pause("已断开主机"); Session.Disconnect(); StopVideo();
            FindFirstObjectByType<VoiceAudioStreamer>()?.StopForSession(); Robot.Disconnect();
        }
        public void SetVoiceEnabled(bool value)
        { Settings.voiceEnabled = value; Session.SetVoiceRequested(value); SaveSettings(); }
        public void SetForeground(bool value)
        {
            if (!value && Session != null)
            { Session.Pause("应用已暂停；恢复后请重新开始发送"); FindFirstObjectByType<VoiceAudioStreamer>()?.StopForSession(); }
            TrackingEnabled = value;
            if (Settings != null) ApplyTrackingMode();
        }
        private void UpdateForeground() => SetForeground(_focused && !_paused && _vrFocused && _inputFocused && _hmdMounted);
        private void OnHmdMounted() { _hmdMounted = true; UpdateForeground(); }
        private void OnHmdUnmounted() { _hmdMounted = false; UpdateForeground(); }
        private void OnVrFocusAcquired() { _vrFocused = true; UpdateForeground(); }
        private void OnVrFocusLost() { _vrFocused = false; UpdateForeground(); }
        private void OnInputFocusAcquired() { _inputFocused = true; UpdateForeground(); }
        private void OnInputFocusLost() { _inputFocused = false; UpdateForeground(); }
        private void OnApplicationFocus(bool focused) { _focused = focused; UpdateForeground(); }
        private void OnApplicationPause(bool paused) { _paused = paused; UpdateForeground(); }
        private void OnDestroy()
        {
            if (Instance != this) return;
            OVRManager.HMDMounted -= OnHmdMounted; OVRManager.HMDUnmounted -= OnHmdUnmounted;
            OVRManager.VrFocusAcquired -= OnVrFocusAcquired; OVRManager.VrFocusLost -= OnVrFocusLost;
            OVRManager.InputFocusAcquired -= OnInputFocusAcquired; OVRManager.InputFocusLost -= OnInputFocusLost;
            Session?.Pause("应用退出"); CloseTelemetry(); Instance = null;
        }
    }
}
