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

            // ── Step 0: Welcome ──────────────────────────────────────────────
            if (WizUpgradeNotice != null)
                WizUpgradeNotice.Visibility = _isUpgrade ? Visibility.Visible : Visibility.Collapsed;
            if (_isUpgrade)
            {
                if (WizUpgradeTitle != null)
                    WizUpgradeTitle.Text = Lang.T("WizardUpgradeTitle");
                if (WizUpgradeBody != null)
                    WizUpgradeBody.Text  = Lang.T("WizardUpgradeBody",
                        UpdateChecker.CurrentVersionString, _vm.PreviousAppVersion);
            }

            // ── Step 1: Language & theme ─────────────────────────────────────
            if (_vm.Step == 1)
            {
                _settingControls = true;
                WizLangPicker.SelectedItem = _vm.SelectedLanguage;
                var sysMode = _main.ConfigSvc.Config.SystemThemeMode ?? "auto";
                if (WizThemeAuto  != null) WizThemeAuto.IsChecked  = sysMode == "auto";
                if (WizThemeDark  != null) WizThemeDark.IsChecked  = sysMode == "dark";
                if (WizThemeLight != null) WizThemeLight.IsChecked = sysMode == "light";
                PopulateWizThemePicker();
                _settingControls = false;
            }

            // ── Step 2: Choose your view ─────────────────────────────────────
            if (_vm.Step == 2)
                HighlightMatchingPreset();

            // ── Step 3: Custom view details (only reachable via "Custom") ────
            if (_vm.Step == 3)
            {
                var cfg = _main.ConfigSvc.Config;
                _settingControls = true;
                WizCustomTimelineToggle.IsChecked    = cfg.ShowTimeline;
                WizCustomActivityLogToggle.IsChecked = cfg.ShowActivityLog;
                WizCustomWifiRulesToggle.IsChecked   = cfg.ShowWifiRulesOnMainWindow;
                WizCustomManualToggle.IsChecked      = _vm.DisableWifiRules;
                _settingControls = false;
            }

            // ── Step 4: Mode ─────────────────────────────────────────────────
            if (_vm.Step == 4)
            {
                _settingControls = true;
                WizModeStandalone.IsChecked = _vm.Mode == AppMode.Standalone;
                WizModeCompanion.IsChecked  = _vm.Mode == AppMode.Companion;
                WizModeMixed.IsChecked      = _vm.Mode == AppMode.Mixed;
                _settingControls = false;
            }

            // ── Step 5: Startup & Installation ───────────────────────────────
            if (_vm.Step == 5)
            {
                if (WizInstallChoice != null)
                    WizInstallChoice.Visibility =
                        (!_isUpgrade && _main.AppRunMode == MainWindow.AppRunModeKind.Standalone)
                        ? Visibility.Visible : Visibility.Collapsed;

                _settingControls = true;
                var cfg = _main.ConfigSvc.Config;
                if (WizStartWithWindowsToggle != null)
                    WizStartWithWindowsToggle.IsChecked = cfg.StartWithWindows;
                if (WizConfirmOnCloseToggle != null)
                    WizConfirmOnCloseToggle.IsChecked = cfg.ConfirmOnClose;
                _settingControls = false;
            }

            // ── Step 6: WiFi Automation ──────────────────────────────────────
            if (_vm.Step == 6)
            {
                _settingControls = true;
                if (WizManualToggle != null)
                    WizManualToggle.IsChecked = _vm.DisableWifiRules;
                if (WizShowRulesToggle != null)
                    WizShowRulesToggle.IsChecked = _main.ConfigSvc.Config.ShowWifiRulesOnMainWindow;
                if (WizDnsIndicatorToggle != null)
                    WizDnsIndicatorToggle.IsChecked = _main.ConfigSvc.Config.ShowDnsIndicator;
                _settingControls = false;
                if (WizShowRulesCard != null)
                    WizShowRulesCard.Visibility = _vm.DisableWifiRules
                        ? Visibility.Collapsed : Visibility.Visible;
            }

            // ── Step 7: Behavior ─────────────────────────────────────────────
            if (_vm.Step == 7)
            {
                _settingControls = true;
                var cfg = _main.ConfigSvc.Config;
                string ar = cfg.AutoReconnectMode;
                if (WizArOff       != null) WizArOff.IsChecked       = ar == "off";
                if (WizArPerTunnel != null) WizArPerTunnel.IsChecked = ar == "per-tunnel";
                if (WizArAlways    != null) WizArAlways.IsChecked    = ar == "always";
                if (WizArOff != null && WizArPerTunnel != null && WizArAlways != null
                    && WizArOff.IsChecked != true && WizArPerTunnel.IsChecked != true && WizArAlways.IsChecked != true)
                    WizArOff.IsChecked = true;

                if (WizStoreConnectionHistoryToggle != null)
                    WizStoreConnectionHistoryToggle.IsChecked = cfg.StoreConnectionHistory;
                if (WizStoreWifiHistoryToggle != null)
                    WizStoreWifiHistoryToggle.IsChecked = cfg.StoreWifiHistory;
                if (WizTrayPopupToggle != null)
                    WizTrayPopupToggle.IsChecked = cfg.ShowTrayPopupOnSwitch;
                _settingControls = false;
            }

            // ── Step 8: Done ─────────────────────────────────────────────────
            if (_vm.Step == 8)
            {
                if (WizVersionLabel != null)
                    WizVersionLabel.Text = $"MasselGUARD v{UpdateChecker.CurrentVersionString}";
                if (WizCheckUpdateBtn != null)
                    WizCheckUpdateBtn.Content = Lang.T("BtnCheckUpdate");
                BuildSummary();
            }

            BtnBack.IsEnabled = _vm.CanGoBack;
            BtnNext.Content   = _vm.IsLastStep && _pendingRelease != null
                ? "Save & Update"
                : _vm.IsLastStep
                    ? Lang.T("WizardBtnFinish")
                    : Lang.T("WizardBtnNext");
        }

        // ── Summary (step 6) ──────────────────────────────────────────────────
        private void BuildSummary()
        {
            if (WizSummaryPanel == null) return;
            WizSummaryPanel.Children.Clear();
            var cfg = _main.ConfigSvc.Config;

            void Row(string label, string value)
            {
                var g = new System.Windows.Controls.Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var lbl = new TextBlock
                {
                    Text       = label,
                    FontFamily = (FontFamily)FindResource("Theme.FontFamily"),
                    FontSize   = 10,
                    Foreground = (Brush)FindResource("TextMuted"),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                var val = new TextBlock
                {
                    Text       = value,
                    FontFamily = (FontFamily)FindResource("Theme.FontFamily"),
                    FontSize   = 10,
                    Foreground = (Brush)FindResource("TextPrimary"),
                    TextWrapping = TextWrapping.Wrap,
                };
                System.Windows.Controls.Grid.SetColumn(lbl, 0);
                System.Windows.Controls.Grid.SetColumn(val, 1);
                g.Children.Add(lbl);
                g.Children.Add(val);
                g.Margin = new Thickness(0, 0, 0, 4);
                WizSummaryPanel.Children.Add(g);
            }

            Row("Mode",              cfg.Mode.ToString());
            Row("Auto-reconnect",    cfg.AutoReconnectMode);
            Row("Start with Windows", cfg.StartWithWindows ? "Yes" : "No");
            Row("Record connections", cfg.StoreConnectionHistory ? "On" : "Off");
            Row("Record WiFi SSID",   cfg.StoreWifiHistory ? "On" : "Off");
            Row("Tray notifications", cfg.ShowTrayPopupOnSwitch ? "On" : "Off");
        }

        private bool IsCurrentThemeDark()
        {
            var bg = Application.Current?.Resources["WindowBg"] as SolidColorBrush;
            if (bg == null) return true;
            var c = bg.Color;
            double lum = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
            return lum < 128;
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

        // ── Mode ──────────────────────────────────────────────────────────────
        private void WizMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            if (WizModeStandalone?.IsChecked == true)     _vm.Mode = AppMode.Standalone;
            else if (WizModeCompanion?.IsChecked == true) _vm.Mode = AppMode.Companion;
            else                                          _vm.Mode = AppMode.Mixed;
        }

        // ── Language ──────────────────────────────────────────────────────────
        private void WizLang_Changed(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_settingControls) return;
            if (WizLangPicker.SelectedItem is LangItem item)
                _vm.SelectedLanguage = item;
        }

        // ── Theme ─────────────────────────────────────────────────────────────
        private void WizTheme_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            var cfg = _main.ConfigSvc.Config;
            if (WizThemeAuto?.IsChecked == true)
            {
                cfg.SystemThemeMode = "auto";
                bool sysIsDark = ThemeManager.GetSystemIsDark();
                ThemeManager.Instance.Load(cfg.ActiveTheme ?? "__system__", sysIsDark);
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
            if (string.IsNullOrWhiteSpace(url))
                url = AppConfig.DefaultSharedThemesRepoUrl;

            var browser = new ThemeBrowserWindow(_main, url) { Owner = this };
            browser.ShowDialog();
            if (browser.AnyInstalled)
                PopulateWizThemePicker();   // surface newly downloaded themes right away
        }

        /// <summary>Fills the theme picker from every installed theme and selects the active one —
        /// without this, a theme downloaded via "Download more themes…" has no way to be picked.</summary>
        private void PopulateWizThemePicker()
        {
            if (WizThemePicker == null) return;
            WizThemePicker.Items.Clear();
            foreach (var f in ThemeManager.AvailableThemes())
                WizThemePicker.Items.Add(new ThemePickerItem(f, ThemeManager.GetThemeDisplayName(f)));
            var active = _main.ConfigSvc.Config.ActiveTheme;
            WizThemePicker.SelectedItem = WizThemePicker.Items
                .OfType<ThemePickerItem>()
                .FirstOrDefault(i => i.FolderName == active);
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

        // ── View preset (Step 2) ────────────────────────────────────────────────
        // Simple/Manual/Expert just pre-fill the same panel-visibility toggles that
        // live individually in Settings — nothing new to persist, and every value
        // stays freely editable afterward (in Settings, or by re-picking a preset
        // there via the reusable selector). Custom instead advances to Step 3, which
        // shows the same four toggles individually (WizardViewModel skips Step 3 for
        // any other card, so it's only ever reachable this way).
        private void WizPresetSimple_Click(object sender, MouseButtonEventArgs e) =>
            ApplyViewPreset(showTimeline: false, showActivityLog: false, showWifiRules: true, manualMode: false, WizPresetSimple);

        private void WizPresetManual_Click(object sender, MouseButtonEventArgs e) =>
            ApplyViewPreset(showTimeline: false, showActivityLog: true, showWifiRules: false, manualMode: true, WizPresetManual);

        private void WizPresetExpert_Click(object sender, MouseButtonEventArgs e) =>
            ApplyViewPreset(showTimeline: true, showActivityLog: true, showWifiRules: true, manualMode: false, WizPresetExpert);

        private void ApplyViewPreset(bool showTimeline, bool showActivityLog, bool showWifiRules, bool manualMode, Border selectedCard)
        {
            _vm.CustomView = false;   // a fixed preset replaces any custom picks — Step 3 is skipped again

            var cfg = _main.ConfigSvc.Config;
            cfg.ShowTimeline              = showTimeline;
            cfg.ShowActivityLog           = showActivityLog;
            cfg.ShowWifiRulesOnMainWindow = showWifiRules;
            cfg.ShowTunnelRulesColumn     = showWifiRules;
            // The timeline panel also stays visible from WiFi history alone
            // (ApplyInfoSectionMode: ShowTimeline || ShowWifiInChart) — tie it to the
            // same on/off so Simple/Manual genuinely hide it rather than leaving a
            // WiFi-only strip behind.
            cfg.ShowWifiInChart           = showTimeline;
            _vm.DisableWifiRules          = manualMode;   // committed to ManualMode when the wizard finishes

            ApplyViewLive(showActivityLog);
            HighlightPresetCard(selectedCard);
        }

        private void WizPresetCustom_Click(object sender, MouseButtonEventArgs e)
        {
            _vm.CustomView = true;   // makes Step 3 reachable on Next
            HighlightPresetCard(WizPresetCustom);
        }

        private void WizCustomToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;

            var cfg = _main.ConfigSvc.Config;
            bool showTimeline    = WizCustomTimelineToggle.IsChecked == true;
            bool showActivityLog = WizCustomActivityLogToggle.IsChecked == true;
            bool showWifiRules   = WizCustomWifiRulesToggle.IsChecked == true;
            bool manualMode      = WizCustomManualToggle.IsChecked == true;

            cfg.ShowTimeline              = showTimeline;
            cfg.ShowActivityLog           = showActivityLog;
            cfg.ShowWifiRulesOnMainWindow = showWifiRules;
            cfg.ShowTunnelRulesColumn     = showWifiRules;
            cfg.ShowWifiInChart           = showTimeline;
            _vm.DisableWifiRules          = manualMode;

            ApplyViewLive(showActivityLog);
        }

        private void ApplyViewLive(bool showActivityLog)
        {
            _main.RefreshWifiRulesPanel();
            _main._vm.NotifyRulesColumnChanged();
            _main.ApplyInfoSectionMode();
            _main.SetLogPanelVisible(showActivityLog);
        }

        /// <summary>Best-effort match of the current config against a known preset, for when
        /// the user navigates back to this step after picking one.</summary>
        private void HighlightMatchingPreset()
        {
            var cfg = _main.ConfigSvc.Config;
            if (!cfg.ShowTimeline && !cfg.ShowActivityLog && cfg.ShowWifiRulesOnMainWindow && !_vm.DisableWifiRules)
                HighlightPresetCard(WizPresetSimple);
            else if (!cfg.ShowTimeline && cfg.ShowActivityLog && !cfg.ShowWifiRulesOnMainWindow && _vm.DisableWifiRules)
                HighlightPresetCard(WizPresetManual);
            else if (cfg.ShowTimeline && cfg.ShowActivityLog && cfg.ShowWifiRulesOnMainWindow && !_vm.DisableWifiRules)
                HighlightPresetCard(WizPresetExpert);
            // Otherwise leave whichever card (if any) the user already picked this
            // session highlighted — re-guessing "Custom" on every back-navigation
            // would fight a fixed preset that just doesn't match due to an unrelated
            // config value.
        }

        private void HighlightPresetCard(Border? selected)
        {
            var accent = (Brush)FindResource("Accent");
            var dim    = (Brush)FindResource("BorderColor");
            foreach (var b in new[] { WizPresetSimple, WizPresetManual, WizPresetExpert, WizPresetCustom })
                if (b != null) b.BorderBrush = b == selected ? accent : dim;
        }

        // ── Startup ───────────────────────────────────────────────────────────
        private void WizStartWithWindows_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.StartWithWindows = WizStartWithWindowsToggle?.IsChecked == true;
        }

        private void WizConfirmOnClose_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.ConfirmOnClose = WizConfirmOnCloseToggle?.IsChecked == true;
        }

        // ── WiFi Automation ───────────────────────────────────────────────────
        private void WizManualToggle_Changed(object sender, RoutedEventArgs e)
        {
            bool on = WizManualToggle?.IsChecked == true;
            _vm.DisableWifiRules = on;
            if (WizShowRulesCard != null)
                WizShowRulesCard.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        }

        private void WizShowRules_Changed(object sender, RoutedEventArgs e)
        {
            _main.ConfigSvc.Config.ShowWifiRulesOnMainWindow =
                WizShowRulesToggle?.IsChecked == true;
        }

        private void WizDnsIndicator_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.ShowDnsIndicator = WizDnsIndicatorToggle?.IsChecked == true;
        }

        // ── Behavior ─────────────────────────────────────────────────────────
        private void WizArMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            if (sender is RadioButton rb)
                _main.ConfigSvc.Config.AutoReconnectMode = rb.Tag as string ?? "off";
        }

        private void WizStoreConnectionHistory_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.StoreConnectionHistory =
                WizStoreConnectionHistoryToggle?.IsChecked == true;
        }

        private void WizStoreWifiHistory_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.StoreWifiHistory =
                WizStoreWifiHistoryToggle?.IsChecked == true;
        }

        private void WizTrayPopup_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingControls) return;
            _main.ConfigSvc.Config.ShowTrayPopupOnSwitch =
                WizTrayPopupToggle?.IsChecked == true;
        }

        // ── Install choice ────────────────────────────────────────────────────
        private void WizRunPortable_Click(object sender, RoutedEventArgs e)
        {
            if (WizInstallChoice != null)
                WizInstallChoice.Visibility = Visibility.Collapsed;
        }

        private void WizInstallNow_Click(object sender, RoutedEventArgs e)
        {
            _main.RunInstallPublic();
            if (WizInstallChoice != null)
                WizInstallChoice.Visibility = Visibility.Collapsed;
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
                        Lang.T(key, fileVersion, current),
                        Lang.T("SettingsImportTitle"),
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (proceed != MessageBoxResult.Yes)
                    {
                        _main.ConfigSvc.Load();
                        return;
                    }
                }

                if (WizImportResultLabel != null)
                {
                    WizImportResultLabel.Text       = Lang.T("WizardImportSuccess");
                    WizImportResultLabel.Visibility = Visibility.Visible;
                }

                _vm.LoadFromConfig();

                // Jump to Done step
                while (_vm.Step < TotalSteps - 1)
                    _vm.NextCommand.Execute(null);
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
            // Second click after a found update → trigger install
            if (_pendingRelease != null)
            {
                await StartUpdateAsync(_pendingRelease);
                return;
            }

            if (WizCheckUpdateBtn != null)
            {
                WizCheckUpdateBtn.IsEnabled = false;
                WizCheckUpdateBtn.Content   = Lang.T("SettingsUpdateChecking");
            }

            ReleaseInfo? latest = null;
            try
            {
                latest = await UpdateChecker.CheckNowAsync(
                    _main.ConfigSvc.Config, _main.ConfigSvc.Save);
            }
            catch { /* network unavailable */ }

            if (WizCheckUpdateBtn != null) WizCheckUpdateBtn.IsEnabled = true;

            if (latest == null)
            {
                if (WizVersionLabel    != null) WizVersionLabel.Text    = $"MasselGUARD v{UpdateChecker.CurrentVersionString} — could not reach server";
                if (WizCheckUpdateBtn  != null) WizCheckUpdateBtn.Content = Lang.T("BtnCheckUpdate");
                return;
            }

            if (UpdateChecker.IsNewerVersion(latest.TagName))
            {
                _pendingRelease = latest;
                if (WizVersionLabel   != null) WizVersionLabel.Text     = $"v{latest.TagName} is available  →  you are on v{UpdateChecker.CurrentVersionString}";
                if (WizCheckUpdateBtn != null) WizCheckUpdateBtn.Content = Lang.T("BtnUpdate") is { Length: > 0 } s ? s : "Update now";
                // Flip the footer Finish button to "Save & Update"
                if (BtnNext != null) BtnNext.Content = "Save & Update";
            }
            else if (UpdateChecker.IsAheadOfLatest(latest.TagName))
            {
                if (WizVersionLabel   != null) WizVersionLabel.Text     = $"MasselGUARD v{UpdateChecker.CurrentVersionString} — ahead of release";
                if (WizCheckUpdateBtn != null) { WizCheckUpdateBtn.Content = "✓  Dev build"; WizCheckUpdateBtn.IsEnabled = false; }
            }
            else
            {
                if (WizVersionLabel   != null) WizVersionLabel.Text     = $"MasselGUARD v{UpdateChecker.CurrentVersionString} — up to date";
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

            // Save wizard settings before handing off to updater
            _vm.NextCommand.Execute(null); // triggers ApplyAndFinish if on last step, otherwise harmless

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
                // UpdateAsync calls onShutdown on success — execution never continues here.
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
