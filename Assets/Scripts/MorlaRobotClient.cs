using System;
using System.Collections;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace MORLA.XR
{
    /// <summary>Adapter for the existing MORLA Web API; never falls back to legacy motion endpoints.</summary>
    public sealed class MorlaRobotClient : MonoBehaviour
    {
        private bool _connected, _commandBusy, _preparingFollow;
        private int _generation;
        private string _endpoint = "", _pendingId, _pendingCommand, _pendingProfile;
        private float _deadline;
        public bool Connected => _connected;
        private AppManager App => AppManager.Instance;
        private string BaseUrl => "http://" + App.Settings.RobotHost + ":" + App.Settings.robotPort;
        private IEnumerator Start()
        {
            if (App.Settings.robotStatusEnabled && App.Settings.autoConnect) Connect();
            while (true)
            {
                if (_connected && App.Settings.robotStatusEnabled && App.TrackingEnabled)
                {
                    int generation = _generation; string endpoint = BaseUrl;
                    if (_endpoint != endpoint) { Disconnect(); yield return null; continue; }
                    using (var request = UnityWebRequest.Get(endpoint + "/api/v1/state"))
                    {
                        request.timeout = 2; yield return request.SendWebRequest();
                        if (generation != _generation) continue;
                        if (request.result == UnityWebRequest.Result.Success)
                        {
                            try { ApplyState(request.downloadHandler.text); }
                            catch (Exception ex) { Unknown("状态格式无效：" + ex.Message); }
                        }
                        else Unknown("MORLA 状态不可用：" + request.error);
                    }
                    if (!string.IsNullOrEmpty(_pendingId) && Time.unscaledTime > _deadline)
                    { App.Notify("操作确认超时；未自动重试"); _pendingId = null; }
                }
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }
        public void Connect()
        {
            if (!App.Settings.robotStatusEnabled || !AppSettings.ValidHost(App.Settings.RobotHost)) return;
            _generation++; _connected = true; _endpoint = BaseUrl; Unknown("正在读取 MORLA 状态");
        }
        public void Disconnect()
        { _generation++; _connected = false; _pendingId = null; _commandBusy = false; Unknown("状态未接入"); }
        private void Unknown(string detail)
        {
            bool wasSending = App.Session != null && App.Session.Requested;
            App.Status.robotFresh = false; App.Status.robotFollowing = false;
            App.Status.robot = "状态未知"; App.Status.robotDetail = detail;
            App.Status.robotEnabled = App.Status.robotRecording = App.Status.robotOwner = "状态未知";
            if (wasSending) App.Session.Pause("机器人状态过期；发送已暂停");
        }
        private void ApplyState(string json)
        {
            if (!MorlaControlSnapshot.TryParse(json, out var control))
            { Unknown("控制协调器未启动、状态过期或状态契约不完整"); return; }
            var status = App.Status;
            string profile = control.profile;
            if (!string.IsNullOrEmpty(status.robotProfile) && status.robotProfile != profile && App.Session.Requested)
                App.Session.Pause("机器人 Profile 已切换；请重新确认输入配置");
            status.robotProfile = profile;
            status.robotFresh = true; status.robotReceivedUs = QuestPacketEnvelope.MonotonicUs();
            status.robotFollowing = control.following;
            status.robotOperation = control.operation;
            status.robotPhase = control.phase;
            status.robotBusy = status.robotPhase == "accepted" || status.robotPhase == "executing";
            status.robot = status.robotFollowing ? "跟随中" : "未跟随";
            status.robotDetail = control.message;
            status.robotEnabled = control.enabled; status.robotRecording = control.recording; status.robotOwner = control.owner;
            string operationId = control.operationId;
            if (_pendingId != null && operationId == _pendingId && profile == _pendingProfile)
            {
                if (control.Confirms(_pendingId, _pendingCommand, _pendingProfile))
                { App.Notify(_pendingCommand == "pause" ? "MORLA 已确认暂停跟随" : "MORLA 已确认开始跟随"); _pendingId = null; }
                else if (status.robotPhase == "failed" || status.robotPhase == "cancelled")
                { App.Notify("MORLA 操作未完成：" + status.robotDetail); _pendingId = null; }
            }
        }
        public void PauseIfFollowing()
        {
            if (_connected && App.Status.robotFresh && App.Status.robotFollowing && !_commandBusy && _pendingId == null)
                StartCoroutine(Command("pause"));
        }
        public void RequestPause()
        {
            if (!_connected || !App.Status.robotFresh) { App.Notify("请先连接有效的 MORLA 状态接口"); return; }
            if (!_commandBusy && _pendingId == null) StartCoroutine(Command("pause"));
            else App.Notify("正在等待上一项 MORLA 操作确认");
        }
        public void RequestFollow() { if (!_commandBusy && !_preparingFollow && _pendingId == null) StartCoroutine(PrepareFollow()); }
        private IEnumerator PrepareFollow()
        {
            if (!App.Status.robotFresh || App.Status.robotBusy) { App.Notify("MORLA 状态未就绪"); yield break; }
            _preparingFollow = true;
            int generation = _generation;
            App.Workspace.HideSettings(); App.Session.RequestStart();
            float until = Time.unscaledTime + 15;
            while (App.Session.Requested && (!App.isStreaming || !App.HasSentData) && Time.unscaledTime < until) yield return null;
            _preparingFollow = false;
            if (generation != _generation || !App.isStreaming || !App.HasSentData) { App.Notify("未恢复有效位姿发送，跟随请求已取消"); yield break; }
            yield return Command("start");
        }
        private IEnumerator Command(string command)
        {
            if (!_connected || !App.Status.robotFresh || _commandBusy || _pendingId != null) yield break;
            _commandBusy = true; int generation = _generation;
            string profile = App.Status.robotProfile;
            using (var request = new UnityWebRequest(_endpoint + "/api/v1/control/" + command, "POST"))
            {
                request.downloadHandler = new DownloadHandlerBuffer(); request.timeout = 6;
                yield return request.SendWebRequest();
                _commandBusy = false;
                if (generation != _generation) yield break;
                if (request.result != UnityWebRequest.Result.Success)
                { App.Notify("MORLA 请求未确认：" + request.error); yield break; }
                try
                {
                    var result = JObject.Parse(request.downloadHandler.text);
                    string message = result.Value<string>("message") ?? "";
                    if (result.Value<bool?>("ok") != true) { App.Notify("MORLA 拒绝请求：" + message); yield break; }
                    var match = Regex.Match(message, @"operation_id=([a-zA-Z0-9_-]+)");
                    if (!match.Success) { App.Notify("主机已接收，缺少可关联回执；请查看 MORLA 状态"); yield break; }
                    _pendingId = match.Groups[1].Value; _pendingCommand = command; _pendingProfile = profile;
                    _deadline = Time.unscaledTime + 15; App.Notify("主机已接收，等待实际执行确认");
                }
                catch (Exception ex) { App.Notify("控制应答无效：" + ex.Message); }
            }
        }
        private void Update()
        {
            if (App != null && App.Status.robotFresh && QuestPacketEnvelope.MonotonicUs() - App.Status.robotReceivedUs > 2000000)
                Unknown("MORLA 状态已过期");
        }
    }
}
