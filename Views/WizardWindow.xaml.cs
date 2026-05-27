using System;
using System.Windows;
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

        private bool _settingControls; // true while programmatically updating controls — suppresses event handlers

        private const int TotalSteps = 5; // steps 0-4

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

            // Language picker
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
            // 5 steps: Step0–Step4
            var steps = new[] { Step0, Step1, Step2, Step3, Step4 };
            for (int i = 0; i < steps.Length; i++)
                if (steps[i] != null)
                    steps[i].Visibility = i == _vm.Step
                        ? Visibility.Visible : Visibility.Collapsed;

            // ── Step 0: Welcome ──────────────────────────────────────────────
            if (WizInstallChoice != null)
                WizInstallChoice.Visibility =
                    (!_isUpgrade && _main.AppRunMode == MainWindow.AppRunModeKind.Standalone)
                    ? Visibility.Visible : Visibility.Collapsed;

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
                bool autoTheme = _main.ConfigSvc.Config.AutoTheme;
                if (WizThemeAuto != null)  WizThemeAuto.IsChecked  = autoTheme;
                if (WizThemeDark != null)  WizThemeDark.IsChecked  = !autoTheme && IsCurrentThemeDark();
                if (WizThemeLight != null) WizThemeLight.IsChecked = !autoTheme && !IsCurrentThemeDark();
                _settingControls = false;
            }

            // ── Step 2: Mode ─────────────────────────────────────────────────
            if (_vm.Step == 2)
            {
                _settingControls = true;
                WizModeStandalone.IsChecked = _vm.Mode == AppMode.Standalone;
                WizModeCompanion.IsChecked  = _vm.Mode == AppMode.Companion;
                WizModeMixed.IsChecked      = _vm.Mode == AppMode.Mixed;
                _settingControls = false;
            }

            // ── Step 3: Automation ───────────────────────────────────────────
            if (_vm.Step == 3)
            {
                _settingControls = true;
                WizManualToggle.IsChecked = _vm.DisableWifiRules;
                if (WizShowRulesToggle != null)
                    WizShowRulesToggle.IsChecked = _main.ConfigSvc.Config.ShowWifiRulesOnMainWindow;
                _settingControls = false;
                if (WizShowRulesCard != null)
                    WizShowRulesCard.Visibility = _vm.DisableWifiRules
                        ? Visibility.Collapsed : Visibility.Visible;
            }

            // ── Step 4: Done ─────────────────────────────────────────────────
            if (_vm.Step == 4)
            {
                if (WizVersionLabel != null)
                    WizVersionLabel.Text = $"MasselGUARD v{UpdateChecker.CurrentVersionString}";
                if (WizCheckUpdateBtn != null)
                    WizCheckUpdateBtn.Content = Lang.T("BtnCheckUpdate");
            }

            // Nav buttons
            BtnBack.IsEnabled = _vm.CanGoBack;
            BtnNext.Content   = _vm.IsLastStep
                ? Lang.T("WizardBtnFinish")
                : Lang.T("WizardBtnNext");
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
            var dots   = new[] { Dot0, Dot1, Dot2, Dot3, Dot4 };
            var accent = (Brush)FindResource("Accent");
            var dim    = (Brush)FindResource("BorderColor");
            for (int i = 0; i < dots.Length; i++)
                if (dots[i] != null)
                    dots[i].Fill = i == _vm.Step ? accent : dim;
        }

        // ── Navigation ────────────────────────────────────────────────────────
        private void BtnBack_Click(object sender, RoutedEventArgs e) => _vm.BackCommand.Execute(null);
        private void BtnNext_Click(object sender, RoutedEventArgs e) => _vm.NextCommand.Execute(null);
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
                cfg.AutoTheme = true;
                // Apply system preference immediately
                bool sysIsDark = ThemeManager.GetSystemIsDark();
                ThemeManager.Instance.Load(sysIsDark
                    ? (cfg.ActiveDarkTheme  ?? "default-dark")
                    : (cfg.ActiveLightTheme ?? "default-light"));
            }
            else if (WizThemeDark?.IsChecked == true)
            {
                cfg.AutoTheme = false;
                ThemeManager.Instance.Load(cfg.ActiveDarkTheme ?? "default-dark");
            }
            else if (WizThemeLight?.IsChecked == true)
            {
                cfg.AutoTheme = false;
                ThemeManager.Instance.Load(cfg.ActiveLightTheme ?? "default-light");
            }
        }

        // ── Automation ────────────────────────────────────────────────────────
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

        // ── Install choice ────────────────────────────────────────────────────
        private void WizRunPortable_Click(object sender, RoutedEventArgs e)
            => _vm.NextCommand.Execute(null);

        private void WizInstallNow_Click(object sender, RoutedEventArgs e)
        {
            _main.RunInstallPublic();
            _vm.NextCommand.Execute(null);
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

                // Build summary
                var cfg = _main.ConfigSvc.Config;
                string summary = $"{cfg.Rules.Count} WiFi rules, {cfg.TunnelGroups.Count} groups, mode: {cfg.Mode}";

                if (WizImportResultLabel != null)
                {
                    WizImportResultLabel.Text       = Lang.T("WizardImportSuccess");
                    WizImportResultLabel.Visibility = Visibility.Visible;
                }

                // Sync VM from imported config
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
        private async void WizCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (WizCheckUpdateBtn != null)
            {
                WizCheckUpdateBtn.IsEnabled = false;
                WizCheckUpdateBtn.Content   = Lang.T("SettingsUpdateChecking");
            }
            await _vm.CheckUpdateCommand.ExecuteAsync(null);
            if (WizCheckUpdateBtn != null) WizCheckUpdateBtn.IsEnabled = true;
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
