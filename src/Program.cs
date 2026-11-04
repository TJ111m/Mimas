// Mimas - tray app entry point.
//
// Mimas is a moon of Saturn: it sits quietly in orbit around your agent. It
// runs a tray icon plus a background HTTP proxy, and shows a window only when
// asked (tray double-click, or --ui on the command line).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Mimas
{
    static class Program
    {
        public const string AppName = "Mimas";
        public const string AppVersion = "1.1.0";

        [STAThread]
        static void Main(string[] args)
        {
            bool noTray = false;
            bool forceUi = false;
            bool noWizard = false;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "--no-tray" || a == "/no-tray" || a == "-notray") noTray = true;
                else if (a == "--ui" || a == "/ui" || a == "-ui") forceUi = true;
                else if (a == "--no-wizard" || a == "/no-wizard") noWizard = true;
            }

            bool first;
            using (Mutex mutex = new Mutex(true, "Global\\Mimas-Saturn-Moon-Proxy", out first))
            {
                if (!first)
                {
                    // Already running. A second launch means "show me the window",
                    // which is what double-clicking the exe should do.
                    SignalExistingInstance();
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MimasContext(noTray, forceUi, noWizard));
            }
        }

        // ---------- single-instance activation ----------

        const int HWND_BROADCAST = 0xFFFF;
        static readonly int WM_MIMAS_SHOW = RegisterWindowMessage("Mimas-Show-Window");

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        static void SignalExistingInstance()
        {
            try { PostMessage((IntPtr)HWND_BROADCAST, WM_MIMAS_SHOW, IntPtr.Zero, IntPtr.Zero); }
            catch { }
        }

        public static int ShowMessageId { get { return WM_MIMAS_SHOW; } }
    }

    sealed class MimasContext : ApplicationContext
    {
        readonly AppHost _host;
        readonly List<NotifyIcon> _icons = new List<NotifyIcon>();
        readonly bool _noTray;
        readonly bool _forceUi;
        readonly bool _noWizard;

        MainForm _window;
        ToolStripMenuItem _statusItem;
        ToolStripMenuItem _modeItem;
        ToolStripMenuItem _startupItem;
        ToolStripMenuItem _upstreamItem;
        ToolStripMenuItem _portItem;

        NativeWindow _messageSink;

        public MimasContext(bool noTray, bool forceUi, bool noWizard)
        {
            _noTray = noTray;
            _forceUi = forceUi;
            _noWizard = noWizard;

            _host = new AppHost(new LogHub());
            _host.Start();

            if (!_noTray) BuildTray();
            BuildMessageSink();

            bool firstRun = _host.IsFirstRun;

            if (forceUi || (firstRun && !noWizard && !noTray))
            {
                // Defer until the message loop is running so the tray icon is
                // already visible behind the dialog.
                Application.Idle += ShowOnFirstIdle;
            }
        }

        void ShowOnFirstIdle(object sender, EventArgs e)
        {
            Application.Idle -= ShowOnFirstIdle;
            if (_host.IsFirstRun && !_noWizard) ShowWizard();
            else ShowWindow();
        }

        // ---------- window ----------

        public void ShowWindow()
        {
            if (_window == null || _window.IsDisposed)
            {
                _window = new MainForm(_host);
                _window.FormClosed += delegate { _window = null; };
            }

            if (!_window.Visible) _window.Show();
            if (_window.WindowState == FormWindowState.Minimized)
                _window.WindowState = FormWindowState.Normal;

            _window.Activate();
            _window.BringToFront();
            _host.Raise();
        }

        public void ShowWizard()
        {
            using (SetupWizard w = new SetupWizard(_host))
            {
                DialogResult r = w.ShowDialog();
                if (r == DialogResult.OK) ShowWindow();
            }
        }

        // ---------- single-instance message ----------

        void BuildMessageSink()
        {
            _messageSink = new MessageSink(delegate { ShowWindow(); });
        }

        // A NativeWindow subclass is required: WndProc is protected, so it cannot
        // be replaced with a delegate from outside the class.
        sealed class MessageSink : NativeWindow
        {
            readonly Action _onShow;

            public MessageSink(Action onShow)
            {
                _onShow = onShow;
                CreateHandle(new CreateParams());
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == Program.ShowMessageId)
                {
                    try { _onShow(); } catch { }
                    return;
                }
                base.WndProc(ref m);
            }
        }

        // ---------- tray ----------

        void BuildTray()
        {
            Icon icon = TrayIcon();
            ContextMenuStrip menu = new ContextMenuStrip();

            _statusItem = new ToolStripMenuItem("状态：启动中…") { Enabled = false };
            _modeItem = new ToolStripMenuItem("运行方式", null, delegate { ToggleMode(); });
            _upstreamItem = new ToolStripMenuItem("上游：") { Enabled = false };
            _portItem = new ToolStripMenuItem("本地端口：") { Enabled = false };

            ToolStripMenuItem openUi = new ToolStripMenuItem("打开设置…", null, delegate { ShowWindow(); });
            openUi.Font = new Font(openUi.Font, FontStyle.Bold);

            ToolStripMenuItem restart = new ToolStripMenuItem("重启代理", null, delegate
            {
                string err = _host.RestartProxy();
                if (err != null) _statusItem.Text = "状态：" + err;
            });

            ToolStripMenuItem openConfig = new ToolStripMenuItem("打开配置文件…", null,
                delegate { OpenPath(AppConfig.ConfigPath); });
            ToolStripMenuItem openLog = new ToolStripMenuItem("查看日志…", null,
                delegate { OpenPath(AppConfig.LogPath); });

            _startupItem = new ToolStripMenuItem("开机自启动", null, delegate { ToggleStartup(); });
            _startupItem.Checked = _host.Config.StartWithWindows;

            ToolStripMenuItem quit = new ToolStripMenuItem("退出", null, delegate { Quit(); });

            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(openUi);
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
            tray.MouseDoubleClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowWindow();
            };
            _icons.Add(tray);

            // A second hidden icon keeps the tray entry alive across Explorer restarts.
            NotifyIcon spare = new NotifyIcon();
            spare.Icon = icon;
            spare.Text = Program.AppName + " (backup)";
            spare.ContextMenuStrip = menu;
            spare.Visible = false;
            _icons.Add(spare);

            _host.StateChanged += delegate { SafeUpdateStatus(); };
            UpdateStatus();
        }

        void SafeUpdateStatus()
        {
            try
            {
                if (Application.OpenForms.Count >= 0) UpdateStatus();
            }
            catch { }
        }

        void UpdateStatus()
        {
            string state = _host.StatusText;

            if (_statusItem != null) _statusItem.Text = "状态：" + state;
            if (_modeItem != null)
            {
                _modeItem.Text = _host.Config.Mode == "watch"
                    ? "运行方式：跟随进程（" + string.Join(", ", _host.Config.WatchProcesses.ToArray()) + "）"
                    : "运行方式：常驻";
            }
            if (_upstreamItem != null) _upstreamItem.Text = "上游：" + _host.Config.Upstream;
            if (_portItem != null) _portItem.Text = "本地端口：" + _host.Config.Port;
            if (_startupItem != null) _startupItem.Checked = _host.Config.StartWithWindows;

            for (int i = 0; i < _icons.Count; i++)
                _icons[i].Text = Clip(Program.AppName + " · " + state);
        }

        static string Clip(string s)
        {
            // NotifyIcon.Text is capped at 63 characters.
            return s.Length <= 63 ? s : s.Substring(0, 60) + "...";
        }

        void ToggleMode()
        {
            _host.ToggleMode();
            UpdateStatus();
        }

        void ToggleStartup()
        {
            _host.ToggleStartWithWindows();
            UpdateStatus();
        }

        // ---------- helpers ----------

        static void OpenPath(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path)) System.IO.File.WriteAllText(path, "");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true
                });
            }
            catch { }
        }

        static Icon TrayIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                Theme.Setup(g);
                g.Clear(Color.Transparent);
                Theme.DrawLogo(g, new Rectangle(0, 0, 32, 32));
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
            _host.Log.Info("shutting down");
            try { if (_window != null) { _window.Dispose(); _window = null; } } catch { }
            _host.Dispose();

            for (int i = 0; i < _icons.Count; i++)
            {
                try { _icons[i].Visible = false; _icons[i].Dispose(); } catch { }
            }

            ExitThread();
        }
    }
}
