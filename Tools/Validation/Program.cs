using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MORLA.XR;
using UnityEngine;

static class Program
{
    static int assertions;
    static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); assertions++; }
    static async Task Wait(Func<bool> condition, string message)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition() && timeout.ElapsedMilliseconds < 3000) await Task.Delay(5);
        Check(condition(), message);
    }
    static void Reset()
    {
        PlayerPrefs.DeleteAll();
        if (Directory.Exists(Application.persistentDataPath)) Directory.Delete(Application.persistentDataPath, true);
    }
    static void SettingsTests()
    {
        Reset();
        var value = SettingsStore.Load(true, true, false);
        Check(value.protocol == 1 && value.host == "127.0.0.1" && value.posePort == 8000, "Default endpoint");
        Check(value.trackingMode == TrackingMode.FullBody && !value.voiceEnabled && !value.showDiagnostics, "Safe defaults");
        Check(value.protocolDebug && value.Validate() == null, "Protocol debug migration independent of HUD");
        Reset(); PlayerPrefs.SetInt("SavedMixed", 1); PlayerPrefs.SetInt("SavedIobt", 1);
        PlayerPrefs.SetString("SavedIP", "10.0.0.42"); PlayerPrefs.SetString("SavedPort", "8123");
        PlayerPrefs.SetInt("SavedVoiceMic", 1); PlayerPrefs.SetInt("Saved90Hz", 0);
        value = SettingsStore.Load(false, true, false);
        Check(value.trackingMode == TrackingMode.Mixed && value.host == "10.0.0.42" && value.posePort == 8123, "Legacy precedence and endpoint");
        Check(value.voiceEnabled && !value.refresh90 && !value.protocolDebug, "Legacy opt-ins");
        value.SelectProtocol(2); Check(value.host == "", "Wireless does not invent router IP");
        value.host = "robot.local"; value.posePort = 9001; value.SelectProtocol(1);
        Check(value.host == "10.0.0.42" && value.posePort == 8123, "Per-protocol wired history");
        value.SelectProtocol(2); Check(value.host == "robot.local" && value.posePort == 9001, "Per-protocol wireless history");
        var copy = value.Copy(); copy.host = "new.local";
        Check(value.host == "robot.local", "Draft isolation");
        Check(!AppSettings.ValidHost("host/path") && !AppSettings.ValidHost("host:8000") && !AppSettings.ValidHost("evil\nheader"), "Reject host injection");
        Check(!AppSettings.ValidHost("255.255.255.255") && AppSettings.ValidHost("255.255.255.255", true), "Media broadcast restriction");
        Check(!AppSettings.ValidPort(0) && !AppSettings.ValidPort(65536) && AppSettings.ValidPort(65535), "Port range");
        value.voiceEnabled = false; value.videoEnabled = false; value.posePort = 65536;
        Check(value.Validate() != null, "Invalid settings rejected");
        value.posePort = 9001; SettingsStore.Save(value); SettingsStore.Save(copy);
        Check(SettingsStore.Load(true, true, true).host == "new.local", "Atomic save and reload");
        File.WriteAllText(SettingsStore.FilePath, "{broken");
        value = SettingsStore.Load(true, true, false);
        Check(SettingsStore.LoadWarning != null && Directory.GetFiles(Application.persistentDataPath, "*.invalid-*").Length == 1, "Corrupt settings backup");
        var source = new SourceStatus(); source.Observe("hands", true);
        Check(source.Fresh(QuestPacketEnvelope.MonotonicUs()), "Fresh source");
        Check(!source.Fresh(QuestPacketEnvelope.MonotonicUs() + 300000), "Stale source");
    }
    static async Task TransportTests()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; int gate = 1; string fault = null;
        using var channel = new TelemetryChannel("127.0.0.1", port, 1, () => Volatile.Read(ref gate) == 1, error => fault = error);
        using var server = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
        using var stream = server.GetStream(); using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
        await Wait(() => channel.Connected, "Actual TCP connection");
        channel.Submit("Left wrist:, 1, 2, 3, 0, 0, 0, 1");
        string line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Check(line.StartsWith("@q3m ") && line.EndsWith("|Left wrist:, 1, 2, 3, 0, 0, 0, 1"), "Wire contract unchanged");
        await stream.WriteAsync(Encoding.ASCII.GetBytes("@q3sync testnonce 123\n"));
        string ack = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Check(ack.StartsWith("@q3sync_ack ") && ack.Contains(" testnonce "), "TCP sync challenge");
        Volatile.Write(ref gate, 0); long sent = channel.Sent;
        for (int i = 0; i < 50; i++) channel.Submit("Head pose:, " + i);
        await Task.Delay(80); Check(channel.Sent == sent && !stream.DataAvailable, "Closed gate never queues samples");
        var timer = Stopwatch.StartNew(); channel.Dispose(); channel.Dispose();
        Check(timer.ElapsedMilliseconds < 100, "Dispose is nonblocking and idempotent");
        listener.Stop(); Check(fault == null, "Intentional stop has no fault callback");
        using var udpServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int udpPort = ((IPEndPoint)udpServer.Client.LocalEndPoint).Port;
        using var udp = new TelemetryChannel("127.0.0.1", udpPort, 0, () => true, error => fault = error);
        await Wait(() => udp.Connected, "UDP initialized");
        udp.Submit("Head pose:, 0, 0, 0, 0, 0, 0, 1");
        var packet = await udpServer.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Check(Encoding.UTF8.GetString(packet.Buffer).Contains("|Head pose:, "), "UDP payload");
        byte[] challenge = Encoding.ASCII.GetBytes("@q3sync udpnonce 123");
        await udpServer.SendAsync(challenge, challenge.Length, packet.RemoteEndPoint);
        var reply = await udpServer.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Check(Encoding.ASCII.GetString(reply.Buffer).Contains(" udpnonce "), "UDP sync challenge");
        var closed = new TcpListener(IPAddress.Loopback, 0); closed.Start(); int closedPort = ((IPEndPoint)closed.LocalEndpoint).Port; closed.Stop();
        string refused = null;
        using var failed = new TelemetryChannel("127.0.0.1", closedPort, 1, () => true, error => refused = error);
        await Wait(() => refused != null, "Connection failure surfaced");
    }
    static void SessionTests()
    {
        var app = AppManager.Instance = new AppManager();
        var tick = typeof(SessionCoordinator).GetMethod("Update", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        void Tick(float time) { Time.unscaledTime = time; tick.Invoke(app.Session, null); }
        Time.unscaledTime = 0; WorkspacePointer.released = false;
        app.Session.RequestStart(); Tick(1);
        Check(app.Session.Requested && !app.Session.CanSend, "Held UI input blocks resume");
        WorkspacePointer.released = true; Tick(1.1f); Tick(1.3f);
        Check(app.Session.CanSend && app.Status.telemetry == TelemetryState.Sending, "Explicit start plus released input opens gate");
        WorkspacePointer.released = false; app.Tracking.ready = false; Tick(1.4f);
        Check(!app.Session.CanSend && !app.Session.Requested, "Tracking failure closes gate even with held robot trigger");
        app.Tracking.ready = true; WorkspacePointer.released = true; Tick(2);
        Check(!app.Session.CanSend, "Tracking recovery never auto resumes");
        app.Session.RequestStart(); Tick(2.1f); Tick(2.3f);
        app.Session.EnterUiInteraction();
        Check(!app.Session.CanSend && !app.Session.Requested && app.closed > 0, "UI entry closes transport");
        Tick(3); Check(!app.Session.CanSend, "Closing UI does not resume");
        app.Session.SetVoiceRequested(true); Check(app.Session.VoiceAllowed, "Independent voice session");
        app.Session.EnterUiInteraction(); Check(!app.Session.VoiceAllowed, "UI voice isolation");
        app.Settings.videoEnabled = true; app.Settings.criticalVideo = true; app.Status.video = MediaState.Stale;
        app.Session.RequestStart(); Tick(3.2f); Tick(3.4f);
        Check(!app.Session.CanSend, "Critical stale camera blocks motion telemetry");
        Tick(20); Check(app.Status.telemetry == TelemetryState.Faulted && !app.Session.Requested, "Preparation timeout does not arm");
        app.Settings.videoEnabled = false; app.Session.RequestStart(); Tick(20.2f); Tick(20.4f);
        app.HasSentData = false; Tick(21.2f);
        Check(!app.Session.CanSend && app.Status.telemetry == TelemetryState.Faulted, "Required transport watchdog");
        app.Session.SetVoiceRequested(true); app.TrackingEnabled = false;
        Check(!app.Session.VoiceAllowed, "Foreground required for voice");
        app.Session.RequestStart(); Tick(22);
        Check(!app.Session.CanSend && !app.Session.Requested, "Background cannot send");
    }
    static void RobotContractTests()
    {
        const string paused = "{\"ok\":true,\"data\":{\"control\":{\"profile_id\":\"nero\",\"stale\":false,\"node_count\":1,\"teleop_armed\":false,\"operation_id\":\"op1\",\"operation\":\"pause\",\"phase\":\"completed\",\"result\":true}}}";
        Check(MorlaControlSnapshot.TryParse(paused, out var state), "MORLA existing state envelope");
        Check(state.Confirms("op1", "pause", "nero"), "Matching completed pause confirms");
        Check(!state.Confirms("old", "pause", "nero") && !state.Confirms("op1", "pause", "other"), "No cross-request/profile confirmation");
        Check(!state.Confirms("op1", "start", "nero"), "Pause cannot confirm start");
        Check(MorlaControlSnapshot.TryParse(paused.Replace("completed", "accepted"), out state) && !state.Confirms("op1", "pause", "nero"), "Accepted is not completed");
        Check(!MorlaControlSnapshot.TryParse(paused.Replace("\"stale\":false", "\"stale\":true"), out _), "Host-stale control refused");
        Check(!MorlaControlSnapshot.TryParse(paused.Replace("\"node_count\":1", "\"node_count\":2"), out _), "Ambiguous duplicate coordinator refused");
        Check(!MorlaControlSnapshot.TryParse("{\"ok\":true,\"data\":{\"control\":{\"profile_id\":\"nero\",\"stale\":false}}}", out _), "Missing actual writer state is unknown");
        Check(MorlaControlSnapshot.TryParse(paused.Replace("\"pause\"", "\"start\"").Replace("\"teleop_armed\":false", "\"teleop_armed\":true"), out state) && state.Confirms("op1", "start", "nero"), "Matching actual start confirms");
        Check(!MorlaControlSnapshot.TryParse("{\"ok\":true,\"data\":{\"control\":null}}", out _), "Offline coordinator is unknown");
        Check(!MorlaControlSnapshot.TryParse("{broken", out _), "Malformed JSON fails closed");
        Check(!MorlaControlSnapshot.TryParse(paused.Replace("\"teleop_armed\":false", "\"teleop_armed\":\"false\""), out _), "Do not coerce string writer state");
    }
    static void SyntaxTests(string root)
    {
        var trees = new List<SyntaxTree>();
        foreach (string file in Directory.GetFiles(Path.Combine(root, "Assets"), "*.cs", SearchOption.AllDirectories))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), new CSharpParseOptions(LanguageVersion.CSharp9), file);
            trees.Add(tree);
            var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            Check(errors.Length == 0, string.Join("\n", errors.Select(e => e.ToString())));
            var android = CSharpSyntaxTree.ParseText(File.ReadAllText(file), new CSharpParseOptions(LanguageVersion.CSharp9, preprocessorSymbols: new[] { "UNITY_ANDROID" }), file);
            Check(!android.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), "Android conditional syntax: " + file);
        }
        // Check language control-flow errors even when Unity assemblies are unavailable.
        // Missing Unity/Meta API diagnostics are deliberately not treated as a Unity compilation pass.
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("Morla.ControlFlow", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var fatal = new HashSet<string> { "CS1626", "CS1631", "CS1622", "CS0128", "CS0136", "CS0841", "CS0163", "CS0152", "CS0111", "CS0102" };
        var issues = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && fatal.Contains(d.Id)).ToArray();
        Check(issues.Length == 0, string.Join("\n", issues.Select(d => d.ToString())));
    }
    static async Task Main(string[] args)
    {
        try
        {
            string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("../..");
            SyntaxTests(root); SettingsTests(); SessionTests(); RobotContractTests(); await TransportTests();
            Console.WriteLine($"PASS: {assertions} assertions; C# 9 syntax, settings migration, real TCP/UDP, gate and sync checks.");
            Console.WriteLine("Unity/Meta API compilation, Android build and Quest device checks require Unity 6000.0.65f1.");
        }
        finally { Reset(); }
    }
}
