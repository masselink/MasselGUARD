using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace MasselGUARD.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly MainWindow _main;
        private ReleaseInfo? _pendingRelease;
        private bool _loading = true;
        private string _activeTab = "General";
        private bool _themeSwitching = false;

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

            // ── Theme picker ─────────────────────────────────────────────────
            PopulateThemePicker();

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
            // Tray popup notification
            TrayPopupToggle.IsChecked = _main.GetConfig().ShowTrayPopupOnSwitch;

            Lang.Instance.LanguageChanged += OnLanguageChanged;
            Closed += (_, _) => Lang.Instance.LanguageChanged -= OnLanguageChanged;

            _loading = false;
            VersionLabel.Text = Lang.T("AppTitle");
            ShowTab("General");
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
            // Refresh display immediately so user sees the DLL/WG status
            // for the selected mode — actual save happens on Save button.
            RefreshDllStatus();
            RefreshWireGuardSection();
        }

        // Read the selected mode from the radio buttons (not from saved config)
        private AppMode SelectedMode()
        {
            if (ModeStandalone.IsChecked == true) return AppMode.Standalone;
            if (ModeCompanion.IsChecked  == true) return AppMode.Companion;
            return AppMode.Mixed;
        }

        // ── Manual (automation) mode ──────────────────────────────────────────
        private void ManualMode_Changed(object sender, RoutedEventArgs e)
        {
            // Deferred — applied on Save
        }

        // ── Log level ─────────────────────────────────────────────────────────
        private void LogLevel_Changed(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // Deferred — applied on Save
        }

        // ── WireGuard client section ──────────────────────────────────────────
        private void OpenWireGuard_Click(object sender, RoutedEventArgs e) =>
            _main.OpenWireGuardGui();

        private void ShowWireGuardLog_Click(object sender, RoutedEventArgs e) =>
            _main.OpenWireGuardLog();

        private void RefreshWireGuardSection()
        {
            bool wgInstalled = MainWindow.FindWireGuardExe() != null;
            bool showSection = wgInstalled && SelectedMode() != AppMode.Standalone;
            var vis = showSection ? Visibility.Visible : Visibility.Collapsed;
            WireGuardSectionLabel.Visibility = vis;
            WireGuardSectionCard.Visibility  = vis;
        }

        // ── Mode dependency status ────────────────────────────────────────────
        // Always visible — shows DLL status for Standalone/Mixed,
        // WireGuard installation status for Companion/Mixed.
        private void RefreshDllStatus()
        {
            var mode = SelectedMode();
            DllStatusPanel.Visibility = Visibility.Visible;

            var lines = new System.Text.StringBuilder();
            bool allOk = true;

            // DLL check (Standalone + Mixed)
            if (mode != AppMode.Companion)
            {
                var dllError = TunnelDll.ValidateDlls();
                if (dllError == null)
                    lines.AppendLine("✓  " + Lang.T("DllStatusPresent"));
                else
                {
                    lines.AppendLine("⚠  " + dllError);
                    allOk = false;
                }
            }

            // WireGuard GUI check (Companion + Mixed)
            if (mode != AppMode.Standalone)
            {
                bool wgFound = MainWindow.FindWireGuardExe() != null;
                if (wgFound)
                    lines.AppendLine("✓  " + Lang.T("WizardWgBody"));
                else
                {
                    lines.AppendLine("⚠  " + Lang.T("WizardWgMissing"));
                    lines.AppendLine("    ↳ " + Lang.T("WgDownloadHint"));
                    allOk = false;
                }
            }

            DllStatusLabel.Text       = lines.ToString().TrimEnd();
            DllStatusLabel.Foreground = allOk ? ThemeRes.Success : ThemeRes.Danger;
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
            // Deferred — applied on Save
        }

        private void TrayPopup_Changed(object sender, RoutedEventArgs e)
        {
            // Deferred — applied on Save
        }

        // ── Save button ───────────────────────────────────────────────────────
        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var cfg = _main.GetConfig();

            // Mode
            var newMode = SelectedMode();
            if (cfg.Mode != newMode)
            {
                _main.SetMode(newMode);
                _main.ApplyLocalTunnelModePublic();
            }

            // Manual (automation) mode
            bool newManual = ManualModeToggle.IsChecked == true;
            if (cfg.ManualMode != newManual)
            {
                cfg.ManualMode = newManual;
                _main.ApplyManualMode();
            }

            // Log level
            cfg.LogLevelSetting = LogLevelPicker.SelectedIndex == 1 ? "debug" : "normal";

            // Suppress update prompt
            cfg.SuppressPortableUpdatePrompt = SuppressUpdatePromptToggle.IsChecked == true;

            // Tray popup
            cfg.ShowTrayPopupOnSwitch = TrayPopupToggle.IsChecked == true;

            _main.SaveConfigPublic();
            Close();
        }

        // ── Tab switching ─────────────────────────────────────────────────────
        private void TabBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button btn) return;
            // Map button Name → tab name (Tag is used for active-highlight styling)
            string tab = btn.Name switch
            {
                "TabBtnAdvanced" => "Advanced",
                "TabBtnAbout"    => "About",
                _                => "General",
            };
            ShowTab(tab);
        }

        private void ShowTab(string tab)
        {
            _activeTab = tab;
            PageGeneral.Visibility    = tab == "General"    ? Visibility.Visible : Visibility.Collapsed;
            PageAppearance.Visibility = tab == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
            PageAdvanced.Visibility   = tab == "Advanced"   ? Visibility.Visible : Visibility.Collapsed;
            PageAbout.Visibility      = tab == "About"      ? Visibility.Visible : Visibility.Collapsed;

            // Highlight the active sidebar button
            TabBtnGeneral.Tag    = tab == "General"    ? "Active" : null;
            TabBtnAppearance.Tag = tab == "Appearance" ? "Active" : null;
            TabBtnAdvanced.Tag   = tab == "Advanced"   ? "Active" : null;
            TabBtnAbout.Tag      = tab == "About"      ? "Active" : null;

            // Refresh state for the tab we just switched to
            if (tab == "Advanced")   { RefreshInstallState(); RefreshDllStatus(); RefreshWireGuardSection(); ScanOrphans(); }
            if (tab == "About")      { RefreshUpdateState(); }
            if (tab == "Appearance") { PopulateThemePicker(); }
        }

        // ── Wizard ───────────────────────────────────────────────────────────
        private void RunWizard_Click(object sender, RoutedEventArgs e)
        {
            Close();
            var wiz = new WizardWindow(_main) { Owner = _main };
            wiz.ShowDialog();
        }

        // ── GitHub link ──────────────────────────────────────────────────────
        private void GithubLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = "https://github.com/masselink/MasselGUARD", UseShellExecute = true }); }
            catch { }
        }

        // ── Orphaned services ────────────────────────────────────────────────
        private List<MainWindow.OrphanedService> _lastOrphans = new();

        private void ScanOrphans_Click(object sender, RoutedEventArgs e) => ScanOrphans();

        private void ScanOrphans()
        {
            OrphanStatusLabel.Text = Lang.T("OrphansScanning");
            OrphanListPanel.Children.Clear();
            OrphanListPanel.Visibility = Visibility.Collapsed;
            RemoveAllOrphansBtn.Visibility = Visibility.Collapsed;

            try
            {
                _lastOrphans = _main.GetOrphanedServices();
            }
            catch
            {
                OrphanStatusLabel.Text = Lang.T("OrphansNone");
                return;
            }

            if (_lastOrphans.Count == 0)
            {
                OrphanStatusLabel.Text = Lang.T("OrphansNone");
                OrphanStatusLabel.Foreground =
                    (System.Windows.Media.SolidColorBrush)FindResource("Success");
                return;
            }

            OrphanStatusLabel.Text = Lang.T("OrphansFound", _lastOrphans.Count);
            OrphanStatusLabel.Foreground =
                (System.Windows.Media.SolidColorBrush)FindResource("Danger");

            // Build one row per orphan
            foreach (var orphan in _lastOrphans)
                OrphanListPanel.Children.Add(BuildOrphanRow(orphan));

            OrphanListPanel.Visibility    = Visibility.Visible;
            RemoveAllOrphansBtn.Visibility = Visibility.Visible;
        }

        private System.Windows.FrameworkElement BuildOrphanRow(
            MainWindow.OrphanedService orphan)
        {
            var grid = new System.Windows.Controls.Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
                { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
                { Width = GridLength.Auto });

            var info = new System.Windows.Controls.StackPanel();
            var nameLabel = new System.Windows.Controls.TextBlock
            {
                Text       = orphan.TunnelName,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize   = 11,
                FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.SolidColorBrush)FindResource("TextPrimary")
            };
            var stateLabel = new System.Windows.Controls.TextBlock
            {
                Text       = orphan.TunnelActive
                    ? Lang.T("OrphansTunnelActive")
                    : Lang.T("OrphansTunnelStale"),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize   = 9,
                Foreground = orphan.TunnelActive ? ThemeRes.Danger : ThemeRes.TextMuted
            };
            info.Children.Add(nameLabel);
            info.Children.Add(stateLabel);
            System.Windows.Controls.Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var btn = new System.Windows.Controls.Button
            {
                Content = Lang.T("BtnRemoveOrphan"),
                Style   = (Style)FindResource("DangerBtn"),
                Padding = new Thickness(10, 4, 10, 4),
                Margin  = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = orphan
            };
            btn.Click += OrphanRemoveBtn_Click;
            System.Windows.Controls.Grid.SetColumn(btn, 1);
            grid.Children.Add(btn);

            return grid;
        }

        private void OrphanRemoveBtn_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as System.Windows.Controls.Button)?.Tag
                is not MainWindow.OrphanedService orphan) return;
            try
            {
                _main.RemoveOrphanedService(orphan);
            }
            catch (Exception ex)
            {
                OrphanStatusLabel.Text =
                    Lang.T("OrphanRemoveFailed", orphan.TunnelName, ex.Message);
            }
            ScanOrphans();  // refresh list
        }

        private void RemoveAllOrphans_Click(object sender, RoutedEventArgs e)
        {
            foreach (var orphan in _lastOrphans.ToList())
            {
                try { _main.RemoveOrphanedService(orphan); } catch { }
            }
            ScanOrphans();  // refresh list
        }

        // ── Window chrome ─────────────────────────────────────────────────────
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        // ── Theme section ─────────────────────────────────────────────────────
        private void PopulateThemePicker()
        {
            _themeSwitching = true;
            var themes = ThemeManager.AvailableThemes();

            // Show/hide section
            if (ThemePickerCard != null)
                ThemePickerCard.Visibility = themes.Count > 0
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;

            // Populate both pickers with all available themes
            PopulatePicker(DarkThemePicker,  themes, _main.GetConfig().ActiveDarkTheme);
            PopulatePicker(LightThemePicker, themes, _main.GetConfig().ActiveLightTheme);

            // Auto-switch toggle
            AutoThemeToggle.IsChecked = _main.GetConfig().AutoTheme;

            RefreshThemeInfo(DarkThemePicker,  DarkThemeInfoPanel,  DarkThemeInfoText);
            RefreshThemeInfo(LightThemePicker, LightThemeInfoPanel, LightThemeInfoText);

            _themeSwitching = false;
        }

        private static void PopulatePicker(
            System.Windows.Controls.ComboBox picker,
            List<string> themes, string selectedFolder)
        {
            picker.Items.Clear();
            ThemePickerItem? sel = null;
            foreach (var folder in themes)
            {
                var item = new ThemePickerItem(folder, ThemeManager.GetThemeDisplayName(folder));
                picker.Items.Add(item);
                if (folder == selectedFolder) sel = item;
            }
            picker.SelectedItem = sel ?? picker.Items.OfType<ThemePickerItem>().FirstOrDefault();
        }

        private static void RefreshThemeInfo(
            System.Windows.Controls.ComboBox picker,
            System.Windows.Controls.Border panel,
            System.Windows.Controls.TextBlock label)
        {
            if (picker.SelectedItem is not ThemePickerItem item)
            {
                panel.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }
            var meta = ThemeManager.GetThemeMetadata(item.FolderName);
            if (meta == null) { panel.Visibility = System.Windows.Visibility.Collapsed; return; }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(meta.Type))
                parts.Add($"{meta.Type[0..1].ToUpper()}{meta.Type[1..].ToLower()} theme");
            if (!string.IsNullOrWhiteSpace(meta.Creator))
                parts.Add($"By {ThemeManager.SanitizeInfo(meta.Creator, 60)}");
            var desc = ThemeManager.SanitizeInfo(meta.Description, 150);
            if (!string.IsNullOrWhiteSpace(desc))
                parts.Add(desc);

            if (parts.Count == 0)
            {
                panel.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }
            label.Text = string.Join("  ·  ", parts.Take(3));
            panel.Visibility = System.Windows.Visibility.Visible;
        }

        // Called from MainWindow when title-bar toggle changes
        public void RefreshThemeSection()
        {
            _themeSwitching = true;
            AutoThemeToggle.IsChecked = _main.GetConfig().AutoTheme;
            // Re-select the currently active dark/light folders
            SelectPickerItem(DarkThemePicker,  _main.GetConfig().ActiveDarkTheme);
            SelectPickerItem(LightThemePicker, _main.GetConfig().ActiveLightTheme);
            _themeSwitching = false;
            _main.UpdateThemeToggleIcon();
        }

        private static void SelectPickerItem(System.Windows.Controls.ComboBox picker, string folder)
        {
            foreach (ThemePickerItem item in picker.Items)
                if (item.FolderName == folder) { picker.SelectedItem = item; return; }
        }

        private void AutoTheme_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _themeSwitching) return;
            var cfg = _main.GetConfig();
            cfg.AutoTheme = AutoThemeToggle.IsChecked == true;

            if (cfg.AutoTheme)
            {
                // Apply the correct theme for the current system preference
                bool isDark = ThemeManager.GetSystemIsDark();
                var target = isDark ? cfg.ActiveDarkTheme : cfg.ActiveLightTheme;
                if (!string.IsNullOrEmpty(target))
                {
                    ThemeManager.Instance.Load(target);
                    cfg.ActiveTheme = target;
                }
            }
            AppConfig.SaveThemeConfig(cfg.ActiveTheme, cfg.ActiveDarkTheme,
                                      cfg.ActiveLightTheme, cfg.AutoTheme);
            _main.UpdateThemeToggleIcon();
        }

        private void DarkThemePicker_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading || _themeSwitching) return;
            if (DarkThemePicker.SelectedItem is not ThemePickerItem item) return;
            var cfg = _main.GetConfig();
            cfg.ActiveDarkTheme = item.FolderName;

            // If not auto mode, apply immediately when this is the active type
            bool currentIsDark = ThemeManager.Instance.Current.Type
                .Equals("dark", StringComparison.OrdinalIgnoreCase);
            if (!cfg.AutoTheme || ThemeManager.GetSystemIsDark())
            {
                ThemeManager.Instance.Load(item.FolderName);
                cfg.ActiveTheme = item.FolderName;
            }
            AppConfig.SaveThemeConfig(cfg.ActiveTheme, cfg.ActiveDarkTheme,
                                      cfg.ActiveLightTheme, cfg.AutoTheme);
            _main.UpdateThemeToggleIcon();
            RefreshThemeInfo(DarkThemePicker, DarkThemeInfoPanel, DarkThemeInfoText);
        }

        private void LightThemePicker_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading || _themeSwitching) return;
            if (LightThemePicker.SelectedItem is not ThemePickerItem item) return;
            var cfg = _main.GetConfig();
            cfg.ActiveLightTheme = item.FolderName;

            // Apply immediately if system is light or auto is off and current is light
            if (cfg.AutoTheme ? !ThemeManager.GetSystemIsDark() :
                ThemeManager.Instance.Current.Type.Equals("light", StringComparison.OrdinalIgnoreCase))
            {
                ThemeManager.Instance.Load(item.FolderName);
                cfg.ActiveTheme = item.FolderName;
            }
            AppConfig.SaveThemeConfig(cfg.ActiveTheme, cfg.ActiveDarkTheme,
                                      cfg.ActiveLightTheme, cfg.AutoTheme);
            _main.UpdateThemeToggleIcon();
            RefreshThemeInfo(LightThemePicker, LightThemeInfoPanel, LightThemeInfoText);
        }

        // kept for compatibility (old ThemePicker reference removed)
        private void UpdateThemeFolderLabel() { }
    }
}
