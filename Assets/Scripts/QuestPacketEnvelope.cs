using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

namespace MORLA.XR
{

// Quest uses a monotonic clock. The receiver estimates its clock offset with
// a short challenge/response exchange, so Quest and PC wall clocks need not match.
public static class QuestPacketEnvelope
{
    private static readonly string Session = Guid.NewGuid().ToString("N");
    private static readonly Dictionary<string, long> Sequences = new Dictionary<string, long>();
    private static readonly object Sync = new object();

    public static string Wrap(string message)
    {
        // Telemetry senders normally pass one line. Keep that hot path free of
        // Split/Join arrays so timestamping does not add avoidable per-frame GC.
        if (message.Length == 0) return message;
        if (message.IndexOf('\r') < 0 && message.IndexOf('\n') < 0)
            return WrapLine(message);

        string[] lines = message.Split(new[] {'\r', '\n'}, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < lines.Length; i++) lines[i] = WrapLine(lines[i]);
        return string.Join("\n", lines);
    }

    // Respond to a receiver challenge. The two Quest monotonic samples let the
    // receiver account for the time spent parsing and writing the response.
    public static bool TryBuildSyncResponse(string challenge, out string response)
    {
        response = null;
        if (string.IsNullOrEmpty(challenge)) return false;
        string[] fields = challenge.Trim().Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 3 || fields[0] != "@q3sync") return false;
        long serverSendNs;
        if (!long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out serverSendNs)
            || serverSendNs < 0)
            return false;
        if (fields[1].Length == 0 || fields[1].Length > 64) return false;

        long questReceiveUs = MonotonicUs();
        long questSendUs = MonotonicUs();
        response = "@q3sync_ack " + Session + " " + fields[1] + " "
            + questReceiveUs.ToString(CultureInfo.InvariantCulture) + " "
            + questSendUs.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    // TCP streams are bidirectional. Read only the receiver's sync challenges
    // on a background task; telemetry writes continue on Unity's main thread.
    public static void StartTcpSyncListener(NetworkStream stream)
    {
        if (stream == null) return;
        Task.Run(() =>
        {
            byte[] bytes = new byte[1024];
            StringBuilder pending = new StringBuilder(256);
            while (stream.CanRead)
            {
                int count;
                try { count = stream.Read(bytes, 0, bytes.Length); }
                catch (IOException) { break; }
                catch (ObjectDisposedException) { break; }
                if (count <= 0) break;

                pending.Append(Encoding.UTF8.GetString(bytes, 0, count));
                int newline;
                while ((newline = pending.ToString().IndexOf('\n')) >= 0)
                {
                    string line = pending.ToString(0, newline).TrimEnd('\r');
                    pending.Remove(0, newline + 1);
                    string response;
                    if (!TryBuildSyncResponse(line, out response)) continue;
                    byte[] reply = Encoding.UTF8.GetBytes(response + "\n");
                    try
                    {
                        lock (stream)
                        {
                            if (!stream.CanWrite) break;
                            stream.Write(reply, 0, reply.Length);
                            stream.Flush();
                        }
                    }
                    catch (IOException) { return; }
                    catch (ObjectDisposedException) { return; }
                }
                if (pending.Length > 4096) pending.Length = 0;
            }
        });
    }

    public static void WriteTcp(NetworkStream stream, byte[] bytes)
    {
        if (stream == null || bytes == null) return;
        lock (stream)
        {
            if (stream.CanWrite) stream.Write(bytes, 0, bytes.Length);
        }
    }

    public static void ContinueUdpSyncReceive(UdpClient client, IAsyncResult result, AsyncCallback callback)
    {
        if (client == null) return;
        try
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            byte[] bytes = client.EndReceive(result, ref remote);
            string message = Encoding.UTF8.GetString(bytes);
            foreach (string line in message.Split(new[] {'\r', '\n'}, StringSplitOptions.RemoveEmptyEntries))
            {
                string response;
                if (!TryBuildSyncResponse(line, out response)) continue;
                byte[] reply = Encoding.UTF8.GetBytes(response);
                client.Send(reply, reply.Length, remote);
            }
            client.BeginReceive(callback, null);
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
        catch (InvalidOperationException) { }
        catch (Exception) { }
    }

    private static string WrapLine(string message)
    {
        int delimiter = message.IndexOf(':');
        string stream = delimiter >= 0 ? message.Substring(0, delimiter) : message;
        int payloadDelimiter = stream.IndexOf('|');
        if (payloadDelimiter >= 0) stream = stream.Substring(0, payloadDelimiter);
        stream = stream.Trim();
        long sequence;
        lock (Sync)
        {
            Sequences.TryGetValue(stream, out sequence);
            sequence++;
            Sequences[stream] = sequence;
        }
        return "@q3m " + Session + " " + sequence.ToString(CultureInfo.InvariantCulture)
            + " " + MonotonicUs().ToString(CultureInfo.InvariantCulture) + "|" + message;
    }

    private static long MonotonicUs()
    {
        return (long)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));
    }
}
}
