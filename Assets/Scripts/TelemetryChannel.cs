using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace MORLA.XR
{
    /// <summary>One transport per original source. Latest-frame queue; no Unity API on worker threads.</summary>
    public sealed class TelemetryChannel : IDisposable
    {
        private readonly object _sync = new object();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly string _host;
        private readonly int _port, _protocol;
        private readonly Func<bool> _maySend;
        private readonly Action<string> _fault;
        private readonly Thread _worker;
        private volatile bool _running = true;
        private byte[] _latest;
        private long _captured;
        private TcpClient _tcp;
        private UdpClient _udp;
        private long _sent, _lastSent;
        private volatile bool _connected;
        private int _disposed, _writesMilliHz, _rateCount;
        private long _rateStart;
        public float WritesHz => LastSentUs > 0 && QuestPacketEnvelope.MonotonicUs() - LastSentUs < 1000000 ? Volatile.Read(ref _writesMilliHz) / 1000f : 0;
        public string Name { get; }
        public bool Connected => _connected;
        public long Sent => Interlocked.Read(ref _sent);
        public long LastSentUs => Interlocked.Read(ref _lastSent);

        public TelemetryChannel(string host, int port, int protocol, Func<bool> maySend, Action<string> fault, string name = "telemetry")
        {
            Name = name;
            _host = host; _port = port; _protocol = protocol; _maySend = maySend; _fault = fault;
            _worker = new Thread(Run) { IsBackground = true, Name = "MORLA telemetry" };
            _worker.Start();
        }
        public void Submit(string message)
        {
            if (!_running || !_maySend()) return;
            // Wrap at capture/submission, never when draining a delayed queue.
            string wrapped = QuestPacketEnvelope.Wrap(message);
            byte[] bytes = Encoding.UTF8.GetBytes(_protocol == 0 ? wrapped : wrapped + "\n");
            lock (_sync)
            {
                if (!_running) return;
                _latest = bytes; _captured = QuestPacketEnvelope.MonotonicUs();
                try { _wake.Set(); } catch (ObjectDisposedException) { }
            }
        }
        private void Run()
        {
            try
            {
                NetworkStream stream = null;
                IPEndPoint endpoint = null;
                if (_protocol == 0)
                {
                    _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
                    endpoint = new IPEndPoint(IPAddress.Parse(_host), _port);
                    _udp.Client.SendTimeout = 200;
                    _udp.BeginReceive(OnUdp, _udp);
                }
                else
                {
                    var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true, SendTimeout = 200 };
                    _tcp = client;
                    if (!_running) return;
                    var connect = client.ConnectAsync(_host, _port);
                    if (!connect.Wait(1500)) throw new TimeoutException("连接主机超时");
                    if (!_running) return;
                    stream = client.GetStream();
                    QuestPacketEnvelope.StartTcpSyncListener(stream);
                }
                _connected = true;
                while (_running)
                {
                    _wake.WaitOne(20);
                    byte[] bytes; long captured;
                    lock (_sync) { bytes = _latest; captured = _captured; _latest = null; }
                    if (bytes == null || !_running || !_maySend() || QuestPacketEnvelope.MonotonicUs() - captured > 100000) continue;
                    if (_protocol == 0) _udp.Send(bytes, bytes.Length, endpoint);
                    else QuestPacketEnvelope.WriteTcp(stream, bytes);
                    Interlocked.Increment(ref _sent);
                    long now = QuestPacketEnvelope.MonotonicUs();
                    Interlocked.Exchange(ref _lastSent, now); _rateCount++;
                    if (_rateStart == 0) _rateStart = now;
                    if (now - _rateStart >= 1000000)
                    { Volatile.Write(ref _writesMilliHz, (int)(_rateCount * 1000000000L / (now - _rateStart))); _rateStart = now; _rateCount = 0; }
                }
            }
            catch (Exception ex) { if (_running) _fault(Describe(ex.GetBaseException())); }
            finally
            {
                _connected = false;
                try { _tcp?.Close(); _udp?.Close(); } catch (SocketException) { }
                lock (_sync) { _running = false; _latest = null; _wake.Dispose(); }
            }
        }
        /// <summary>
        /// Mono on Android reports raw errno text ("mono-io-layer-error(111)"), which tells the user
        /// nothing. Say what was contacted and what usually causes it.
        /// </summary>
        private string Describe(Exception error)
        {
            string endpoint = _host + ":" + _port;
            string hint = _protocol == 1
                ? "主机侧接收器需先启动，并在主机执行 adb reverse tcp:" + _port + " tcp:" + _port
                : "主机侧接收器需先启动，并放行端口 " + _port;
            if (error is TimeoutException) return "连接超时：" + endpoint + "（" + hint + "）";
            var socket = error as SocketException;
            if (socket != null)
            {
                switch (socket.SocketErrorCode)
                {
                    case SocketError.ConnectionRefused: return "连接被拒绝：" + endpoint + " 没有接收端（" + hint + "）";
                    case SocketError.HostNotFound:
                    case SocketError.NoData: return "找不到主机 " + _host + "：检查地址与局域网";
                    case SocketError.HostUnreachable:
                    case SocketError.NetworkUnreachable: return "网络不可达：" + _host + "（检查 Wi-Fi / USB 转发）";
                    case SocketError.ConnectionReset: return "主机重置了连接：" + endpoint;
                    case SocketError.TimedOut: return "连接超时：" + endpoint + "（" + hint + "）";
                }
                // errno 111 is ECONNREFUSED; Mono sometimes surfaces it only in the text.
                if (error.Message.Contains("111")) return "连接被拒绝：" + endpoint + " 没有接收端（" + hint + "）";
                return error.Message + " → " + endpoint + "（" + hint + "）";
            }
            return error.Message;
        }
        private void OnUdp(IAsyncResult result)
        {
            var client = result.AsyncState as UdpClient;
            QuestPacketEnvelope.ContinueUdpSyncReceive(client, result, next => OnUdpWithClient(next, client));
        }
        private void OnUdpWithClient(IAsyncResult result, UdpClient client)
        {
            QuestPacketEnvelope.ContinueUdpSyncReceive(client, result, next => OnUdpWithClient(next, client));
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_sync)
            {
                _running = false; _latest = null;
                try { _wake.Set(); } catch (ObjectDisposedException) { }
            }
            try { _tcp?.Close(); _udp?.Close(); } catch (SocketException) { }
            // Joining here would stall XR. The worker owns its final cleanup.
        }
    }
}
