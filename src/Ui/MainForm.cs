// The main window: a frameless, dark, sidebar-navigated settings app.
//
// WinForms' default chrome cannot be themed, so the window draws its own title
// bar and resizes via WM_NCHITTEST. Closing hides to tray rather than exiting,
// because the proxy must keep running.
//
// Layout is computed once per resize into plain Rectangle fields; every widget
// is a child Control, and page bodies are painted in OnPaint. That keeps the
// hit-testing of WinForms and the look of a hand-drawn UI.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace Mimas
{
    public sealed class MainForm : Form
    {
        const int WM_NCHITTEST = 0x84;
        const int HTCLIENT = 1;
        const int HTCAPTION = 2;
        const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
        const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        readonly AppHost _host;
        readonly LogHub _log;

        // chrome
        Rectangle _titleBar;
        Rectangle _btnMin, _btnClose;
        bool _hoverMin, _hoverClose;

        // layout
        Rectangle _sidebar, _content;

        // nav
        readonly string[] _pageTitles = { "概览", "上游", "Agent", "日志", "关于" };
        readonly NavItem[] _nav = new NavItem[5];
        int _page;

        // dashboard
        StatusPill _pill;
        FlatButton _runButton;
        StatBlock _statRequests, _statRewritten, _statBlocks, _statErrors;
        TextField _copyField;
        FlatButton _copyButton, _restartButton, _openLogButton;
        TextLine _hint;
        Rectangle _cardRect;

        // upstream
        TextField _upstreamField, _portField, _watchField;
        OptionCard _modeAlways, _modeWatch;
        Toggle _startupToggle, _verboseToggle;
        TextLine _upstreamStatus;
        FlatButton _saveButton, _testButton;
        Rectangle _upCardRect, _modeCardRect, _behaveCardRect;

        // agent
        readonly List<AgentRow> _agentRows = new List<AgentRow>();
        TextLine _agentStatus;

        // log
        LiveLogView _liveLog;
        TextLine _logStatus;
        FlatButton _refreshLogButton, _clearLogButton;
        Toggle _showRequestsToggle;

        System.Windows.Forms.Timer _tick;
        string _toast;
        int _toastTicks;

        public MainForm(AppHost host)
        {
            _host = host;
            _log = host.Log;

            Ui.Scale = Ui.DetectScale();

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            Text = "Mimas";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            DoubleBuffered = true;
            KeyPreview = true;
            ShowInTaskbar = true;

            BuildChrome();
            BuildPages();

            // Size is set after the widgets exist: assigning it earlier fires
            // OnResize, which runs LayoutAll against half-built controls.
            MinimumSize = new Size(Ui.S(880), Ui.S(600));
            ClientSize = new Size(Ui.S(980), Ui.S(660));

            LayoutAll();
            LoadFromConfig();
            ShowPage(0);
            RefreshState();

            _tick = new System.Windows.Forms.Timer();
            _tick.Interval = 1500;
            _tick.Tick += delegate { RefreshState(); };
            _tick.Start();

            _host.StateChanged += delegate { SafeInvoke(RefreshState); };
        }

        void SafeInvoke(MethodInvoker m)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(m);
                else m();
            }
            catch { }
        }

        // ================= chrome =================

        void BuildChrome()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            for (int i = 0; i < _nav.Length; i++)
            {
                NavItem item = new NavItem();
                item.Text = _pageTitles[i];
                int captured = i;
                item.Click += delegate { ShowPage(captured); };
                _nav[i] = item;
                Controls.Add(item);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Setup(g);

            g.Clear(Theme.Bg);
            PaintSidebar(g);
            PaintTitleBar(g);
            PaintPage(g);
            PaintToast(g);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Fully painted in OnPaint; skipping this avoids a flash on resize.
        }

        void PaintSidebar(Graphics g)
        {
            using (SolidBrush b = new SolidBrush(Theme.Sidebar))
                g.FillRectangle(b, _sidebar);
            using (Pen p = new Pen(Theme.Border))
                g.DrawLine(p, _sidebar.Right, _sidebar.Top, _sidebar.Right, _sidebar.Bottom);

            int logoSize = Ui.S(30);
            Rectangle logoRect = new Rectangle(_sidebar.X + Ui.S(20), _sidebar.Y + Ui.S(16), logoSize, logoSize);
            Theme.DrawLogo(g, logoRect);

            Rectangle word = new Rectangle(logoRect.Right + Ui.S(10), logoRect.Y, Ui.S(150), logoSize);
            Theme.DrawText(g, "Mimas", Theme.Semi, Theme.Text, word);

            Rectangle sub = new Rectangle(logoRect.X, logoRect.Bottom + Ui.S(6), _sidebar.Width - Ui.S(40), Ui.S(16));
            Theme.DrawText(g, "土卫一 · tool_result 图片修复", Theme.Tiny, Theme.TextFaint, sub);

            Rectangle foot = new Rectangle(_sidebar.X + Ui.S(20), _sidebar.Bottom - Ui.S(52),
                _sidebar.Width - Ui.S(40), Ui.S(16));
            Theme.DrawText(g, "v" + Program.AppVersion, Theme.Tiny, Theme.TextFaint, foot);

            Rectangle foot2 = new Rectangle(_sidebar.X + Ui.S(20), _sidebar.Bottom - Ui.S(34),
                _sidebar.Width - Ui.S(40), Ui.S(16));
            Theme.DrawText(g, _host.IsRunning ? "代理运行中" : "代理已停止", Theme.Tiny,
                _host.IsRunning ? Theme.Green : Theme.TextFaint, foot2);
        }

        void PaintTitleBar(Graphics g)
        {
            using (SolidBrush b = new SolidBrush(Theme.Bg))
                g.FillRectangle(b, _titleBar);
            using (Pen p = new Pen(Theme.Border))
                g.DrawLine(p, 0, _titleBar.Bottom - 1, Width, _titleBar.Bottom - 1);

            Rectangle title = new Rectangle(_sidebar.Right + Ui.S(24), 0,
                Math.Max(Ui.S(100), Width - _sidebar.Right - Ui.S(140)), _titleBar.Height);
            Theme.DrawText(g, _pageTitles[_page] + " — Mimas", Theme.Medium, Theme.TextDim, title);

            DrawWindowButton(g, _btnMin, _hoverMin, false);
            DrawWindowButton(g, _btnClose, _hoverClose, true);
        }

        void DrawWindowButton(Graphics g, Rectangle r, bool hover, bool close)
        {
            if (hover)
            {
                Color bg = close ? Color.FromArgb(0xC0, 0x4A, 0x50) : Theme.SurfaceHi;
                using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, r);
            }

            Color fg = hover && close ? Color.White : Theme.TextDim;
            using (Pen p = new Pen(fg, Ui.Sf(1.3f)))
            {
                int cx = r.X + r.Width / 2;
                int cy = r.Y + r.Height / 2;
                int s = Ui.S(4);

                if (close)
                {
                    g.DrawLine(p, cx - s, cy - s, cx + s, cy + s);
                    g.DrawLine(p, cx - s, cy + s, cx + s, cy - s);
                }
                else
                {
                    g.DrawLine(p, cx - s, cy + s - Ui.S(1), cx + s, cy + s - Ui.S(1));
                }
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutAll();
            Invalidate();
        }

        bool _pagesBuilt;

        void LayoutAll()
        {
            if (!_pagesBuilt) return;

            int titleH = Ui.S(42);
            _titleBar = new Rectangle(0, 0, Width, titleH);

            int btnW = Ui.S(46);
            _btnClose = new Rectangle(Width - btnW, 0, btnW, titleH);
            _btnMin = new Rectangle(Width - btnW * 2, 0, btnW, titleH);

            int sideW = Ui.S(212);
            _sidebar = new Rectangle(0, titleH, sideW, Height - titleH);
            _content = new Rectangle(sideW, titleH, Width - sideW, Height - titleH);

            int navTop = titleH + Ui.S(98);
            for (int i = 0; i < _nav.Length; i++)
            {
                _nav[i].SetBounds(_sidebar.X + Ui.S(12), navTop + i * Ui.S(40),
                    _sidebar.Width - Ui.S(24), Ui.S(36));
            }

            LayoutDashboard();
            LayoutUpstream();
            LayoutAgent();
            LayoutLog();
        }

        // ================= pages =================

        void BuildPages()
        {
            // ---- dashboard ----
            _pill = new StatusPill();
            Controls.Add(_pill);

            _runButton = new FlatButton();
            _runButton.Kind = ButtonKind.Primary;
            _runButton.Click += delegate { ToggleRun(); };
            Controls.Add(_runButton);

            _statRequests = NewStat();
            _statRequests.Label = "请求数";
            _statRewritten = NewStat();
            _statRewritten.Label = "已改写";
            _statBlocks = NewStat();
            _statBlocks.Label = "提升的图片块";
            _statErrors = NewStat();
            _statErrors.Label = "错误";

            _copyField = new TextField();
            _copyField.ReadOnlyField = true;
            Controls.Add(_copyField);

            _copyButton = new FlatButton();
            _copyButton.Text = "复制";
            _copyButton.Click += delegate { CopyLocalUrl(); };
            Controls.Add(_copyButton);

            _restartButton = new FlatButton();
            _restartButton.Text = "重启代理";
            _restartButton.Click += delegate { DoRestart(); };
            Controls.Add(_restartButton);

            _openLogButton = new FlatButton();
            _openLogButton.Text = "打开日志文件";
            _openLogButton.Click += delegate { OpenPath(AppConfig.LogPath); };
            Controls.Add(_openLogButton);

            _hint = NewLabel();
            _hint.Paragraph = true;
            _hint.Font = Theme.Body;
            _hint.TextColor = Theme.TextFaint;
            Controls.Add(_hint);

            // ---- upstream ----
            _upstreamField = new TextField();
            _upstreamField.Placeholder = "https://your-relay.example.com";
            Controls.Add(_upstreamField);

            _portField = new TextField();
            _portField.Placeholder = "8817";
            Controls.Add(_portField);

            _watchField = new TextField();
            _watchField.Placeholder = "ZCode, claude";
            Controls.Add(_watchField);

            _modeAlways = new OptionCard();
            _modeAlways.Text = "常驻";
            _modeAlways.Detail = "始终监听端口，无论 agent 是否在运行";
            _modeAlways.Click += delegate { SelectMode("always"); };
            Controls.Add(_modeAlways);

            _modeWatch = new OptionCard();
            _modeWatch.Text = "跟随进程";
            _modeWatch.Detail = "仅当指定进程运行时监听，空闲时释放端口";
            _modeWatch.Click += delegate { SelectMode("watch"); };
            Controls.Add(_modeWatch);

            _startupToggle = new Toggle();
            Controls.Add(_startupToggle);

            _verboseToggle = new Toggle();
            Controls.Add(_verboseToggle);

            _upstreamStatus = NewLabel();
            Controls.Add(_upstreamStatus);

            _saveButton = new FlatButton();
            _saveButton.Text = "保存并应用";
            _saveButton.Kind = ButtonKind.Primary;
            _saveButton.Click += delegate { SaveUpstream(); };
            Controls.Add(_saveButton);

            _testButton = new FlatButton();
            _testButton.Text = "测试上游";
            _testButton.Click += delegate { TestUpstream(); };
            Controls.Add(_testButton);

            // ---- agent ----
            _agentRows.Add(MakeAgentRow("ZCode", "%USERPROFILE%\\.zcode\\v2\\provider_config.json", 0));
            _agentRows.Add(MakeAgentRow("Claude Code", "环境变量 ANTHROPIC_BASE_URL", 1));

            _agentStatus = NewLabel();
            Controls.Add(_agentStatus);

            // ---- log ----
            _liveLog = new LiveLogView(_log);
            Controls.Add(_liveLog);

            _logStatus = NewLabel();
            Controls.Add(_logStatus);

            _refreshLogButton = new FlatButton();
            _refreshLogButton.Text = "滚到最新";
            _refreshLogButton.Click += delegate { _liveLog.ScrollToEnd(); };
            Controls.Add(_refreshLogButton);

            _clearLogButton = new FlatButton();
            _clearLogButton.Text = "清空";
            _clearLogButton.Kind = ButtonKind.Ghost;
            _clearLogButton.Click += delegate { _log.Clear(); Toast("日志已清空"); };
            Controls.Add(_clearLogButton);

            _showRequestsToggle = new Toggle();
            _showRequestsToggle.SetSilently(true);
            _showRequestsToggle.Changed += delegate
            {
                _liveLog.ShowRequests = _showRequestsToggle.On;
            };
            Controls.Add(_showRequestsToggle);

            _pagesBuilt = true;
        }

        AgentRow MakeAgentRow(string name, string detail, int index)
        {
            AgentRow row = new AgentRow(name, detail);
            row.Primary.Click += delegate { PointAgent(index); };
            row.Secondary.Click += delegate { RestoreAgent(index); };
            Controls.Add(row.Primary);
            Controls.Add(row.Secondary);
            return row;
        }

        StatBlock NewStat()
        {
            StatBlock s = new StatBlock();
            Controls.Add(s);
            return s;
        }

        TextLine NewLabel()
        {
            TextLine l = new TextLine();
            l.Font = Theme.Tiny;
            l.TextColor = Theme.TextDim;
            l.Text = "";
            return l;
        }

        // ---- dashboard layout ----

        void LayoutDashboard()
        {
            int x = _content.X + Ui.S(28);
            int w = _content.Width - Ui.S(56);
            int y = _content.Y + Ui.S(22);

            _pill.SetBounds(x, y + Ui.S(4), Ui.S(150), Ui.S(26));
            _runButton.SetBounds(x + w - Ui.S(120), y, Ui.S(120), Ui.S(34));
            y += Ui.S(54);

            int gap = Ui.S(12);
            int sw = (w - gap * 3) / 4;
            int sh = Ui.S(62);
            _statRequests.SetBounds(x, y, sw, sh);
            _statRewritten.SetBounds(x + sw + gap, y, sw, sh);
            _statBlocks.SetBounds(x + sw * 2 + gap * 2, y, sw, sh);
            _statErrors.SetBounds(x + sw * 3 + gap * 3, y, sw, sh);
            y += sh + Ui.S(18);

            _cardRect = new Rectangle(x, y, w, Ui.S(146));
            int ix = x + Ui.S(18);
            int iw = w - Ui.S(36);

            _copyField.SetBounds(ix, y + Ui.S(56), iw - Ui.S(88), Ui.S(36));
            _copyButton.SetBounds(x + w - Ui.S(96), y + Ui.S(56), Ui.S(78), Ui.S(36));

            _restartButton.SetBounds(ix, y + Ui.S(102), Ui.S(110), Ui.S(30));
            _openLogButton.SetBounds(ix + Ui.S(120), y + Ui.S(102), Ui.S(120), Ui.S(30));

            _hint.SetBounds(x, y + Ui.S(160), w, Ui.S(52));
        }

        // ---- upstream layout ----

        void LayoutUpstream()
        {
            int x = _content.X + Ui.S(28);
            int w = _content.Width - Ui.S(56);
            int y = _content.Y + Ui.S(22) + Ui.S(48);

            _upCardRect = new Rectangle(x, y, w, Ui.S(160));
            int ix = x + Ui.S(18);
            int iw = w - Ui.S(36);

            _upstreamField.SetBounds(ix, y + Ui.S(52), iw, Ui.S(36));
            _portField.SetBounds(ix, y + Ui.S(114), Ui.S(110), Ui.S(34));
            _testButton.SetBounds(ix + Ui.S(122), y + Ui.S(114), Ui.S(110), Ui.S(34));
            _upstreamStatus.SetBounds(ix + Ui.S(244), y + Ui.S(114), Math.Max(Ui.S(80), iw - Ui.S(244)), Ui.S(34));
            y += Ui.S(176);

            _modeCardRect = new Rectangle(x, y, w, Ui.S(190));
            int halfW = (iw - Ui.S(12)) / 2;
            _modeAlways.SetBounds(ix, y + Ui.S(52), halfW, Ui.S(62));
            _modeWatch.SetBounds(ix + halfW + Ui.S(12), y + Ui.S(52), halfW, Ui.S(62));
            _watchField.SetBounds(ix, y + Ui.S(142), iw, Ui.S(36));
            y += Ui.S(206);

            _behaveCardRect = new Rectangle(x, y, w, Ui.S(94));
            _startupToggle.SetBounds(ix + iw - Ui.S(42), y + Ui.S(18), Ui.S(42), Ui.S(22));
            _verboseToggle.SetBounds(ix + iw - Ui.S(42), y + Ui.S(56), Ui.S(42), Ui.S(22));

            _saveButton.SetBounds(x + w - Ui.S(130), _behaveCardRect.Bottom + Ui.S(16), Ui.S(130), Ui.S(36));
        }

        // ---- agent layout ----

        void LayoutAgent()
        {
            int x = _content.X + Ui.S(28);
            int w = _content.Width - Ui.S(56);
            int y = _content.Y + Ui.S(22) + Ui.S(48);

            int rowH = Ui.S(92);
            for (int i = 0; i < _agentRows.Count; i++)
            {
                AgentRow row = _agentRows[i];
                row.Rect = new Rectangle(x, y, w, rowH);

                int by = y + rowH - Ui.S(44);
                row.Primary.SetBounds(x + w - Ui.S(302), by, Ui.S(140), Ui.S(30));
                row.Secondary.SetBounds(x + w - Ui.S(150), by, Ui.S(132), Ui.S(30));

                y += rowH + Ui.S(12);
            }

            _agentStatus.SetBounds(x, y + Ui.S(6), w, Ui.S(48));
        }

        // ---- log layout ----

        void LayoutLog()
        {
            int x = _content.X + Ui.S(28);
            int w = _content.Width - Ui.S(56);
            int y = _content.Y + Ui.S(22) + Ui.S(48);

            int right = x + w;
            _clearLogButton.SetBounds(right - Ui.S(70), _content.Y + Ui.S(24), Ui.S(70), Ui.S(28));
            _refreshLogButton.SetBounds(right - Ui.S(182), _content.Y + Ui.S(24), Ui.S(104), Ui.S(28));
            _showRequestsToggle.SetBounds(right - Ui.S(256), _content.Y + Ui.S(26), Ui.S(42), Ui.S(22));

            _liveLog.SetBounds(x, y, w, Math.Max(Ui.S(120), _content.Bottom - y - Ui.S(46)));
            _logStatus.SetBounds(x, _liveLog.Bottom + Ui.S(8), w, Ui.S(24));
        }

        sealed class AgentRow
        {
            public Rectangle Rect;
            public string Name;
            public string Detail;
            public string Status = "";
            public bool Configured;
            public bool Present;
            public readonly FlatButton Primary = new FlatButton();
            public readonly FlatButton Secondary = new FlatButton();

            public AgentRow(string name, string detail)
            {
                Name = name;
                Detail = detail;
                Primary.Kind = ButtonKind.Primary;
                Primary.Text = "指向 Mimas";
                Secondary.Text = "恢复原配置";
            }
        }

        // ================= page switching =================

        void ShowPage(int index)
        {
            _page = index;
            for (int i = 0; i < _nav.Length; i++) _nav[i].Selected = i == index;
            ApplyVisibility();
            Invalidate();
        }

        void ApplyVisibility()
        {
            bool dash = _page == 0, up = _page == 1, agent = _page == 2, log = _page == 3;

            _pill.Visible = dash;
            _runButton.Visible = dash;
            _statRequests.Visible = dash;
            _statRewritten.Visible = dash;
            _statBlocks.Visible = dash;
            _statErrors.Visible = dash;
            _copyField.Visible = dash;
            _copyButton.Visible = dash;
            _restartButton.Visible = dash;
            _openLogButton.Visible = dash;
            _hint.Visible = dash;

            _upstreamField.Visible = up;
            _portField.Visible = up;
            _watchField.Visible = up;
            _modeAlways.Visible = up;
            _modeWatch.Visible = up;
            _startupToggle.Visible = up;
            _verboseToggle.Visible = up;
            _upstreamStatus.Visible = up;
            _saveButton.Visible = up;
            _testButton.Visible = up;

            for (int i = 0; i < _agentRows.Count; i++)
            {
                _agentRows[i].Primary.Visible = agent;
                _agentRows[i].Secondary.Visible = agent;
            }
            _agentStatus.Visible = agent;

            _liveLog.Visible = log;
            _logStatus.Visible = log;
            _refreshLogButton.Visible = log;
            _clearLogButton.Visible = log;
            _showRequestsToggle.Visible = log;

            if (log) _liveLog.Rebuild();
        }

        // ================= data binding =================

        void LoadFromConfig()
        {
            AppConfig c = _host.Config;
            _upstreamField.Text = c.Upstream;
            _portField.Text = c.Port.ToString();
            _watchField.Text = string.Join(", ", c.WatchProcesses.ToArray());
            _modeAlways.Selected = c.Mode == "always";
            _modeWatch.Selected = c.Mode == "watch";
            _startupToggle.SetSilently(c.StartWithWindows);
            _verboseToggle.SetSilently(c.Verbose);
            _copyField.Text = "http://127.0.0.1:" + c.Port;
        }

        void SelectMode(string mode)
        {
            _modeAlways.Selected = mode == "always";
            _modeWatch.Selected = mode == "watch";
            _modeAlways.Invalidate();
            _modeWatch.Invalidate();
        }

        string SelectedMode()
        {
            return _modeWatch.Selected ? "watch" : "always";
        }

        void SaveUpstream()
        {
            int port;
            if (!int.TryParse(_portField.Text.Trim(), out port))
            {
                SetStatus(_upstreamStatus, "端口必须是数字", Theme.Red);
                return;
            }

            List<string> procs = new List<string>();
            string[] parts = _watchField.Text.Split(new char[] { ',', '，', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string n = parts[i].Trim();
                if (n.Length > 0) procs.Add(n);
            }
            if (procs.Count == 0) procs.Add("ZCode");

            string err = _host.Apply(_upstreamField.Text.Trim(), port, SelectedMode(), procs,
                _startupToggle.On, _verboseToggle.On);

            if (err != null)
            {
                SetStatus(_upstreamStatus, err, Theme.Red);
                return;
            }

            _copyField.Text = "http://127.0.0.1:" + _host.Config.Port;
            LoadFromConfig();

            SetStatus(_upstreamStatus, "已保存并应用", Theme.Green);
            RefreshState();
            Toast("配置已应用");
        }

        void TestUpstream()
        {
            string url = _upstreamField.Text.Trim();
            SetStatus(_upstreamStatus, "测试中…", Theme.TextDim);

            ThreadPool.QueueUserWorkItem(delegate
            {
                Diagnostics.Probe up = Diagnostics.TestUpstream(url);
                SafeInvoke(delegate
                {
                    SetStatus(_upstreamStatus,
                        up.Ok ? up.Message + " · " + up.LatencyMs + " ms" : "不可达：" + up.Message,
                        up.Ok ? Theme.Green : Theme.Red);
                });
            });
        }

        void CopyLocalUrl()
        {
            try
            {
                Clipboard.SetText(_copyField.Text);
                Toast("已复制本地地址");
            }
            catch { }
        }

        void ToggleRun()
        {
            if (_host.IsRunning) _host.StopProxy();
            else _host.StartProxy();
            RefreshState();
        }

        void DoRestart()
        {
            _restartButton.Busy = true;
            _log.Info("restart requested from UI");

            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = _host.RestartProxy();
                SafeInvoke(delegate
                {
                    _restartButton.Busy = false;
                    RefreshState();
                    Toast(err == null ? "代理已重启" : err);
                });
            });
        }

        void PointAgent(int index)
        {
            string msg = index == 0
                ? AgentIntegration.PointZCodeAtMimas(_host.Config.Port)
                : AgentIntegration.PointClaudeCodeAtMimas(_host.Config.Port);

            SetStatus(_agentStatus, msg, msg.StartsWith("已") ? Theme.Green : Theme.Red);
            RefreshAgentRows();
            Toast(msg);
        }

        void RestoreAgent(int index)
        {
            string msg = index == 0
                ? AgentIntegration.RestoreZCode()
                : AgentIntegration.ClearClaudeCode();

            SetStatus(_agentStatus, msg, msg.StartsWith("已") ? Theme.Green : Theme.Red);
            RefreshAgentRows();
            Toast(msg);
        }

        // ================= refresh =================

        void RefreshState()
        {
            if (IsDisposed) return;

            bool running = _host.IsRunning;
            _pill.Live = running;
            _pill.Caption = running ? "运行中" : (_host.Config.Mode == "watch" ? "待机中" : "已停止");
            _pill.Invalidate();

            _runButton.Text = running ? "停止代理" : "启动代理";
            _runButton.Kind = running ? ButtonKind.Secondary : ButtonKind.Primary;
            _runButton.Invalidate();

            ProxyStats st = _host.Proxy != null ? _host.Proxy.Stats : null;
            if (st != null)
            {
                _statRequests.Value = st.Requests.ToString("N0");
                _statRewritten.Value = st.Rewritten.ToString("N0");
                _statBlocks.Value = st.BlocksHoisted.ToString("N0");
                _statErrors.Value = st.Errors.ToString("N0");
                _statRequests.Invalidate();
                _statRewritten.Invalidate();
                _statBlocks.Invalidate();
                _statErrors.Invalidate();
            }

            _hint.Text = running
                ? "把 agent 的 provider baseUrl 指向上面的地址即可。图片会以独立 content block 转发，"
                  + "不再被当作 base64 文本计费。"
                : (_host.Config.Mode == "watch"
                    ? "当前为「跟随进程」模式：等待 " + string.Join(", ", _host.Config.WatchProcesses.ToArray()) + " 启动。"
                    : "代理未运行。点击右上角「启动代理」。");

            RefreshAgentRows();

            if (_page == 3 && _liveLog != null)
            {
                _logStatus.SetText("最近 " + _log.Count + " 条 · " + AppConfig.LogPath, Theme.TextFaint);
            }

            Invalidate();
        }

        void RefreshAgentRows()
        {
            List<AgentTarget> targets = AgentIntegration.Detect(_host.Config.Port);
            for (int i = 0; i < _agentRows.Count && i < targets.Count; i++)
            {
                AgentRow row = _agentRows[i];
                AgentTarget t = targets[i];

                row.Configured = t.Configured;
                row.Present = t.Present;
                row.Status = t.StatusText;

                row.Primary.Text = t.Configured ? "重新指向" : "指向 Mimas";
                row.Primary.Kind = t.Configured ? ButtonKind.Secondary : ButtonKind.Primary;
                row.Primary.Enabled = t.Present || t.Kind == AgentKind.ClaudeCode;
                row.Secondary.Enabled = t.Configured;

                row.Primary.Invalidate();
                row.Secondary.Invalidate();
            }
        }

        void SetStatus(TextLine l, string text, Color color)
        {
            l.SetText(text, color);
        }

        // ================= page body painting =================

        void PaintPage(Graphics g)
        {
            if (_page == 0) PaintDashboard(g);
            else if (_page == 1) PaintUpstream(g);
            else if (_page == 2) PaintAgent(g);
            else if (_page == 3) PaintLog(g);
            else PaintAbout(g);
        }

        void PaintDashboard(Graphics g)
        {
            DrawCard(g, _cardRect, "本地入口", "把 agent 的 provider baseUrl 改成这个地址");
        }

        void PaintUpstream(Graphics g)
        {
            DrawPageHeader(g, "上游与运行方式", "Mimas 把请求转发到这里。改动会立即生效并重启代理。");

            DrawCard(g, _upCardRect, "上游地址", "agent 原本连接的地址，不含 /v1/messages");
            Theme.DrawText(g, "地址", Theme.Tiny, Theme.TextDim,
                new Rectangle(_upCardRect.X + Ui.S(18), _upCardRect.Y + Ui.S(34), Ui.S(200), Ui.S(16)));
            Theme.DrawText(g, "本地端口", Theme.Tiny, Theme.TextDim,
                new Rectangle(_upCardRect.X + Ui.S(18), _upCardRect.Y + Ui.S(96), Ui.S(200), Ui.S(16)));

            DrawCard(g, _modeCardRect, "运行方式", "");
            Theme.DrawText(g, "跟随的进程名（逗号分隔，.exe 可省略）", Theme.Tiny, Theme.TextDim,
                new Rectangle(_modeCardRect.X + Ui.S(18), _modeCardRect.Y + Ui.S(124), Ui.S(420), Ui.S(16)));

            DrawCard(g, _behaveCardRect, "行为", "");
            Theme.DrawText(g, "开机自启动", Theme.Body, Theme.Text,
                new Rectangle(_behaveCardRect.X + Ui.S(18), _behaveCardRect.Y + Ui.S(10), Ui.S(300), Ui.S(38)));
            Theme.DrawText(g, "记录每一次调用", Theme.Body, Theme.Text,
                new Rectangle(_behaveCardRect.X + Ui.S(18), _behaveCardRect.Y + Ui.S(48), Ui.S(300), Ui.S(38)));
        }

        void PaintAgent(Graphics g)
        {
            DrawPageHeader(g, "Agent 集成", "Mimas 只改写 baseUrl，不接触你的 API Key。");

            for (int i = 0; i < _agentRows.Count; i++)
            {
                AgentRow row = _agentRows[i];
                DrawCard(g, row.Rect, row.Name, row.Detail);

                Color c = row.Configured ? Theme.Green : (row.Present ? Theme.TextDim : Theme.TextFaint);
                string label = row.Configured ? "● 已指向 Mimas"
                    : (row.Present ? "○ 未指向 Mimas" : "○ 未检测到该 Agent");

                Theme.DrawText(g, label, Theme.Tiny, c,
                    new Rectangle(row.Rect.X + Ui.S(18), row.Rect.Y + Ui.S(50), Ui.S(300), Ui.S(18)));
            }
        }

        void PaintLog(Graphics g)
        {
            DrawPageHeader(g, "调用日志", "每一次请求一行；被改写的会额外标出 hoisted。");

            Theme.DrawText(g, "显示普通调用", Theme.Tiny, Theme.TextDim,
                new Rectangle(_showRequestsToggle.Left - Ui.S(84), _showRequestsToggle.Top, Ui.S(80), Ui.S(22)),
                StringAlignment.Far);

            Rectangle r = _liveLog.Bounds;
            Theme.FillRound(g, r, Ui.S(8), Theme.Surface);
            Theme.StrokeRound(g, r, Ui.S(8), Theme.Border);
        }

        void DrawPageHeader(Graphics g, string title, string subtitle)
        {
            int x = _content.X + Ui.S(28);
            int w = _content.Width - Ui.S(56);
            Theme.DrawText(g, title, Theme.Semi, Theme.Text,
                new Rectangle(x, _content.Y + Ui.S(18), w, Ui.S(26)));
            Theme.DrawText(g, subtitle, Theme.Tiny, Theme.TextFaint,
                new Rectangle(x, _content.Y + Ui.S(46), w, Ui.S(18)));
        }

        void DrawCard(Graphics g, Rectangle r, string title, string subtitle)
        {
            Theme.FillRound(g, r, Ui.S(8), Theme.Surface);
            Theme.StrokeRound(g, r, Ui.S(8), Theme.Border);

            Theme.DrawText(g, title, Theme.Medium, Theme.Text,
                new Rectangle(r.X + Ui.S(18), r.Y + Ui.S(12), r.Width - Ui.S(36), Ui.S(20)));

            if (!string.IsNullOrEmpty(subtitle))
            {
                Theme.DrawText(g, subtitle, Theme.Tiny, Theme.TextFaint,
                    new Rectangle(r.X + Ui.S(18), r.Y + Ui.S(32), r.Width - Ui.S(36), Ui.S(16)));
            }
        }

        void PaintAbout(Graphics g)
        {
            int x = _content.X + Ui.S(28);
            int w = _content.Width - Ui.S(56);
            int y = _content.Y + Ui.S(26);

            Rectangle logoRect = new Rectangle(x, y, Ui.S(64), Ui.S(64));
            Theme.DrawLogo(g, logoRect);

            Theme.DrawText(g, "Mimas " + Program.AppVersion, Theme.Title, Theme.Text,
                new Rectangle(logoRect.Right + Ui.S(18), y + Ui.S(6), w - logoRect.Width, Ui.S(30)));
            Theme.DrawText(g, "土星的第七颗卫星", Theme.Tiny, Theme.TextFaint,
                new Rectangle(logoRect.Right + Ui.S(18), y + Ui.S(38), w - logoRect.Width, Ui.S(18)));

            y += Ui.S(96);

            string body =
                "Mimas 是一个只监听 127.0.0.1 的本地代理，它只改写一件事：把嵌套在\n"
                + "tool_result.content 里的 image / document block 提升为同级 content block。\n"
                + "\n"
                + "某些中转站会把 tool_result.content 序列化成字符串再转发，base64 于是被当作\n"
                + "文本计费：一张 1.1 MB 的 PNG 会变成 1,047,059 输入 token，而不是 1,155，\n"
                + "而且模型根本看不到图。提升之后同一张图是 18,143 token，视觉也恢复了。\n"
                + "\n"
                + "它不做的事：不改响应、不碰 API Key、不记录请求内容、不联网上报。";

            Theme.DrawParagraph(g, body, Theme.Body, Theme.TextDim,
                new Rectangle(x, y, w, Ui.S(180)));

            y += Ui.S(190);

            Theme.DrawText(g, "https://github.com/TJ111m/Mimas", Theme.Mono, Theme.AccentHi,
                new Rectangle(x, y, w, Ui.S(20)));

            Theme.DrawText(g, "MIT License · 单文件 · 无需管理员权限 · 无需安装", Theme.Tiny, Theme.TextFaint,
                new Rectangle(x, y + Ui.S(26), w, Ui.S(20)));
        }

        // ================= window behaviour =================

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST)
            {
                base.WndProc(ref m);
                if ((int)m.Result != HTCLIENT) return;

                int lp = m.LParam.ToInt32();
                Point p = PointToClient(new Point(lp & 0xFFFF, (lp >> 16) & 0xFFFF));
                int edge = Ui.S(6);

                bool left = p.X <= edge, right = p.X >= Width - edge;
                bool top = p.Y <= edge, bottom = p.Y >= Height - edge;

                if (top && left) { m.Result = (IntPtr)HTTOPLEFT; return; }
                if (top && right) { m.Result = (IntPtr)HTTOPRIGHT; return; }
                if (bottom && left) { m.Result = (IntPtr)HTBOTTOMLEFT; return; }
                if (bottom && right) { m.Result = (IntPtr)HTBOTTOMRIGHT; return; }
                if (left) { m.Result = (IntPtr)HTLEFT; return; }
                if (right) { m.Result = (IntPtr)HTRIGHT; return; }
                if (top) { m.Result = (IntPtr)HTTOP; return; }
                if (bottom) { m.Result = (IntPtr)HTBOTTOM; return; }

                if (_titleBar.Contains(p) && !_btnMin.Contains(p) && !_btnClose.Contains(p))
                {
                    m.Result = (IntPtr)HTCAPTION;
                    return;
                }
                return;
            }

            base.WndProc(ref m);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool hMin = _btnMin.Contains(e.Location);
            bool hClose = _btnClose.Contains(e.Location);
            if (hMin != _hoverMin || hClose != _hoverClose)
            {
                _hoverMin = hMin;
                _hoverClose = hClose;
                Invalidate(new Rectangle(_btnMin.X, 0, Width - _btnMin.X, _titleBar.Height));
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hoverMin || _hoverClose)
            {
                _hoverMin = _hoverClose = false;
                Invalidate(new Rectangle(_btnMin.X, 0, Width - _btnMin.X, _titleBar.Height));
            }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (_btnClose.Contains(e.Location)) { Hide(); return; }
            if (_btnMin.Contains(e.Location)) { WindowState = FormWindowState.Minimized; return; }
            base.OnMouseDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Hide(); return; }
            base.OnKeyDown(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Closing hides; the tray icon owns the process lifetime.
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        public void GoTo(int page)
        {
            ShowPage(page);
        }

        // ---- transient toast ----

        void Toast(string message)
        {
            _toast = message;
            _toastTicks = 3;
            Invalidate();
        }

        void PaintToast(Graphics g)
        {
            if (_toast == null || _toastTicks <= 0) return;

            SizeF size = g.MeasureString(_toast, Theme.Body);
            int w = (int)size.Width + Ui.S(34);
            int h = Ui.S(34);
            Rectangle r = new Rectangle((Width - w) / 2, Height - h - Ui.S(26), w, h);

            Theme.FillRound(g, r, Ui.S(6), Color.FromArgb(0x25, 0x2A, 0x35));
            Theme.StrokeRound(g, r, Ui.S(6), Theme.BorderHi);
            Theme.DrawText(g, _toast, Theme.Body, Theme.Text, r, StringAlignment.Center);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _tick != null) { _tick.Stop(); _tick.Dispose(); _tick = null; }
            base.Dispose(disposing);
        }

        static void OpenPath(string path)
        {
            try
            {
                if (!File.Exists(path)) File.WriteAllText(path, "");
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { }
        }
    }
}
