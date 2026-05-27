using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using WinForms = System.Windows.Forms;
using MasselGUARD.Models;
using MasselGUARD.Services;

namespace MasselGUARD
{
    public partial class App : System.Windows.Application
    {
        /// <summary>
        /// Set to true immediately before Application.Shutdown() so the unhandled-exception
        /// handler can suppress all teardown noise (InvalidCastException, ResourceReference-
        /// KeyNotFoundException, etc.) that WPF fires while closing windows and unloading
        /// ResourceDictionaries. These are harmless artifacts, never real bugs.
        /// </summary>
        internal static bool IsShuttingDown;

        private WinForms.NotifyIcon?       _trayIcon;
        private WinForms.ContextMenuStrip? _trayMenu;
        private WinForms.ToolStripMenuItem? _tunnelMenuHeader;
        private WinForms.ToolStripMenuItem? _trayShowItem;
        private WinForms.ToolStripMenuItem? _trayExitItem;
        private MainWindow? _mainWindow;
        private Mutex?      _instanceMutex;
        private bool        _lastSystemDark = true;

        // ── System theme change handling ──────────────────────────────────────

        /// <summary>
        /// Reacts to a Windows dark ↔ light mode transition.
        /// Must be called on the UI / Dispatcher thread.
        /// </summary>
        private void OnSystemThemeChanged(bool isDark)
        {
            // __system__ theme always follows the current Windows dark/light mode.
            if (ThemeManager.Instance.CurrentThemeName is "__system__" or "system")
            {
                ThemeManager.Instance.LoadSystem(isDark);
                return;
            }

            var cfg = MasselGUARD.MainWindow.GetConfigStatic();
            if (cfg == null) return;

            // AutoTheme: switch to the user-configured dark or light theme.
            if (cfg.AutoTheme)
            {
                var target = isDark ? cfg.ActiveDarkTheme : cfg.ActiveLightTheme;
                if (!string.IsNullOrEmpty(target) && target != ThemeManager.Instance.CurrentThemeName)
                {
                    ThemeManager.Instance.Load(target);
                    cfg.ActiveTheme = target;
                }
            }
        }

