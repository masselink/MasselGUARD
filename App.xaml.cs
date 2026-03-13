using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace WGWifiSwitcher
{
    public partial class App : System.Windows.Application
    {
        private WinForms.NotifyIcon? _trayIcon;
        private MainWindow? _mainWindow;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (!IsAdmin())
            {
                var result = System.Windows.MessageBox.Show(
                    "WireGuard WiFi Switcher needs Administrator privileges to\n" +
                    "start and stop WireGuard tunnels.\n\nRelaunch as Administrator now?",
                    "Administrator Required",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.Yes)
                {
                    var psi = new ProcessStartInfo(
                        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName)
                    {
                        UseShellExecute = true,
                        Verb = "runas"
                    };
                    try { Process.Start(psi); } catch { }
                }
                Shutdown();
                return;
            }

            _mainWindow = new MainWindow();
            _mainWindow.Show();
            SetupTrayIcon();
        }

        private void SetupTrayIcon()
        {
            _trayIcon = new WinForms.NotifyIcon
            {
                Text = "WireGuard WiFi Switcher 0.6 beta",
                Visible = true,
                Icon = TrayIconHelper.CreateIcon()
            };

            var menu = new WinForms.ContextMenuStrip();
            menu.BackColor = System.Drawing.Color.FromArgb(22, 27, 34);
            menu.ForeColor = System.Drawing.Color.FromArgb(230, 237, 243);
            menu.ShowImageMargin = false;
            menu.ShowCheckMargin = false;
            menu.Renderer = new DarkMenuRenderer();

            var showItem = new WinForms.ToolStripMenuItem("Show Window");
            showItem.Font = new System.Drawing.Font("Consolas", 9f, System.Drawing.FontStyle.Bold);
            showItem.Click += (_, _) => ShowMainWindow();
            menu.Items.Add(showItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());

            var exitItem = new WinForms.ToolStripMenuItem("Exit");
            exitItem.Font = new System.Drawing.Font("Consolas", 9f);
            exitItem.Click += (_, _) => { _trayIcon!.Visible = false; Shutdown(); };
            menu.Items.Add(exitItem);

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
        }

        private void ShowMainWindow()
        {
            if (_mainWindow == null) return;
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }

        public void UpdateTrayStatus(string tunnelName, bool active)
        {
            if (_trayIcon == null) return;
            _trayIcon.Text = active
                ? $"WG WiFi Switcher \u2014 {tunnelName} active"
                : "WG WiFi Switcher \u2014 No tunnel active";
            _trayIcon.Icon = TrayIconHelper.CreateIcon(active);
        }

        private static bool IsAdmin() =>
            new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator);

        protected override void OnExit(ExitEventArgs e)
        {
            _trayIcon?.Dispose();
            base.OnExit(e);
        }
    }

    internal static class TrayIconHelper
    {
        public static System.Drawing.Icon CreateIcon(bool active = false)
        {
            // Draw at 256x256 then embed as multi-size icon so Windows picks the right size
            // for both taskbar (32px) and tray (16px)
            const int S = 256;
            using var bmp = new System.Drawing.Bitmap(S, S, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g   = System.Drawing.Graphics.FromImage(bmp);
            g.SmoothingMode    = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            var shieldColor = active
                ? System.Drawing.Color.FromArgb(63, 185, 80)
                : System.Drawing.Color.FromArgb(88, 166, 255);

            // Shield polygon scaled to 256x256
            using var brush = new System.Drawing.SolidBrush(shieldColor);
            var pts = new System.Drawing.PointF[]
            {
                new(128, 8),  new(232, 48), new(232, 140),
                new(128, 248), new(24, 140), new(24, 48)
            };
            g.FillPolygon(brush, pts);

            // Dark padlock body
            var dark = System.Drawing.Color.FromArgb(14, 17, 23);
            using var wBrush = new System.Drawing.SolidBrush(dark);
            g.FillRectangle(wBrush, 88, 128, 80, 80);

            // Padlock shackle arc
            using var pen = new System.Drawing.Pen(dark, 20f);
            g.DrawArc(pen, 88, 72, 80, 80, 180, 180);

            // Save to stream and load as Icon
            using var stream = new System.IO.MemoryStream();
            bmp.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;

            // Build a proper .ico with 32x32 and 16x16 frames
            using var ico32 = new System.Drawing.Bitmap(32, 32);
            using var g32   = System.Drawing.Graphics.FromImage(ico32);
            g32.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g32.DrawImage(bmp, 0, 0, 32, 32);

            var hIcon = ico32.GetHicon();
            return System.Drawing.Icon.FromHandle(hIcon);
        }
    }

    // Flat dark renderer — no gradients, no bright highlights
    internal class DarkMenuRenderer : System.Windows.Forms.ToolStripRenderer
    {
        private static readonly System.Drawing.Color Bg  = System.Drawing.Color.FromArgb(22, 27, 34);
        private static readonly System.Drawing.Color Hov = System.Drawing.Color.FromArgb(48, 54, 61);
        private static readonly System.Drawing.Color Fg  = System.Drawing.Color.FromArgb(230, 237, 243);
        private static readonly System.Drawing.Color Sep = System.Drawing.Color.FromArgb(48, 54, 61);

        protected override void OnRenderToolStripBackground(System.Windows.Forms.ToolStripRenderEventArgs e)
            => e.Graphics.Clear(Bg);

        protected override void OnRenderMenuItemBackground(System.Windows.Forms.ToolStripItemRenderEventArgs e)
        {
            var color = e.Item.Selected ? Hov : Bg;
            using var b = new System.Drawing.SolidBrush(color);
            e.Graphics.FillRectangle(b, new System.Drawing.Rectangle(System.Drawing.Point.Empty, e.Item.Size));
        }

        protected override void OnRenderItemText(System.Windows.Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Fg;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(System.Windows.Forms.ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new System.Drawing.Pen(Sep);
            e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
        }

        protected override void OnRenderToolStripBorder(System.Windows.Forms.ToolStripRenderEventArgs e)
        {
            using var pen = new System.Drawing.Pen(Sep);
            e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }
    }
}
