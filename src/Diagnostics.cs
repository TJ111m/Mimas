// Connectivity checks used by the UI.
//
// These are deliberately honest: they prove the network path works (TCP +
// TLS + HTTP status), not that an API key is valid. Mimas never sees keys, so
// it has no way to test authentication — saying so beats pretending.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace Mimas
{
    public static class Diagnostics
    {
        public sealed class Probe
        {
            public bool Ok;
            public int LatencyMs;
            public string Message = "";
        }

        /// <summary>
        /// Connects to the upstream host and, for https, completes a TLS
        /// handshake. Reports latency. Certificate validity is not asserted
        /// because the proxy itself does not enforce it.
        /// </summary>
        public static Probe TestUpstream(string upstreamUrl)
        {
            Probe p = new Probe();
            Uri uri;
            if (!Uri.TryCreate(upstreamUrl, UriKind.Absolute, out uri))
            {
                p.Message = "地址格式不正确";
                return p;
            }

            string host = uri.Host;
            int port = uri.Port;
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                using (TcpClient tcp = new TcpClient())
                {
                    IAsyncResult ar = tcp.BeginConnect(host, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(8000))
                    {
                        p.Message = "连接超时（8 秒）";
                        return p;
                    }
                    tcp.EndConnect(ar);

                    if (uri.Scheme == Uri.UriSchemeHttps)
                    {
                        using (SslStream ssl = new SslStream(tcp.GetStream(), false, delegate { return true; }))
                        {
                            ssl.AuthenticateAsClient(host);
                            p.Ok = true;
                            p.Message = "TLS 握手成功";
                        }
                    }
                    else
                    {
                        p.Ok = true;
                        p.Message = "TCP 连接成功";
                    }
                }
            }
            catch (Exception ex)
            {
                p.Ok = false;
                p.Message = ex.Message;
            }
            finally
            {
                sw.Stop();
                p.LatencyMs = (int)sw.ElapsedMilliseconds;
            }

            return p;
        }

        /// <summary>Checks that something is listening on the local proxy port.</summary>
        public static Probe TestLocalPort(int port)
        {
            Probe p = new Probe();
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                using (TcpClient tcp = new TcpClient())
                {
                    IAsyncResult ar = tcp.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(3000))
                    {
                        p.Message = "端口无响应";
                        return p;
                    }
                    tcp.EndConnect(ar);
                    p.Ok = true;
                    p.Message = "127.0.0.1:" + port + " 正在监听";
                }
            }
            catch (Exception ex)
            {
                p.Message = "127.0.0.1:" + port + " 未监听（" + ex.Message + "）";
            }
            finally
            {
                sw.Stop();
                p.LatencyMs = (int)sw.ElapsedMilliseconds;
            }
            return p;
        }

        /// <summary>
        /// Sends one real request through the local proxy so the caller can see
        /// whether the whole chain (agent → Mimas → relay) returns an HTTP
        /// response. Any status code counts as reachable; only a transport
        /// failure is reported as broken.
        /// </summary>
        public static Probe TestChain(int port, string upstreamUrl)
        {
            Probe p = new Probe();
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                string body = "{\"model\":\"claude-sonnet-5-5\",\"max_tokens\":1,"
                    + "\"messages\":[{\"role\":\"user\",\"content\":\"ping\"}]}";
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);

                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/v1/messages");
                req.Method = "POST";
                req.ContentType = "application/json";
                req.ContentLength = bytes.Length;
                req.Timeout = 20000;
                req.ReadWriteTimeout = 20000;
                req.Proxy = null;
                req.UserAgent = "Mimas/self-test";

                using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);

                int status;
                try
                {
                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                        status = (int)resp.StatusCode;
                }
                catch (WebException wex)
                {
                    HttpWebResponse r = wex.Response as HttpWebResponse;
                    if (r == null)
                    {
                        p.Message = "链路不通：" + wex.Message;
                        return p;
                    }
                    using (r) status = (int)r.StatusCode;
                }

                p.Ok = true;
                p.Message = "链路通畅，上游返回 HTTP " + status
                    + (status == 401 || status == 403 ? "（缺少或无效的 API Key，属正常）" : "");
            }
            catch (Exception ex)
            {
                p.Message = ex.Message;
            }
            finally
            {
                sw.Stop();
                p.LatencyMs = (int)sw.ElapsedMilliseconds;
            }
            return p;
        }
    }
}