        /// <summary>Polling fallback — detects dark/light changes if the event fires late or is missed.</summary>
        private void PollSystemTheme()
        {
            bool isDark = ThemeManager.GetSystemIsDark();
            if (isDark == _lastSystemDark) return;
            _lastSystemDark = isDark;
            OnSystemThemeChanged(isDark);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Global exception handler — show error instead of silent crash
            DispatcherUnhandledException += (_, ex) =>
            {
                // During shutdown, suppress all dispatcher exceptions.
                // WPF's teardown fires FindResource / DynamicResource lookups against
                // already-unloaded ResourceDictionaries, producing InvalidCastException
                // (MS.Internal.NamedObject cast to Style/Brush) and
                // ResourceReferenceKeyNotFoundException. Both are harmless teardown noise.
                if (IsShuttingDown ||
                    ex.Exception is System.Windows.ResourceReferenceKeyNotFoundException)
                {
                    ex.Handled = true;
                    return;
                }

                System.Windows.MessageBox.Show(
                    $"Unhandled error:\n\n{ex.Exception.GetType().Name}: {ex.Exception.Message}\n\n{ex.Exception.StackTrace?.Split('\n').FirstOrDefault()}",
                    "MasselGUARD — Unexpected Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                ex.Handled = true;
            };

            // ── 1. Load language immediately — needed by all dialogs below ───
            // ── 1. Load language and theme from persisted config ──────────────
            {
                var bootCfg = new Services.ConfigService();
                bootCfg.Load();

                // ── Emergency reset (Shift held at startup) ──────────────────────
                // Useful if a bad font choice (e.g. Wingdings) or custom theme makes
                // the UI unreadable.  Detect the key press immediately (before any
                // windows open), reset and save to disk, then show the confirmation
                // *after* the theme is loaded so the dialog uses the correct palette.
                bool shiftHeld       = (WinForms.Control.ModifierKeys & WinForms.Keys.Shift) != 0;
                bool shiftFontReset  = false;
                bool shiftThemeReset = false;

                if (shiftHeld)
                {
                    if (bootCfg.Config.FontOverrideEnabled)
                    {
                        bootCfg.Config.FontOverrideEnabled = false;
                        bootCfg.Config.FontOverrideFamily  = "";
                        bootCfg.Config.FontOverrideSize    = 0.0;
                        shiftFontReset = true;
                    }
                    if (bootCfg.Config.UseCustomTheme || bootCfg.Config.SystemThemeMode != "auto")
                    {
                        bootCfg.Config.UseCustomTheme  = false;
                        bootCfg.Config.SystemThemeMode = "auto";
                        shiftThemeReset = true;
                    }
                    if (shiftFontReset || shiftThemeReset)
                        bootCfg.Save();
                }

                // Use explicit load with fallback to ensure lang files are found
                string langCode = bootCfg.Config.Language ?? "en";
                var langFile = System.IO.Path.Combine(AppContext.BaseDirectory, "lang", langCode + ".json");
                if (!System.IO.File.Exists(langFile))
                    langFile = System.IO.Path.Combine(AppContext.BaseDirectory, "lang", "en.json");
                if (System.IO.File.Exists(langFile))
                    Lang.Instance.Load(langCode);

                if (shiftThemeReset)
                    ThemeManager.Instance.LoadSystem(ThemeManager.GetSystemIsDark());
                else
                    ThemeManager.Instance.Load(bootCfg.Config.ActiveTheme ?? "default-dark");

                // Show the reset confirmation now that theme resources are loaded
                if (shiftFontReset || shiftThemeReset)
                {
                    var parts = new System.Collections.Generic.List<string>();
                    if (shiftFontReset)  parts.Add("• Font override reset to system UI font");
                    if (shiftThemeReset) parts.Add("• Custom theme reverted to Windows system colours (auto mode)");
                    ShowThemedInfo(
                        "MasselGUARD — Emergency Reset",
                        string.Join("\n", parts) + "\n\nThis was triggered by holding Shift at startup.");
                }
            }
            ThemeManager.Instance.ThemeChanged += OnThemeChanged;

            // ── 1c. System dark/light tracking ───────────────────────────────
            // Seed from the real current state so the first poll doesn't fire a
            // spurious change if the user is in light mode (field defaults to true).
            _lastSystemDark = ThemeManager.GetSystemIsDark();

            // React immediately when Windows flips dark ↔ light mode.
            // UserPreferenceChanged fires (on a thread-pool thread) when the shell
            // broadcasts WM_SETTINGCHANGE / WM_SYSCOLORCHANGE — marshal to Dispatcher.
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
                bool nowDark = ThemeManager.GetSystemIsDark();
                if (nowDark == _lastSystemDark) return;
                _lastSystemDark = nowDark;
                Dispatcher.BeginInvoke(() => OnSystemThemeChanged(nowDark));
            };

            // 5-second poll as a belt-and-braces backup (some Windows builds
            // deliver the colour-change message late or not at all to WPF).
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
                // Retry acquiring the mutex for up to 3 s regardless of whether a
                // real process is visible.  This covers two scenarios:
                //   (1) Orphaned mutex — old process crashed without releasing it.
                //   (2) Update-installer — the installer closes the running instance
                //       and immediately launches the new one before the old process
                //       has fully exited and released the mutex.
                for (int i = 0; i < 6 && !isNewInstance; i++)
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

                if (!isNewInstance && RealInstanceExists())
                {
                    ShowAlreadyRunning();
                    Shutdown();
                    return;
                }
                // Acquired after wait (orphaned mutex or update scenario) — continue normally
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
            _trayMenu.ShowImageMargin = false;
            _trayMenu.ShowCheckMargin = false;
            _trayMenu.Renderer = new DarkMenuRenderer();

            _trayShowItem = new WinForms.ToolStripMenuItem(Lang.T("TrayShowWindow"));
            _trayShowItem.Font   = GetTrayFont(bold: true);
            _trayShowItem.Image  = DrawMenuIcon(MenuIconKind.Window);
            _trayShowItem.Click += (_, _) => ShowMainWindow();
            _trayMenu.Items.Add(_trayShowItem);
            _trayMenu.Items.Add(new WinForms.ToolStripSeparator());

            // Tunnel submenu placeholder — rebuilt lazily by RebuildTrayTunnelMenu on Opening
            _tunnelMenuHeader = new WinForms.ToolStripMenuItem(Lang.T("TrayTunnels"));
            _tunnelMenuHeader.Font  = GetTrayFont(bold: true);
            _tunnelMenuHeader.Image = DrawMenuIcon(MenuIconKind.ShieldOff);
            _trayMenu.Items.Add(_tunnelMenuHeader);
            _trayMenu.Items.Add(new WinForms.ToolStripSeparator());

            _trayExitItem = new WinForms.ToolStripMenuItem(Lang.T("TrayExit"));
            _trayExitItem.Font  = GetTrayFont();
            _trayExitItem.Image = DrawMenuIcon(MenuIconKind.Exit);
            _trayExitItem.Click += (_, _) =>
            {
                if (_mainWindow == null) { _trayIcon!.Visible = false; Shutdown(); return; }
                _mainWindow.Dispatcher.Invoke(() =>
                {
                    var activeTunnels = _mainWindow._vm.TunnelList
                        .Where(t => t.IsActive).ToList();

                    // No active tunnels — exit immediately without asking
                    if (activeTunnels.Count == 0)
                    {
                        _trayIcon!.Visible = false;
                        Shutdown();
                        return;
                    }

                    // Active tunnels present — confirm only when ConfirmOnClose is set
                    bool needsConfirm = _mainWindow.ConfigSvc.Config.ConfirmOnClose;
                    if (needsConfirm)
                    {
                        string plural = activeTunnels.Count == 1 ? "" : "s";
                        bool doExit = _mainWindow.ShowThemedYesNo(
                            $"There {(activeTunnels.Count == 1 ? "is" : "are")} {activeTunnels.Count} active tunnel{plural}.\n\nDisconnect and exit MasselGUARD?",
                            "Exit MasselGUARD");
                        if (!doExit) return;
                    }

                    // Disconnect all active tunnels then exit
                    foreach (var t in activeTunnels)
                        t.DisconnectCommand.Execute(null);
                    _mainWindow._vm.RefreshTunnelStatus();

                    _trayIcon!.Visible = false;
                    Shutdown();
                });
            };
            _trayMenu.Items.Add(_trayExitItem);

            _trayIcon.ContextMenuStrip = _trayMenu;
            _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

            // Rebuild the grouped tunnel list lazily whenever the menu opens
            _trayMenu.Opening += (_, _) => RebuildTrayTunnelMenu();

            // Keep static menu item labels in sync with the active language
            Lang.Instance.LanguageChanged += (_, _) => UpdateTrayMenuLanguage();
        }

