// Visual language for Mimas's own windows.
//
// Everything is drawn by hand: a dark, low-chroma palette with one violet
// accent and an amber highlight (Saturn's rings). No external assets, no
// designer files, no third-party UI library.
//
// All pixel constants go through Ui.S() so the whole window scales cleanly on
// high-DPI displays. Fonts are in points, so GDI scales them for us.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Mimas
{
    public static class Ui
    {
        static float _scale = 1f;

        public static float Scale { get { return _scale; } set { _scale = value <= 0 ? 1f : value; } }

        public static int S(int v) { return (int)Math.Round(v * _scale); }
        public static float Sf(float v) { return v * _scale; }

        // DPI of the primary display, resolved without creating a Form.
        public static float DetectScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                    return g.DpiX / 96f;
            }
            catch { return 1f; }
        }
    }

    public static class Theme
    {
        // Surfaces
        public static readonly Color Bg = Color.FromArgb(0x0F, 0x11, 0x15);
        public static readonly Color Sidebar = Color.FromArgb(0x12, 0x14, 0x1A);
        public static readonly Color Surface = Color.FromArgb(0x17, 0x1A, 0x21);
        public static readonly Color SurfaceHi = Color.FromArgb(0x1E, 0x22, 0x2B);
        public static readonly Color Border = Color.FromArgb(0x2A, 0x2F, 0x3A);
        public static readonly Color BorderHi = Color.FromArgb(0x3A, 0x41, 0x50);

        // Text
        public static readonly Color Text = Color.FromArgb(0xE8, 0xEB, 0xF0);
        public static readonly Color TextDim = Color.FromArgb(0x93, 0x9A, 0xA8);
        public static readonly Color TextFaint = Color.FromArgb(0x66, 0x6D, 0x7A);

        // Accents
        public static readonly Color Accent = Color.FromArgb(0x8B, 0x7C, 0xF6);
        public static readonly Color AccentHi = Color.FromArgb(0xA3, 0x96, 0xFF);
        public static readonly Color AccentSoft = Color.FromArgb(0x24, 0x20, 0x40);
        public static readonly Color Amber = Color.FromArgb(0xE9, 0xB8, 0x72);
        public static readonly Color Green = Color.FromArgb(0x60, 0xCD, 0x9A);
        public static readonly Color Red = Color.FromArgb(0xF0, 0x71, 0x78);

        // ---------- fonts ----------

        static Font _body, _medium, _semi, _title, _mono, _tiny;

        public static Font Body { get { return _body ?? (_body = Make(9f, FontStyle.Regular)); } }
        public static Font Medium { get { return _medium ?? (_medium = Make(9f, FontStyle.Bold)); } }
        public static Font Semi { get { return _semi ?? (_semi = Make(12f, FontStyle.Bold)); } }
        public static Font Title { get { return _title ?? (_title = Make(19f, FontStyle.Bold)); } }
        public static Font Tiny { get { return _tiny ?? (_tiny = Make(8f, FontStyle.Regular)); } }

        public static Font Mono
        {
            get
            {
                if (_mono == null) _mono = new Font("Consolas", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
                return _mono;
            }
        }

        static Font Make(float size, FontStyle style)
        {
            string[] candidates = { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Tahoma" };
            for (int i = 0; i < candidates.Length; i++)
            {
                try
                {
                    using (Font probe = new Font(candidates[i], size, style, GraphicsUnit.Point))
                    {
                        if (string.Equals(probe.Name, candidates[i], StringComparison.OrdinalIgnoreCase))
                            return new Font(candidates[i], size, style, GraphicsUnit.Point);
                    }
                }
                catch { }
            }
            return new Font(FontFamily.GenericSansSerif, size, style, GraphicsUnit.Point);
        }

        // ---------- shapes ----------

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (radius <= 0 || r.Width <= 0 || r.Height <= 0) { p.AddRectangle(r); return p; }

            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0) { p.AddRectangle(r); return p; }

            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color fill)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (GraphicsPath p = RoundRect(r, radius))
            using (SolidBrush b = new SolidBrush(fill))
                g.FillPath(b, p);
        }

        public static void StrokeRound(Graphics g, Rectangle r, int radius, Color stroke, float width = 1f)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (GraphicsPath p = RoundRect(r, radius))
            using (Pen pen = new Pen(stroke, width))
                g.DrawPath(pen, p);
        }

        public static void FillRoundTop(Graphics g, Rectangle r, int radius, Color fill)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddLine(r.Right, r.Y + radius, r.Right, r.Bottom);
            p.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
            p.AddLine(r.X, r.Bottom, r.X, r.Y + radius);
            p.CloseFigure();
            using (SolidBrush b = new SolidBrush(fill))
                g.FillPath(b, p);
            p.Dispose();
        }

        // ---------- text ----------

        public static void Setup(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        }

        public static void DrawText(Graphics g, string text, Font font, Color color, Rectangle bounds,
            StringAlignment align = StringAlignment.Near, StringAlignment valign = StringAlignment.Center)
        {
            if (string.IsNullOrEmpty(text) || bounds.Width <= 0) return;
            using (SolidBrush b = new SolidBrush(color))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = align;
                sf.LineAlignment = valign;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.FormatFlags |= StringFormatFlags.NoWrap;
                g.DrawString(text, font, b, bounds, sf);
            }
        }

        public static void DrawParagraph(Graphics g, string text, Font font, Color color, Rectangle bounds)
        {
            if (string.IsNullOrEmpty(text) || bounds.Width <= 0) return;
            using (SolidBrush b = new SolidBrush(color))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Near;
                sf.Trimming = StringTrimming.EllipsisWord;
                g.DrawString(text, font, b, bounds, sf);
            }
        }

        // ---------- logo ----------

        // A ringed planet. Used for the title bar, the wizard and the tray icon,
        // so all three stay visually identical.
        //
        // Draw order is what sells the 3D read: the back half of the ring goes
        // down first, then the planet covers it, then the front half is redrawn
        // on top. Nothing is clipped or masked.
        public static void DrawLogo(Graphics g, Rectangle r)
        {
            float cx = r.X + r.Width / 2f;
            float cy = r.Y + r.Height / 2f;
            float rad = Math.Min(r.Width, r.Height) * 0.29f;

            RectangleF ringBox = new RectangleF(
                cx - rad * 1.95f, cy - rad * 0.52f, rad * 3.9f, rad * 1.04f);
            float ringWidth = Math.Max(1.3f, Ui.Sf(2.0f));

            using (Pen ring = new Pen(Color.FromArgb(0xE9, 0xB8, 0x72), ringWidth))
            {
                // back half (180..360 degrees in GDI terms is the top arc)
                g.DrawArc(ring, ringBox, 180f, 180f);

                using (SolidBrush body = new SolidBrush(Color.FromArgb(0xE4, 0xE7, 0xEF)))
                    g.FillEllipse(body, cx - rad, cy - rad, rad * 2, rad * 2);

                using (SolidBrush shade = new SolidBrush(Color.FromArgb(0x3A, 0x35, 0x52)))
                    g.FillEllipse(shade, cx - rad * 0.40f, cy - rad * 0.88f, rad * 1.28f, rad * 1.76f);

                using (SolidBrush spec = new SolidBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)))
                    g.FillEllipse(spec, cx - rad * 0.60f, cy - rad * 0.60f, rad * 0.46f, rad * 0.34f);

                // front half
                g.DrawArc(ring, ringBox, 0f, 180f);
            }
        }
    }
}
