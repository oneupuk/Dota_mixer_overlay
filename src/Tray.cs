using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DotaGlass
{
    internal sealed class Tray : IDisposable
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "DotaGlassOverlay";

        readonly NotifyIcon icon;
        readonly ToolStripMenuItem status, preview;

        public Tray(Controller c)
        {
            var menu = new ContextMenuStrip();
            status = new ToolStripMenuItem("Starting...") { Enabled = false };
            preview = new ToolStripMenuItem("Preview overlay");
            preview.Click += (s, e) => { c.TogglePreview(); preview.Checked = c.IsPreview; };
            var startup = new ToolStripMenuItem("Start with Windows") { Checked = AutostartEnabled() };
            startup.Click += (s, e) => { SetAutostart(!startup.Checked); startup.Checked = AutostartEnabled(); };
            var steam = new ToolStripMenuItem("Copy Steam launch option (auto-start with Dota)");
            steam.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(GameLauncher.LaunchOption);
                    Notify("Copied to clipboard",
                        "Steam > Dota 2 > Properties > Launch options: paste it (keep any options you already have after it).", false);
                }
                catch { }
            };
            var edit = new ToolStripMenuItem("Edit settings...");
            edit.Click += (s, e) => c.OpenSettings();
            var reload = new ToolStripMenuItem("Reload settings");
            reload.Click += (s, e) => c.ReloadSettings();
            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (s, e) => c.Exit();
            menu.Items.AddRange(new ToolStripItem[] { status, new ToolStripSeparator(), preview, steam, startup, edit, reload, new ToolStripSeparator(), exit });

            icon = new NotifyIcon { Icon = BuildIcon(), Text = "Dota Glass Overlay", ContextMenuStrip = menu, Visible = true };
            icon.MouseDoubleClick += (s, e) => { c.TogglePreview(); preview.Checked = c.IsPreview; };
        }

        public void SetStatus(string text)
        {
            status.Text = text;
            string tip = "Dota Glass Overlay - " + text;
            icon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        }

        public void SetPreviewChecked(bool on) { preview.Checked = on; }

        public void Notify(string title, string text, bool warning)
        {
            icon.ShowBalloonTip(8000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }

        public void Dispose()
        {
            icon.Visible = false;
            icon.Dispose();
        }

        static bool AutostartEnabled()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null; }
            catch { return false; }
        }

        static void SetAutostart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunName, false);
                }
            }
            catch { }
        }

        /// <summary>Little glass pill with a note, drawn at runtime (no .ico file to ship).</summary>
        static Icon BuildIcon()
        {
            using (var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var path = RoundRect(1.5f, 1.5f, 29, 29, 10))
                    {
                        using (var fill = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.FromArgb(255, 196, 48, 40), Color.FromArgb(255, 92, 16, 22), 60f))
                            g.FillPath(fill, path);
                        using (var sheen = new LinearGradientBrush(new Rectangle(0, 0, 32, 18), Color.FromArgb(110, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                            g.FillPath(sheen, path);
                        using (var rim = new Pen(Color.FromArgb(200, 255, 255, 255), 1.3f))
                            g.DrawPath(rim, path);
                    }
                    using (var white = new SolidBrush(Color.White))
                    {
                        g.FillEllipse(white, 8.5f, 18.5f, 8.5f, 7f);
                        g.FillRectangle(white, 15f, 7f, 2.3f, 15f);
                        g.FillPolygon(white, new[] { new PointF(15f, 7f), new PointF(23.5f, 9.5f), new PointF(23.5f, 13.5f), new PointF(15f, 11f) });
                    }
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            float d = r * 2;
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
