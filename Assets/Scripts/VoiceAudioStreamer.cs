using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace MORLA.XR
{
/// <summary>
/// Opt-in, independent 16 kHz mono PCM uplink. No recognition or robot commands
/// run on Quest. Ubuntu recognizes audio and returns final operator results.
/// </summary>
public class VoiceAudioStreamer : MonoBehaviour
{
    public static string HudLine = "Voice: off";
    public int voicePort = 8001;
    private const int Rate = 16000;
    private const int Samples = 640;
    private AudioClip _capture;
    private int _cursor;
    private float[] _samples;
    private readonly byte[] _pcm = new byte[Samples * 2];
    private bool _paused, _permissionAsked, _denied, _focused = true;
    private double _lastReadTime;
    private double _nextCaptureAttempt;
    private volatile bool _running;
    private Thread _networkThread;
    private string _host;
    private readonly ConcurrentQueue<Frame> _frames = new ConcurrentQueue<Frame>();
    private readonly ConcurrentQueue<string> _feedback = new ConcurrentQueue<string>();
    private volatile string _networkStatus = "waiting";
    private volatile Connection _connection;
    private AudioSource _speaker;
    private AudioClip _okTone, _errorTone;
    private string _lastResult = "";

    private sealed class Frame
    {
        public byte[] Bytes;
        public long CapturedAt;
    }
    private sealed class Connection
    {
        public volatile bool Alive = true;
        public TcpClient Client;
        public NetworkStream Stream;
    }
    [Serializable] private sealed class Feedback
    {
        public string type;
        public string phase;
        public string message;
    }

    private void Update()
    {
        AppManager app = AppManager.Instance;
        bool enabledByUser = app != null && app.VoiceControlEnabled;
        if (!enabledByUser)
        {
            _permissionAsked = _denied = false;
        }
        if (!enabledByUser || !app.isStreaming || _paused || !_focused)
        {
            StopCapture();
            HudLine = enabledByUser ? "Voice: waiting for Start Stream" : "Voice: off";
            return;
        }
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
        {
            StopCapture();
            HudLine = _denied ? "Voice: microphone permission denied" : "Voice: allow microphone permission";
            if (!_permissionAsked)
            {
                _permissionAsked = true;
                var callbacks = new PermissionCallbacks();
                callbacks.PermissionDenied += ignored => _denied = true;
                callbacks.PermissionDeniedAndDontAskAgain += ignored => _denied = true;
                Permission.RequestUserPermission(Permission.Microphone, callbacks);
            }
            return;
        }
#endif
        if (_capture == null && (Time.realtimeSinceStartupAsDouble < _nextCaptureAttempt || !StartCapture(app.ServerIP))) return;
        ReadMicrophone();
        while (_feedback.TryDequeue(out string json))
        {
            try
            {
                Feedback result = JsonUtility.FromJson<Feedback>(json);
                if (result.type != "feedback") continue;
                _lastResult = (result.message ?? "").Replace('\n', ' ');
                if (_lastResult.Length > 180) _lastResult = _lastResult.Substring(0, 180);
                PlayTone(result.phase == "confirmed");
            }
            catch (ArgumentException) { }
        }
        HudLine = "Voice: " + _networkStatus + "  :" + voicePort + "\n" + _lastResult;
    }

    private bool StartCapture(string host)
    {
        try
        {
            if (Microphone.devices.Length == 0) throw new InvalidOperationException("no microphone");
            _capture = Microphone.Start(null, true, 2, Rate);
            if (_capture == null || _capture.frequency != Rate) throw new InvalidOperationException("16 kHz microphone unavailable");
            _samples = new float[Samples * _capture.channels];
            _cursor = 0;
            _lastReadTime = Time.realtimeSinceStartupAsDouble;
            _host = host;
            _running = true;
            _networkThread = new Thread(NetworkLoop) { IsBackground = true };
            _networkThread.Start();
            return true;
        }
        catch (Exception ex)
        {
            StopCapture();
            _nextCaptureAttempt = Time.realtimeSinceStartupAsDouble + 2;
            HudLine = "Voice: " + ex.Message;
            return false;
        }
    }

    private void ReadMicrophone()
    {
        int end = Microphone.GetPosition(null);
        if (end < 0) return;
        int available = (end - _cursor + _capture.samples) % _capture.samples;
        double readAt = Time.realtimeSinceStartupAsDouble;
        if (available > Rate / 5 || readAt - _lastReadTime > 0.2)
        {
            // Never disguise a Unity stall as fresh audio by sending old ring data.
            _cursor = (end - Samples + _capture.samples) % _capture.samples;
            available = Samples;
            ClearFrames();
        }
        _lastReadTime = readAt;
        while (available >= Samples)
        {
            if (!_capture.GetData(_samples, _cursor)) break;
            int channels = _capture.channels;
            for (int i = 0; i < Samples; i++)
            {
                float sum = 0;
                for (int channel = 0; channel < channels; channel++) sum += _samples[i * channels + channel];
                short value = (short)(Mathf.Clamp(sum / channels, -1f, 1f) * 32767f);
                _pcm[i * 2] = (byte)(value & 255);
                _pcm[i * 2 + 1] = (byte)((value >> 8) & 255);
            }
            long capturedUs = QuestPacketEnvelope.MonotonicUs() - available * 1_000_000L / Rate;
            string line = QuestPacketEnvelope.WrapCaptured("VoicePCM:16000:" + Convert.ToBase64String(_pcm), capturedUs) + "\n";
            if (_frames.Count >= 4) ClearFrames();
            _frames.Enqueue(new Frame { Bytes = Encoding.ASCII.GetBytes(line), CapturedAt = Stopwatch.GetTimestamp() });
            _cursor = (_cursor + Samples) % _capture.samples;
            available -= Samples;
        }
    }

    private void NetworkLoop()
    {
        while (_running)
        {
            var connection = new Connection { Client = new TcpClient() };
            _connection = connection;
            try
            {
                _networkStatus = "connecting";
                var connect = connection.Client.ConnectAsync(_host, voicePort);
                if (!connect.Wait(1000)) throw new IOException("voice connect timeout");
                connection.Client.NoDelay = true;
                connection.Client.SendTimeout = 300;
                connection.Stream = connection.Client.GetStream();
                connection.Stream.ReadTimeout = 5000;
                _networkStatus = "microphone uplink connected";
                ClearFrames();
                var reader = new Thread(() => ReadServer(connection)) { IsBackground = true };
                reader.Start();
                while (_running && connection.Alive)
                {
                    if (_frames.TryDequeue(out Frame frame))
                    {
                        double age = (Stopwatch.GetTimestamp() - frame.CapturedAt) / (double)Stopwatch.Frequency;
                        if (age <= 0.2) QuestPacketEnvelope.WriteTcp(connection.Stream, frame.Bytes);
                    }
                    else Thread.Sleep(5);
                }
            }
            catch (Exception) { _networkStatus = "waiting for host voice receiver"; }
            finally
            {
                connection.Alive = false;
                connection.Client.Close();
                ClearFrames();
                _networkStatus = "waiting for host voice receiver";
            }
            for (int i = 0; i < 10 && _running; i++) Thread.Sleep(100);
        }
    }

    private void ReadServer(Connection connection)
    {
        try
        {
            using (var reader = new StreamReader(connection.Stream, Encoding.UTF8, false, 1024, true))
            {
                while (_running && connection.Alive)
                {
                    string line = reader.ReadLine();
                    if (line == null || line.Length > 4096) break;
                    if (QuestPacketEnvelope.TryBuildSyncResponse(line, out string reply))
                        QuestPacketEnvelope.WriteTcp(connection.Stream, Encoding.ASCII.GetBytes(reply + "\n"));
                    else if (line.StartsWith("{") && _feedback.Count < 8) _feedback.Enqueue(line);
                }
            }
        }
        catch (Exception) { }
        finally { connection.Alive = false; }
    }

    private void ClearFrames() { while (_frames.TryDequeue(out Frame ignored)) { } }
    private void StopCapture()
    {
        _running = false;
        Connection connection = _connection;
        if (connection != null) { connection.Alive = false; connection.Client.Close(); }
        if (_networkThread != null) { _networkThread.Join(1300); _networkThread = null; }
        if (_capture != null) { Microphone.End(null); Destroy(_capture); _capture = null; }
        ClearFrames();
        while (_feedback.TryDequeue(out string ignored)) { }
    }

    private void PlayTone(bool success)
    {
        if (_speaker == null) { _speaker = gameObject.AddComponent<AudioSource>(); _speaker.spatialBlend = 0; }
        if (_okTone == null) _okTone = MakeTone(880);
        if (_errorTone == null) _errorTone = MakeTone(330);
        _speaker.PlayOneShot(success ? _okTone : _errorTone, 0.25f);
    }
    private static AudioClip MakeTone(float frequency)
    {
        float[] samples = new float[2400];
        for (int i = 0; i < samples.Length; i++) samples[i] = Mathf.Sin(2 * Mathf.PI * frequency * i / Rate) * 0.15f;
        AudioClip clip = AudioClip.Create("operator feedback", samples.Length, 1, Rate, false);
        clip.SetData(samples, 0);
        return clip;
    }
    private void OnApplicationPause(bool paused) { _paused = paused; if (paused) StopCapture(); }
    private void OnApplicationFocus(bool focused) { _focused = focused; if (!focused) StopCapture(); }
    private void OnDisable() { StopCapture(); }
    private void OnDestroy()
    {
        StopCapture();
        if (_okTone != null) Destroy(_okTone);
        if (_errorTone != null) Destroy(_errorTone);
    }
}
}
