// Only configuration serialization/math are shimmed. Network tests use actual .NET sockets.
// This harness does not compile or simulate the Unity/Meta runtime.
namespace UnityEngine
{
    public sealed class DefaultExecutionOrder : Attribute { public DefaultExecutionOrder(int order) { } }
    public class MonoBehaviour { protected static T FindFirstObjectByType<T>() where T : class => null; }
    public static class Time { public static float unscaledTime; }
    [Serializable] public struct Vector3 { public float x, y, z; }
    [Serializable] public struct Quaternion { public float x, y, z, w; public static Quaternion identity => new Quaternion { w = 1 }; }
    public static class Application { public static string persistentDataPath = Path.Combine(Path.GetTempPath(), "morla-validation-" + Guid.NewGuid().ToString("N")); }
    public static class Mathf
    {
        public static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
        public static float Clamp(float value, float min, float max) => Math.Clamp(value, min, max);
    }
    public static class PlayerPrefs
    {
        private static readonly Dictionary<string, string> Values = new();
        public static int GetInt(string key, int fallback) => Values.TryGetValue(key, out var value) ? int.Parse(value) : fallback;
        public static string GetString(string key, string fallback) => Values.GetValueOrDefault(key, fallback);
        public static void SetInt(string key, int value) => Values[key] = value.ToString();
        public static void SetString(string key, string value) => Values[key] = value;
        public static void DeleteAll() => Values.Clear();
    }
    public static class JsonUtility
    {
        private static readonly System.Text.Json.JsonSerializerOptions Options = new() { IncludeFields = true, IgnoreReadOnlyProperties = true };
        public static string ToJson(object value, bool prettyPrint = false) => System.Text.Json.JsonSerializer.Serialize(value, Options);
        public static T FromJson<T>(string value) => System.Text.Json.JsonSerializer.Deserialize<T>(value, Options);
    }
}

namespace MORLA.XR
{
    public sealed class VoiceAudioStreamer { public void StopForSession() { } }
    public static class WorkspacePointer { public static bool released = true; public static bool InputsReleased() => released; }
    public sealed class WorkspaceStub { public bool SettingsVisible; public void ShowSettings(int index) => SettingsVisible = true; }
    public sealed class TrackingStub
    {
        public bool ready = true;
        public bool Ready(out string reason, long minimumSampleUs = 0) { reason = "Tracking missing"; return ready; }
    }
    public sealed class RobotStub { public int pauseRequests; public void PauseIfFollowing() => pauseRequests++; }
    public sealed class AppManager
    {
        public static AppManager Instance;
        public AppSettings Settings = new() { videoEnabled = false };
        public SessionStatus Status = new();
        public SessionCoordinator Session = new();
        public WorkspaceStub Workspace = new();
        public TrackingStub Tracking = new();
        public RobotStub Robot = new();
        public bool TrackingEnabled = true, HasSentData = true;
        public int SelectedProtocol => Settings.protocol;
        public int closed;
        public void MarkStartup(string name) { }
        public void Notify(string message) => Status.notice = message;
        public void CloseTelemetry() => closed++;
        public void StartVideo() => Status.video = MediaState.Connecting;
    }
}
