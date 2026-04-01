using System;
using System.Windows;
using System.Windows.Input;

namespace MasselGUARD.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly MainWindow _main;
        private ReleaseInfo? _pendingRelease;
        private bool _loading = true;

        public SettingsWindow(MainWindow main)
        {
            InitializeComponent();
            _main = main;

            // ── Log level ────────────────────────────────────────────────────
            LogLevelPicker.Items.Add(Lang.T("LogLevelNormal"));
            LogLevelPicker.Items.Add(Lang.T("LogLevelDebug"));
            LogLevelPicker.SelectedIndex = _main.GetConfig().LogLevelSetting == "debug" ? 1 : 0;

            // ── Language ─────────────────────────────────────────────────────
            foreach (var (code, name) in Lang.AvailableLanguages())
                LanguagePicker.Items.Add(new LangItem(code, name));
            LanguagePicker.DisplayMemberPath = "Name";
            foreach (LangItem item in LanguagePicker.Items)
            {
                if (item.Code == Lang.Instance.CurrentCode)
                {
                    LanguagePicker.SelectedItem = item;
                    break;
                }
            }

            // ── App mode radio buttons ────────────────────────────────────────
            ApplyModeToRadios(_main.GetConfig().Mode);

            // ── Manual (automation) mode ──────────────────────────────────────
            ManualModeToggle.IsChecked = _main.GetConfig().ManualMode;

            // ── Install / DLL / WireGuard section ────────────────────────────
            RefreshInstallState();
            RefreshDllStatus();
            RefreshWireGuardSection();
            RefreshUpdateState();

            // Suppress portable-update prompt toggle
            SuppressUpdatePromptToggle.IsChecked = _main.GetConfig().SuppressPortableUpdatePrompt;

            Lang.Instance.LanguageChanged += OnLanguageChanged;
            Closed += (_, _) => Lang.Instance.LanguageChanged -= OnLanguageChanged;

            _loading = false;
            VersionLabel.Text = Lang.T("SettingsVersion") + " " + Lang.T("AppTitle");
        }

        // ── Language change ───────────────────────────────────────────────────
        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                RefreshInstallState();
                RefreshDllStatus();
                RefreshWireGuardSection();
                RefreshUpdateState();
                VersionLabel.Text = Lang.T("SettingsVersion") + " " + Lang.T("AppTitle");
                CheckUpdateBtn.Content = Lang.T("BtnCheckUpdate");
                // Re-sync the suppress toggle in case language changed its label
                SuppressUpdatePromptToggle.IsChecked =
                    _main.GetConfig().SuppressPortableUpdatePrompt;
            });
        }

        // ── Mode radios ───────────────────────────────────────────────────────
        private void ApplyModeToRadios(AppMode mode)
        {
            ModeStandalone.IsChecked = mode == AppMode.Standalone;
            ModeCompanion.IsChecked  = mode == AppMode.Companion;
            ModeMixed.IsChecked      = mode == AppMode.Mixed;
            RefreshDllStatus();
            RefreshWireGuardSection();
        }

        private void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            AppMode mode = AppMode.Mixed;
            if (ModeStandalone.IsChecked == true) mode = AppMode.Standalone;
            else if (ModeCompanion.IsChecked == true) mode = AppMode.Companion;
            _main.SetMode(mode);
            RefreshDllStatus();
            RefreshWireGuardSection();
        }

        // ── Manual (automation) mode ──────────────────────────────────────────
        private void ManualMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var cfg = _main.GetConfig();
            cfg.ManualMode = ManualModeToggle.IsChecked == true;
            _main.SaveConfigPublic();
            _main.ApplyManualMode();
        }

        // ── Log level ─────────────────────────────────────────────────────────
        private void LogLevel_Changed(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var cfg = _main.GetConfig();
            cfg.LogLevelSetting = LogLevelPicker.SelectedIndex == 1 ? "debug" : "normal";
            _main.SaveConfigPublic();
        }

        // ── WireGuard client section ──────────────────────────────────────────
        private void OpenWireGuard_Click(object sender, RoutedEventArgs e) =>
            _main.OpenWireGuardGui();

        private void ShowWireGuardLog_Click(object sender, RoutedEventArgs e) =>
            _main.OpenWireGuardLog();

        private void RefreshWireGuardSection()
        {
            bool wgInstalled = MainWindow.FindWireGuardExe() != null;
            bool showSection = wgInstalled && _main.GetConfig().Mode != AppMode.Standalone;
            var vis = showSection ? Visibility.Visible : Visibility.Collapsed;
            WireGuardSectionLabel.Visibility = vis;
            WireGuardSectionCard.Visibility  = vis;
        }

        // ── DLL status ────────────────────────────────────────────────────────
        private void RefreshDllStatus()
        {
            var mode = _main.GetConfig().Mode;
            bool showDll = mode != AppMode.Companion;
            DllStatusPanel.Visibility = showDll ? Visibility.Visible : Visibility.Collapsed;

            if (!showDll) return;

            // ValidateDlls() catches both missing DLLs and the wrong wireguard.dll version
            var dllError = TunnelDll.ValidateDlls();
            if (dllError == null)
            {
                DllStatusLabel.Text       = Lang.T("DllStatusPresent");
                DllStatusLabel.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("Green");
            }
            else
            {
                DllStatusLabel.Text       = dllError;
                DllStatusLabel.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("Red");
            }
        }

        // ── Install state ─────────────────────────────────────────────────────
        public void RefreshInstallState()
        {
            if (_main.IsInstalledCheck())
            {
                var path = _main.GetInstalledPathPublic();
                InstallStatusLabel.Text     = Lang.T("AlreadyInstalled", path ?? "");
                InstallPathLabel.Text       = path ?? "";
                InstallPathLabel.Visibility = Visibility.Visible;
                InstallBtn.Content          = _main.IsRunningPortableWhileInstalled()
                    ? Lang.T("TooltipUpdate")
                    : Lang.T("BtnUninstall");
            }
            else
            {
                InstallStatusLabel.Text     = Lang.T("NotInstalled");
                InstallPathLabel.Visibility = Visibility.Collapsed;
                InstallBtn.Content          = Lang.T("BtnInstall");
            }
        }

        private void InstallBtn_Click(object sender, RoutedEventArgs e)
        {
            _main.RunInstallPublic();
            RefreshInstallState();
        }

        // ── Language picker ───────────────────────────────────────────────────
        private void LanguagePicker_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (LanguagePicker.SelectedItem is LangItem item)
            {
                Lang.Instance.Load(item.Code);
                AppConfig.SaveLanguage(item.Code);
            }
        }

        // ── Update section ────────────────────────────────────────────────────
        private void RefreshUpdateState()
        {
            CheckUpdateBtn.Content = Lang.T("BtnCheckUpdate");
            var cfg = _main.GetConfig();
            string current = UpdateChecker.CurrentVersionString;
            string? latest = cfg.LatestKnownVersion;

            if (latest != null && UpdateChecker.IsAheadOfLatest(latest))
                UpdateStatusLabel.Text = Lang.T("SettingsUpdateAhead", current, latest);
            else if (latest != null && UpdateChecker.IsNewerVersion(latest))
                UpdateStatusLabel.Text = Lang.T("SettingsUpdateAvailable", latest);
            else
                UpdateStatusLabel.Text = Lang.T("SettingsUpdateCurrent", current);

            LastCheckedLabel.Text = cfg.LastUpdateCheck == DateTime.MinValue
                ? Lang.T("SettingsUpdateLastChecked", Lang.T("SettingsUpdateNever"))
                : Lang.T("SettingsUpdateLastChecked",
                    cfg.LastUpdateCheck.ToLocalTime().ToString("g"));

            // Show update button only when the published version is actually newer
            bool hasUpdate = _pendingRelease != null
                && UpdateChecker.IsNewerVersion(_pendingRelease.TagName);
            DoUpdateBtn.Visibility = hasUpdate ? Visibility.Visible : Visibility.Collapsed;
            if (hasUpdate)
                DoUpdateBtn.Content = Lang.T("BtnUpdate", _pendingRelease!.TagName);
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdateBtn.IsEnabled = false;
            UpdateStatusLabel.Text   = Lang.T("SettingsUpdateChecking");
            DoUpdateBtn.Visibility   = Visibility.Collapsed;

            try
            {
                _pendingRelease = await UpdateChecker.CheckNowAsync(
                    _main.GetConfig(), _main.SaveConfigPublic);
                RefreshUpdateState();
            }
            catch (Exception ex)
            {
                UpdateStatusLabel.Text = Lang.T("UpdateCheckFailed", ex.Message);
            }
            finally
            {
                CheckUpdateBtn.IsEnabled = true;
            }
        }

        private async void DoUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingRelease == null) return;
            DoUpdateBtn.IsEnabled    = false;
            CheckUpdateBtn.IsEnabled = false;

            try
            {
                var progress = new Progress<string>(msg =>
                    UpdateStatusLabel.Text = msg);
                await UpdateChecker.UpdateAsync(
                    _pendingRelease,
                    progress,
                    _main.GetConfig(), _main.SaveConfigPublic);
            }
            catch (Exception ex)
            {
                UpdateStatusLabel.Text = Lang.T("UpdateFailed", ex.Message);
            }
            finally
            {
                DoUpdateBtn.IsEnabled    = true;
                CheckUpdateBtn.IsEnabled = true;
            }
        }

        // ── Suppress update prompt ────────────────────────────────────────────
        private void SuppressUpdatePrompt_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var cfg = _main.GetConfig();
            cfg.SuppressPortableUpdatePrompt = SuppressUpdatePromptToggle.IsChecked == true;
            _main.SaveConfigPublic();
        }

        // ── Window chrome ─────────────────────────────────────────────────────
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
    }
}
