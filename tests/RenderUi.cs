// Offscreen render harness for the hand-painted UI.
//
//   scripts\render-ui.cmd [outDir]
//
// Builds each window, paints it into a bitmap with DrawToBitmap, and writes a
// PNG. The windows are shown at (-32000, -32000) with no taskbar entry, so
// nothing appears on screen — but they *are* shown, because WinForms will not
// create handles (and therefore will not paint child controls) for a window
// that was never displayed. CreateControl() alone is not enough.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using Mimas;

namespace MimasTests
{
    static class RenderUi
    {
        [STAThread]
        static int Main(string[] args)
        {
            string outDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "mimas-ui");
            Directory.CreateDirectory(outDir);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            LogHub log = new LogHub();

            // Seed the log so the log page has something to show.
            log.Start("listening on http://127.0.0.1:8817 -> https://relay.example.com");
            log.Info("upstream=https://relay.example.com port=8817 mode=always");
            log.Write("POST /v1/messages  200  1841ms  in=71.3KB  hoisted=1", LogKind.Rewrite);
            log.Write("hoisted 1 block(s): 71332 -> 71405 bytes", LogKind.Rewrite);
            log.Write("POST /v1/messages  200  903ms  in=12.4KB", LogKind.Request);
            log.Write("POST /v1/messages  401  122ms  in=1.2KB", LogKind.Error);
            log.Write("POST /v1/messages  200  640ms  in=2.1KB", LogKind.Request);
            log.Write("POST /v1/messages  200  512ms  in=2.0KB", LogKind.Request);
            log.Write("POST /v1/messages  200  470ms  in=1.9KB", LogKind.Request);

            AppHost host = new AppHost(log);

            RenderMain(host, outDir);
            RenderWizard(host, outDir);

            log.Dispose();
            Console.WriteLine("wrote " + outDir);
            return 0;
        }

        static void ParkOffscreen(Form f)
        {
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-32000, -32000);
            f.ShowInTaskbar = false;
            f.Show();
            Application.DoEvents();
        }

        static void Snap(Form f, string path)
        {
            Application.DoEvents();
            using (Bitmap bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(path, ImageFormat.Png);
            }
        }

        static void RenderMain(AppHost host, string outDir)
        {
            using (MainForm f = new MainForm(host))
            {
                ParkOffscreen(f);

                string[] pages = { "dashboard", "upstream", "agent", "log", "about" };
                for (int i = 0; i < pages.Length; i++)
                {
                    f.GoTo(i);
                    Snap(f, Path.Combine(outDir, "main-" + i + "-" + pages[i] + ".png"));
                }

                f.Hide();
            }
        }

        static void RenderWizard(AppHost host, string outDir)
        {
            using (SetupWizard w = new SetupWizard(host))
            {
                ParkOffscreen(w);

                for (int i = 0; i < 3; i++)
                {
                    w.ShowStepForRender(i);
                    Snap(w, Path.Combine(outDir, "wizard-" + i + ".png"));
                }

                w.Hide();
            }
        }
    }
}
