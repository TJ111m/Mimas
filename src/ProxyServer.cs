// Streaming HTTP proxy in front of an Anthropic-Messages-compatible upstream.
//
// Only /v1/messages request bodies are inspected and (when needed) rewritten.
// Everything else is forwarded untouched, and all responses are streamed back
// byte for byte so SSE keeps working.
//
// The HttpListener is created inside Start() and thrown away in Stop().
// A closed HttpListener cannot be restarted, which is exactly why the old
// "restart proxy" button appeared to do nothing: it called Start() on a
// listener that had already been disposed.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;

namespace Mimas
{
    public enum LogKind { Info, Start, Rewrite, Error, Request }

    public sealed class ProxyStats
    {
        public long Requests;
        public long Rewritten;
        public long BlocksHoisted;
        public long BytesIn;
        public long BytesOut;
        public long Errors;
        public long LastLatencyMs;
        public string LastPath = "";
        public int LastStatus;
    }

    public sealed class ProxyServer : IDisposable
    {
        readonly object _gate = new object();
        readonly ProxyStats _stats = new ProxyStats();
        readonly Action<string, LogKind> _log;

        HttpListener _listener;
        string _upstreamOrigin;
        int _port;
        volatile bool _running;

        public int Port { get { return _port; } }
        public string Upstream { get { return _upstreamOrigin; } }
        public ProxyStats Stats { get { return _stats; } }
        public bool IsRunning { get { return _running; } }

        public ProxyServer(string upstreamUrl, int port, Action<string, LogKind> log)
        {
            _log = log ?? delegate { };
            Uri uri = new Uri(upstreamUrl);
            _upstreamOrigin = uri.GetLeftPart(UriPartial.Authority);
            _port = port;
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_running) return;

                // .NET Framework defaults to TLS 1.0 on some builds.
                try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
                catch { }

                // Always a fresh listener: http.sys keeps the port reservation
                // process-wide, so rebinding immediately is safe.
                HttpListener listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + _port + "/");
                listener.Prefixes.Add("http://localhost:" + _port + "/");

                listener.Start();
                _listener = listener;
                _running = true;

                Thread t = new Thread(AcceptLoop);
                t.IsBackground = true;
                t.Name = "mimas-accept";
                t.Start(listener);

