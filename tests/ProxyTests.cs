// Headless integration tests for the proxy lifecycle and the per-call log.
//
//   scripts\test-proxy.cmd
//
// These exist because of a real bug: v1.0.0 reused one HttpListener instance,
// and a closed HttpListener cannot be restarted, so "restart proxy" silently
// did nothing. Everything here runs without opening a window.
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using Mimas;

namespace MimasTests
{
    static class ProxyTests
    {
        static int _passed;
        static int _failed;

        static int Main(string[] args)
        {
            string upstream = args.Length > 0 ? args[0] : "https://ai.hajimi.yun";

            RestartRebindsThePort();
            RestartSurvivesTenCycles();
            StopReleasesThePort();
            ReusedListenerIsTheRootCause();
            PerCallLogLineIsEmitted(upstream);
            LogRingBufferKeepsNewest();
            ConfigDefaultsAreSane();

            Console.WriteLine();
            Console.WriteLine("passed " + _passed + ", failed " + _failed);
            return _failed == 0 ? 0 : 1;
        }

        // ---------- lifecycle ----------

        static void RestartRebindsThePort()
        {
            LogHub log = new LogHub();
            ProxyServer p = new ProxyServer("https://example.com", 18817, log.Write);

            p.Start();
            bool first = p.IsRunning && IsListening(18817);

            p.Stop();
            bool stopped = !p.IsRunning;

            p.Start();
            bool second = p.IsRunning && IsListening(18817);

            p.Stop();
            log.Dispose();

            Check("first start binds the port", first);
            Check("stop reports not running", stopped);
            Check("start after stop rebinds (v1.0.0 failed here)", second);
        }

        static void RestartSurvivesTenCycles()
        {
            LogHub log = new LogHub();
            ProxyServer p = new ProxyServer("https://example.com", 18818, log.Write);

            int ok = 0;
            for (int i = 0; i < 10; i++)
            {
                p.Start();
                if (p.IsRunning && IsListening(18818)) ok++;
                p.Stop();
                Thread.Sleep(60);
            }

            log.Dispose();
            Check("ten start/stop cycles all bind (" + ok + "/10)", ok == 10);
        }

        static void StopReleasesThePort()
        {
            LogHub log = new LogHub();
            ProxyServer p = new ProxyServer("https://example.com", 18819, log.Write);

            p.Start();
            p.Stop();
            Thread.Sleep(120);

            Check("port is released after stop", !IsListening(18819));
            log.Dispose();
        }

        // Proves the v1.0.0 bug was real, not a guess: a HttpListener that has
        // been Stopped() throws on a second Start(), which is exactly what the
        // old restart path did.
        static void ReusedListenerIsTheRootCause()
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:18821/");
            listener.Start();
            listener.Stop();
            listener.Close();

            bool threw = false;
            try { listener.Start(); }
            catch (ObjectDisposedException) { threw = true; }
            catch (InvalidOperationException) { threw = true; }

            Check("a closed HttpListener cannot be restarted (old bug)", threw);
        }

        // ---------- logging ----------

        static void PerCallLogLineIsEmitted(string upstream)
        {
            LogHub log = new LogHub();
            ProxyServer p = new ProxyServer(upstream, 18820, log.Write);
            p.Start();

            try
            {
                Post("http://127.0.0.1:18820/v1/messages",
                    "{\"model\":\"m\",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");
            }
            catch { }

            Thread.Sleep(400);

            List<LogEntry> tail = log.Tail(50);
            bool hasCall = false;
            for (int i = 0; i < tail.Count; i++)
                if (tail[i].Text.IndexOf("POST /v1/messages", StringComparison.Ordinal) >= 0)
                    hasCall = true;

            p.Stop();
            log.Dispose();

            Check("every call produces a log line with method+path", hasCall);
        }

        static void LogRingBufferKeepsNewest()
        {
            LogHub log = new LogHub();
            for (int i = 0; i < 2500; i++) log.Info("line " + i);

            List<LogEntry> tail = log.Tail(10);
            bool newest = tail.Count == 10 && tail[9].Text == "line 2499";
            bool bounded = log.Count <= 2000;

            log.Dispose();

            Check("ring buffer keeps the newest entries", newest);
            Check("ring buffer is bounded at 2000", bounded);
        }

        static void ConfigDefaultsAreSane()
        {
            AppConfig c = new AppConfig();
            Check("default port is 8817", c.Port == 8817);
            Check("default mode is always", c.Mode == "always");
            Check("default upstream is absolute", c.Upstream.StartsWith("http"));
        }

        // ---------- helpers ----------

        static bool IsListening(int port)
        {
            try
            {
                using (System.Net.Sockets.TcpClient c = new System.Net.Sockets.TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(1500)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        static void Post(string url, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.ContentLength = bytes.Length;
            req.Timeout = 30000;
            req.Proxy = null;

            using (System.IO.Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);

            try
            {
                using (HttpWebResponse r = (HttpWebResponse)req.GetResponse()) { }
            }
            catch (WebException wex)
            {
                if (wex.Response != null) wex.Response.Close();
            }
        }

        static void Check(string name, bool ok)
        {
            if (ok) { _passed++; Console.WriteLine("  ok   " + name); }
            else { _failed++; Console.WriteLine("  FAIL " + name); }
        }
    }
}
