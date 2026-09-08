using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using MasselGUARD.Infrastructure;
using MasselGUARD.Models;
using MasselGUARD.Services;
using MasselGUARD.ViewModels;

namespace MasselGUARD
{
    public partial class MainWindow : Window
    {
        // ── Services (shared across the application lifetime) ─────────────────
        internal readonly ConfigService  ConfigSvc;
        internal readonly LogService     LogSvc;
        internal readonly WiFiService    WifiSvc;
        internal readonly TunnelService  TunnelSvc;
        internal readonly RuleEngine     RuleEngine;
        internal readonly ScriptService  ScriptSvc;
        internal readonly Services.HistoryService     HistorySvc;
        internal readonly Services.KillSwitchService  KillSwitchSvc;

        // ── ViewModel ─────────────────────────────────────────────────────────
        internal readonly MainViewModel _vm;

        // ── Activity-log collapse state ───────────────────────────────────────
        private bool _logPanelVisible = true;

        // ── Column sort state ─────────────────────────────────────────────────
        private string _tunnelSortCol = "";   // "" = natural / insertion order
        private bool   _tunnelSortAsc = true;
        private string _ruleSortCol   = "";
        private bool   _ruleSortAsc   = true;

        private void LogToggle_Click(object sender, RoutedEventArgs e)
        {
            SetLogPanelVisible(!_logPanelVisible);
            // Persist so the preference survives restarts
            ConfigSvc.Config.ShowActivityLog = _logPanelVisible;
            ConfigSvc.Save();
        }

        /// <summary>Show or hide the activity log panel. Called by the title-bar toggle and by Settings.</summary>
        public void SetLogPanelVisible(bool visible)
        {
            if (_logPanelVisible == visible) return;
            _logPanelVisible = visible;

            LogPanelGrid.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            MainContentGrid.ColumnDefinitions[1].Width =
                visible ? new GridLength(10) : new GridLength(0);
            MainContentGrid.ColumnDefinitions[2].Width =
                visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

            // ☰ in the tunnel header — only visible when the log is collapsed
            LogOpenBtn.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;

            // Spread columns evenly across the new section width
            if (_colInitDone)
            {
                ResetTunnelColsToStars();
                ResetWifiColsToStars();
            }
        }

        // ── Window state (maximize / restore) ────────────────────────────────
        private bool _reallyClosing = false;

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void MaximizeBtn_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            bool maximized = WindowState == WindowState.Maximized;
            if (MaximizeBtn != null)
                MaximizeBtn.Content = maximized ? "❐" : "□";   // Restore / Maximize glyph
            if (OuterBorder != null)
            {
                OuterBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
                if (maximized)
                    OuterBorder.CornerRadius = new CornerRadius(0);
                else
                    OuterBorder.SetResourceReference(Border.CornerRadiusProperty, "Theme.CornerRadius");
            }
        }