        private void UpdateTrayMenuLanguage()
        {
            if (_trayShowItem    != null) _trayShowItem.Text    = Lang.T("TrayShowWindow");
            if (_tunnelMenuHeader != null) _tunnelMenuHeader.Text = Lang.T("TrayTunnels");
            if (_trayExitItem    != null) _trayExitItem.Text    = Lang.T("TrayExit");
        }

        private enum MenuIconKind { ShieldOff, ShieldOn, Window, Exit }

        private static System.Drawing.Bitmap DrawMenuIcon(MenuIconKind kind)
        {
            const int S = 16;
            var bmp = new System.Drawing.Bitmap(S, S,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            System.Drawing.Color Res(string key, System.Drawing.Color fb)
            {
                var v = System.Windows.Application.Current?.Resources[key];
                if (v is System.Windows.Media.Color mc)
                    return System.Drawing.Color.FromArgb(mc.A, mc.R, mc.G, mc.B);
                if (v is System.Windows.Media.SolidColorBrush scb)
                    return System.Drawing.Color.FromArgb(
                        scb.Color.A, scb.Color.R, scb.Color.G, scb.Color.B);
                return fb;
            }

            float X(float x) => x * S / 16f;
            float Y(float y) => y * S / 16f;

            switch (kind)
            {
                case MenuIconKind.ShieldOff:
                case MenuIconKind.ShieldOn:
                {
                    var fill = kind == MenuIconKind.ShieldOn
                        ? Res("Success",    System.Drawing.Color.FromArgb(34,197,94))
                        : Res("BorderColor",System.Drawing.Color.FromArgb(71,85,105));
                    var shield = new System.Drawing.Drawing2D.GraphicsPath();
                    shield.AddLine(X(8),Y(1), X(14),Y(3.5f));
                    shield.AddLine(X(14),Y(3.5f), X(14),Y(9));
                    shield.AddBezier(X(14),Y(9), X(14),Y(13), X(11),Y(15), X(8),Y(16));
                    shield.AddBezier(X(8),Y(16), X(5),Y(15), X(2),Y(13), X(2),Y(9));
                    shield.AddLine(X(2),Y(9), X(2),Y(3.5f));
                    shield.CloseFigure();
                    using (var b = new System.Drawing.SolidBrush(fill)) g.FillPath(b, shield);
                    if (kind == MenuIconKind.ShieldOn)
                    {
                        using var p = new System.Drawing.Pen(System.Drawing.Color.White, X(1.8f))
                            { StartCap = System.Drawing.Drawing2D.LineCap.Round,
                              EndCap   = System.Drawing.Drawing2D.LineCap.Round,
                              LineJoin = System.Drawing.Drawing2D.LineJoin.Round };
                        g.DrawLines(p, new[] {
                            new System.Drawing.PointF(X(5),Y(8.5f)),
                            new System.Drawing.PointF(X(7.5f),Y(11.5f)),
                            new System.Drawing.PointF(X(11.5f),Y(6)) });
                    }
                    shield.Dispose();
                    break;
                }
                case MenuIconKind.Window:
                {
                    var col  = Res("Accent", System.Drawing.Color.FromArgb(96,165,250));
                    var col2 = Res("TextMuted", System.Drawing.Color.FromArgb(100,116,139));
                    // Window frame
                    using var pen = new System.Drawing.Pen(col, 1.2f);
                    g.DrawRectangle(pen, X(1.5f), Y(1.5f), X(13), Y(13));
                    // Title bar
                    using var tb = new System.Drawing.SolidBrush(col);
                    g.FillRectangle(tb, X(1.5f), Y(1.5f), X(13), Y(3.5f));
                    // Three dots
                    using var dot = new System.Drawing.SolidBrush(System.Drawing.Color.White);
                    g.FillEllipse(dot, X(3), Y(2.2f), X(1.4f), Y(1.4f));
                    g.FillEllipse(dot, X(5.2f), Y(2.2f), X(1.4f), Y(1.4f));
                    g.FillEllipse(dot, X(7.4f), Y(2.2f), X(1.4f), Y(1.4f));
                    break;
                }
                case MenuIconKind.Exit:
                {
                    var col = Res("ErrorColor", System.Drawing.Color.FromArgb(247,129,102));
                    using var pen = new System.Drawing.Pen(col, 1.5f)
                        { StartCap = System.Drawing.Drawing2D.LineCap.Round,
                          EndCap   = System.Drawing.Drawing2D.LineCap.Round };
                    // Arrow pointing right out of a box
                    g.DrawLine(pen, X(7), Y(3), X(7), Y(13));
                    g.DrawLine(pen, X(2), Y(3), X(7), Y(3));
                    g.DrawLine(pen, X(2), Y(13), X(7), Y(13));
                    g.DrawLine(pen, X(2), Y(3), X(2), Y(13));
                    // Arrow
                    g.DrawLine(pen, X(9), Y(8), X(14), Y(8));
                    g.DrawLine(pen, X(11.5f), Y(5.5f), X(14), Y(8));
                    g.DrawLine(pen, X(11.5f), Y(10.5f), X(14), Y(8));
                    break;
                }
            }
            return bmp;
        }

        public void ShutdownApp()
        {
            IsShuttingDown     = true;
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

        private Views.ToastWindow? _activeToast;
        private string _lastToastKey    = "";
        private int    _lastActiveCount = 0;  // preserved across theme changes

        public void ShowTrayNotification(Views.ToastNotification n)
        {
            // Deduplicate: ignore identical notification fired within 1 second
            string key = $"{n.Category}|{n.Primary}|{n.Secondary}";
            if (key == _lastToastKey) return;
            _lastToastKey = key;
            var resetTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromSeconds(1) };
            resetTimer.Tick += (_, _) => { _lastToastKey = ""; resetTimer.Stop(); };
            resetTimer.Start();

            Dispatcher.Invoke(() =>
            {
                try
                {
                    if (_activeToast != null)
                    {
                        try { _activeToast.Close(); } catch { }
                        _activeToast = null;
                    }
                    var toast = new Views.ToastWindow(n);
                    _activeToast = toast;
                    toast.Closed += (_, _) =>
                    {
                        if (_activeToast == toast) _activeToast = null;
                    };
                    toast.Show();
                }
                catch { }
            });
        }

        // Convenience overload for plain text (legacy callers)
        public void ShowTrayNotification(string title, string body, int durationMs = 5000)
            => ShowTrayNotification(new Views.ToastNotification
            {
                Category  = title,
                Primary   = body,
                DurationMs = durationMs,
            });

        public void UpdateTrayStatus(string tunnelName, int activeCount)
        {
            if (_trayIcon == null) return;
            _lastActiveCount = activeCount;   // remember for theme-change redraws
            var appName = ThemeManager.Instance.Current.AppName;
            _trayIcon.Text = activeCount > 0 ? Lang.T("TrayActive", tunnelName) : appName;
            _trayIcon.Icon = GetTrayIcon(activeCount);

            // Update tunnel header shield to reflect active state
            if (_tunnelMenuHeader != null)
                _tunnelMenuHeader.Image = DrawMenuIcon(
                    activeCount > 0 ? MenuIconKind.ShieldOn : MenuIconKind.ShieldOff);
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
            // Redraw icon and menu with current active-count so state is preserved
            _trayIcon.Icon = GetTrayIcon(_lastActiveCount);
            if (_tunnelMenuHeader != null)
                _tunnelMenuHeader.Image = DrawMenuIcon(
                    _lastActiveCount > 0 ? MenuIconKind.ShieldOn : MenuIconKind.ShieldOff);
            ApplyTrayMenuTheme();
            // Re-apply fonts so a font-override change saved from Settings takes effect
            ApplyTrayFonts();
        }

        /// <summary>
        /// Returns a System.Drawing.Font that respects the user's font-override settings.
        /// Falls back to "Segoe UI" at the given default size when no override is active.
        /// Tray fonts use the same family as the app font but 3 pt smaller by default,
        /// mirroring the Tiny tier (base − 2) at a size that fits WinForms menus.
        /// </summary>
        private System.Drawing.Font GetTrayFont(bool bold = false, float defaultSize = 9f)
        {
            string family = "Segoe UI";
            float  size   = defaultSize;

            var cfg = _mainWindow?.ConfigSvc?.Config;
            if (cfg?.FontOverrideEnabled == true)
            {
                if (!string.IsNullOrWhiteSpace(cfg.FontOverrideFamily))
                    family = cfg.FontOverrideFamily;
                if (cfg.FontOverrideSize > 0)
                    size = Math.Max(7f, (float)cfg.FontOverrideSize - 2f); // Tiny tier
            }

            var style = bold
                ? System.Drawing.FontStyle.Bold
                : System.Drawing.FontStyle.Regular;

            try   { return new System.Drawing.Font(family, size, style); }
            catch { return new System.Drawing.Font("Segoe UI", size,    style); }
        }

        /// <summary>Re-applies the current font override to the static tray menu items.</summary>
        private void ApplyTrayFonts()
        {
            if (_trayShowItem     != null) _trayShowItem.Font     = GetTrayFont(bold: true);
            if (_tunnelMenuHeader != null) _tunnelMenuHeader.Font = GetTrayFont(bold: true);
            if (_trayExitItem     != null) _trayExitItem.Font     = GetTrayFont();
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

        private void RebuildTrayTunnelMenu()
        {
            if (_tunnelMenuHeader == null || _mainWindow == null) return;
            _tunnelMenuHeader.DropDownItems.Clear();

            // Gather tunnel + group data on the WPF UI thread
            List<ViewModels.TunnelEntryViewModel> allTunnels = new();
            List<Models.TunnelGroup> groups = new();
            _mainWindow.Dispatcher.Invoke(() =>
            {
                allTunnels = _mainWindow._vm.TunnelList.ToList();
                groups     = _mainWindow.ConfigSvc.Config.TunnelGroups.ToList();
            });

            // Read theme colours — handles Drawing.Color (Tray* keys), Media.Color (C.* keys),
            // and SolidColorBrush (named brush keys like Accent, TextMuted).
            System.Drawing.Color GetColor(string key, System.Drawing.Color fb)
            {
                try
                {
                    var v = System.Windows.Application.Current?.Resources[key];
                    if (v is System.Drawing.Color dc)                        return dc;  // Theme.Tray* stored as System.Drawing.Color
                    if (v is System.Windows.Media.Color mc)
                        return System.Drawing.Color.FromArgb(mc.A, mc.R, mc.G, mc.B);
                    if (v is System.Windows.Media.SolidColorBrush scb)
                        return System.Drawing.Color.FromArgb(
                            scb.Color.A, scb.Color.R, scb.Color.G, scb.Color.B);
                }
                catch { }
                return fb;
            }

            var accentColor = GetColor("Accent",             System.Drawing.Color.FromArgb( 88, 166, 255));
            var mutedColor  = GetColor("TextMuted",          System.Drawing.Color.FromArgb(110, 118, 129));
            var bgColor     = GetColor("Theme.TrayBgColor",  System.Drawing.Color.FromArgb( 22,  27,  34));
            var txtColor    = GetColor("Theme.TrayTextColor",System.Drawing.Color.FromArgb(230, 237, 243));

            var activeTunnels = allTunnels.Where(t => t.IsActive).ToList();

            // ── "Disconnect All" — visible only when at least one tunnel is active ──
            if (activeTunnels.Count > 0)
            {
                var disconnectAll = new WinForms.ToolStripMenuItem(Lang.T("TrayDisconnectAll"));
                disconnectAll.Font      = GetTrayFont(bold: true);
                disconnectAll.ForeColor = accentColor;
                disconnectAll.Click += (_, _) =>
                {
                    _mainWindow?.Dispatcher.Invoke(() =>
                    {
                        foreach (var t in _mainWindow._vm.TunnelList.Where(t2 => t2.IsActive).ToList())
                            t.DisconnectCommand.Execute(null);
                        _mainWindow._vm.RefreshTunnelStatus();
                    });
                };
                _tunnelMenuHeader.DropDownItems.Add(disconnectAll);
                _tunnelMenuHeader.DropDownItems.Add(new WinForms.ToolStripSeparator());
            }

            // ── Empty state ───────────────────────────────────────────────────────
            if (allTunnels.Count == 0)
            {
                var none = new WinForms.ToolStripMenuItem(Lang.T("TrayNoTunnels"));
                none.Font      = new System.Drawing.Font("Segoe UI", 9f);
                none.ForeColor = mutedColor;
                none.Enabled   = false;
                _tunnelMenuHeader.DropDownItems.Add(none);
                return;
            }

            // ── Groups as fly-out submenus ────────────────────────────────────────
            bool hasGroupedTunnels = groups.Count > 0
                && allTunnels.Any(t => !string.IsNullOrEmpty(t.Group)
                                       && groups.Any(g => g.Name == t.Group));

            foreach (var grp in groups)
            {
                var tunnelsInGroup = allTunnels.Where(t => t.Group == grp.Name).ToList();
                if (tunnelsInGroup.Count == 0) continue;   // skip empty groups

                bool groupHasActive = tunnelsInGroup.Any(t => t.IsActive);

                // Group item — opens a submenu listing its tunnels
                var groupItem = new WinForms.ToolStripMenuItem(grp.Name);
                groupItem.Font      = GetTrayFont(bold: true);
                groupItem.ForeColor = groupHasActive ? accentColor : txtColor;
                groupItem.Image     = MakeStatusDot(groupHasActive ? accentColor : mutedColor);

                // Apply dark renderer + correct bg to the fly-out — without this the
                // submenu uses the Windows system renderer and looks completely different.
                ApplyDropDownStyle(groupItem, bgColor);

                foreach (var tunnel in tunnelsInGroup)
                    AddTunnelItem(groupItem.DropDownItems, tunnel, accentColor, mutedColor);

                _tunnelMenuHeader.DropDownItems.Add(groupItem);
            }

            // ── Ungrouped tunnels ─────────────────────────────────────────────────
            var ungrouped = allTunnels
                .Where(t => string.IsNullOrEmpty(t.Group)
                            || !groups.Any(g => g.Name == t.Group))
                .ToList();

            if (ungrouped.Count > 0)
            {
                if (hasGroupedTunnels)
                {
                    // When groups exist, ungrouped gets its own submenu too
                    bool ungroupedHasActive = ungrouped.Any(t => t.IsActive);
                    var ungroupedItem = new WinForms.ToolStripMenuItem(Lang.T("TrayUngrouped"));
                    ungroupedItem.Font      = GetTrayFont(bold: true);
                    ungroupedItem.ForeColor = ungroupedHasActive ? accentColor : txtColor;
                    ungroupedItem.Image     = MakeStatusDot(ungroupedHasActive ? accentColor : mutedColor);

                    ApplyDropDownStyle(ungroupedItem, bgColor);

                    foreach (var tunnel in ungrouped)
                        AddTunnelItem(ungroupedItem.DropDownItems, tunnel, accentColor, mutedColor);

                    // Separator before the ungrouped submenu when groups are present
                    if (groups.Any(g => allTunnels.Any(t => t.Group == g.Name)))
                        _tunnelMenuHeader.DropDownItems.Add(new WinForms.ToolStripSeparator());

                    _tunnelMenuHeader.DropDownItems.Add(ungroupedItem);
                }
                else
                {
                    // No groups at all — show tunnels as a flat list
                    foreach (var tunnel in ungrouped)
                        AddTunnelItem(_tunnelMenuHeader.DropDownItems, tunnel, accentColor, mutedColor);
                }
            }

            // Keep tray icon badge in sync
            if (_trayIcon != null)
                _trayIcon.Icon = GetTrayIcon(activeTunnels.Count);
        }

        /// <summary>
        /// Applies the DarkMenuRenderer and correct background colour to a submenu's DropDown,
        /// preventing WinForms from painting it with the Windows system colours.
        /// </summary>
        private static void ApplyDropDownStyle(
            WinForms.ToolStripMenuItem item,
            System.Drawing.Color bgColor)
        {
            item.DropDown.Renderer  = new DarkMenuRenderer();
            item.DropDown.BackColor = bgColor;
            if (item.DropDown is WinForms.ToolStripDropDownMenu ddm)
            {
                ddm.ShowImageMargin = false;
                ddm.ShowCheckMargin = false;
            }
        }

        private void AddTunnelItem(
            WinForms.ToolStripItemCollection items,
            ViewModels.TunnelEntryViewModel tunnel,
            System.Drawing.Color accentColor,
            System.Drawing.Color mutedColor)
        {
            var item = new WinForms.ToolStripMenuItem(tunnel.Name);

            if (tunnel.IsActive)
            {
                item.Font      = GetTrayFont(bold: true);
                item.ForeColor = accentColor;
                item.Image     = MakeStatusDot(accentColor);
            }
            else
            {
                item.Font      = GetTrayFont();
                item.ForeColor = mutedColor;
                item.Image     = MakeStatusDot(mutedColor);
            }

            var capture = tunnel;
            item.Click += (_, _) =>
            {
                _mainWindow?.Dispatcher.Invoke(() =>
                {
                    if (capture.IsActive)
                        capture.DisconnectCommand.Execute(null);
                    else
                        capture.ConnectCommand.Execute(null);
                    _mainWindow._vm.RefreshTunnelStatus();
                });
            };

            items.Add(item);
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

        /// <summary>
        /// Shows a simple one-button info dialog themed to match the current dark/light palette.
        /// Must be called after ThemeManager has loaded a theme so resource colours are available.
        /// </summary>
        private static void ShowThemedInfo(string title, string message)
        {
            // Read colours from the already-loaded theme resources
            System.Windows.Media.Color Clr(string key, System.Windows.Media.Color fb)
            {
                var v = Application.Current?.Resources[key];
                if (v is System.Windows.Media.SolidColorBrush b) return b.Color;
                if (v is System.Windows.Media.Color c)           return c;
                return fb;
            }

            var bg      = Clr("WindowBg",    System.Windows.Media.Color.FromRgb( 28,  28,  28));
            var surface = Clr("Surface",     System.Windows.Media.Color.FromRgb( 44,  44,  44));
            var border  = Clr("BorderColor", System.Windows.Media.Color.FromRgb( 61,  61,  61));
            var accent  = Clr("Accent",      System.Windows.Media.Color.FromRgb(  0, 120, 212));
            var textC   = Clr("TextPrimary", System.Windows.Media.Color.FromRgb(238, 238, 238));
            var cardBg  = Clr("CardBg",      System.Windows.Media.Color.FromRgb( 37,  37,  37));
            var hovC    = Clr("Highlight",   System.Windows.Media.Color.FromRgb( 48,  48,  48));

            var cr = Application.Current?.Resources["Theme.CornerRadius"] is CornerRadius r ? r : new CornerRadius(0);
            var ff = Application.Current?.Resources["Theme.FontFamily"]   is System.Windows.Media.FontFamily f
                     ? f : new System.Windows.Media.FontFamily("Segoe UI");

            System.Windows.Media.Brush Br(System.Windows.Media.Color c) =>
                new System.Windows.Media.SolidColorBrush(c);

            // Title bar
            var titleBar = new System.Windows.Controls.Border
            {
                Background = Br(surface),
                Height     = 40,
                Child      = new System.Windows.Controls.TextBlock
                {
                    Text              = title,
                    FontFamily        = ff,
                    FontSize          = 12,
                    FontWeight        = FontWeights.Bold,
                    Foreground        = Br(accent),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(14, 0, 0, 0),
                },
            };

            // Message body
            var msgBlock = new System.Windows.Controls.TextBlock
            {
                Text         = message,
                FontFamily   = ff,
                FontSize     = 11,
                Foreground   = Br(textC),
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 20),
            };

            // OK button (themed Border + TextBlock — avoids default Button chrome)
            var okTb = new System.Windows.Controls.TextBlock
            {
                Text                = "OK",
                FontFamily          = ff,
                FontSize            = 11,
                FontWeight          = FontWeights.SemiBold,
                Foreground          = Br(textC),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };
            var okBtn = new System.Windows.Controls.Border
            {
                Background          = Br(cardBg),
                BorderBrush         = Br(border),
                BorderThickness     = new Thickness(1),
                CornerRadius        = cr,
                Padding             = new Thickness(28, 6, 28, 6),
                Cursor              = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                Child               = okTb,
            };
            okBtn.MouseEnter += (_, _) => okBtn.Background = Br(hovC);
            okBtn.MouseLeave += (_, _) => okBtn.Background = Br(cardBg);

            var body = new System.Windows.Controls.StackPanel
            {
                Margin = new Thickness(20, 18, 20, 18),
            };
            body.Children.Add(msgBlock);
            body.Children.Add(okBtn);

            var grid = new System.Windows.Controls.Grid();
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
            System.Windows.Controls.Grid.SetRow(titleBar, 0);
            System.Windows.Controls.Grid.SetRow(body,     1);
            grid.Children.Add(titleBar);
            grid.Children.Add(body);

            var wrapper = new System.Windows.Controls.Border
            {
                Background      = Br(bg),
                BorderBrush     = Br(border),
                BorderThickness = new Thickness(1),
                CornerRadius    = cr,
                Child           = grid,
            };

            Window? win = null;
            win = new Window
            {
                Title                 = title,
                Width                 = 400,
                SizeToContent         = SizeToContent.Height,
                WindowStyle           = WindowStyle.None,
                AllowsTransparency    = true,
                Background            = System.Windows.Media.Brushes.Transparent,
                ResizeMode            = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Content               = wrapper,
            };

            titleBar.MouseLeftButtonDown += (_, ev) =>
            {
                if (ev.LeftButton == System.Windows.Input.MouseButtonState.Pressed) win!.DragMove();
            };
            okBtn.MouseLeftButtonUp += (_, _) => win!.Close();

            win.ShowDialog();
        }

        private void ShowAlreadyRunning()
        {
            // The theme is loaded before the single-instance check, so
            // Application.Current.Resources already holds the correct colours.
            System.Windows.Media.Color Clr(string key, System.Windows.Media.Color fb)
            {
                var v = Application.Current?.Resources[key];
                if (v is System.Windows.Media.SolidColorBrush b) return b.Color;
                if (v is System.Windows.Media.Color c)           return c;
                return fb;
            }

            var bg      = Clr("WindowBg",    System.Windows.Media.Color.FromRgb(13,  17,  23));
            var panel   = Clr("Surface",     System.Windows.Media.Color.FromRgb(22,  27,  34));
            var border  = Clr("BorderColor", System.Windows.Media.Color.FromRgb(48,  54,  61));
            var accent  = Clr("Accent",      System.Windows.Media.Color.FromRgb(88, 166, 255));
            var textC   = Clr("TextPrimary", System.Windows.Media.Color.FromRgb(230, 237, 243));
            var subC    = Clr("TextMuted",   System.Windows.Media.Color.FromRgb(139, 148, 158));
            var warn    = Clr("Danger",      System.Windows.Media.Color.FromRgb(247, 129, 102));
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

            var bgExit  = Clr("CardBg",    System.Windows.Media.Color.FromRgb(36, 41, 51));
            var hovExit = Clr("Highlight", System.Windows.Media.Color.FromRgb(55, 62, 76));
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

            // Read theme colours — Accent (idle), Success (active), TextMuted (idle rim)
            // WPF resources are Colors; convert to System.Drawing.Color
            System.Drawing.Color ToDrawing(string key, System.Drawing.Color fallback)
            {
                try
                {
                    var res = System.Windows.Application.Current?.Resources[key];
                    if (res is System.Windows.Media.Color mc)
                        return System.Drawing.Color.FromArgb(mc.A, mc.R, mc.G, mc.B);
                    if (res is System.Windows.Media.SolidColorBrush scb)
                        return System.Drawing.Color.FromArgb(
                            scb.Color.A, scb.Color.R, scb.Color.G, scb.Color.B);
                }
                catch { }
                return fallback;
            }

            bool active = activeCount > 0;

            // Always the same dark shield — chevron colour is the only thing that changes,
            // mirroring the ShieldChevronBrush logic in the main window title bar.
            var colShieldFill = ToDrawing("CardBg",      System.Drawing.Color.FromArgb( 22,  27,  34));
            var colShieldRim  = ToDrawing("BorderColor", System.Drawing.Color.FromArgb( 48,  54,  61));
            var colChevronOn  = ToDrawing("Accent",      System.Drawing.Color.FromArgb( 88, 166, 255)); // connected
            var colChevronOff = ToDrawing("TextMuted",   System.Drawing.Color.FromArgb(110, 118, 129)); // idle

            // ── Shield path ─────────────────────────────────────────────────
            var shield = new System.Drawing.Drawing2D.GraphicsPath();
            shield.AddLine   (X(12), Y(1),    X(22), Y(5));
            shield.AddLine   (X(22), Y(5),    X(22), Y(13));
            shield.AddBezier (X(22), Y(13),   X(22), Y(18.5f), X(17.5f), Y(22.5f), X(12), Y(24));
            shield.AddBezier (X(12), Y(24),   X(6.5f), Y(22.5f), X(2), Y(18.5f),  X(2),  Y(13));
            shield.AddLine   (X(2),  Y(13),   X(2),  Y(5));
            shield.CloseFigure();

            // Same dark shield in both states
            using (var fill = new System.Drawing.SolidBrush(colShieldFill))
                g.FillPath(fill, shield);
            using (var rim = new System.Drawing.Pen(colShieldRim, X(1.2f)))
                g.DrawPath(rim, shield);

            // Chevron: grey when no connection, Accent colour when connected
            using var pen = new System.Drawing.Pen(active ? colChevronOn : colChevronOff, X(2.2f))
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap   = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
            };
            var chevron = new System.Drawing.PointF[]
            {
                new(X(7f),  Y(9.5f)),
                new(X(12f), Y(15f)),
                new(X(17f), Y(9.5f)),
            };
            g.DrawLines(pen, chevron);

            shield.Dispose();
            return bmp;
        }
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
