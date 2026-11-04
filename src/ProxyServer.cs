// Streaming HTTP proxy in front of an Anthropic-Messages-compatible upstream.
//
// Only /v1/messages request bodies are inspected and (when needed) rewritten.
// Everything else is forwarded untouched, and all responses are streamed back
// byte for byte so SSE keeps working.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;

namespace Mimas
{
    public sealed class ProxyStats
    {
        public long Requests;
        public long Rewritten;
        public long BlocksHoisted;
        public long BytesIn;
        public long BytesOut;

        public string Describe()
        {
            return string.Format(
                "requests={0} rewritten={1} blocks={2}",
                Interlocked.Read(ref Requests),
                Interlocked.Read(ref Rewritten),
                Interlocked.Read(ref BlocksHoisted));
        }
    }

    public sealed class ProxyServer : IDisposable
    {
        readonly HttpListener _listener = new HttpListener();
        readonly ProxyStats _stats = new ProxyStats();
        readonly string _upstreamOrigin;
        readonly Action<string> _log;

        volatile bool _running;

        public int Port { get; private set; }
        public ProxyStats Stats { get { return _stats; } }
        public bool IsRunning { get { return _running; } }

        public ProxyServer(string upstreamUrl, int port, Action<string> log)
        {
            _log = log ?? delegate { };

            Uri uri = new Uri(upstreamUrl);
            _upstreamOrigin = uri.GetLeftPart(UriPartial.Authority);
            Port = port;

            _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            _listener.Prefixes.Add("http://localhost:" + port + "/");
        }

        public void Start()
        {
            // .NET Framework defaults to TLS 1.0 on some builds.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch { }

            _listener.Start();
            _running = true;

            Thread t = new Thread(AcceptLoop);
            t.IsBackground = true;
            t.Name = "mimas-accept";
            t.Start();

            _log("listening on http://127.0.0.1:" + Port + " -> " + _upstreamOrigin);
        }

        void AcceptLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; }

                ThreadPool.QueueUserWorkItem(delegate(object state)
                {
                    Handle((HttpListenerContext)state);
                }, ctx);
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            Interlocked.Increment(ref _stats.Requests);

            try
            {
                // http.sys already rejects foreign Host headers for these prefixes;
                // this is defence in depth so the loopback-only promise survives a
                // misconfigured prefix.
                string host = ctx.Request.Headers["Host"];
                if (!IsLoopbackHost(host))
                {
                    byte[] deny = Encoding.UTF8.GetBytes(
                        "{\"type\":\"error\",\"error\":{\"type\":\"permission_error\",\"message\":\"mimas is loopback-only\"}}");
                    ctx.Response.StatusCode = 403;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = deny.Length;
                    ctx.Response.OutputStream.Write(deny, 0, deny.Length);
                    ctx.Response.Close();
                    return;
                }

                byte[] body = ReadRequestBody(ctx.Request);

                if (ctx.Request.HttpMethod == "POST" && IsMessagesPath(ctx.Request.Url.AbsolutePath))
                {
                    byte[] rewritten = TryRewrite(body);
                    if (rewritten != null) body = rewritten;
                }

                Interlocked.Add(ref _stats.BytesIn, body.Length);
                Forward(ctx, body);
            }
            catch (Exception ex)
            {
                _log("proxy error: " + ex.Message);
                try
                {
                    byte[] err = Encoding.UTF8.GetBytes(
                        "{\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"mimas proxy: "
                        + EscapeJson(ex.Message) + "\"}}");
                    ctx.Response.StatusCode = 502;
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = err.Length;
                    ctx.Response.OutputStream.Write(err, 0, err.Length);
                    ctx.Response.Close();
                }
                catch { }
            }
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

        byte[] TryRewrite(byte[] body)
        {
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
            _log("hoisted " + r.BlocksHoisted + " block(s): " + body.Length + " -> " + outBytes.Length + " bytes");
            return outBytes;
        }

        void Forward(HttpListenerContext ctx, byte[] body)
        {
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
                    return;
                }
            }

            using (resp)
            {
                ctx.Response.StatusCode = (int)resp.StatusCode;

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
            _running = false;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }

        public void Dispose() { Stop(); }
    }
}