        // ── WPF Taskbar visibility ─────────────────────────────────────────────
        private const int GWL_EXSTYLE      = -20;
        private const int WS_EX_APPWINDOW  = 0x00040000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        [DllImport("user32.dll")] private static extern int  GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] private static extern int  SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

        // ── Taskbar icon refresh ────────────────────────────────────────────────
        // Window.Icon alone updates the title bar / Alt-Tab thumbnail, but the taskbar
        // button's icon is cached from window creation and doesn't reliably re-fetch it.
        // WM_SETICON only sets the per-instance icon; Explorer's taskband re-registration
        // (triggered by the ShowInTaskbar toggle in RefreshTaskbarButtonIcon) reads the
        // WINDOW CLASS's own icon slot instead, which WM_SETICON never touches — so both
        // have to be updated for the taskbar button to actually pick up the new icon.
        private const int WM_SETICON   = 0x0080;
        private const int ICON_SMALL   = 0;
        private const int ICON_BIG     = 1;
        private const int GCLP_HICON   = -14;
        private const int GCLP_HICONSM = -34;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public MainWindow()
        {
            // ── Bootstrap services ────────────────────────────────────────────
            ConfigSvc  = new ConfigService();
            ConfigSvc.Load();

            LogSvc     = new LogService();
            LogSvc.IsExtended = ConfigSvc.Config.LogLevelSetting == "extended";

            ScriptSvc      = new ScriptService();
            HistorySvc     = new Services.HistoryService();
            HistorySvc.Load();
            HistorySvc.LoadSsid();
            KillSwitchSvc  = new Services.KillSwitchService(LogSvc);
            TunnelSvc      = new TunnelService(LogSvc, ScriptSvc, HistorySvc, KillSwitchSvc);
            WifiSvc        = new WiFiService();
            RuleEngine     = new RuleEngine();

            // ── Build ViewModel ───────────────────────────────────────────────
            _vm = new MainViewModel(ConfigSvc, TunnelSvc, LogSvc, WifiSvc, RuleEngine, HistorySvc);

            // ── Wire ViewModel → View dialog requests ─────────────────────────
            _vm.AddTunnelRequested    += OnAddTunnel;
            _vm.EditTunnelRequested   += OnEditTunnel;
            _vm.DeleteTunnelRequested += OnDeleteTunnel;
            _vm.QuickConnectRequested += OnQuickConnect;
            _vm.OpenSettingsRequested += OnOpenSettings;
            _vm.StatusTick            += OnStatusTick;

            InitializeComponent();
            DataContext = _vm;

            // Override the lang-bound title with the real assembly version so Task Manager
            // and the taskbar always show the current version without updating lang files.
            // Kept in step with the theme's app name (see UpdateWindowTitle) for the same
            // reason as the tray tooltip — Window.Title is what Alt-Tab / taskbar hover /
            // Task Manager actually read, independent of the custom in-window title bar.
            UpdateWindowTitle();

            // ── Startup ───────────────────────────────────────────────────────
            Loaded += OnLoaded;
        }

        // ── Windows message hook — live theme / accent updates ────────────────
        private const int WM_SETTINGCHANGE = 0x001A;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)
                      ?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero)
            {
                string? change = Marshal.PtrToStringAuto(lParam);
                // "ImmersiveColorSet" fires on accent-colour change and dark/light mode switch
                if (change is "ImmersiveColorSet" && ConfigSvc.Config.ActiveTheme is "__system__" or "" or null)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        ApplyThemeFromConfig();
                        // Re-colour footer labels — Accent may have changed
                        UpdateAdminLabel();
                        UpdateFooterLabel();
                    });
                }
            }
            return IntPtr.Zero;
        }

        // ── Loaded ────────────────────────────────────────────────────────────
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ForceTaskbarButton();

            // Remove any stale kill-switch firewall rules left from a previous crash
            KillSwitchSvc.CleanupStaleRules();

            // Log level from config
            LogSvc.IsExtended = ConfigSvc.Config.LogLevelSetting == "extended";

            LogSvc.Ok(Lang.T("LogAppStarted"));
            LogSvc.Info($"Started from: {Environment.ProcessPath ?? AppContext.BaseDirectory}");
            LogSvc.Debug($"OS    : {Environment.OSVersion}");
            LogSvc.Debug($".NET  : {Environment.Version}");
            LogSvc.Debug($"User  : {Environment.UserDomainName}\\{Environment.UserName}");

            // Subscribe to log service and render existing entries
            LogSvc.EntryAdded += AppendLogEntry;
            RebuildLog();

            // Restore saved log-panel visibility (default true)
            if (!ConfigSvc.Config.ShowActivityLog)
                SetLogPanelVisible(false);

            // Apply info section mode
            ApplyInfoSectionMode();

            // Restore or initialise column widths
            InitColumnWidths();

            // Update footer
            UpdateAdminLabel();
            UpdateFooterLabel();

            // Apply language-change refresh
            Lang.Instance.LanguageChanged += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _vm.RebuildTunnelList();
                UpdateAdminLabel();
                UpdateFooterLabel();
                RebuildLog();
            });

            // Theme change: rebuild tunnel list so colours/badges/status render correctly,
            // also rebuild group tabs for contrast recalculation
            ThemeManager.Instance.ThemeChanged += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                // Re-apply font override — Load() resets Theme.FontFamily from the theme
                // definition, so the override must be re-stamped after every theme change.
                ThemeManager.ApplyFontOverride(
                    ConfigSvc.Config.FontOverrideEnabled,
                    ConfigSvc.Config.FontOverrideFamily,
                    ConfigSvc.Config.FontOverrideSize);

                _vm.RebuildTunnelList();
                RebuildTunnelGroups();
                RefreshWifiRulesPanel();
                UpdateStatusBarCentre();
                UpdateFooterLabel();
                UpdateAdminLabel();
                UpdateShieldChevron();
                UpdateTaskbarIcon();
                UpdateWindowTitle();
                NotifyAllBadges();
                ApplyGroupFilter();
                RebuildLog();   // re-resolve brush colours after accent/theme change
                RefreshInfoSection();   // re-derive timeline colours from the new theme
            });

            // Theme: apply on startup based on UseCustomTheme + SystemThemeMode
            ApplyThemeFromConfig();

            // Close any history entries that are still open but whose tunnel is no longer
            // running — catches crashes, force-kills, and tunnels deleted while the app was off.
            HistorySvc.CloseStaleHistoryEntries(name =>
            {
                var stored = ConfigSvc.Config.Tunnels
                    .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                return stored != null && TunnelSvc.IsActive(stored);
            });

            // Build initial tunnel list
            _vm.RebuildTunnelList();
            RebuildTunnelGroups();
            RefreshWifiRulesPanel();
            UpdateStatusBarCentre();
            ApplyPolicyGating();

            // WiFi — single consolidated handler: label + rule evaluation
            WifiSvc.SsidChanged += OnWifiChanged;
            WifiSvc.Start();

            // Query initial state; retry with timer until SSID found
            TryUpdateWifi();
            var retryTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
            int retryCount = 0;
            retryTimer.Tick += (_, _) =>
            {
                retryCount++;
                if (WifiSvc.CurrentSsid != null || retryCount >= 5)
                    retryTimer.Stop();
                else
                    TryUpdateWifi();
            };
            retryTimer.Start();

            // Show badge immediately if a newer version was already known from a previous run
            RefreshUpdateBadge();

            // Background update check (frequency-based) — also refreshes the badge when done
            if (ShouldCheckForUpdates())
                _ = CheckForUpdatesAsync(silent: true);

            // Running the x64 build emulated on an ARM64 PC — offer a one-click switch to the
            // native ARM64 build (unless the user chose "Don't remind me"). Local tunnels can't
            // work under emulation, so this is worth surfacing proactively.
            if (!ConfigSvc.Config.ArmSwitchDismissed && TunnelDll.ArchSupportError() != null)
                _ = OfferArm64SwitchAsync();

            // Managed Portable + different version → offer to overwrite installed version
            if (AppRunMode == AppRunModeKind.ManagedPortable)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    var installed = GetInstalledPath();
                    if (installed == null) return;
                    string installedExe = System.IO.Path.Combine(installed, "MasselGUARD.exe");
                    if (!System.IO.File.Exists(installedExe)) return;
                    try
                    {
                        var installedVer = System.Diagnostics.FileVersionInfo
                            .GetVersionInfo(installedExe).FileVersion ?? "0.0.0";
                        var currentVer   = UpdateChecker.CurrentVersionString;

                        // Prompt whenever versions differ — covers newer, older, or same base with different build
                        if (NormaliseVersion(currentVer) == NormaliseVersion(installedVer)) return;

                        bool currentIsNewer = IsVersionNewer(currentVer, installedVer);
                        string msg = currentIsNewer
                            ? $"This copy of MasselGUARD (v{currentVer}) is newer than the installed version (v{installedVer}).\n\nDo you want to overwrite the installed version with this copy?"
                            : $"This copy of MasselGUARD (v{currentVer}) differs from the installed version (v{installedVer}).\n\nDo you want to overwrite the installed version with this copy?";

                        if (ShowThemedYesNo(msg, "Update installed version"))
                            RunInstallPublic();
                    }
                    catch { }
                }));

            // Portable install prompt (shown when running standalone/portable)
            if (AppRunMode == AppRunModeKind.Standalone &&
                !ConfigSvc.Config.SuppressPortableUpdatePrompt)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                    new Action(ShowPortableInstallPrompt));

            // Show wizard on first run OR when running a newer version than last wizard run
            bool isUpgrade = !ConfigSvc.IsFirstRun &&
                !string.IsNullOrEmpty(ConfigSvc.Config.LastRunVersion) &&
                UpdateChecker.IsNewerVersion(ConfigSvc.Config.LastRunVersion) == false &&
                IsVersionNewer(UpdateChecker.CurrentVersionString,
                    ConfigSvc.Config.LastRunVersion ?? "0.0.0");

            if (ConfigSvc.IsFirstRun || isUpgrade)
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    var wiz = new Views.WizardWindow(this, isUpgrade) { Owner = this };
                    wiz.ShowDialog();
                    // Record the version that completed the wizard
                    ConfigSvc.Config.LastRunVersion = UpdateChecker.CurrentVersionString;
                    ConfigSvc.Save();
                }));

            // Orphan check
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                var orphans = GetOrphanedServices();
                if (orphans.Count > 0)
                    LogSvc.Warn($"⚠ {orphans.Count} orphaned WireGuardTunnel$ service(s) found. " +
                                "Use Settings → Advanced to remove them.");
            });
        }

        // ── Taskbar visibility ────────────────────────────────────────────────
        private void ForceTaskbarButton()
        {
            var helper = new WindowInteropHelper(this);
            int style  = GetWindowLong(helper.Handle, GWL_EXSTYLE);
            style |=  WS_EX_APPWINDOW;
            style &= ~WS_EX_TOOLWINDOW;
            SetWindowLong(helper.Handle, GWL_EXSTYLE, style);
        }

        // ── Activity log rendering ────────────────────────────────────────────
        private void AppendLogEntry(LogEntry entry)
        {
            // When called from RebuildLog() we're already on the UI thread — run synchronously
            // so that rapid successive RebuildLog() calls (language + theme change on save)
            // don't queue up stacked BeginInvoke batches that produce duplicate entries.
            void AddToDoc()
            {
                var para = new Paragraph { Margin = new Thickness(0), Padding = new Thickness(0) };

                // Timestamp
                Brush tsBrush;
                try
                {
                    var col = (System.Windows.Media.Color)FindResource("Theme.LogTimestampColor");
                    tsBrush = new SolidColorBrush(col);
                }
                catch { tsBrush = (Brush)FindResource("TextMuted"); }

                var ts = new Run(entry.Timestamp.ToString("HH:mm:ss") + "  ")
                {
                    Foreground = tsBrush
                };

                // Ok events (Connected, Disconnected, …) shown in Accent (Windows theme colour)
                Brush msgBrush = entry.Level switch
                {
                    LogLevel.Ok    => SafeBrush("Accent"),
                    LogLevel.Warn  => SafeBrush("Danger"),
                    LogLevel.Info  => SafeBrush("Accent"),
                    _              => SafeBrush("TextMuted"),
                };

                string prefix = entry.IsContinuation ? "  ↳ " : "";
                var msgRun = new Run(prefix + entry.Message) { Foreground = msgBrush };

                para.Inlines.Add(ts);
                para.Inlines.Add(msgRun);
                if (LogDocument.Blocks.FirstBlock != null)
                    LogDocument.Blocks.InsertBefore(LogDocument.Blocks.FirstBlock, para);
                else
                    LogDocument.Blocks.Add(para);
                LogBox.ScrollToHome();
                LogCountLabel.Text = LogSvc.Count.ToString();
            }

            if (Dispatcher.CheckAccess())
                AddToDoc();
            else
                Dispatcher.BeginInvoke(AddToDoc);
        }

        private Brush SafeBrush(string key)
        {
            try { return (Brush)FindResource(key); }
            catch { return System.Windows.Media.Brushes.Gray; }
        }

        private void RebuildLog()
        {
            LogDocument.Blocks.Clear();
            foreach (var e in LogSvc.Entries)
                AppendLogEntry(e);
            LogCountLabel.Text = LogSvc.Count.ToString();
        }

        // ── Tunnel group tab strip (UI-only — ViewModel owns data) ────────────
        private string _activeGroup    = "";   // empty = pick first visible on first build
        private bool   _showAllOverride = false;

        public void RebuildTunnelGroupsPublic() => RebuildTunnelGroups();

        private void RebuildTunnelGroups()
        {
            TunnelTabButtons.Children.Clear();
            var all     = _vm.TunnelList;
            var groups  = ConfigSvc.Config.TunnelGroups;
            var hidden  = ConfigSvc.Config.HiddenTabs;
            var defGrp  = ConfigSvc.Config.DefaultGroup ?? "";

            // On first build, select DefaultGroup if set, else first visible group
            if (string.IsNullOrEmpty(_activeGroup))
            {
                _activeGroup = !string.IsNullOrEmpty(defGrp) ? defGrp
                    : groups.FirstOrDefault(g => !hidden.Contains(g.Name))?.Name
                    ?? (hidden.Contains("Uncategorized") ? "" : "Uncategorized");
            }

            int CountFor(string tag) => tag switch
            {
                "All"           => all.Count,
                "Uncategorized" => all.Count(t =>
                    string.IsNullOrEmpty(t.Group) ||
                    !groups.Any(g => g.Name == t.Group)),
                _               => all.Count(t => t.Group == tag),
            };

            void AddTab(string label, string tag, string? colour = null)
            {
                int  count  = CountFor(tag);
                bool active = tag == _activeGroup;

                // Resolve group colour and compute contrast foreground
                System.Windows.Media.Brush? tabBg = null;
                System.Windows.Media.Brush? tabFg = null;
                if (!string.IsNullOrEmpty(colour))
                {
                    try
                    {
                        System.Windows.Media.Color? resolvedColor = null;

                        if (colour.StartsWith("#"))
                        {
                            // Hex colour string — parse directly
                            var parsed = (System.Windows.Media.Color)
                                System.Windows.Media.ColorConverter.ConvertFromString(colour);
                            tabBg         = new System.Windows.Media.SolidColorBrush(parsed);
                            resolvedColor = parsed;
                        }
                        else
                        {
                            // Theme resource key — resolve via FindResource
                            var res = TryFindResource(colour);
                            if (res is System.Windows.Media.SolidColorBrush scbRes)
                            {
                                tabBg         = scbRes;
                                resolvedColor = scbRes.Color;
                            }
                            else if (res is System.Windows.Media.Color colorRes)
                            {
                                tabBg         = new System.Windows.Media.SolidColorBrush(colorRes);
                                resolvedColor = colorRes;
                            }
                        }

                        // Compute contrast foreground from resolved colour
                        if (resolvedColor.HasValue)
                        {
                            var c = resolvedColor.Value;
                            // Relative luminance (sRGB)
                            double Linearise(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
                            double L = 0.2126 * Linearise(c.R / 255.0)
                                     + 0.7152 * Linearise(c.G / 255.0)
                                     + 0.0722 * Linearise(c.B / 255.0);
                            tabFg = L > 0.179
                                ? System.Windows.Media.Brushes.Black
                                : System.Windows.Media.Brushes.White;
                        }
                    }
                    catch { }
                }

                bool hideCount = ConfigSvc.Config.AlwaysHideTunnelCount;

                // For tabs without a custom colour, compute contrast against the window background
                // so text is always readable in both dark and light themes
                if (tabFg == null)
                {
                    try
                    {
                        var winBg = FindResource("WindowBg") as SolidColorBrush;
                        if (winBg != null)
                        {
                            var c = winBg.Color;
                            double Linearise2(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
                            double L = 0.2126 * Linearise2(c.R / 255.0)
                                     + 0.7152 * Linearise2(c.G / 255.0)
                                     + 0.0722 * Linearise2(c.B / 255.0);
                            // In light themes (L > 0.4), use dark text; in dark themes, use themed text
                            if (L > 0.4)
                                tabFg = active
                                    ? (Brush)FindResource("Accent")
                                    : new SolidColorBrush(Color.FromRgb(30, 30, 30));
                        }
                    }
                    catch { }
                }
                var btn = new Button
                {
                    Content         = hideCount ? label : $"{label}  {count}",
                    Tag             = tag,
                    Style           = (Style)FindResource("FlatBtn"),
                    BorderThickness = new Thickness(0, 0, 0, active ? 2 : 0),
                    Padding         = new Thickness(8, 2, 8, 2),
                    FontWeight      = active ? FontWeights.Bold : FontWeights.Normal,
                    Margin          = new Thickness(0, 0, 2, 0),
                };
                btn.SetResourceReference(FontSizeProperty, "Theme.FontSize.Tiny");

                // Theme-derived colours use dynamic resource references so the tabs
                // (including the selected one) restyle with theme switches and live
                // Theme Builder edits — FindResource would freeze a snapshot of the
                // brush at build time. Group-specific colours stay literal.
                if (active && tabBg != null)  btn.Background = tabBg;
                else if (active)              btn.SetResourceReference(BackgroundProperty, "Surface");
                else                          btn.Background = tabBg ?? Brushes.Transparent;

                if (!active)                  btn.BorderBrush = Brushes.Transparent;
                else if (tabBg != null)       btn.BorderBrush = tabBg;
                else                          btn.SetResourceReference(BorderBrushProperty, "Accent");

                if (tabFg != null)            btn.Foreground = tabFg;
                else                          btn.SetResourceReference(ForegroundProperty, active ? "Accent" : "TextMuted");
                btn.Click += TunnelTab_Click;
                btn.AllowDrop = true;
                btn.DragOver  += TunnelTabDragOver;
                btn.Drop      += TunnelTabDrop;
                TunnelTabButtons.Children.Add(btn);
            }

            bool hideEmpty = ConfigSvc.Config.HideEmptyGroups;

            // Custom groups — skip hidden (unless override) and empty (if toggle on)
            foreach (var g in groups)
            {
                if (!_showAllOverride && hidden.Contains(g.Name)) continue;
                if (hideEmpty && CountFor(g.Name) == 0) continue;
                AddTab(g.Name, g.Name, string.IsNullOrEmpty(g.Color) ? null : g.Color);
            }

            // Uncategorized — skip if hidden (unless override) or empty (if toggle on)
            bool uncatHidden = !_showAllOverride && hidden.Contains("Uncategorized");
            bool uncatEmpty  = hideEmpty && CountFor("Uncategorized") == 0;
            if (!uncatHidden && !uncatEmpty)
                AddTab(Lang.T("TabUncategorized"), "Uncategorized");

            // If active group is no longer visible (hidden or empty), fall to first visible
            bool IsTabVisible(string tag) {
                if (tag == "Uncategorized")
                    return (_showAllOverride || !hidden.Contains("Uncategorized"))
                        && (!hideEmpty || CountFor("Uncategorized") > 0);
                var g2 = groups.FirstOrDefault(g => g.Name == tag);
                if (g2 == null) return false;
                return (_showAllOverride || !hidden.Contains(tag))
                    && (!hideEmpty || CountFor(tag) > 0);
            }

            if (!IsTabVisible(_activeGroup))
            {
                var first = groups.FirstOrDefault(g => IsTabVisible(g.Name));
                _activeGroup = first?.Name
                    ?? (IsTabVisible("Uncategorized") ? "Uncategorized" : "");
            }

            ApplyGroupFilter();
            UpdateHiddenCountBadge();
        }

        private void TunnelTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                _activeGroup = tag;
                RebuildTunnelGroups();
            }
        }

        private void HiddenCountBtn_Click(object sender, RoutedEventArgs e)
        {
            _showAllOverride = !_showAllOverride;
            if (!_showAllOverride)
            {
                var hidden    = ConfigSvc.Config.HiddenTabs;
                var groups    = ConfigSvc.Config.TunnelGroups;
                bool hideEmpty = ConfigSvc.Config.HideEmptyGroups;
                // If active group is now invisible, pick first visible
                bool stillVisible = _activeGroup == "Uncategorized"
                    ? !hidden.Contains("Uncategorized") && (!hideEmpty || _vm.TunnelList.Any(t => string.IsNullOrEmpty(t.Group) || !groups.Any(g=>g.Name==t.Group)))
                    : !hidden.Contains(_activeGroup) && (!hideEmpty || _vm.TunnelList.Any(t=>t.Group==_activeGroup));
                if (!stillVisible)
                {
                    var first = groups.FirstOrDefault(g => !hidden.Contains(g.Name) && (!hideEmpty || _vm.TunnelList.Any(t=>t.Group==g.Name)));
                    _activeGroup = first?.Name
                        ?? (!hidden.Contains("Uncategorized") ? "Uncategorized" : "");
                }
            }
            RebuildTunnelGroups();
        }

        private void UpdateHiddenCountBadge()
        {
            // Never show when toggle is on
            if (ConfigSvc.Config.AlwaysHideTunnelCount)
            {
                HiddenCountBadge.Visibility = Visibility.Collapsed;
                return;
            }

            var hidden = ConfigSvc.Config.HiddenTabs;
            var all    = _vm.TunnelList;

            int total       = all.Count;
            int hiddenCount = all.Count(t =>
                string.IsNullOrEmpty(t.Group)
                    ? hidden.Contains("Uncategorized")
                    : hidden.Contains(t.Group));
            int visible = total - hiddenCount;

            if (hiddenCount == 0 && !_showAllOverride)
            {
                // All tunnels visible, no override — hide the badge
                HiddenCountBadge.Visibility = Visibility.Collapsed;
                return;
            }

            HiddenCountBadge.Visibility = Visibility.Visible;
            HiddenCountBtn.IsEnabled    = hiddenCount > 0;

            // SetResourceReference, not FindResource — this method only re-runs when the
            // hidden-tunnel count itself changes, not on every theme change, so a
            // FindResource snapshot here would go stale (ThemeManager replaces each brush
            // resource with a new object rather than mutating it in place).
            if (_showAllOverride)
            {
                // Override active — show plain total in accent with border
                HiddenCountBtn.Content      = total.ToString();
                HiddenCountBtn.SetResourceReference(ForegroundProperty, "Accent");
                HiddenCountBadge.SetResourceReference(Border.BackgroundProperty, "BorderColor");
                HiddenCountBadge.BorderThickness = new Thickness(1);
                HiddenCountBadge.SetResourceReference(Border.BorderBrushProperty, "Accent");
                HiddenCountBadge.CornerRadius    = new System.Windows.CornerRadius(8);
                HiddenCountBtn.ToolTip = "Override active: showing all tunnels. Click to restore hidden groups.";
            }
            else
            {
                // Hidden tunnels exist — show x/y, no border
                HiddenCountBtn.Content      = $"{visible}/{total}";
                HiddenCountBtn.SetResourceReference(ForegroundProperty, "TextMuted");
                HiddenCountBadge.SetResourceReference(Border.BackgroundProperty, "BorderColor");
                HiddenCountBadge.BorderThickness = new Thickness(0);
                HiddenCountBadge.BorderBrush     = Brushes.Transparent;
                HiddenCountBadge.CornerRadius    = new System.Windows.CornerRadius(8);
                HiddenCountBtn.ToolTip = "Some tunnels hidden by group settings. Click to show all.";
            }
        }

        private void ApplyGroupFilter()
        {
            var all    = _vm.TunnelList;
            var groups = ConfigSvc.Config.TunnelGroups;

            IEnumerable<TunnelEntryViewModel> filtered;
            if (_activeGroup == "Uncategorized")
                filtered = all.Where(t => string.IsNullOrEmpty(t.Group) ||
                                          !groups.Any(g => g.Name == t.Group));
            else
                filtered = all.Where(t => t.Group == _activeGroup);

            TunnelsListView.ItemsSource = SortTunnels(filtered).ToList();
        }

        private IEnumerable<TunnelEntryViewModel> SortTunnels(IEnumerable<TunnelEntryViewModel> src)
            => _tunnelSortCol switch
            {
                "Name"   => _tunnelSortAsc ? src.OrderBy(t => t.Name)      : src.OrderByDescending(t => t.Name),
                "Type"   => _tunnelSortAsc ? src.OrderBy(t => t.TypeLabel) : src.OrderByDescending(t => t.TypeLabel),
                "Status" => _tunnelSortAsc
                    ? src.OrderByDescending(t => t.IsActive).ThenByDescending(t => t.IsAvailable).ThenBy(t => t.Name)
                    : src.OrderBy(t => t.IsActive).ThenBy(t => t.IsAvailable).ThenBy(t => t.Name),
                "Rules"  => _tunnelSortAsc ? src.OrderBy(t => t.RuleCount) : src.OrderByDescending(t => t.RuleCount),
                _        => src,
            };

        // ── Tunnel sort click handlers ────────────────────────────────────────
        private void TunSort_Name  (object s, RoutedEventArgs e) => TunnelSort("Name");
        private void TunSort_Status(object s, RoutedEventArgs e) => TunnelSort("Status");
        private void TunSort_Rules (object s, RoutedEventArgs e) => TunnelSort("Rules");
        private void TunSort_Action(object s, RoutedEventArgs e) => TunnelSort("Action");

        private void TunnelSort(string col)
        {
            if (_tunnelSortCol == col) _tunnelSortAsc = !_tunnelSortAsc;
            else { _tunnelSortCol = col; _tunnelSortAsc = true; }
            ApplyGroupFilter();
            UpdateTunnelSortArrows();
        }

        private void UpdateTunnelSortArrows()
        {
            string asc = "▲", desc = "▼";
            TunArrowName.Text   = _tunnelSortCol == "Name"   ? (_tunnelSortAsc ? asc : desc) : "";
            TunArrowStatus.Text = _tunnelSortCol == "Status" ? (_tunnelSortAsc ? asc : desc) : "";
            TunArrowRules.Text  = _tunnelSortCol == "Rules"  ? (_tunnelSortAsc ? asc : desc) : "";
        }

        private void TunnelTabDragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent("TunnelEntry")
                ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void TunnelTabDrop(object sender, DragEventArgs e)
        {
            if (sender is not Button btn) return;
            if (e.Data.GetData("TunnelEntry") is not TunnelEntryViewModel vm) return;
            var tag = btn.Tag as string ?? "";

            var stored = ConfigSvc.Config.Tunnels
                .FirstOrDefault(t => t.Name == vm.Name);
            if (stored == null) return;

            stored.Group = tag == "Uncategorized" ? "" : tag;
            ConfigSvc.Save();
            LogSvc.Ok($"Tunnel \"{stored.Name}\" moved to group \"{(string.IsNullOrEmpty(stored.Group) ? "Uncategorized" : stored.Group)}\"");
            _vm.RebuildTunnelList();
            RebuildTunnelGroups();
        }

        // ── Tunnel drag-to-reorder ────────────────────────────────────────────
        private TunnelEntryViewModel? _dragItem;
        private System.Windows.Point  _dragStart;
        private DropLineAdorner?      _dropAdorner;

        private void TunnelItem_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.ListViewItem item &&
                item.DataContext is TunnelEntryViewModel vm)
            {
                _dragItem  = vm;
                _dragStart = e.GetPosition(null);
            }
        }

        private void TunnelList_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null) return;
            var pos  = e.GetPosition(null);
            var diff = _dragStart - pos;
            if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance) return;

            // Dim the source row
            SetItemOpacity(_dragItem, 0.4);

            DragDrop.DoDragDrop(TunnelsListView,
                new DataObject("TunnelEntry", _dragItem), DragDropEffects.Move);

            // Cleanup after DoDragDrop returns
            SetItemOpacity(_dragItem, 1.0);
            RemoveDropLine();
            _dragItem = null;
        }

        private void TunnelList_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent("TunnelEntry"))
            { e.Effects = DragDropEffects.None; e.Handled = true; return; }
            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            var target = GetItemUnderCursor<TunnelEntryViewModel>(
                TunnelsListView, e.GetPosition(TunnelsListView));
            if (target == null || target == _dragItem) { RemoveDropLine(); return; }

            var container = TunnelsListView.ItemContainerGenerator
                .ContainerFromItem(target) as System.Windows.Controls.ListViewItem;
            if (container == null) return;

            var posInItem = e.GetPosition(container);
            bool above    = posInItem.Y < container.ActualHeight / 2;
            var  linePos  = container.TranslatePoint(
                above ? new System.Windows.Point(0, 0)
                      : new System.Windows.Point(0, container.ActualHeight),
                TunnelsListView).Y - 1;

            if (_dropAdorner == null)
            {
                var layer = System.Windows.Documents.AdornerLayer
                    .GetAdornerLayer(TunnelsListView);
                if (layer != null)
                {
                    _dropAdorner = new DropLineAdorner(TunnelsListView);
                    layer.Add(_dropAdorner);
                }
            }
            _dropAdorner?.SetY(linePos);
        }

        private void TunnelList_Drop(object sender, DragEventArgs e)
        {
            RemoveDropLine();
            if (!e.Data.GetDataPresent("TunnelEntry")) return;
            if (e.Data.GetData("TunnelEntry") is not TunnelEntryViewModel dragged) return;

            var target = GetItemUnderCursor<TunnelEntryViewModel>(
                TunnelsListView, e.GetPosition(TunnelsListView));
            if (target == null || target == dragged) return;

            var tunnels = ConfigSvc.Config.Tunnels;
            int fromIdx = tunnels.FindIndex(t => t.Name == dragged.Name);
            int toIdx   = tunnels.FindIndex(t => t.Name == target.Name);
            if (fromIdx < 0 || toIdx < 0 || fromIdx == toIdx) return;

            var moved = tunnels[fromIdx];
            tunnels.RemoveAt(fromIdx);
            tunnels.Insert(toIdx, moved);
            ConfigSvc.Save();
            _vm.RebuildTunnelList();
            RebuildTunnelGroups();
        }

        private void RemoveDropLine()
        {
            if (_dropAdorner == null) return;
            var layer = System.Windows.Documents.AdornerLayer
                .GetAdornerLayer(TunnelsListView);
            layer?.Remove(_dropAdorner);
            _dropAdorner = null;
        }

        private void SetItemOpacity(TunnelEntryViewModel vm, double opacity)
        {
            var container = TunnelsListView.ItemContainerGenerator
                .ContainerFromItem(vm) as System.Windows.Controls.ListViewItem;
            if (container != null) container.Opacity = opacity;
        }

        private static T? GetItemUnderCursor<T>(System.Windows.Controls.ListView lv,
            System.Windows.Point pos) where T : class
        {
            var hit = VisualTreeHelper.HitTest(lv, pos);
            if (hit == null) return null;
            var dep = hit.VisualHit as DependencyObject;
            while (dep != null)
            {
                if (dep is System.Windows.Controls.ListViewItem li) return li.DataContext as T;
                dep = VisualTreeHelper.GetParent(dep);
            }
            return null;
        }

        /// <summary>
        /// Disables the main-window Add buttons a managed preset forbids. Edit/Delete/Toggle
        /// (selection-driven) are gated in their SelectionChanged handlers.
        /// </summary>
        private void ApplyPolicyGating()
        {
            if (AddTunnelBtn  != null && ConfigSvc.TunnelsLocked)  AddTunnelBtn.IsEnabled  = false;
            if (WifiRuleAddBtn != null && ConfigSvc.IsLocked("Rules")) WifiRuleAddBtn.IsEnabled = false;
        }

        private void TunnelsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _vm.SelectedTunnel = TunnelsListView.SelectedItem as TunnelEntryViewModel;
            bool tunnelsLocked = ConfigSvc.TunnelsLocked;
            EditTunnelBtn.IsEnabled   = _vm.SelectedTunnel != null && !tunnelsLocked;
            DeleteTunnelBtn.IsEnabled = _vm.SelectedTunnel != null && !tunnelsLocked;
            // Export exposes the config (incl. private key), so a managed lock on
            // Tunnels disables it too — a locked/kiosk install must not exfiltrate.
            if (ExportTunnelBtn != null)
                ExportTunnelBtn.IsEnabled = _vm.SelectedTunnel != null && !tunnelsLocked;
            DeleteTunnelBtn.Visibility = _vm.SelectedTunnel != null
                ? Visibility.Visible : Visibility.Collapsed;
            if (_vm.SelectedTunnel != null)
                DeleteTunnelBtn.Content = GetDeleteButtonLabel(_vm.SelectedTunnel.StoredTunnel);
        }

        private static string GetDeleteButtonLabel(StoredTunnel t) =>
            t.Source == "local"
                ? Lang.T("BtnDeleteTunnel")
                : Lang.T("BtnUnlinkTunnel");

        private void TunnelToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn &&
                btn.DataContext is TunnelEntryViewModel entry)
            {
                if (entry.IsActive)
                {
                    entry.DisconnectCommand.Execute(null);
                }
                else
                {
                    // Manual connect over a "kill at cap" limit → ask before ignoring it.
                    var kill = _vm.CapKillState(entry);
                    if (kill != null)
                    {
                        var msg = Lang.T("CapConnectConfirmMsg", entry.Name,
                            ViewModels.MainViewModel.FmtBytes(kill.UsedBytes),
                            ViewModels.MainViewModel.FmtBytes(kill.CapBytes));
                        if (!ShowThemedYesNo(msg, Lang.T("CapConnectConfirmTitle"))) return;
                        _vm.OverrideCap(entry);
                    }
                    entry.ConnectCommand.Execute(null);
                }

                _vm.RefreshTunnelStatus();
                UpdateTunnelLabel();
            }
        }

        private void ShowQr_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not TunnelEntryViewModel vm)
                return;
            var stored = vm.StoredTunnel;

            // Only local tunnels have a config stored in MasselGUARD to encode.
            if (stored.Source != "local")
            {
                MessageBox.Show(Lang.T("QrExportUnavailable"),
                    Lang.T("QrExportTitle", stored.Name),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string config;
            try { config = Services.TunnelService.DecryptConfig(stored); }
            catch (Exception ex)
            {
                LogSvc.Warn($"QR export failed: {ex.Message}");
                return;
            }
            if (string.IsNullOrWhiteSpace(config))
            {
                MessageBox.Show(Lang.T("QrExportUnavailable"),
                    Lang.T("QrExportTitle", stored.Name),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            new Views.QrExportWindow(stored.Name, config) { Owner = this }.ShowDialog();
        }

        // ── Export selected tunnel (plain / encrypted / QR) ───────────────────
        private void ExportTunnel_Click(object sender, RoutedEventArgs e)
        {
            // Managed lock on Tunnels forbids exporting configs (private keys).
            if (ConfigSvc.TunnelsLocked) return;

            var stored = _vm.SelectedTunnel?.StoredTunnel;
            if (stored == null) return;

            // Only local tunnels carry a config MasselGUARD can export; companion
            // tunnels are managed by the WireGuard app and have nothing stored here.
            if (stored.Source != "local")
            {
                MessageBox.Show(Lang.T("QrExportUnavailable"),
                    Lang.T("ExportTunnelTitle", stored.Name),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string config;
            try { config = Services.TunnelService.DecryptConfig(stored); }
            catch (Exception ex)
            {
                LogSvc.Warn($"Tunnel export failed: {ex.Message}");
                return;
            }
            if (string.IsNullOrWhiteSpace(config))
            {
                MessageBox.Show(Lang.T("QrExportUnavailable"),
                    Lang.T("ExportTunnelTitle", stored.Name),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            new Views.TunnelExportWindow(stored, config) { Owner = this }.ShowDialog();
        }

        // ── Dialog dispatchers (called by ViewModel events) ───────────────────
        private void OnAddTunnel()
        {
            var groupNames    = ConfigSvc.Config.TunnelGroups.Select(g => g.Name).ToList();
            bool isGlobalAlways = ConfigSvc.Config.KillSwitchMode == "always";
            var arMode = ConfigSvc.Config.AutoReconnectMode;
            var dlg = new Views.TunnelConfigDialog(
                groupNames: groupNames,
                isGlobalAlways: isGlobalAlways,
                autoReconnectMode: arMode) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            var stored = new StoredTunnel
            {
                Name                = dlg.ResultName ?? "",
                Path                = Services.TunnelService.SaveConfigToFile(dlg.ResultName ?? "", dlg.ResultConfig ?? ""),
                Source              = "local",
                Group               = dlg.ResultGroup,
                PreConnectScript    = dlg.ResultPreConnectScript,
                PostConnectScript   = dlg.ResultPostConnectScript,
                PreDisconnectScript = dlg.ResultPreDisconnectScript,
                PostDisconnectScript= dlg.ResultPostDisconnectScript,
                KillSwitch          = dlg.ResultKillSwitch,
                AutoReconnect       = arMode != "always" && dlg.ResultAutoReconnect,
                DailyCapMB          = dlg.ResultDailyCapMB,
                WeeklyCapMB         = dlg.ResultWeeklyCapMB,
                MonthlyCapMB        = dlg.ResultMonthlyCapMB,
                DailyCapKill        = dlg.ResultDailyCapKill,
                WeeklyCapKill       = dlg.ResultWeeklyCapKill,
                MonthlyCapKill      = dlg.ResultMonthlyCapKill,
                HideCapRing         = dlg.ResultHideCapRing,
                SplitMode           = dlg.ResultSplitMode,
                SplitRanges         = dlg.ResultSplitRanges,
            };
            ConfigSvc.Config.Tunnels.Add(stored);
            ConfigSvc.Save();
            LogSvc.Ok($"Tunnel added: {stored.Name}");
            _vm.RebuildTunnelList();
            RebuildTunnelGroups();
        }

        /// <summary>Period-to-date usage (bytes) for a tunnel: history for the calendar
        /// day / week / month + the live session if it's currently active. Shown in the
        /// tunnel editor's data-usage section.</summary>
        private (long day, long week, long month) PeriodUsedBytes(string name)
        {
            var now        = DateTime.UtcNow;
            var dayStart   = now.Date;
            var weekStart  = dayStart.AddDays(-(((int)dayStart.DayOfWeek + 6) % 7));
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var (drx, dtx) = HistorySvc.GetUsageInRange(name, dayStart,   dayStart.AddDays(1));
            var (wrx, wtx) = HistorySvc.GetUsageInRange(name, weekStart,  weekStart.AddDays(7));
            var (mrx, mtx) = HistorySvc.GetUsageInRange(name, monthStart, monthStart.AddMonths(1));
            var vm = _vm.TunnelList.FirstOrDefault(t =>
                t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            long live = vm?.IsActive == true ? vm.SessionBytes : 0;
            return (drx + dtx + live, wrx + wtx + live, mrx + mtx + live);
        }

        private void OnEditTunnel(StoredTunnel stored)
        {
            bool isDefault = ConfigSvc.Config.DefaultTunnel    == stored.Name
                          && ConfigSvc.Config.DefaultAction == "activate";
            bool isOpen    = ConfigSvc.Config.OpenWifiTunnel   == stored.Name;
            bool isGlobalAlways = ConfigSvc.Config.KillSwitchMode == "always";
            var  arMode         = ConfigSvc.Config.AutoReconnectMode;
            bool arAlways       = arMode == "always";

            var groupNames = ConfigSvc.Config.TunnelGroups.Select(g => g.Name).ToList();
            var (usedDay, usedWeek, usedMonth) = PeriodUsedBytes(stored.Name);
            var dlg = stored.Source == "local"
                ? (Window)new Views.TunnelConfigDialog(
                    stored.Name, Services.TunnelService.DecryptConfig(stored), stored.Group,
                    stored.PreConnectScript, stored.PostConnectScript,
                    stored.PreDisconnectScript, stored.PostDisconnectScript,
                    isDefault, isOpen, groupNames,
                    isKillSwitch: stored.KillSwitch || isGlobalAlways,
                    isGlobalAlways: isGlobalAlways,
                    isAutoReconnect: stored.AutoReconnect || arAlways,
                    autoReconnectMode: arMode,
                    existingMonthlyCapMB: stored.MonthlyCapMB,
                    existingDailyCapMB: stored.DailyCapMB,
                    existingWeeklyCapMB: stored.WeeklyCapMB,
                    existingDailyCapKill: stored.DailyCapKill,
                    existingWeeklyCapKill: stored.WeeklyCapKill,
                    existingMonthlyCapKill: stored.MonthlyCapKill,
                    existingHideCapRing: stored.HideCapRing,
                    existingSplitMode: stored.SplitMode,
                    existingSplitRanges: stored.SplitRanges,
                    existingSplitApps: stored.SplitApps,
                    existingDailyUsedBytes: usedDay,
                    existingWeeklyUsedBytes: usedWeek,
                    existingMonthlyUsedBytes: usedMonth)
                    { Owner = this }
                : new Views.TunnelMetadataDialog(
                    stored.Name, stored.Group, stored.Notes,
                    ConfigSvc.Config.TunnelGroups.Select(g=>g.Name).ToList(),
                    stored.PreConnectScript, stored.PostConnectScript,
                    stored.PreDisconnectScript, stored.PostDisconnectScript,
                    isDefault, isOpen,
                    isKillSwitch: stored.KillSwitch || isGlobalAlways,
                    isGlobalAlways: isGlobalAlways,
                    isAutoReconnect: stored.AutoReconnect || arAlways,
                    autoReconnectMode: arMode,
                    existingMonthlyCapMB: stored.MonthlyCapMB,
                    existingDailyCapMB: stored.DailyCapMB,
                    existingWeeklyCapMB: stored.WeeklyCapMB,
                    existingDailyCapKill: stored.DailyCapKill,
                    existingWeeklyCapKill: stored.WeeklyCapKill,
                    existingMonthlyCapKill: stored.MonthlyCapKill,
                    existingHideCapRing: stored.HideCapRing,
                    existingDailyUsedBytes: usedDay,
                    existingWeeklyUsedBytes: usedWeek,
                    existingMonthlyUsedBytes: usedMonth)
                    { Owner = this };

            if (dlg.ShowDialog() != true) return;

            bool newDefault, newOpen;

            if (dlg is Views.TunnelConfigDialog tcd)
            {
                var newName    = tcd.ResultName ?? stored.Name;
                var newConfig  = tcd.ResultConfig ?? Services.TunnelService.DecryptConfig(stored);
                var oldPath    = stored.Path;
                stored.Path    = Services.TunnelService.SaveConfigToFile(newName, newConfig);
                stored.Config  = null;
                // Delete old file if the name changed (new file already written above).
                if (!string.IsNullOrEmpty(oldPath) && oldPath != stored.Path &&
                    System.IO.File.Exists(oldPath))
                    try { System.IO.File.Delete(oldPath); } catch { }
                stored.Name    = newName;
                stored.Group                = tcd.ResultGroup;
                stored.PreConnectScript     = tcd.ResultPreConnectScript;
                stored.PostConnectScript    = tcd.ResultPostConnectScript;
                stored.PreDisconnectScript  = tcd.ResultPreDisconnectScript;
                stored.PostDisconnectScript = tcd.ResultPostDisconnectScript;
                newDefault = tcd.ResultIsDefault;
                newOpen    = tcd.ResultIsOpenProtection;
                if (!isGlobalAlways) stored.KillSwitch    = tcd.ResultKillSwitch;
                if (!arAlways)      stored.AutoReconnect = tcd.ResultAutoReconnect;
                stored.DailyCapMB     = tcd.ResultDailyCapMB;
                stored.WeeklyCapMB    = tcd.ResultWeeklyCapMB;
                stored.MonthlyCapMB   = tcd.ResultMonthlyCapMB;
                stored.DailyCapKill   = tcd.ResultDailyCapKill;
                stored.WeeklyCapKill  = tcd.ResultWeeklyCapKill;
                stored.MonthlyCapKill = tcd.ResultMonthlyCapKill;
                stored.HideCapRing    = tcd.ResultHideCapRing;
                stored.SplitMode      = tcd.ResultSplitMode;
                stored.SplitRanges    = tcd.ResultSplitRanges;
            }
            else if (dlg is Views.TunnelMetadataDialog tmd)
            {
                stored.Group                = tmd.ResultGroup;
                stored.Notes                = tmd.ResultNotes;
                stored.PreConnectScript     = tmd.ResultPreConnectScript;
                stored.PostConnectScript    = tmd.ResultPostConnectScript;
                stored.PreDisconnectScript  = tmd.ResultPreDisconnectScript;
                stored.PostDisconnectScript = tmd.ResultPostDisconnectScript;
                newDefault = tmd.ResultIsDefault;
                newOpen    = tmd.ResultIsOpenProtection;
                if (!isGlobalAlways) stored.KillSwitch    = tmd.ResultKillSwitch;
                if (!arAlways)      stored.AutoReconnect = tmd.ResultAutoReconnect;
                stored.DailyCapMB     = tmd.ResultDailyCapMB;
                stored.WeeklyCapMB    = tmd.ResultWeeklyCapMB;
                stored.MonthlyCapMB   = tmd.ResultMonthlyCapMB;
                stored.DailyCapKill   = tmd.ResultDailyCapKill;
                stored.WeeklyCapKill  = tmd.ResultWeeklyCapKill;
                stored.MonthlyCapKill = tmd.ResultMonthlyCapKill;
                stored.HideCapRing    = tmd.ResultHideCapRing;
            }
            else return;

            // Apply default action
            if (newDefault)
            {
                ConfigSvc.Config.DefaultAction = "activate";
                ConfigSvc.Config.DefaultTunnel = stored.Name;
            }
            else if (isDefault) // was default, now unchecked → clear
            {
                ConfigSvc.Config.DefaultAction = "none";
                ConfigSvc.Config.DefaultTunnel = "";
            }

            // Apply open network protection
            if (newOpen)
                ConfigSvc.Config.OpenWifiTunnel = stored.Name;
            else if (isOpen)
                ConfigSvc.Config.OpenWifiTunnel = "";

            // Editing the caps re-arms enforcement: forget any "ignore this period" override
            // or stop marker so the new limits take effect from a clean slate.
            _vm.ResetCapEnforcement(stored.Name);

            ConfigSvc.Save();
            _vm.RebuildTunnelList();
            RebuildTunnelGroups();
            NotifyAllBadges();
            UpdateStatusBarCentre();
            LogSvc.Ok($"Tunnel updated: {stored.Name}");
        }

        private void OnDeleteTunnel(StoredTunnel stored)
        {
            var msg   = stored.Source == "local"
                ? Lang.T("ConfirmDeleteTunnel", stored.Name)
                : Lang.T("ConfirmUnlinkTunnel", stored.Name);
            var title = stored.Source == "local"
                ? Lang.T("BtnDeleteTunnel")
                : Lang.T("BtnUnlinkTunnel");

            if (!ShowThemedYesNo(msg, title))
                return;

            // Close any open history entry so the tunnel doesn't appear permanently active.
            HistorySvc.RecordDisconnect(stored.Name);
            ConfigSvc.Config.Tunnels.Remove(stored);
            ConfigSvc.Save();
            LogSvc.Ok($"Tunnel removed: {stored.Name}");
            _vm.RebuildTunnelList();
            RebuildTunnelGroups();
        }

        private void OnQuickConnect() => QuickConnect_Click(this, new RoutedEventArgs());

        private void OnOpenSettings() => OpenSettings("General");

        private void UpdateAvailableBtn_Click(object sender, RoutedEventArgs e) => OpenSettings("About");

        /// <summary>Opens Settings on the given tab and refreshes app state once it closes.
        /// Public so other windows (e.g. the Theme Manager) can return focus here.</summary>
        public void OpenSettings(string initialTab)
        {
            var win = new Views.SettingsWindow(this) { Owner = this, InitialTab = initialTab };
            win.ShowDialog();
            // Refresh after settings close
            LogSvc.IsExtended = ConfigSvc.Config.LogLevelSetting == "extended";
            _vm.RebuildTunnelList();
            RebuildTunnelGroups();
            UpdateFooterLabel();
            UpdateStatusBarCentre();  // footer ⚡/🔓 labels can change via Settings → Default action / Open network
            NotifyAllBadges();        // and the ⚡/🔓 badges behind tunnel names
            RefreshUpdateBadge();
        }

        // ── Button click handlers (thin — delegate to VM or OnXxx) ────────────
        private void AddTunnel_Click(object s, RoutedEventArgs e)     => _vm.AddTunnelCommand.Execute(null);
        private void EditTunnel_Click(object s, RoutedEventArgs e)    => _vm.EditTunnelCommand.Execute(null);
        private void DeleteTunnel_Click(object s, RoutedEventArgs e)  => _vm.DeleteTunnelCommand.Execute(null);
        private void SettingsBtn_Click(object s, RoutedEventArgs e)   => _vm.OpenSettingsCommand.Execute(null);
        private void ExportLog_Click(object s, RoutedEventArgs e)     => _vm.ExportLogCommand.Execute(null);

        private void ClearLog_Click(object s, RoutedEventArgs e)
        {
            LogSvc.Clear();
            LogDocument.Blocks.Clear();
            LogCountLabel.Text = "0";
            LogSvc.Ok(Lang.T("LogCleared"));   // first entry after the clear
        }

        private void ImportTunnel_Click(object s, RoutedEventArgs e)
        {
            var alreadyImported = new System.Collections.Generic.HashSet<string>(
                ConfigSvc.Config.Tunnels.Select(t=>t.Name), StringComparer.OrdinalIgnoreCase);
            var dlg = new Views.ImportTunnelDialog(alreadyImported, ConfigSvc.Config.Mode)
                { Owner = this };
            string? lastImported = null;
            dlg.TunnelImported += (name, cfg2, src, path, settings) =>
            {
                var st = new StoredTunnel { Name=name, Source=src, Path=path };
                // Restore any MasselGUARD settings that travelled with the import.
                settings?.ApplyTo(st);
                // A global "always" kill-switch/auto-reconnect owns those flags —
                // don't let an imported per-tunnel value fight the global policy.
                if (ConfigSvc.Config.KillSwitchMode == "always")    st.KillSwitch    = false;
                if (ConfigSvc.Config.AutoReconnectMode == "always") st.AutoReconnect = false;

                // Replace an existing tunnel with the same name (import "overwrite");
                // otherwise add. The import dialog only reuses a name when the user
                // chose Overwrite, so a name match here means replace-in-place.
                var existing = ConfigSvc.Config.Tunnels
                    .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (!string.IsNullOrEmpty(existing.Path) && existing.Path != st.Path
                        && System.IO.File.Exists(existing.Path))
                        try { System.IO.File.Delete(existing.Path); } catch { }
                    ConfigSvc.Config.Tunnels[ConfigSvc.Config.Tunnels.IndexOf(existing)] = st;
                }
                else
                {
                    ConfigSvc.Config.Tunnels.Add(st);
                }
                lastImported = name;
            };
            dlg.ShowDialog();
            if (lastImported != null)
            {
                ConfigSvc.Save();
                LogSvc.Ok($"Tunnel imported: {lastImported}");
                _vm.RebuildTunnelList();
                RebuildTunnelGroups();
            }
        }

        // ── Quick Connect ─────────────────────────────────────────────────────
        private async void QuickConnect_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title  = Lang.T("QuickConnectTitle"),
                Filter = "WireGuard config (*.conf;*.conf.dpapi)|*.conf;*.conf.dpapi",
            };
            if (ofd.ShowDialog() != true) return;

            var stored = new StoredTunnel
            {
                Name   = System.IO.Path.GetFileNameWithoutExtension(ofd.FileName)
                              .Replace(".conf", ""),
                Source = "local",
                Path   = ofd.FileName,
            };

            await Task.Run(() => TunnelSvc.Connect(stored, ConfigSvc.Config));
            _vm.RebuildTunnelList();
            _vm.RefreshTunnelStatus();
            UpdateTunnelLabel();
        }

        // ── WireGuard client helpers ──────────────────────────────────────────
        private string WireGuardExe =>
            ConfigSvc.Config.WgExePath ?? "wireguard";

        internal void OpenWireGuardGui()
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                WireGuardExe) { UseShellExecute = true }); }
            catch (Exception ex) { LogSvc.Warn($"WireGuard: {ex.Message}"); }
        }


        private int _lastTrayActiveCount = -1; // -1 forces initial update

        private void OnStatusTick()
        {
            string? serviceSsid = WifiSvc.CurrentSsid;
            UpdateWifiLabel(serviceSsid);
            UpdateTunnelLabel();
            RefreshInfoSection();

            // Only redraw tray icon when active tunnel count actually changes
            int activeCount = _vm.TunnelList.Count(t => t.IsActive);
            if (activeCount != _lastTrayActiveCount)
            {
                _lastTrayActiveCount = activeCount;
                var activeEntry = _vm.TunnelList.FirstOrDefault(t => t.IsActive);
                ((App)Application.Current).UpdateTrayStatus(
                    activeEntry?.Name ?? "", activeCount);
            }
        }

        // ── Status bar ────────────────────────────────────────────────────────
        private string? _lastRecordedSsid = null;

        private void OnWifiChanged(string? ssid, bool isOpen)
        {
            Dispatcher.BeginInvoke(() =>
            {
                ApplyWifiState(ssid, isOpen);
                // Give tunnel service a moment to connect, then refresh tunnel label
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() =>
                    {
                        _vm.RefreshTunnelStatus();
                        UpdateTunnelLabel();
                    }));
            });
        }

        /// <summary>
        /// Central handler for any WiFi state update — from notification or startup query.
        /// Records SSID history and updates all UI.
        /// </summary>
        private void ApplyWifiState(string? ssid, bool isOpen)
        {
            if (!string.Equals(ssid, _lastRecordedSsid, StringComparison.OrdinalIgnoreCase))
            {
                if (ConfigSvc.Config.StoreWifiHistory)
                {
                    if (ssid != null) HistorySvc.RecordSsidConnect(ssid, isOpen);
                    else              HistorySvc.RecordSsidDisconnect();
                }
                _lastRecordedSsid = ssid;
            }

            UpdateWifiLabel(ssid);
            _vm.ApplyWifiState(ssid, isOpen);
        }

        private void TryUpdateWifi()
        {
            var (ssid, isOpen) = WifiSvc.QueryCurrentSsid();
            ApplyWifiState(ssid, isOpen);
        }

        private void UpdateWifiLabel(string? ssid)
        {
            WifiLabel.Text = string.IsNullOrEmpty(ssid) ? Lang.T("StatusNoWifi") : ssid;
            UpdateStatusBarCentre(); // keeps footer WiFi label in sync
        }

        private void UpdateTunnelLabel()
        {
            var active = _vm.TunnelList.FirstOrDefault(t => t.IsActive);
            TunnelLabel.Text = active?.Name ?? Lang.T("StatusNone");
            TunnelLabel.Foreground = active != null
                ? (Brush)FindResource("Success")
                : (Brush)FindResource("TextMuted");

            // Dynamic tooltip: show status text (includes uptime) when active
            TunnelLabel.ToolTip = active != null
                ? $"{active.Name}\n{active.StatusText}\nType: {active.TypeLabel}"
                : "No active tunnel";

            UpdateShieldChevron();
        }

        private void UpdateShieldChevron()
        {
            bool anyActive = _vm.TunnelList.Any(t => t.IsActive);
            Application.Current.Resources["ShieldChevronBrush"] =
                anyActive ? FindResource("Accent") : FindResource("TextMuted");
        }

        // ── Taskbar icon (Window.Icon — taskbar button + Alt-Tab) ──────────────
        // Mirrors the tray icon: a theme's appIcon drives both (ThemeManager.ApplyAppIcon
        // sets Theme.AppIcon for this and Theme.TrayIcon for the tray). Window.Icon alone
        // updates the title bar / Alt-Tab thumbnail fine, but the taskbar button's icon is
        // cached from window creation and doesn't reliably re-fetch a live Icon change —
        // an explicit WM_SETICON is needed to force that specific refresh (see
        // RefreshTaskbarButtonIcon). Falls back to the compiled exe icon when unset.
        private ImageSource? _defaultTaskbarIcon;
        private System.Drawing.Icon? _defaultTaskbarWinIcon;

        // WM_SETICON / SetClassLongPtr hand Windows a raw HICON with no notion of .NET
        // object lifetime. Each theme (re-)apply creates fresh Icon objects — once the old
        // ones are no longer referenced anywhere, GC can finalize them, which destroys the
        // native handle Windows' taskbar/window-class icon slot is still holding, leaving a
        // blank icon. Keeping every icon ever assigned to the window alive for the app's
        // lifetime (a handful of small GDI objects at most) avoids that entirely.
        private readonly List<System.Drawing.Icon> _liveTaskbarIcons = new();

        private void UpdateTaskbarIcon()
        {
            BitmapSource? bmp;
            System.Drawing.Icon? smallIcon, bigIcon;
            string source;

            if (Application.Current.Resources["Theme.AppIcon"] is BitmapSource themedBmp)
            {
                bmp        = themedBmp;
                smallIcon  = Application.Current.Resources["Theme.TrayIcon"]      as System.Drawing.Icon;
                bigIcon    = Application.Current.Resources["Theme.TaskbarBigIcon"] as System.Drawing.Icon ?? smallIcon;
                source     = "themed";
            }
            else
            {
                if (_defaultTaskbarIcon == null)
                    (_defaultTaskbarIcon, _defaultTaskbarWinIcon) = LoadDefaultExeIcon();
                bmp       = _defaultTaskbarIcon as BitmapSource;
                smallIcon = _defaultTaskbarWinIcon;
                bigIcon   = _defaultTaskbarWinIcon;
                source    = "default-exe";
            }

            LogSvc.Debug($"[Icon] UpdateTaskbarIcon: source={source} bmp={(bmp != null)} " +
                         $"small={(smallIcon != null ? smallIcon.Handle.ToString() : "null")} " +
                         $"big={(bigIcon != null ? bigIcon.Handle.ToString() : "null")} " +
                         $"runMode={AppRunMode}");

            Icon = bmp;
            RefreshTaskbarButtonIcon(smallIcon, bigIcon);
        }

        /// <summary>Forces the OS taskbar button to re-fetch the window icon — Window.Icon
        /// alone updates the title bar / Alt-Tab thumbnail but doesn't reliably reach the
        /// taskbar button once it already exists (its icon is cached at registration); an
        /// explicit WM_SETICON plus the window class's own icon slot (GCLP_HICON/HICONSM —
        /// some shell code paths read the class icon rather than the per-instance one) is
        /// needed to force that refresh. Small/big are set separately — reusing one
        /// undersized icon for both made the taskbar button look tiny/blurry.</summary>
        private void RefreshTaskbarButtonIcon(System.Drawing.Icon? smallIcon, System.Drawing.Icon? bigIcon)
        {
            if (smallIcon == null && bigIcon == null) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            LogSvc.Debug($"[Icon] RefreshTaskbarButtonIcon: hwnd={hwnd}");
            if (hwnd == IntPtr.Zero) return;

            if (bigIcon != null)
            {
                _liveTaskbarIcons.Add(bigIcon);
                var r1 = SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_BIG, bigIcon.Handle);
                var r2 = SetClassLongPtr(hwnd, GCLP_HICON, bigIcon.Handle);
                LogSvc.Debug($"[Icon] big: WM_SETICON prev={r1} SetClassLongPtr prev={r2}");
            }
            if (smallIcon != null)
            {
                _liveTaskbarIcons.Add(smallIcon);
                var r1 = SendMessage(hwnd, WM_SETICON, (IntPtr)ICON_SMALL, smallIcon.Handle);
                var r2 = SetClassLongPtr(hwnd, GCLP_HICONSM, smallIcon.Handle);
                LogSvc.Debug($"[Icon] small: WM_SETICON prev={r1} SetClassLongPtr prev={r2}");
            }
        }

        /// <summary>Window.Title — what Alt-Tab, the taskbar hover tooltip, and Task
        /// Manager actually read, independent of the custom in-window title bar text.
        /// Kept in step with the theme's app name, same pattern as the tray tooltip.</summary>
        private void UpdateWindowTitle()
        {
            var appName = ThemeManager.Instance.Current.AppName;
            Title = $"{appName} v{UpdateChecker.CurrentVersionString} - WireGuard VPN Client";
        }

        private static (ImageSource?, System.Drawing.Icon?) LoadDefaultExeIcon()
        {
            try
            {
                var path = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(path)) return (null, null);
                // Not disposed — its Handle is reused by RefreshTaskbarButtonIcon for the
                // lifetime of the app, same as the theme-derived Theme.TrayIcon resource.
                var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (ico == null) return (null, null);
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return (src, ico);
            }
            catch { return (null, null); }
        }

        private void UpdateFooterLabel()
        {
            string modeText = AppRunMode switch
            {
                AppRunModeKind.Managed         => Lang.T("InstallStatusManaged"),
                AppRunModeKind.ManagedPortable => "Managed (Portable)",
                _                              => Lang.T("InstallStatusNotInstalled"),
            };
            FooterLabel.Text = $"{Lang.T("FooterMode")}: {modeText}";
            FooterLabel.SetResourceReference(ForegroundProperty, "TextMuted");  // always grey
        }

        private void UpdateAdminLabel()
        {
            bool isAdmin = new System.Security.Principal.WindowsPrincipal(
                System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

            AdminLabel.Text = isAdmin ? Lang.T("AdminYes") : Lang.T("AdminNo");
            // Admin → Windows accent colour (theme colour); not admin → muted grey
            AdminLabel.Foreground = isAdmin
                ? (Brush)FindResource("Accent")
                : (Brush)FindResource("TextMuted");
        }

        // ── Theme application ─────────────────────────────────────────────────
        /// <summary>
        /// Applies the active theme based on ActiveTheme + SystemThemeMode from config.
        /// Called on startup and after settings are saved.
        /// </summary>
        internal void ApplyThemeFromConfig()
        {
            var cfg = ConfigSvc.Config;
            bool isDark = cfg.SystemThemeMode switch
            {
                "light" => false,
                "dark"  => true,
                _       => ThemeManager.GetSystemIsDark()   // "auto"
            };

            var theme = cfg.ActiveTheme;

            // A managed preset can force a theme that isn't installed in this build. Fetch it from
            // the shared-themes repo in the background and fall back to system colours until it
            // lands (permanently, if the download fails).
            if (!string.IsNullOrEmpty(theme) && theme != "__system__"
                && ConfigSvc.HasManagedPreset && ConfigSvc.IsLocked("ActiveTheme")
                && !ThemeManager.ThemeExists(theme))
            {
                _ = TryInstallPresetThemeAsync(theme);
                theme = "__system__";
            }

            if (string.IsNullOrEmpty(theme) || theme == "__system__")
                ThemeManager.Instance.LoadSystem(isDark);
            else
                ThemeManager.Instance.Load(theme, isDark);

            // Font override: applied after theme so it wins over the theme's own font.
            ThemeManager.ApplyFontOverride(cfg.FontOverrideEnabled, cfg.FontOverrideFamily, cfg.FontOverrideSize);
        }

        /// <summary>
        /// Downloads shared themes from the repo so a preset-forced theme becomes available, then
        /// re-applies the real theme. Best-effort — on failure the app stays on system colours.
        /// </summary>
        private async System.Threading.Tasks.Task TryInstallPresetThemeAsync(string themeName)
        {
            try
            {
                var url = (ConfigSvc.Config.SharedThemesRepoUrl ?? "").Trim();
                if (string.IsNullOrWhiteSpace(url)) url = Models.AppConfig.DefaultSharedThemesRepoUrl;
                await ThemeDownloadService.DownloadAsync(url, ThemeManager.SharedThemeRoot);
                if (ThemeManager.ThemeExists(themeName))
                    Dispatcher.Invoke(ApplyThemeFromConfig);
                else
                    LogSvc.Warn($"Preset theme '{themeName}' not found in the shared-themes repo — using system colours.");
            }
            catch (Exception ex)
            {
                LogSvc.Warn($"Could not fetch preset theme '{themeName}': {ex.Message} — using system colours.");
            }
        }

        internal void ApplyManualMode()
        {
            UpdateFooterLabel();
            RefreshWifiRulesPanel();
            _vm.NotifyRulesColumnChanged();
        }

        // ── Window chrome ─────────────────────────────────────────────────────
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_reallyClosing) return;   // allow Application.Current.Shutdown()

            // Shift+close (e.g. Alt+F4 while holding Shift) → clean exit
            if ((System.Windows.Input.Keyboard.Modifiers &
                 System.Windows.Input.ModifierKeys.Shift) != 0)
            {
                e.Cancel = true;   // cancel this close; TryExit → ShutdownApp will handle it
                ((App)Application.Current).TryExit();
                return;
            }

            e.Cancel = true;
            Hide();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            // Shift+X → clean exit (same as tray Exit)
            if ((System.Windows.Input.Keyboard.Modifiers &
                 System.Windows.Input.ModifierKeys.Shift) != 0)
                ((App)Application.Current).TryExit();
            else
                Hide();
        }

        private void DismissBanner_Click(object sender, RoutedEventArgs e)
            => ErrorBanner.Visibility = Visibility.Collapsed;

        // ── Public API used by Settings/Wizard ───────────────────────────────
        public AppConfig    GetConfig()             => ConfigSvc.Config;
        public static AppConfig? GetConfigStatic()  => null; // removed — use DI
        public void         SaveConfigPublic()      { ConfigSvc.Save(); }
        public void         SaveConfigPublic(string desc)
        {
            ConfigSvc.Save();
            LogSvc.Ok($"Saved: {desc}");
        }
        public void         LogInfoPublic(string msg) => LogSvc.Info(msg);
        public List<string> GetTunnelNames()
        {
            return ConfigSvc.Config.Tunnels
                .Where(t => ConfigSvc.Config.Mode switch
                {
                    AppMode.Standalone => t.Source == "local",
                    AppMode.Companion  => t.Source != "local",
                    _                  => true,
                })
                .Select(t => t.Name)
                .ToList();
        }
        public System.Collections.ObjectModel.ObservableCollection<TunnelRule> GetRules()
        {
            var col = new System.Collections.ObjectModel.ObservableCollection<TunnelRule>(
                ConfigSvc.Config.Rules);
            return col;
        }
        public void AddRulePublic()
        {
            var dlg = new Views.RuleDialog(GetCurrentSsid(), tunnels: GetAvailableTunnels())
                { Owner = this };
            if (dlg.ShowDialog() != true) return;
            var rule = new TunnelRule { Ssid = dlg.ResultSsid, Tunnel = dlg.ResultTunnel };
            ConfigSvc.Config.Rules.Add(rule);
            OnRulesChanged();
        }
        public void EditRulePublic(TunnelRule rule)
        {
            var oldCount = rule.ExecutionCount;
            var dlg = new Views.RuleDialog(GetCurrentSsid(),
                existingName:   rule.Name,
                existingSsid:   rule.Ssid,
                existingTunnel: rule.Tunnel,
                executionCount: rule.ExecutionCount,
                tunnels:        GetAvailableTunnels()) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            rule.Name   = dlg.ResultName;
            rule.Ssid   = dlg.ResultSsid;
            rule.Tunnel = dlg.ResultTunnel;
            if (dlg.ResultNewCounterValue >= 0) rule.ExecutionCount = dlg.ResultNewCounterValue;
            if (dlg.ResultNewCounterValue >= 0)
                LogSvc.Info($"  Counter: {oldCount} → {dlg.ResultNewCounterValue}");
            OnRulesChanged();
        }
        public void DeleteRulePublic(TunnelRule rule)
        {
            ConfigSvc.Config.Rules.Remove(rule);
            OnRulesChanged();
        }

        private string? GetCurrentSsid() => WifiSvc.CurrentSsid;
        private List<string> GetAvailableTunnels() => GetTunnelNames();

        // ── Orphaned service detection ────────────────────────────────────────
        public record OrphanedService(string ServiceName, string TunnelName);

        /// <summary>
        /// Service names queued for deletion — hidden from the orphan list immediately
        /// so the user doesn't see them again while removal is in progress.
        /// Entries are pruned automatically once SCM confirms they are gone.
        /// </summary>
        private readonly System.Collections.Generic.HashSet<string> _pendingOrphanDeletion =
            new(StringComparer.OrdinalIgnoreCase);

        public List<OrphanedService> GetOrphanedServices()
        {
            var result = new List<OrphanedService>();
            try
            {
                var presentInScm = new System.Collections.Generic.HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

                foreach (var svc in System.ServiceProcess.ServiceController.GetServices())
                {
                    const string prefix = "WireGuardTunnel$";
                    if (!svc.ServiceName.StartsWith(prefix,
                        StringComparison.OrdinalIgnoreCase)) continue;

                    presentInScm.Add(svc.ServiceName);

                    // Running/starting services are actively in use — not orphans.
                    // Both WireGuard for Windows and MasselGUARD delete the SCM entry
                    // when a tunnel is deactivated, so any stopped service is leftover debris.
                    if (svc.Status is System.ServiceProcess.ServiceControllerStatus.Running
                                   or System.ServiceProcess.ServiceControllerStatus.StartPending)
                        continue;

                    // Already queued for deletion — hide until SCM confirms it is gone.
                    if (_pendingOrphanDeletion.Contains(svc.ServiceName)) continue;

                    var name = svc.ServiceName[prefix.Length..];

                    // A tunnel MasselGUARD is mid-connect on (service created but not yet
                    // started), active, or mid-disconnect is not an orphan. Without this,
                    // the startup orphan check races an auto-connect and flags the
                    // brand-new service in its brief Stopped window.
                    var vm = _vm.TunnelList.FirstOrDefault(t =>
                        t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (vm != null && (vm.IsConnecting || vm.IsActive || vm.IsDisconnecting))
                        continue;

                    result.Add(new OrphanedService(svc.ServiceName, name));
                }

                // Prune entries that have already disappeared from SCM.
                _pendingOrphanDeletion.RemoveWhere(s => !presentInScm.Contains(s));
            }
            catch { }

            return result;
        }

        public void RemoveOrphan(OrphanedService orphan)
        {
            // Mark as pending immediately so the next scan hides it.
            _pendingOrphanDeletion.Add(orphan.ServiceName);
            try
            {
                // ForceRemoveService stops (if running) and deletes the SCM entry —
                // works for both running and already-stopped orphan services.
                TunnelDll.ForceRemoveService(orphan.ServiceName);
                LogSvc.Ok($"Orphan removed: {orphan.TunnelName}");
            }
            catch (Exception ex)
            {
                // Removal failed — unmark so it reappears in the list.
                _pendingOrphanDeletion.Remove(orphan.ServiceName);
                LogSvc.Warn($"Remove orphan failed ({orphan.TunnelName}): {ex.Message}");
            }
        }

        /// <summary>Normalises a version string for equality comparison — strips leading v, trims whitespace.</summary>
        private static string NormaliseVersion(string v)
            => v?.Trim().TrimStart('v', 'V').Trim() ?? "";

        private static bool IsVersionNewer(string current, string previous)
        {
            static int[] Parse(string v)
            {
                var parts = v.TrimStart('v','V').Split('.');
                return parts.Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
            }
            var c = Parse(current);
            var p = Parse(previous);
            int len = Math.Max(c.Length, p.Length);
            for (int i = 0; i < len; i++)
            {
                int cv = i < c.Length ? c[i] : 0;
                int pv = i < p.Length ? p[i] : 0;
                if (cv != pv) return cv > pv;
            }
            return false;
        }

        public void UpdateRulesColumnVisibility()
        {
            bool show = ConfigSvc.Config.ShowTunnelRulesColumn;
            var vis   = show ? Visibility.Visible : Visibility.Collapsed;

            // Header column
            if (RulesColHeader != null) RulesColHeader.Visibility = vis;

            // Collapse the column width so it takes no space when hidden
            // The header bar and DataTemplate both use a 5-column Grid.
            // We can't easily set ColumnDefinition.Width from code-behind on a DataTemplate,
            // but setting the header TextBlock to Collapsed shrinks visually;
            // the row cells are also bound to Visibility via the converter-free approach below.
            // Best approach: tag the ListView with a resource key and update all rendered rows.
            foreach (var item in TunnelsListView.Items)
            {
                var container = TunnelsListView.ItemContainerGenerator
                    .ContainerFromItem(item) as System.Windows.Controls.ListViewItem;
                if (container == null) continue;
                var cell = FindChild<System.Windows.Controls.TextBlock>(container,
                    tb => tb.GetValue(System.Windows.FrameworkElement.NameProperty) as string == "RulesCountCell"
                          || (tb.Text != null && tb.DataContext is TunnelEntryViewModel));
                if (cell != null) cell.Visibility = vis;
            }
        }

        private static T? FindChild<T>(DependencyObject parent, Func<T, bool>? predicate = null)
            where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t && (predicate == null || predicate(t))) return t;
                var found = FindChild<T>(child, predicate);
                if (found != null) return found;
            }
            return null;
        }

        private void TunnelItem_RightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not System.Windows.Controls.ListViewItem item) return;
            var entry = item.DataContext as TunnelEntryViewModel;
            if (entry == null) return;
            e.Handled = true;

            ShowTunnelPopup(entry, e);
        }

        private Window? _tunnelPopup;

        private void ShowTunnelPopup(TunnelEntryViewModel entry,
            System.Windows.Input.MouseButtonEventArgs mouseEvent)
        {
            _tunnelPopup?.Close();

            var surface   = (Brush)FindResource("Surface");
            var textPri   = (Brush)FindResource("TextPrimary");
            var textMuted = (Brush)FindResource("TextMuted");
            var accent    = (Brush)FindResource("Accent");
            var success   = (Brush)FindResource("Success");
            var border    = (Brush)FindResource("BorderColor");
            var hover     = (Brush)FindResource("ListHover");
            var fontFam   = (System.Windows.Media.FontFamily)FindResource("Theme.FontFamily");
            var corner    = (CornerRadius)FindResource("Theme.CornerRadius");

            // ── Build menu items ──────────────────────────────────────────────
            var items = new List<(string label, Brush icon, Action action)>();

            if (entry.IsDefaultTunnel)
                items.Add(("Clear default action tunnel", textMuted,
                    () => { ConfigSvc.Config.DefaultAction = "none";
                            ConfigSvc.Config.DefaultTunnel = "";
                            ApplyDefaultTunnelChange(); }));
            else
                items.Add(("⚡  Set as default action tunnel", accent,
                    () => { ConfigSvc.Config.DefaultAction = "activate";
                            ConfigSvc.Config.DefaultTunnel = entry.Name;
                            ApplyDefaultTunnelChange(); }));

            if (entry.IsOpenProtection)
                items.Add(("Clear open network protection", textMuted,
                    () => { ConfigSvc.Config.OpenWifiTunnel = "";
                            ApplyDefaultTunnelChange(); }));
            else
                items.Add(("🔓  Set as open network protection", success,
                    () => { ConfigSvc.Config.OpenWifiTunnel = entry.Name;
                            ApplyDefaultTunnelChange(); }));

            // ── Build the popup window ────────────────────────────────────────
            var popup = new Window
            {
                WindowStyle         = WindowStyle.None,
                AllowsTransparency  = true,
                Background          = System.Windows.Media.Brushes.Transparent,
                ResizeMode          = ResizeMode.NoResize,
                ShowInTaskbar       = false,
                SizeToContent       = SizeToContent.WidthAndHeight,
                Topmost             = true,
                Owner               = this,
            };

            var stack = new System.Windows.Controls.StackPanel { Background = surface };

            foreach (var (label, iconBrush, action) in items)
            {
                var row = new System.Windows.Controls.Border
                {
                    Background    = System.Windows.Media.Brushes.Transparent,
                    Padding       = new Thickness(14, 9, 14, 9),
                };
                var tb = new System.Windows.Controls.TextBlock
                {
                    Text       = label,
                    Foreground = iconBrush,
                    FontFamily = fontFam,
                };
                tb.SetResourceReference(FontSizeProperty, "Theme.FontSize");
                row.Child = tb;

                var capturedAction = action;
                row.MouseEnter += (_, _) => row.Background = hover;
                row.MouseLeave += (_, _) => row.Background = System.Windows.Media.Brushes.Transparent;
                row.MouseLeftButtonUp += (_, _) =>
                {
                    try { if (popup.IsLoaded) popup.Close(); } catch { }
                    _tunnelPopup = null;
                    capturedAction();
                };
                row.Cursor = System.Windows.Input.Cursors.Hand;
                stack.Children.Add(row);

                // Separator between items
                if (label != items[^1].label)
                    stack.Children.Add(new System.Windows.Controls.Border
                    {
                        Height     = 1,
                        Background = border,
                        Margin     = new Thickness(0),
                    });
            }

            popup.Content = new System.Windows.Controls.Border
            {
                Background      = surface,
                BorderBrush     = border,
                BorderThickness = new Thickness(1),
                CornerRadius    = corner,
                Child           = stack,
                Effect          = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color     = System.Windows.Media.Colors.Black,
                    Opacity   = 0.4,
                    BlurRadius= 12,
                    ShadowDepth = 3,
                    Direction = 270,
                },
            };

            // Position at the exact mouse click point (DPI-aware)
            var mousePos   = mouseEvent.GetPosition(this);
            var screenPt   = PointToScreen(mousePos);
            // Convert from physical pixels to WPF DIPs
            var source     = PresentationSource.FromVisual(this);
            double dpiX    = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY    = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            popup.Left     = screenPt.X / dpiX;
            popup.Top      = screenPt.Y / dpiY;

            popup.Deactivated += (_, _) =>
            {
                try { if (popup.IsLoaded) popup.Close(); } catch { }
                _tunnelPopup = null;
            };
            popup.KeyDown += (_, ke) =>
            {
                if (ke.Key == System.Windows.Input.Key.Escape)
                {
                    try { if (popup.IsLoaded) popup.Close(); } catch { }
                    _tunnelPopup = null;
                }
            };

            _tunnelPopup = popup;
            popup.Show();
        }

        // ── WiFi rule drag-to-reorder ─────────────────────────────────────────
        private WifiRuleRow? _dragRule;
        private System.Windows.Point _rulesDragStart;

        private void WifiRule_PreviewMouseDown(object sender,
            System.Windows.Input.MouseButtonEventArgs e)
        {
            _rulesDragStart = e.GetPosition(null);
            _dragRule       = (e.OriginalSource as FrameworkElement)
                              ?.DataContext as WifiRuleRow;
        }

        private void WifiRule_PreviewMouseMove(object sender,
            System.Windows.Input.MouseEventArgs e)
        {
            if (_dragRule == null || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
            var delta = e.GetPosition(null) - _rulesDragStart;
            if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4) return;
            DragDrop.DoDragDrop(WifiRulesListView, _dragRule,
                DragDropEffects.Move);
            _dragRule = null;
        }

        private void WifiRule_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(WifiRuleRow)) is not WifiRuleRow draggedRow) return;
            var target = (e.OriginalSource as FrameworkElement)
                         ?.DataContext as WifiRuleRow;
            if (target == null || target == draggedRow) return;

            var rules = ConfigSvc.Config.Rules;
            var srcRule = rules.FirstOrDefault(r => r.Ssid == draggedRow.Ssid);
            var tgtRule = rules.FirstOrDefault(r => r.Ssid == target.Ssid);
            if (srcRule == null || tgtRule == null) return;

            int si = rules.IndexOf(srcRule);
            int ti = rules.IndexOf(tgtRule);
            rules.RemoveAt(si);
            rules.Insert(ti, srcRule);
            ConfigSvc.Save();
            RefreshWifiRulesPanel();
        }

        // ── WiFi rule edit buttons ────────────────────────────────────────────
        private void WifiRulesListView_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            bool hasSelection = WifiRulesListView.SelectedItem != null;
            bool rulesLocked  = ConfigSvc.IsLocked("Rules");
            if (WifiRuleEditBtn   != null) WifiRuleEditBtn.IsEnabled   = hasSelection && !rulesLocked;
            if (WifiRuleDeleteBtn != null) WifiRuleDeleteBtn.IsEnabled = hasSelection && !rulesLocked;
            if (WifiRuleToggleBtn != null)
            {
                WifiRuleToggleBtn.IsEnabled = hasSelection && !rulesLocked;
                // Label reflects what the button will DO to the selected rule.
                bool enabled = (WifiRulesListView.SelectedItem as WifiRuleRow)?.Rule.Enabled ?? true;
                WifiRuleToggleBtn.Content = Lang.T(enabled ? "BtnDisableRule" : "BtnEnableRule");
            }
        }

        /// <summary>
        /// Call after ANY change to <c>ConfigSvc.Config.Rules</c>. Persists the change,
        /// refreshes the WiFi rules panel, and rebuilds BOTH the tunnel VMs and the
        /// grouped list view so the per-tunnel rule-count column reflects it.
        /// <c>RebuildTunnelList</c> alone is not enough: <c>TunnelsListView</c> is bound to
        /// a <c>.ToList()</c> snapshot produced by <c>ApplyGroupFilter</c> (via
        /// <c>RebuildTunnelGroups</c>), not to the live <c>_vm.TunnelList</c> collection, so
        /// without the group rebuild the column keeps showing the pre-change VMs.
        /// </summary>
        public void OnRulesChanged()
        {
            ConfigSvc.Save();
            RefreshWifiRulesPanel();
            _vm.RebuildTunnelList();
            RebuildTunnelGroups();
        }

        private void WifiRuleAdd_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Views.RuleDialog(
                WifiSvc.CurrentSsid,
                tunnels: GetTunnelNames())
                { Owner = this };
            if (dlg.ShowDialog() != true) return;
            var rule = new Models.TunnelRule
            {
                Kind        = dlg.ResultKind,
                Name        = dlg.ResultName,
                Ssid        = dlg.ResultSsid,
                Tunnel      = dlg.ResultTunnel,
                StartTime   = dlg.ResultStartTime,
                EndTime     = dlg.ResultEndTime,
                Days        = dlg.ResultDays,
                TrustedWhen = dlg.ResultTrustedWhen,
            };
            ConfigSvc.Config.Rules.Add(rule);
            LogSvc.Ok($"Rule added: {rule.RuleName}");
            OnRulesChanged();
        }

        private void WifiRuleEdit_Click(object sender, RoutedEventArgs e)
        {
            if (WifiRulesListView.SelectedItem is not WifiRuleRow row) return;
            // Operate on the exact rule instance — matching by SSID breaks for trusted /
            // schedule rules, whose SSID is empty.
            var rule = row.Rule;

            var oldCount = rule.ExecutionCount;
            var dlg = new Views.RuleDialog(
                WifiSvc.CurrentSsid,
                existingName:   rule.Name,
                existingSsid:   rule.Ssid,
                existingTunnel: rule.Tunnel,
                executionCount: rule.ExecutionCount,
                tunnels:        GetTunnelNames(),
                existingKind:   rule.Kind,
                existingStart:  rule.StartTime,
                existingEnd:    rule.EndTime,
                existingDays:   rule.Days,
                existingTrustedWhen: rule.TrustedWhen)
                { Owner = this };
            if (dlg.ShowDialog() != true) return;
            rule.Kind        = dlg.ResultKind;
            rule.Name        = dlg.ResultName;
            rule.Ssid        = dlg.ResultSsid;
            rule.Tunnel      = dlg.ResultTunnel;
            rule.StartTime   = dlg.ResultStartTime;
            rule.EndTime     = dlg.ResultEndTime;
            rule.Days        = dlg.ResultDays;
            rule.TrustedWhen = dlg.ResultTrustedWhen;
            if (dlg.ResultNewCounterValue >= 0) rule.ExecutionCount = dlg.ResultNewCounterValue;
            LogSvc.Ok($"Rule updated: {rule.RuleName}");
            if (dlg.ResultNewCounterValue >= 0)
                LogSvc.Info($"  Counter: {oldCount} → {dlg.ResultNewCounterValue}");
            OnRulesChanged();
        }

        private void WifiRuleDelete_Click(object sender, RoutedEventArgs e)
        {
            if (WifiRulesListView.SelectedItem is not WifiRuleRow row) return;
            var rule = row.Rule;
            if (!ShowThemedYesNo($"Delete rule \"{rule.RuleName}\"?", "Delete rule")) return;
            ConfigSvc.Config.Rules.Remove(rule);
            LogSvc.Ok($"Rule deleted: {rule.RuleName}");
            OnRulesChanged();
        }

        private void WifiRuleToggle_Click(object sender, RoutedEventArgs e)
        {
            if (WifiRulesListView.SelectedItem is not WifiRuleRow row) return;
            var rule = row.Rule;
            rule.Enabled = !rule.Enabled;          // grey-out + icon update via bindings
            LogSvc.Ok(rule.Enabled
                ? $"Rule enabled: {rule.RuleName}"
                : $"Rule disabled: {rule.RuleName}");
            ConfigSvc.Save();
            _vm.RebuildTunnelList();
            // Refresh the button label for the still-selected row.
            if (WifiRuleToggleBtn != null)
                WifiRuleToggleBtn.Content = Lang.T(rule.Enabled ? "BtnDisableRule" : "BtnEnableRule");
        }

        // ── Defaults popup ────────────────────────────────────────────────────
        private void DefaultsBtn_Click(object sender, RoutedEventArgs e)
        {
            var surface   = (Brush)FindResource("Surface");
            var cardBg    = (Brush)FindResource("CardBg");
            var textPri   = (Brush)FindResource("TextPrimary");
            var textMuted = (Brush)FindResource("TextMuted");
            var accent    = (Brush)FindResource("Accent");
            var success   = (Brush)FindResource("Success");
            var border    = (Brush)FindResource("BorderColor");
            var fontFam   = (System.Windows.Media.FontFamily)FindResource("Theme.FontFamily");
            var corner    = (CornerRadius)FindResource("Theme.CornerRadius");

            var tunnelNames = _vm.TunnelList.Select(t => t.Name).ToList();
            string clearItem = Lang.T("BehaviourClear");        // 🔓 row: "disable this feature"
            string noneItem  = Lang.T("DefaultActionNone");     // ⚡ row: "Do nothing" (matches Settings)

            // ── Build popup window ────────────────────────────────────────────
            var popup = new Window
            {
                WindowStyle        = WindowStyle.None,
                AllowsTransparency = true,
                Background         = System.Windows.Media.Brushes.Transparent,
                ResizeMode         = ResizeMode.NoResize,
                ShowInTaskbar      = false,
                SizeToContent      = SizeToContent.WidthAndHeight,
                Width              = 360,
                Owner              = this,
            };

            var root = new System.Windows.Controls.Border
            {
                Background      = surface,
                BorderBrush     = border,
                BorderThickness = new Thickness(1),
                CornerRadius    = corner,
                Padding         = new Thickness(0),
                Effect          = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, Opacity = 0.45,
                    BlurRadius = 14, ShadowDepth = 3, Direction = 270,
                },
            };

            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(0) };

            // Header
            var hdr = new System.Windows.Controls.Border
            {
                Background    = cardBg,
                CornerRadius  = new CornerRadius(corner.TopLeft, corner.TopRight, 0, 0),
                Padding       = new Thickness(14, 10, 14, 10),
                BorderBrush   = border,
                BorderThickness = new Thickness(0,0,0,1),
            };
            var hdrTb = new System.Windows.Controls.TextBlock
            {
                Text = Lang.T("BtnDefaults"), FontFamily = fontFam,
                FontWeight = FontWeights.SemiBold,
                Foreground = accent,
            };
            hdrTb.SetResourceReference(FontSizeProperty, "Theme.FontSize");
            hdr.Child = hdrTb;
            panel.Children.Add(hdr);

            // Helper to make a picker row
            System.Windows.Controls.ComboBox MakeRow(string emoji, string label,
                string currentValue, string? emptyItem, string? extraItem = null)
            {
                var row = new System.Windows.Controls.Border
                    { Padding = new Thickness(14, 10, 14, 6) };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var lbl = new System.Windows.Controls.TextBlock
                {
                    Text = $"{emoji}  {label}", FontFamily = fontFam,
                    Foreground = textPri, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0,0,12,0),
                };
                lbl.SetResourceReference(FontSizeProperty, "Theme.FontSize.Small");
                Grid.SetColumn(lbl, 0);

                var cb = new System.Windows.Controls.ComboBox
                {
                    FontFamily = fontFam, VerticalAlignment = VerticalAlignment.Center,
                };
                cb.SetResourceReference(FontSizeProperty, "Theme.FontSize.Small");
                if (emptyItem != null) cb.Items.Add(emptyItem);
                if (extraItem != null) cb.Items.Add(extraItem);
                foreach (var t in tunnelNames) cb.Items.Add(t);
                cb.SelectedItem =
                    (extraItem != null && currentValue == extraItem) ? extraItem
                    : tunnelNames.Contains(currentValue) ? currentValue
                    : emptyItem;
                Grid.SetColumn(cb, 1);

                g.Children.Add(lbl);
                g.Children.Add(cb);
                row.Child = g;
                panel.Children.Add(row);
                return cb;
            }

            // The default picker can represent all three DefaultAction states — a tunnel
            // (activate), "— clear —" (none), or this sentinel (disconnect all) — so opening
            // the popup on a "disconnect" default and saving no longer silently downgrades it.
            string disconnectItem = "🚫 " + Lang.T("DefaultActionDisconnect");
            string curDefault = ConfigSvc.Config.DefaultAction switch
            {
                "activate"   => ConfigSvc.Config.DefaultTunnel,
                "disconnect" => disconnectItem,
                _            => "",
            };
            string curOpen    = ConfigSvc.Config.OpenWifiTunnel;

            var defaultPicker = MakeRow("⚡", Lang.T("BehaviourDefaultAction"),   curDefault, noneItem, disconnectItem);
            var openPicker    = MakeRow("🔓", Lang.T("BehaviourOpenProtection"), curOpen,    clearItem);

            // Separator
            panel.Children.Add(new System.Windows.Controls.Border
                { Height = 1, Background = border });

            // Buttons row
            var btnRow = new System.Windows.Controls.Border
                { Padding = new Thickness(14, 8, 14, 10) };
            var btnGrid = new Grid();
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            btnGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var btnCancel = new System.Windows.Controls.Button
            {
                Content = Lang.T("BtnCancel"), FontFamily = fontFam,
                Style = (Style)Application.Current.Resources["FlatBtn"],
                Padding = new Thickness(14,5,14,5), Margin = new Thickness(0,0,8,0),
            };
            btnCancel.SetResourceReference(FontSizeProperty, "Theme.FontSize.Small");
            var btnSave = new System.Windows.Controls.Button
            {
                Content = Lang.T("BtnSave"), FontFamily = fontFam,
                Style = (Style)Application.Current.Resources["PrimaryBtn"],
                Padding = new Thickness(14,5,14,5),
            };
            btnSave.SetResourceReference(FontSizeProperty, "Theme.FontSize.Small");
            Grid.SetColumn(btnCancel, 1);
            Grid.SetColumn(btnSave,   2);
            btnGrid.Children.Add(btnCancel);
            btnGrid.Children.Add(btnSave);
            btnRow.Child = btnGrid;
            panel.Children.Add(btnRow);

            root.Child = panel;
            popup.Content = root;

            btnCancel.Click += (_, _) => popup.Close();
            btnSave.Click   += (_, _) =>
            {
                var defSel  = defaultPicker.SelectedItem as string ?? "";
                var openSel = openPicker.SelectedItem   as string ?? "";

                if (defSel == disconnectItem)
                {
                    ConfigSvc.Config.DefaultAction = "disconnect";
                    ConfigSvc.Config.DefaultTunnel = "";
                }
                else if (defSel == noneItem || string.IsNullOrEmpty(defSel))
                {
                    ConfigSvc.Config.DefaultAction = "none";
                    ConfigSvc.Config.DefaultTunnel = "";
                }
                else
                {
                    ConfigSvc.Config.DefaultAction = "activate";
                    ConfigSvc.Config.DefaultTunnel = defSel;
                }

                ConfigSvc.Config.OpenWifiTunnel = openSel == clearItem ? "" : openSel;

                ConfigSvc.Save();
                NotifyAllBadges();
                UpdateStatusBarCentre();
                RefreshWifiRulesPanel();
                _vm.RebuildTunnelList();
                ApplyGroupFilter();
                popup.Close();
            };

            // Position centred on main window
            popup.Loaded += (_, _) =>
            {
                var winPos  = PointToScreen(new Point(0, 0));
                var source  = PresentationSource.FromVisual(this);
                double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
                popup.Left  = winPos.X / dpiX + (ActualWidth  - popup.ActualWidth)  / 2;
                popup.Top   = winPos.Y / dpiY + (ActualHeight - popup.ActualHeight) / 2;
            };
            popup.Deactivated += (_, _) =>
            {
                try { if (popup.IsLoaded) popup.Close(); } catch { }
            };

            popup.Show();
        }
        private void TunnelContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.ContextMenu cm) return;
            var entry = (cm.PlacementTarget as System.Windows.Controls.ListViewItem)
                        ?.DataContext as TunnelEntryViewModel;
            if (entry == null) return;
            if (cm.FindName("MenuSetDefault")         is System.Windows.Controls.MenuItem ms)
                ms.Visibility  = entry.IsDefaultTunnel  ? Visibility.Collapsed : Visibility.Visible;
            if (cm.FindName("MenuClearDefault")       is System.Windows.Controls.MenuItem mc)
                mc.Visibility  = entry.IsDefaultTunnel  ? Visibility.Visible   : Visibility.Collapsed;
            if (cm.FindName("MenuSetOpenProtection")  is System.Windows.Controls.MenuItem mo)
                mo.Visibility  = entry.IsOpenProtection ? Visibility.Collapsed : Visibility.Visible;
            if (cm.FindName("MenuClearOpenProtection")is System.Windows.Controls.MenuItem mp)
                mp.Visibility  = entry.IsOpenProtection ? Visibility.Visible   : Visibility.Collapsed;
        }

        private TunnelEntryViewModel? GetContextMenuEntry(object sender)
        {
            if (sender is not System.Windows.Controls.MenuItem mi) return null;
            return (mi.Parent as System.Windows.Controls.ContextMenu)?.Tag
                   as TunnelEntryViewModel;
        }

        private void ApplyDefaultTunnelChange()
        {
            ConfigSvc.Save();
            UpdateStatusBarCentre();
            RefreshWifiRulesPanel();
            _vm.RebuildTunnelList();
            NotifyAllBadges();
            ApplyGroupFilter();
        }

        private void NotifyAllBadges()
        {
            foreach (var t in _vm.TunnelList) t.NotifyBadgesChanged();
        }

        private void MenuSetDefault_Click(object s, RoutedEventArgs e)
        {
            var entry = GetContextMenuEntry(s); if (entry == null) return;
            ConfigSvc.Config.DefaultAction = "activate";
            ConfigSvc.Config.DefaultTunnel = entry.Name;
            ApplyDefaultTunnelChange();
        }

        private void MenuClearDefault_Click(object s, RoutedEventArgs e)
        {
            if (GetContextMenuEntry(s) == null) return;
            ConfigSvc.Config.DefaultAction = "none";
            ConfigSvc.Config.DefaultTunnel = "";
            ApplyDefaultTunnelChange();
        }

        private void MenuSetOpenProtection_Click(object s, RoutedEventArgs e)
        {
            var entry = GetContextMenuEntry(s); if (entry == null) return;
            ConfigSvc.Config.OpenWifiTunnel = entry.Name;
            ApplyDefaultTunnelChange();
        }

        private void MenuClearOpenProtection_Click(object s, RoutedEventArgs e)
        {
            if (GetContextMenuEntry(s) == null) return;
            ConfigSvc.Config.OpenWifiTunnel = "";
            ApplyDefaultTunnelChange();
        }

        public void UpdateStatusBarCentre()
        {
            var def  = ConfigSvc.Config.DefaultTunnel;
            var open = ConfigSvc.Config.OpenWifiTunnel;
            bool showDef  = !string.IsNullOrEmpty(def)  && ConfigSvc.Config.DefaultAction == "activate";
            bool showOpen = !string.IsNullOrEmpty(open);

            // WiFi footer indicator — hidden when no network is connected
            string? ssid  = WifiSvc.CurrentSsid;
            bool showWifi = !string.IsNullOrEmpty(ssid);
            WifiFooterLabel.Text       = showWifi ? $"📶 {ssid}" : "";
            WifiFooterLabel.Visibility = showWifi ? Visibility.Visible : Visibility.Collapsed;
            // Separator between WiFi and the other items — only when something follows
            WifiFooterSep.Visibility   = showWifi && (showDef || showOpen)
                ? Visibility.Visible : Visibility.Collapsed;

            DefaultTunnelLabel.Text        = showDef  ? $"⚡ {def}"  : "";
            OpenProtectionLabel.Text       = showOpen ? $"🔓 {open}" : "";
            DefaultTunnelLabel.Visibility  = showDef  ? Visibility.Visible : Visibility.Collapsed;
            OpenProtectionLabel.Visibility = showOpen ? Visibility.Visible : Visibility.Collapsed;
            StatusCentreSep.Visibility     = showDef && showOpen
                ? Visibility.Visible : Visibility.Collapsed;

            DefaultTunnelLabel.ToolTip  = showDef
                ? $"Default action: activate \"{def}\"\nActivates when no WiFi rule matches the current network"
                : null;
            OpenProtectionLabel.ToolTip = showOpen
                ? $"Open network protection: \"{open}\"\nActivates automatically when joining a passwordless WiFi network"
                : null;
        }

        private void RuleCount_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.TextBlock tb &&
                tb.DataContext is TunnelEntryViewModel vm &&
                vm.RuleCount > 0)
            {
                e.Handled = true;
                HighlightRulesForTunnel(vm.Name);
            }
        }

        // ── WiFi Rules panel ──────────────────────────────────────────────────
        private string? _activeRuleFilter; // tunnel name currently highlighted

        public void HighlightRulesForTunnel(string tunnelName)
        {
            // Toggle: clicking the same tunnel again clears the filter
            _activeRuleFilter = _activeRuleFilter == tunnelName ? null : tunnelName;
            RefreshWifiRulesPanel();
        }

        public void RefreshWifiRulesPanel()
        {
            bool manualMode = ConfigSvc.Config.ManualMode;
            bool showPanel  = ConfigSvc.Config.ShowWifiRulesOnMainWindow && !manualMode;

            if (WifiRulesPanel       != null) WifiRulesPanel.Visibility       = showPanel ? Visibility.Visible : Visibility.Collapsed;
            if (WifiRulesHeader      != null) WifiRulesHeader.Visibility      = showPanel ? Visibility.Visible : Visibility.Collapsed;
            if (WifiRuleButtonsPanel != null) WifiRuleButtonsPanel.Visibility = showPanel ? Visibility.Visible : Visibility.Collapsed;

            // Collapse/restore the grid rows — tunnels:WiFi = 3*:2* = 60%:40%
            var zero      = new GridLength(0);
            var wifiStar  = new GridLength(2, GridUnitType.Star);   // 40 %
            var auto      = GridLength.Auto;
            if (WifiRulesHeaderRow != null) WifiRulesHeaderRow.Height = showPanel ? auto     : zero;
            if (WifiRulesBtnsRow   != null) WifiRulesBtnsRow.Height   = showPanel ? auto     : zero;
            if (WifiRulesPanelRow  != null)
            {
                // Drop MinHeight when hidden too — otherwise the row keeps its 152 px and
                // leaves a gap, so the tunnel list can't grow to fill the freed space.
                WifiRulesPanelRow.MinHeight = showPanel ? 152 : 0;
                WifiRulesPanelRow.Height    = showPanel ? wifiStar : zero;
            }

            if (!showPanel) { _activeRuleFilter = null; return; }

            var rules = ConfigSvc.Config.Rules;
            if (WifiRuleCountLabel != null)
                WifiRuleCountLabel.Text = rules.Count.ToString();

            var rows = rules.Select(r => new WifiRuleRow(r, _activeRuleFilter, this));
            WifiRulesListView.ItemsSource = SortRules(rows).ToList();
        }

        private IEnumerable<WifiRuleRow> SortRules(IEnumerable<WifiRuleRow> src)
            => _ruleSortCol switch
            {
                "Name"   => _ruleSortAsc ? src.OrderBy(r => r.RuleName)      : src.OrderByDescending(r => r.RuleName),
                "Ssid"   => _ruleSortAsc ? src.OrderBy(r => r.Ssid)          : src.OrderByDescending(r => r.Ssid),
                "Action" => _ruleSortAsc ? src.OrderBy(r => r.ActionLabel)   : src.OrderByDescending(r => r.ActionLabel),
                "Count"  => _ruleSortAsc ? src.OrderBy(r => r.ExecutionCount): src.OrderByDescending(r => r.ExecutionCount),
                "Tunnel" => _ruleSortAsc ? src.OrderBy(r => r.TunnelName)    : src.OrderByDescending(r => r.TunnelName),
                _        => src,
            };

        // ── WiFi rule sort click handlers ─────────────────────────────────────
        private void RuleSort_Name  (object s, RoutedEventArgs e) => RuleSort("Name");
        private void RuleSort_Ssid  (object s, RoutedEventArgs e) => RuleSort("Ssid");
        private void RuleSort_Action(object s, RoutedEventArgs e) => RuleSort("Action");
        private void RuleSort_Count (object s, RoutedEventArgs e) => RuleSort("Count");
        private void RuleSort_Tunnel(object s, RoutedEventArgs e) => RuleSort("Tunnel");

        private void RuleSort(string col)
        {
            if (_ruleSortCol == col) _ruleSortAsc = !_ruleSortAsc;
            else { _ruleSortCol = col; _ruleSortAsc = true; }
            RefreshWifiRulesPanel();
            UpdateRuleSortArrows();
        }

        private void UpdateRuleSortArrows()
        {
            string asc = "▲", desc = "▼";
            RuleArrowName.Text   = _ruleSortCol == "Name"   ? (_ruleSortAsc ? asc : desc) : "";
            RuleArrowSsid.Text   = _ruleSortCol == "Ssid"   ? (_ruleSortAsc ? asc : desc) : "";
            RuleArrowAction.Text = _ruleSortCol == "Action" ? (_ruleSortAsc ? asc : desc) : "";
            RuleArrowCount.Text  = _ruleSortCol == "Count"  ? (_ruleSortAsc ? asc : desc) : "";
            RuleArrowTunnel.Text = _ruleSortCol == "Tunnel" ? (_ruleSortAsc ? asc : desc) : "";
        }

        private sealed class WifiRuleRow
        {
            /// <summary>The underlying rule — edit/delete/toggle act on this exact instance.</summary>
            public Models.TunnelRule Rule { get; }
            public string RuleName      { get; }
            public string Ssid          { get; }
            public string ActionLabel   { get; }
            public string TunnelName    { get; }
            public bool   IsHighlighted { get; }
            public int    ExecutionCount { get; }
            public string ExecutionCountText => ExecutionCount > 0 ? ExecutionCount.ToString() : "0";

            // Accent only when this row is highlighted (tunnel filter active); grey otherwise
            public System.Windows.Media.Brush ActionColor =>
                IsHighlighted
                    ? (System.Windows.Media.Brush)(System.Windows.Application.Current
                        .Resources["Accent"] ?? System.Windows.Media.Brushes.CornflowerBlue)
                    : (System.Windows.Media.Brush)(System.Windows.Application.Current
                        .Resources["TextMuted"] ?? System.Windows.Media.Brushes.Gray);

            public System.Windows.Media.Brush ExecutionCountColor =>
                ExecutionCount > 0 && IsHighlighted
                    ? (System.Windows.Media.Brush)(System.Windows.Application.Current
                        .Resources["Accent"] ?? System.Windows.Media.Brushes.CornflowerBlue)
                    : (System.Windows.Media.Brush)(System.Windows.Application.Current
                        .Resources["TextMuted"] ?? System.Windows.Media.Brushes.Gray);

            public System.Windows.FontWeight TunnelFontWeight =>
                IsHighlighted ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal;

            public System.Windows.Media.Brush RowBg =>
                IsHighlighted
                    ? new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromArgb(30, 96, 165, 250))
                    : System.Windows.Media.Brushes.Transparent;

            public System.Windows.Media.Brush BorderBrush =>
                IsHighlighted
                    ? (System.Windows.Media.Brush)(System.Windows.Application.Current
                        .Resources["Accent"] ?? System.Windows.Media.Brushes.CornflowerBlue)
                    : System.Windows.Media.Brushes.Transparent;

            public System.Windows.Thickness BorderThick =>
                new(IsHighlighted ? 2 : 0, 0, 0, 0);

            public System.Windows.Media.Brush TunnelFg =>
                IsHighlighted
                    ? (System.Windows.Media.Brush)(System.Windows.Application.Current
                        .Resources["Accent"] ?? System.Windows.Media.Brushes.CornflowerBlue)
                    : (System.Windows.Media.Brush)(System.Windows.Application.Current
                        .Resources["TextMuted"] ?? System.Windows.Media.Brushes.Gray);

            private readonly MainWindow _main;

            public WifiRuleRow(Models.TunnelRule r, string? filter, MainWindow main)
            {
                _main          = main;
                Rule           = r;
                // Kind-aware display comes straight from the rule (handles wifi / schedule / trusted).
                RuleName       = r.RuleName;
                Ssid           = r.SsidDisplay;
                TunnelName     = string.IsNullOrEmpty(r.Tunnel) ? "" : r.Tunnel;
                ActionLabel    = string.IsNullOrEmpty(r.Tunnel)
                    ? Lang.T("RuleActionDisconnect")
                    : Lang.T("RuleActionConnect");
                IsHighlighted  = filter != null && r.Tunnel == filter;
                ExecutionCount = r.ExecutionCount;
            }
        }

        // ── App run mode ──────────────────────────────────────────────────────
        public enum AppRunModeKind { Standalone, ManagedPortable, Managed }

        public AppRunModeKind AppRunMode
        {
            get
            {
                var installed = GetInstalledPath();
                if (installed == null) return AppRunModeKind.Standalone;

                var exePath = Environment.ProcessPath;
                var current = string.IsNullOrEmpty(exePath)
                    ? AppContext.BaseDirectory.TrimEnd('\\', '/')
                    : System.IO.Path.GetDirectoryName(exePath) ?? "";

                return string.Equals(
                    System.IO.Path.GetFullPath(current),
                    System.IO.Path.GetFullPath(installed),
                    StringComparison.OrdinalIgnoreCase)
                    ? AppRunModeKind.Managed
                    : AppRunModeKind.ManagedPortable;
            }
        }

        /// <summary>
        /// Shows a themed Yes/No dialog matching the app's current theme.
        /// Returns true if Yes was clicked.
        /// </summary>
        public bool ShowThemedYesNo(string message, string title)
        {
            bool result = false;
            var win = new Window
            {
                WindowStyle           = WindowStyle.None,
                AllowsTransparency    = true,
                Background            = System.Windows.Media.Brushes.Transparent,
                Width                 = 400,
                SizeToContent         = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = this,
                ResizeMode            = ResizeMode.NoResize,
            };

            var border = new System.Windows.Controls.Border
            {
                Background      = (System.Windows.Media.Brush)Application.Current.Resources["WindowBg"],
                BorderBrush     = (System.Windows.Media.Brush)Application.Current.Resources["Accent"],
                BorderThickness = new Thickness(1),
                CornerRadius    = new System.Windows.CornerRadius(6),
                Padding         = new Thickness(20),
            };

            var panel = new System.Windows.Controls.StackPanel();

            var yesNoTitleTb = new System.Windows.Controls.TextBlock
            {
                Text       = title,
                FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimary"],
                Margin     = new Thickness(0, 0, 0, 10),
            };
            yesNoTitleTb.SetResourceReference(FontSizeProperty, "Theme.FontSize");
            panel.Children.Add(yesNoTitleTb);
            var yesNoMsgTb = new System.Windows.Controls.TextBlock
            {
                Text         = message,
                Foreground   = (System.Windows.Media.Brush)Application.Current.Resources["TextMuted"],
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 16),
            };
            yesNoMsgTb.SetResourceReference(FontSizeProperty, "Theme.FontSize.Small");
            panel.Children.Add(yesNoMsgTb);

            var btns = new System.Windows.Controls.StackPanel
            {
                Orientation         = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var btnNo  = new System.Windows.Controls.Button { Content = Lang.T("BtnNo"),  Style = (Style)Application.Current.Resources["FlatBtn"],    Padding = new Thickness(14,6,14,6), Margin = new Thickness(0,0,8,0) };
            var btnYes = new System.Windows.Controls.Button { Content = Lang.T("BtnYes"), Style = (Style)Application.Current.Resources["SuccessBtn"], Padding = new Thickness(14,6,14,6) };
            btnNo.Click  += (_, _) => { result = false; win.Close(); };
            btnYes.Click += (_, _) => { result = true;  win.Close(); };
            btns.Children.Add(btnNo);
            btns.Children.Add(btnYes);
            panel.Children.Add(btns);
            border.Child = panel;
            win.Content  = border;
            win.ShowDialog();
            return result;
        }

        /// <summary>Single-button (OK) themed info dialog — same style as ShowThemedYesNo.</summary>
        public void ShowThemedInfo(string message, string title)
        {
            var win = new Window
            {
                WindowStyle           = WindowStyle.None,
                AllowsTransparency    = true,
                Background            = System.Windows.Media.Brushes.Transparent,
                Width                 = 400,
                SizeToContent         = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = this,
                ResizeMode            = ResizeMode.NoResize,
            };

            var border = new System.Windows.Controls.Border
            {
                Background      = (System.Windows.Media.Brush)Application.Current.Resources["WindowBg"],
                BorderBrush     = (System.Windows.Media.Brush)Application.Current.Resources["Accent"],
                BorderThickness = new Thickness(1),
                CornerRadius    = new System.Windows.CornerRadius(6),
                Padding         = new Thickness(20),
            };

            var panel = new System.Windows.Controls.StackPanel();

            var infoTitleTb = new System.Windows.Controls.TextBlock
            {
                Text       = title,
                FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimary"],
                Margin     = new Thickness(0, 0, 0, 10),
            };
            infoTitleTb.SetResourceReference(FontSizeProperty, "Theme.FontSize");
            panel.Children.Add(infoTitleTb);
            var infoMsgTb = new System.Windows.Controls.TextBlock
            {
                Text         = message,
                Foreground   = (System.Windows.Media.Brush)Application.Current.Resources["TextMuted"],
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 16),
            };
            infoMsgTb.SetResourceReference(FontSizeProperty, "Theme.FontSize.Small");
            panel.Children.Add(infoMsgTb);

            var btns = new System.Windows.Controls.StackPanel
            {
                Orientation         = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var btnOk = new System.Windows.Controls.Button
            {
                Content = Lang.T("BtnOk"),
                Style   = (Style)Application.Current.Resources["FlatBtn"],
                Padding = new Thickness(14, 6, 14, 6),
            };
            btnOk.Click += (_, _) => win.Close();
            btns.Children.Add(btnOk);
            panel.Children.Add(btns);
            border.Child = panel;
            win.Content  = border;
            win.ShowDialog();
        }

        private void ShowPortableInstallPrompt()
        {
            // Custom dialog with "Don't ask again" checkbox
            var win = new Window
            {
                WindowStyle           = WindowStyle.None,
                AllowsTransparency    = true,
                Background            = System.Windows.Media.Brushes.Transparent,
                Width                 = 420,
                SizeToContent         = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = this,
                ResizeMode            = ResizeMode.NoResize,
            };

            bool suppress = false;
            bool install  = false;

            var border = new System.Windows.Controls.Border
            {
                Background      = (System.Windows.Media.Brush)Application.Current.Resources["WindowBg"],
                BorderBrush     = (System.Windows.Media.Brush)Application.Current.Resources["Accent"],
                BorderThickness = new Thickness(1),
                CornerRadius    = new System.Windows.CornerRadius(6),
                Padding         = new Thickness(20),
            };

            var panel = new System.Windows.Controls.StackPanel();

            var title = new System.Windows.Controls.TextBlock
            {
                Text       = Lang.T("InstallPortableTitle"),
                FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimary"],
                Margin     = new Thickness(0, 0, 0, 10),
            };
            title.SetResourceReference(FontSizeProperty, "Theme.FontSize.Header");
            var body = new System.Windows.Controls.TextBlock
            {
                Text        = Lang.T("InstallPortablePrompt"),
                Foreground  = (System.Windows.Media.Brush)Application.Current.Resources["TextMuted"],
                TextWrapping= TextWrapping.Wrap,
                Margin      = new Thickness(0, 0, 0, 10),
            };
            body.SetResourceReference(FontSizeProperty, "Theme.FontSize.Small");
            var chk = new System.Windows.Controls.CheckBox
            {
                Content    = Lang.T("InstallPortableDoNotAsk"),
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextMuted"],
                Margin     = new Thickness(0, 0, 0, 14),
            };
            chk.SetResourceReference(FontSizeProperty, "Theme.FontSize.Tiny");
            chk.Checked   += (_, _) => suppress = true;
            chk.Unchecked += (_, _) => suppress = false;

            var btns = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var btnNo  = new System.Windows.Controls.Button { Content = Lang.T("BtnCancel"), Style = (Style)Application.Current.Resources["FlatBtn"], Padding = new Thickness(14,6,14,6), Margin = new Thickness(0,0,8,0) };
            var btnYes = new System.Windows.Controls.Button { Content = Lang.T("BtnInstall"), Style = (Style)Application.Current.Resources["SuccessBtn"], Padding = new Thickness(14,6,14,6) };
            btnNo.Click  += (_, _) => { install = false; win.Close(); };
            btnYes.Click += (_, _) => { install = true;  win.Close(); };
            btns.Children.Add(btnNo);
            btns.Children.Add(btnYes);

            panel.Children.Add(title);
            panel.Children.Add(body);
            panel.Children.Add(chk);
            panel.Children.Add(btns);
            border.Child = panel;
            win.Content  = border;
            win.ShowDialog();

            if (suppress)
            {
                ConfigSvc.Config.SuppressPortableUpdatePrompt = true;
                ConfigSvc.Save();
            }
            if (install)
                RunInstallPublic();
        }

        // ── Update check frequency ────────────────────────────────────────────
        private bool ShouldCheckForUpdates()
        {
            var freq = ConfigSvc.Config.UpdateCheckFrequency ?? "weekly";
            if (freq == "manual") return false;

            var last = ConfigSvc.Config.LastUpdateCheck;
            double daysSince = (DateTime.UtcNow - last).TotalDays;

            return freq switch
            {
                "onstart" => true,
                "daily"   => daysSince >= 1,
                "weekly"  => daysSince >= 7,
                _         => daysSince >= 7,
            };
        }

        public async System.Threading.Tasks.Task CheckForUpdatesAsync(bool silent)
        {
            try
            {
                var latest = await UpdateChecker.CheckNowAsync(ConfigSvc.Config, ConfigSvc.Save);

                // Always refresh the badge — even when no update is found (clears a stale badge).
                Dispatcher.Invoke(RefreshUpdateBadge);

                // Theme updates piggyback on the same check (same frequency/trigger) —
                // a passive badge only, no prompt, so it doesn't compete with the app update.
                _ = CheckForThemeUpdatesAsync();

                if (latest == null) return;
                if (!UpdateChecker.IsNewerVersion(latest.TagName)) return;

                // Ask on the UI thread, then start the download on a background thread.
                var shouldUpdate = false;
                await Dispatcher.InvokeAsync(() =>
                {
                    var current = UpdateChecker.CurrentVersionString;
                    shouldUpdate = ShowThemedYesNo(
                        Lang.T("UpdateAvailableMsg", latest.TagName, current),
                        Lang.T("UpdateAvailableTitle"));
                });

                if (shouldUpdate)
                {
                    var progress = new System.Progress<string>(
                        msg => LogSvc.Info($"[Update] {msg}"));
                    await UpdateChecker.UpdateAsync(
                        latest, progress, ConfigSvc.Config, ConfigSvc.Save,
                        onShutdown: () => Dispatcher.Invoke(
                            () => ((App)System.Windows.Application.Current).ShutdownApp()));
                }
            }
            catch { /* silent — network may not be available */ }
        }

        // ── ARM64 switch (x64 build running emulated on an ARM64 PC) ──────────
        private enum Arm64Choice { Download, Later, Dismiss }

        /// <summary>
        /// Offers to download the latest ARM64 build from GitHub and switch this install to it.
        /// Shown at startup only when running the x64 build on an ARM64 system. "Download" reuses
        /// the update pipeline forced to the arm64 asset; "Don't remind me" persists a flag.
        /// </summary>
        private async System.Threading.Tasks.Task OfferArm64SwitchAsync()
        {
            // Let the window settle before popping a modal over it.
            await System.Threading.Tasks.Task.Delay(1200);

            var choice = Arm64Choice.Later;
            await Dispatcher.InvokeAsync(() => { choice = ShowArm64SwitchPrompt(); });

            if (choice == Arm64Choice.Dismiss)
            {
                ConfigSvc.Config.ArmSwitchDismissed = true;
                ConfigSvc.Save();
                return;
            }
            if (choice != Arm64Choice.Download) return;   // Later → offer again next launch

            try
            {
                // Force the arm64 asset even though this process is emulated x64.
                var release = await UpdateChecker.FetchLatestReleaseAsync(forceArch: "arm64");
                if (release?.ZipUrl == null)
                {
                    await Dispatcher.InvokeAsync(() =>
                        ShowThemedInfo(Lang.T("Arm64NoticeNoAsset"), Lang.T("Arm64NoticeTitle")));
                    return;
                }

                var progress = new System.Progress<string>(msg => LogSvc.Info($"[ARM64] {msg}"));
                await UpdateChecker.UpdateAsync(
                    release, progress, ConfigSvc.Config, ConfigSvc.Save,
                    onShutdown: () => Dispatcher.Invoke(
                        () => ((App)System.Windows.Application.Current).ShutdownApp()));
                // UpdateAsync calls onShutdown on success — execution never reaches here.
            }
            catch (Exception ex)
            {
                LogSvc.Warn($"[ARM64] switch failed: {ex.Message}");
                await Dispatcher.InvokeAsync(() =>
                    ShowThemedInfo(Lang.T("Arm64NoticeFailed"), Lang.T("Arm64NoticeTitle")));
            }
        }

        /// <summary>Themed three-choice prompt for the ARM64 switch: Download / Later / Don't remind me.</summary>
        private Arm64Choice ShowArm64SwitchPrompt()
        {
            var choice = Arm64Choice.Later;
            var win = new Window
            {
                WindowStyle           = WindowStyle.None,
                AllowsTransparency    = true,
                Background            = System.Windows.Media.Brushes.Transparent,
                Width                 = 440,
                SizeToContent         = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = this,
                ResizeMode            = ResizeMode.NoResize,
            };

            var border = new System.Windows.Controls.Border
            {
                Background      = (System.Windows.Media.Brush)Application.Current.Resources["WindowBg"],
                BorderBrush     = (System.Windows.Media.Brush)Application.Current.Resources["Accent"],
                BorderThickness = new Thickness(1),
                CornerRadius    = new System.Windows.CornerRadius(6),
                Padding         = new Thickness(20),
            };

            var panel = new System.Windows.Controls.StackPanel();

            var titleTb = new System.Windows.Controls.TextBlock
            {
                Text       = Lang.T("Arm64NoticeTitle"),
                FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimary"],
                Margin     = new Thickness(0, 0, 0, 10),
            };
            titleTb.SetResourceReference(FontSizeProperty, "Theme.FontSize");
            panel.Children.Add(titleTb);

            var msgTb = new System.Windows.Controls.TextBlock
            {
                Text         = Lang.T("Arm64NoticeMsg"),
                Foreground   = (System.Windows.Media.Brush)Application.Current.Resources["TextMuted"],
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 16),
            };
            msgTb.SetResourceReference(FontSizeProperty, "Theme.FontSize.Small");
            panel.Children.Add(msgTb);

            var btns = new System.Windows.Controls.StackPanel
            {
                Orientation         = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var btnDismiss  = new System.Windows.Controls.Button { Content = Lang.T("Arm64NoticeDismiss"),  Style = (Style)Application.Current.Resources["FlatBtn"],    Padding = new Thickness(14,6,14,6), Margin = new Thickness(0,0,8,0) };
            var btnLater    = new System.Windows.Controls.Button { Content = Lang.T("Arm64NoticeLater"),    Style = (Style)Application.Current.Resources["FlatBtn"],    Padding = new Thickness(14,6,14,6), Margin = new Thickness(0,0,8,0) };
            var btnDownload = new System.Windows.Controls.Button { Content = Lang.T("Arm64NoticeDownload"), Style = (Style)Application.Current.Resources["SuccessBtn"], Padding = new Thickness(14,6,14,6) };
            btnDismiss.Click  += (_, _) => { choice = Arm64Choice.Dismiss;  win.Close(); };
            btnLater.Click    += (_, _) => { choice = Arm64Choice.Later;    win.Close(); };
            btnDownload.Click += (_, _) => { choice = Arm64Choice.Download; win.Close(); };
            btns.Children.Add(btnDismiss);
            btns.Children.Add(btnLater);
            btns.Children.Add(btnDownload);
            panel.Children.Add(btns);

            border.Child = panel;
            win.Content  = border;
            win.ShowDialog();
            return choice;
        }

        /// <summary>Checks installed community themes against the repo manifest for updates —
        /// called alongside the app update check (same frequency/trigger). Purely a passive
        /// result (ConfigSvc.Config.ThemeUpdatesAvailable) for Settings to badge; unlike the
        /// app update, it never prompts on its own.</summary>
        public async System.Threading.Tasks.Task CheckForThemeUpdatesAsync()
        {
            try
            {
                var url = (ConfigSvc.Config.SharedThemesRepoUrl ?? "").Trim();
                if (string.IsNullOrWhiteSpace(url)) url = AppConfig.DefaultSharedThemesRepoUrl;

                var updated = await ThemeDownloadService.CheckForThemeUpdatesAsync(url, ThemeManager.SharedThemeRoot);
                ConfigSvc.Config.ThemeUpdatesAvailable = updated;
                ConfigSvc.Save();
            }
            catch { /* silent — network may not be available */ }
        }

        // ── Update badge ──────────────────────────────────────────────────────

        /// <summary>
        /// Shows or hides the ↑ update badge in the title bar based on whether
        /// a newer version has been seen. Safe to call from any thread via Dispatcher.
        /// </summary>
        public void RefreshUpdateBadge()
        {
            bool available = UpdateChecker.IsNewerVersion(ConfigSvc.Config.LatestKnownVersion);
            var vis = available ? Visibility.Visible : Visibility.Collapsed;

            if (UpdateAvailableBtn != null)
            {
                UpdateAvailableBtn.Visibility = vis;
                if (available)
                {
                    var ver = ConfigSvc.Config.LatestKnownVersion?.TrimStart('v', 'V') ?? "";
                    UpdateAvailableBtn.ToolTip =
                        $"MasselGUARD v{ver} is available — click to install";
                }
            }
            if (UpdateAvailableSep != null)
                UpdateAvailableSep.Visibility = vis;
        }

        // ── Install helpers (used by SettingsWindow) ──────────────────────────
        private static readonly string InstallRegKey    = @"SOFTWARE\MasselGUARD";
        private static readonly string InstallFolderName = "MasselGUARD";

        public bool IsInstalledCheck() =>
            GetInstalledPath() is string p &&
            System.IO.File.Exists(System.IO.Path.Combine(p, "MasselGUARD.exe"));

        public void RunInstallPublic()
        {
            var mode = AppRunMode;

            if (mode == AppRunModeKind.Managed)
            {
                // Running FROM the install dir — only option is uninstall
                RunUninstall();
                return;
            }

            if (mode == AppRunModeKind.ManagedPortable)
            {
                // Running a separate copy while an installed version exists — offer overwrite
                var installed = GetInstalledPath()!;
                if (!ShowThemedYesNo(
                    Lang.T("InstallOverwritePrompt", installed),
                    Lang.T("InstallTitle")))
                    return;
                // Skip the folder picker and install directly to the existing location
                DoInstall(installed);
                return;
            }

            // Standalone — pick folder and install fresh
            var defaultParent = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string installDir = PickInstallFolder(defaultParent);
            if (string.IsNullOrEmpty(installDir)) return;
            DoInstall(installDir);
        }

        private void DoInstall(string installDir)
        {
            try
            {
                LogSvc.Info($"Installing to: {installDir}");
                var currentExe = Environment.ProcessPath ?? AppContext.BaseDirectory;
                var sourceDir  = System.IO.Path.GetDirectoryName(currentExe)!;

                // 1. Copy files — explicit allowlist of what's actually needed to run,
                // rather than the source folder's entire contents. A portable copy can
                // pick up things that were never meant to travel into an install: .pdb
                // debug symbols, install-dotnet.bat (a first-run helper, pointless once
                // already running), and legacy cruft like an old exe-relative theme\
                // folder from before themes moved to %APPDATA%. Existing installs are
                // left alone — this only changes what a fresh install/update copies.
                System.IO.Directory.CreateDirectory(installDir);
                foreach (var name in new[] { "MasselGUARD.exe", "MasselGUARDcli.exe", "tunnel.dll", "wireguard.dll" })
                {
                    var src = System.IO.Path.Combine(sourceDir, name);
                    if (System.IO.File.Exists(src))
                        System.IO.File.Copy(src, System.IO.Path.Combine(installDir, name), overwrite: true);
                }
                var langSrc = System.IO.Path.Combine(sourceDir, "lang");
                if (System.IO.Directory.Exists(langSrc))
                    CopyDirRecursive(langSrc, System.IO.Path.Combine(installDir, "lang"));

                // Managed preset — offer to carry the locked policy into the install so the
                // installed copy stays managed. Only asked when a *.masselguard sits alongside.
                try
                {
                    var presetSrc = System.IO.Directory.GetFiles(sourceDir, "*" + Services.PresetService.Extension);
                    if (presetSrc.Length > 0 &&
                        ShowThemedYesNo(Lang.T("InstallCopyPreset"), Lang.T("InstallTitle")))
                    {
                        foreach (var pf in presetSrc)
                            System.IO.File.Copy(pf,
                                System.IO.Path.Combine(installDir, System.IO.Path.GetFileName(pf)),
                                overwrite: true);
                    }
                }
                catch { /* preset copy is best-effort */ }

                var installedExe = System.IO.Path.Combine(installDir, "MasselGUARD.exe");

                // 2. Start Menu shortcut
                var startMenuDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                    InstallFolderName);
                System.IO.Directory.CreateDirectory(startMenuDir);
                var shortcut = System.IO.Path.Combine(startMenuDir, "MasselGUARD.lnk");
                RunPS($@"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{shortcut}');$s.TargetPath='{installedExe}';$s.WorkingDirectory='{installDir}';$s.Save()");

                // 3. Registry + config
                using (var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(InstallRegKey)!)
                    key.SetValue("InstallPath", installDir);
                ConfigSvc.Config.InstalledPath = installDir;
                ConfigSvc.Save();

                LogSvc.Ok(Lang.T("InstallDone"));

                // 4. Auto-start — only ask if not already configured
                if (!GetStartWithWindows())
                {
                    if (ShowThemedYesNo(Lang.T("InstallAutostart"), Lang.T("InstallAutostartTitle")))
                    {
                        RunPS($@"$a=New-ScheduledTaskAction -Execute '{installedExe}';$t=New-ScheduledTaskTrigger -AtLogOn;$p=New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest;Register-ScheduledTask -TaskName 'MasselGUARD' -Action $a -Trigger $t -Principal $p -Force");
                        ConfigSvc.Config.StartWithWindows = true;
                        ConfigSvc.Save();
                        LogSvc.Ok(Lang.T("InstallScheduledOk"));
                    }
                }
                else
                {
                    // Already configured — re-register with new exe path silently
                    RunPS($@"$a=New-ScheduledTaskAction -Execute '{installedExe}';$t=New-ScheduledTaskTrigger -AtLogOn;$p=New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest;Register-ScheduledTask -TaskName 'MasselGUARD' -Action $a -Trigger $t -Principal $p -Force");
                    LogSvc.Ok(Lang.T("InstallScheduledOk"));
                }

                // 5. Relaunch from installed location
                LogSvc.Ok(Lang.T("InstallRestarting"));
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(installedExe)
                    { UseShellExecute = true });
                Application.Current.Dispatcher.BeginInvoke(() =>
                    ((App)Application.Current).ShutdownApp());
            }
            catch (Exception ex)
            {
                LogSvc.Warn($"Install failed: {ex.Message}");
                MessageBox.Show(Lang.T("InstallFailed", ex.Message), Lang.T("InstallTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RunUninstall()
        {
            var installDir = GetInstalledPath();
            if (installDir == null || !System.IO.Directory.Exists(installDir))
            {
                ShowThemedInfo(Lang.T("NotInstalled"), Lang.T("UninstallTitle")); return;
            }
            if (!ShowThemedYesNo(Lang.T("UninstallConfirm"), Lang.T("UninstallTitle")))
                return;

            bool keepConfig = ShowThemedYesNo(Lang.T("UninstallKeepConfig"),
                Lang.T("UninstallKeepConfigTitle"));

            try
            {
                LogSvc.Info("Uninstalling...");
                RunPS("Unregister-ScheduledTask -TaskName 'MasselGUARD' -Confirm:$false -ErrorAction SilentlyContinue");
                var startMenuDir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                    InstallFolderName);
                if (System.IO.Directory.Exists(startMenuDir))
                    System.IO.Directory.Delete(startMenuDir, recursive: true);
                Microsoft.Win32.Registry.LocalMachine.DeleteSubKey(InstallRegKey, false);
                ConfigSvc.Config.InstalledPath = null;
                ConfigSvc.Save();

                if (!keepConfig)
                {
                    var appData = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "MasselGUARD");
                    if (System.IO.Directory.Exists(appData))
                        System.IO.Directory.Delete(appData, recursive: true);
                }

                var current = System.IO.Path.GetDirectoryName(
                    Environment.ProcessPath ?? AppContext.BaseDirectory) ?? "";
                bool runningFromInstall = string.Equals(current, installDir,
                    StringComparison.OrdinalIgnoreCase);

                LogSvc.Ok("Uninstall complete.");

                if (runningFromInstall)
                {
                    // Schedule deletion of own folder and exit
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        "cmd.exe", $"/c timeout /t 2 /nobreak >nul & rd /s /q \"{installDir}\"")
                    { CreateNoWindow = true, UseShellExecute = false });
                    Application.Current.Dispatcher.BeginInvoke(() =>
                        ((App)Application.Current).ShutdownApp());
                }
            }
            catch (Exception ex)
            {
                LogSvc.Warn($"Uninstall failed: {ex.Message}");
                ShowThemedInfo(Lang.T("UninstallFailed", ex.Message), Lang.T("UninstallTitle"));
            }
        }

        private string PickInstallFolder(string defaultParent)
        {
            string? finalResult = null;

            var win = new Window
            {
                WindowStyle=WindowStyle.None, AllowsTransparency=true,
                Background=System.Windows.Media.Brushes.Transparent,
                Width=500, SizeToContent=SizeToContent.Height,
                WindowStartupLocation=WindowStartupLocation.CenterOwner,
                Owner=this, ResizeMode=ResizeMode.NoResize,
            };
            var border = new System.Windows.Controls.Border
            {
                Background=(System.Windows.Media.Brush)Application.Current.Resources["WindowBg"],
                BorderBrush=(System.Windows.Media.Brush)Application.Current.Resources["Accent"],
                BorderThickness=new Thickness(1),
                CornerRadius=new System.Windows.CornerRadius(6),
                Padding=new Thickness(20),
            };
            var panel = new System.Windows.Controls.StackPanel();

            var installTitleTb = new System.Windows.Controls.TextBlock
            {
                Text=Lang.T("InstallTitle"), FontWeight=FontWeights.Bold,
                Foreground=(System.Windows.Media.Brush)Application.Current.Resources["TextPrimary"],
                Margin=new Thickness(0,0,0,12),
            };
            installTitleTb.SetResourceReference(FontSizeProperty, "Theme.FontSize.Header");
            panel.Children.Add(installTitleTb);
            var installSelectFolderTb = new System.Windows.Controls.TextBlock
            {
                Text=Lang.T("InstallSelectFolder"),
                Foreground=(System.Windows.Media.Brush)Application.Current.Resources["TextMuted"],
                Margin=new Thickness(0,0,0,4),
            };
            installSelectFolderTb.SetResourceReference(FontSizeProperty, "Theme.FontSize.Tiny");
            panel.Children.Add(installSelectFolderTb);

            var folderRow = new System.Windows.Controls.Grid { Margin=new Thickness(0,0,0,8) };
            folderRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
            folderRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width=GridLength.Auto });

            var folderBox = new System.Windows.Controls.TextBox
            {
                Text=defaultParent,
                FontFamily=(System.Windows.Media.FontFamily)Application.Current.Resources["Theme.FontFamily"],
                Background=(System.Windows.Media.Brush)Application.Current.Resources["CardBg"],
                Foreground=(System.Windows.Media.Brush)Application.Current.Resources["TextPrimary"],
                BorderBrush=(System.Windows.Media.Brush)Application.Current.Resources["BorderColor"],
                BorderThickness=new Thickness(1), Padding=new Thickness(6,4,6,4),
                VerticalContentAlignment=VerticalAlignment.Center,
            };
            folderBox.SetResourceReference(FontSizeProperty, "Theme.FontSize.Small");
            var browseBtn = new System.Windows.Controls.Button
            {
                Content="…", Style=(Style)Application.Current.Resources["FlatBtn"],
                Padding=new Thickness(10,4,10,4), Margin=new Thickness(6,0,0,0),
                VerticalAlignment=VerticalAlignment.Center,
            };
            System.Windows.Controls.Grid.SetColumn(folderBox,0);
            System.Windows.Controls.Grid.SetColumn(browseBtn,1);
            folderRow.Children.Add(folderBox); folderRow.Children.Add(browseBtn);
            panel.Children.Add(folderRow);

            var willInstallToTb = new System.Windows.Controls.TextBlock
            {
                Text="Will install to:",
                Foreground=(System.Windows.Media.Brush)Application.Current.Resources["TextMuted"],
                Margin=new Thickness(0,0,0,2),
            };
            willInstallToTb.SetResourceReference(FontSizeProperty, "Theme.FontSize.Tiny");
            panel.Children.Add(willInstallToTb);
            var resolvedLabel = new System.Windows.Controls.TextBlock
            {
                FontFamily=(System.Windows.Media.FontFamily)Application.Current.Resources["Theme.FontFamily"],
                FontWeight=FontWeights.Bold,
                Foreground=(System.Windows.Media.Brush)Application.Current.Resources["Accent"],
                TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,0,0,16),
            };
            resolvedLabel.SetResourceReference(FontSizeProperty, "Theme.FontSize.Small");

            void UpdateResolved()
            {
                var parent = folderBox.Text.Trim();
                resolvedLabel.Text = string.IsNullOrEmpty(parent) ? ""
                    : System.IO.Path.GetFullPath(System.IO.Path.Combine(parent, InstallFolderName));
            }
            UpdateResolved();
            folderBox.TextChanged += (_,_) => UpdateResolved();
            panel.Children.Add(resolvedLabel);

            browseBtn.Click += (_,_) =>
            {
                var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description=Lang.T("InstallSelectFolder"),
                    SelectedPath=folderBox.Text.Trim(),
                    ShowNewFolderButton=true,
                };
                if (dlg.ShowDialog()==System.Windows.Forms.DialogResult.OK)
                    folderBox.Text=dlg.SelectedPath;
            };

            var btns = new System.Windows.Controls.StackPanel
            {
                Orientation=System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment=HorizontalAlignment.Right,
            };
            var btnCancel  = new System.Windows.Controls.Button { Content=Lang.T("BtnCancel"),  Style=(Style)Application.Current.Resources["FlatBtn"],    Padding=new Thickness(14,6,14,6), Margin=new Thickness(0,0,8,0) };
            var btnInstall = new System.Windows.Controls.Button { Content=Lang.T("BtnInstall"), Style=(Style)Application.Current.Resources["SuccessBtn"], Padding=new Thickness(14,6,14,6) };
            btnCancel.Click  += (_,_) => { finalResult=null; win.Close(); };
            btnInstall.Click += (_,_) =>
            {
                var parent = folderBox.Text.Trim();
                if (string.IsNullOrEmpty(parent)) { resolvedLabel.Text="Please select a folder."; return; }
                finalResult = System.IO.Path.GetFullPath(System.IO.Path.Combine(parent, InstallFolderName));
                // Save chosen path to config immediately
                ConfigSvc.Config.InstalledPath = finalResult;
                ConfigSvc.Save();
                win.Close();
            };
            btns.Children.Add(btnCancel); btns.Children.Add(btnInstall);
            panel.Children.Add(btns);
            border.Child=panel; win.Content=border;
            win.ShowDialog();
            return finalResult ?? "";
        }

        private static void CopyDirRecursive(string src, string dst)
        {
            System.IO.Directory.CreateDirectory(dst);
            foreach (var f in System.IO.Directory.GetFiles(src))
                System.IO.File.Copy(f, System.IO.Path.Combine(dst,
                    System.IO.Path.GetFileName(f)), overwrite: true);
            foreach (var dir in System.IO.Directory.GetDirectories(src))
            {
                var name = System.IO.Path.GetFileName(dir);
                var dest = System.IO.Path.Combine(dst, name);
                CopyDirRecursive(dir, dest);
            }
        }

        public bool GetStartWithWindows()
        {
            // Check if our scheduled task exists
            try
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "schtasks.exe", "/query /tn \"MasselGUARD\"")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                p?.WaitForExit(2000);
                return p?.ExitCode == 0;
            }
            catch { return false; }
        }

        public void SetStartWithWindows(bool enable)
        {
            if (enable)
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                RunPS($@"$a=New-ScheduledTaskAction -Execute '{exe}';$t=New-ScheduledTaskTrigger -AtLogOn;$p=New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest;Register-ScheduledTask -TaskName 'MasselGUARD' -Action $a -Trigger $t -Principal $p -Force");
            }
            else
            {
                RunPS("Unregister-ScheduledTask -TaskName 'MasselGUARD' -Confirm:$false -ErrorAction SilentlyContinue");
            }
            ConfigSvc.Config.StartWithWindows = enable;
            ConfigSvc.Save();
        }

        private static bool RunPS(string script)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                    $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"")
                { UseShellExecute = false, CreateNoWindow = true };
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.WaitForExit(30000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }
        public string? GetInstalledPath()
        {
            // Primary: config.json InstalledPath (stored as directory)
            var dir = ConfigSvc.Config.InstalledPath;
            if (!string.IsNullOrEmpty(dir))
            {
                var exe = System.IO.Path.Combine(dir, "MasselGUARD.exe");
                if (System.IO.File.Exists(exe)) return dir;
            }

            // Fallback: registry written during install
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(InstallRegKey);
                if (key?.GetValue("InstallPath") is string regDir &&
                    System.IO.File.Exists(System.IO.Path.Combine(regDir, "MasselGUARD.exe")))
                {
                    // Sync back to config
                    ConfigSvc.Config.InstalledPath = regDir;
                    ConfigSvc.Save();
                    return regDir;
                }
            }
            catch { }

            return null;
        }

        public bool IsRunningPortableWhileInstalled()
        {
            var installed = GetInstalledPath();
            if (installed == null) return false;
            return !installed.Equals(Environment.ProcessPath,
                StringComparison.OrdinalIgnoreCase);
        }

        // RunUpdate() is superseded by UpdateChecker.UpdateAsync().
        // Kept as a no-op to avoid compilation errors if any dead code still references it.
        [System.Obsolete("Use UpdateChecker.UpdateAsync() instead.")]
        public void RunUpdate() => LogSvc.Warn("RunUpdate() called — use UpdateChecker.UpdateAsync().");

        public (MessageBoxResult result, bool suppress) ShowUpdatePrompt(string msg, string title)
        {
            // Simple implementation — SettingsWindow has the suppress checkbox
            var res = MessageBox.Show(msg, title, MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            return (res, false);
        }

        // ── WireGuard install dir detection ───────────────────────────────────
        public static string? DetectWireGuardInstallDir()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey(@"SOFTWARE\WireGuard");
                if (key?.GetValue("InstallDirectory") is string d &&
                    System.IO.Directory.Exists(d)) return d.TrimEnd('\\', '/');
            }
            catch { }
            var def = @"C:\Program Files\WireGuard";
            return System.IO.File.Exists(System.IO.Path.Combine(def, "wireguard.exe"))
                ? def : null;
        }

        public static string? FindWireGuardExe()
        {
            var dir = DetectWireGuardInstallDir();
            if (dir != null)
            {
                var p = System.IO.Path.Combine(dir, "wireguard.exe");
                if (System.IO.File.Exists(p)) return p;
            }
            return null;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Info / statistics section
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>Tunnels the user has hidden by clicking their legend entry.</summary>
        private readonly HashSet<string> _hiddenChartTunnels =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Colour assigned to each tunnel name (populated by RenderChart).</summary>
        private readonly Dictionary<string, System.Windows.Media.Color> _chartColors =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>History entries + period totals per tunnel, cached per render pass.</summary>
        private record ChartTunnelData(
            List<Models.ConnectionHistoryEntry> Entries,
            long PeriodRx, long PeriodTx);
        private readonly Dictionary<string, ChartTunnelData> _chartData =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Previous Rx/Tx snapshot for live KB/s computation.</summary>
        private readonly Dictionary<string, (long rx, long tx)> _prevStats =
            new(StringComparer.OrdinalIgnoreCase);

        // Session navigation
        private int _navIndex = -1;   // -1 = no active nav selection

        // Chart layout — set by RenderChartCore, read by MouseMove hit-testing
        private const double ChartBarH    = 16;
        private const double ChartBarGap  =  5;
        private const double ChartSsidGap =  2;
        /// <summary>Y coordinate where the WiFi band starts (0 when no WiFi rendered).</summary>
        private double _chartWifiBandTop  =  0;
        /// <summary>Ordered list of SSIDs rendered in the WiFi band (top to bottom).</summary>
        private List<string> _chartSsids  = new();

        // WiFi SSID chart support
        private readonly HashSet<string> _hiddenWifiSsids =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, System.Windows.Media.Color> _wifiColors =
            new(StringComparer.OrdinalIgnoreCase);

        // Stable per-name colour INDEX (only ever grows) so each tunnel / SSID keeps the
        // same slot across renders. The actual colour is derived from the active THEME at
        // draw time (TimelinePaletteColor), so the timeline matches whatever theme is
        // selected — no manual colour picking, and it scales to any number of tunnels/SSIDs.
        private readonly Dictionary<string, int> _chartColorIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _wifiColorIndex  = new(StringComparer.OrdinalIgnoreCase);

        private static int ColorIndexFor(Dictionary<string, int> map, string name)
        {
            if (!map.TryGetValue(name, out var idx)) { idx = map.Count; map[name] = idx; }
            return idx;
        }

        /// <summary>
        /// Timeline segment colour for the Nth tunnel/SSID, derived from the theme's Accent.
        /// Hues fan out from the accent by the golden angle (evenly spread + distinct for any
        /// count); the WiFi band is phase-shifted 180° so it reads differently from the
        /// tunnel bar. Saturation/brightness adapt to a dark vs light theme.
        /// </summary>
        private System.Windows.Media.Color TimelinePaletteColor(int index, bool wifi)
        {
            double baseHue = 210;   // fallback if Accent can't be read
            if (FindResource("Accent") is SolidColorBrush ab)
                RgbToHsv(ab.Color, out baseHue, out _, out _);

            bool dark = true;
            if (FindResource("WindowBg") is SolidColorBrush wb)
                dark = (wb.Color.R * 0.299 + wb.Color.G * 0.587 + wb.Color.B * 0.114) < 128;

            double hue = baseHue + (wifi ? 180.0 : 0.0) + index * 137.50776;
            double sat = wifi ? 0.50 : 0.62;
            double val = dark ? 0.85 : 0.68;
            return HsvToColor(hue, sat, val);
        }

        private static void RgbToHsv(System.Windows.Media.Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            h = 0;
            if (d != 0)
            {
                if      (max == r) h = 60 * (((g - b) / d) % 6);
                else if (max == g) h = 60 * (((b - r) / d) + 2);
                else               h = 60 * (((r - g) / d) + 4);
            }
            if (h < 0) h += 360;
            s = max == 0 ? 0 : d / max;
            v = max;
        }

        private static System.Windows.Media.Color HsvToColor(double h, double s, double v)
        {
            h = ((h % 360) + 360) % 360;
            double c = v * s, x = c * (1 - Math.Abs((h / 60.0 % 2) - 1)), m = v - c;
            double r = 0, g = 0, b = 0;
            if      (h <  60) { r = c; g = x; }
            else if (h < 120) { r = x; g = c; }
            else if (h < 180) { g = c; b = x; }
            else if (h < 240) { g = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else              { r = c; b = x; }
            return System.Windows.Media.Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }

        /// <summary>Apply the configured InfoSectionMode — called on load and after settings save.</summary>
        public void ApplyInfoSectionMode()
        {
            var cfg = ConfigSvc.Config;

            // Panel is visible when at least one layer has both capture and display
            // enabled, or when the Data-usage view is selected (it needs connection
            // history stored, not the timeline display toggle).
            bool panelVisible = (cfg.ShowTimeline     && cfg.StoreConnectionHistory)
                             || (cfg.ShowWifiInChart   && cfg.StoreWifiHistory)
                             || (UsageMode             && cfg.StoreConnectionHistory);
            InfoSectionBorder.Visibility = panelVisible ? Visibility.Visible : Visibility.Collapsed;

            // Sync range toggle buttons with persisted config
            int rangeDays = cfg.InfoTimeRangeDays;
            if (Range24hBtn != null) Range24hBtn.IsChecked  = rangeDays == 1;
            if (Range7dBtn  != null) Range7dBtn.IsChecked   = rangeDays == 7;
            if (Range31dBtn != null) Range31dBtn.IsChecked  = rangeDays == 31;

            // Sync the Timeline/Usage view toggle + nav-button visibility
            if (ModeUsageBtn    != null) ModeUsageBtn.IsChecked    = UsageMode;
            if (ModeTimelineBtn != null) ModeTimelineBtn.IsChecked = !UsageMode;
            SetNavButtonsVisible(!UsageMode);

            if (panelVisible)
                RefreshInfoSection();
        }

        private void InfoRange_Changed(object sender, RoutedEventArgs e)
        {
            ConfigSvc.Config.InfoTimeRangeDays =
                Range31dBtn?.IsChecked == true ? 31 :
                Range7dBtn?.IsChecked  == true ?  7 : 1;
            ConfigSvc.Save();
            RefreshInfoSection();
        }

        private void TimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
            => RefreshInfoSection();

        /// <summary>Called each second from OnStatusTick when the info section is visible.</summary>
        private void RefreshInfoSection()
        {
            if (InfoSectionBorder.Visibility != Visibility.Visible) return;

            // Update live KB/s snapshots for active tunnels
            foreach (var tvm in _vm.TunnelList.Where(t => t.IsActive))
            {
                var s = TunnelDll.GetTrafficStats(tvm.Name);
                if (s.AdapterFound) _prevStats[tvm.Name] = (s.RxBytes, s.TxBytes);
            }

            // Remember nav state so we can restore it after the redraw
            int savedNavIndex = _navIndex;
            RenderChart();

            // Re-pin the nav tooltip if the user had navigated to a session
            if (savedNavIndex >= 0)
            {
                var entries = GetNavEntries();
                if (savedNavIndex < entries.Count)
                {
                    _navIndex = savedNavIndex;
                    UpdateNavLabel(_navIndex, entries.Count);
                    ShowNavTooltip(entries[_navIndex].Name, entries[_navIndex].Entry);
                }
            }
        }

        // ── Chart rendering ───────────────────────────────────────────────────

        private bool UsageMode => ConfigSvc.Config.InfoPanelMode == "usage";

        private void RenderChart()
        {
            if (UsageMode) RenderUsageChart();
            else           RenderChartCore();
        }

        // ── Data-usage bar chart (Timeline ⇄ Data usage toggle) ─────────────────
        private sealed class UsageBucket
        {
            public DateTime Start;
            public DateTime End;
            public readonly Dictionary<string, long> ByTunnel = new(StringComparer.OrdinalIgnoreCase);
            public long Total { get { long s = 0; foreach (var v in ByTunnel.Values) s += v; return s; } }
        }
        private readonly List<UsageBucket> _usageBuckets = new();

        /// <summary>
        /// Renders per-bucket data usage as stacked bars over the selected range.
        /// 24h → hourly buckets, 7d / 31d → daily buckets; each bucket is coloured
        /// per tunnel (same palette as the timeline). Bytes are attributed to the
        /// bucket of a session's ConnectedAt (matching the cap accounting); the live
        /// session's bytes land in the current bucket. The selected range doubles as
        /// the cap period, so the legend shows progress toward each tunnel's
        /// day / week / month cap.
        /// </summary>
        private void RenderUsageChart()
        {
            TimelineCanvas.Children.Clear();
            ChartOverlayCanvas.Children.Clear();
            LegendPanel.Children.Clear();
            WifiLegendPanel.Children.Clear();
            WifiLegendContainer.Visibility = Visibility.Collapsed;
            _chartData.Clear();
            _chartColors.Clear();
            _usageBuckets.Clear();
            _navIndex = -1;

            double W = TimelineCanvas.ActualWidth;
            if (W < 20) return;

            int rangeDays = ConfigSvc.Config.InfoTimeRangeDays;   // 1 / 7 / 31
            var now = DateTime.Now;

            // ── Build buckets ─────────────────────────────────────────────────
            int count; TimeSpan unit; DateTime first;
            if (rangeDays == 1)
            {
                count = 24; unit = TimeSpan.FromHours(1);
                var hourFloor = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
                first = hourFloor - TimeSpan.FromHours(count - 1);
            }
            else
            {
                count = rangeDays; unit = TimeSpan.FromDays(1);
                first = now.Date - TimeSpan.FromDays(count - 1);
            }
            for (int i = 0; i < count; i++)
            {
                var bs = first + TimeSpan.FromTicks(unit.Ticks * i);
                _usageBuckets.Add(new UsageBucket { Start = bs, End = bs + unit });
            }
            var rangeStart = _usageBuckets[0].Start;
            var rangeEnd   = _usageBuckets[^1].End;
            int BucketIndex(DateTime t)
                => (t < rangeStart || t >= rangeEnd) ? -1 : (int)((t - rangeStart).Ticks / unit.Ticks);

            // ── Tunnels present + colours ─────────────────────────────────────
            var entries = HistorySvc.Entries
                .Where(e => e.ConnectedAt >= rangeStart && e.ConnectedAt < rangeEnd)
                .ToList();
            var tunnelNames = entries.Select(e => e.TunnelName)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
            foreach (var tvm in _vm.TunnelList.Where(t => t.IsActive))
                if (!tunnelNames.Contains(tvm.Name, StringComparer.OrdinalIgnoreCase))
                    tunnelNames.Add(tvm.Name);
            foreach (var name in tunnelNames)
                _chartColors[name] = TimelinePaletteColor(ColorIndexFor(_chartColorIndex, name), wifi: false);

            // ── Fill buckets: closed sessions at their ConnectedAt bucket ─────
            foreach (var e in entries)
            {
                if (_hiddenChartTunnels.Contains(e.TunnelName)) continue;
                int bi = BucketIndex(e.ConnectedAt);
                if (bi < 0) continue;
                long bytes = e.SessionRxBytes + e.SessionTxBytes;
                if (bytes <= 0) continue;
                var d = _usageBuckets[bi].ByTunnel;
                d.TryGetValue(e.TunnelName, out var cur);
                d[e.TunnelName] = cur + bytes;
            }
            // Live session → current (last) bucket
            foreach (var tvm in _vm.TunnelList.Where(t => t.IsActive))
            {
                if (_hiddenChartTunnels.Contains(tvm.Name)) continue;
                if (_prevStats.TryGetValue(tvm.Name, out var lv))
                {
                    long bytes = lv.rx + lv.tx;
                    if (bytes <= 0) continue;
                    var d = _usageBuckets[^1].ByTunnel;
                    d.TryGetValue(tvm.Name, out var cur);
                    d[tvm.Name] = cur + bytes;
                }
            }

            // ── Geometry ──────────────────────────────────────────────────────
            const double axisH  = 18;
            const double chartH = 120;
            TimelineCanvas.Height = chartH + axisH;

            // Lines are per-tunnel (not stacked), so scale to the largest single value.
            long maxSingle = 0;
            foreach (var b in _usageBuckets)
                foreach (var kv in b.ByTunnel)
                    if (!_hiddenChartTunnels.Contains(kv.Key) && kv.Value > maxSingle) maxSingle = kv.Value;
            bool empty = maxSingle <= 0;
            if (maxSingle <= 0) maxSingle = 1;

            var gridBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(28, 128, 128, 128));
            var tickBrush = (System.Windows.Media.Brush)FindResource("TextMuted");

            // Baseline + a faint mid gridline + max-value label
            TimelineCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0, Y1 = chartH, X2 = W, Y2 = chartH, Stroke = gridBrush, StrokeThickness = 1,
            });
            TimelineCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0, Y1 = chartH / 2, X2 = W, Y2 = chartH / 2, Stroke = gridBrush, StrokeThickness = 1,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 3, 3 },
            });
            if (!empty)
            {
                var maxLbl = new TextBlock { Text = FormatInfoBytes(maxSingle), FontSize = 8, Foreground = tickBrush };
                System.Windows.Controls.Canvas.SetLeft(maxLbl, 2);
                System.Windows.Controls.Canvas.SetTop(maxLbl, 0);
                TimelineCanvas.Children.Add(maxLbl);
            }

            // ── Lines with dots — one series per tunnel ───────────────────────
            double slot = W / count;
            double CenterX(int i) => i * slot + slot / 2.0;
            double YFor(long v)  => chartH - v / (double)maxSingle * chartH;

            foreach (var name in tunnelNames)
            {
                if (_hiddenChartTunnels.Contains(name)) continue;
                var stroke = new System.Windows.Media.SolidColorBrush(_chartColors[name]);

                // Line through every bucket centre (zeros sit on the baseline).
                if (count > 1)
                {
                    var pts = new System.Windows.Media.PointCollection(count);
                    for (int i = 0; i < count; i++)
                    {
                        _usageBuckets[i].ByTunnel.TryGetValue(name, out var v);
                        pts.Add(new System.Windows.Point(CenterX(i), YFor(v)));
                    }
                    TimelineCanvas.Children.Add(new System.Windows.Shapes.Polyline
                    {
                        Points = pts, Stroke = stroke, StrokeThickness = 1.6,
                        StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                    });
                }

                // Dots only where there is usage, so the markers mean something.
                const double dotR = 5.0;
                for (int i = 0; i < count; i++)
                {
                    if (!_usageBuckets[i].ByTunnel.TryGetValue(name, out var v) || v <= 0) continue;
                    var dot = new System.Windows.Shapes.Ellipse { Width = dotR, Height = dotR, Fill = stroke };
                    System.Windows.Controls.Canvas.SetLeft(dot, CenterX(i) - dotR / 2);
                    System.Windows.Controls.Canvas.SetTop(dot, YFor(v) - dotR / 2);
                    TimelineCanvas.Children.Add(dot);
                }
            }

            // ── Limit markers — a red ring on the bucket where cumulative usage
            //    first reached the cap for this range. The line then either drops
            //    (killed) or continues (limit overruled), which the eye reads off. ──
            string rangeWord = rangeDays == 1 ? "daily" : rangeDays == 7 ? "weekly" : "monthly";
            var dangerBrush = (System.Windows.Media.Brush)FindResource("Danger");
            foreach (var name in tunnelNames)
            {
                if (_hiddenChartTunnels.Contains(name)) continue;
                var stored = ConfigSvc.Config.Tunnels
                    .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (stored == null) continue;
                int capMB = rangeDays == 1 ? stored.DailyCapMB
                          : rangeDays == 7 ? stored.WeeklyCapMB
                          :                  stored.MonthlyCapMB;
                if (capMB <= 0) continue;
                long capBytes = (long)capMB * 1_048_576L;

                long cum = 0; int hitBucket = -1;
                for (int i = 0; i < count; i++)
                {
                    _usageBuckets[i].ByTunnel.TryGetValue(name, out var v);
                    cum += v;
                    if (cum >= capBytes) { hitBucket = i; break; }
                }
                if (hitBucket < 0) continue;

                _usageBuckets[hitBucket].ByTunnel.TryGetValue(name, out var hv);
                double mx = CenterX(hitBucket), my = YFor(hv);
                const double ringR = 11.0;
                var ring = new System.Windows.Shapes.Ellipse
                {
                    Width = ringR, Height = ringR,
                    Stroke = dangerBrush, StrokeThickness = 2,
                    Fill = System.Windows.Media.Brushes.Transparent,
                    ToolTip = $"{name}: {rangeWord} limit reached ({FormatInfoBytes(capBytes)})",
                };
                System.Windows.Controls.Canvas.SetLeft(ring, mx - ringR / 2);
                System.Windows.Controls.Canvas.SetTop(ring, my - ringR / 2);
                TimelineCanvas.Children.Add(ring);
            }

            // ── Axis labels ───────────────────────────────────────────────────
            int labelEvery = rangeDays == 1 ? 4 : rangeDays == 7 ? 1 : 5;
            for (int i = 0; i < count; i += labelEvery)
            {
                var b   = _usageBuckets[i];
                var lbl = rangeDays == 1 ? b.Start.ToString("HH:mm") : b.Start.ToString("ddd d");
                var tb  = new TextBlock { Text = lbl, FontSize = 8, Foreground = tickBrush };
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double cx = i * slot + slot / 2.0;
                System.Windows.Controls.Canvas.SetLeft(tb, Math.Clamp(cx - tb.DesiredSize.Width / 2, 0, Math.Max(0, W - tb.DesiredSize.Width)));
                System.Windows.Controls.Canvas.SetTop(tb, chartH + 2);
                TimelineCanvas.Children.Add(tb);
            }

            // ── Legend: tunnel name + colour swatch (usage lives on the chart) ─
            foreach (var name in tunnelNames)
            {
                bool hidden = _hiddenChartTunnels.Contains(name);
                var  color  = _chartColors[name];

                var dot = new System.Windows.Shapes.Rectangle
                {
                    Width = 8, Height = 8, RadiusX = 2, RadiusY = 2,
                    Fill = new System.Windows.Media.SolidColorBrush(color),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var nameTb = new TextBlock
                {
                    Text = name, FontSize = 9,
                    Foreground = tickBrush,
                    Margin = new Thickness(3, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var item = new StackPanel
                {
                    Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                    Opacity = hidden ? 0.35 : 1.0, Cursor = System.Windows.Input.Cursors.Hand,
                    Margin = new Thickness(0, 0, 2, 0),
                };
                item.Children.Add(dot);
                item.Children.Add(nameTb);
                var capturedName = name;
                item.MouseLeftButtonUp += (_, _) =>
                {
                    if (_hiddenChartTunnels.Contains(capturedName)) _hiddenChartTunnels.Remove(capturedName);
                    else _hiddenChartTunnels.Add(capturedName);
                    RenderChart();
                };
                LegendPanel.Children.Add(item);
            }

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
                new Action(UpdateLegendScrollMarkers));
        }

        private void RenderChartCore()
        {
            TimelineCanvas.Children.Clear();
            ChartOverlayCanvas.Children.Clear();
            LegendPanel.Children.Clear();
            WifiLegendPanel.Children.Clear();
            WifiLegendContainer.Visibility = Visibility.Collapsed;
            _chartData.Clear();
            _chartColors.Clear();
            _navIndex = -1;

            double W = TimelineCanvas.ActualWidth;
            if (W < 20) return;

            var now   = DateTime.Now;
            var span  = ConfigSvc.Config.InfoTimeRangeDays == 31 ? TimeSpan.FromDays(31) : ConfigSvc.Config.InfoTimeRangeDays == 7 ? TimeSpan.FromDays(7) : TimeSpan.FromHours(24);
            var start = now - span;

            // Fixed bar geometry
            const double barH    = 16;
            const double barGap  = 5;
            const double ssidGap = 2;   // gap between per-SSID rows
            const double axisH   = 20;  // tall enough for two-line "ddd\nHH:mm" labels

            // ── Collect tunnels in the window ─────────────────────────────────
            bool showTunnels = ConfigSvc.Config.ShowTimeline && ConfigSvc.Config.StoreConnectionHistory;

            var allEntries = showTunnels
                ? HistorySvc.Entries
                    .Where(e => e.ConnectedAt >= start || (e.DisconnectedAt ?? now) >= start)
                    .ToList()
                : new List<Models.ConnectionHistoryEntry>();

            var tunnelNames = allEntries
                .Select(e => e.TunnelName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();

            if (showTunnels)
                foreach (var tvm in _vm.TunnelList.Where(t => t.IsActive))
                    if (!tunnelNames.Contains(tvm.Name, StringComparer.OrdinalIgnoreCase))
                        tunnelNames.Add(tvm.Name);

            // ── Assign colours + build cached data ───────────────────────────
            for (int i = 0; i < tunnelNames.Count; i++)
            {
                string name   = tunnelNames[i];
                var    color  = TimelinePaletteColor(ColorIndexFor(_chartColorIndex, name), wifi: false);
                _chartColors[name] = color;

                var tunnelEntries = allEntries
                    .Where(e => e.TunnelName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.ConnectedAt)
                    .ToList();

                // Synthesise open entry for active tunnel not yet in history
                var tvm = _vm.TunnelList
                    .FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (tvm?.IsActive == true && !tunnelEntries.Any(e => e.DisconnectedAt == null))
                    tunnelEntries.Add(new Models.ConnectionHistoryEntry
                        { TunnelName = name, ConnectedAt = now, DisconnectedAt = null });

                // Period totals (completed sessions only — active session RxBytes ~ cumulative since adapter up)
                long periodRx = tunnelEntries.Where(e => e.DisconnectedAt.HasValue).Sum(e => e.SessionRxBytes);
                long periodTx = tunnelEntries.Where(e => e.DisconnectedAt.HasValue).Sum(e => e.SessionTxBytes);
                if (tvm?.IsActive == true && _prevStats.TryGetValue(name, out var live))
                    { periodRx += live.rx; periodTx += live.tx; }

                _chartData[name] = new ChartTunnelData(tunnelEntries, periodRx, periodTx);
            }

            // ── WiFi entries ──────────────────────────────────────────────────
            var ssidEntriesInWindow = (ConfigSvc.Config.ShowWifiInChart && ConfigSvc.Config.StoreWifiHistory)
                ? HistorySvc.SsidEntries
                    .Where(e => e.ConnectedAt >= start || (e.DisconnectedAt ?? now) >= start)
                    .OrderBy(e => e.ConnectedAt).ToList()
                : new List<Models.WifiHistoryEntry>();
            bool showWifi = ssidEntriesInWindow.Count > 0;

            // All SSIDs on one combined row (like the tunnel bar)
            var distinctSsids = showWifi
                ? ssidEntriesInWindow.Select(e => e.Ssid)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList()
                : new List<string>();
            double wifiTotalH = showWifi ? barGap + barH : 0;

            // Set canvas height so the card auto-sizes
            double totalH     = barH + wifiTotalH + axisH;
            TimelineCanvas.Height = totalH;
            double wifiBandTop = barH + barGap;

            // Expose layout to hover hit-testing (single combined row)
            _chartWifiBandTop = showWifi ? wifiBandTop : double.MaxValue;

            // ── Coordinate helper ─────────────────────────────────────────────
            double LocalX(DateTime dt) =>
                Math.Clamp((dt - start).TotalSeconds / span.TotalSeconds * W, 0, W);

            int n = tunnelNames.Count;

            // ── Vertical grid lines + time-axis labels ────────────────────────
            var gridBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(28, 128, 128, 128));
            var tickBrush = (System.Windows.Media.Brush)FindResource("TextMuted");

            double barsH  = barH + wifiTotalH;  // total height of bar area
            int rangeDays2 = ConfigSvc.Config.InfoTimeRangeDays;
            int tickCount  = 7;   // 7 ticks works for all three ranges
            for (int t = 0; t <= tickCount; t++)
            {
                double frac = (double)t / tickCount;
                double x    = Math.Round(frac * W);

                TimelineCanvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = x, Y1 = 0, X2 = x, Y2 = barsH,
                    Stroke = gridBrush, StrokeThickness = 1,
                });

                var dt  = start + TimeSpan.FromSeconds(span.TotalSeconds * frac);
                var lbl = rangeDays2 >= 7 ? dt.ToString("ddd\nHH:mm") : dt.ToString("HH:mm");
                var tb  = new TextBlock
                {
                    Text = lbl, FontSize = 8, Foreground = tickBrush,
                    TextAlignment = TextAlignment.Center, LineHeight = 9,
                };
                tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double lblW = tb.DesiredSize.Width;
                System.Windows.Controls.Canvas.SetLeft(tb, Math.Clamp(x - lblW / 2, 0, Math.Max(0, W - lblW)));
                System.Windows.Controls.Canvas.SetTop(tb, barsH);
                TimelineCanvas.Children.Add(tb);
            }

            var greyDisconn = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(90, 128, 128, 128));

            if (n > 0)
                DrawTunnelBar(W, barH, start, now, LocalX, greyDisconn);

            if (showWifi)
                DrawWifiBand(W, wifiBandTop, barH, ssidGap, start, now, LocalX, ssidEntriesInWindow, distinctSsids);

            // ── Legend ────────────────────────────────────────────────────────
            for (int i = 0; i < tunnelNames.Count; i++)
            {
                string name   = tunnelNames[i];
                bool   hidden = _hiddenChartTunnels.Contains(name);
                var    color  = _chartColors[name];

                var dot = new System.Windows.Shapes.Rectangle
                {
                    Width   = 8, Height = 8,
                    RadiusX = 2, RadiusY = 2,
                    Fill    = new System.Windows.Media.SolidColorBrush(color),
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var nameTb = new TextBlock
                {
                    Text              = name,
                    FontSize          = 9,
                    Foreground        = tickBrush,
                    Margin            = new Thickness(3, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var item = new StackPanel
                {
                    Orientation       = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity           = hidden ? 0.35 : 1.0,
                    Cursor            = System.Windows.Input.Cursors.Hand,
                    Margin            = new Thickness(0, 0, 2, 0),
                };
                item.Children.Add(dot);
                item.Children.Add(nameTb);

                // Tooltip: period totals
                if (_chartData.TryGetValue(name, out var cd) && (cd.PeriodRx > 0 || cd.PeriodTx > 0))
                {
                    var tip = new StackPanel { Orientation = Orientation.Vertical };
                    tip.Children.Add(new TextBlock { Text = name, FontSize = 10, FontWeight = FontWeights.SemiBold });
                    tip.Children.Add(new TextBlock { Text = $"Period total  ↑ {FormatInfoBytes(cd.PeriodTx)}   ↓ {FormatInfoBytes(cd.PeriodRx)}", FontSize = 9 });
                    item.ToolTip = new ToolTip { Content = tip };
                }

                // Click toggles visibility
                var capturedName = name;
                item.MouseLeftButtonUp += (_, _) =>
                {
                    if (_hiddenChartTunnels.Contains(capturedName))
                        _hiddenChartTunnels.Remove(capturedName);
                    else
                        _hiddenChartTunnels.Add(capturedName);
                    RenderChart();
                };

                LegendPanel.Children.Add(item);
            }

            // Update scroll markers after layout pass
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,
                new Action(UpdateLegendScrollMarkers));

            // Nav label: show total session count
            UpdateNavLabel(-1, GetNavEntries().Count);
        }

        // ── Tunnel bar: all tunnels on one bar, stacked when simultaneous ────

        private void DrawTunnelBar(double W, double barH,
            DateTime start, DateTime now, Func<DateTime, double> LocalX,
            System.Windows.Media.Brush greyDisconn)
        {
            double midY = barH / 2;

            // Grey dashed baseline (visible in disconnected gaps)
            TimelineCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0, Y1 = midY, X2 = W, Y2 = midY,
                Stroke = greyDisconn, StrokeThickness = 1,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 3, 3 },
            });

            // Build sorted event list
            var events = new List<(DateTime t, string name, bool connect)>();
            foreach (var (name, data) in _chartData)
            {
                if (_hiddenChartTunnels.Contains(name)) continue;
                foreach (var e in data.Entries)
                {
                    var cs = e.ConnectedAt < start ? start : e.ConnectedAt;
                    if (cs >= now) continue;
                    events.Add((cs, name, true));
                    var ds = e.DisconnectedAt.HasValue
                        ? (e.DisconnectedAt.Value > now ? now : e.DisconnectedAt.Value) : now;
                    if (ds > cs) events.Add((ds, name, false));
                }
            }
            events.Sort((a, b) => a.t.CompareTo(b.t));

            // Walk intervals
            var      active = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            DateTime cur    = start;

            void DrawInterval(DateTime t1, DateTime t2)
            {
                if (active.Count == 0 || t2 <= t1) return;
                double x1     = LocalX(t1);
                double x2     = LocalX(t2);
                double sw     = Math.Max(x2 - x1, 1.5);
                int    cnt    = active.Count;
                double sliceH = barH / cnt;
                int    j      = 0;
                foreach (var tName in active)
                {
                    if (!_chartColors.TryGetValue(tName, out var col)) { j++; continue; }
                    double ty = j * sliceH;

                    var fillRect = new System.Windows.Shapes.Rectangle
                    {
                        Width  = sw, Height = sliceH,
                        Fill   = new System.Windows.Media.SolidColorBrush(
                                     System.Windows.Media.Color.FromArgb(55, col.R, col.G, col.B)),
                    };
                    System.Windows.Controls.Canvas.SetLeft(fillRect, x1);
                    System.Windows.Controls.Canvas.SetTop(fillRect, ty);
                    TimelineCanvas.Children.Add(fillRect);

                    TimelineCanvas.Children.Add(new System.Windows.Shapes.Line
                    {
                        X1 = x1, Y1 = ty, X2 = x1 + sw, Y2 = ty,
                        Stroke = new System.Windows.Media.SolidColorBrush(col),
                        StrokeThickness = 1.5,
                    });
                    j++;
                }
            }

            foreach (var ev in events)
            {
                DrawInterval(cur, ev.t);
                cur = ev.t;
                if (ev.connect) active.Add(ev.name);
                else            active.Remove(ev.name);
            }
            DrawInterval(cur, now);
        }

        // ── WiFi SSID band ────────────────────────────────────────────────────

        private void DrawWifiBand(double W, double bandTop, double rowH, double rowGap,
            DateTime start, DateTime now, Func<DateTime, double> LocalX,
            List<Models.WifiHistoryEntry> entries, List<string> orderedSsids)
        {
            // Separator line above the WiFi section
            TimelineCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 0, Y1 = bandTop - 1, X2 = W, Y2 = bandTop - 1,
                Stroke = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(35, 128, 128, 128)),
                StrokeThickness = 1,
            });

            // Re-derive every SSID colour from the theme each render (so theme switches
            // apply), keyed by a stable per-SSID index so each SSID keeps its colour.
            _wifiColors.Clear();
            foreach (var ssid in orderedSsids)
                _wifiColors[ssid] = TimelinePaletteColor(ColorIndexFor(_wifiColorIndex, ssid), wifi: true);

            // Single row background for the whole WiFi band
            var bgRect = new System.Windows.Shapes.Rectangle
            {
                Width = W, Height = rowH,
                Fill  = new System.Windows.Media.SolidColorBrush(
                            System.Windows.Media.Color.FromArgb(10, 128, 128, 128)),
            };
            System.Windows.Controls.Canvas.SetLeft(bgRect, 0);
            System.Windows.Controls.Canvas.SetTop(bgRect, bandTop);
            TimelineCanvas.Children.Add(bgRect);

            // All SSID segments on the same row, coloured per SSID
            foreach (var e in entries)
            {
                if (_hiddenWifiSsids.Contains(e.Ssid)) continue;
                if (!_wifiColors.TryGetValue(e.Ssid, out var color)) continue;

                var segStart = e.ConnectedAt < start ? start : e.ConnectedAt;
                var segEnd   = e.DisconnectedAt.HasValue
                    ? (e.DisconnectedAt.Value > now ? now : e.DisconnectedAt.Value) : now;
                if (segEnd <= segStart) continue;

                double x1 = LocalX(segStart);
                double x2 = LocalX(segEnd);
                double sw = Math.Max(x2 - x1, 1.5);

                var fillRect = new System.Windows.Shapes.Rectangle
                {
                    Width   = sw,
                    Height  = rowH - 2,
                    Fill    = new System.Windows.Media.SolidColorBrush(
                                  System.Windows.Media.Color.FromArgb(210, color.R, color.G, color.B)),
                    RadiusX = 1, RadiusY = 1,
                };
                System.Windows.Controls.Canvas.SetLeft(fillRect, x1);
                System.Windows.Controls.Canvas.SetTop(fillRect, bandTop + 1);
                TimelineCanvas.Children.Add(fillRect);

                // Inline SSID label if the segment is wide enough
                if (sw > 18)
                {
                    double luminance = color.R * 0.299 + color.G * 0.587 + color.B * 0.114;
                    var textBrush = new System.Windows.Media.SolidColorBrush(
                        luminance > 140 ? System.Windows.Media.Colors.Black
                                        : System.Windows.Media.Colors.White);
                    var lbl = new TextBlock
                    {
                        Text              = e.Ssid,
                        FontSize          = 7,
                        Foreground        = textBrush,
                        Width             = sw - 4,
                        TextTrimming      = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    System.Windows.Controls.Canvas.SetLeft(lbl, x1 + 2);
                    System.Windows.Controls.Canvas.SetTop(lbl, bandTop + (rowH - 9) / 2.0);
                    TimelineCanvas.Children.Add(lbl);
                }
            }

            // ── WiFi legend items ─────────────────────────────────────────────
            var mutedBrush = (System.Windows.Media.Brush)FindResource("TextMuted");

            foreach (var ssid in orderedSsids)
            {
                bool hidden = _hiddenWifiSsids.Contains(ssid);
                var  color  = _wifiColors[ssid];

                var dot = new System.Windows.Shapes.Rectangle
                {
                    Width = 8, Height = 8, RadiusX = 2, RadiusY = 2,
                    Fill  = new System.Windows.Media.SolidColorBrush(color),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var nameTb = new TextBlock
                {
                    Text              = ssid,
                    FontSize          = 9,
                    Foreground        = mutedBrush,
                    Margin            = new Thickness(3, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var item = new StackPanel
                {
                    Orientation       = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity           = hidden ? 0.35 : 1.0,
                    Cursor            = System.Windows.Input.Cursors.Hand,
                    Margin            = new Thickness(0, 0, 2, 0),
                };
                item.Children.Add(dot);
                item.Children.Add(nameTb);

                var capturedSsid = ssid;
                item.MouseLeftButtonUp += (_, _) =>
                {
                    if (_hiddenWifiSsids.Contains(capturedSsid)) _hiddenWifiSsids.Remove(capturedSsid);
                    else _hiddenWifiSsids.Add(capturedSsid);
                    RenderChart();
                };

                WifiLegendPanel.Children.Add(item);
            }

            WifiLegendContainer.Visibility =
                orderedSsids.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Legend scroll markers ─────────────────────────────────────────────

        private void LegendScroll_Changed(object sender, ScrollChangedEventArgs e)
            => UpdateLegendScrollMarkers();

        private void LegendScroll_Wheel(object sender,
            System.Windows.Input.MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta / 3.0);
                e.Handled = true;
            }
        }

        private void TunnelScrollLeft_Click(object sender, RoutedEventArgs e)
            => TunnelLegendScroll.ScrollToHorizontalOffset(
                   Math.Max(0, TunnelLegendScroll.HorizontalOffset - 80));
        private void TunnelScrollRight_Click(object sender, RoutedEventArgs e)
            => TunnelLegendScroll.ScrollToHorizontalOffset(
                   Math.Min(TunnelLegendScroll.ScrollableWidth,
                            TunnelLegendScroll.HorizontalOffset + 80));
        private void WifiScrollLeft_Click(object sender, RoutedEventArgs e)
            => WifiLegendScroll.ScrollToHorizontalOffset(
                   Math.Max(0, WifiLegendScroll.HorizontalOffset - 80));
        private void WifiScrollRight_Click(object sender, RoutedEventArgs e)
            => WifiLegendScroll.ScrollToHorizontalOffset(
                   Math.Min(WifiLegendScroll.ScrollableWidth,
                            WifiLegendScroll.HorizontalOffset + 80));

        private void UpdateLegendScrollMarkers()
        {
            SyncScrollPair(TunnelLegendScroll, TunnelScrollLeft, TunnelScrollRight);
            SyncScrollPair(WifiLegendScroll,   WifiScrollLeft,   WifiScrollRight);
        }

        private static void SyncScrollPair(ScrollViewer sv, Button left, Button right)
        {
            if (sv == null) return;
            left.Visibility  = sv.HorizontalOffset > 0.5
                ? Visibility.Visible : Visibility.Collapsed;
            right.Visibility = sv.HorizontalOffset < sv.ScrollableWidth - 0.5
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Chart hover (crosshair + tooltip) ────────────────────────────────

        private bool _usageHeightApplied;
        private const double UsageExtraHeight = 100;

        // Switch between Timeline and Data-usage views.
        private void InfoMode_Changed(object sender, RoutedEventArgs e)
        {
            // Fires while the XAML is still being parsed (ModeTimelineBtn's default
            // IsChecked), before ChartOverlayCanvas exists — bail until the panel's
            // controls are built. ApplyInfoSectionMode does the real sync afterwards.
            if (ConfigSvc?.Config == null || ChartOverlayCanvas == null) return;
            ConfigSvc.Config.InfoPanelMode = ModeUsageBtn?.IsChecked == true ? "usage" : "timeline";
            ConfigSvc.Save();
            SetNavButtonsVisible(!UsageMode);
            ApplyUsageWindowHeight();
            ChartOverlayCanvas.Children.Clear();
            RefreshInfoSection();
        }

        /// <summary>
        /// The Data-usage view is taller than the timeline, which would squeeze the
        /// tunnel list. Grow the window by <see cref="UsageExtraHeight"/> while it is
        /// shown and shrink back on return, so the list keeps its size. No-op when
        /// maximized (there's already room).
        /// </summary>
        private void ApplyUsageWindowHeight()
        {
            if (WindowState != WindowState.Normal) return;
            if (UsageMode && !_usageHeightApplied)
            {
                Height = Math.Min(Height + UsageExtraHeight, System.Windows.SystemParameters.WorkArea.Height);
                _usageHeightApplied = true;
            }
            else if (!UsageMode && _usageHeightApplied)
            {
                Height = Math.Max(MinHeight, Height - UsageExtraHeight);
                _usageHeightApplied = false;
            }
        }

        private void SetNavButtonsVisible(bool visible)
        {
            var v = visible ? Visibility.Visible : Visibility.Collapsed;
            if (ChartPrevBtn  != null) ChartPrevBtn.Visibility  = v;
            if (ChartNextBtn  != null) ChartNextBtn.Visibility  = v;
            if (ChartNavLabel != null) ChartNavLabel.Visibility = v;
        }

        // Hover for the data-usage chart: highlight the bucket column and show a
        // per-tunnel breakdown for that hour/day.
        private void UsageHover(System.Windows.Input.MouseEventArgs e)
        {
            ChartOverlayCanvas.Children.Clear();
            double W = TimelineCanvas.ActualWidth;
            double H = TimelineCanvas.ActualHeight;
            if (W < 4 || _usageBuckets.Count == 0) return;

            var    pos   = e.GetPosition(TimelineCanvas);
            int    count = _usageBuckets.Count;
            double slot  = W / count;
            int    idx   = Math.Clamp((int)(Math.Clamp(pos.X, 0, W) / slot), 0, count - 1);
            var    b     = _usageBuckets[idx];

            // Column highlight
            var hi = new System.Windows.Shapes.Rectangle
            {
                Width = slot, Height = Math.Max(0, H - 18),
                Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(28, 255, 255, 255)),
            };
            System.Windows.Controls.Canvas.SetLeft(hi, idx * slot);
            System.Windows.Controls.Canvas.SetTop(hi, 0);
            ChartOverlayCanvas.Children.Add(hi);

            int rangeDays = ConfigSvc.Config.InfoTimeRangeDays;
            var tipStack = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
            tipStack.Children.Add(new TextBlock
            {
                Text = rangeDays == 1 ? $"{b.Start:HH:mm}–{b.End:HH:mm}" : b.Start.ToString("ddd dd MMM"),
                FontSize = 9, FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
                Margin = new Thickness(0, 0, 0, 4),
            });

            if (b.Total <= 0)
            {
                tipStack.Children.Add(new TextBlock
                {
                    Text = "No data", FontSize = 9,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"),
                });
            }
            else
            {
                foreach (var kv in b.ByTunnel.OrderByDescending(k => k.Value))
                {
                    if (_hiddenChartTunnels.Contains(kv.Key) || kv.Value <= 0) continue;
                    var col = _chartColors.TryGetValue(kv.Key, out var c) ? c : System.Windows.Media.Colors.Gray;
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
                    row.Children.Add(new System.Windows.Shapes.Rectangle
                    {
                        Width = 7, Height = 7, RadiusX = 1, RadiusY = 1,
                        Fill = new System.Windows.Media.SolidColorBrush(col),
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
                    });
                    row.Children.Add(new TextBlock
                    {
                        Text = $"{kv.Key}   {FormatInfoBytes(kv.Value)}", FontSize = 9,
                        Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    tipStack.Children.Add(row);
                }
                tipStack.Children.Add(new TextBlock
                {
                    Text = $"Total   {FormatInfoBytes(b.Total)}", FontSize = 9, FontWeight = FontWeights.SemiBold,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
                    Margin = new Thickness(0, 2, 0, 0),
                });
            }

            var tipBorder = new Border
            {
                Background      = (System.Windows.Media.Brush)FindResource("CardBg"),
                BorderBrush     = (System.Windows.Media.Brush)FindResource("BorderColor"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Child           = tipStack,
            };
            tipBorder.Measure(new Size(600, 400));
            double bW = tipBorder.DesiredSize.Width, bH = tipBorder.DesiredSize.Height;
            double overlayW = ChartOverlayCanvas.ActualWidth, overlayH = ChartOverlayCanvas.ActualHeight;
            double xPos = idx * slot + slot + 8;
            if (xPos + bW > overlayW) xPos = idx * slot - bW - 8;
            xPos = Math.Clamp(xPos, 0, Math.Max(0, overlayW - bW));
            double yPos = Math.Clamp(pos.Y + 6, 0, Math.Max(0, overlayH - bH - 2));
            System.Windows.Controls.Canvas.SetLeft(tipBorder, xPos);
            System.Windows.Controls.Canvas.SetTop(tipBorder, yPos);
            ChartOverlayCanvas.Children.Add(tipBorder);
        }

        private void TimelineCanvas_MouseMove(object sender,
            System.Windows.Input.MouseEventArgs e)
        {
            if (UsageMode) { UsageHover(e); return; }

            if (_navIndex >= 0)
            {
                _navIndex = -1;
                UpdateNavLabel(-1, GetNavEntries().Count);
            }
            ChartOverlayCanvas.Children.Clear();

            double W = TimelineCanvas.ActualWidth;
            double H = TimelineCanvas.ActualHeight;
            if (W < 4) return;

            var    pos = e.GetPosition(TimelineCanvas);
            double mX  = Math.Clamp(pos.X, 0, W);

            var now    = DateTime.Now;
            var span   = ConfigSvc.Config.InfoTimeRangeDays == 31 ? TimeSpan.FromDays(31) : ConfigSvc.Config.InfoTimeRangeDays == 7 ? TimeSpan.FromDays(7) : TimeSpan.FromHours(24);
            var start  = now - span;
            var hoverT = start + TimeSpan.FromSeconds(mX / W * span.TotalSeconds);

            // Crosshair through the bar area (stop before the axis labels)
            const double axisH = 14;
            ChartOverlayCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = mX, Y1 = 0,
                X2 = mX, Y2 = Math.Max(0, H - axisH),
                Stroke = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(120, 255, 255, 255)),
                StrokeThickness = 1,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 2, 2 },
            });

            var tipStack = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };

            // Time header
            string timeLabel = ConfigSvc.Config.InfoTimeRangeDays >= 7
                ? hoverT.ToString("ddd dd MMM  HH:mm")
                : hoverT.ToString("HH:mm");
            tipStack.Children.Add(new TextBlock
            {
                Text       = timeLabel,
                FontSize   = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
                Margin     = new Thickness(0, 0, 0, 4),
            });

            // ── Tunnel connections active at this time ────────────────────────
            bool isNearNow = (now - hoverT).TotalSeconds < span.TotalSeconds / W * 4;

            foreach (var (name, data) in _chartData)
            {
                if (_hiddenChartTunnels.Contains(name)) continue;
                if (!_chartColors.TryGetValue(name, out var col)) continue;

                var session = data.Entries.FirstOrDefault(en =>
                    en.ConnectedAt <= hoverT &&
                    (en.DisconnectedAt == null || en.DisconnectedAt >= hoverT));
                if (session == null) continue;

                var rowStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
                rowStack.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = 7, Height = 7, RadiusX = 1, RadiusY = 1,
                    Fill = new System.Windows.Media.SolidColorBrush(col),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0),
                });

                string info;
                if (session.DisconnectedAt == null)
                {
                    if (isNearNow && _prevStats.TryGetValue(name, out var lv))
                        info = $"{name}   active   ↑ {FormatInfoBytes(lv.tx)}/s   ↓ {FormatInfoBytes(lv.rx)}/s";
                    else
                        info = $"{name}   active since {session.ConnectedAt.ToLocalTime():HH:mm}";
                }
                else
                {
                    var dur = session.DisconnectedAt.Value - session.ConnectedAt;
                    string durStr = dur.TotalMinutes < 60
                        ? $"{(int)dur.TotalMinutes}m"
                        : $"{(int)dur.TotalHours}h {dur.Minutes:D2}m";
                    string bw = (session.SessionRxBytes > 0 || session.SessionTxBytes > 0)
                        ? $"   ↑ {FormatInfoBytes(session.SessionTxBytes)}   ↓ {FormatInfoBytes(session.SessionRxBytes)}"
                        : "";
                    info = $"{name}   {session.ConnectedAt.ToLocalTime():HH:mm}–{session.DisconnectedAt.Value.ToLocalTime():HH:mm} ({durStr}){bw}";
                }

                rowStack.Children.Add(new TextBlock
                {
                    Text = info, FontSize = 9,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                tipStack.Children.Add(rowStack);
            }

            // ── WiFi active at this time ──────────────────────────────────────
            AddWifiRow(tipStack, hoverT);

            // Nothing to show — only crosshair, no tooltip
            if (tipStack.Children.Count <= 1) return;

            // Build and position tooltip
            var tipBorder = new Border
            {
                Background      = (System.Windows.Media.Brush)FindResource("CardBg"),
                BorderBrush     = (System.Windows.Media.Brush)FindResource("BorderColor"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Child           = tipStack,
            };
            tipBorder.Measure(new Size(600, 400));
            double bW = tipBorder.DesiredSize.Width;
            double bH = tipBorder.DesiredSize.Height;

            double overlayW = ChartOverlayCanvas.ActualWidth;
            double overlayH = ChartOverlayCanvas.ActualHeight;
            double xPos = mX + 12;
            if (xPos + bW > overlayW) xPos = mX - bW - 8;
            xPos = Math.Clamp(xPos, 0, Math.Max(0, overlayW - bW));

            double yPos = pos.Y + 6;
            if (yPos + bH > overlayH) yPos = Math.Max(0, overlayH - bH - 2);

            System.Windows.Controls.Canvas.SetLeft(tipBorder, xPos);
            System.Windows.Controls.Canvas.SetTop(tipBorder, yPos);
            ChartOverlayCanvas.Children.Add(tipBorder);
        }

        private void TimelineCanvas_MouseLeave(object sender,
            System.Windows.Input.MouseEventArgs e)
        {
            // Don't clear if a nav selection is pinned
            if (_navIndex >= 0) return;
            ChartOverlayCanvas.Children.Clear();
        }

        // ── WiFi helpers ──────────────────────────────────────────────────────

        /// <summary>Returns the WiFi entry whose session contains <paramref name="t"/> (local time).</summary>
        private Models.WifiHistoryEntry? GetSsidAt(DateTime t)
        {
            // History is stored in UTC; hoverT is local — align by converting to UTC.
            var tUtc = t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime();
            return HistorySvc.SsidEntries.FirstOrDefault(e =>
                e.ConnectedAt <= tUtc &&
                (e.DisconnectedAt == null || e.DisconnectedAt.Value >= tUtc));
        }

        /// <summary>
        /// Appends a WiFi/SSID row to <paramref name="tipStack"/> when an SSID
        /// was active at <paramref name="t"/>. Returns true if a row was added.
        /// </summary>
        private bool AddWifiRow(StackPanel tipStack, DateTime t, bool addSeparator = true)
        {
            if (!ConfigSvc.Config.StoreWifiHistory) return false;
            var entry = GetSsidAt(t);
            if (entry == null) return false;

            if (addSeparator && tipStack.Children.Count > 1)
            {
                tipStack.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Height = 1,
                    Fill   = (System.Windows.Media.Brush)FindResource("BorderColor"),
                    Margin = new Thickness(0, 4, 0, 4),
                });
            }

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };

            // WiFi icon dot (steel-blue tint)
            row.Children.Add(new TextBlock
            {
                Text              = "📶",
                FontSize          = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 4, 0),
            });

            // Build label
            var connLocal = entry.ConnectedAt.ToLocalTime();
            string openTag = entry.IsOpen ? "  ⚠ open" : "";
            string text;
            if (entry.DisconnectedAt == null)
            {
                text = $"{entry.Ssid}{openTag}   since {connLocal:HH:mm}";
            }
            else
            {
                var discLocal = entry.DisconnectedAt.Value.ToLocalTime();
                var dur = entry.DisconnectedAt.Value - entry.ConnectedAt;
                string durStr = dur.TotalMinutes < 60
                    ? $"{(int)dur.TotalMinutes}m"
                    : $"{(int)dur.TotalHours}h {dur.Minutes:D2}m";
                text = $"{entry.Ssid}{openTag}   {connLocal:HH:mm}–{discLocal:HH:mm} ({durStr})";
            }

            row.Children.Add(new TextBlock
            {
                Text              = text,
                FontSize          = 9,
                Foreground        = (System.Windows.Media.Brush)FindResource("TextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            tipStack.Children.Add(row);
            return true;
        }

        // ── Session navigation (◀ ▶ buttons) ────────────────────────────────

        private List<(string Name, Models.ConnectionHistoryEntry Entry)> GetNavEntries()
        {
            var now   = DateTime.Now;
            var span  = ConfigSvc.Config.InfoTimeRangeDays == 31 ? TimeSpan.FromDays(31)
                      : ConfigSvc.Config.InfoTimeRangeDays == 7  ? TimeSpan.FromDays(7)
                      : TimeSpan.FromHours(24);
            var start = now - span;

            return _chartData
                .Where(kv => !_hiddenChartTunnels.Contains(kv.Key))
                .SelectMany(kv => kv.Value.Entries.Select(e => (kv.Key, e)))
                .Where(t => t.e.ConnectedAt >= start || (t.e.DisconnectedAt ?? now) >= start)
                .OrderBy(t => t.e.ConnectedAt)
                .ToList();
        }

        private void UpdateNavLabel(int index, int total)
        {
            if (ChartNavLabel == null) return;
            ChartNavLabel.Text = index < 0 ? total.ToString() : $"{index + 1}/{total}";
        }

        private void ChartPrev_Click(object sender, RoutedEventArgs e)
        {
            var entries = GetNavEntries();
            if (entries.Count == 0) return;
            _navIndex = _navIndex <= 0 ? entries.Count - 1 : _navIndex - 1;
            UpdateNavLabel(_navIndex, entries.Count);
            ShowNavTooltip(entries[_navIndex].Name, entries[_navIndex].Entry);
        }

        private void ChartNext_Click(object sender, RoutedEventArgs e)
        {
            var entries = GetNavEntries();
            if (entries.Count == 0) return;
            _navIndex = _navIndex < 0 || _navIndex >= entries.Count - 1 ? 0 : _navIndex + 1;
            UpdateNavLabel(_navIndex, entries.Count);
            ShowNavTooltip(entries[_navIndex].Name, entries[_navIndex].Entry);
        }

        private void ShowNavTooltip(string name, Models.ConnectionHistoryEntry entry)
        {
            ChartOverlayCanvas.Children.Clear();

            double W = TimelineCanvas.ActualWidth;
            double H = TimelineCanvas.ActualHeight;
            if (W < 4) return;

            var now   = DateTime.Now;
            var span  = ConfigSvc.Config.InfoTimeRangeDays == 31 ? TimeSpan.FromDays(31)
                      : ConfigSvc.Config.InfoTimeRangeDays == 7  ? TimeSpan.FromDays(7)
                      : TimeSpan.FromHours(24);
            var start = now - span;

            // Centre the crosshair on the session's midpoint (clamped to window)
            var segStart = entry.ConnectedAt < start ? start : entry.ConnectedAt;
            var segEnd   = entry.DisconnectedAt.HasValue
                ? (entry.DisconnectedAt.Value > now ? now : entry.DisconnectedAt.Value)
                : now;
            double x1 = Math.Clamp((segStart - start).TotalSeconds / span.TotalSeconds * W, 0, W);
            double x2 = Math.Clamp((segEnd   - start).TotalSeconds / span.TotalSeconds * W, 0, W);
            double mX  = (x1 + x2) / 2.0;

            const double axisH = 20;
            // Crosshair
            ChartOverlayCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = mX, Y1 = 0,
                X2 = mX, Y2 = Math.Max(0, H - axisH),
                Stroke = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(180, 255, 255, 255)),
                StrokeThickness = 1,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 2, 2 },
            });
            // Highlight bar span
            if (x2 > x1)
            {
                ChartOverlayCanvas.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width  = x2 - x1,
                    Height = Math.Max(0, H - axisH),
                    Fill   = new System.Windows.Media.SolidColorBrush(
                                 System.Windows.Media.Color.FromArgb(30, 255, 255, 255)),
                });
                System.Windows.Controls.Canvas.SetLeft(
                    (System.Windows.UIElement)ChartOverlayCanvas.Children[ChartOverlayCanvas.Children.Count - 1], x1);
            }

            // Tooltip
            var tipStack = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };

            // Time header
            var hoverT = (segStart + TimeSpan.FromSeconds((segEnd - segStart).TotalSeconds / 2));
            string timeLabel = ConfigSvc.Config.InfoTimeRangeDays >= 7
                ? hoverT.ToString("ddd dd MMM  HH:mm")
                : hoverT.ToString("HH:mm");
            tipStack.Children.Add(new TextBlock
            {
                Text       = timeLabel,
                FontSize   = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
                Margin     = new Thickness(0, 0, 0, 4),
            });

            if (_chartColors.TryGetValue(name, out var col))
            {
                var rowStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
                rowStack.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = 7, Height = 7, RadiusX = 1, RadiusY = 1,
                    Fill = new System.Windows.Media.SolidColorBrush(col),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0),
                });

                string info;
                if (entry.DisconnectedAt == null)
                {
                    info = $"{name}   active since {entry.ConnectedAt.ToLocalTime():HH:mm}";
                }
                else
                {
                    var dur = entry.DisconnectedAt.Value - entry.ConnectedAt;
                    string durStr = dur.TotalMinutes < 60
                        ? $"{(int)dur.TotalMinutes}m"
                        : $"{(int)dur.TotalHours}h {dur.Minutes:D2}m";
                    string bw = (entry.SessionRxBytes > 0 || entry.SessionTxBytes > 0)
                        ? $"   ↑ {FormatInfoBytes(entry.SessionTxBytes)}   ↓ {FormatInfoBytes(entry.SessionRxBytes)}"
                        : "";
                    info = $"{name}   {entry.ConnectedAt.ToLocalTime():HH:mm}–{entry.DisconnectedAt.Value.ToLocalTime():HH:mm} ({durStr}){bw}";
                }

                rowStack.Children.Add(new TextBlock
                {
                    Text = info, FontSize = 9,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                tipStack.Children.Add(rowStack);
            }

            // WiFi row at the session's midpoint time (hoverT is UTC-based here)
            AddWifiRow(tipStack, hoverT);

            var tipBorder = new Border
            {
                Background      = (System.Windows.Media.Brush)FindResource("CardBg"),
                BorderBrush     = (System.Windows.Media.Brush)FindResource("BorderColor"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Child           = tipStack,
            };
            tipBorder.Measure(new Size(600, 400));
            double bW = tipBorder.DesiredSize.Width;
            double bH = tipBorder.DesiredSize.Height;

            double overlayW = ChartOverlayCanvas.ActualWidth;
            double overlayH = ChartOverlayCanvas.ActualHeight;
            double xPos = mX + 12;
            if (xPos + bW > overlayW) xPos = mX - bW - 8;
            xPos = Math.Clamp(xPos, 0, Math.Max(0, overlayW - bW));
            double yPos = Math.Max(0, overlayH / 2 - bH / 2);

            System.Windows.Controls.Canvas.SetLeft(tipBorder, xPos);
            System.Windows.Controls.Canvas.SetTop(tipBorder, yPos);
            ChartOverlayCanvas.Children.Add(tipBorder);
        }

        // ── Column resize ─────────────────────────────────────────────────────

        private System.Windows.Threading.DispatcherTimer? _colSaveTimer;
        private bool _colInitDone;

        private void InitColumnWidths()
        {
            var cfg = ConfigSvc.Config;

            // Discard saved tunnel widths that look wrong (Rules > 100px = auto-saved star proportion)
            if (cfg.TunColRulesW > 100)
            {
                cfg.TunColNameW = cfg.TunColStatusW = cfg.TunColRulesW = cfg.TunColActionW = 0;
                ConfigSvc.Save();
            }

            if (cfg.TunColNameW > 0)
            {
                TunColDef0.Width = new GridLength(cfg.TunColNameW);
                TunColDef1.Width = new GridLength(cfg.TunColStatusW);
                TunColDef2.Width = new GridLength(cfg.TunColRulesW);
                TunColDef3.Width = new GridLength(cfg.TunColActionW);
                _vm.TunCol0W = cfg.TunColNameW;
                _vm.TunCol1W = cfg.TunColStatusW;
                _vm.TunCol2W = cfg.TunColRulesW;
                _vm.TunCol3W = cfg.TunColActionW;
            }
            if (cfg.WifiColNameW > 0)
            {
                WifColDef0.Width = new GridLength(cfg.WifiColNameW);
                WifColDef1.Width = new GridLength(cfg.WifiColSsidW);
                WifColDef2.Width = new GridLength(cfg.WifiColActionW);
                WifColDef3.Width = new GridLength(cfg.WifiColCountW);
                WifColDef4.Width = new GridLength(cfg.WifiColTunnelW);
                _vm.WifCol0W = cfg.WifiColNameW;
                _vm.WifCol1W = cfg.WifiColSsidW;
                _vm.WifCol2W = cfg.WifiColActionW;
                _vm.WifCol3W = cfg.WifiColCountW;
                _vm.WifCol4W = cfg.WifiColTunnelW;
            }
            _colInitDone = true;
        }

        private void TunnelColGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double total = e.NewSize.Width;
            double min   = Math.Floor(total * 0.10);
            TunColDef0.MinWidth = min; TunColDef1.MinWidth = min;
            TunColDef2.MinWidth = min; TunColDef3.MinWidth = min;
            // MaxWidth: no column can grow past total minus the other columns' minimums
            TunColDef0.MaxWidth = total - min - min - min;
            TunColDef1.MaxWidth = total - min - min - min;
            TunColDef2.MaxWidth = total - min - min - min;
            TunColDef3.MaxWidth = total - min - min - min;
            if (TunColDef0.ActualWidth > 0)
            {
                if (TunColDef0.ActualWidth < min) TunColDef0.Width = new GridLength(min);
                if (TunColDef1.ActualWidth < min) TunColDef1.Width = new GridLength(min);
                if (TunColDef2.ActualWidth < min) TunColDef2.Width = new GridLength(min);
                if (TunColDef3.ActualWidth < min) TunColDef3.Width = new GridLength(min);
            }
        }

        private void WifiColGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double total = e.NewSize.Width;
            double min   = Math.Floor(total * 0.10);
            WifColDef0.MinWidth = min; WifColDef1.MinWidth = min;
            WifColDef2.MinWidth = min; WifColDef3.MinWidth = 30; WifColDef4.MinWidth = min;
            WifColDef0.MaxWidth = total - min - min - 30 - min;
            WifColDef1.MaxWidth = total - min - min - 30 - min;
            WifColDef2.MaxWidth = total - min - min - 30 - min;
            WifColDef4.MaxWidth = total - min - min - min - 30;
            if (WifColDef0.ActualWidth > 0)
            {
                if (WifColDef0.ActualWidth < min) WifColDef0.Width = new GridLength(min);
                if (WifColDef1.ActualWidth < min) WifColDef1.Width = new GridLength(min);
                if (WifColDef2.ActualWidth < min) WifColDef2.Width = new GridLength(min);
                if (WifColDef4.ActualWidth < min) WifColDef4.Width = new GridLength(min);
            }
        }

        // Reset star columns to proportional widths so they spread evenly.
        private void ResetTunnelColsToStars()
        {
            TunColDef0.Width = new GridLength(3,   GridUnitType.Star);
            TunColDef1.Width = new GridLength(3.2, GridUnitType.Star);
            // TunColDef2 stays fixed 50px
            TunColDef3.Width = new GridLength(1.5, GridUnitType.Star);
        }

        private void ResetWifiColsToStars()
        {
            WifColDef0.Width = new GridLength(3,   GridUnitType.Star);
            WifColDef1.Width = new GridLength(2,   GridUnitType.Star);
            WifColDef2.Width = new GridLength(1.5, GridUnitType.Star);
            // WifColDef3 stays fixed 40px
            WifColDef4.Width = new GridLength(2,   GridUnitType.Star);
        }

        // Fires continuously during layout; only act when widths have settled.
        private void TunnelColGrid_LayoutUpdated(object sender, EventArgs e)
        {
            if (!_colInitDone) return;
            double w0 = TunColDef0.ActualWidth, w1 = TunColDef1.ActualWidth,
                   w2 = TunColDef2.ActualWidth, w3 = TunColDef3.ActualWidth;
            if (w0 < 1) return;
            if (Math.Abs(w0 - _vm.TunCol0W) < 0.5 && Math.Abs(w1 - _vm.TunCol1W) < 0.5 &&
                Math.Abs(w2 - _vm.TunCol2W) < 0.5 && Math.Abs(w3 - _vm.TunCol3W) < 0.5) return;
            _vm.TunCol0W = Math.Max(TunColDef0.MinWidth, w0);
            _vm.TunCol1W = Math.Max(TunColDef1.MinWidth, w1);
            _vm.TunCol2W = Math.Max(TunColDef2.MinWidth, w2);
            _vm.TunCol3W = Math.Max(TunColDef3.MinWidth, w3);
        }

        private void WifiColGrid_LayoutUpdated(object sender, EventArgs e)
        {
            if (!_colInitDone) return;
            double w0 = WifColDef0.ActualWidth, w1 = WifColDef1.ActualWidth,
                   w2 = WifColDef2.ActualWidth, w3 = WifColDef3.ActualWidth, w4 = WifColDef4.ActualWidth;
            if (w0 < 1) return;
            if (Math.Abs(w0 - _vm.WifCol0W) < 0.5 && Math.Abs(w1 - _vm.WifCol1W) < 0.5 &&
                Math.Abs(w2 - _vm.WifCol2W) < 0.5 && Math.Abs(w3 - _vm.WifCol3W) < 0.5 &&
                Math.Abs(w4 - _vm.WifCol4W) < 0.5) return;
            _vm.WifCol0W = Math.Max(WifColDef0.MinWidth, w0);
            _vm.WifCol1W = Math.Max(WifColDef1.MinWidth, w1);
            _vm.WifCol2W = Math.Max(WifColDef2.MinWidth, w2);
            _vm.WifCol3W = Math.Max(WifColDef3.MinWidth, w3);
            _vm.WifCol4W = Math.Max(WifColDef4.MinWidth, w4);
        }

        // ColSplitter_MouseUp fires when a drag ends — immediate save (skip debounce).
        private void ColSplitter_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _colSaveTimer?.Stop();
            SaveColumnWidths();
        }

        private void ScheduleColSave()
        {
            if (_colSaveTimer == null)
            {
                _colSaveTimer = new System.Windows.Threading.DispatcherTimer
                    { Interval = TimeSpan.FromMilliseconds(600) };
                _colSaveTimer.Tick += (_, _) => { _colSaveTimer.Stop(); SaveColumnWidths(); };
            }
            _colSaveTimer.Stop();
            _colSaveTimer.Start();
        }

        private void SaveColumnWidths()
        {
            var cfg = ConfigSvc.Config;
            cfg.TunColNameW    = _vm.TunCol0W;
            cfg.TunColStatusW  = _vm.TunCol1W;
            cfg.TunColRulesW   = _vm.TunCol2W;
            cfg.TunColActionW  = _vm.TunCol3W;
            cfg.WifiColNameW   = _vm.WifCol0W;
            cfg.WifiColSsidW   = _vm.WifCol1W;
            cfg.WifiColActionW = _vm.WifCol2W;
            cfg.WifiColCountW  = _vm.WifCol3W;
            cfg.WifiColTunnelW = _vm.WifCol4W;
            ConfigSvc.Save();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string FormatInfoBytes(long bytes)
        {
            if (bytes < 1024)             return $"{bytes} B";
            if (bytes < 1024 * 1024)      return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
