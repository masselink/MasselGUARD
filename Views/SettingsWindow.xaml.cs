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

            // Populate log level picker
            LogLevelPicker.Items.Add(Lang.T("LogLevelNormal"));
            LogLevelPicker.Items.Add(Lang.T("LogLevelDebug"));
            LogLevelPicker.SelectedIndex = _main.GetConfig().LogLevelSetting == "debug" ? 1 : 0;

            // Initialise local tunnels toggle
            LocalTunnelsToggle.IsChecked = _main.GetConfig().EnableLocalTunnels;

            // Populate language picker
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

            Lang.Instance.LanguageChanged += OnLanguageChanged;
            Closed += (_, _) => Lang.Instance.LanguageChanged -= OnLanguageChanged;

            RefreshInstallState();
            RefreshDllStatus();
            RefreshWireGuardSection();
            RefreshUpdateState();

            // Initialise manual mode toggle without firing the handler
            ManualModeToggle.IsChecked = _main.GetConfig().ManualMode;
            _loading = false;
            VersionLabel.Text = Lang.T("SettingsVersion") + " " + Lang.T("AppTitle");
        }

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
            });
        }

        private void ManualMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var cfg = _main.GetConfig();
            cfg.ManualMode = ManualModeToggle.IsChecked == true;
            _main.SaveConfigPublic();
            _main.ApplyManualMode();
        }

        private void LocalTunnels_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var cfg = _main.GetConfig();
            cfg.EnableLocalTunnels = LocalTunnelsToggle.IsChecked == true;
            _main.SaveConfigPublic();
            _main.ApplyLocalTunnelModePublic();
        }

        private void LogLevel_Changed(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var cfg = _main.GetConfig();
            cfg.LogLevelSetting = LogLevelPicker.SelectedIndex == 1 ? "debug" : "normal";
            _main.SaveConfigPublic();
        }

        private void OpenWireGuard_Click(object sender, RoutedEventArgs e) =>
            _main.OpenWireGuardGui();

        private void ShowWireGuardLog_Click(object sender, RoutedEventArgs e) =>
            _main.OpenWireGuardLog();

        private void RefreshWireGuardSection()
        {
            bool wgInstalled = MainWindow.FindWireGuardExe() != null;
            var vis = wgInstalled ? Visibility.Visible : Visibility.Collapsed;
            WireGuardSectionLabel.Visibility = vis;
            WireGuardSectionCard.Visibility  = vis;
        }

        // ── Install state ────────────────────────────────────────────────────
        public void RefreshInstallState()
        {
            if (_main.IsInstalledCheck())
            {
                var path = _main.GetInstalledPathPublic();
                InstallStatusLabel.Text     = Lang.T("AlreadyInstalled", path ?? "");
                InstallPathLabel.Text       = path ?? "";
                InstallPathLabel.Visibility = Visibility.Visible;

                if (_main.IsRunningPortableWhileInstalled())
                {
                    // Portable copy running alongside install — offer to update
                    InstallBtn.Content = Lang.T("BtnUpdate");
                    InstallBtn.SetResourceReference(ForegroundProperty, "Green");
                    InstallBtn.ToolTip = Lang.T("TooltipUpdate");
                }
                else
                {
                    InstallBtn.Content = Lang.T("BtnUninstall");
                    InstallBtn.SetResourceReference(ForegroundProperty, "Red");
                    InstallBtn.ToolTip = Lang.T("TooltipUninstall");
                }
            }
            else
            {
                InstallStatusLabel.Text     = Lang.T("NotInstalled");
                InstallPathLabel.Visibility = Visibility.Collapsed;
                InstallBtn.Content          = Lang.T("BtnInstall");
                InstallBtn.SetResourceReference(ForegroundProperty, "Accent");
                InstallBtn.ToolTip          = Lang.T("TooltipInstall");
            }
        }

        private void InstallBtn_Click(object sender, RoutedEventArgs e)
        {
            _main.RunInstallPublic();
            RefreshInstallState();
        }

        private void RefreshDllStatus() { /* DLLs no longer required for local tunnels */ }

        // ── Update state ─────────────────────────────────────────────────────
        private void RefreshUpdateState()
        {
            CheckUpdateBtn.Content = Lang.T("BtnCheckUpdate");
            var cfg = _main.GetConfig();

            // Last checked label
            LastCheckedLabel.Text = cfg.LastUpdateCheck == DateTime.MinValue
                ? Lang.T("SettingsUpdateLastChecked", Lang.T("SettingsUpdateNever"))
                : Lang.T("SettingsUpdateLastChecked",
                    cfg.LastUpdateCheck.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

            // Status label
            if (!string.IsNullOrEmpty(cfg.LatestKnownVersion) &&
                UpdateChecker.IsNewerVersion(cfg.LatestKnownVersion))
            {
                UpdateStatusLabel.Text = Lang.T("SettingsUpdateAvailable", cfg.LatestKnownVersion);
                UpdateStatusLabel.SetResourceReference(ForegroundProperty, "Green");
            }
            else if (!string.IsNullOrEmpty(cfg.LatestKnownVersion))
            {
                UpdateStatusLabel.Text = Lang.T("SettingsUpdateCurrent", cfg.LatestKnownVersion);
                UpdateStatusLabel.SetResourceReference(ForegroundProperty, "Sub");
                DoUpdateBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                UpdateStatusLabel.Text = Lang.T("SettingsUpdateLastChecked",
                    Lang.T("SettingsUpdateNever"));
                UpdateStatusLabel.SetResourceReference(ForegroundProperty, "Sub");
            }

            // Show update button if newer version known
            if (_pendingRelease != null && UpdateChecker.IsNewerVersion(_pendingRelease.TagName))
            {
                DoUpdateBtn.Content    = Lang.T("BtnUpdate", _pendingRelease.TagName);
                DoUpdateBtn.Visibility = Visibility.Visible;
            }
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdateBtn.IsEnabled = false;
            UpdateStatusLabel.Text   = Lang.T("SettingsUpdateChecking");
            UpdateStatusLabel.SetResourceReference(ForegroundProperty, "Sub");
            DoUpdateBtn.Visibility   = Visibility.Collapsed;

            try
            {
                _pendingRelease = await UpdateChecker.CheckNowAsync(
                    _main.GetConfig(), _main.SaveConfigPublic);

                LastCheckedLabel.Text = Lang.T("SettingsUpdateLastChecked",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm"));

                if (_pendingRelease != null &&
                    UpdateChecker.IsNewerVersion(_pendingRelease.TagName))
                {
                    UpdateStatusLabel.Text = Lang.T("SettingsUpdateAvailable",
                        _pendingRelease.TagName);
                    UpdateStatusLabel.SetResourceReference(ForegroundProperty, "Green");
                    DoUpdateBtn.Content    = Lang.T("BtnUpdate", _pendingRelease.TagName);
                    DoUpdateBtn.Visibility = Visibility.Visible;
                }
                else
                {
                    UpdateStatusLabel.Text = Lang.T("SettingsUpdateCurrent",
                        _pendingRelease?.TagName ?? "?");
                    UpdateStatusLabel.SetResourceReference(ForegroundProperty, "Sub");
                }
            }
            catch (Exception ex)
            {
                UpdateStatusLabel.Text = Lang.T("UpdateCheckFailed", ex.Message);
                UpdateStatusLabel.SetResourceReference(ForegroundProperty, "Red");
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

            var progress = new Progress<string>(msg =>
                Dispatcher.BeginInvoke(() => UpdateStatusLabel.Text = msg));

            try
            {
                await UpdateChecker.UpdateAsync(_pendingRelease, progress,
                    _main.GetConfig(), _main.SaveConfigPublic);
                // App will exit after this — UpdateAsync calls ShutdownApp
            }
            catch (Exception ex)
            {
                UpdateStatusLabel.Text = Lang.T("UpdateFailed", ex.Message);
                UpdateStatusLabel.SetResourceReference(ForegroundProperty, "Red");
                DoUpdateBtn.IsEnabled    = true;
                CheckUpdateBtn.IsEnabled = true;
            }
        }

        // ── Language picker ──────────────────────────────────────────────────
        private void LanguagePicker_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (LanguagePicker.SelectedItem is LangItem item)
                Lang.Instance.Load(item.Code);
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }
    }
}
