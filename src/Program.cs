// Mimas - tray app entry point.
//
// Mimas is a moon of Saturn: it sits quietly in orbit around your agent and
// never shows a window. There is no main form; the process runs a tray icon
// plus a background HTTP proxy.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Mimas
{
    static class Program
    {
        public const string AppName = "Mimas";
        public const string AppVersion = "1.0.0";

        [STAThread]
        static void Main(string[] args)
        {
            bool noTray = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--no-tray" || a == "/no-tray" || a == "-notray") noTray = true;
            }

            bool first;
            using (Mutex mutex = new Mutex(true, "Global\\Mimas-Saturn-Moon-Proxy", out first))
            {
                if (!first)
                {
                    // Already running; do not start a second listener.
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MimasContext(noTray));
            }
        }
    }

    sealed class MimasContext : ApplicationContext
    {
        readonly AppConfig _cfg;
        readonly ProxyServer _proxy;
        readonly ProcessWatcher _watcher;
        readonly List<NotifyIcon> _icons = new List<NotifyIcon>();
        readonly object _logLock = new object();
        readonly bool _noTray;

        ToolStripMenuItem _statusItem;
        ToolStripMenuItem _modeItem;
        ToolStripMenuItem _startupItem;
        ToolStripMenuItem _upstreamItem;
        ToolStripMenuItem _portItem;

        bool _proxyRunning;

        public MimasContext(bool noTray)
        {
            _noTray = noTray;
            _cfg = AppConfig.Load();

            _proxy = new ProxyServer(_cfg.Upstream, _cfg.Port, Log);
            _watcher = new ProcessWatcher(_cfg.WatchProcesses, 3000, OnWatchChanged);

            Log("Mimas " + Program.AppVersion + " starting");
            Log("upstream=" + _cfg.Upstream + " port=" + _cfg.Port + " mode=" + _cfg.Mode);

            if (!_noTray) BuildTray();

            if (_cfg.Mode == "watch")
            {
                _watcher.Start();
            }
            else
            {
                StartProxy();
            }

            if (_cfg.StartWithWindows) EnsureStartupEntry(true);
        }

        // ---------- proxy lifecycle ----------

        void StartProxy()
        {
            if (_proxyRunning) return;
            try
            {
                _proxy.Start();
                _proxyRunning = true;
                Log("proxy started");
            }
            catch (Exception ex)
            {
                _proxyRunning = false;
                Log("proxy failed to start: " + ex.Message);
            }
            UpdateStatus();
        }

        void StopProxy()
        {
            if (!_proxyRunning) return;
            try { _proxy.Stop(); } catch { }
            _proxyRunning = false;
            Log("proxy stopped");
            UpdateStatus();
        }

        void OnWatchChanged(bool alive)
        {
            Log(alive ? "watched process appeared -> starting proxy" : "watched process gone -> stopping proxy");
            if (alive) StartProxy();
            else StopProxy();
        }

        // ---------- tray ----------

        void BuildTray()
        {
            Icon icon = MakeIcon();
            ContextMenuStrip menu = new ContextMenuStrip();

            _statusItem = new ToolStripMenuItem("状态：启动中…") { Enabled = false };
            _modeItem = new ToolStripMenuItem("运行方式", null,
                (s, e) => ToggleMode());
            _upstreamItem = new ToolStripMenuItem("上游：" + _cfg.Upstream) { Enabled = false };
            _portItem = new ToolStripMenuItem("本地端口：" + _cfg.Port) { Enabled = false };

            ToolStripMenuItem openConfig = new ToolStripMenuItem("打开配置文件…", null,
                (s, e) => OpenPath(AppConfig.ConfigPath));
            ToolStripMenuItem openLog = new ToolStripMenuItem("查看日志…", null,
                (s, e) => OpenPath(AppConfig.LogPath));

            _startupItem = new ToolStripMenuItem("开机自启动", null,
                (s, e) => ToggleStartup());
            _startupItem.Checked = _cfg.StartWithWindows;

            ToolStripMenuItem restart = new ToolStripMenuItem("重启代理", null,
                (s, e) => { StopProxy(); StartProxy(); });
            ToolStripMenuItem quit = new ToolStripMenuItem("退出", null,
                (s, e) => Quit());

            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_modeItem);
            menu.Items.Add(_upstreamItem);
            menu.Items.Add(_portItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(restart);
            menu.Items.Add(openConfig);
            menu.Items.Add(openLog);
            menu.Items.Add(_startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(quit);

            NotifyIcon tray = new NotifyIcon();
            tray.Icon = icon;
            tray.Text = Program.AppName;
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.MouseDoubleClick += (s, e) => OpenPath(AppConfig.LogPath);
            _icons.Add(tray);

            // A second hidden icon keeps the tray entry alive across Explorer restarts.
            NotifyIcon spare = new NotifyIcon();
            spare.Icon = icon;
            spare.Text = Program.AppName + " (backup)";
            spare.ContextMenuStrip = menu;
            spare.Visible = false;
            _icons.Add(spare);

            UpdateStatus();
        }

        void UpdateStatus()
        {
            string state;
            if (_proxyRunning) state = "运行中 · 127.0.0.1:" + _cfg.Port;
            else if (_cfg.Mode == "watch") state = "待机 · 等待 " + string.Join(", ", _cfg.WatchProcesses.ToArray());
            else state = "未运行";

            if (_statusItem != null) _statusItem.Text = "状态：" + state;
            if (_modeItem != null)
            {
                _modeItem.Text = _cfg.Mode == "watch"
                    ? "运行方式：跟随进程（" + string.Join(", ", _cfg.WatchProcesses.ToArray()) + "）"
                    : "运行方式：常驻";
            }

            for (int i = 0; i < _icons.Count; i++)
            {
                _icons[i].Text = Clip(Program.AppName + " · " + state);
            }
        }

        static string Clip(string s)
        {
            // NotifyIcon.Text is capped at 63 characters.
            return s.Length <= 63 ? s : s.Substring(0, 60) + "...";
        }

        void ToggleMode()
        {
            if (_cfg.Mode == "always")
            {
                _cfg.Mode = "watch";
                _cfg.Save();
                _watcher.Start();
                if (!_watcher.IsAnyAlive()) StopProxy();
                Log("mode -> watch");
            }
            else
            {
                _cfg.Mode = "always";
                _cfg.Save();
                _watcher.Dispose();
                StartProxy();
                Log("mode -> always");
            }
            UpdateStatus();
        }

        void ToggleStartup()
        {
            _cfg.StartWithWindows = !_cfg.StartWithWindows;
            _cfg.Save();
            EnsureStartupEntry(_cfg.StartWithWindows);
            _startupItem.Checked = _cfg.StartWithWindows;
            Log("startWithWindows -> " + _cfg.StartWithWindows);
        }

        // ---------- startup registry ----------

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        void EnsureStartupEntry(bool enable)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (enable) k.SetValue(Program.AppName, "\"" + AppConfig.ExePath + "\"");
                    else k.DeleteValue(Program.AppName, false);
                }
            }
            catch (Exception ex)
            {
                Log("startup registry error: " + ex.Message);
            }
        }

        // ---------- helpers ----------

        static void OpenPath(string path)
        {
            try
            {
                if (!File.Exists(path)) File.WriteAllText(path, "");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true
                });
            }
            catch { }
        }

        public void Log(string message)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + "\r\n";
            lock (_logLock)
            {
                try { File.AppendAllText(AppConfig.LogPath, line); }
                catch { }
            }
        }

        static Icon MakeIcon()
        {
            // Draw a small ringed planet; no external asset needed.
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (SolidBrush body = new SolidBrush(Color.FromArgb(210, 214, 222)))
                    g.FillEllipse(body, 9, 9, 14, 14);

                using (SolidBrush shade = new SolidBrush(Color.FromArgb(90, 60, 70, 90)))
                    g.FillEllipse(shade, 12, 12, 11, 11);

                using (Pen ring = new Pen(Color.FromArgb(235, 190, 150), 2.2f))
                {
                    g.DrawEllipse(ring, 3, 12, 26, 8);
                }

                using (SolidBrush spec = new SolidBrush(Color.FromArgb(120, 255, 255, 255)))
                    g.FillEllipse(spec, 12, 11, 4, 3);
            }

            IntPtr h = bmp.GetHicon();
            try
            {
                using (Icon tmp = Icon.FromHandle(h))
                    return (Icon)tmp.Clone();
            }
            finally
            {
                DestroyIcon(h);
                bmp.Dispose();
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr handle);

        void Quit()
        {
            Log("shutting down");
            StopProxy();
            try { _watcher.Dispose(); } catch { }
            for (int i = 0; i < _icons.Count; i++)
            {
                try { _icons[i].Visible = false; _icons[i].Dispose(); } catch { }
            }
            ExitThread();
        }
    }
}
