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
            LogLevelPicker.Items.Add(Lang.T("LogLevelExtended"));
            LogLevelPicker.SelectedIndex = _main.GetConfig().LogLevelSetting switch
            {
                "extended" => 1,
                _          => 0,
            };

            // ── Language ─────────────────────────────────────────────────────
            foreach (var (code, name) in Lang.AvailableLanguages())
                LanguagePicker.Items.Add(new LangItem(code, name));
            foreach (LangItem item in LanguagePicker.Items)
            {
                if (string.Equals(item.Code, Lang.Instance.CurrentCode, StringComparison.OrdinalIgnoreCase))
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
            // Deferred — applied on Save; but update the UI immediately
            bool manual = ManualModeToggle.IsChecked == true;
            if (AutomationPanel != null)
                AutomationPanel.IsEnabled = !manual;
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

        private void ExportSettings_Click(object sender, RoutedEventArgs e)
        {
            // Warn user before export
            var warn = System.Windows.MessageBox.Show(
                Lang.T("SettingsExportWarning"),
                Lang.T("SettingsExportTitle"),
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Information);
            if (warn != System.Windows.MessageBoxResult.OK) return;

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title      = Lang.T("SettingsExportTitle"),
                Filter     = "MasselGUARD settings (*.masselguard)|*.masselguard|JSON (*.json)|*.json",
                FileName   = $"MasselGUARD-settings-{DateTime.Now:yyyyMMdd}",
                DefaultExt = ".masselguard",
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var cfg = _main.GetConfig();
                var export = new
                {
                    ExportVersion    = 1,
                    AppVersion       = UpdateChecker.CurrentVersionString,
                    ExportedAt       = DateTime.UtcNow,
                    Rules            = cfg.Rules,
                    TunnelGroups     = cfg.TunnelGroups,
                    DefaultAction    = cfg.DefaultAction,
                    DefaultTunnel    = cfg.DefaultTunnel,
                    OpenWifiTunnel   = cfg.OpenWifiTunnel,
                    ManualMode       = cfg.ManualMode,
                    Mode             = cfg.Mode.ToString(),
                    Language         = cfg.Language,
                    ActiveTheme      = cfg.ActiveTheme,
                    ActiveDarkTheme  = cfg.ActiveDarkTheme,
                    ActiveLightTheme = cfg.ActiveLightTheme,
                    AutoTheme        = cfg.AutoTheme,
                    LogLevelSetting  = cfg.LogLevelSetting,
                    ShowTrayPopupOnSwitch = cfg.ShowTrayPopupOnSwitch,
                };
                var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                System.IO.File.WriteAllText(dlg.FileName,
                    System.Text.Json.JsonSerializer.Serialize(export, opts));
                _main.LogInfoPublic(Lang.T("SettingsExportSuccess", dlg.FileName));
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    Lang.T("SettingsExportError", ex.Message),
                    Lang.T("SettingsExportTitle"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        private void ImportSettings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = Lang.T("SettingsImportTitle"),
                Filter = "MasselGUARD settings (*.masselguard;*.json)|*.masselguard;*.json",
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var json = System.IO.File.ReadAllText(dlg.FileName);
                var opts = new System.Text.Json.JsonSerializerOptions
                    { PropertyNameCaseInsensitive = true };

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Version check — warn if mismatch
                var current  = UpdateChecker.CurrentVersionString;
                var fileVer  = root.TryGetProperty("AppVersion", out var verEl)
                    ? verEl.GetString() ?? "" : "";

                if (!string.IsNullOrEmpty(fileVer) && fileVer != current)
                {
                    // Compare: is the file from a newer or older version?
                    int cmp = string.Compare(fileVer, current, StringComparison.OrdinalIgnoreCase);
                    var warnKey = cmp > 0
                        ? "SettingsImportVersionNewer"
                        : "SettingsImportVersionWarning";
                    var proceed = System.Windows.MessageBox.Show(
                        Lang.T(warnKey, fileVer, current),
                        Lang.T("SettingsImportTitle"),
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Warning);
                    if (proceed != System.Windows.MessageBoxResult.Yes) return;
                }

                var cfg = _main.GetConfig();

                void TrySetString(string key, System.Action<string> setter)
                {
                    if (root.TryGetProperty(key, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String)
                        setter(el.GetString() ?? "");
                }
                void TrySetBool(string key, System.Action<bool> setter)
                {
                    if (root.TryGetProperty(key, out var el) &&
                        (el.ValueKind == System.Text.Json.JsonValueKind.True ||
                         el.ValueKind == System.Text.Json.JsonValueKind.False))
                        setter(el.GetBoolean());
                }

                TrySetString("DefaultAction",    v => cfg.DefaultAction   = v);
                TrySetString("DefaultTunnel",    v => cfg.DefaultTunnel   = v);
                TrySetString("OpenWifiTunnel",   v => cfg.OpenWifiTunnel  = v);
                TrySetString("Language",         v => cfg.Language        = v);
                TrySetString("ActiveTheme",      v => cfg.ActiveTheme     = v);
                TrySetString("ActiveDarkTheme",  v => cfg.ActiveDarkTheme = v);
                TrySetString("ActiveLightTheme", v => cfg.ActiveLightTheme= v);
                TrySetString("LogLevelSetting",  v => cfg.LogLevelSetting = v);
                TrySetBool("ManualMode",         v => cfg.ManualMode      = v);
                TrySetBool("AutoTheme",          v => cfg.AutoTheme       = v);
                TrySetBool("ShowTrayPopupOnSwitch", v => cfg.ShowTrayPopupOnSwitch = v);

                if (root.TryGetProperty("Rules", out var rulesEl))
                {
                    var rules = System.Text.Json.JsonSerializer.Deserialize<
                        System.Collections.Generic.List<TunnelRule>>(rulesEl.GetRawText(), opts);
                    if (rules != null) cfg.Rules = rules;
                }                if (root.TryGetProperty("TunnelGroups", out var grpEl))
                {
                    var groups = System.Text.Json.JsonSerializer.Deserialize<
                        System.Collections.Generic.List<TunnelGroup>>(grpEl.GetRawText(), opts);
                    if (groups != null) cfg.TunnelGroups = groups;
                }

                _main.SaveConfigPublic("Settings imported");
                _main.ApplyManualMode();
                ShowTab(_activeTab);

                System.Windows.MessageBox.Show(
                    Lang.T("SettingsImportSuccess"),
                    Lang.T("SettingsImportTitle"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    Lang.T("SettingsImportError", ex.Message),
                    Lang.T("SettingsImportTitle"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

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
                _main.LogInfoPublic($"Language: [{item.Code}] {item.Name}");
                Lang.Instance.Load(item.Code.ToLowerInvariant());
                AppConfig.SaveLanguage(item.Code.ToLowerInvariant());
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
                _main.LogInfoPublic($"App mode: {newMode}");
                _main.SetMode(newMode);
                _main.ApplyLocalTunnelModePublic();
            }

            // Manual (automation) mode
            bool newManual = ManualModeToggle.IsChecked == true;
            if (cfg.ManualMode != newManual)
            {
                _main.LogInfoPublic(newManual
                    ? "Automation: disabled (manual mode)"
                    : "Automation: enabled");
                cfg.ManualMode = newManual;
                _main.ApplyManualMode();
            }

            // Log level
            var newLogLevel = LogLevelPicker.SelectedIndex switch
            {
                1 => "extended",
                _ => "normal",
            };
            if (cfg.LogLevelSetting != newLogLevel)
                _main.LogInfoPublic($"Log level changed: {newLogLevel}");
            cfg.LogLevelSetting = newLogLevel;

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
            string tab = btn.Name switch
            {
                "TabBtnAppearance"    => "Appearance",
                "TabBtnRules"         => "Rules",
                "TabBtnDefaultAction" => "DefaultAction",
                "TabBtnAdvanced"      => "Advanced",
                "TabBtnAbout"         => "About",
                _                     => "General",
            };
            ShowTab(tab);
        }

        private void ShowTab(string tab)
        {
            _activeTab = tab;
            PageGeneral.Visibility       = tab == "General"       ? Visibility.Visible : Visibility.Collapsed;
            PageAppearance.Visibility    = tab == "Appearance"    ? Visibility.Visible : Visibility.Collapsed;
            PageRules.Visibility         = tab == "Rules"         ? Visibility.Visible : Visibility.Collapsed;
            PageDefaultAction.Visibility = tab == "DefaultAction" ? Visibility.Visible : Visibility.Collapsed;
            PageAdvanced.Visibility      = tab == "Advanced"      ? Visibility.Visible : Visibility.Collapsed;
            PageAbout.Visibility         = tab == "About"         ? Visibility.Visible : Visibility.Collapsed;

            // Highlight active sidebar button
            TabBtnGeneral.Tag       = tab == "General"       ? "Active" : null;
            TabBtnAppearance.Tag    = tab == "Appearance"    ? "Active" : null;
            TabBtnRules.Tag         = tab == "Rules"         ? "Active" : null;
            TabBtnDefaultAction.Tag = tab == "DefaultAction" ? "Active" : null;
            TabBtnAdvanced.Tag      = tab == "Advanced"      ? "Active" : null;
            TabBtnAbout.Tag         = tab == "About"         ? "Active" : null;

            if (tab == "Advanced")      { RefreshInstallState(); RefreshDllStatus(); RefreshWireGuardSection(); ScanOrphans(); }
            if (tab == "About")         { RefreshUpdateState(); }
            if (tab == "Appearance")    { PopulateThemePicker(); }
            if (tab == "General")       { RefreshGroupList(); }
            if (tab == "Rules")         { RefreshAutomationControls(); }
            if (tab == "DefaultAction") { RefreshAutomationControls(); }
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

        // ── Tunnel group manager ──────────────────────────────────────────────
        private void RefreshGroupList()
        {
            GroupListPanel.Items.Clear();
            var groups = _main.GetConfig().TunnelGroups;
            for (int i = 0; i < groups.Count; i++)
            {
                int idx = i;
                var group = groups[i];

                var nameBox = new System.Windows.Controls.TextBox
                {
                    Text       = group.Name,
                    FontFamily = (System.Windows.Media.FontFamily)FindResource("Theme.FontFamily"),
                    FontSize   = 11,
                    Padding    = new Thickness(6, 3, 6, 3),
                    MinWidth   = 120,
                };
                nameBox.LostFocus += (_, _) =>
                {
                    var newName = nameBox.Text.Trim();
                    if (string.IsNullOrEmpty(newName) || newName == group.Name) return;
                    // Also update any tunnels referencing the old group name
                    foreach (var t in _main.GetConfig().Tunnels)
                        if (string.Equals(t.Group, group.Name, StringComparison.OrdinalIgnoreCase))
                            t.Group = newName;
                    group.Name = newName;
                    _main.SaveConfigPublic();
                    _main.RefreshTunnelDropdownsPublic();
                };

                var upBtn = new System.Windows.Controls.Button
                {
                    Content  = "↑", FontSize = 10, Padding = new Thickness(6, 2, 6, 2),
                    Margin   = new Thickness(4, 0, 0, 0),
                    IsEnabled = idx > 0,
                    Style    = (Style)FindResource("FlatBtn"),
                };
                upBtn.Click += (_, _) =>
                {
                    if (idx <= 0) return;
                    groups.RemoveAt(idx);
                    groups.Insert(idx - 1, group);
                    _main.SaveConfigPublic();
                    _main.RefreshTunnelDropdownsPublic();
                    RefreshGroupList();
                };

                var downBtn = new System.Windows.Controls.Button
                {
                    Content  = "↓", FontSize = 10, Padding = new Thickness(6, 2, 6, 2),
                    Margin   = new Thickness(2, 0, 0, 0),
                    IsEnabled = idx < groups.Count - 1,
                    Style    = (Style)FindResource("FlatBtn"),
                };
                downBtn.Click += (_, _) =>
                {
                    if (idx >= groups.Count - 1) return;
                    groups.RemoveAt(idx);
                    groups.Insert(idx + 1, group);
                    _main.SaveConfigPublic();
                    _main.RefreshTunnelDropdownsPublic();
                    RefreshGroupList();
                };

                var delBtn = new System.Windows.Controls.Button
                {
                    Content  = "✕", FontSize = 10, Padding = new Thickness(6, 2, 6, 2),
                    Margin   = new Thickness(2, 0, 0, 0),
                    Style    = (Style)FindResource("DangerBtn"),
                };
                delBtn.Click += (_, _) =>
                {
                    // Move tunnels in this group to ungrouped
                    foreach (var t in _main.GetConfig().Tunnels)
                        if (string.Equals(t.Group, group.Name, StringComparison.OrdinalIgnoreCase))
                            t.Group = "";
                    groups.Remove(group);
                    _main.SaveConfigPublic();
                    _main.RefreshTunnelDropdownsPublic();
                    RefreshGroupList();
                };

                var row = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    Margin      = new Thickness(0, 0, 0, 4),
                };
                row.Children.Add(nameBox);
                row.Children.Add(upBtn);
                row.Children.Add(downBtn);
                row.Children.Add(delBtn);
                GroupListPanel.Items.Add(row);
            }
        }

        private void AddGroup_Click(object sender, RoutedEventArgs e)
        {
            var name = NewGroupNameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) return;
            var groups = _main.GetConfig().TunnelGroups;
            if (groups.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
                return; // duplicate
            groups.Add(new TunnelGroup(name));
            NewGroupNameBox.Text = "";
            _main.SaveConfigPublic();
            _main.RefreshTunnelDropdownsPublic();
            RefreshGroupList();
        }

        // ── Theme section ─────────────────────────────────────────────────────
        private void PopulateThemePicker()
        {
            _themeSwitching = true;
            var allThemes = ThemeManager.AvailableThemes();

            // Show/hide section
            if (ThemePickerCard != null)
                ThemePickerCard.Visibility = allThemes.Count > 0
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;

            // Populate pickers filtered by theme type
            var darkThemes  = allThemes.Where(f =>
            {
                var m = ThemeManager.GetThemeMetadata(f);
                return m == null || m.Type.Equals("dark", StringComparison.OrdinalIgnoreCase);
            }).ToList();

            var lightThemes = allThemes.Where(f =>
            {
                var m = ThemeManager.GetThemeMetadata(f);
                return m != null && m.Type.Equals("light", StringComparison.OrdinalIgnoreCase);
            }).ToList();

            PopulatePicker(DarkThemePicker,  darkThemes,  _main.GetConfig().ActiveDarkTheme);
            PopulatePicker(LightThemePicker, lightThemes, _main.GetConfig().ActiveLightTheme);

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

            if (!cfg.AutoTheme || ThemeManager.GetSystemIsDark())
            {
                ThemeManager.Instance.Load(item.FolderName);
                cfg.ActiveTheme = item.FolderName;
            }
            _main.LogInfoPublic($"Theme (dark): {item.DisplayName}");
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

            if (cfg.AutoTheme ? !ThemeManager.GetSystemIsDark() :
                ThemeManager.Instance.Current.Type.Equals("light", StringComparison.OrdinalIgnoreCase))
            {
                ThemeManager.Instance.Load(item.FolderName);
                cfg.ActiveTheme = item.FolderName;
            }
            _main.LogInfoPublic($"Theme (light): {item.DisplayName}");
            AppConfig.SaveThemeConfig(cfg.ActiveTheme, cfg.ActiveDarkTheme,
                                      cfg.ActiveLightTheme, cfg.AutoTheme);
            _main.UpdateThemeToggleIcon();
            RefreshThemeInfo(LightThemePicker, LightThemeInfoPanel, LightThemeInfoText);
        }

        // ── Rules / Default Action / Open Network ─────────────────────────────
        private void RefreshAutomationControls()
        {
            var cfg = _main.GetConfig();

            // Disable automation content when manual mode is on
            if (AutomationPanel != null)
                AutomationPanel.IsEnabled = !cfg.ManualMode;
            if (ManualModeToggle != null)
                ManualModeToggle.IsChecked = cfg.ManualMode;

            // Rules list
            if (RulesListView != null)
                RulesListView.ItemsSource = _main.GetRules();

            // Default action radio buttons
            _loading = true;
            if (ActionNone     != null) ActionNone.IsChecked     = cfg.DefaultAction == "none"       || string.IsNullOrEmpty(cfg.DefaultAction);
            if (ActionDiscon   != null) ActionDiscon.IsChecked   = cfg.DefaultAction == "disconnect";
            if (ActionActivate != null) ActionActivate.IsChecked = cfg.DefaultAction == "activate";
            _loading = false;

            // Default tunnel combobox
            var tunnels = _main.GetTunnelNames();
            if (DefaultTunnelBox != null)
            {
                _loading = true;
                DefaultTunnelBox.Items.Clear();
                foreach (var t in tunnels) DefaultTunnelBox.Items.Add(t);
                DefaultTunnelBox.Text = cfg.DefaultTunnel ?? "";
                _loading = false;
            }

            // Open wifi combobox
            _loading = true;
            if (OpenWifiTunnelBox != null)
            {
                OpenWifiTunnelBox.Items.Clear();
                OpenWifiTunnelBox.Items.Add(Lang.T("OpenWifiNone"));
                foreach (var t in tunnels) OpenWifiTunnelBox.Items.Add(t);
                var match = tunnels.FirstOrDefault(t =>
                    string.Equals(t, cfg.OpenWifiTunnel, StringComparison.OrdinalIgnoreCase));
                OpenWifiTunnelBox.SelectedItem = (object?)match ?? Lang.T("OpenWifiNone");
            }
            _loading = false;
        }
        private void SaveRules_Click(object sender, RoutedEventArgs e)
        {
            _main.SaveConfigPublic("WiFi rules saved");
        }
        private void DefaultAction_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var cfg = _main.GetConfig();
            if      (ActionNone.IsChecked   == true) cfg.DefaultAction = "none";
            else if (ActionDiscon.IsChecked == true) cfg.DefaultAction = "disconnect";
            else                                     cfg.DefaultAction = "activate";
            _main.SaveConfigPublic($"Default action: {cfg.DefaultAction}");
        }

        private void DefaultTunnelBox_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var cfg = _main.GetConfig();
            if (DefaultTunnelBox.SelectedItem is string t)
            {
                cfg.DefaultTunnel = t;
                cfg.DefaultAction = "activate";
                _loading = true;
                ActionActivate.IsChecked = true;
                _loading = false;
                _main.SaveConfigPublic($"Default tunnel: {t}");
            }
        }

        private void DefaultTunnelBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var cfg  = _main.GetConfig();
            var text = DefaultTunnelBox.Text.Trim();
            if (!string.IsNullOrEmpty(text) && text != cfg.DefaultTunnel)
            {
                cfg.DefaultTunnel = text;
                cfg.DefaultAction = "activate";
                _loading = true;
                ActionActivate.IsChecked = true;
                _loading = false;
                _main.SaveConfigPublic($"Default tunnel: {text}");
            }
        }

        private void OpenWifiTunnel_Changed(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var cfg  = _main.GetConfig();
            var none = Lang.T("OpenWifiNone");
            cfg.OpenWifiTunnel = OpenWifiTunnelBox.SelectedItem as string == none
                ? "" : OpenWifiTunnelBox.SelectedItem as string ?? "";
            _main.SaveConfigPublic($"Open network tunnel: {(string.IsNullOrEmpty(cfg.OpenWifiTunnel) ? "none" : cfg.OpenWifiTunnel)}");
        }

        private void AddRule_Click(object sender, RoutedEventArgs e)
        {
            _main.Dispatcher.Invoke(() => _main.AddRulePublic());
            RefreshAutomationControls();
        }

        private void EditRule_Click(object sender, RoutedEventArgs e)
        {
            if (RulesListView.SelectedItem is not TunnelRule rule) return;
            _main.Dispatcher.Invoke(() => _main.EditRulePublic(rule));
            RefreshAutomationControls();
        }

        private void DeleteRule_Click(object sender, RoutedEventArgs e)
        {
            if (RulesListView.SelectedItem is not TunnelRule rule) return;
            _main.Dispatcher.Invoke(() => _main.DeleteRulePublic(rule));
            RefreshAutomationControls();
        }

        private void RulesListView_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            bool sel = RulesListView.SelectedItem != null;
            if (EditBtn   != null) EditBtn.IsEnabled   = sel;
            if (DeleteBtn != null) DeleteBtn.IsEnabled = sel;
        }
    }
}
