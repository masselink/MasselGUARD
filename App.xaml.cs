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
        private bool        _lastSystemDark = true;

        // ── System theme polling ──────────────────────────────────────────────
        private void PollSystemTheme()
        {
            var cfg = MasselGUARD.MainWindow.GetConfigStatic();
            if (cfg == null || !cfg.AutoTheme) return;

            bool isDark = ThemeManager.GetSystemIsDark();
            if (isDark == _lastSystemDark) return;
            _lastSystemDark = isDark;

            var target = isDark ? cfg.ActiveDarkTheme : cfg.ActiveLightTheme;
            if (!string.IsNullOrEmpty(target) && target != ThemeManager.Instance.CurrentThemeName)
            {
                ThemeManager.Instance.Load(target);
                cfg.ActiveTheme = target;
            }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ── 1. Load language immediately — needed by all dialogs below ───
            Lang.Instance.Load(AppConfig.LoadLanguage());

            // ── 1b. Bootstrap theme ───────────────────────────────────────────
            ThemeManager.Instance.Load(AppConfig.LoadTheme());
            ThemeManager.Instance.ThemeChanged += OnThemeChanged;

            // ── 1c. System theme auto-switch polling ─────────────────────────
            var sysPollTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5)
            };
            sysPollTimer.Tick += (_, _) => PollSystemTheme();
            sysPollTimer.Start();

            // ── 2. Single-instance check ────────────────────────────────────
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
                isNewInstance = false;
            }

            if (!isNewInstance)
            {
                // Verify a real process is running before treating as duplicate.
                // After install/move the mutex can be orphaned (old process exited
                // without releasing it). Retry up to 2 s if no real instance found.
                if (!RealInstanceExists())
                {
                    for (int i = 0; i < 4 && !isNewInstance; i++)
                    {
                        System.Threading.Thread.Sleep(500);
                        try
                        {
                            _instanceMutex?.Dispose();
                            _instanceMutex = new Mutex(
                                initiallyOwned: true,
                                name: "Global\\MasselGUARD_SingleInstance",
                                out isNewInstance);
                        }
                        catch { }
                    }
                }

                if (!isNewInstance && RealInstanceExists())
                {
                    ShowAlreadyRunning();
                    Shutdown();
                    return;
                }
                // Orphaned mutex acquired after retry — continue normally
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
                Text    = ThemeManager.Instance.Current.AppName,
                Visible = true,
                Icon    = GetTrayIcon(0)
            };

            _trayMenu = new WinForms.ContextMenuStrip();
            ApplyTrayMenuTheme();
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

        public void UpdateTrayStatus(string tunnelName, int activeCount)
        {
            if (_trayIcon == null) return;
            var appName = ThemeManager.Instance.Current.AppName;
            _trayIcon.Text = activeCount > 0 ? Lang.T("TrayActive", tunnelName) : appName;
            _trayIcon.Icon = GetTrayIcon(activeCount);
        }

        private static System.Drawing.Icon GetTrayIcon(int activeCount)
        {
            // Custom theme icon takes precedence; fall back to built-in shield with badge
            if (Application.Current.Resources["Theme.TrayIcon"] is System.Drawing.Icon custom)
                return custom;
            return TrayIconHelper.CreateIcon(activeCount);
        }

        private void OnThemeChanged(object? sender, EventArgs e)
        {
            if (_trayIcon == null) return;
            _trayIcon.Text = ThemeManager.Instance.Current.AppName;
            _trayIcon.Icon = GetTrayIcon(0);
            ApplyTrayMenuTheme();
        }

        private void ApplyTrayMenuTheme()
        {
            if (_trayMenu == null) return;
            var bg  = GetDrawingColor("Theme.TrayBgColor",  System.Drawing.Color.FromArgb(22,  27,  34));
            var txt = GetDrawingColor("Theme.TrayTextColor", System.Drawing.Color.FromArgb(230, 237, 243));
            _trayMenu.BackColor = bg;
            _trayMenu.ForeColor = txt;
        }

        private static System.Drawing.Color GetDrawingColor(string key, System.Drawing.Color fallback)
        {
            var v = Application.Current?.Resources[key];
            return v is System.Drawing.Color c ? c : fallback;
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

            // Keep icon badge in sync with the active count
            if (_trayIcon != null)
                _trayIcon.Icon = GetTrayIcon(active.Count);
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

        // ── P/Invoke helpers for bringing another window to front ─────────────
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        private const int SW_RESTORE = 9;

        /// <summary>Finds the running MasselGUARD process (not this one) and brings it to front.</summary>
        private static bool BringExistingToFront()
        {
            var current = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(current.ProcessName))
            {
                if (p.Id == current.Id) continue;
                try
                {
                    var hwnd = p.MainWindowHandle;
                    if (hwnd == IntPtr.Zero) continue;
                    ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static bool RealInstanceExists()
        {
            var current = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
                if (p.Id != current.Id) return true;
            return false;
        }

        private void ShowAlreadyRunning()
        {
            // colours — defined inline since App resources aren't loaded yet
            var bg      = System.Windows.Media.Color.FromRgb(13,  17,  23);
            var panel   = System.Windows.Media.Color.FromRgb(22,  27,  34);
            var border  = System.Windows.Media.Color.FromRgb(48,  54,  61);
            var accent  = System.Windows.Media.Color.FromRgb(88, 166, 255);
            var textC   = System.Windows.Media.Color.FromRgb(230, 237, 243);
            var subC    = System.Windows.Media.Color.FromRgb(139, 148, 158);
            var warn    = System.Windows.Media.Color.FromRgb(247, 129, 102);
            System.Windows.Media.Brush Br(System.Windows.Media.Color c) =>
                new System.Windows.Media.SolidColorBrush(c);

            // Use Border+TextBlock — WPF Button ignores Foreground via default chrome
            System.Windows.Controls.Border MakeBtn(string label,
                System.Windows.Media.Color fg, System.Windows.Media.Color bgCol,
                System.Windows.Media.Color hoverCol)
            {
                var tb = new System.Windows.Controls.TextBlock
                {
                    Text                = label,
                    FontFamily          = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize            = 11,
                    FontWeight          = FontWeights.Bold,
                    Foreground          = Br(fg),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                };
                var b = new System.Windows.Controls.Border
                {
                    Background      = Br(bgCol),
                    BorderBrush     = Br(fg),   // border matches text colour for clear contrast
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(4),
                    Padding         = new Thickness(20, 7, 20, 7),
                    Cursor          = System.Windows.Input.Cursors.Hand,
                    Child           = tb,
                };
                b.MouseEnter += (_, _) => b.Background = Br(hoverCol);
                b.MouseLeave += (_, _) => b.Background = Br(bgCol);
                return b;
            }

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

            var bgExit  = System.Windows.Media.Color.FromRgb(36, 41, 51);
            var hovExit = System.Windows.Media.Color.FromRgb(55, 62, 76);
            var exitBtn = MakeBtn(Lang.T("AlreadyRunningBtnExit"), textC, bgExit, hovExit);
            exitBtn.HorizontalAlignment = HorizontalAlignment.Right;
            stack.Children.Add(exitBtn);

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

            var wrapGrid = new System.Windows.Controls.Grid();
            wrapGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            wrapGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            System.Windows.Controls.Grid.SetRow(titleBar, 0);
            System.Windows.Controls.Grid.SetRow(stack, 1);
            wrapGrid.Children.Add(titleBar);
            wrapGrid.Children.Add(stack);

            var wrapper = new System.Windows.Controls.Border
            {
                Background      = Br(bg),
                BorderBrush     = Br(border),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Child           = wrapGrid
            };

            var win = new Window
            {
                Title                 = "MasselGUARD — Already running",
                Width                 = 460,
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

            exitBtn.MouseLeftButtonUp += (_, _) => win.Close();

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
        public static System.Drawing.Icon CreateIcon(int activeCount = 0)
        {
            const int S = 256;
            using var src = RenderIcon(S, activeCount);

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

        // ── Renderer — shield + chevron + optional badge ──────────────────
        private static System.Drawing.Bitmap RenderIcon(int S, int activeCount)
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

            // ── Chevron: colour = green when any active, blue when idle
            var wc = activeCount > 0 ? ColGreen : ColAccent;
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

            // ── Badge counter (bottom-right) — shown when activeCount >= 1
            if (activeCount > 0)
            {
                string label = activeCount > 9 ? "9+" : activeCount.ToString();

                // Badge circle: sits in the bottom-right quadrant
                float bR   = X(5.6f);                        // radius
                float bCx  = X(24) - bR - X(0.4f);           // centre X (near right edge)
                float bCy  = Y(24) - bR - Y(0.4f);           // centre Y (near bottom edge)

                // Dark outline ring so badge reads on any background
                using (var outline = new System.Drawing.SolidBrush(C(0, 0, 0, 200)))
                    g.FillEllipse(outline, bCx - bR - X(0.8f), bCy - bR - X(0.8f),
                                           (bR + X(0.8f)) * 2, (bR + X(0.8f)) * 2);

                // Green fill
                using (var fill = new System.Drawing.SolidBrush(ColGreen))
                    g.FillEllipse(fill, bCx - bR, bCy - bR, bR * 2, bR * 2);

                // Number
                float fontSize = label.Length == 1 ? X(5.8f) : X(4.6f);
                using var font = new System.Drawing.Font("Segoe UI", fontSize,
                    System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
                using var whiteBrush = new System.Drawing.SolidBrush(System.Drawing.Color.White);
                var sf = new System.Drawing.StringFormat
                {
                    Alignment     = System.Drawing.StringAlignment.Center,
                    LineAlignment = System.Drawing.StringAlignment.Center
                };
                g.DrawString(label, font, whiteBrush,
                    new System.Drawing.RectangleF(bCx - bR, bCy - bR, bR * 2, bR * 2), sf);
            }

            return bmp;
        }
    }

    // Flat dark renderer — no gradients, no bright highlights
    internal class DarkMenuRenderer : System.Windows.Forms.ToolStripRenderer
    {
        // All colours read from Application.Resources at render time so theme hot-swap works
        private static System.Drawing.Color Res(string key, System.Drawing.Color fallback)
        {
            try
            {
                if (System.Windows.Application.Current?.Resources[key] is System.Drawing.Color c)
                    return c;
            }
            catch { }
            return fallback;
        }

        private static System.Drawing.Color Bg     => Res("Theme.TrayBgColor",          System.Drawing.Color.FromArgb(22,  27,  34));
        private static System.Drawing.Color Hov    => Res("Theme.TrayHoverColor",       System.Drawing.Color.FromArgb(48,  54,  61));
        private static System.Drawing.Color Sep    => Res("Theme.TrayBorderColor",      System.Drawing.Color.FromArgb(48,  54,  61));
        private static System.Drawing.Color ImgCol => Res("Theme.TrayImageMarginColor", System.Drawing.Color.FromArgb(16,  21,  28));
        private static System.Drawing.Color Txt    => Res("Theme.TrayTextColor",        System.Drawing.Color.FromArgb(230, 237, 243));

        protected override void OnRenderToolStripBackground(System.Windows.Forms.ToolStripRenderEventArgs e)
            => e.Graphics.Clear(Bg);

        protected override void OnRenderImageMargin(System.Windows.Forms.ToolStripRenderEventArgs e)
        {
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
            e.TextColor = e.Item.ForeColor != System.Drawing.Color.Empty
                          && e.Item.ForeColor != System.Drawing.SystemColors.ControlText
                        ? e.Item.ForeColor
                        : Txt;
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
