// Live log view: tails LogHub, auto-scrolls while pinned to the bottom.
//
// "Pinned" means the user has not scrolled up. Scrolling up pauses the tail so
// reading old lines is not fought by new ones arriving; scrolling back to the
// bottom resumes it. This is the behaviour people expect from a console.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Mimas
{
    public sealed class LiveLogView : Control
    {
        readonly LogHub _hub;
        List<LogEntry> _rows = new List<LogEntry>();
        int _lineHeight = 15;
        int _scroll;
        bool _pinned = true;
        int _hoverRow = -1;
        bool _showRequests = true;

        public bool ShowRequests
        {
            get { return _showRequests; }
            set { _showRequests = value; Rebuild(); }
        }

        public LiveLogView(LogHub hub)
        {
            _hub = hub;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Font = Theme.Mono;

            _hub.Appended += delegate { SafeRefresh(); };
            Rebuild();
        }

        void SafeRefresh()
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(new MethodInvoker(Rebuild));
                else Rebuild();
            }
            catch { }
        }

        public void Rebuild()
        {
            List<LogEntry> all = _hub.Tail(1200);

            List<LogEntry> rows = new List<LogEntry>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                LogEntry e = all[i];
                if (!_showRequests && e.Kind == LogKind.Request) continue;
                rows.Add(e);
            }

            _rows = rows;

            if (_pinned) _scroll = Math.Max(0, _rows.Count - VisibleLines);
            Clamp();
            Invalidate();
        }

        int VisibleLines
        {
            get { return Math.Max(1, (Height - Ui.S(12)) / Math.Max(1, _lineHeight)); }
        }

        void Clamp()
        {
            int max = Math.Max(0, _rows.Count - VisibleLines);
            if (_scroll > max) _scroll = max;
            if (_scroll < 0) _scroll = 0;
            _pinned = _scroll >= max;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using (Graphics g = CreateGraphics())
                _lineHeight = (int)Math.Ceiling(g.MeasureString("Ag", Theme.Mono).Height) + Ui.S(2);
            Clamp();
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            _scroll -= Math.Sign(e.Delta) * 3;
            Clamp();
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int idx = (e.Y - Ui.S(6)) / Math.Max(1, _lineHeight) + _scroll;
            if (idx != _hoverRow) { _hoverRow = idx; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hoverRow = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        public void ScrollToEnd()
        {
            _pinned = true;
            Clamp();
            _scroll = Math.Max(0, _rows.Count - VisibleLines);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Setup(g);

            g.Clear(Theme.Surface);

            int pad = Ui.S(10);

            if (_rows.Count == 0)
            {
                Theme.DrawText(g, "还没有请求。把 agent 的 baseUrl 指向 Mimas 后，每一次调用都会出现在这里。",
                    Theme.Body, Theme.TextFaint,
                    new Rectangle(pad, pad, Width - pad * 2, Height - pad * 2),
                    StringAlignment.Near, StringAlignment.Near);
                return;
            }

            int y = Ui.S(6);
            int from = _scroll;
            int to = Math.Min(_rows.Count, from + VisibleLines);

            for (int i = from; i < to; i++)
            {
                LogEntry row = _rows[i];
                Color color;
                switch (row.Kind)
                {
                    case LogKind.Rewrite: color = Theme.Amber; break;
                    case LogKind.Error: color = Theme.Red; break;
                    case LogKind.Start: color = Theme.Green; break;
                    case LogKind.Request: color = Theme.TextDim; break;
                    default: color = Theme.TextFaint; break;
                }

                if (i == _hoverRow)
                {
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(0x1C, 0x20, 0x28)))
                        g.FillRectangle(bg, 0, y - Ui.S(1), Width, _lineHeight);
                }

                // timestamp column
                Rectangle tr = new Rectangle(pad, y, Ui.S(58), _lineHeight);
                Theme.DrawText(g, row.At.ToString("HH:mm:ss"), Theme.Mono, Theme.TextFaint, tr);

                Rectangle lr = new Rectangle(pad + Ui.S(62), y, Width - pad * 2 - Ui.S(62), _lineHeight);
                Theme.DrawText(g, row.Text, Theme.Mono, color, lr);

                y += _lineHeight;
            }

            ScrollHint.Draw(g, ClientRectangle, _scroll, _rows.Count, VisibleLines);

            // "paused" badge when the user scrolled away from the tail
            if (!_pinned)
            {
                string badge = "已暂停自动滚动 · 滚到底部继续";
                SizeF size = g.MeasureString(badge, Theme.Tiny);
                Rectangle br = new Rectangle(Width - (int)size.Width - Ui.S(28), Height - Ui.S(28),
                    (int)size.Width + Ui.S(16), Ui.S(20));
                Theme.FillRound(g, br, Ui.S(5), Color.FromArgb(0x2C, 0x22, 0x18));
                Theme.DrawText(g, badge, Theme.Tiny, Theme.Amber, br, StringAlignment.Center);
            }
        }
    }
}
