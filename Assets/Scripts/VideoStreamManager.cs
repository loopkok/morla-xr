using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace MORLA.XR
{

public class VideoStreamManager : MonoBehaviour
{
    public enum SessionState { Idle, SignalingConnecting, AwaitConfig, OfferSent, Connected, Playing, Stopping }

    [SerializeField] private QuestVideoReceiver videoReceiver;
    [SerializeField] private VideoPanelRenderer panelRenderer;   // template / track 0
    [SerializeField] private VideoStatsOverlay statsOverlay;
    [SerializeField] private string logSource = "Left";
    [Tooltip("Anchor (CenterEyeAnchor) panels are parented to for eye-relative layout. If empty, template panel's parent is used.")]
    [SerializeField] private Transform eyeAnchor;
    [Tooltip("Fallback seconds to wait for video_config before assuming a single-track legacy host.")]
    [SerializeField] private float configTimeout = 2.5f;

    [Header("Muted track display (host track_visibility)")]
    [Tooltip("Muted track panels shrink to small chips stacked at the bottom-left view edge. If false, muted panels are hidden entirely.")]
    [SerializeField] private bool minimizeMutedPanels = true;
    [Tooltip("Size multiplier for minimized chips (relative to the FOV-derived natural size).")]
    [SerializeField] private float minimizedScale = 0.12f;
    [Tooltip("Eye-space position (meters) of the first minimized chip.")]
    [SerializeField] private Vector3 minimizedOrigin = new Vector3(-0.45f, -0.26f, 1.2f);
    [Tooltip("Horizontal spacing (meters) between stacked minimized chips.")]
    [SerializeField] private float minimizedSpacing = 0.24f;

    private VideoSignalingClient _signalingClient;
    private SessionState _state = SessionState.Idle;
    private string _sessionId;
    private bool _isStopping;
    private readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();

    private readonly List<TrackSpec> _tracks = new List<TrackSpec>();
    private readonly List<VideoPanelRenderer> _panels = new List<VideoPanelRenderer>();
    private float _configTimer;
    private bool _configResolved;
    private bool _offerStarted;

    public SessionState CurrentState => _state;
    private int _generation, _selectedTrack;
    private bool _visible = true;
    private long _lastPrimaryFrame, _frameWindowStart;
    private int _frameWindowCount;
    private float _receiveFps;
    public float ReceiveFps => LastFrameAgeSeconds >= 0 && LastFrameAgeSeconds < 0.75 ? _receiveFps : 0;
    private float _startedAt;
    private string _layoutKey = "";
    private Transform _worldAnchor;
    private bool _visibilityReceived;
    private readonly HashSet<string> _enabledTracks = new HashSet<string>();
    private readonly Dictionary<int, long> _frameTimes = new Dictionary<int, long>();
    public string[] TrackLabels => _tracks.ConvertAll(t => t.label).ToArray();
    public void SelectPrimaryTrack(int index) { if (index < 0 || index >= _tracks.Count) return; _selectedTrack = index; _layoutKey = ""; }
    public double LastFrameAgeSeconds => _lastPrimaryFrame == 0 ? -1 : (QuestPacketEnvelope.MonotonicUs() - _lastPrimaryFrame) / 1000000.0;

    private class TrackSpec
    {
        public string label = "camera";
        public int width = 1280;
        public int height = 720;
        public float fovH = 69f;
        public Vector3 position = new Vector3(0f, -0.1f, 1.8f);
        public float distance = 1.8f;
        public float sizeMultiplier = 1f;
    }

    public void SetVideoUiVisible(bool visible)
    {
        _visible = visible;
        foreach (var panel in _panels) panel?.SetVisible(visible);
        if (_panels.Count == 0) panelRenderer?.SetVisible(visible);
        if (!visible) statsOverlay?.SetVisible(false);
        _layoutKey = "";
    }

    public async Task<bool> StartVideoSession(string signalingHost, int signalingPort, string preset, bool showDebugStats)
    {
        if (_state != SessionState.Idle) return false;
        if (videoReceiver == null) { FailFatal("Video receiver missing."); return false; }

        int generation = ++_generation;
        _lastPrimaryFrame = 0; _frameWindowStart = 0; _frameWindowCount = 0; _receiveFps = 0; _frameTimes.Clear(); _visibilityReceived = false; _enabledTracks.Clear(); _startedAt = Time.unscaledTime;
        _layoutKey = "";
        _sessionId = Guid.NewGuid().ToString("N");
        _signalingClient = new VideoSignalingClient();
        _signalingClient.OnEnvelope += e => EnqueueMainThread(() => { if (generation == _generation) HandleEnvelope(e); });
        _signalingClient.OnError += error => EnqueueMainThread(() => { if (generation == _generation) FailFatal(error); });
        _signalingClient.OnConnected += () => EnqueueMainThread(() =>
        {
            if (generation != _generation) return;
            _state = SessionState.Connected;
            statsOverlay?.SetSignalingState("connected");
        });
        _signalingClient.OnDisconnected += () => EnqueueMainThread(() => { if (generation == _generation && !_isStopping) FailFatal("视频信令已断开"); });

        _state = SessionState.SignalingConnecting;
        SetVideoUiVisible(true);
        panelRenderer?.SetStatus("Connecting Video...");
        statsOverlay?.SetVisible(showDebugStats);
        statsOverlay?.SetPreset(preset);
        statsOverlay?.SetSignalingState("connecting");
        LogInfo($"session={_sessionId} signaling connect {signalingHost}:{signalingPort} preset={preset}");

        bool connected = await _signalingClient.ConnectAsync(signalingHost, signalingPort, 4000);
        if (generation != _generation) return false;
        if (!connected) { FailFatal("Signaling connection failed."); return false; }
        panelRenderer?.SetStatus("Signaling Connected");
        LogInfo("signaling connected");

        videoReceiver.OnLocalOfferReady += OnLocalOfferReady;
        videoReceiver.OnLocalIceCandidate += OnLocalIceCandidate;
        videoReceiver.OnRemoteTextureForTrack += OnRemoteTextureForTrack;
        videoReceiver.OnPeerStateChanged += OnPeerStateChanged;
        videoReceiver.OnError += FailFatal;

        // Send hello; host replies with hello_ack + video_config. We wait for
        // video_config (or timeout) before creating the offer so the number of
        // recv transceivers matches the number of host sources.
        await _signalingClient.SendAsync("hello", _sessionId, $"{{\"app_version\":\"{Application.version}\",\"video_preset\":\"{preset}\",\"client_name\":\"quest\"}}");
        if (generation != _generation) return false;
        _state = SessionState.AwaitConfig;
        if (AppManager.Instance != null) AppManager.Instance.Status.video = MediaState.WaitingFrame;
        _configTimer = 0f;
        _configResolved = false;
        _offerStarted = false;
        LogInfo("sent hello; awaiting video_config");
        return true;
    }

    public async Task StopVideoSession(string reason)
    {
        if (_isStopping) return;
        _isStopping = true;
        ++_generation;
        _state = SessionState.Stopping;
        statsOverlay?.SetSignalingState("stopping");
        LogInfo($"stopping session reason={reason}");

        try
        {
            if (_signalingClient != null && _signalingClient.IsConnected && !string.IsNullOrWhiteSpace(_sessionId))
            {
                await _signalingClient.SendAsync("stop_video", _sessionId,
                    new JObject { ["reason"] = reason ?? "stop" }.ToString(Newtonsoft.Json.Formatting.None));
            }
        }
        catch { }

        if (videoReceiver != null)
        {
            videoReceiver.OnLocalOfferReady -= OnLocalOfferReady;
            videoReceiver.OnLocalIceCandidate -= OnLocalIceCandidate;
            videoReceiver.OnRemoteTextureForTrack -= OnRemoteTextureForTrack;
            videoReceiver.OnPeerStateChanged -= OnPeerStateChanged;
            videoReceiver.OnError -= FailFatal;
            videoReceiver.ClosePeer();
        }
        if (_signalingClient != null) { _signalingClient.Dispose(); _signalingClient = null; }

        ClearPanels();
        panelRenderer?.Clear();
        panelRenderer?.SetStatus("Video Off");
        statsOverlay?.SetSignalingState("idle");
        statsOverlay?.SetPeerState("idle");

        _sessionId = string.Empty;
        _state = SessionState.Idle;
        _isStopping = false;
        SetVideoUiVisible(false);
        _frameTimes.Clear(); _lastPrimaryFrame = 0;
        if (AppManager.Instance != null) AppManager.Instance.Status.video = MediaState.Off;
        LogInfo("session stopped");
    }

    private void Update()
    {
        while (_mainThreadActions.TryDequeue(out Action action))
        {
            try { action?.Invoke(); } catch { }
        }
        UpdateWorkspaceState();
        if (_state == SessionState.AwaitConfig && !_configResolved)
        {
            _configTimer += Time.deltaTime;
            if (_configTimer >= configTimeout)
            {
                LogInfo("video_config timeout; assuming 1 track (legacy host)");
                ResolveConfig(new List<TrackSpec> { new TrackSpec() });
            }
        }
    }

    private async void OnLocalOfferReady(string sdp)
    {
        if (_signalingClient == null) return;
        if (string.IsNullOrWhiteSpace(sdp)) { FailFatal("Local SDP offer is empty."); return; }

        int videoMLines = CountOccurrences(sdp, "m=video");
        LogInfo($"local offer sdp_len={sdp.Length} video_mlines={videoMLines}");
        if (videoMLines == 0) { FailFatal("Local SDP offer missing m=video."); return; }

        int generation = _generation;
        try
        {
            await _signalingClient.SendAsync("offer", _sessionId,
                new JObject { ["sdp"] = sdp ?? string.Empty }.ToString(Newtonsoft.Json.Formatting.None));
            if (generation == _generation) LogInfo("offer sent to host");
        }
        catch (Exception ex) { if (generation == _generation) FailFatal(ex.Message); }
    }

    private async void OnLocalIceCandidate(string candidate, string sdpMid, int? sdpMLineIndex)
    {
        if (_signalingClient == null) return;
        var payload = new JObject
        {
            ["candidate"] = candidate ?? string.Empty,
            ["sdpMid"] = sdpMid,
            ["sdpMLineIndex"] = sdpMLineIndex.HasValue ? sdpMLineIndex.Value : JValue.CreateNull(),
        };
        int generation = _generation;
        try
        {
            await _signalingClient.SendAsync("ice_candidate", _sessionId, payload.ToString(Newtonsoft.Json.Formatting.None));
            if (generation == _generation) LogDebug("local ICE sent");
        }
        catch (Exception ex) { if (generation == _generation) FailFatal(ex.Message); }
    }

    private void OnRemoteTextureForTrack(int trackIndex, Texture texture)
    {
        if (_isStopping || _state == SessionState.Idle) return;
        _frameTimes[trackIndex] = QuestPacketEnvelope.MonotonicUs();
        if (trackIndex == _selectedTrack)
        {
            _lastPrimaryFrame = _frameTimes[trackIndex]; AppManager.Instance?.MarkStartup("video_first_decoded_frame");
            _frameWindowCount++;
            if (_frameWindowStart == 0) _frameWindowStart = _lastPrimaryFrame;
            if (_lastPrimaryFrame - _frameWindowStart >= 1000000)
            { _receiveFps = _frameWindowCount * 1000000f / (_lastPrimaryFrame - _frameWindowStart); _frameWindowStart = _lastPrimaryFrame; _frameWindowCount = 0; }
        }
        if (trackIndex >= 0 && trackIndex < _panels.Count && _panels[trackIndex] != null)
        {
            _panels[trackIndex].SetTexture(texture);
            _panels[trackIndex].SetStatus(string.Empty);
        }
        if (trackIndex == 0)
        {
            panelRenderer?.SetTexture(texture);
            panelRenderer?.SetStatus(string.Empty);
        }
        if (_state != SessionState.Playing)
        {
            _state = SessionState.Playing;
            LogInfo($"remote texture received (playing) track={trackIndex}");
        }
    }

    private void OnPeerStateChanged(string state)
    {
        statsOverlay?.SetPeerState(state);
        LogDebug($"peer state: {state}");
    }

    private async void HandleEnvelope(VideoSignalingClient.Envelope envelope)
    {
        if (envelope.SessionId != _sessionId) return;
        switch (envelope.Type)
        {
            case "hello_ack":
                statsOverlay?.SetSignalingState("ready");
                LogInfo("hello_ack received");
                break;
            case "video_config":
                HandleVideoConfig(envelope.PayloadJson);
                break;
            case "answer":
                await HandleAnswer(envelope.PayloadJson);
                break;
            case "ice_candidate":
                HandleRemoteIce(envelope.PayloadJson);
                break;
            case "video_state":
                HandleVideoState(envelope.PayloadJson);
                break;
            case "stats":
                HandleStats(envelope.PayloadJson);
                break;
            case "track_visibility":
                HandleTrackVisibility(envelope.PayloadJson);
                break;
            case "error":
                HandleServerError(envelope.PayloadJson);
                break;
            case "pong":
                LogDebug("pong received");
                break;
            default:
                LogDebug($"unknown envelope type={envelope.Type}");
                break;
        }
    }

    private void HandleVideoConfig(string payloadJson)
    {
        if (_configResolved) { LogDebug("video_config received again; ignoring"); return; }
        List<TrackSpec> specs = new List<TrackSpec>();
        try
        {
            JObject root = JObject.Parse(payloadJson);
            JArray arr = root["tracks"] as JArray;
            if (arr == null || arr.Count == 0)
            {
                specs.Add(ParseLegacyTrackSpec(root));
            }
            else
            {
                foreach (JObject t in arr) specs.Add(ParseTrackSpec(t));
            }
        }
        catch (Exception ex)
        {
            LogInfo($"video_config parse failed: {ex.Message}; assuming 1 track");
            specs.Add(new TrackSpec());
        }
        var labels = new List<string>();
        foreach (var s in specs) labels.Add(s.label);
        LogInfo($"video_config tracks={specs.Count} labels={string.Join(",", labels)}");
        ResolveConfig(specs);
    }

    private static TrackSpec ParseTrackSpec(JObject t)
    {
        var s = new TrackSpec
        {
            label = t.Value<string>("label") ?? "camera",
            width = t.Value<int?>("width") ?? 1280,
            height = t.Value<int?>("height") ?? 720,
            fovH = t.Value<float?>("fov_h_deg") ?? 69f,
        };
        JObject layout = t["layout"] as JObject;
        if (layout != null)
        {
            JArray pos = layout["position"] as JArray;
            if (pos != null && pos.Count >= 3)
            {
                s.position = new Vector3(
                    (float)(pos[0] ?? 0f), (float)(pos[1] ?? -0.1f), (float)(pos[2] ?? 1.8f));
            }
            s.distance = layout.Value<float?>("distance") ?? 1.8f;
            s.sizeMultiplier = layout.Value<float?>("size_multiplier") ?? 1f;
        }
        return s;
    }

    private static TrackSpec ParseLegacyTrackSpec(JObject root)
    {
        return new TrackSpec
        {
            label = root.Value<string>("label") ?? "camera",
            width = root.Value<int?>("width") ?? 1280,
            height = root.Value<int?>("height") ?? 720,
            fovH = root.Value<float?>("fov_h_deg") ?? 69f,
        };
    }

    private async void ResolveConfig(List<TrackSpec> specs)
    {
        if (_configResolved) return;
        int generation = _generation;
        _configResolved = true;
        _tracks.Clear();
        _tracks.AddRange(specs);

        _selectedTrack = Mathf.Clamp(_selectedTrack, 0, Mathf.Max(0, specs.Count - 1));
        BuildPanels(specs);

        videoReceiver.SetExpectedTrackCount(specs.Count);
        videoReceiver.InitializePeer();

        try
        {
            await _signalingClient.SendAsync("start_video", _sessionId, "{}");
            panelRenderer?.SetStatus("Starting Video...");
            LogInfo("sent start_video");
        }
        catch (Exception ex)
        {
            FailFatal($"start_video send failed: {ex.Message}");
            return;
        }

        if (generation != _generation || _isStopping) return;
        if (_offerStarted) return;
        _offerStarted = true;
        _state = SessionState.OfferSent;
        bool offerOk = await videoReceiver.CreateAndSendOfferAsync();
        if (generation != _generation) return;
        if (!offerOk) FailFatal("Offer creation failed.");
        else LogInfo("local offer created");
    }

    private void BuildPanels(List<TrackSpec> specs)
    {
        ClearPanels();
        Transform anchor = ResolveEyeAnchor();

        if (specs.Count > 0)
        {
            ConfigurePanel(panelRenderer, specs[0], anchor);
            _panels.Add(panelRenderer);
        }
        if (specs.Count > 1 && panelRenderer != null && panelRenderer.PanelRoot != null)
        {
            for (int i = 1; i < specs.Count; i++)
            {
                GameObject clone = Instantiate(panelRenderer.PanelRoot, anchor, false);
                clone.name = $"VideoCanvas_{specs[i].label}_{i}";
                var rawImage = clone.GetComponentInChildren<RawImage>();
                var rend = clone.GetComponent<VideoPanelRenderer>() ?? clone.AddComponent<VideoPanelRenderer>();
                SetRendererFields(rend, clone, rawImage);
                ConfigurePanel(rend, specs[i], anchor);
                _panels.Add(rend);
            }
        }
    }

    private void ConfigurePanel(VideoPanelRenderer rend, TrackSpec spec, Transform anchor)
    {
        if (rend == null) return;
        rend.SetAnchor(anchor, spec.position, spec.distance);
        rend.SetSizeMultiplier(spec.sizeMultiplier);
        rend.ApplyFov(spec.fovH, spec.width, spec.height);
        rend.SetVisible(true);
        rend.SetCaption(spec.label);
        rend.SetStatus("等待相机首帧…");
    }

    private void SetRendererFields(VideoPanelRenderer rend, GameObject root, RawImage rawImage)
    {
        try
        {
            var t = typeof(VideoPanelRenderer);
            SetPrivate(t, rend, "panelRoot", root);
            SetPrivate(t, rend, "targetRawImage", rawImage);
            var placeholder = root.GetComponentInChildren<TMPro.TextMeshProUGUI>();
            if (placeholder != null) SetPrivate(t, rend, "placeholderText", placeholder);
        }
        catch (Exception ex) { LogDebug($"SetRendererFields failed: {ex.Message}"); }
    }

    private static void SetPrivate(Type type, object obj, string name, object value)
    {
        var field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        if (field != null) field.SetValue(obj, value);
    }

    private Transform ResolveEyeAnchor()
    {
        if (AppManager.Instance != null && AppManager.Instance.Settings.worldLockedVideo)
        {
            if (_worldAnchor == null)
            {
                _worldAnchor = new GameObject("Video World Anchor").transform;
                var camera = Camera.main;
                if (camera != null) _worldAnchor.SetPositionAndRotation(camera.transform.position, camera.transform.rotation);
            }
            return _worldAnchor;
        }
        if (_worldAnchor != null) { Destroy(_worldAnchor.gameObject); _worldAnchor = null; }
        if (eyeAnchor != null) return eyeAnchor;
        // Prefer the main camera (CenterEyeAnchor in the OVR rig) so layout
        // positions are eye-relative. Falls back to the template panel's parent.
        var cam = Camera.main;
        if (cam != null) return cam.transform;
        if (panelRenderer != null && panelRenderer.PanelRoot != null
            && panelRenderer.PanelRoot.transform.parent != null)
        {
            return panelRenderer.PanelRoot.transform.parent;
        }
        return transform;
    }

    private void ClearPanels()
    {
        for (int i = 1; i < _panels.Count; i++)
        {
            if (_panels[i] != null && _panels[i].PanelRoot != null) Destroy(_panels[i].PanelRoot);
        }
        _panels.Clear();
    }

    // __TAIL_SENTINEL__

    private async Task HandleAnswer(string payloadJson)
    {
        try
        {
            JObject root = JObject.Parse(payloadJson);
            string sdp = root.Value<string>("sdp");
            if (string.IsNullOrWhiteSpace(sdp)) { FailFatal("Empty SDP answer."); return; }
            bool ok = await videoReceiver.SetRemoteAnswerAsync(sdp);
            if (!ok) FailFatal("Failed to set remote answer.");
            LogInfo("answer applied");
        }
        catch (Exception ex) { FailFatal($"Invalid answer payload: {ex.Message}"); }
    }

    private void HandleRemoteIce(string payloadJson)
    {
        try
        {
            JObject root = JObject.Parse(payloadJson);
            string candidate = root.Value<string>("candidate") ?? string.Empty;
            string sdpMid = root["sdpMid"]?.Type == JTokenType.Null ? null : root.Value<string>("sdpMid");
            int? sdpMLineIndex = root["sdpMLineIndex"]?.Type == JTokenType.Integer
                ? root.Value<int?>("sdpMLineIndex") : null;
            videoReceiver.AddRemoteIceCandidate(candidate, sdpMid, sdpMLineIndex);
            LogDebug("remote ICE applied");
        }
        catch (Exception ex) { FailFatal($"Invalid remote ICE payload: {ex.Message}"); }
    }

    private void HandleVideoState(string payloadJson)
    {
        try
        {
            JObject root = JObject.Parse(payloadJson);
            string state = root.Value<string>("state") ?? "unknown";
            string reason = root.Value<string>("reason") ?? string.Empty;
            statsOverlay?.SetSignalingState(state);
            LogInfo($"video_state={state} reason={reason}");
            if (state == "error") FailFatal($"Video state error: {reason}");
        }
        catch (Exception ex) { FailFatal($"Invalid video_state payload: {ex.Message}"); }
    }

    private void HandleStats(string payloadJson)
    {
        try
        {
            JObject root = JObject.Parse(payloadJson);
            float fps = root.Value<float?>("fps") ?? 0f;
            float bitrate = root.Value<float?>("bitrate_kbps") ?? 0f;
            int drops = root.Value<int?>("frame_drops") ?? 0;
            float rtt = root["rtt_ms"]?.Type == JTokenType.Null ? -1f : (root.Value<float?>("rtt_ms") ?? -1f);
            statsOverlay?.SetStats(fps, bitrate, drops, rtt);
            LogDebug($"stats fps={fps:F1} bitrate={bitrate:F0} drops={drops} rtt={rtt:F1}");
        }
        catch { }
    }

    private void HandleTrackVisibility(string payloadJson)
    {
        try
        {
            JArray enabled = JObject.Parse(payloadJson)["enabled"] as JArray;
            if (enabled == null) return;
            _enabledTracks.Clear();
            foreach (var item in enabled) _enabledTracks.Add(item.ToString());
            _visibilityReceived = true; _layoutKey = "";
        }
        catch (Exception ex) { LogDebug("track_visibility parse failed: " + ex.Message); }
    }

    private void HandleServerError(string payloadJson)
    {
        try
        {
            JObject root = JObject.Parse(payloadJson);
            string message = root.Value<string>("message") ?? "Unknown video error";
            FailFatal(message);
        }
        catch (Exception ex) { FailFatal($"Video error payload parse failed: {ex.Message}"); }
    }

    private async void FailFatal(string reason)
    {
        if (_isStopping) return;
        // An error that arrives after the session was torn down belongs to a stopped generation;
        // reporting it would pause a telemetry session the user just restarted.
        if (_state == SessionState.Idle) { LogInfo("ignored stale video error: " + reason); return; }
        statsOverlay?.SetError(reason);
        await StopVideoSession("video_error");
        AppManager.Instance?.VideoFault(reason);
    }

    private void UpdateWorkspaceState()
    {
        var app = AppManager.Instance;
        if (app == null || _state == SessionState.Idle || _isStopping) return;
        if (_frameTimes.TryGetValue(_selectedTrack, out long received)) _lastPrimaryFrame = received;
        if (_lastPrimaryFrame > 0)
        {
            bool selectedEnabled = !_visibilityReceived || _selectedTrack < _tracks.Count && _enabledTracks.Contains(_tracks[_selectedTrack].label);
            bool stale = LastFrameAgeSeconds > 0.75 || !selectedEnabled;
            app.Status.video = stale ? MediaState.Stale : MediaState.Playing;
            for (int i = 0; i < _panels.Count; i++)
            {
                bool trackStale = !_frameTimes.TryGetValue(i, out long frame) || QuestPacketEnvelope.MonotonicUs() - frame > 750000;
                _panels[i]?.SetStatus(trackStale ? "画面已中断 · 非实时画面" : "");
            }
            if (stale && app.Settings.videoEnabled && app.Settings.criticalVideo && app.Session.Requested) app.Session.Pause("必要相机画面已中断");
        }
        else if (Time.unscaledTime - _startedAt > 12) { FailFatal("等待视频首帧超时"); return; }
        else app.Status.video = MediaState.WaitingFrame;
        string layoutKey = app.Settings.videoLayout + ":" + app.Settings.videoScale + ":" + app.Settings.worldLockedVideo + ":" +
            (app.Workspace != null && app.Workspace.SettingsVisible) + ":" + _selectedTrack + ":" + _visible + ":" + _panels.Count;
        if (layoutKey == _layoutKey) return;
        _layoutKey = layoutKey;
        Transform anchor = ResolveEyeAnchor();
        bool compact = app.Settings.videoLayout == VideoLayout.Thumbnail || app.Workspace != null && app.Workspace.SettingsVisible;
        for (int i = 0; i < _panels.Count && i < _tracks.Count; i++)
        {
            var panel = _panels[i]; if (panel == null) continue;
            bool hostVisible = !_visibilityReceived || _enabledTracks.Contains(_tracks[i].label);
            panel.SetVisible(_visible && app.Settings.videoLayout != VideoLayout.Hidden && (hostVisible || minimizeMutedPanels));
            bool thumbnail = compact || i != _selectedTrack || !hostVisible;
            Vector3 position = thumbnail ? new Vector3(-0.65f + 0.27f * i, -0.25f, 1.2f) : _tracks[i].position;
            float scale = thumbnail ? 0.16f : _tracks[i].sizeMultiplier * app.Settings.videoScale;
            panel.SetAnchor(anchor, position, thumbnail ? 1.2f : _tracks[i].distance);
            panel.SetSizeMultiplier(scale);
        }
    }

    private void OnDestroy()
    {
        ++_generation; _signalingClient?.Dispose();
        if (_worldAnchor != null) Destroy(_worldAnchor.gameObject);
    }

    private void EnqueueMainThread(Action action)
    {
        if (action != null) _mainThreadActions.Enqueue(action);
    }

    private static int CountOccurrences(string text, string sub)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(sub)) return 0;
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(sub, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += sub.Length;
        }
        return count;
    }

    private void LogDebug(string msg)
    {
        if (LogManager.Instance == null) return;
        bool shouldLog = AppManager.Instance != null && AppManager.Instance.ShowDebugInfo;
        if (!shouldLog) return;
        LogManager.Instance.Log(logSource, $"[VideoDebug] {msg}");
    }

    private void LogInfo(string msg)
    {
        if (LogManager.Instance == null) return;
        LogManager.Instance.Log(logSource, $"[Video] {msg}");
    }
}
}
