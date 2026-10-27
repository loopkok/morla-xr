using System;
using System.Collections.Generic;

namespace MORLA.XR
{
    public sealed class SourceStatus
    {
        public string source = "未检测", detail = "";
        public bool valid;
        public long sampledUs;
        public long samples;
        public float sampleHz;
        private long _windowStart, _windowCount;
        public void Observe(string name, bool isValid, string message = "")
        {
            source = name; valid = isValid; detail = message;
            if (!isValid) return;
            long now = QuestPacketEnvelope.MonotonicUs();
            sampledUs = now; samples++; _windowCount++;
            if (_windowStart == 0) _windowStart = now;
            if (now - _windowStart >= 1000000)
            {
                sampleHz = _windowCount * 1000000f / (now - _windowStart);
                _windowStart = now; _windowCount = 0;
            }
        }
        public bool Fresh(long now, long maximumAgeUs = 250000) => valid && sampledUs > 0 && now - sampledUs <= maximumAgeUs;
    }
    public sealed class SessionStatus
    {
        public TelemetryState telemetry;
        public MediaState video, voice;
        public bool voiceRecording;
        public readonly List<string> voiceHistory = new List<string>();
        public string notice = "", error = "", videoMessage = "", voiceMessage = "", feedback = "";
        public string robot = "状态未接入", robotDetail = "";
        public bool robotFresh, robotFollowing, robotBusy;
        public string robotProfile = "", robotOperation = "", robotPhase = "";
        public string robotEnabled = "未提供", robotRecording = "未提供", robotOwner = "未提供";
        public long robotReceivedUs;
        public readonly Dictionary<string, SourceStatus> sources = new Dictionary<string, SourceStatus>
        {
            ["left"] = new SourceStatus(), ["right"] = new SourceStatus(),
            ["head"] = new SourceStatus(), ["body"] = new SourceStatus()
        };
        public string TelemetryLabel => telemetry == TelemetryState.Sending ? "发送中" :
            telemetry == TelemetryState.Connecting ? "等待连接 / 追踪" : telemetry == TelemetryState.Paused ? "已暂停" :
            telemetry == TelemetryState.Faulted ? "连接异常" : "未发送";
    }
}
