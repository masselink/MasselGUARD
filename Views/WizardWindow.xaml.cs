using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MasselGUARD.Models;
using MasselGUARD.ViewModels;

namespace MasselGUARD.Views
{
    public partial class WizardWindow : Window
    {
        private readonly WizardViewModel _vm;
        private readonly MainWindow      _main;
        private readonly bool            _isUpgrade;

        private bool _settingControls;

        private const int TotalSteps = 9; // steps 0-8

        public WizardWindow(MainWindow main, bool isUpgrade = false)
        {
            _main      = main;
            _isUpgrade = isUpgrade;
            _vm = new WizardViewModel(
                main.ConfigSvc,
                main.LogSvc,
                code => Dispatcher.Invoke(() => Lang.Instance.Load(code)));

            _vm.Finished += () => Dispatcher.Invoke(() => { DialogResult = true;  Close(); });
            _vm.Skipped  += () => Dispatcher.Invoke(() => { DialogResult = false; Close(); });

            InitializeComponent();
            DataContext = _vm;

            WizLangPicker.Items.Clear();
            foreach (var item in _vm.AvailableLanguages)
                WizLangPicker.Items.Add(item);
            WizLangPicker.SelectedItem = _vm.SelectedLanguage;

            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(WizardViewModel.Step))
                    Dispatcher.Invoke(() => { UpdateDots(); UpdateStepVisibility(); });
            };

