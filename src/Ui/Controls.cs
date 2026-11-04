// Hand-painted WinForms controls for the Mimas window.
//
// WinForms' stock controls cannot be themed dark without looking broken
// (white focus rectangles, system-coloured scrollbars, 3D borders). Every
// interactive surface here is therefore a plain Control that paints itself.
// Only the text input hosts a real TextBox, because caret, selection and IME
// composition are not worth reimplementing.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Mimas
{
    // ---------- button ----------

    public enum ButtonKind { Primary, Secondary, Ghost, Danger }

    public sealed class FlatButton : Control
    {
        public ButtonKind Kind = ButtonKind.Secondary;
        bool _hover, _press;
        bool _busy;

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            BackColor = Theme.Bg;
            Font = Theme.Body;
            Height = Ui.S(34);
        }

        public bool Busy
        {
            get { return _busy; }
            set { _busy = value; Enabled = !value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _press = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _press = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _press = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Setup(g);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int radius = Ui.S(6);

            Color fill, border, text;

            switch (Kind)
            {
                case ButtonKind.Primary:
                    fill = !Enabled ? Color.FromArgb(0x33, 0x2E, 0x52)
                        : _press ? Color.FromArgb(0x6E, 0x60, 0xD8)
                        : _hover ? Theme.AccentHi : Theme.Accent;
                    border = fill;
                    text = !Enabled ? Theme.TextFaint : Color.FromArgb(0x14, 0x11, 0x20);
                    break;

                case ButtonKind.Danger:
                    fill = !Enabled ? Color.FromArgb(0x33, 0x20, 0x22)
                        : _press ? Color.FromArgb(0xC4, 0x52, 0x58)
                        : _hover ? Color.FromArgb(0xF5, 0x86, 0x8C) : Color.FromArgb(0xD9, 0x5F, 0x66);
                    border = fill;
                    text = !Enabled ? Theme.TextFaint : Color.FromArgb(0x1A, 0x10, 0x12);
                    break;

                case ButtonKind.Ghost:
                    fill = _press ? Theme.SurfaceHi : _hover ? Color.FromArgb(0x1A, 0x1D, 0x24) : Color.Transparent;
                    border = Color.Transparent;
                    text = !Enabled ? Theme.TextFaint : Theme.TextDim;
                    break;

                default:
                    fill = _press ? Color.FromArgb(0x24, 0x29, 0x33)
                        : _hover ? Theme.SurfaceHi : Theme.Surface;
                    border = _hover ? Theme.BorderHi : Theme.Border;
                    text = !Enabled ? Theme.TextFaint : Theme.Text;
                    break;
            }

            if (fill != Color.Transparent) Theme.FillRound(g, r, radius, fill);
            if (border != Color.Transparent) Theme.StrokeRound(g, r, radius, border);

            Theme.DrawText(g, _busy ? "请稍候…" : Text, Font, text, r, StringAlignment.Center, StringAlignment.Center);
        }
    }

    // ---------- toggle ----------

    public sealed class Toggle : Control
    {
        bool _on;
        bool _hover;

        public event EventHandler Changed;

        public Toggle()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            BackColor = Theme.Surface;
            Size = new Size(Ui.S(42), Ui.S(22));
        }

        public bool On
        {
            get { return _on; }
            set { if (_on != value) { _on = value; Invalidate(); } }
        }

        public void SetSilently(bool value) { _on = value; Invalidate(); }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnClick(EventArgs e)
        {
            _on = !_on;
            Invalidate();
            if (Changed != null) Changed(this, EventArgs.Empty);
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Setup(g);

            Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
            int radius = track.Height / 2;

            Color trackColor = _on
                ? (_hover ? Theme.AccentHi : Theme.Accent)
                : (_hover ? Theme.BorderHi : Theme.Border);

            Theme.FillRound(g, track, radius, trackColor);

            int knobD = track.Height - Ui.S(6);
            int knobX = _on ? track.Right - knobD - Ui.S(3) : track.X + Ui.S(3);
            Rectangle knob = new Rectangle(knobX, track.Y + Ui.S(3), knobD, knobD);

            using (SolidBrush b = new SolidBrush(_on ? Color.FromArgb(0x14, 0x11, 0x20) : Color.FromArgb(0xB8, 0xBE, 0xCA)))
                g.FillEllipse(b, knob);
        }
    }

    // ---------- text field ----------

    // A rounded surface with a real TextBox inside it. The border, placeholder
    // and focus ring are painted here; the TextBox itself is chrome-free.
    public sealed class TextField : Control
    {
        readonly TextBox _box = new TextBox();
        string _placeholder = "";
        bool _focused;

        public TextField()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Height = Ui.S(36);

            _box.BorderStyle = BorderStyle.None;
            _box.BackColor = Theme.Surface;
            _box.ForeColor = Theme.Text;
            _box.Font = Theme.Body;
            _box.Visible = true;

            _box.GotFocus += delegate { _focused = true; Invalidate(); };
            _box.LostFocus += delegate { _focused = false; Invalidate(); };
            _box.TextChanged += delegate
            {
                Invalidate();
                if (TextChanged2 != null) TextChanged2(this, EventArgs.Empty);
            };
            _box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && EnterPressed != null) { EnterPressed(this, EventArgs.Empty); e.SuppressKeyPress = true; }
            };

            Controls.Add(_box);
        }

        public event EventHandler TextChanged2;
        public event EventHandler EnterPressed;

        public string Placeholder
        {
            get { return _placeholder; }
            set { _placeholder = value ?? ""; Invalidate(); }
        }

        public override string Text
        {
            get { return _box.Text; }
            set { _box.Text = value ?? ""; Invalidate(); }
        }

        public TextBox Inner { get { return _box; } }

        public bool ReadOnlyField
        {
            get { return _box.ReadOnly; }
            set { _box.ReadOnly = value; }
        }

        public void SelectAllText() { _box.SelectAll(); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int pad = Ui.S(11);
            _box.SetBounds(pad, (Height - _box.PreferredHeight) / 2 + Ui.S(1),
                Math.Max(10, Width - pad * 2), _box.PreferredHeight);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _box.Focus();
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Setup(g);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, Ui.S(6), Theme.Surface);

            Color border = _focused ? Theme.Accent : Theme.Border;
            Theme.StrokeRound(g, r, Ui.S(6), border);

            if (_box.TextLength == 0 && _placeholder.Length > 0 && !_focused)
            {
                int pad = Ui.S(12);
                Rectangle pr = new Rectangle(pad, 0, Math.Max(10, Width - pad * 2), Height);
                Theme.DrawText(g, _placeholder, Theme.Body, Theme.TextFaint, pr);
            }
        }
    }

    // ---------- card ----------

    public sealed class Card : Panel
    {
        public string Title = "";
        public string Subtitle = "";

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Setup(g);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, Ui.S(8), Theme.Surface);
            Theme.StrokeRound(g, r, Ui.S(8), Theme.Border);

            int top = Ui.S(14);
            if (Title.Length > 0)
            {
                Rectangle tr = new Rectangle(Ui.S(16), top, Width - Ui.S(32), Ui.S(18));
                Theme.DrawText(g, Title, Theme.Medium, Theme.Text, tr);
                top += Ui.S(20);
            }
            if (Subtitle.Length > 0)
            {
                Rectangle sr = new Rectangle(Ui.S(16), top, Width - Ui.S(32), Ui.S(18));
                Theme.DrawText(g, Subtitle, Theme.Tiny, Theme.TextFaint, sr);
            }
        }
    }

    // ---------- sidebar nav item ----------

    public sealed class NavItem : Control
    {
        bool _hover;
        bool _selected;

        public NavItem()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            BackColor = Theme.Sidebar;
            Height = Ui.S(36);
            Font = Theme.Body;
        }

        public bool Selected
        {
            get { return _selected; }
            set { if (_selected != value) { _selected = value; Invalidate(); } }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Setup(g);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);

            if (_selected) Theme.FillRound(g, r, Ui.S(6), Theme.AccentSoft);
            else if (_hover) Theme.FillRound(g, r, Ui.S(6), Color.FromArgb(0x1A, 0x1D, 0x25));

            if (_selected)
            {
                Rectangle bar = new Rectangle(r.X + Ui.S(1), r.Y + Ui.S(9), Ui.S(2), r.Height - Ui.S(18));
                Theme.FillRound(g, bar, Ui.S(1), Theme.Accent);
            }

            Rectangle tr = new Rectangle(r.X + Ui.S(14), 0, r.Width - Ui.S(20), r.Height);
            Theme.DrawText(g, Text, _selected ? Theme.Medium : Theme.Body,
                _selected ? Theme.Text : (_hover ? Theme.Text : Theme.TextDim), tr);
        }
    }

    // ---------- stat block ----------

    public sealed class StatBlock : Control
    {
        string _value = "0";
        string _label = "";

        public StatBlock()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        public string Value { get { return _value; } set { _value = value ?? "0"; Invalidate(); } }
        public string Label { get { return _label; } set { _label = value ?? ""; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Setup(g);

            g.Clear(Theme.Bg);

            Rectangle card = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, card, Ui.S(8), Theme.Surface);
            Theme.StrokeRound(g, card, Ui.S(8), Theme.Border);

            Rectangle vr = new Rectangle(0, Ui.S(9), Width, Ui.S(26));
            Theme.DrawText(g, _value, Theme.Semi, Theme.Text, vr, StringAlignment.Center);

            Rectangle lr = new Rectangle(0, Ui.S(35), Width, Ui.S(16));
            Theme.DrawText(g, _label, Theme.Tiny, Theme.TextFaint, lr, StringAlignment.Center);
        }
    }

    // ---------- status pill ----------

    public sealed class StatusPill : Control
    {
        public bool Live;
        public string Caption = "";

        public StatusPill()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
            Height = Ui.S(26);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Setup(g);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color tint = Live ? Color.FromArgb(0x16, 0x33, 0x2A) : Color.FromArgb(0x2A, 0x1E, 0x22);
            Theme.FillRound(g, r, r.Height / 2, tint);

            int d = Ui.S(7);
            int cx = r.X + Ui.S(11);
            int cy = r.Y + (r.Height - d) / 2;
            using (SolidBrush b = new SolidBrush(Live ? Theme.Green : Theme.Red))
                g.FillEllipse(b, cx, cy, d, d);

            Rectangle tr = new Rectangle(cx + d + Ui.S(7), 0, r.Width - (cx + d + Ui.S(7)) - Ui.S(8), r.Height);
            Theme.DrawText(g, Caption, Theme.Tiny, Live ? Theme.Green : Theme.Red, tr);
        }
    }

    // ---------- selectable option card ----------

    public sealed class OptionCard : Control
    {
        bool _selected;
        bool _hover;
        public string Detail = "";

        public OptionCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            BackColor = Theme.Bg;
            Height = Ui.S(62);
        }

        public bool Selected
        {
            get { return _selected; }
            set { if (_selected != value) { _selected = value; Invalidate(); } }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Setup(g);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, Ui.S(7), _selected ? Theme.AccentSoft : (_hover ? Theme.SurfaceHi : Theme.Surface));
            Theme.StrokeRound(g, r, Ui.S(7), _selected ? Theme.Accent : (_hover ? Theme.BorderHi : Theme.Border));

            // radio dot
            int d = Ui.S(14);
            int cx = r.X + Ui.S(16);
            int cy = r.Y + Ui.S(16);
            using (Pen p = new Pen(_selected ? Theme.Accent : Theme.BorderHi, Ui.Sf(1.6f)))
                g.DrawEllipse(p, cx, cy, d, d);
            if (_selected)
            {
                using (SolidBrush b = new SolidBrush(Theme.Accent))
                    g.FillEllipse(b, cx + Ui.S(4), cy + Ui.S(4), d - Ui.S(8), d - Ui.S(8));
            }

            int tx = cx + d + Ui.S(12);
            Rectangle tr = new Rectangle(tx, r.Y + Ui.S(11), r.Width - tx - Ui.S(14), Ui.S(18));
            Theme.DrawText(g, Text, Theme.Medium, Theme.Text, tr);

            if (Detail.Length > 0)
            {
                Rectangle dr = new Rectangle(tx, r.Y + Ui.S(30), r.Width - tx - Ui.S(14), Ui.S(20));
                Theme.DrawText(g, Detail, Theme.Tiny, Theme.TextFaint, dr);
            }
        }
    }

    // ---------- text line ----------

    // A label that paints itself instead of relying on WinForms transparency.
    // BackColor.Transparent on a Label asks the parent to paint underneath it,
    // which flickers badly on a hand-painted background. This just draws text
    // on the window's own background.
    public sealed class TextLine : Control
    {
        Color _color = Theme.TextDim;
        StringAlignment _align = StringAlignment.Near;
        StringAlignment _valign = StringAlignment.Center;
        bool _paragraph;

        public TextLine()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Font = Theme.Tiny;
            Height = Ui.S(18);
        }

        public Color TextColor
        {
            get { return _color; }
            set { _color = value; Invalidate(); }
        }

        public StringAlignment Align
        {
            get { return _align; }
            set { _align = value; Invalidate(); }
        }

        public StringAlignment VerticalAlign
        {
            get { return _valign; }
            set { _valign = value; Invalidate(); }
        }

        /// <summary>Wraps across lines instead of ellipsising.</summary>
        public bool Paragraph
        {
            get { return _paragraph; }
            set { _paragraph = value; Invalidate(); }
        }

        public void SetText(string text, Color color)
        {
            Text = text ?? "";
            _color = color;
            Invalidate();
        }

        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Setup(g);

            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);

            Rectangle r = new Rectangle(0, 0, Width, Height);
            if (_paragraph) Theme.DrawParagraph(g, Text, Font, _color, r);
            else Theme.DrawText(g, Text, Font, _color, r, _align, _valign);
        }
    }

    // ---------- log view ----------

    public sealed class LogView : Control
    {
        string[] _lines = new string[0];
        public int ScrollOffset;
        int _lineHeight = 14;
        int _hoverLine = -1;

        public LogView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
        }

        public void SetLines(string[] lines)
        {
            _lines = lines ?? new string[0];
            Clamp();
            Invalidate();
        }

        public int LineCount { get { return _lines.Length; } }

        int VisibleLines { get { return Math.Max(1, (Height - Ui.S(16)) / Math.Max(1, _lineHeight)); } }

        void Clamp()
        {
            int max = Math.Max(0, _lines.Length - VisibleLines);
            if (ScrollOffset > max) ScrollOffset = max;
            if (ScrollOffset < 0) ScrollOffset = 0;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using (Graphics g = CreateGraphics()) _lineHeight = (int)Math.Ceiling(g.MeasureString("Ag", Theme.Mono).Height);
            Clamp();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollOffset -= Math.Sign(e.Delta) * 3;
            Clamp();
            Invalidate();
            base.OnMouseWheel(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int idx = (e.Y - Ui.S(8)) / Math.Max(1, _lineHeight) + ScrollOffset;
            if (idx != _hoverLine) { _hoverLine = idx; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hoverLine = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Setup(g);

            g.Clear(Theme.Surface);

            int pad = Ui.S(8);
            int y = pad;

            if (_lines.Length == 0)
            {
                Theme.DrawText(g, "暂无日志", Theme.Body, Theme.TextFaint,
                    new Rectangle(pad, pad, Width - pad * 2, Height - pad * 2));
                return;
            }

            int from = ScrollOffset;
            int to = Math.Min(_lines.Length, from + VisibleLines);

            for (int i = from; i < to; i++)
            {
                string line = _lines[i];
                Color c = Theme.TextDim;

                if (line.IndexOf("hoisted", StringComparison.OrdinalIgnoreCase) >= 0) c = Theme.Amber;
                else if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                      || line.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0) c = Theme.Red;
                else if (line.IndexOf("proxy started", StringComparison.OrdinalIgnoreCase) >= 0
                      || line.IndexOf("listening", StringComparison.OrdinalIgnoreCase) >= 0) c = Theme.Green;

                if (i == _hoverLine)
                {
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(0x1C, 0x20, 0x28)))
                        g.FillRectangle(bg, 0, y - Ui.S(1), Width, _lineHeight);
                }

                Rectangle lr = new Rectangle(pad, y, Width - pad * 2, _lineHeight);
                Theme.DrawText(g, line, Theme.Mono, c, lr);
                y += _lineHeight;
            }
        }
    }

    // ---------- thin scrollbar hint (drawn, not interactive) ----------

    public static class ScrollHint
    {
        public static void Draw(Graphics g, Rectangle track, int offset, int total, int visible)
        {
            if (total <= visible) return;
            float frac = (float)visible / total;
            int h = Math.Max(Ui.S(24), (int)(track.Height * frac));
            int maxOffset = Math.Max(1, total - visible);
            int y = track.Y + (int)((track.Height - h) * ((float)offset / maxOffset));

            Theme.FillRound(g, new Rectangle(track.Right - Ui.S(5), y, Ui.S(3), h), Ui.S(2),
                Color.FromArgb(0x3A, 0x41, 0x50));
        }
    }
}
