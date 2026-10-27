using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace MORLA.XR
{
    public sealed class WorkspaceUIController : MonoBehaviour
    {
        public Canvas Canvas { get; private set; }
        public bool SettingsVisible => _settings != null && _settings.gameObject.activeSelf;
        public bool OnboardingVisible => _onboarding != null && _onboarding.gameObject.activeSelf;
        public bool PointerOverUi { get; set; }
        private RectTransform _settings, _page, _onboarding, _toolbar;
        private TMP_Text _statusText, _noticeText, _sendLabel, _micLabel, _videoLabel;
        private AppSettings _draft;
        private string _draftBaseline;
        private int _selected;
        private float _nextRefresh;
        private bool _placed, _confirmReset, _showVoiceHistory;
        private float _outOfViewSince = -1;
        private WorkspaceIcon _sendIcon, _micIcon;
        private Button _pauseRobot;
        private readonly List<Action> _live = new List<Action>();
        private readonly List<Button> _navigation = new List<Button>();
        private static readonly string[] Pages = { "概览", "连接与传输", "追踪输入", "视频与显示", "语音", "诊断与关于" };
        private AppManager App => AppManager.Instance;

        private void Start()
        {
            if (EventSystem.current == null) new GameObject("MORLA EventSystem", typeof(EventSystem));
            var events = EventSystem.current;
            foreach (var oldModule in events.GetComponents<BaseInputModule>())
                if (!(oldModule is InputSystemUIInputModule)) oldModule.enabled = false;
            var inputModule = events.GetComponent<InputSystemUIInputModule>() ?? events.gameObject.AddComponent<InputSystemUIInputModule>();
            inputModule.enabled = false; inputModule.AssignDefaultActions();
            // Mouse/touch text entry remains native. Tracked pointers are owned exclusively by WorkspacePointer.
            foreach (var map in inputModule.actionsAsset.actionMaps)
                foreach (var action in map.actions)
                    for (int i = 0; i < action.bindings.Count; i++)
                    {
                        string path = action.bindings[i].path ?? "";
                        if (path.IndexOf("XRController", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            path.IndexOf("TrackedDevice", StringComparison.OrdinalIgnoreCase) >= 0) action.ApplyBindingOverride(i, "");
                    }
            inputModule.trackedDevicePosition = null; inputModule.trackedDeviceOrientation = null;
            inputModule.move = null; inputModule.submit = null; inputModule.cancel = null;
            inputModule.enabled = true;
            var canvasObject = new GameObject("WorkspaceCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            Canvas = canvasObject.GetComponent<Canvas>(); Canvas.renderMode = RenderMode.WorldSpace;
            Canvas.worldCamera = Camera.main; Canvas.sortingOrder = 100;
            var rect = (RectTransform)Canvas.transform; rect.sizeDelta = new Vector2(1100, 1100);
            rect.localScale = Vector3.one * 0.0009f;
            _statusText = UiKit.Text(rect, "StatusStrip", "MORLA XR", 25, new Vector2(0, 425), new Vector2(1020, 90));
            _statusText.alignment = TextAlignmentOptions.Center;
            _statusText.raycastTarget = true;
            var statusButton = _statusText.gameObject.AddComponent<Button>(); statusButton.targetGraphic = _statusText;
            statusButton.transition = Selectable.Transition.None; statusButton.onClick.AddListener(() => ShowSettings(0));
            _noticeText = UiKit.Text(rect, "Notice", "", 24, new Vector2(0, -355), new Vector2(1000, 100));
            _noticeText.alignment = TextAlignmentOptions.Center;
            _pauseRobot = UiKit.Button(rect, "PauseFollow", "暂停跟随", new Vector2(340, -365), new Vector2(245, 62), () => App.Robot.RequestPause());
            _pauseRobot.gameObject.SetActive(false);
            _toolbar = UiKit.Panel(rect, "Toolbar", new Vector2(0, -465), new Vector2(850, 92));
            var send = UiKit.Button(_toolbar, "Send", "开始发送", new Vector2(-310, 0), new Vector2(210, 70), () =>
            { if (App.Session.Requested) App.Session.Pause("位姿发送已暂停"); else { HideSettings(); App.Session.RequestStart(); } }, true);
            _sendLabel = send.GetComponentInChildren<TMP_Text>();
            _sendIcon = UiKit.AddToolbarIcon(send, WorkspaceIcon.Kind.Play);
            var video = UiKit.Button(_toolbar, "Video", "视频", new Vector2(-100, 0), new Vector2(170, 70), () =>
            { if (App.Status.video == MediaState.Off || App.Status.video == MediaState.Faulted) App.StartVideo(); else ShowSettings(3); });
            _videoLabel = video.GetComponentInChildren<TMP_Text>();
            UiKit.AddToolbarIcon(video, WorkspaceIcon.Kind.Camera);
            var mic = UiKit.Button(_toolbar, "Microphone", "麦克风", new Vector2(90, 0), new Vector2(180, 70), () => App.SetVoiceEnabled(!App.VoiceSessionEnabled));
            _micLabel = mic.GetComponentInChildren<TMP_Text>();
            _micIcon = UiKit.AddToolbarIcon(mic, WorkspaceIcon.Kind.Microphone);
            var settingsButton = UiKit.Button(_toolbar, "Settings", "设置", new Vector2(295, 0), new Vector2(190, 70), ToggleSettings);
            UiKit.AddToolbarIcon(settingsButton, WorkspaceIcon.Kind.Settings);
            _settings = UiKit.Panel(rect, "SettingsWindow", new Vector2(0, 20), new Vector2(1000, 680));
            var title = UiKit.Text(_settings, "Title", "MORLA XR   设置", 34, new Vector2(-30, 290), new Vector2(890, 70));
            title.raycastTarget = true;
            title.gameObject.AddComponent<WorkspaceWindowDrag>().target = transform;
            UiKit.Button(_settings, "Close", "×", new Vector2(445, 292), new Vector2(66, 62), HideSettings);
            var sidebar = UiKit.Panel(_settings, "Sidebar", new Vector2(-360, -10), new Vector2(240, 530), UiKit.Subtle);
            for (int i = 0; i < Pages.Length; i++)
            {
                int index = i;
                _navigation.Add(UiKit.Button(sidebar, "Page" + i, Pages[i], new Vector2(0, 215 - i * 77), new Vector2(220, 65), () => SelectPage(index)));
            }
            _page = UiKit.ScrollPage(_settings, new Vector2(130, 10), new Vector2(690, 510));
            UiKit.Button(_settings, "Recenter", "回到面前", new Vector2(-350, -295), new Vector2(210, 60), Recenter);
            UiKit.Button(_settings, "Apply", "应用并保存", new Vector2(330, -295), new Vector2(250, 60), () =>
            { EventSystem.current?.SetSelectedGameObject(null); if (App.ApplySettings(_draft)) App.Notify("配置已保存；关闭设置后可恢复发送"); }, true);
            _settings.gameObject.SetActive(false);
            _draft = App.Settings.Copy(); _draftBaseline = JsonUtility.ToJson(_draft);
            if (App.Settings.onboardingVersion < 1) BuildOnboarding(rect);
            if (App.Settings.autoConnect && App.Settings.robotStatusEnabled) App.Robot.Connect();
            StartCoroutine(MarkFirstFrame());
        }
        private System.Collections.IEnumerator MarkFirstFrame()
        { yield return new WaitForEndOfFrame(); App.MarkStartup("workspace_first_rendered_frame"); }
        private void BuildOnboarding(RectTransform parent)
        {
            _onboarding = UiKit.Panel(parent, "ConnectMORLA", Vector2.zero, new Vector2(730, 540));
            UiKit.Text(_onboarding, "Welcome", "连接 MORLA", 36, new Vector2(0, 205), new Vector2(650, 70));
            UiKit.Text(_onboarding, "Description", "选择连接方式，或先进入透视工作区。", 25, new Vector2(0, 140), new Vector2(650, 55));
            TMP_InputField host = null;
            string[] modes = { "UDP", "USB / ADB", "局域网 TCP" };
            for (int i = 0; i < modes.Length; i++)
            {
                int protocol = i;
                UiKit.Button(_onboarding, "Protocol" + i, modes[i], new Vector2(-220 + 220 * i, 76), new Vector2(210, 62), () =>
                {
                    _draft.SelectProtocol(protocol);
                    if (protocol == 0) { _draft.videoEnabled = false; _draft.voiceEnabled = false; }
                    host.SetTextWithoutNotify(_draft.host);
                    App.Notify(protocol == 1 ? "USB 请在主机配置 ADB 转发" : "连接方式：" + modes[protocol]);
                });
            }
            UiKit.Text(_onboarding, "HostLabel", "主机地址", 24, new Vector2(0, 17), new Vector2(650, 38));
            host = UiKit.Input(_onboarding, _draft.host, value => _draft.host = value, false);
            ((RectTransform)host.transform.parent).anchoredPosition = new Vector2(0, -34);
            UiKit.Text(_onboarding, "PortHint", "默认位姿端口：TCP 8000 / UDP 9000；更多参数可在设置中调整。", 23, new Vector2(0, -101), new Vector2(650, 70));
            UiKit.Button(_onboarding, "Connect", "连接", new Vector2(-165, -175), new Vector2(290, 65), () =>
            {
                EventSystem.current?.SetSelectedGameObject(null);
                if (!App.ApplySettings(_draft)) return;
                CompleteOnboarding();
                if (App.Settings.robotStatusEnabled) App.Robot.Connect();
                if (App.Settings.videoEnabled) App.StartVideo();
                App.Notify("配置已保存；点击开始发送，或先观察视频");
            }, true);
            UiKit.Button(_onboarding, "Later", "稍后设置", new Vector2(165, -175), new Vector2(290, 65), CompleteOnboarding);
            UiKit.Button(_onboarding, "More", "更多设置", new Vector2(0, -237), new Vector2(290, 46), () => { CompleteOnboarding(); ShowSettings(1); });
        }
        private void CompleteOnboarding()
        { App.Settings.onboardingVersion = 1; App.SaveSettings(); if (_onboarding != null) _onboarding.gameObject.SetActive(false); }
        public void ToggleSettings() { if (SettingsVisible) HideSettings(); else ShowSettings(_selected); }
        public void ShowSettings(int page)
        {
            App.Session.EnterUiInteraction(); if (_onboarding != null) CompleteOnboarding();
            if (_draft == null || JsonUtility.ToJson(_draft) == _draftBaseline)
            { _draft = App.Settings.Copy(); _draftBaseline = JsonUtility.ToJson(_draft); }
            else App.Notify("保留尚未应用的配置草稿；点击应用后才生效");
            _settings.gameObject.SetActive(true); SelectPage(page);
        }
        public void HideSettings()
        { EventSystem.current?.SetSelectedGameObject(null); if (_settings != null) _settings.gameObject.SetActive(false); SavePose(); }
        public void RefreshSettings() { _draft = App.Settings.Copy(); _draftBaseline = JsonUtility.ToJson(_draft); if (SettingsVisible) SelectPage(_selected); UiKit.ApplyTheme(gameObject); }
        private void SelectPage(int index)
        {
            _selected = index; _live.Clear();
            foreach (Transform child in _page) { child.gameObject.SetActive(false); Destroy(child.gameObject); }
            for (int i = 0; i < _navigation.Count; i++) _navigation[i].GetComponent<Image>().color = i == index ? UiKit.Selected : UiKit.Surface;
            Heading(Pages[index]);
            if (index == 0) Overview(); else if (index == 1) Connection(); else if (index == 2) Tracking();
            else if (index == 3) Display(); else if (index == 4) Voice(); else Diagnostics();
            _page.anchoredPosition = Vector2.zero;
        }
        private void Heading(string value) => UiKit.FlowText(_page, value, 36, 66);
        private void Note(string value) => UiKit.FlowText(_page, value, 24, 90);
        private void Action(string label, Action action, bool primary = false) => UiKit.FlowButton(_page, label, action, primary);
        private void Toggle(string label, Func<bool> get, Action<bool> set)
        {
            var button = UiKit.FlowButton(_page, "", () => { set(!get()); SelectPage(_selected); });
            button.GetComponentInChildren<TMP_Text>().text = label + "    " + (get() ? "开启" : "关闭");
        }
        private void Choices(string label, string[] labels, int selected, Action<int> set)
        {
            UiKit.FlowText(_page, label, 27, 48);
            var row = UiKit.FlowRect(_page, "ChoiceRow", 66);
            float width = 650f / labels.Length;
            for (int i = 0; i < labels.Length; i++)
            {
                int value = i;
                var button = UiKit.Button(row, "Choice" + i, labels[i], new Vector2(-325 + width * (i + 0.5f), 0), new Vector2(width - 8, 62), () => { set(value); SelectPage(_selected); });
                button.GetComponent<Image>().color = selected == i ? UiKit.Selected : UiKit.Subtle;
            }
        }
        private void Field(string label, string value, Action<string> change, bool numeric = false)
        { UiKit.FlowText(_page, label, 26, 46); UiKit.Input(_page, value, change, numeric); }
        private void Overview()
        {
            Note("Quest 3 · 主机 " + App.ServerIP + ":" + App.ServerPort + "\n本地追踪独立运行；设置交互时暂停位姿发送。");
            var live = UiKit.FlowText(_page, "", 26, 295);
            _live.Add(() => live.text = "位姿  " + App.Status.TelemetryLabel + "\n视频  " + MediaLabel(App.Status.video) +
                "\n语音  " + MediaLabel(App.Status.voice) + "\n机器人  " + App.Status.robot + "\n使能：" + App.Status.robotEnabled + " · 采集：" + App.Status.robotRecording +
                "\n控制权：" + App.Status.robotOwner + "\n" + App.Status.robotDetail);
            Action("关闭设置并开始 / 恢复发送", () => { HideSettings(); App.Session.RequestStart(); }, true);
            Action("连接 MORLA 状态", () => { if (App.Settings.robotStatusEnabled) App.Robot.Connect(); else { SelectPage(1); App.Notify("请开启 MORLA 状态接口并应用配置"); } });
            Action("请求暂停跟随", () => App.Robot.RequestPause());
            Action("恢复发送并请求跟随", () => App.Robot.RequestFollow());
            Action("断开所有主机会话", App.DisconnectHost);
        }
        private void Connection()
        {
            Choices("连接方式", new[] { "UDP", "USB / ADB", "局域网 TCP" }, _draft.protocol, value => _draft.SelectProtocol(value));
            Field("位姿主机地址", _draft.host, value => _draft.host = value);
            Field("位姿端口", _draft.posePort.ToString(), value => _draft.posePort = ParsePort(value), true);
            Note(_draft.protocol == 1 ? "USB 使用 ADB TCP 转发。主机需启动接收器，并配置 8000 / 8001 / 8765 转发。" : "位姿、视频与语音独立连接。UDP 广播没有接收确认；媒体需要单播地址。");
            Action("测试位姿端点（仅测试连接）", TestConnection);
            Field("视频主机（留空跟随位姿主机）", _draft.videoHost, value => _draft.videoHost = value);
            Field("视频信令端口", _draft.videoPort.ToString(), value => _draft.videoPort = ParsePort(value), true);
            Field("语音主机（留空跟随位姿主机）", _draft.voiceHost, value => _draft.voiceHost = value);
            Field("语音端口", _draft.voicePort.ToString(), value => _draft.voicePort = ParsePort(value), true);
            Toggle("MORLA 状态接口", () => _draft.robotStatusEnabled, value => _draft.robotStatusEnabled = value);
            Field("MORLA Web 主机（留空跟随位姿主机）", _draft.robotHost, value => _draft.robotHost = value);
            Field("MORLA Web 端口", _draft.robotPort.ToString(), value => _draft.robotPort = ParsePort(value), true);
            Toggle("启动后连接 MORLA 状态", () => _draft.autoConnect, value => _draft.autoConnect = value);
            Action("应用并连接状态 / 视频", () => { if (!App.ApplySettings(_draft)) return; App.Robot.Connect(); if (App.Settings.videoEnabled) App.StartVideo(); });
        }
        private static int ParsePort(string value) => int.TryParse(value, out int result) ? result : -1;
        private async void TestConnection()
        {
            EventSystem.current?.SetSelectedGameObject(null);
            if (!AppSettings.ValidHost(_draft.host, _draft.protocol == 0) || !AppSettings.ValidPort(_draft.posePort)) { App.Notify("地址或端口无效"); return; }
            if (_draft.protocol == 0) { App.Notify("UDP 无连接握手；请在主机确认接收"); return; }
            string host = _draft.host; int port = _draft.posePort;
            App.Notify("正在测试 " + host + ":" + port);
            try
            {
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    var connect = client.ConnectAsync(host, port);
                    if (await System.Threading.Tasks.Task.WhenAny(connect, System.Threading.Tasks.Task.Delay(1500)) != connect)
                    { App.Notify("连接测试超时；检查主机服务与 ADB 转发"); return; }
                    await connect; App.Notify("端点可连接；尚未发送位姿或使能机器人");
                }
            }
            catch (Exception ex) { App.Notify("连接测试失败：" + ex.Message); }
        }
        private void Tracking()
        {
            Choices("追踪配置", new[] { "标准输入", "手与手柄混合", "全身与当前输入" }, (int)_draft.trackingMode, value => _draft.trackingMode = (TrackingMode)value);
            Note(_draft.trackingMode == TrackingMode.Mixed ? "每侧使用当前手或手柄，避免重复发布；此模式不启用身体追踪。" :
                _draft.trackingMode == TrackingMode.FullBody ? "身体追踪需要权限；与当前有效的手 / 手柄配合。全身与混合模式互斥。" : "使用系统当前手部或手柄输入，不承诺左右异构同时追踪。");
            Choices("输出侧", new[] { "双侧", "仅左侧", "仅右侧" }, _draft.outputSide, value => _draft.outputSide = value);
            Toggle("头部位姿", () => _draft.headPose, value => _draft.headPose = value);
            var text = UiKit.FlowText(_page, "", 26, 160);
            _live.Add(() => text.text = SourceLine("left", "左侧") + "\n" + SourceLine("right", "右侧") + "\n" + SourceLine("body", "身体"));
            Note("切换全身配置会改变输出坐标系。应用后等待新追踪有效，再在 MORLA 重新对齐 / 接管。");
        }
        private void Display()
        {
            Toggle("视频接收", () => _draft.videoEnabled, value => _draft.videoEnabled = value);
            Action("立即开始视频观察", App.StartVideo);
            Action("停止视频接收", App.StopVideo);
            Choices("画面显示", new[] { "主画面", "缩略", "隐藏" }, (int)_draft.videoLayout, value => _draft.videoLayout = (VideoLayout)value);
            var video = FindFirstObjectByType<VideoStreamManager>();
            if (video != null)
            {
                var labels = video.TrackLabels;
                for (int i = 0; i < labels.Length; i++) { int index = i; Action("主相机：" + labels[i], () => video.SelectPrimaryTrack(index)); }
                var videoState = UiKit.FlowText(_page, "", 24, 60);
                _live.Add(() => videoState.text = "距最后解码帧：" + (video.LastFrameAgeSeconds < 0 ? "未收到" : video.LastFrameAgeSeconds.ToString("F2") + " s") + " · 接收 " + video.ReceiveFps.ToString("F1") + " FPS");
            }
            Toggle("主相机为必要视角", () => _draft.criticalVideo, value => _draft.criticalVideo = value);
            Toggle("视频固定在世界空间", () => _draft.worldLockedVideo, value => _draft.worldLockedVideo = value);
            Choices("视频尺寸", new[] { "小", "标准", "大" }, _draft.videoScale < 0.9f ? 0 : _draft.videoScale > 1.1f ? 2 : 1, value => _draft.videoScale = value == 0 ? 0.75f : value == 2 ? 1.25f : 1f);
            Choices("显示刷新率", new[] { "72 Hz", "90 Hz" }, _draft.refresh90 ? 1 : 0, value => _draft.refresh90 = value == 1);
            Note("实际显示刷新率：" + OVRPlugin.systemDisplayFrequency.ToString("F0") + " Hz\n视频预设沿用主机 720p30；显示刷新率不等于手部新样本率。");
            Choices("文字大小", new[] { "标准", "较大", "大" }, _draft.textScale > 1.3f ? 2 : _draft.textScale > 1.1f ? 1 : 0, value => _draft.textScale = 1f + value * 0.2f);
            Toggle("增强对比", () => _draft.highContrast, value => _draft.highContrast = value);
            Toggle("减少动画", () => _draft.reduceMotion, value => _draft.reduceMotion = value);
        }
        private void Voice()
        {
            Toggle("麦克风上行偏好", () => _draft.voiceEnabled, value => _draft.voiceEnabled = value);
            Note("音频发送到 MORLA 主机识别。首次需授予麦克风权限；设置交互、失焦和后台会停止采集。");
            Action(App.VoiceSessionEnabled ? "立即关闭麦克风" : "关闭设置并开启麦克风", () => { bool enable = !App.VoiceSessionEnabled; HideSettings(); App.SetVoiceEnabled(enable); });
            var text = UiKit.FlowText(_page, "", 26, 175);
            _live.Add(() => text.text = MediaLabel(App.Status.voice) + "\n" + App.Status.voiceMessage + "\n最近结果：" + App.Status.feedback);
            Action(_showVoiceHistory ? "收起最近反馈" : "展开最近反馈", () => { _showVoiceHistory = !_showVoiceHistory; SelectPage(4); });
            if (_showVoiceHistory)
            {
                var history = UiKit.FlowText(_page, "", 23, 320);
                _live.Add(() => history.text = string.Join("\n", App.Status.voiceHistory));
            }
            Note("可用指令：机器人开始 / 暂停跟随、开始 / 停止采集。语音不自动使能、不执行 HOME 或工作位。主机已接收与执行确认分别显示。");
        }
        private void Diagnostics()
        {
            Toggle("显示诊断面板", () => _draft.showDiagnostics, value => _draft.showDiagnostics = value);
            Toggle("显示骨骼 / 手部关键点", () => _draft.showLandmarks, value => _draft.showLandmarks = value);
            Toggle("发送协议调试元信息", () => _draft.protocolDebug, value => _draft.protocolDebug = value);
            var text = UiKit.FlowText(_page, "", 23, 270);
            _live.Add(() => text.text = "MORLA XR " + Application.version + "\nUnity " + Application.unityVersion + "\n" +
                SourceLine("left", "左侧") + "\n" + SourceLine("right", "右侧") + "\n" + SourceLine("head", "头部") + "\n" + SourceLine("body", "身体") + "\n" + App.TransportDiagnostics());
            Action("导出诊断日志", ExportDiagnostics);
            Action("重置窗口布局", () => { App.Settings.hasWindowPose = false; Recenter(); App.SaveSettings(); });
            Action(_confirmReset ? "再次点击：确认恢复默认设置" : "恢复默认设置…", () =>
            {
                if (!_confirmReset) { _confirmReset = true; SelectPage(5); return; }
                _confirmReset = false; _draft = new AppSettings(); App.ApplySettings(_draft);
            });
        }
        private string SourceLine(string key, string label)
        {
            var source = App.Status.sources[key];
            bool fresh = source.Fresh(QuestPacketEnvelope.MonotonicUs());
            return label + "：" + source.source + (fresh ? " · 有效" : " · 等待") + "  " + source.sampleHz.ToString("F0") + " 次/s  " + source.detail;
        }
        private void ExportDiagnostics()
        {
            string path = Path.Combine(Application.persistentDataPath, "morla-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            try
            {
                File.WriteAllText(path, "MORLA XR " + Application.version + "\nUnity " + Application.unityVersion + "\n" +
                    SourceLine("left", "left") + "\n" + SourceLine("right", "right") + "\n" + SourceLine("body", "body") +
                    "\n" + App.TransportDiagnostics() + "\n" + App.Status.notice + "\n" + App.Status.error);
                App.Notify("已导出：" + path);
            }
            catch (Exception ex) { App.Notify("导出失败：" + ex.Message); }
        }
        private void Update()
        {
            if (Canvas == null || App == null) return;
            if (!_placed && Camera.main != null) { Recenter(); _placed = true; }
            if (_placed && Camera.main != null && !App.Session.Requested && !SettingsVisible && !OnboardingVisible)
            {
                var offset = transform.position - Camera.main.transform.position;
                bool inaccessible = offset.magnitude > 2.5f || Vector3.Dot(Camera.main.transform.forward, offset.normalized) < -0.2f;
                if (inaccessible && WorkspacePointer.InputsReleased() && !PointerOverUi)
                {
                    if (_outOfViewSince < 0) _outOfViewSince = Time.unscaledTime;
                    else if (Time.unscaledTime - _outOfViewSince > 2) { Recenter(); _outOfViewSince = -1; }
                }
                else _outOfViewSince = -1;
            }
            if (Time.unscaledTime < _nextRefresh) return; _nextRefresh = Time.unscaledTime + 0.15f;
            var status = App.Status;
            _pauseRobot.gameObject.SetActive(status.robotFresh && status.robotFollowing && !SettingsVisible);
            _noticeText.rectTransform.sizeDelta = new Vector2(_pauseRobot.gameObject.activeSelf ? 700 : 1000, 100);
            _noticeText.rectTransform.anchoredPosition = new Vector2(_pauseRobot.gameObject.activeSelf ? -140 : 0, -355);
            _sendLabel.text = App.Session.Requested ? "暂停发送" : status.telemetry == TelemetryState.Paused ? "恢复发送" : "开始发送";
            _sendIcon.SetKind(App.Session.Requested ? WorkspaceIcon.Kind.Pause : WorkspaceIcon.Kind.Play);
            _micLabel.text = status.voiceRecording ? "录音中" : status.voice == MediaState.PermissionRequired ? "待授权" : "麦克风";
            _micIcon.color = status.voiceRecording ? UiKit.Blue : UiKit.Ink;
            _videoLabel.text = status.video == MediaState.Faulted || status.video == MediaState.Stale ? "视频异常" : "视频";
            _statusText.text = "MORLA XR    位姿：" + status.TelemetryLabel + "    机器人：" + status.robot +
                "\n左：" + App.Status.sources["left"].source + "    右：" + App.Status.sources["right"].source;
            _noticeText.text = status.error.Length > 0 ? status.error : status.notice;
            if (status.video == MediaState.Faulted && status.error.Length == 0) _noticeText.text = "视频异常：" + status.videoMessage;
            if (status.voiceRecording) _noticeText.text += "\n麦克风正在使用 · " + status.feedback;
            foreach (var update in _live) update();
            UiKit.SetReadable(_statusText, App.Settings.highContrast);
            UiKit.SetReadable(_noticeText, App.Settings.highContrast);
        }
        public void Recenter()
        {
            var cam = Camera.main; if (cam == null) return;
            Vector3 forward = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward; forward.Normalize();
            transform.SetPositionAndRotation(cam.transform.position + forward * 1.2f + Vector3.down * 0.08f, Quaternion.LookRotation(forward, Vector3.up));
            if (!_placed && App.Settings.hasWindowPose)
            {
                // Restore only if the saved pose is still comfortably near the current head.
                if (Vector3.Distance(App.Settings.windowPosition, cam.transform.position) < 2.5f)
                    transform.SetPositionAndRotation(App.Settings.windowPosition, App.Settings.windowRotation);
            }
            if (Canvas != null) Canvas.worldCamera = cam;
        }
        private void SavePose()
        {
            App.Settings.hasWindowPose = true; App.Settings.windowPosition = transform.position;
            App.Settings.windowRotation = transform.rotation; App.SaveSettings();
        }
        public static string MediaLabel(MediaState value)
        {
            switch (value)
            {
                case MediaState.Connecting: return "连接中";
                case MediaState.WaitingFrame: return "等待首帧";
                case MediaState.Playing: return "运行中";
                case MediaState.Stale: return "已中断";
                case MediaState.Paused: return "已暂停";
                case MediaState.Faulted: return "异常";
                case MediaState.PermissionRequired: return "等待授权";
                default: return "关闭";
            }
        }
    }
}
