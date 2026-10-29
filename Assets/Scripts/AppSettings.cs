using System;
using System.IO;
using System.Net;
using UnityEngine;

namespace MORLA.XR
{
    public enum TrackingMode { Standard, Mixed, FullBody }
    public enum TelemetryState { Idle, Connecting, Sending, Paused, Faulted }
    public enum MediaState { Off, Connecting, WaitingFrame, Playing, Stale, Paused, Faulted, PermissionRequired }
    public enum VideoLayout { Main, Thumbnail, Hidden }

    [Serializable]
    public sealed class AppSettings
    {
        public int schemaVersion = 1;
        public int onboardingVersion;
        public int protocol = 1;
        public string host = "127.0.0.1";
        public int posePort = 8000;
        public string wiredHost = "127.0.0.1", wirelessHost = "", udpHost = "255.255.255.255";
        public int wiredPort = 8000, wirelessPort = 8000, udpPort = 9000;
        public TrackingMode trackingMode = TrackingMode.FullBody;
        public int outputSide;
        public bool headPose = true, refresh90 = true;
        public bool showLandmarks, showDiagnostics, protocolDebug = true;
        public bool videoEnabled = true, voiceEnabled;
        public string videoHost = "", voiceHost = "";
        public int videoPort = 8765, voicePort = 8001;
        public bool criticalVideo = true, highContrast, reduceMotion, worldLockedVideo;
        public float textScale = 1f, videoScale = 1f;
        public VideoLayout videoLayout;
        public bool robotStatusEnabled;
        public string robotHost = "";
        public int robotPort = 8080;
        public bool autoConnect;
        public bool hasWindowPose;
        // Quest-shell default: constant angular size, so pushing the panel away grows it in metres
        // and it stays just as legible. false = fixed physical size, where farther looks smaller.
        public bool angularWindowScale = true;
        public Vector3 windowPosition;
        public Quaternion windowRotation = Quaternion.identity;

        public AppSettings Copy() => JsonUtility.FromJson<AppSettings>(JsonUtility.ToJson(this));
        public string VideoHost => string.IsNullOrWhiteSpace(videoHost) ? host : videoHost.Trim();
        public string VoiceHost => string.IsNullOrWhiteSpace(voiceHost) ? host : voiceHost.Trim();
        public string RobotHost => string.IsNullOrWhiteSpace(robotHost) ? host : robotHost.Trim();
        public static bool ValidPort(int value) => value > 0 && value <= 65535;
        public static bool ValidHost(string value, bool broadcastAllowed = false)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim();
            if (value.IndexOfAny(new[] { '/', '\\', ':', ' ', '\n', '\r' }) >= 0) return false;
            if (!broadcastAllowed && value == "255.255.255.255") return false;
            return IPAddress.TryParse(value, out _) || Uri.CheckHostName(value) == UriHostNameType.Dns;
        }
        public string Validate()
        {
            host = (host ?? "").Trim();
            if (protocol < 0 || protocol > 2) return "连接方式无效";
            if (!ValidHost(host, protocol == 0)) return "请输入有效主机地址";
            if (!ValidPort(posePort)) return "位姿端口应为 1–65535";
            if (protocol == 0 && !IPAddress.TryParse(host, out _)) return "UDP 需要 IPv4 地址";
            if (videoEnabled && (!ValidHost(VideoHost) || !ValidPort(videoPort))) return "视频需要单播主机地址和有效端口";
            if (voiceEnabled && (!ValidHost(VoiceHost) || !ValidPort(voicePort))) return "语音需要单播主机地址和有效端口";
            if (robotStatusEnabled && (!ValidHost(RobotHost) || !ValidPort(robotPort))) return "MORLA 状态地址或端口无效";
            if (!Enum.IsDefined(typeof(TrackingMode), trackingMode) || outputSide < 0 || outputSide > 2) return "追踪配置无效";
            if (!Enum.IsDefined(typeof(VideoLayout), videoLayout)) return "视频布局无效";
            return null;
        }
        public void RememberEndpoint()
        {
            if (protocol == 0) { udpHost = host; udpPort = posePort; }
            else if (protocol == 1) { wiredHost = host; wiredPort = posePort; }
            else { wirelessHost = host; wirelessPort = posePort; }
        }
        public void SelectProtocol(int value)
        {
            RememberEndpoint(); protocol = value;
            host = value == 0 ? udpHost : value == 1 ? wiredHost : wirelessHost;
            posePort = value == 0 ? udpPort : value == 1 ? wiredPort : wirelessPort;
        }
    }

    public static class SettingsStore
    {
        public static string FilePath => Path.Combine(Application.persistentDataPath, "morla-settings.json");
        public static string LoadWarning { get; private set; }
        public static AppSettings Load(bool legacyDebug, bool legacyHead, bool legacyVideo)
        {
            LoadWarning = null;
            if (File.Exists(FilePath))
            {
                try
                {
                    var value = JsonUtility.FromJson<AppSettings>(File.ReadAllText(FilePath));
                    if (value == null || value.schemaVersion != 1) throw new InvalidDataException("Unsupported settings version");
                    if (value.protocol < 0 || value.protocol > 2 || value.outputSide < 0 || value.outputSide > 2 ||
                        !Enum.IsDefined(typeof(TrackingMode), value.trackingMode) || !AppSettings.ValidPort(value.posePort))
                        throw new InvalidDataException("Invalid saved settings");
                    value.textScale = Mathf.Clamp(value.textScale, 1f, 1.4f);
                    value.videoScale = Mathf.Clamp(value.videoScale, 0.5f, 1.5f);
                    return value;
                }
                catch (Exception ex)
                {
                    string backup = FilePath + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                    try { File.Copy(FilePath, backup, true); } catch (IOException) { }
                    LoadWarning = "配置已备份并恢复默认：" + ex.Message;
                }
            }
            var settings = new AppSettings
            {
                protocol = PlayerPrefs.GetInt("SavedProtocol", 1),
                host = PlayerPrefs.GetString("SavedIP", "127.0.0.1"),
                outputSide = PlayerPrefs.GetInt("SavedHandMode", 0),
                trackingMode = PlayerPrefs.GetInt("SavedMixed", 0) == 1 ? TrackingMode.Mixed :
                    PlayerPrefs.GetInt("SavedIobt", 1) == 1 ? TrackingMode.FullBody : TrackingMode.Standard,
                refresh90 = PlayerPrefs.GetInt("Saved90Hz", 1) == 1,
                voiceEnabled = PlayerPrefs.GetInt("SavedVoiceMic", 0) == 1,
                protocolDebug = legacyDebug, headPose = legacyHead, videoEnabled = legacyVideo
            };
            settings.protocol = Mathf.Clamp(settings.protocol, 0, 2);
            settings.outputSide = Mathf.Clamp(settings.outputSide, 0, 2);
            int.TryParse(PlayerPrefs.GetString("SavedPort", settings.protocol == 0 ? "9000" : "8000"), out settings.posePort);
            if (!AppSettings.ValidPort(settings.posePort)) settings.posePort = settings.protocol == 0 ? 9000 : 8000;
            if (settings.protocol == 0 && settings.host == "127.0.0.1") settings.host = "255.255.255.255";
            settings.RememberEndpoint();
            Save(settings);
            return settings;
        }
        public static void Save(AppSettings settings)
        {
            Directory.CreateDirectory(Application.persistentDataPath);
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(settings, true));
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
        }
    }
}