                _log("listening on http://127.0.0.1:" + _port + " -> " + _upstreamOrigin, LogKind.Start);
            }
        }

        void AcceptLoop(object state)
        {
            HttpListener listener = (HttpListener)state;

            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { break; }

                ThreadPool.QueueUserWorkItem(delegate(object s)
                {
                    Handle((HttpListenerContext)s);
                }, ctx);
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            Stopwatch sw = Stopwatch.StartNew();
            Interlocked.Increment(ref _stats.Requests);

            string method = ctx.Request.HttpMethod;
            string path = ctx.Request.Url.PathAndQuery;
            int status = 0;
            long inBytes = 0;
            long outBytes = 0;
            int hoisted = 0;
            string model = "";
            bool failed = false;

            try
            {
                // http.sys already rejects foreign Host headers for these prefixes;
                // this is defence in depth so the loopback-only promise survives a
                // misconfigured prefix.
                string host = ctx.Request.Headers["Host"];
                if (!IsLoopbackHost(host))
                {
                    status = 403;
                    WriteJson(ctx, 403, "{\"type\":\"error\",\"error\":{\"type\":\"permission_error\","
                        + "\"message\":\"mimas is loopback-only\"}}");
                    return;
                }

                byte[] body = ReadRequestBody(ctx.Request);
                inBytes = body.Length;

                if (method == "POST" && IsMessagesPath(ctx.Request.Url.AbsolutePath))
                {
                    model = PeekModel(body);

                    byte[] rewritten = TryRewrite(body, out hoisted);
                    if (rewritten != null) body = rewritten;
                }

                Interlocked.Add(ref _stats.BytesIn, body.Length);

                long outCount;
                status = Forward(ctx, body, out outCount);
                outBytes = outCount;
                Interlocked.Add(ref _stats.BytesOut, outCount);
            }
            catch (Exception ex)
            {
                failed = true;
                Interlocked.Increment(ref _stats.Errors);
                _log("proxy error: " + ex.Message, LogKind.Error);
                try
                {
                    WriteJson(ctx, 502, "{\"type\":\"error\",\"error\":{\"type\":\"api_error\","
                        + "\"message\":\"mimas proxy: " + EscapeJson(ex.Message) + "\"}}");
                }
                catch { }
            }
            finally
            {
                sw.Stop();
                _stats.LastLatencyMs = sw.ElapsedMilliseconds;
                _stats.LastPath = path;
                _stats.LastStatus = status;

                if (!failed)
                {
                    // One line per call, always. This is the live feed the UI
                    // tails; rewrites are logged separately with their sizes.
                    StringBuilder line = new StringBuilder(96);
                    line.Append(method).Append(' ').Append(ShortPath(path));
                    line.Append("  ").Append(status);
                    line.Append("  ").Append(sw.ElapsedMilliseconds).Append("ms");
                    line.Append("  in=").Append(FormatBytes(inBytes));
                    line.Append(" out=").Append(FormatBytes(outBytes));
                    if (hoisted > 0) line.Append("  hoisted=").Append(hoisted);
                    if (model.Length > 0) line.Append("  ").Append(model);

                    LogKind kind = hoisted > 0 ? LogKind.Rewrite
                        : (status >= 400 ? LogKind.Error : LogKind.Request);

                    _log(line.ToString(), kind);
                }
            }
        }

        /// <summary>
        /// Reads just the "model" field out of a request body without parsing
        /// the whole document. Used to label log lines.
        /// </summary>
        static string PeekModel(byte[] body)
        {
            if (body.Length < 16 || body.Length > 64 * 1024 * 1024) return "";

            try
            {
                // The model name is near the front of every request body.
                int window = Math.Min(body.Length, 4096);
                string head = Encoding.UTF8.GetString(body, 0, window);

                int at = head.IndexOf("\"model\"", StringComparison.Ordinal);
                if (at < 0) return "";

                int colon = head.IndexOf(':', at);
                if (colon < 0) return "";

                int open = head.IndexOf('"', colon);
                if (open < 0) return "";

                int close = head.IndexOf('"', open + 1);
                if (close <= open) return "";

                string m = head.Substring(open + 1, close - open - 1);
                return m.Length <= 40 ? m : "";
            }
            catch { return ""; }
        }

        static string ShortPath(string pathAndQuery)
        {
            int q = pathAndQuery.IndexOf('?');
            return q >= 0 ? pathAndQuery.Substring(0, q) : pathAndQuery;
        }

        static string FormatBytes(long b)
        {
            if (b < 1024) return b + "B";
            if (b < 1024 * 1024) return (b / 1024.0).ToString("0.0") + "KB";
            return (b / (1024.0 * 1024.0)).ToString("0.0") + "MB";
        }

        static void WriteJson(HttpListenerContext ctx, int status, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        static bool IsLoopbackHost(string hostHeader)
        {
            if (string.IsNullOrEmpty(hostHeader)) return true; // no Host header: local client

            string h = hostHeader.Trim().ToLowerInvariant();

            // strip :port (handle bare IPv6 in brackets)
            if (h.StartsWith("["))
            {
                int close = h.IndexOf(']');
                if (close > 0) h = h.Substring(1, close - 1);
            }
            else
            {
                int colon = h.IndexOf(':');
                if (colon > 0) h = h.Substring(0, colon);
            }

            return h == "127.0.0.1" || h == "localhost" || h == "::1";
        }

        static bool IsMessagesPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase);
        }

        static byte[] ReadRequestBody(HttpListenerRequest req)
        {
            using (Stream src = req.InputStream)
            using (MemoryStream ms = new MemoryStream())
            {
                src.CopyTo(ms);
                byte[] raw = ms.ToArray();
                if (raw.Length == 0) return raw;

                string enc = req.Headers["Content-Encoding"];
                if (string.IsNullOrEmpty(enc)) return raw;

                enc = enc.ToLowerInvariant();
                try
                {
                    if (enc.Contains("gzip")) return Inflate(raw, true);
                    if (enc.Contains("deflate")) return Inflate(raw, false);
                }
                catch { }
                return raw;
            }
        }

        static byte[] Inflate(byte[] data, bool gzip)
        {
            using (MemoryStream input = new MemoryStream(data))
            using (MemoryStream output = new MemoryStream())
            {
                Stream z = gzip
                    ? (Stream)new GZipStream(input, CompressionMode.Decompress)
                    : new DeflateStream(input, CompressionMode.Decompress);
                using (z) { z.CopyTo(output); }
                return output.ToArray();
            }
        }

        byte[] TryRewrite(byte[] body, out int hoisted)
        {
            hoisted = 0;
            if (body.Length == 0) return null;

            string text;
            try { text = new UTF8Encoding(false, true).GetString(body); }
            catch { return null; }

            // Cheap guard: only parse bodies that could contain a nested image.
            if (text.IndexOf("\"tool_result\"", StringComparison.Ordinal) < 0) return null;

            JNode node;
            if (!Json.TryParse(text, out node)) return null;

            RewriteResult r = Rewriter.Rewrite(node);
            if (!r.Changed) return null;

            byte[] outBytes = new UTF8Encoding(false).GetBytes(Json.Serialize(node));
            Interlocked.Increment(ref _stats.Rewritten);
            Interlocked.Add(ref _stats.BlocksHoisted, r.BlocksHoisted);
            hoisted = r.BlocksHoisted;

            _log("hoisted " + r.BlocksHoisted + " block(s): " + body.Length + " -> " + outBytes.Length + " bytes",
                LogKind.Rewrite);

            return outBytes;
        }

        int Forward(HttpListenerContext ctx, byte[] body, out long outBytes)
        {
            outBytes = 0;
            string path = ctx.Request.Url.PathAndQuery;
            string url = _upstreamOrigin + (path.StartsWith("/") ? path : "/" + path);

            HttpWebRequest up = (HttpWebRequest)WebRequest.Create(url);
            up.Method = ctx.Request.HttpMethod;
            up.AllowAutoRedirect = false;
            up.Timeout = 600000;
            up.ReadWriteTimeout = 600000;
            up.KeepAlive = false;
            up.Proxy = null;
            up.ServerCertificateValidationCallback = delegate { return true; };
            up.AutomaticDecompression = DecompressionMethods.None;

            foreach (string header in ctx.Request.Headers.AllKeys)
            {
                if (IsHopByHop(header)) continue;
                try { up.Headers[header] = ctx.Request.Headers[header]; }
                catch { }
            }

            if (body.Length > 0 || ctx.Request.HttpMethod == "POST" || ctx.Request.HttpMethod == "PUT")
            {
                up.ContentType = ctx.Request.ContentType;
                up.ContentLength = body.Length;
                using (Stream s = up.GetRequestStream())
                {
                    s.Write(body, 0, body.Length);
                }
            }

            HttpWebResponse resp;
            try
            {
                resp = (HttpWebResponse)up.GetResponse();
            }
            catch (WebException wex)
            {
                resp = wex.Response as HttpWebResponse;
                if (resp == null)
                {
                    byte[] err = Encoding.UTF8.GetBytes(
                        "{\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"upstream unreachable: "
                        + EscapeJson(wex.Message) + "\"}}");
                    ctx.Response.StatusCode = 502;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = err.Length;
                    ctx.Response.OutputStream.Write(err, 0, err.Length);
                    ctx.Response.Close();
                    return 502;
                }
            }

            int status = (int)resp.StatusCode;

            using (resp)
            {
                ctx.Response.StatusCode = status;

                foreach (string header in resp.Headers.AllKeys)
                {
                    if (IsHopByHop(header)) continue;
                    if (header.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    try { ctx.Response.Headers[header] = resp.Headers[header]; }
                    catch { }
                }

                ctx.Response.SendChunked = true;

                using (Stream upstream = resp.GetResponseStream())
                using (Stream downstream = ctx.Response.OutputStream)
                {
                    byte[] buffer = new byte[8192];
                    while (true)
                    {
                        int n;
                        try { n = upstream.Read(buffer, 0, buffer.Length); }
                        catch (IOException) { break; }
                        if (n <= 0) break;

                        downstream.Write(buffer, 0, n);
                        downstream.Flush();
                        Interlocked.Add(ref _stats.BytesOut, n);
                    }
                }

                try { ctx.Response.Close(); }
                catch { }
            }

            return status;
        }

        static bool IsHopByHop(string header)
        {
            switch (header.ToLowerInvariant())
            {
                case "host":
                case "connection":
                case "keep-alive":
                case "proxy-connection":
                case "transfer-encoding":
                case "upgrade":
                case "content-length":
                    return true;
                default:
                    return false;
            }
        }

        static string EscapeJson(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public void Stop()
        {
            lock (_gate)
            {
                _running = false;

                HttpListener listener = _listener;
                _listener = null;

                if (listener != null)
                {
                    try { listener.Stop(); } catch { }
                    try { listener.Close(); } catch { }
                }
            }
        }

        public void Dispose() { Stop(); }
    }
}
