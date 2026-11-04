// First-run wizard.
//
// Shown once, the first time Mimas starts on a machine. Three steps:
//   1. where the upstream relay is
//   2. how Mimas should run (always / watch a process)
//   3. point the agents at it
//
// It is a plain Form with its own painted chrome, matching the main window.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Mimas
{
    public sealed class SetupWizard : Form
    {
        readonly AppHost _host;

        // step 1
        TextField _upstreamField;
        TextLine _upstreamHint;

        // step 2
        OptionCard _modeAlways, _modeWatch;
        TextField _watchField;
        Toggle _startupToggle;

        // step 3
        readonly List<AgentRow> _agentRows = new List<AgentRow>();

        FlatButton _back, _next, _finish;
        TextLine _stepLabel, _status;
        int _step;

        // Painted text that has to line up with the fields above it. Computed in
        // LayoutAll so the paint pass and the widget pass cannot drift apart.
        Rectangle _titleBar, _btnClose;
        Rectangle _heading, _sub, _fieldLabel, _watchLabel, _startupLabel;
        bool _hoverClose;

        public SetupWizard(AppHost host)
        {
            _host = host;

            Ui.Scale = Ui.DetectScale();

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            Text = "Mimas 初始配置";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            DoubleBuffered = true;
            KeyPreview = true;
            ShowInTaskbar = false;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            Build();

            // Size last: assigning it fires OnResize -> LayoutAll, which needs
            // every widget to exist already.
            ClientSize = new Size(Ui.S(640), Ui.S(520));

            LayoutAll();
            ShowStep(0);
        }

        void Build()
        {
            _upstreamField = new TextField();
            _upstreamField.Placeholder = "https://your-relay.example.com";
            _upstreamField.Text = _host.Config.Upstream;
            _upstreamField.TextChanged2 += delegate { CheckUpstream(); };
            Controls.Add(_upstreamField);

            _upstreamHint = NewLabel();
            _upstreamHint.Paragraph = true;
            _upstreamHint.Font = Theme.Body;
            _upstreamHint.TextColor = Theme.TextFaint;
            Controls.Add(_upstreamHint);

            _modeAlways = new OptionCard();
            _modeAlways.Text = "常驻";
            _modeAlways.Detail = "始终监听端口。简单、稳定，推荐。";
            _modeAlways.Click += delegate { SelectMode("always"); };
            Controls.Add(_modeAlways);

            _modeWatch = new OptionCard();
            _modeWatch.Text = "跟随进程";
            _modeWatch.Detail = "只在 ZCode / Claude Code 运行时监听，空闲时不占端口。";
            _modeWatch.Click += delegate { SelectMode("watch"); };
            Controls.Add(_modeWatch);

            _watchField = new TextField();
            _watchField.Placeholder = "ZCode, claude";
            _watchField.Text = string.Join(", ", _host.Config.WatchProcesses.ToArray());
            Controls.Add(_watchField);

            _startupToggle = new Toggle();
            _startupToggle.SetSilently(_host.Config.StartWithWindows);
            Controls.Add(_startupToggle);

            _agentRows.Add(MakeRow("ZCode", "%USERPROFILE%\\.zcode\\v2\\provider_config.json", 0));
            _agentRows.Add(MakeRow("Claude Code", "环境变量 ANTHROPIC_BASE_URL", 1));

            _stepLabel = NewLabel();
            _stepLabel.VerticalAlign = StringAlignment.Center;
            Controls.Add(_stepLabel);

            _status = NewLabel();
            _status.VerticalAlign = StringAlignment.Center;
            Controls.Add(_status);

            _back = new FlatButton();
            _back.Text = "上一步";
            _back.Click += delegate { ShowStep(_step - 1); };
            Controls.Add(_back);

            _next = new FlatButton();
            _next.Text = "下一步";
            _next.Kind = ButtonKind.Primary;
            _next.Click += delegate { Next(); };
            Controls.Add(_next);

            _finish = new FlatButton();
            _finish.Text = "完成";
            _finish.Kind = ButtonKind.Primary;
            _finish.Click += delegate { Finish(); };
            Controls.Add(_finish);

            _built = true;
        }

        AgentRow MakeRow(string name, string detail, int index)
        {
            AgentRow row = new AgentRow(name, detail);
            row.Primary.Click += delegate { PointAgent(index); };
            row.Secondary.Click += delegate { RestoreAgent(index); };
            Controls.Add(row.Primary);
            Controls.Add(row.Secondary);
            return row;
        }

        TextLine NewLabel()
        {
            TextLine l = new TextLine();
            l.Font = Theme.Tiny;
            l.TextColor = Theme.TextDim;
            l.Text = "";
            return l;
        }

        bool _built;

        void LayoutAll()
        {
            if (!_built) return;

            int titleH = Ui.S(42);
            _titleBar = new Rectangle(0, 0, Width, titleH);
            _btnClose = new Rectangle(Width - Ui.S(46), 0, Ui.S(46), titleH);

            int x = Ui.S(40);
            int w = Width - Ui.S(80);

            // Shared heading block. Every step starts its widgets at the same y,
            // so stepping forward never makes the content jump.
            int top = titleH + Ui.S(8);
            _heading = new Rectangle(x, top, w, Ui.S(26));
            _sub = new Rectangle(x, _heading.Bottom + Ui.S(2), w, Ui.S(18));
            int y = _heading.Bottom + Ui.S(26);

            // step 1
            _fieldLabel = new Rectangle(x, y, w, Ui.S(16));
            _upstreamField.SetBounds(x, y + Ui.S(22), w, Ui.S(38));
            _upstreamHint.SetBounds(x, y + Ui.S(68), w, Ui.S(44));

            // step 2
            int half = (w - Ui.S(12)) / 2;
            _modeAlways.SetBounds(x, y, half, Ui.S(64));
            _modeWatch.SetBounds(x + half + Ui.S(12), y, half, Ui.S(64));
            _watchLabel = new Rectangle(x, y + Ui.S(82), w, Ui.S(16));
            _watchField.SetBounds(x, y + Ui.S(104), w, Ui.S(38));
            _startupLabel = new Rectangle(x, y + Ui.S(160), w - Ui.S(56), Ui.S(22));
            _startupToggle.SetBounds(x + w - Ui.S(42), y + Ui.S(160), Ui.S(42), Ui.S(22));

            // step 3
            int agentY = _sub.Bottom + Ui.S(16);
            for (int i = 0; i < _agentRows.Count; i++)
            {
                AgentRow row = _agentRows[i];
                row.Rect = new Rectangle(x, agentY + i * Ui.S(96), w, Ui.S(84));
                row.Primary.SetBounds(x + w - Ui.S(300), row.Rect.Bottom - Ui.S(40), Ui.S(140), Ui.S(30));
                row.Secondary.SetBounds(x + w - Ui.S(148), row.Rect.Bottom - Ui.S(40), Ui.S(130), Ui.S(30));
            }

            int footY = Height - Ui.S(64);
            _back.SetBounds(x, footY, Ui.S(90), Ui.S(34));
            _finish.SetBounds(x + w - Ui.S(110), footY, Ui.S(110), Ui.S(34));
            _next.SetBounds(x + w - Ui.S(110), footY, Ui.S(110), Ui.S(34));

            // The step counter and the status share the footer row with the
            // buttons, so neither can collide with the content above.
            _stepLabel.SetBounds(x + Ui.S(102), footY, Ui.S(96), Ui.S(34));
            _status.SetBounds(x + Ui.S(210), footY, w - Ui.S(330), Ui.S(34));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutAll();
            Invalidate();
        }

        // ---------- steps ----------

        void ShowStep(int index)
        {
            _step = index;
            if (_step < 0) _step = 0;
            if (_step > 2) _step = 2;

            bool s0 = _step == 0, s1 = _step == 1, s2 = _step == 2;

            _upstreamField.Visible = s0;
            _upstreamHint.Visible = s0;

            _modeAlways.Visible = s1;
            _modeWatch.Visible = s1;
            _watchField.Visible = s1;
            _startupToggle.Visible = s1;

            for (int i = 0; i < _agentRows.Count; i++)
            {
                _agentRows[i].Primary.Visible = s2;
                _agentRows[i].Secondary.Visible = s2;
            }

            _back.Visible = _step > 0;
            _next.Visible = _step < 2;
            _finish.Visible = _step == 2;

            _stepLabel.SetText("第 " + (_step + 1) + " / 3 步", Theme.TextFaint);

            SetStatus("", Theme.TextDim);
            if (s0) CheckUpstream();
            if (s2) RefreshAgentRows();

            Invalidate();
        }

        void CheckUpstream()
        {
            string url = _upstreamField.Text.Trim();
            if (url.Length == 0)
            {
                _upstreamHint.SetText("必填。例如 https://relay.example.com", Theme.TextFaint);
                return;
            }

            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                _upstreamHint.SetText("需要以 http:// 或 https:// 开头", Theme.Red);
                return;
            }
            if (url.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
            {
                _upstreamHint.SetText("不要带 /v1/messages —— 只填到域名即可", Theme.Amber);
                return;
            }
            _upstreamHint.SetText("看起来没问题。Mimas 会转发到 " + url.TrimEnd('/') + "/v1/messages", Theme.TextFaint);
        }

        // Test seam: the offscreen render harness steps through pages without a
        // modal message loop.
        public void ShowStepForRender(int index)
        {
            ShowStep(index);
        }

        void SelectMode(string mode)
        {
            _modeAlways.Selected = mode == "always";
            _modeWatch.Selected = mode == "watch";
            _modeAlways.Invalidate();
            _modeWatch.Invalidate();
        }

        void Next()
        {
            if (_step == 0)
            {
                string url = _upstreamField.Text.Trim();
                Uri parsed;
                if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)
                    || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                {
                    SetStatus("请填写合法的 http:// 或 https:// 地址", Theme.Red);
                    CheckUpstream();
                    return;
                }
                if (url.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus("上游地址不要带 /v1/messages", Theme.Amber);
                    CheckUpstream();
                    return;
                }
            }
            ShowStep(_step + 1);
        }

        void Finish()
        {
            List<string> procs = new List<string>();
            string[] parts = _watchField.Text.Split(new char[] { ',', '，', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string n = parts[i].Trim();
                if (n.Length > 0) procs.Add(n);
            }
            if (procs.Count == 0) procs.Add("ZCode");

            string err = _host.Apply(_upstreamField.Text.Trim(), _host.Config.Port,
                _modeWatch.Selected ? "watch" : "always", procs, _startupToggle.On, _host.Config.Verbose);

            if (err != null)
            {
                SetStatus(err, Theme.Red);
                return;
            }

            _host.MarkFirstRunDone();
            DialogResult = DialogResult.OK;
            Close();
        }

        void PointAgent(int index)
        {
            string msg = index == 0
                ? AgentIntegration.PointZCodeAtMimas(_host.Config.Port)
                : AgentIntegration.PointClaudeCodeAtMimas(_host.Config.Port);

            SetStatus(msg, msg.StartsWith("已") ? Theme.Green : Theme.Red);
            RefreshAgentRows();
        }

        void RestoreAgent(int index)
        {
            string msg = index == 0
                ? AgentIntegration.RestoreZCode()
                : AgentIntegration.ClearClaudeCode();

            SetStatus(msg, msg.StartsWith("已") ? Theme.Green : Theme.Red);
            RefreshAgentRows();
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
                row.Primary.Text = t.Configured ? "重新指向" : "指向 Mimas";
                row.Primary.Kind = t.Configured ? ButtonKind.Secondary : ButtonKind.Primary;
                row.Primary.Enabled = t.Present || t.Kind == AgentKind.ClaudeCode;
                row.Secondary.Enabled = t.Configured;
                row.Primary.Invalidate();
                row.Secondary.Invalidate();
            }
        }

        void SetStatus(string text, Color color)
        {
            _status.SetText(text, color);
        }

        // ---------- painting ----------

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Setup(g);
            g.Clear(Theme.Bg);

            // title bar
            using (Pen p = new Pen(Theme.Border))
                g.DrawLine(p, 0, _titleBar.Bottom - 1, Width, _titleBar.Bottom - 1);

            Rectangle logoRect = new Rectangle(Ui.S(40), Ui.S(9), Ui.S(24), Ui.S(24));
            Theme.DrawLogo(g, logoRect);
            Theme.DrawText(g, "Mimas 初始配置", Theme.Medium, Theme.TextDim,
                new Rectangle(logoRect.Right + Ui.S(10), 0, Ui.S(300), _titleBar.Height));

            if (_hoverClose)
                using (SolidBrush b = new SolidBrush(Color.FromArgb(0xC0, 0x4A, 0x50)))
                    g.FillRectangle(b, _btnClose);

            using (Pen p = new Pen(_hoverClose ? Color.White : Theme.TextDim, Ui.Sf(1.3f)))
            {
                int cx = _btnClose.X + _btnClose.Width / 2;
                int cy = _btnClose.Y + _btnClose.Height / 2;
                int s = Ui.S(4);
                g.DrawLine(p, cx - s, cy - s, cx + s, cy + s);
                g.DrawLine(p, cx - s, cy + s, cx + s, cy - s);
            }

            int x = Ui.S(40);
            int w = Width - Ui.S(80);

            if (_step == 0)
            {
                Theme.DrawText(g, "你的中转站在哪里？", Theme.Semi, Theme.Text, _heading);
                Theme.DrawText(g, "Mimas 会把 agent 的请求转发到这里，并把嵌套在 tool_result "
                    + "里的图片提升成独立 content block。", Theme.Tiny, Theme.TextFaint, _sub);
                Theme.DrawText(g, "上游地址", Theme.Tiny, Theme.TextDim, _fieldLabel);
            }
            else if (_step == 1)
            {
                Theme.DrawText(g, "Mimas 什么时候运行？", Theme.Semi, Theme.Text, _heading);
                Theme.DrawText(g, "常驻最省心；跟随进程只在 agent 运行时占用端口。", Theme.Tiny, Theme.TextFaint, _sub);
                Theme.DrawText(g, "跟随的进程名（逗号分隔）", Theme.Tiny, Theme.TextDim, _watchLabel);
                Theme.DrawText(g, "开机自启动", Theme.Body, Theme.Text, _startupLabel);
            }
            else
            {
                Theme.DrawText(g, "把 agent 指向 Mimas", Theme.Semi, Theme.Text, _heading);
                Theme.DrawText(g, "Mimas 只改写 baseUrl，不会碰你的 API Key。", Theme.Tiny, Theme.TextFaint, _sub);

                for (int i = 0; i < _agentRows.Count; i++)
                {
                    AgentRow row = _agentRows[i];
                    Theme.FillRound(g, row.Rect, Ui.S(8), Theme.Surface);
                    Theme.StrokeRound(g, row.Rect, Ui.S(8), Theme.Border);

                    Theme.DrawText(g, row.Name, Theme.Medium, Theme.Text,
                        new Rectangle(row.Rect.X + Ui.S(18), row.Rect.Y + Ui.S(12), Ui.S(240), Ui.S(20)));
                    Theme.DrawText(g, row.Detail, Theme.Tiny, Theme.TextFaint,
                        new Rectangle(row.Rect.X + Ui.S(18), row.Rect.Y + Ui.S(32), row.Rect.Width - Ui.S(36), Ui.S(16)));

                    Color c = row.Configured ? Theme.Green : (row.Present ? Theme.TextDim : Theme.TextFaint);
                    string label = row.Configured ? "● 已指向 Mimas"
                        : (row.Present ? "○ 未指向 Mimas" : "○ 未检测到该 Agent");
                    Theme.DrawText(g, label, Theme.Tiny, c,
                        new Rectangle(row.Rect.X + Ui.S(18), row.Rect.Y + Ui.S(50), Ui.S(300), Ui.S(18)));
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = _btnClose.Contains(e.Location);
            if (h != _hoverClose) { _hoverClose = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hoverClose) { _hoverClose = false; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (_btnClose.Contains(e.Location)) { Close(); return; }
            base.OnMouseDown(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            base.OnKeyDown(e);
        }

        sealed class AgentRow
        {
            public Rectangle Rect;
            public string Name;
            public string Detail;
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
    }
}
