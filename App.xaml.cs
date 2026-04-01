using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace MasselGUARD
{
    public partial class App : System.Windows.Application
    {
        private WinForms.NotifyIcon?      _trayIcon;
        private WinForms.ContextMenuStrip? _trayMenu;
        private WinForms.ToolStripMenuItem? _tunnelMenuHeader;
        private MainWindow? _mainWindow;
        private Mutex?      _instanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ── 1. Load language immediately — needed by all dialogs below ───
            Lang.Instance.Load(AppConfig.LoadLanguage());

            // ── 2. Single-instance check (mutex) ────────────────────────────
            bool isNewInstance = false;
            try
            {
                _instanceMutex = new Mutex(
                    initiallyOwned: true,
                    name: "Global\\MasselGUARD_SingleInstance",
                    out isNewInstance);
            }
            catch (UnauthorizedAccessException)
            {
                // Mutex exists but belongs to a different session/user — treat as already running
                isNewInstance = false;
            }

            if (!isNewInstance)
            {
                ShowAlreadyRunning();
                Shutdown();
                return;
            }


            // ── 3. Launch main window ────────────────────────────────────────
            _mainWindow = new MainWindow();

            // Show() then Activate() ensures the window comes to foreground
            // even when launched via UAC elevation from a non-elevated parent.
            _mainWindow.Show();
            _mainWindow.Activate();
            _mainWindow.Dispatcher.BeginInvoke(new Action(() =>
            {
                _mainWindow.Topmost = true;
                _mainWindow.Topmost = false;
                _mainWindow.Focus();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            SetupTrayIcon();
        }

        private void SetupTrayIcon()
        {
            _trayIcon = new WinForms.NotifyIcon
            {
                Text = "MasselGUARD v2.0",
                Visible = true,
                Icon = TrayIconHelper.CreateIcon()
            };

            _trayMenu = new WinForms.ContextMenuStrip();
            _trayMenu.BackColor = System.Drawing.Color.FromArgb(22, 27, 34);
            _trayMenu.ForeColor = System.Drawing.Color.FromArgb(230, 237, 243);
            _trayMenu.ShowImageMargin = true;
            _trayMenu.ShowCheckMargin = false;
            _trayMenu.Renderer = new DarkMenuRenderer();

            var showItem = new WinForms.ToolStripMenuItem(Lang.T("TrayShowWindow"));
            showItem.Font = new System.Drawing.Font("Consolas", 9f, System.Drawing.FontStyle.Bold);
            showItem.Click += (_, _) => ShowMainWindow();
            _trayMenu.Items.Add(showItem);
            _trayMenu.Items.Add(new WinForms.ToolStripSeparator());

            // Tunnel submenu placeholder — rebuilt by RebuildTrayTunnelMenu
            _tunnelMenuHeader = new WinForms.ToolStripMenuItem(Lang.T("TrayTunnels"));
            _tunnelMenuHeader.Font = new System.Drawing.Font("Consolas", 9f, System.Drawing.FontStyle.Bold);
            _trayMenu.Items.Add(_tunnelMenuHeader);
            _trayMenu.Items.Add(new WinForms.ToolStripSeparator());

            var exitItem = new WinForms.ToolStripMenuItem(Lang.T("TrayExit"));
            exitItem.Font = new System.Drawing.Font("Consolas", 9f);
            exitItem.Click += (_, _) => { _trayIcon!.Visible = false; Shutdown(); };
            _trayMenu.Items.Add(exitItem);

            _trayIcon.ContextMenuStrip = _trayMenu;
            _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
        }

        public void ShutdownApp()
        {
            _trayIcon!.Visible = false;
            Shutdown();
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
                ? Lang.T("TrayActive", tunnelName)
                : Lang.T("TrayIdle");
            _trayIcon.Icon = TrayIconHelper.CreateIcon(active);
        }

        public void RebuildTrayTunnelMenu(List<string> tunnels, List<string> active)
        {
            if (_tunnelMenuHeader == null) return;
            _tunnelMenuHeader.DropDownItems.Clear();

            if (tunnels.Count == 0)
            {
                var none = new WinForms.ToolStripMenuItem(Lang.T("TrayNoTunnels"));
                none.Font    = new System.Drawing.Font("Consolas", 9f);
                none.Enabled = false;
                _tunnelMenuHeader.DropDownItems.Add(none);
                return;
            }

            var disconnectAll = new WinForms.ToolStripMenuItem(Lang.T("TrayDisconnectAll"));
            disconnectAll.Font = new System.Drawing.Font("Consolas", 9f, System.Drawing.FontStyle.Bold);
            disconnectAll.Click += (_, _) =>
            {
                if (_mainWindow is MainWindow mw)
                    mw.Dispatcher.Invoke(() =>
                    {
                        foreach (var t in active.ToList()) mw.ManualStop(t);
                        mw.UpdateStatusDisplay();
                    });
            };
            _tunnelMenuHeader.DropDownItems.Add(disconnectAll);
            _tunnelMenuHeader.DropDownItems.Add(new WinForms.ToolStripSeparator());

            foreach (var tunnel in tunnels)
            {
                bool isActive = active.Contains(tunnel);

                // Each tunnel is a direct click-to-toggle item — no submenu, no bullet prefix
                var item = new WinForms.ToolStripMenuItem(tunnel);

                if (isActive)
                {
                    // Connected: bold green text + checkmark image drawn inline
                    item.Font      = new System.Drawing.Font("Consolas", 9f, System.Drawing.FontStyle.Bold);
                    item.ForeColor = System.Drawing.Color.FromArgb(63, 185, 80);   // #3FB950 green
                    item.Image     = MakeStatusDot(System.Drawing.Color.FromArgb(63, 185, 80));
                }
                else
                {
                    item.Font      = new System.Drawing.Font("Consolas", 9f);
                    item.ForeColor = System.Drawing.Color.FromArgb(139, 148, 158); // #8B949E sub
                    item.Image     = MakeStatusDot(System.Drawing.Color.FromArgb(48, 54, 61));  // dim dot
                }

                string t2 = tunnel;
                bool   a2 = isActive;
                item.Click += (_, _) =>
                {
                    if (_mainWindow is MainWindow mw)
                        mw.Dispatcher.Invoke(() =>
                        {
                            if (a2) mw.ManualStop(t2);
                            else    mw.ManualStart(t2);
                            mw.UpdateStatusDisplay();
                        });
                };
                _tunnelMenuHeader.DropDownItems.Add(item);
            }
        }



        // Small filled circle bitmap used as menu item icon
        private static System.Drawing.Bitmap MakeStatusDot(System.Drawing.Color color)
        {
            const int S = 12;
            var bmp = new System.Drawing.Bitmap(S, S, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var brush = new System.Drawing.SolidBrush(color);
            g.FillEllipse(brush, 1, 1, S - 2, S - 2);
            return bmp;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Stop all active local tunnel services before the process exits.
            // This prevents orphaned WireGuardTunnel$ services remaining in the SCM.
            try { TunnelDll.DisconnectAll(); } catch { }
            _trayIcon?.Dispose();
            try { _instanceMutex?.ReleaseMutex(); } catch { }
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }

        private void ShowAlreadyRunning()
        {
            // colours — defined inline since App resources aren't loaded yet
            var bg     = System.Windows.Media.Color.FromRgb(13,  17,  23);
            var panel  = System.Windows.Media.Color.FromRgb(22,  27,  34);
            var border = System.Windows.Media.Color.FromRgb(48,  54,  61);
            var accent = System.Windows.Media.Color.FromRgb(88, 166, 255);
            var textC  = System.Windows.Media.Color.FromRgb(230, 237, 243);
            var subC   = System.Windows.Media.Color.FromRgb(139, 148, 158);
            var warn   = System.Windows.Media.Color.FromRgb(247, 129, 102);

            System.Windows.Media.Brush Br(System.Windows.Media.Color c) =>
                new System.Windows.Media.SolidColorBrush(c);

            var stack = new System.Windows.Controls.StackPanel
                { Margin = new Thickness(28, 20, 28, 24) };

            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text       = "⚠  " + Lang.T("AlreadyRunningTitle"),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize   = 15, FontWeight = FontWeights.Bold,
                Foreground = Br(warn),
                Margin     = new Thickness(0, 0, 0, 14)
            });

            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text         = Lang.T("AlreadyRunningMessage"),
                FontFamily   = new System.Windows.Media.FontFamily("Consolas"),
                FontSize     = 11, Foreground = Br(subC),
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 20)
            });

            var closeBtn = new System.Windows.Controls.Button
            {
                Content           = Lang.T("BtnOk"),
                FontFamily        = new System.Windows.Media.FontFamily("Consolas"),
                FontSize          = 11,
                Foreground        = Br(textC),
                Background        = Br(panel),
                BorderBrush       = Br(border),
                BorderThickness   = new Thickness(1),
                Padding           = new Thickness(28, 6, 28, 6),
                HorizontalAlignment = HorizontalAlignment.Right,
                Cursor            = System.Windows.Input.Cursors.Hand
            };
            stack.Children.Add(closeBtn);

            var outerBorder = new System.Windows.Controls.Border
            {
                Background      = Br(bg),
                BorderBrush     = Br(border),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Child           = stack
            };

            // Title bar
            var titleBar = new System.Windows.Controls.Border
            {
                Background = Br(panel),
                Height     = 40,
                Child      = new System.Windows.Controls.TextBlock
                {
                    Text              = "MasselGUARD",
                    FontFamily        = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize          = 12, FontWeight = FontWeights.Bold,
                    Foreground        = Br(accent),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(14, 0, 0, 0)
                }
            };

            var root = new System.Windows.Controls.Grid();
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
                { Height = System.Windows.GridLength.Auto });
            root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
                { Height = System.Windows.GridLength.Auto });
            System.Windows.Controls.Grid.SetRow(titleBar, 0);
            System.Windows.Controls.Grid.SetRow(outerBorder, 1);

            // Wrap title bar inside the outer border's corner radius
            var wrapper = new System.Windows.Controls.Border
            {
                Background      = Br(bg),
                BorderBrush     = Br(border),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6)
            };
            var wrapGrid = new System.Windows.Controls.Grid();
            wrapGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
                { Height = System.Windows.GridLength.Auto });
            wrapGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition
                { Height = System.Windows.GridLength.Auto });
            System.Windows.Controls.Grid.SetRow(titleBar, 0);
            System.Windows.Controls.Grid.SetRow(stack, 1);
            wrapGrid.Children.Add(titleBar);
            wrapGrid.Children.Add(stack);
            wrapper.Child = wrapGrid;

            var win = new Window
            {
                Title                 = "MasselGUARD — Already running",
                Width                 = 440,
                SizeToContent         = SizeToContent.Height,
                WindowStyle           = WindowStyle.None,
                AllowsTransparency    = true,
                Background            = System.Windows.Media.Brushes.Transparent,
                ResizeMode            = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content               = wrapper
            };

            titleBar.MouseLeftButtonDown += (_, mev) =>
            {
                if (mev.LeftButton == System.Windows.Input.MouseButtonState.Pressed) win.DragMove();
            };
            closeBtn.Click += (_, _) => win.Close();

            win.ShowDialog();
        }
    }

    internal static class TrayIconHelper
    {
        // ── Colour palette ───────────────────────────────────────────────────
        private static System.Drawing.Color C(int r, int g, int b, int a = 255)
            => System.Drawing.Color.FromArgb(a, r, g, b);

        private static readonly System.Drawing.Color ColBg      = C(14,  17,  23);       // near-black bg
        private static readonly System.Drawing.Color ColAccent  = C(88, 166, 255);        // blue accent
        private static readonly System.Drawing.Color ColGreen   = C(63, 185,  80);        // connected green
        private static readonly System.Drawing.Color ColShield  = C(28,  33,  40);        // shield fill (dark card)
        private static readonly System.Drawing.Color ColRim     = C(48,  54,  61);        // rim / border

        // ── Public entry point ───────────────────────────────────────────────
        public static System.Drawing.Icon CreateIcon(bool active = false)
        {
            const int S = 256;
            using var src = RenderIcon(S, active);

            // Write a proper multi-size .ico: 256, 48, 32, 16 frames
            // Windows picks the best size for taskbar, tray, alt-tab, etc.
            int[] sizes = { 256, 48, 32, 16 };
            using var ms = new System.IO.MemoryStream();
            WriteMultiSizeIco(ms, src, sizes);
            ms.Position = 0;
            return new System.Drawing.Icon(ms);
        }

        private static void WriteMultiSizeIco(System.IO.Stream s,
            System.Drawing.Bitmap src, int[] sizes)
        {
            // Pre-render each size as PNG bytes
            var frames = new List<byte[]>();
            foreach (var sz in sizes)
            {
                using var scaled = new System.Drawing.Bitmap(sz, sz);
                using (var g = System.Drawing.Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode     = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.DrawImage(src, 0, 0, sz, sz);
                }
                using var ms2 = new System.IO.MemoryStream();
                scaled.Save(ms2, System.Drawing.Imaging.ImageFormat.Png);
                frames.Add(ms2.ToArray());
            }

            int n = sizes.Length;
            int headerSize = 6 + n * 16; // ICONDIR + n × ICONDIRENTRY

            using var w = new System.IO.BinaryWriter(s, System.Text.Encoding.UTF8, leaveOpen: true);
            // ICONDIR
            w.Write((short)0); // reserved
            w.Write((short)1); // type: icon
            w.Write((short)n); // count

            // Write ICONDIRENTRY for each frame
            int offset = headerSize;
            for (int i = 0; i < n; i++)
            {
                int sz = sizes[i];
                w.Write((byte)(sz >= 256 ? 0 : sz)); // width  (0 = 256)
                w.Write((byte)(sz >= 256 ? 0 : sz)); // height (0 = 256)
                w.Write((byte)0);                    // colour count
                w.Write((byte)0);                    // reserved
                w.Write((short)1);                   // colour planes
                w.Write((short)32);                  // bits per pixel
                w.Write(frames[i].Length);           // data size
                w.Write(offset);                     // offset
                offset += frames[i].Length;
            }

            // Write pixel data
            foreach (var frame in frames)
                w.Write(frame);
        }

        // ── Renderer — shield + chevron, matches title bar icon exactly ───
        // Viewbox is 24x24. Render into S×S.
        private static System.Drawing.Bitmap RenderIcon(int S, bool active)
        {
            var bmp = new System.Drawing.Bitmap(S, S, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.SmoothingMode   = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            g.Clear(System.Drawing.Color.Transparent);

            float sc = S / 24f;
            float X(float x) => x * sc;
            float Y(float y) => y * sc;

            // ── Shield: M12,1 L22,5 L22,13 C22,18.5 17.5,22.5 12,24 C6.5,22.5 2,18.5 2,13 L2,5 Z
            var shield = new System.Drawing.Drawing2D.GraphicsPath();
            shield.AddLine   (X(12),Y(1),   X(22),Y(5));
            shield.AddLine   (X(22),Y(5),   X(22),Y(13));
            shield.AddBezier (X(22),Y(13),  X(22),Y(18.5f), X(17.5f),Y(22.5f), X(12),Y(24));
            shield.AddBezier (X(12),Y(24),  X(6.5f),Y(22.5f), X(2),Y(18.5f),  X(2), Y(13));
            shield.AddLine   (X(2), Y(13),  X(2), Y(5));
            shield.CloseFigure();

            using (var fill = new System.Drawing.SolidBrush(ColShield))
                g.FillPath(fill, shield);
            using (var rim = new System.Drawing.Pen(ColRim, X(0.5f)))
                g.DrawPath(rim, shield);
            shield.Dispose();

            // ── Chevron: points 7,9  12,15  17,9 — round caps and join
            var wc = active ? ColGreen : ColAccent;
            using var pen = new System.Drawing.Pen(wc, X(2.2f))
            {
                StartCap  = System.Drawing.Drawing2D.LineCap.Round,
                EndCap    = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin  = System.Drawing.Drawing2D.LineJoin.Round
            };
            var chevron = new System.Drawing.PointF[]
            {
                new(X(7),  Y(9)),
                new(X(12), Y(15)),
                new(X(17), Y(9))
            };
            g.DrawLines(pen, chevron);

            return bmp;
        }
    }

    // Flat dark renderer — no gradients, no bright highlights
    internal class DarkMenuRenderer : System.Windows.Forms.ToolStripRenderer
    {
        private static readonly System.Drawing.Color Bg      = System.Drawing.Color.FromArgb(22, 27, 34);
        private static readonly System.Drawing.Color Hov     = System.Drawing.Color.FromArgb(48, 54, 61);
        private static readonly System.Drawing.Color Sep     = System.Drawing.Color.FromArgb(48, 54, 61);
        private static readonly System.Drawing.Color ImgCol  = System.Drawing.Color.FromArgb(16, 21, 28);

        protected override void OnRenderToolStripBackground(System.Windows.Forms.ToolStripRenderEventArgs e)
            => e.Graphics.Clear(Bg);

        protected override void OnRenderImageMargin(System.Windows.Forms.ToolStripRenderEventArgs e)
        {
            // Paint image margin column slightly darker so dots pop
            using var b = new System.Drawing.SolidBrush(ImgCol);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderMenuItemBackground(System.Windows.Forms.ToolStripItemRenderEventArgs e)
        {
            var color = e.Item.Selected ? Hov : Bg;
            using var b = new System.Drawing.SolidBrush(color);
            e.Graphics.FillRectangle(b, new System.Drawing.Rectangle(System.Drawing.Point.Empty, e.Item.Size));
        }

        protected override void OnRenderItemText(System.Windows.Forms.ToolStripItemTextRenderEventArgs e)
        {
            // Respect per-item ForeColor (green for active, grey for inactive)
            e.TextColor = e.Item.ForeColor != System.Drawing.Color.Empty
                          && e.Item.ForeColor != System.Drawing.SystemColors.ControlText
                        ? e.Item.ForeColor
                        : System.Drawing.Color.FromArgb(230, 237, 243);
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
