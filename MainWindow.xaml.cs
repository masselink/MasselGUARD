using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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

        public MainWindow()
        {
            // ── Bootstrap services ────────────────────────────────────────────
            ConfigSvc  = new ConfigService();
            ConfigSvc.Load();

            LogSvc     = new LogService();
            LogSvc.IsExtended = ConfigSvc.Config.LogLevelSetting == "extended";

            ScriptSvc  = new ScriptService();
            TunnelSvc  = new TunnelService(LogSvc, ScriptSvc);
            WifiSvc    = new WiFiService();
            RuleEngine = new RuleEngine();

            // ── Build ViewModel ───────────────────────────────────────────────
            _vm = new MainViewModel(ConfigSvc, TunnelSvc, LogSvc, WifiSvc, RuleEngine);

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
            Title = $"MasselGUARD  v{UpdateChecker.CurrentVersionString}";

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
                if (change is "ImmersiveColorSet" && !ConfigSvc.Config.UseCustomTheme)
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

            // Log level from config
            LogSvc.IsExtended = ConfigSvc.Config.LogLevelSetting == "extended";

            LogSvc.Ok(Lang.T("LogAppStarted"));
            LogSvc.Info($"Started from: {Environment.ProcessPath ?? AppContext.BaseDirectory}");
            LogSvc.Debug($"  [DBG] OS    : {Environment.OSVersion}");
            LogSvc.Debug($"  [DBG] .NET  : {Environment.Version}");
            LogSvc.Debug($"  [DBG] User  : {Environment.UserDomainName}\\{Environment.UserName}");

            // Subscribe to log service and render existing entries
            LogSvc.EntryAdded += AppendLogEntry;
            RebuildLog();

            // Restore saved log-panel visibility (default true)
            if (!ConfigSvc.Config.ShowActivityLog)
                SetLogPanelVisible(false);

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
                NotifyAllBadges();
                ApplyGroupFilter();
                RebuildLog();   // re-resolve brush colours after accent/theme change
            });

            // Theme: apply on startup based on UseCustomTheme + SystemThemeMode
            ApplyThemeFromConfig();

            // Build initial tunnel list
            _vm.RebuildTunnelList();
            RebuildTunnelGroups();
            RefreshWifiRulesPanel();
            UpdateStatusBarCentre();

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

            // Background update check (frequency-based)
            if (ShouldCheckForUpdates())
                _ = CheckForUpdatesAsync(silent: true);

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
                LogCountLabel.Text = LogSvc.Entries.Count.ToString();
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
            LogCountLabel.Text = LogSvc.Entries.Count.ToString();
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
                    Background      = active && tabBg != null ? tabBg :
                                      active ? (Brush)FindResource("Surface") :
                                      tabBg ?? Brushes.Transparent,
                    BorderThickness = new Thickness(0, 0, 0, active ? 2 : 0),
                    BorderBrush     = active
                        ? (tabBg != null ? tabBg : (Brush)FindResource("Accent"))
                        : Brushes.Transparent,
                    Foreground      = tabFg ??
                                      (active ? (Brush)FindResource("Accent")
                                               : (Brush)FindResource("TextMuted")),
                    FontSize        = 9,
                    Padding         = new Thickness(8, 2, 8, 2),
                    FontWeight      = active ? FontWeights.Bold : FontWeights.Normal,
                    Margin          = new Thickness(0, 0, 2, 0),
                };
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

            if (_showAllOverride)
            {
                // Override active — show plain total in accent with border
                HiddenCountBtn.Content      = total.ToString();
                HiddenCountBtn.Foreground   = (Brush)FindResource("Accent");
                HiddenCountBadge.Background = (Brush)FindResource("BorderColor");
                HiddenCountBadge.BorderThickness = new Thickness(1);
                HiddenCountBadge.BorderBrush     = (Brush)FindResource("Accent");
                HiddenCountBadge.CornerRadius    = new System.Windows.CornerRadius(8);
                HiddenCountBtn.ToolTip = "Override active: showing all tunnels. Click to restore hidden groups.";
            }
            else
            {
                // Hidden tunnels exist — show x/y, no border
                HiddenCountBtn.Content      = $"{visible}/{total}";
                HiddenCountBtn.Foreground   = (Brush)FindResource("TextMuted");
                HiddenCountBadge.Background = (Brush)FindResource("BorderColor");
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
        private void TunSort_Type  (object s, RoutedEventArgs e) => TunnelSort("Type");
        private void TunSort_Status(object s, RoutedEventArgs e) => TunnelSort("Status");
        private void TunSort_Rules (object s, RoutedEventArgs e) => TunnelSort("Rules");

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
            TunArrowType.Text   = _tunnelSortCol == "Type"   ? (_tunnelSortAsc ? asc : desc) : "";
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

        private void TunnelsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _vm.SelectedTunnel = TunnelsListView.SelectedItem as TunnelEntryViewModel;
            EditTunnelBtn.IsEnabled   = _vm.SelectedTunnel != null;
            DeleteTunnelBtn.IsEnabled = _vm.SelectedTunnel != null;
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
                    entry.DisconnectCommand.Execute(null);
                else
                    entry.ConnectCommand.Execute(null);

                _vm.RefreshTunnelStatus();
                UpdateTunnelLabel();
            }
        }

        // ── Dialog dispatchers (called by ViewModel events) ───────────────────
        private void OnAddTunnel()
        {
            var groupNames = ConfigSvc.Config.TunnelGroups.Select(g => g.Name).ToList();
            var dlg = new Views.TunnelConfigDialog(groupNames: groupNames) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            var stored = new StoredTunnel
            {
                Name                = dlg.ResultName ?? "",
                Config              = dlg.ResultConfig ?? "",
                Source              = "local",
                Group               = dlg.ResultGroup,
                PreConnectScript    = dlg.ResultPreConnectScript,
                PostConnectScript   = dlg.ResultPostConnectScript,
                PreDisconnectScript = dlg.ResultPreDisconnectScript,
                PostDisconnectScript= dlg.ResultPostDisconnectScript,
            };
            ConfigSvc.Config.Tunnels.Add(stored);
            ConfigSvc.Save();
            LogSvc.Ok($"Tunnel added: {stored.Name}");
            _vm.RebuildTunnelList();
            RebuildTunnelGroups();
        }

        private void OnEditTunnel(StoredTunnel stored)
        {
            bool isDefault = ConfigSvc.Config.DefaultTunnel    == stored.Name
                          && ConfigSvc.Config.DefaultAction == "activate";
            bool isOpen    = ConfigSvc.Config.OpenWifiTunnel   == stored.Name;

            var groupNames = ConfigSvc.Config.TunnelGroups.Select(g => g.Name).ToList();
            var dlg = stored.Source == "local"
                ? (Window)new Views.TunnelConfigDialog(
                    stored.Name, stored.Config, stored.Group,
                    stored.PreConnectScript, stored.PostConnectScript,
                    stored.PreDisconnectScript, stored.PostDisconnectScript,
                    isDefault, isOpen, groupNames)
                    { Owner = this }
                : new Views.TunnelMetadataDialog(
                    stored.Name, stored.Group, stored.Notes,
                    ConfigSvc.Config.TunnelGroups.Select(g=>g.Name).ToList(),
                    stored.PreConnectScript, stored.PostConnectScript,
                    stored.PreDisconnectScript, stored.PostDisconnectScript,
                    isDefault, isOpen)
                    { Owner = this };

            if (dlg.ShowDialog() != true) return;

            bool newDefault, newOpen;

            if (dlg is Views.TunnelConfigDialog tcd)
            {
                stored.Name                 = tcd.ResultName ?? stored.Name;
                stored.Config               = tcd.ResultConfig ?? stored.Config;
                stored.Group                = tcd.ResultGroup;
                stored.PreConnectScript     = tcd.ResultPreConnectScript;
                stored.PostConnectScript    = tcd.ResultPostConnectScript;
                stored.PreDisconnectScript  = tcd.ResultPreDisconnectScript;
                stored.PostDisconnectScript = tcd.ResultPostDisconnectScript;
                newDefault = tcd.ResultIsDefault;
                newOpen    = tcd.ResultIsOpenProtection;
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

            ConfigSvc.Save();
            _vm.RebuildTunnelList();
            RebuildTunnelGroups();
            NotifyAllBadges();
            UpdateStatusBarCentre();
            LogSvc.Ok($"Tunnel updated: {stored.Name}");
        }

        private void OnDeleteTunnel(StoredTunnel stored)
        {
            var msg = stored.Source == "local"
                ? Lang.T("ConfirmDeleteTunnel", stored.Name)
                : Lang.T("ConfirmUnlinkTunnel", stored.Name);

            if (MessageBox.Show(msg, "MasselGUARD",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            ConfigSvc.Config.Tunnels.Remove(stored);
            ConfigSvc.Save();
            LogSvc.Ok($"Tunnel removed: {stored.Name}");
            _vm.RebuildTunnelList();
            RebuildTunnelGroups();
        }

        private void OnQuickConnect() => QuickConnect_Click(this, new RoutedEventArgs());

        private void OnOpenSettings()
        {
            var win = new Views.SettingsWindow(this) { Owner = this };
            win.ShowDialog();
            // Refresh after settings close
            LogSvc.IsExtended = ConfigSvc.Config.LogLevelSetting == "extended";
            _vm.RebuildTunnelList();
            RebuildTunnelGroups();
            UpdateFooterLabel();
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
            dlg.TunnelImported += (name, cfg2, src, path) =>
            {
                var st = new StoredTunnel { Name=name, Config=cfg2, Source=src, Path=path };
                ConfigSvc.Config.Tunnels.Add(st);
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
        private void QuickConnect_Click(object sender, RoutedEventArgs e)
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

            TunnelSvc.Connect(stored, ConfigSvc.Config);
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
        private void OnWifiChanged(string? ssid, bool isOpen)
        {
            Dispatcher.BeginInvoke(() =>
            {
                UpdateWifiLabel(ssid);
                _vm.ApplyWifiState(ssid, isOpen);
                // Give tunnel service a moment to connect, then refresh tunnel label
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() =>
                    {
                        _vm.RefreshTunnelStatus();
                        UpdateTunnelLabel();
                    }));
            });
        }

        private void TryUpdateWifi()
        {
            var (ssid, isOpen) = WifiSvc.QueryCurrentSsid();
            UpdateWifiLabel(ssid);
            _vm.ApplyWifiState(ssid, isOpen);
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

        private void UpdateFooterLabel()
        {
            string modeText = AppRunMode switch
            {
                AppRunModeKind.Managed         => Lang.T("InstallStatusManaged"),
                AppRunModeKind.ManagedPortable => "Managed (Portable)",
                _                              => Lang.T("InstallStatusNotInstalled"),
            };
            FooterLabel.Text       = $"{Lang.T("FooterMode")}: {modeText}";
            FooterLabel.Foreground = (Brush)FindResource("TextMuted");  // always grey
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
        /// Applies the active theme based on UseCustomTheme + SystemThemeMode from config.
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

            if (!cfg.UseCustomTheme)
            {
                ThemeManager.Instance.LoadSystem(isDark);
            }
            else
            {
                var target = isDark ? cfg.ActiveDarkTheme : cfg.ActiveLightTheme;
                if (!string.IsNullOrEmpty(target))
                    ThemeManager.Instance.Load(target);
                else
                    ThemeManager.Instance.LoadSystem(isDark);
            }

            // Font override: applied after theme so it wins over the theme's own font.
            ThemeManager.ApplyFontOverride(cfg.FontOverrideEnabled, cfg.FontOverrideFamily, cfg.FontOverrideSize);
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
            e.Cancel = true;
            Hide();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Hide();

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
        }
        public void EditRulePublic(TunnelRule rule)
        {
            var dlg = new Views.RuleDialog(GetCurrentSsid(),
                existingName:   rule.Name,
                existingSsid:   rule.Ssid,
                existingTunnel: rule.Tunnel,
                tunnels:        GetAvailableTunnels()) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            rule.Name   = dlg.ResultName;
            rule.Ssid   = dlg.ResultSsid;
            rule.Tunnel = dlg.ResultTunnel;
        }
        public void DeleteRulePublic(TunnelRule rule)
        {
            ConfigSvc.Config.Rules.Remove(rule);
        }

        private string? GetCurrentSsid() => WifiSvc.CurrentSsid;
        private List<string> GetAvailableTunnels() => GetTunnelNames();

        // ── Orphaned service detection ────────────────────────────────────────
        public record OrphanedService(string ServiceName, string TunnelName, bool TunnelActive);

        public List<OrphanedService> GetOrphanedServices()
        {
            var result = new List<OrphanedService>();

            // Tunnels this session intentionally has active — never flag those
            var activeInApp = new System.Collections.Generic.HashSet<string>(
                _vm.TunnelList.Where(t => t.IsActive).Select(t => t.Name),
                StringComparer.OrdinalIgnoreCase);

            // Tunnel names configured in this install (used as a fallback identifier)
            var tracked = new System.Collections.Generic.HashSet<string>(
                ConfigSvc.Config.Tunnels.Select(t => t.Name),
                StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var svc in System.ServiceProcess.ServiceController.GetServices())
                {
                    const string prefix = "WireGuardTunnel$";
                    if (!svc.ServiceName.StartsWith(prefix,
                        StringComparison.OrdinalIgnoreCase)) continue;

                    var name = svc.ServiceName[prefix.Length..];

                    // Never flag a tunnel this session is managing
                    if (activeInApp.Contains(name)) continue;

                    // Primary: DisplayName contains "MasselGUARD"
                    // (e.g. "WireGuard Tunnel: MasselGUARD - TunnelName")
                    // Secondary: Description registry value contains "MasselGUARD"
                    // Tertiary: tunnel name matches one in our config
                    bool isManagedByUs =
                        svc.DisplayName.Contains("MasselGUARD",
                            StringComparison.OrdinalIgnoreCase)
                        || GetServiceDescription(svc.ServiceName).Contains("MasselGUARD",
                            StringComparison.OrdinalIgnoreCase)
                        || tracked.Contains(name);

                    if (!isManagedByUs) continue;

                    bool isRunning = svc.Status == System.ServiceProcess.ServiceControllerStatus.Running;
                    result.Add(new OrphanedService(svc.ServiceName, name, isRunning));
                }
            }
            catch { }

            return result;
        }

        /// <summary>Reads the service Description value from the registry.</summary>
        private static string GetServiceDescription(string serviceName)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                return key?.GetValue("Description") as string ?? "";
            }
            catch { return ""; }
        }

        public void RemoveOrphan(OrphanedService orphan)
        {
            try
            {
                // ForceRemoveService stops (if running) and deletes the SCM entry —
                // works for both running and already-stopped orphan services.
                TunnelDll.ForceRemoveService(orphan.ServiceName);
                LogSvc.Ok($"Orphan removed: {orphan.TunnelName}");
            }
            catch (Exception ex)
            {
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
                    FontSize   = 12,
                };
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
            if (WifiRuleEditBtn   != null) WifiRuleEditBtn.IsEnabled   = hasSelection;
            if (WifiRuleDeleteBtn != null) WifiRuleDeleteBtn.IsEnabled = hasSelection;
        }

        private void WifiRuleAdd_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Views.RuleDialog(
                WifiSvc.CurrentSsid,
                tunnels: GetTunnelNames())
                { Owner = this };
            if (dlg.ShowDialog() != true) return;
            var rule = new Models.TunnelRule
                { Name = dlg.ResultName, Ssid = dlg.ResultSsid, Tunnel = dlg.ResultTunnel };
            ConfigSvc.Config.Rules.Add(rule);
            ConfigSvc.Save();
            LogSvc.Ok($"Rule added: {rule.Ssid}");
            RefreshWifiRulesPanel();
            _vm.RebuildTunnelList();
        }

        private void WifiRuleEdit_Click(object sender, RoutedEventArgs e)
        {
            if (WifiRulesListView.SelectedItem is not WifiRuleRow row) return;
            var rule = ConfigSvc.Config.Rules
                .FirstOrDefault(r => r.Ssid == row.Ssid);
            if (rule == null) return;

            var dlg = new Views.RuleDialog(
                WifiSvc.CurrentSsid,
                existingName:   rule.Name,
                existingSsid:   rule.Ssid,
                existingTunnel: rule.Tunnel,
                tunnels:        GetTunnelNames())
                { Owner = this };
            if (dlg.ShowDialog() != true) return;
            rule.Name   = dlg.ResultName;
            rule.Ssid   = dlg.ResultSsid;
            rule.Tunnel = dlg.ResultTunnel;
            ConfigSvc.Save();
            LogSvc.Ok($"Rule updated: {rule.Ssid}");
            RefreshWifiRulesPanel();
            _vm.RebuildTunnelList();
        }

        private void WifiRuleDelete_Click(object sender, RoutedEventArgs e)
        {
            if (WifiRulesListView.SelectedItem is not WifiRuleRow row) return;
            var rule = ConfigSvc.Config.Rules
                .FirstOrDefault(r => r.Ssid == row.Ssid && r.Tunnel == row.TunnelName);
            if (rule == null) return;
            if (!ShowThemedYesNo($"Delete rule for \"{rule.Ssid}\"?", "Delete rule")) return;
            ConfigSvc.Config.Rules.Remove(rule);
            ConfigSvc.Save();
            LogSvc.Ok($"Rule deleted: {rule.Ssid}");
            RefreshWifiRulesPanel();
            _vm.RebuildTunnelList();
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
            hdr.Child = new System.Windows.Controls.TextBlock
            {
                Text = "Defaults", FontFamily = fontFam,
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = accent,
            };
            panel.Children.Add(hdr);

            // Helper to make a picker row
            System.Windows.Controls.ComboBox MakeRow(string emoji, string label,
                string currentValue, bool addClearOption)
            {
                var row = new System.Windows.Controls.Border
                    { Padding = new Thickness(14, 10, 14, 6) };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var lbl = new System.Windows.Controls.TextBlock
                {
                    Text = $"{emoji}  {label}", FontFamily = fontFam, FontSize = 11,
                    Foreground = textPri, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0,0,12,0),
                };
                Grid.SetColumn(lbl, 0);

                var cb = new System.Windows.Controls.ComboBox
                {
                    FontFamily = fontFam, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                };
                if (addClearOption) cb.Items.Add("— clear —");
                foreach (var t in tunnelNames) cb.Items.Add(t);
                cb.SelectedItem = tunnelNames.Contains(currentValue) ? currentValue
                    : (addClearOption ? "— clear —" : null);
                Grid.SetColumn(cb, 1);

                g.Children.Add(lbl);
                g.Children.Add(cb);
                row.Child = g;
                panel.Children.Add(row);
                return cb;
            }

            string curDefault = ConfigSvc.Config.DefaultAction == "activate"
                ? ConfigSvc.Config.DefaultTunnel : "";
            string curOpen    = ConfigSvc.Config.OpenWifiTunnel;

            var defaultPicker = MakeRow("⚡", "Default action tunnel",    curDefault, true);
            var openPicker    = MakeRow("🔓", "Open network protection", curOpen,    true);

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
                Content = "Cancel", FontFamily = fontFam, FontSize = 11,
                Style = (Style)Application.Current.Resources["FlatBtn"],
                Padding = new Thickness(14,5,14,5), Margin = new Thickness(0,0,8,0),
            };
            var btnSave = new System.Windows.Controls.Button
            {
                Content = "Save", FontFamily = fontFam, FontSize = 11,
                Style = (Style)Application.Current.Resources["PrimaryBtn"],
                Padding = new Thickness(14,5,14,5),
            };
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

                if (defSel == "— clear —" || string.IsNullOrEmpty(defSel))
                {
                    ConfigSvc.Config.DefaultAction = "none";
                    ConfigSvc.Config.DefaultTunnel = "";
                }
                else
                {
                    ConfigSvc.Config.DefaultAction = "activate";
                    ConfigSvc.Config.DefaultTunnel = defSel;
                }

                ConfigSvc.Config.OpenWifiTunnel = openSel == "— clear —" ? "" : openSel;

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
            if (WifiRulesPanelRow  != null) WifiRulesPanelRow.Height  = showPanel ? wifiStar : zero;
            if (WifiRulesBtnsRow   != null) WifiRulesBtnsRow.Height   = showPanel ? auto     : zero;

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
                // Display name: use stored name or auto-generate
                var autoName   = string.IsNullOrEmpty(r.Tunnel)
                    ? $"{(string.IsNullOrEmpty(r.Ssid) ? "—" : r.Ssid)} → disconnect"
                    : $"{(string.IsNullOrEmpty(r.Ssid) ? "—" : r.Ssid)} → {r.Tunnel}";
                RuleName       = string.IsNullOrEmpty(r.Name) ? autoName : r.Name;
                Ssid           = string.IsNullOrEmpty(r.Ssid) ? "—" : r.Ssid;
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
                BorderBrush     = (System.Windows.Media.Brush)Application.Current.Resources["BorderColor"],
                BorderThickness = new Thickness(1),
                CornerRadius    = new System.Windows.CornerRadius(6),
                Padding         = new Thickness(20),
            };

            var panel = new System.Windows.Controls.StackPanel();

            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text       = title,
                FontSize   = 12, FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimary"],
                Margin     = new Thickness(0, 0, 0, 10),
            });
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text         = message,
                FontSize     = 11,
                Foreground   = (System.Windows.Media.Brush)Application.Current.Resources["TextMuted"],
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 16),
            });

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
                BorderBrush     = (System.Windows.Media.Brush)Application.Current.Resources["BorderColor"],
                BorderThickness = new Thickness(1),
                CornerRadius    = new System.Windows.CornerRadius(6),
                Padding         = new Thickness(20),
            };

            var panel = new System.Windows.Controls.StackPanel();

            var title = new System.Windows.Controls.TextBlock
            {
                Text       = Lang.T("InstallPortableTitle"),
                FontSize   = 13, FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimary"],
                Margin     = new Thickness(0, 0, 0, 10),
            };
            var body = new System.Windows.Controls.TextBlock
            {
                Text        = Lang.T("InstallPortablePrompt"),
                FontSize    = 11,
                Foreground  = (System.Windows.Media.Brush)Application.Current.Resources["TextMuted"],
                TextWrapping= TextWrapping.Wrap,
                Margin      = new Thickness(0, 0, 0, 10),
            };
            var chk = new System.Windows.Controls.CheckBox
            {
                Content    = Lang.T("InstallPortableDoNotAsk"),
                FontSize   = 10,
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextMuted"],
                Margin     = new Thickness(0, 0, 0, 14),
            };
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
                if (latest == null) return;

                if (!UpdateChecker.IsNewerVersion(latest.TagName)) return;

                await Dispatcher.InvokeAsync(() =>
                {
                    var current = UpdateChecker.CurrentVersionString;
                    if (ShowThemedYesNo(
                        Lang.T("UpdateAvailableMsg", latest.TagName, current),
                        Lang.T("UpdateAvailableTitle")))
                        RunUpdate();
                });
            }
            catch { /* silent — network may not be available */ }
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

                // 1. Copy files
                System.IO.Directory.CreateDirectory(installDir);
                foreach (var file in System.IO.Directory.GetFiles(sourceDir))
                    System.IO.File.Copy(file,
                        System.IO.Path.Combine(installDir,
                            System.IO.Path.GetFileName(file)), overwrite: true);
                CopyDirRecursive(sourceDir, installDir);

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
                MessageBox.Show(Lang.T("NotInstalled"), Lang.T("UninstallTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information); return;
            }
            if (MessageBox.Show(Lang.T("UninstallConfirm"), Lang.T("UninstallTitle"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            bool keepConfig = MessageBox.Show(Lang.T("UninstallKeepConfig"),
                Lang.T("UninstallKeepConfigTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

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
                MessageBox.Show(Lang.T("UninstallFailed", ex.Message), Lang.T("UninstallTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
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
                BorderBrush=(System.Windows.Media.Brush)Application.Current.Resources["BorderColor"],
                BorderThickness=new Thickness(1),
                CornerRadius=new System.Windows.CornerRadius(6),
                Padding=new Thickness(20),
            };
            var panel = new System.Windows.Controls.StackPanel();

            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text=Lang.T("InstallTitle"), FontSize=12, FontWeight=FontWeights.Bold,
                Foreground=(System.Windows.Media.Brush)Application.Current.Resources["TextPrimary"],
                Margin=new Thickness(0,0,0,12),
            });
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text=Lang.T("InstallSelectFolder"), FontSize=10,
                Foreground=(System.Windows.Media.Brush)Application.Current.Resources["TextMuted"],
                Margin=new Thickness(0,0,0,4),
            });

            var folderRow = new System.Windows.Controls.Grid { Margin=new Thickness(0,0,0,8) };
            folderRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width=new GridLength(1,GridUnitType.Star) });
            folderRow.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width=GridLength.Auto });

            var folderBox = new System.Windows.Controls.TextBox
            {
                Text=defaultParent,
                FontFamily=(System.Windows.Media.FontFamily)Application.Current.Resources["Theme.FontFamily"],
                FontSize=11,
                Background=(System.Windows.Media.Brush)Application.Current.Resources["CardBg"],
                Foreground=(System.Windows.Media.Brush)Application.Current.Resources["TextPrimary"],
                BorderBrush=(System.Windows.Media.Brush)Application.Current.Resources["BorderColor"],
                BorderThickness=new Thickness(1), Padding=new Thickness(6,4,6,4),
                VerticalContentAlignment=VerticalAlignment.Center,
            };
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

            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text="Will install to:", FontSize=10,
                Foreground=(System.Windows.Media.Brush)Application.Current.Resources["TextMuted"],
                Margin=new Thickness(0,0,0,2),
            });
            var resolvedLabel = new System.Windows.Controls.TextBlock
            {
                FontFamily=(System.Windows.Media.FontFamily)Application.Current.Resources["Theme.FontFamily"],
                FontSize=11, FontWeight=FontWeights.Bold,
                Foreground=(System.Windows.Media.Brush)Application.Current.Resources["Accent"],
                TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,0,0,16),
            };

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
            foreach (var dir in System.IO.Directory.GetDirectories(src))
            {
                var name = System.IO.Path.GetFileName(dir);
                var dest = System.IO.Path.Combine(dst, name);
                System.IO.Directory.CreateDirectory(dest);
                foreach (var f in System.IO.Directory.GetFiles(dir))
                    System.IO.File.Copy(f, System.IO.Path.Combine(dest,
                        System.IO.Path.GetFileName(f)), overwrite: true);
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

        public void RunUpdate()
        {
            var installed = GetInstalledPath();
            if (installed == null) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    installed) { UseShellExecute = true });
                Application.Current.Shutdown();
            }
            catch (Exception ex) { LogSvc.Warn($"Update failed: {ex.Message}"); }
        }

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
    }
}
