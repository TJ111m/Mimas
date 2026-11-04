// The single owner of runtime state: config, proxy, watcher, log.
//
// Both the tray icon and the window talk to this object, so "stop the proxy"
// from the window and "restart proxy" from the tray can never disagree.
using System;
using System.Collections.Generic;
using System.IO;

namespace Mimas
{
    public sealed class AppHost : IDisposable
    {
        readonly object _gate = new object();
        readonly LogHub _log;

        AppConfig _cfg;
        ProxyServer _proxy;
        ProcessWatcher _watcher;

        public event EventHandler StateChanged;

        public AppHost(LogHub log)
        {
            _log = log ?? new LogHub();

            // AppConfig.Load() writes a default file when none exists, so the
            // "was this a first run?" question has to be answered before it.
            bool hadConfigFile = File.Exists(AppConfig.ConfigPath);

            _cfg = AppConfig.Load();

            // A config without a "setupCompleted" key is an install from before
            // the wizard existed. Treat it as already set up rather than
            // interrupting an existing user with a first-run dialog.
            if (hadConfigFile && !_cfg.HasSetupFlag)
            {
                _cfg.SetupCompleted = true;
                try { _cfg.Save(); } catch { }
            }

            IsFirstRun = !hadConfigFile;

            _proxy = new ProxyServer(_cfg.Upstream, _cfg.Port, _log.Write);
            _watcher = new ProcessWatcher(_cfg.WatchProcesses, 3000, OnWatchChanged);
        }

        public LogHub Log { get { return _log; } }
        public AppConfig Config { get { return _cfg; } }
        public ProxyServer Proxy { get { return _proxy; } }
        public bool IsRunning { get { return _proxy != null && _proxy.IsRunning; } }
        public bool IsFirstRun { get; private set; }

        public void MarkFirstRunDone()
        {
            IsFirstRun = false;
            _cfg.SetupCompleted = true;
            try { _cfg.Save(); } catch { }
        }

        public void Start()
        {
            _log.Info("Mimas " + Program.AppVersion + " starting");
            _log.Info("upstream=" + _cfg.Upstream + " port=" + _cfg.Port + " mode=" + _cfg.Mode);

            if (_cfg.Mode == "watch") _watcher.Start();
            else StartProxy();

            if (_cfg.StartWithWindows) StartupEntry.Set(true, _log.Info);
        }

        // ---------- proxy lifecycle ----------

        public void StartProxy()
        {
            lock (_gate)
            {
                if (_proxy != null && _proxy.IsRunning) return;
                try
                {
                    _proxy.Start();
                }
                catch (Exception ex)
                {
                    _log.Error("proxy failed to start: " + ex.Message);
                }
            }
            Raise();
        }

        public void StopProxy()
        {
            lock (_gate)
            {
                if (_proxy == null || !_proxy.IsRunning) return;
                try { _proxy.Stop(); } catch { }
                _log.Info("proxy stopped");
            }
            Raise();
        }

        /// <summary>
        /// Stops and starts the proxy. The listener is rebuilt from scratch, so
        /// this genuinely rebinds the port instead of reusing a closed one.
        /// </summary>
        public string RestartProxy()
        {
            StopProxy();

            // Give http.sys a moment to release the port before rebinding.
            System.Threading.Thread.Sleep(120);

            lock (_gate)
            {
                try
                {
                    _proxy.Start();
                    _log.Start("proxy restarted");
                }
                catch (Exception ex)
                {
                    _log.Error("proxy failed to restart: " + ex.Message);
                    Raise();
                    return "重启失败：" + ex.Message;
                }
            }

            Raise();
            return null;
        }

        void OnWatchChanged(bool alive)
        {
            _log.Info(alive ? "watched process appeared -> starting proxy" : "watched process gone -> stopping proxy");
            if (alive) StartProxy();
            else StopProxy();
        }

        // ---------- config ----------

        /// <summary>
        /// Applies edited settings. Rebuilds the proxy because the listen port
        /// and upstream are baked into it at construction time.
        /// </summary>
        public string Apply(string upstream, int port, string mode, List<string> watchProcesses,
            bool startWithWindows, bool verbose)
        {
            Uri parsed;
            if (!Uri.TryCreate(upstream, UriKind.Absolute, out parsed))
                return "上游地址不是合法 URL";
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
                return "上游地址必须以 http:// 或 https:// 开头";
            if (port <= 0 || port > 65535) return "端口必须在 1–65535 之间";

            lock (_gate)
            {
                try { if (_proxy != null) _proxy.Stop(); } catch { }

                _cfg.Upstream = parsed.GetLeftPart(UriPartial.Authority);
                _cfg.Port = port;
                _cfg.Mode = mode == "watch" ? "watch" : "always";
                _cfg.WatchProcesses = watchProcesses != null && watchProcesses.Count > 0
                    ? watchProcesses : new List<string> { "ZCode" };
                _cfg.StartWithWindows = startWithWindows;
                _cfg.Verbose = verbose;

                try { _cfg.Save(); }
                catch (Exception ex) { return "保存配置失败：" + ex.Message; }

                _proxy = new ProxyServer(_cfg.Upstream, _cfg.Port, _log.Write);
                _watcher.Dispose();
                _watcher = new ProcessWatcher(_cfg.WatchProcesses, 3000, OnWatchChanged);

                StartupEntry.Set(_cfg.StartWithWindows, _log.Info);

                _log.Info("config applied: upstream=" + _cfg.Upstream + " port=" + _cfg.Port + " mode=" + _cfg.Mode);
            }

            System.Threading.Thread.Sleep(120);

            if (_cfg.Mode == "watch") _watcher.Start();
            else StartProxy();

            Raise();
            return null;
        }

        public void ToggleMode()
        {
            Apply(_cfg.Upstream, _cfg.Port, _cfg.Mode == "always" ? "watch" : "always",
                _cfg.WatchProcesses, _cfg.StartWithWindows, _cfg.Verbose);
        }

        public void ToggleStartWithWindows()
        {
            Apply(_cfg.Upstream, _cfg.Port, _cfg.Mode, _cfg.WatchProcesses,
                !_cfg.StartWithWindows, _cfg.Verbose);
        }

        public string StatusText
        {
            get
            {
                if (IsRunning) return "运行中 · 127.0.0.1:" + _cfg.Port;
                if (_cfg.Mode == "watch") return "待机 · 等待 " + string.Join(", ", _cfg.WatchProcesses.ToArray());
                return "未运行";
            }
        }

        public void Raise()
        {
            EventHandler h = StateChanged;
            if (h != null) { try { h(this, EventArgs.Empty); } catch { } }
        }

        public void Dispose()
        {
            try { _watcher.Dispose(); } catch { }
            try { if (_proxy != null) _proxy.Dispose(); } catch { }
            try { _log.Dispose(); } catch { }
        }
    }

    // HKCU Run key helper, shared by the window and the tray.
    public static class StartupEntry
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static void Set(bool enable, Action<string> log)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (enable) k.SetValue(Program.AppName, "\"" + AppConfig.ExePath + "\"");
                    else k.DeleteValue(Program.AppName, false);
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("startup registry error: " + ex.Message);
            }
        }

        public static bool IsEnabled()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    return k.GetValue(Program.AppName) != null;
                }
            }
            catch { return false; }
        }
    }
}
