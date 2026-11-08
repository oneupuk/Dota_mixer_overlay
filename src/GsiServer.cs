using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DotaGlass
{
    /// <summary>
    /// Tiny loopback-only HTTP endpoint for Dota 2 Game State Integration.
    /// Raw TcpListener instead of HttpListener: no http.sys URL ACLs/admin rights, no extra threads.
    /// Dota posts ~1 small JSON per second (in a match: on every clock tick; otherwise: the 1 s heartbeat).
    /// </summary>
    internal sealed class GsiServer
    {
        static readonly byte[] OkResponse = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        readonly int port;
        TcpListener listener;

        /// <summary>Raised on an I/O thread. Argument: map.game_state, "" if the map has no state, null when not in a game.</summary>
        public event Action<string> StateReceived;

        public GsiServer(int port) { this.port = port; }

        public bool Start(out string error)
        {
            error = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start(8);
            }
            catch (SocketException ex)
            {
                error = ex.Message;
                listener = null;
                return false;
            }
            AcceptLoop(listener);
            return true;
        }

        public void Stop()
        {
            var l = listener;
            listener = null;
            if (l != null) try { l.Stop(); } catch { }
        }

        async void AcceptLoop(TcpListener l)
        {
            while (listener == l)
            {
                TcpClient client;
                try { client = await l.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }
                catch (SocketException) { if (listener != l) return; continue; }
                Handle(client);
            }
        }

        async void Handle(TcpClient client)
        {
            string body = null;
            DateTime acceptedAt = DebugLog.On ? DateTime.Now : default(DateTime);
            try
            {
                using (var timeout = new CancellationTokenSource(5000))
                using (timeout.Token.Register(client.Close))
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    byte[] buf = new byte[8192];
                    int len = 0, headerEnd = -1, contentLength = 0;
                    while (true)
                    {
                        if (len == buf.Length)
                        {
                            if (buf.Length >= 1 << 20) return; // 1 MB sanity cap
                            Array.Resize(ref buf, buf.Length * 2);
                        }
                        int n = await stream.ReadAsync(buf, len, buf.Length - len).ConfigureAwait(false);
                        if (n <= 0) return;
                        len += n;
                        if (headerEnd < 0)
                        {
                            headerEnd = FindHeaderEnd(buf, len);
                            if (headerEnd >= 0) contentLength = ParseContentLength(buf, headerEnd);
                        }
                        if (headerEnd >= 0 && len >= headerEnd + contentLength) break;
                    }
                    body = Encoding.UTF8.GetString(buf, headerEnd, contentLength);
                    await stream.WriteAsync(OkResponse, 0, OkResponse.Length).ConfigureAwait(false);
                }
            }
            catch { }
            finally { try { client.Close(); } catch { } }

            if (body != null)
            {
                string state = ExtractGameState(body);
                if (DebugLog.On)
                    DebugLog.Write("gsi  accepted " + acceptedAt.ToString("HH:mm:ss.fff") + "  state=" + (state ?? "(no map)")
                        + "  clock=" + Field(body, "\"clock_time\":") + "  dota_ts=" + Field(body, "\"timestamp\":") + "  bytes=" + body.Length);
                var h = StateReceived;
                if (h != null) h(state);
            }
        }

        static string Field(string json, string key)
        {
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return "-";
            i += key.Length;
            int end = i;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != '\n') end++;
            return json.Substring(i, end - i).Trim();
        }

        static int FindHeaderEnd(byte[] b, int len)
        {
            for (int i = 3; i < len; i++)
                if (b[i] == '\n' && b[i - 1] == '\r' && b[i - 2] == '\n' && b[i - 3] == '\r') return i + 1;
            return -1;
        }

        static int ParseContentLength(byte[] b, int headerEnd)
        {
            string headers = Encoding.ASCII.GetString(b, 0, headerEnd);
            int i = headers.IndexOf("content-length:", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return 0;
            i += 15;
            int end = headers.IndexOf('\r', i);
            int v;
            return int.TryParse(headers.Substring(i, (end < 0 ? headers.Length : end) - i).Trim(), out v) && v > 0 ? v : 0;
        }

        /// <summary>
        /// Allocation-light scan for the top-level "map" object's "game_state" value.
        /// Deliberately ignores "previously"/"added" sections (which also contain "map").
        /// </summary>
        internal static string ExtractGameState(string json)
        {
            int depth = 0;
            bool inMap = false, sawMap = false;
            string pendingKey = null;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"')
                {
                    int start = ++i;
                    while (i < json.Length && json[i] != '"') { if (json[i] == '\\') i++; i++; }
                    if (i >= json.Length) break;
                    int j = i + 1;
                    while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                    bool isKey = j < json.Length && json[j] == ':';
                    if (isKey)
                    {
                        pendingKey = (depth == 1 && i - start == 3) || (inMap && depth == 2 && i - start == 10)
                            ? json.Substring(start, i - start) : null;
                    }
                    else if (inMap && depth == 2 && pendingKey == "game_state")
                    {
                        return json.Substring(start, i - start);
                    }
                    continue;
                }
                switch (c)
                {
                    case '{':
                        depth++;
                        if (depth == 2 && pendingKey == "map") { inMap = true; sawMap = true; }
                        pendingKey = null;
                        break;
                    case '}':
                        if (depth == 2) inMap = false;
                        depth--;
                        break;
                    case '[': depth++; break;
                    case ']': depth--; break;
                }
            }
            return sawMap ? "" : null;
        }
    }
}