            UpdateDots();
            UpdateStepVisibility();
        }

        // ── Step visibility ───────────────────────────────────────────────────
        private void UpdateStepVisibility()
        {
            var steps = new[] { Step0, Step1, Step2, Step3, Step4, Step5, Step6, Step7, Step8 };
            for (int i = 0; i < steps.Length; i++)
                if (steps[i] != null)
                    steps[i].Visibility = i == _vm.Step ? Visibility.Visible : Visibility.Collapsed;

            var cfg = _main.ConfigSvc.Config;

            // ── Step 0: Welcome + import + language ──────────────────────────
            if (WizUpgradeNotice != null)
                WizUpgradeNotice.Visibility = _isUpgrade ? Visibility.Visible : Visibility.Collapsed;
            if (_isUpgrade)
            {
                if (WizUpgradeTitle != null) WizUpgradeTitle.Text = Lang.T("WizardUpgradeTitle");
                if (WizUpgradeBody  != null) WizUpgradeBody.Text  = Lang.T("WizardUpgradeBody",
                    UpdateChecker.CurrentVersionString, _vm.PreviousAppVersion);
            }
            if (_vm.Step == 0)
            {
                _settingControls = true;
                WizLangPicker.SelectedItem = _vm.SelectedLanguage;
                _settingControls = false;
            }

            // ── Step 1: Per-feature questions ────────────────────────────────
            if (_vm.Step == 1) PopulateFeatureStep();

            // ── Step 2: Appearance ───────────────────────────────────────────
            if (_vm.Step == 2)
            {
                _settingControls = true;
                var sysMode = cfg.SystemThemeMode ?? "auto";
                if (WizThemeAuto  != null) WizThemeAuto.IsChecked  = sysMode == "auto";
                if (WizThemeDark  != null) WizThemeDark.IsChecked  = sysMode == "dark";
                if (WizThemeLight != null) WizThemeLight.IsChecked = sysMode == "light";
                PopulateWizThemePicker();
                if (WizShowChartsBtnToggle != null) WizShowChartsBtnToggle.IsChecked = cfg.ShowChartsToggleButton;
                if (WizShowLogBtnToggle    != null) WizShowLogBtnToggle.IsChecked    = cfg.ShowLogToggleButton;
                _settingControls = false;
            }

            // ── Step 3: Startup ──────────────────────────────────────────────
            if (_vm.Step == 3)
            {
                if (WizInstallChoice != null)
                    WizInstallChoice.Visibility =
                        (!_isUpgrade && _main.AppRunMode == MainWindow.AppRunModeKind.Standalone)
                        ? Visibility.Visible : Visibility.Collapsed;

                _settingControls = true;
                if (WizStartWithWindowsToggle != null) WizStartWithWindowsToggle.IsChecked = cfg.StartWithWindows;
                if (WizStartMinimizedToggle   != null) WizStartMinimizedToggle.IsChecked   = cfg.StartMinimized;
                if (WizConfirmOnCloseToggle   != null) WizConfirmOnCloseToggle.IsChecked   = cfg.ConfirmOnClose;
                _settingControls = false;
            }

            // ── Step 4: WiFi settings ────────────────────────────────────────
            if (_vm.Step == 4)
            {
                _settingControls = true;
                if (WizManualToggle   != null) WizManualToggle.IsChecked   = _vm.DisableWifiRules;
                if (WizShowRulesToggle != null) WizShowRulesToggle.IsChecked = cfg.ShowWifiRulesOnMainWindow;
                if (WizShowWifiBtnToggle != null) WizShowWifiBtnToggle.IsChecked = cfg.ShowWifiToggleButton;
                if (WizWifiDisableToggle != null) WizWifiDisableToggle.IsChecked = cfg.WifiToggleDisables;
                _settingControls = false;
                if (WizShowRulesCard != null)
                    WizShowRulesCard.Visibility = _vm.DisableWifiRules ? Visibility.Collapsed : Visibility.Visible;
            }

            // ── Step 5: WireGuard Behaviour ──────────────────────────────────
            if (_vm.Step == 5)
            {
                _settingControls = true;
                string ar = cfg.AutoReconnectMode;
                if (WizArOff       != null) WizArOff.IsChecked       = ar == "off";
                if (WizArPerTunnel != null) WizArPerTunnel.IsChecked = ar == "per-tunnel";
                if (WizArAlways    != null) WizArAlways.IsChecked    = ar == "always";
                if (WizArOff != null && WizArOff.IsChecked != true && WizArPerTunnel?.IsChecked != true && WizArAlways?.IsChecked != true)
                    WizArOff.IsChecked = true;

                string ks = cfg.KillSwitchMode;
                if (WizKsOff       != null) WizKsOff.IsChecked       = ks == "off";
                if (WizKsPerTunnel != null) WizKsPerTunnel.IsChecked = ks == "per-tunnel";
                if (WizKsAlways    != null) WizKsAlways.IsChecked    = ks == "always";
                if (WizKsPerTunnel != null && WizKsOff?.IsChecked != true && WizKsPerTunnel.IsChecked != true && WizKsAlways?.IsChecked != true)
                    WizKsPerTunnel.IsChecked = true;

                if (WizDnsIndicatorToggle != null) WizDnsIndicatorToggle.IsChecked = cfg.ShowDnsIndicator;
                if (WizShowTunnelBtnToggle != null) WizShowTunnelBtnToggle.IsChecked = cfg.ShowTunnelToggleButton;
                if (WizTunnelDisableToggle != null) WizTunnelDisableToggle.IsChecked = cfg.TunnelToggleDisables;
                _settingControls = false;
            }

            // ── Step 6: DNS Profiles Behaviour ───────────────────────────────
            if (_vm.Step == 6)
            {
                _settingControls = true;
                if (WizDnsAutomationToggle != null) WizDnsAutomationToggle.IsChecked = cfg.DnsAutomationEnabled;
                if (WizShowDnsBtnToggle != null) WizShowDnsBtnToggle.IsChecked = cfg.ShowDnsToggleButton;
                if (WizDnsDisableToggle != null) WizDnsDisableToggle.IsChecked = cfg.DnsToggleDisables;
                _settingControls = false;
            }

            // ── Step 7: Notifications ────────────────────────────────────────
            if (_vm.Step == 7)
            {
                _settingControls = true;
                if (WizTrayPopupToggle != null) WizTrayPopupToggle.IsChecked = cfg.ShowTrayPopupOnSwitch;
                if (WizNotifDurationBox != null) WizNotifDurationBox.Text = cfg.NotificationDurationSeconds.ToString();
                if (WizStoreConnectionHistoryToggle != null) WizStoreConnectionHistoryToggle.IsChecked = cfg.StoreConnectionHistory;
                if (WizStoreWifiHistoryToggle != null) WizStoreWifiHistoryToggle.IsChecked = cfg.StoreWifiHistory;
                if (WizStoreDnsHistoryToggle != null) WizStoreDnsHistoryToggle.IsChecked = cfg.StoreDnsHistory;
                _settingControls = false;
            }

            // ── Step 8: Done ─────────────────────────────────────────────────
            if (_vm.Step == 8)
            {
                if (WizVersionLabel   != null) WizVersionLabel.Text   = $"MasselGUARD v{UpdateChecker.CurrentVersionString}";
                if (WizCheckUpdateBtn != null) WizCheckUpdateBtn.Content = Lang.T("BtnCheckUpdate");
                BuildSummary();
            }

            BtnBack.IsEnabled = _vm.CanGoBack;
            BtnNext.Content   = _vm.IsLastStep && _pendingRelease != null
                ? "Save & Update"
                : _vm.IsLastStep
                    ? Lang.T("WizardBtnFinish")
                    : Lang.T("WizardBtnNext");
        }

        // ── Summary (Done step) ────────────────────────────────────────────────
        private void BuildSummary()
        {
            if (WizSummaryPanel == null) return;
            WizSummaryPanel.Children.Clear();
            var cfg = _main.ConfigSvc.Config;

            void Row(string label, string value)
            {
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var lbl = new TextBlock { Text = label, FontFamily = (FontFamily)FindResource("Theme.FontFamily"),
                    FontSize = 10, Foreground = (Brush)FindResource("TextMuted"), VerticalAlignment = VerticalAlignment.Top };
                var val = new TextBlock { Text = value, FontFamily = (FontFamily)FindResource("Theme.FontFamily"),
                    FontSize = 10, Foreground = (Brush)FindResource("TextPrimary"), TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(lbl, 0); Grid.SetColumn(val, 1);
                g.Children.Add(lbl); g.Children.Add(val);
                g.Margin = new Thickness(0, 0, 0, 4);
                WizSummaryPanel.Children.Add(g);
            }

            string features = _vm.EnableTunnels && _vm.EnableDns ? "WireGuard VPN + DNS"
                            : _vm.EnableTunnels ? "WireGuard VPN"
                            : "DNS automation";
            Row("Features", features);
            if (_vm.EnableTunnels)
            {
                Row("WiFi automation",  _vm.DisableWifiRules ? "Off (manual)" : "On");
                Row("Auto-reconnect",   cfg.AutoReconnectMode);
                Row("Kill switch",      cfg.KillSwitchMode);
            }
            if (_vm.EnableDns)
                Row("DNS automation",   cfg.DnsAutomationEnabled ? "On" : "Off");
            Row("Start with Windows",  cfg.StartWithWindows ? "Yes" : "No");
            Row("Start minimized",     cfg.StartMinimized ? "Yes" : "No");
            Row("Tray notifications",  cfg.ShowTrayPopupOnSwitch ? "On" : "Off");
        }

        // ── Dot indicators ────────────────────────────────────────────────────
        private void UpdateDots()
        {
            var dots   = new[] { Dot0, Dot1, Dot2, Dot3, Dot4, Dot5, Dot6, Dot7, Dot8 };
            var accent = (Brush)FindResource("Accent");
            var dim    = (Brush)FindResource("BorderColor");
            for (int i = 0; i < dots.Length; i++)
                if (dots[i] != null)
                    dots[i].Fill = i == _vm.Step ? accent : dim;
        }

        // ── Navigation ────────────────────────────────────────────────────────
        private void BtnBack_Click(object sender, RoutedEventArgs e) => _vm.BackCommand.Execute(null);

        private async void BtnNext_Click(object sender, RoutedEventArgs e)
        {
            if (_vm.IsLastStep && _pendingRelease != null)
            {
                await StartUpdateAsync(_pendingRelease);
                return;
            }
            _vm.NextCommand.Execute(null);
        }

        private void BtnSkip_Click(object sender, RoutedEventArgs e) => _vm.SkipCommand.Execute(null);

        // ── Language ──────────────────────────────────────────────────────────
        private void WizLang_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_settingControls) return;
            if (WizLangPicker.SelectedItem is LangItem item)
                _vm.SelectedLanguage = item;
        }

        // ── Per-feature questions (Step 1) ──────────────────────────────────────
        private void PopulateFeatureStep()
        {
            var cfg = _main.ConfigSvc.Config;
            _settingControls = true;
            if (WizFeatWgToggle     != null) WizFeatWgToggle.IsChecked     = _vm.EnableTunnels;
            if (WizFeatDnsToggle    != null) WizFeatDnsToggle.IsChecked    = _vm.EnableDns;
            if (WizFeatAutoToggle   != null) WizFeatAutoToggle.IsChecked   = !_vm.DisableWifiRules;
            if (WizFeatLogToggle    != null) WizFeatLogToggle.IsChecked    = cfg.ActivityLogEnabled;
            if (WizFeatChartsToggle != null) WizFeatChartsToggle.IsChecked = cfg.ChartsEnabled;

            bool debug = cfg.LogLevelSetting == "extended";
            if (WizLogNormal != null) WizLogNormal.IsChecked = !debug;
            if (WizLogDebug  != null) WizLogDebug.IsChecked  = debug;

            if (WizPaneTimeline != null) WizPaneTimeline.IsChecked = cfg.ShowTimelinePane;
            if (WizPaneUsage    != null) WizPaneUsage.IsChecked    = cfg.ShowUsagePane;
            if (WizPaneDns      != null) WizPaneDns.IsChecked      = cfg.ShowDnsPane;
            _settingControls = false;

            UpdateFeatureStepEnablement();
        }

        /// <summary>Grey the sub-options that only make sense when a feature/module is on.</summary>
        private void UpdateFeatureStepEnablement()
        {
            var cfg = _main.ConfigSvc.Config;
            if (WizLogLevelRow    != null) WizLogLevelRow.IsEnabled    = cfg.ActivityLogEnabled;
            if (WizChartLayersRow != null) WizChartLayersRow.IsEnabled = cfg.ChartsEnabled;
            // Data-usage layer needs tunnels; DNS layer needs the DNS feature.
            if (WizPaneUsage != null) WizPaneUsage.IsEnabled = cfg.ChartsEnabled && _vm.EnableTunnels;
            if (WizPaneDns   != null) WizPaneDns.IsEnabled   = cfg.ChartsEnabled && _vm.EnableDns;
        }

        private void WizFeatWg_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            bool on = WizFeatWgToggle?.IsChecked == true;
            if (!on && !_vm.EnableDns) { _settingControls = true; WizFeatWgToggle!.IsChecked = true; _settingControls = false; return; } // keep ≥1 module
            _vm.EnableTunnels = on;
            UpdateFeatureStepEnablement();
        }

        private void WizFeatDns_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            bool on = WizFeatDnsToggle?.IsChecked == true;
            if (!on && !_vm.EnableTunnels) { _settingControls = true; WizFeatDnsToggle!.IsChecked = true; _settingControls = false; return; }
            _vm.EnableDns = on;
            UpdateFeatureStepEnablement();
        }

        private void WizFeatAuto_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _vm.DisableWifiRules = WizFeatAutoToggle?.IsChecked != true;
        }

        private void WizFeatLog_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            bool on = WizFeatLogToggle?.IsChecked == true;
            _main.ConfigSvc.Config.ActivityLogEnabled = on;
            _main.LogSvc.Enabled = on;
            UpdateFeatureStepEnablement();
        }

        private void WizFeatCharts_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            bool on = WizFeatChartsToggle?.IsChecked == true;
            _main.ConfigSvc.Config.ChartsEnabled = on;
            _main.HistorySvc.CaptureEnabled = on;
            UpdateFeatureStepEnablement();
        }

        private void WizLogLevel_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            bool debug = WizLogDebug?.IsChecked == true;
            _main.ConfigSvc.Config.LogLevelSetting = debug ? "extended" : "normal";
            _main.LogSvc.IsExtended = debug;
        }

        private void WizPane_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            var cfg = _main.ConfigSvc.Config;
            cfg.ShowTimelinePane = WizPaneTimeline?.IsChecked == true;
            cfg.ShowUsagePane    = WizPaneUsage?.IsChecked == true;
            cfg.ShowDnsPane      = WizPaneDns?.IsChecked == true;
        }

        // ── Theme ─────────────────────────────────────────────────────────────
        private void WizTheme_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            var cfg = _main.ConfigSvc.Config;
            if (WizThemeAuto?.IsChecked == true)
            {
                cfg.SystemThemeMode = "auto";
                ThemeManager.Instance.Load(cfg.ActiveTheme ?? "__system__", ThemeManager.GetSystemIsDark());
            }
            else if (WizThemeDark?.IsChecked == true)
            {
                cfg.SystemThemeMode = "dark";
                ThemeManager.Instance.Load(cfg.ActiveTheme ?? "__system__", true);
            }
            else if (WizThemeLight?.IsChecked == true)
            {
                cfg.SystemThemeMode = "light";
                ThemeManager.Instance.Load(cfg.ActiveTheme ?? "__system__", false);
            }
        }

        private void WizDownloadThemes_Click(object sender, RoutedEventArgs e)
        {
            var url = (_main.ConfigSvc.Config.SharedThemesRepoUrl ?? "").Trim();
            if (string.IsNullOrWhiteSpace(url)) url = AppConfig.DefaultSharedThemesRepoUrl;
            var browser = new ThemeBrowserWindow(_main, url) { Owner = this };
            browser.ShowDialog();
            if (browser.AnyInstalled) PopulateWizThemePicker();
        }

        private void PopulateWizThemePicker()
        {
            if (WizThemePicker == null) return;
            WizThemePicker.Items.Clear();
            foreach (var f in ThemeManager.AvailableThemes())
                WizThemePicker.Items.Add(new ThemePickerItem(f, ThemeManager.GetThemeDisplayName(f)));
            var active = _main.ConfigSvc.Config.ActiveTheme;
            WizThemePicker.SelectedItem = WizThemePicker.Items
                .OfType<ThemePickerItem>().FirstOrDefault(i => i.FolderName == active);
            if (WizThemePicker.SelectedItem == null && WizThemePicker.Items.Count > 0)
                WizThemePicker.SelectedIndex = 0;
        }

        private void WizThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settingControls) return;
            if (WizThemePicker.SelectedItem is not ThemePickerItem item) return;
            var cfg = _main.ConfigSvc.Config;
            cfg.ActiveTheme = item.FolderName;
            ThemeManager.Instance.Load(item.FolderName, ResolveIsDark(cfg));
        }

        private static bool ResolveIsDark(AppConfig cfg) => cfg.SystemThemeMode switch
        {
            "dark"  => true,
            "light" => false,
            _       => ThemeManager.GetSystemIsDark(),
        };

        // ── Startup ───────────────────────────────────────────────────────────
        private void WizStartWithWindows_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.StartWithWindows = WizStartWithWindowsToggle?.IsChecked == true;
        }

        private void WizStartMinimized_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.StartMinimized = WizStartMinimizedToggle?.IsChecked == true;
        }

        private void WizConfirmOnClose_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.ConfirmOnClose = WizConfirmOnCloseToggle?.IsChecked == true;
        }

        // ── WiFi settings ───────────────────────────────────────────────────────
        private void WizManualToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            bool on = WizManualToggle?.IsChecked == true;
            _vm.DisableWifiRules = on;
            if (WizShowRulesCard != null)
                WizShowRulesCard.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        }

        private void WizShowRules_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.ShowWifiRulesOnMainWindow = WizShowRulesToggle?.IsChecked == true;
        }

        // ── WireGuard Behaviour ─────────────────────────────────────────────────
        private void WizArMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            if (sender is RadioButton rb)
                _main.ConfigSvc.Config.AutoReconnectMode = rb.Tag as string ?? "off";
        }

        private void WizKsMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            if (sender is RadioButton rb)
                _main.ConfigSvc.Config.KillSwitchMode = rb.Tag as string ?? "per-tunnel";
        }

        private void WizDnsIndicator_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.ShowDnsIndicator = WizDnsIndicatorToggle?.IsChecked == true;
        }

        // ── DNS Profiles Behaviour ──────────────────────────────────────────────
        private void WizDnsAutomation_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.DnsAutomationEnabled = WizDnsAutomationToggle?.IsChecked == true;
        }

        // ── Top-bar section toggle buttons (behaviour + visibility) ──────────────
        private void WizShowTunnelBtn_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.ShowTunnelToggleButton = WizShowTunnelBtnToggle?.IsChecked == true;
            _main.RefreshSectionToggleButtons();
        }

        private void WizTunnelDisable_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.TunnelToggleDisables = WizTunnelDisableToggle?.IsChecked == true;
        }

        private void WizShowDnsBtn_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.ShowDnsToggleButton = WizShowDnsBtnToggle?.IsChecked == true;
            _main.RefreshSectionToggleButtons();
        }

        private void WizDnsDisable_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.DnsToggleDisables = WizDnsDisableToggle?.IsChecked == true;
        }

        private void WizShowChartsBtn_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.ShowChartsToggleButton = WizShowChartsBtnToggle?.IsChecked == true;
            _main.RefreshSectionToggleButtons();
        }

        private void WizShowLogBtn_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.ShowLogToggleButton = WizShowLogBtnToggle?.IsChecked == true;
            _main.RefreshSectionToggleButtons();
        }

        private void WizShowWifiBtn_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.ShowWifiToggleButton = WizShowWifiBtnToggle?.IsChecked == true;
            _main.RefreshSectionToggleButtons();
        }

        private void WizWifiDisable_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.WifiToggleDisables = WizWifiDisableToggle?.IsChecked == true;
        }

        // ── Notifications ───────────────────────────────────────────────────────
        private void WizTrayPopup_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.ShowTrayPopupOnSwitch = WizTrayPopupToggle?.IsChecked == true;
        }

        private void WizNotifDuration_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            if (int.TryParse(WizNotifDurationBox?.Text, out int s) && s >= 1 && s <= 60)
                _main.ConfigSvc.Config.NotificationDurationSeconds = s;
            else if (WizNotifDurationBox != null)
                WizNotifDurationBox.Text = _main.ConfigSvc.Config.NotificationDurationSeconds.ToString();
        }

        private void WizStoreConnectionHistory_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.StoreConnectionHistory = WizStoreConnectionHistoryToggle?.IsChecked == true;
        }

        private void WizStoreWifiHistory_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.StoreWifiHistory = WizStoreWifiHistoryToggle?.IsChecked == true;
        }

        private void WizStoreDnsHistory_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.StoreDnsHistory = WizStoreDnsHistoryToggle?.IsChecked == true;
        }

        // ── Install choice ────────────────────────────────────────────────────
        private void WizRunPortable_Click(object sender, RoutedEventArgs e)
        {
            if (WizInstallChoice != null) WizInstallChoice.Visibility = Visibility.Collapsed;
        }

        private void WizInstallNow_Click(object sender, RoutedEventArgs e)
        {
            _main.RunInstallPublic();
            if (WizInstallChoice != null) WizInstallChoice.Visibility = Visibility.Collapsed;
        }

        // ── Import settings ───────────────────────────────────────────────────
        private void WizImport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = Lang.T("SettingsImportTitle"),
                Filter = "MasselGUARD settings (*.masselguard;*.json)|*.masselguard;*.json",
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                string fileVersion = _main.ConfigSvc.Import(dlg.FileName);
                string current     = UpdateChecker.CurrentVersionString;

                if (!string.IsNullOrEmpty(fileVersion) && fileVersion != current)
                {
                    int cmp = string.Compare(fileVersion, current, StringComparison.OrdinalIgnoreCase);
                    var key = cmp > 0 ? "SettingsImportVersionNewer" : "SettingsImportVersionWarning";
                    var proceed = MessageBox.Show(
                        Lang.T(key, fileVersion, current), Lang.T("SettingsImportTitle"),
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (proceed != MessageBoxResult.Yes) { _main.ConfigSvc.Load(); return; }
                }

                if (WizImportResultLabel != null)
                {
                    WizImportResultLabel.Text       = Lang.T("WizardImportSuccess");
                    WizImportResultLabel.Visibility = Visibility.Visible;
                }

                _vm.LoadFromConfig();
                while (_vm.Step < TotalSteps - 1) _vm.NextCommand.Execute(null);
            }
            catch (Exception ex)
            {
                if (WizImportResultLabel != null)
                {
                    WizImportResultLabel.Foreground = (Brush)FindResource("ErrorColor");
                    WizImportResultLabel.Text       = Lang.T("WizardImportFailed", ex.Message);
                    WizImportResultLabel.Visibility = Visibility.Visible;
                }
            }
        }

        // ── Update check ──────────────────────────────────────────────────────
        private ReleaseInfo? _pendingRelease;

        private async void WizCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingRelease != null) { await StartUpdateAsync(_pendingRelease); return; }

            if (WizCheckUpdateBtn != null)
            {
                WizCheckUpdateBtn.IsEnabled = false;
                WizCheckUpdateBtn.Content   = Lang.T("SettingsUpdateChecking");
            }

            ReleaseInfo? latest = null;
            try
            {
                latest = await UpdateChecker.CheckNowAsync(_main.ConfigSvc.Config, _main.ConfigSvc.Save);
                _ = _main.CheckForThemeUpdatesAsync();
            }
            catch { }

            if (WizCheckUpdateBtn != null) WizCheckUpdateBtn.IsEnabled = true;

            if (latest == null)
            {
                if (WizVersionLabel   != null) WizVersionLabel.Text   = $"MasselGUARD v{UpdateChecker.CurrentVersionString} - could not reach server";
                if (WizCheckUpdateBtn != null) WizCheckUpdateBtn.Content = Lang.T("BtnCheckUpdate");
                return;
            }

            if (UpdateChecker.IsNewerVersion(latest.TagName))
            {
                _pendingRelease = latest;
                if (WizVersionLabel   != null) WizVersionLabel.Text     = $"v{latest.TagName} is available  →  you are on v{UpdateChecker.CurrentVersionString}";
                if (WizCheckUpdateBtn != null) WizCheckUpdateBtn.Content = Lang.T("BtnUpdate") is { Length: > 0 } s ? s : "Update now";
                if (BtnNext != null) BtnNext.Content = "Save & Update";
            }
            else if (UpdateChecker.IsAheadOfLatest(latest.TagName))
            {
                if (WizVersionLabel   != null) WizVersionLabel.Text     = $"MasselGUARD v{UpdateChecker.CurrentVersionString} - ahead of release";
                if (WizCheckUpdateBtn != null) { WizCheckUpdateBtn.Content = "✓  Dev build"; WizCheckUpdateBtn.IsEnabled = false; }
            }
            else
            {
                if (WizVersionLabel   != null) WizVersionLabel.Text     = $"MasselGUARD v{UpdateChecker.CurrentVersionString} - up to date";
                if (WizCheckUpdateBtn != null) { WizCheckUpdateBtn.Content = "✓  Up to date"; WizCheckUpdateBtn.IsEnabled = false; }
            }
        }

        private async System.Threading.Tasks.Task StartUpdateAsync(ReleaseInfo release)
        {
            if (release.ZipUrl == null)
            {
                _main.ShowThemedInfo(Lang.T("UpdateNoAsset"), "MasselGUARD");
                return;
            }

            _vm.NextCommand.Execute(null); // save wizard settings (ApplyAndFinish on last step)

            if (WizCheckUpdateBtn != null) WizCheckUpdateBtn.IsEnabled = false;
            if (WizVersionLabel   != null) WizVersionLabel.Text = Lang.T("UpdateDownloading", release.TagName);

            var progress = new Progress<string>(msg =>
                Dispatcher.Invoke(() => { if (WizVersionLabel != null) WizVersionLabel.Text = msg; }));

            try
            {
                await UpdateChecker.UpdateAsync(
                    release, progress, _main.ConfigSvc.Config, _main.ConfigSvc.Save,
                    onShutdown: () => System.Windows.Application.Current.Dispatcher.Invoke(
                        () => ((App)System.Windows.Application.Current).ShutdownApp()));
            }
            catch (Exception ex)
            {
                if (WizCheckUpdateBtn != null) { WizCheckUpdateBtn.IsEnabled = true; WizCheckUpdateBtn.Content = "Retry"; }
                if (WizVersionLabel   != null) WizVersionLabel.Text = $"Update failed: {ex.Message}";
            }
        }

        // ── Window chrome ─────────────────────────────────────────────────────
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }
    }

    internal static class CommandExtensions
    {
        internal static System.Threading.Tasks.Task ExecuteAsync(
            this Infrastructure.AsyncRelayCommand cmd, object? parameter)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource();
            cmd.Execute(parameter);
            tcs.SetResult();
            return tcs.Task;
        }
    }
}
