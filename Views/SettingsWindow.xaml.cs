using System;
using System.Windows;
using System.Windows.Input;

namespace WGClientWifiSwitcher.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly MainWindow _main;
        private ReleaseInfo? _pendingRelease;

        public SettingsWindow(MainWindow main)
        {
            InitializeComponent();
            _main = main;

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
            RefreshUpdateState();
            VersionLabel.Text = Lang.T("SettingsVersion") + " " + Lang.T("AppTitle");
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                RefreshInstallState();
                RefreshUpdateState();
                VersionLabel.Text = Lang.T("SettingsVersion") + " " + Lang.T("AppTitle");
                CheckUpdateBtn.Content = Lang.T("BtnCheckUpdate");
            });
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
                InstallBtn.Content          = Lang.T("BtnUninstall");
                InstallBtn.SetResourceReference(ForegroundProperty, "Red");
                InstallBtn.ToolTip          = Lang.T("TooltipUninstall");
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
