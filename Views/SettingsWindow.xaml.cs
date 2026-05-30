using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MasselGUARD.Infrastructure;
using MasselGUARD.Models;
using MasselGUARD.Services;
using MasselGUARD.ViewModels;

namespace MasselGUARD.Views
{
    public partial class SettingsWindow : Window
    {
        private readonly MainWindow       _main;
        private readonly SettingsViewModel _vm;
        private string _activeTab = "General";
        private bool   _loading   = true;
        private bool   _themeSwitching = false;

        // _originalTheme removed — CancelThemePreview/CancelFontPreview/OnClosing
        // now call _main.ApplyThemeFromConfig() which reads the committed config and
        // correctly handles both system colours and custom theme files.
        private Models.AppConfig _draft = new(); // staged copy — only written to config on Save

        public SettingsWindow(MainWindow main)
        {
            _main = main;
            _vm   = new SettingsViewModel(main.ConfigSvc, main.LogSvc);

            // Wire ViewModel dialog requests
            _vm.AddRuleRequested    += OnAddRule;
            _vm.EditRuleRequested   += OnEditRule;
            _vm.ExportRequested     += OnExportSettings;
            _vm.ImportRequested     += OnImportSettings;
            _vm.ModeChanged         += _ => { _main.ApplyManualMode(); RefreshCurrentTab(); };
            _vm.LogLevelChanged     += v => main.LogSvc.IsExtended = v == "extended";

            InitializeComponent();
            DataContext = _vm;

            Loaded += (_, _) =>
            {
                _loading = false;
                // Create a deep copy of the LIVE config — all edits go here until Save is pressed
                _draft = _main.ConfigSvc.Config.DeepClone();
                ShowTab("General");
                RefreshUpdateState();
            };
        }

        // ── Tab routing ───────────────────────────────────────────────────────
        private void TabBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string tab = btn.Name switch
            {
                "TabBtnGroups"        => "Groups",
                "TabBtnAppearance"    => "Appearance",
                "TabBtnDefaultAction" => "DefaultAction",
                "TabBtnRules"         => "Rules",
                "TabBtnAdvanced"      => "Advanced",
                "TabBtnAbout"         => "About",
                _                     => "General",
            };
            ShowTab(tab);
        }

        public void ShowTab(string tab)
        {
            _activeTab = tab;

            PageGeneral.Visibility       = tab == "General"       ? Visibility.Visible : Visibility.Collapsed;
            PageGroups.Visibility        = tab == "Groups"        ? Visibility.Visible : Visibility.Collapsed;
            PageAppearance.Visibility    = tab == "Appearance"    ? Visibility.Visible : Visibility.Collapsed;
            PageDefaultAction.Visibility = tab == "DefaultAction" ? Visibility.Visible : Visibility.Collapsed;
            PageRules.Visibility         = tab == "Rules"         ? Visibility.Visible : Visibility.Collapsed;
            PageAdvanced.Visibility      = tab == "Advanced"      ? Visibility.Visible : Visibility.Collapsed;
            PageAbout.Visibility         = tab == "About"         ? Visibility.Visible : Visibility.Collapsed;

            TabBtnGeneral.Tag       = tab == "General"       ? "Active" : null;
            TabBtnGroups.Tag        = tab == "Groups"        ? "Active" : null;
            TabBtnAppearance.Tag    = tab == "Appearance"    ? "Active" : null;
            TabBtnDefaultAction.Tag = tab == "DefaultAction" ? "Active" : null;
            TabBtnRules.Tag         = tab == "Rules"         ? "Active" : null;
            TabBtnAdvanced.Tag      = tab == "Advanced"      ? "Active" : null;
            TabBtnAbout.Tag         = tab == "About"         ? "Active" : null;

            if (tab == "Advanced")   { RefreshInstallState(); RefreshDllStatus(); RefreshWireGuardSection(); ScanOrphans(); PopulateLogLevelPicker(); SyncStartWithWindows(); SyncConfirmOnClose(); }
            if (tab == "About")      RefreshUpdateState();
            if (tab == "Appearance") PopulateThemePicker();
            if (tab == "General")    { RefreshGroupList(); RefreshModeStatusBox(); }
            if (tab == "Groups")     RefreshGroupList();
            if (tab == "Rules" || tab == "DefaultAction") RefreshAutomationControls();
        }

        private void RefreshCurrentTab() => ShowTab(_activeTab);

        // ── General tab ───────────────────────────────────────────────────────
        private void LanguagePicker_SelectionChanged(object sender,
            SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (LanguagePicker.SelectedItem is LangItem item)
            {
                Lang.Instance.Load(item.Code);
                _vm.Language = item.Code;
            }
        }

        private void AppMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (ModeStandalone?.IsChecked == true) _vm.Mode = AppMode.Standalone;
            else if (ModeCompanion?.IsChecked == true) _vm.Mode = AppMode.Companion;
            else _vm.Mode = AppMode.Mixed;
            RefreshModeStatusBox();
        }

        private void RefreshModeStatusBox()
        {
            if (DllStatusLabel == null) return;
            var mode    = _vm.Mode;
            var baseDir = AppContext.BaseDirectory;
            var lines   = new System.Text.StringBuilder();

            // ── DLLs (Standalone / Mixed) ─────────────────────────────────────
            if (mode == AppMode.Standalone || mode == AppMode.Mixed)
            {
                var tunnelPath = System.IO.Path.Combine(baseDir, "tunnel.dll");
                var wgPath     = System.IO.Path.Combine(baseDir, "wireguard.dll");

                bool tunnelOk  = System.IO.File.Exists(tunnelPath);
                bool wgOk      = System.IO.File.Exists(wgPath);

                lines.AppendLine(tunnelOk
                    ? $"✓  tunnel.dll      ({new System.IO.FileInfo(tunnelPath).Length / 1024} KB)"
                    : "✗  tunnel.dll      — not found");
                lines.AppendLine(wgOk
                    ? $"✓  wireguard.dll  ({new System.IO.FileInfo(wgPath).Length / 1024} KB)"
                    : "✗  wireguard.dll  — not found");
            }

            // ── WireGuard for Windows (Companion / Mixed) ─────────────────────
            if (mode == AppMode.Companion || mode == AppMode.Mixed)
            {
                var wgInstall = MainWindow.DetectWireGuardInstallDir();
                if (wgInstall != null)
                    lines.AppendLine($"✓  WireGuard for Windows  ({wgInstall})");
                else
                    lines.AppendLine("✗  WireGuard for Windows  — not found");
            }

            DllStatusLabel.Text = lines.ToString().TrimEnd();
        }

        private void RefreshGroupList()
        {
            if (_loading) return;

            // ── General tab controls ──────────────────────────────────────────
            if (LanguagePicker != null && LanguagePicker.Items.Count == 0)
            {
                foreach (var (code, name) in Lang.AvailableLanguages())
                    LanguagePicker.Items.Add(new LangItem(code, name));
            }
            if (LanguagePicker != null)
                LanguagePicker.SelectedItem = LanguagePicker.Items
                    .OfType<LangItem>()
                    .FirstOrDefault(i => string.Equals(i.Code,
                        _draft.Language, StringComparison.OrdinalIgnoreCase));

            // Sync app mode radios (General tab)
            _loading = true;
            var mode = _draft.Mode;
            if (ModeStandalone != null) ModeStandalone.IsChecked = mode == AppMode.Standalone;
            if (ModeCompanion  != null) ModeCompanion.IsChecked  = mode == AppMode.Companion;
            if (ModeMixed      != null) ModeMixed.IsChecked      = mode == AppMode.Mixed;
            if (ShowActivityLogToggle != null) ShowActivityLogToggle.IsChecked = _draft.ShowActivityLog;
            _loading = false;

            // ── Groups tab controls ───────────────────────────────────────────
            _loading = true;
            if (HideTunnelCountToggle  != null) HideTunnelCountToggle.IsChecked  = _draft.AlwaysHideTunnelCount;
            if (HideEmptyGroupsToggle  != null) HideEmptyGroupsToggle.IsChecked  = _draft.HideEmptyGroups;
            _loading = false;

            // ── Group list (used by both General and Groups tab) ──────────────
            GroupListPanel.Items.Clear();
            var groups = _draft.TunnelGroups;
            var hidden = _draft.HiddenTabs;

            // Theme colour presets shown in the colour picker
            var themePresets = new[]
            {
                ("", "—"),               // no colour / transparent
                ("Accent",   "Accent"),
                ("Success",  "Success"),
                ("Danger",   "Danger"),
                ("Surface",  "Surface"),
                ("#1E3A5F",  "#1E3A5F"),
                ("#2D4A1E",  "#2D4A1E"),
                ("#4A1E1E",  "#4A1E1E"),
                ("#2D1E4A",  "#2D1E4A"),
                ("#1E4A4A",  "#1E4A4A"),
            };

            // ── Helper: build one group row ────────────────────────────────────
            void AddGroupRow(string displayName, string groupKey,
                bool canDelete, bool canRename, bool canReorder,
                string currentColor, int? listIdx)
            {
                bool isHidden = hidden.Contains(groupKey);
                var row = new System.Windows.Controls.WrapPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    Margin      = new Thickness(0, 0, 0, 4),
                };

                // Eye toggle (hide/show)
                var eyeBtn = new Button
                {
                    Content   = isHidden ? "👁‍🗨" : "👁",
                    Style     = (Style)FindResource("FlatBtn"),
                    FontSize  = 11,
                    Padding   = new Thickness(4, 2, 4, 2),
                    Margin    = new Thickness(0, 0, 4, 0),
                    ToolTip   = isHidden ? "Show tab" : "Hide tab",
                    Opacity   = isHidden ? 0.4 : 1.0,
                };
                eyeBtn.Click += (_, _) =>
                {
                    if (hidden.Contains(groupKey)) hidden.Remove(groupKey);
                    else hidden.Add(groupKey);
                    RefreshGroupList();
                };
                row.Children.Add(eyeBtn);

                // Default star — marks which group opens on startup
                var isDefault = _draft.DefaultGroup == groupKey;
                var starBtn   = new Button
                {
                    Content   = isDefault ? "⭐" : "☆",
                    Style     = (Style)FindResource("FlatBtn"),
                    FontSize  = 11,
                    Padding   = new Thickness(4, 2, 4, 2),
                    Margin    = new Thickness(0, 0, 4, 0),
                    ToolTip   = isDefault ? "This group opens on startup" : "Set as default on startup",
                    Opacity   = isDefault ? 1.0 : 0.5,
                };
                starBtn.Click += (_, _) =>
                {
                    _draft.DefaultGroup =
                        _draft.DefaultGroup == groupKey ? "" : groupKey;
                    RefreshGroupList();
                };
                row.Children.Add(starBtn);

                // Name field (read-only for All/Uncategorized)
                var nameBox = new TextBox
                {
                    Text            = displayName,
                    Width           = 160,
                    IsReadOnly      = !canRename,
                    FontFamily      = (System.Windows.Media.FontFamily)FindResource("Theme.FontFamily"),
                    FontSize        = 11,
                    Padding         = new Thickness(6, 3, 6, 3),
                    Background      = (System.Windows.Media.Brush)FindResource("CardBg"),
                    Foreground      = (System.Windows.Media.Brush)FindResource(canRename ? "TextPrimary" : "TextMuted"),
                    BorderBrush     = (System.Windows.Media.Brush)FindResource("BorderColor"),
                    BorderThickness = new Thickness(1),
                    Opacity         = canRename ? 1.0 : 0.7,
                };
                if (canRename && listIdx.HasValue)
                {
                    int idx = listIdx.Value;
                    nameBox.LostFocus += (_, _) =>
                    {
                        var text = nameBox.Text.Trim();
                        if (!string.IsNullOrEmpty(text) && text != groups[idx].Name)
                        {
                            // Update hidden key if renamed
                            if (hidden.Contains(groups[idx].Name))
                            {
                                hidden.Remove(groups[idx].Name);
                                hidden.Add(text);
                            }
                            groups[idx].Name = text;
                        }
                    };
                }
                row.Children.Add(nameBox);

                // Colour picker
                var colPicker = new System.Windows.Controls.ComboBox
                {
                    Width   = 80,
                    Margin  = new Thickness(4, 0, 0, 0),
                    Padding = new Thickness(4, 2, 4, 2),
                    FontSize= 10,
                    Background      = (System.Windows.Media.Brush)FindResource("CardBg"),
                    Foreground      = (System.Windows.Media.Brush)FindResource("TextPrimary"),
                    BorderBrush     = (System.Windows.Media.Brush)FindResource("BorderColor"),
                };
                foreach (var (hex, label) in themePresets)
                {
                    var item = new System.Windows.Controls.ComboBoxItem
                    {
                        Tag     = hex,
                        Content = label,
                    };
                    if (!string.IsNullOrEmpty(hex))
                    {
                        try
                        {
                            System.Windows.Media.Brush swatch;
                            if (hex.StartsWith("#"))
                                swatch = new System.Windows.Media.SolidColorBrush(
                                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
                            else
                                swatch = (System.Windows.Media.Brush)FindResource(hex);

                            item.Background = swatch;
                            // Set label to white or black based on luminance
                            item.Foreground = GetContrastBrush(swatch);
                        }
                        catch { }
                    }
                    colPicker.Items.Add(item);
                }
                // Pre-select current colour
                foreach (System.Windows.Controls.ComboBoxItem ci in colPicker.Items)
                    if ((string)ci.Tag == (currentColor ?? "")) { colPicker.SelectedItem = ci; break; }
                if (colPicker.SelectedItem == null) colPicker.SelectedIndex = 0;

                if (listIdx.HasValue)
                {
                    int idx = listIdx.Value;
                    colPicker.SelectionChanged += (_, _) =>
                    {
                        if (colPicker.SelectedItem is System.Windows.Controls.ComboBoxItem ci)
                        {
                            groups[idx].Color = (string)ci.Tag;
                            _main._vm.RebuildTunnelList();
                        }
                    };
                }
                row.Children.Add(colPicker);

                // Reorder buttons (only for custom groups)
                if (canReorder && listIdx.HasValue)
                {
                    int idx = listIdx.Value;
                    void MakeBtn(string label, Action click)
                    {
                        var b = new Button
                        {
                            Content = label, Style=(Style)FindResource("FlatBtn"),
                            FontSize=11, Padding=new Thickness(5,2,5,2), Margin=new Thickness(4,0,0,0),
                        };
                        b.Click += (_,_) => click();
                        row.Children.Add(b);
                    }
                    if (idx > 0)
                        MakeBtn("↑", () => { var t=groups[idx]; groups.RemoveAt(idx); groups.Insert(idx-1,t); RefreshGroupList(); });
                    if (idx < groups.Count - 1)
                        MakeBtn("↓", () => { var t=groups[idx]; groups.RemoveAt(idx); groups.Insert(idx+1,t); RefreshGroupList(); });
                    if (canDelete)
                        MakeBtn("✕", () => { groups.RemoveAt(idx); hidden.Remove(groupKey); RefreshGroupList(); });
                }

                GroupListPanel.Items.Add(row);
            }

            // ── Custom groups ─────────────────────────────────────────────────
            for (int i = 0; i < groups.Count; i++)
                AddGroupRow(groups[i].Name, groups[i].Name,
                    canDelete: true, canRename: true, canReorder: true,
                    currentColor: groups[i].Color, listIdx: i);

            // ── Uncategorized (built-in, non-deletable) ───────────────────────
            AddGroupRow(Lang.T("TabUncategorized"), "Uncategorized",
                canDelete: false, canRename: false, canReorder: false,
                currentColor: "", listIdx: null);
        }

        private void ShowRulesColumn_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.ShowTunnelRulesColumn =
                ShowRulesColumnToggle?.IsChecked == true;
        }

        private void ShowActivityLog_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.ShowActivityLog = ShowActivityLogToggle?.IsChecked == true;
            _main.SetLogPanelVisible(_draft.ShowActivityLog);
        }

        private void HideWifiRules_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.ShowWifiRulesOnMainWindow =
                !(HideWifiRulesToggle?.IsChecked == true);
            _main.RefreshWifiRulesPanel();
        }

        private void HideEmptyGroups_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.HideEmptyGroups =
                HideEmptyGroupsToggle?.IsChecked == true;
        }

        private void HideTunnelCount_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.AlwaysHideTunnelCount =
                HideTunnelCountToggle?.IsChecked == true;   // rebuilds tabs + calls UpdateHiddenCountBadge
        }

        private void AddGroup_Click(object sender, RoutedEventArgs e)
        {
            var name = NewGroupNameBox?.Text.Trim() ?? "";
            if (string.IsNullOrEmpty(name)) name = Lang.T("DefaultGroupName");
            if (NewGroupNameBox != null) NewGroupNameBox.Text = "";
            var newGroup = new TunnelGroup(name);
            // Add to both config AND the VM's staged collection so DoSave preserves it
            _draft.TunnelGroups.Add(newGroup);
            _vm.TunnelGroups.Add(newGroup);
            RefreshGroupList();
        }

        private static System.Windows.Media.Brush GetContrastBrush(
            System.Windows.Media.Brush bg)
        {
            // Compute luminance of the brush's dominant colour
            if (bg is System.Windows.Media.SolidColorBrush scb)
            {
                var c = scb.Color;
                double lum = 0.2126 * (c.R / 255.0)
                           + 0.7152 * (c.G / 255.0)
                           + 0.0722 * (c.B / 255.0);
                return lum > 0.45
                    ? System.Windows.Media.Brushes.Black
                    : System.Windows.Media.Brushes.White;
            }
            return System.Windows.Media.Brushes.White;
        }

        // ── Appearance tab ────────────────────────────────────────────────────
        private void PopulateThemePicker()
        {
            _themeSwitching = true;

            // Exclude virtual/built-in entries from the custom pickers:
            //   __system__   — hardcoded Windows palette (used when UseCustomTheme=false)
            //   windows-dark / windows-light — same palette shipped as theme files; not user-selectable
            var allThemes = ThemeManager.AvailableThemes()
                .Where(f => f != "__system__" && f != "windows-dark" && f != "windows-light")
                .ToList();

            string GetType(string folder) {
                try {
                    var def = System.Text.Json.JsonSerializer.Deserialize<ThemeDefinition>(
                        System.IO.File.ReadAllText(System.IO.Path.Combine(
                            AppContext.BaseDirectory, "theme", folder, "theme.json")),
                        new System.Text.Json.JsonSerializerOptions{PropertyNameCaseInsensitive=true});
                    return def?.Type ?? "dark";
                } catch { return "dark"; }
            }

            var dark  = allThemes.Where(f => GetType(f) == "dark").ToList();
            var light = allThemes.Where(f => GetType(f) == "light").ToList();

            DarkThemePicker.Items.Clear();
            foreach (var f in dark)
                DarkThemePicker.Items.Add(new ThemePickerItem(f, ThemeManager.GetThemeDisplayName(f)));
            DarkThemePicker.SelectedItem = DarkThemePicker.Items
                .OfType<ThemePickerItem>()
                .FirstOrDefault(i => i.FolderName == _draft.ActiveDarkTheme);
            if (DarkThemePicker.SelectedItem == null && DarkThemePicker.Items.Count > 0)
                DarkThemePicker.SelectedIndex = 0;

            LightThemePicker.Items.Clear();
            foreach (var f in light)
                LightThemePicker.Items.Add(new ThemePickerItem(f, ThemeManager.GetThemeDisplayName(f)));
            LightThemePicker.SelectedItem = LightThemePicker.Items
                .OfType<ThemePickerItem>()
                .FirstOrDefault(i => i.FolderName == _draft.ActiveLightTheme);
            if (LightThemePicker.SelectedItem == null && LightThemePicker.Items.Count > 0)
                LightThemePicker.SelectedIndex = 0;

            // System mode pills
            _loading = true;
            var sysMode = _draft.SystemThemeMode ?? "auto";
            if (SysModeLight != null) SysModeLight.IsChecked = sysMode == "light";
            if (SysModeDark  != null) SysModeDark.IsChecked  = sysMode == "dark";
            if (SysModeAuto  != null) SysModeAuto.IsChecked  = sysMode != "light" && sysMode != "dark";
            _loading = false;

            // Custom theme toggle + pickers panel visibility
            if (CustomThemeToggle != null)
            {
                _loading = true;
                CustomThemeToggle.IsChecked = _draft.UseCustomTheme;
                _loading = false;
            }
            if (CustomThemePickersPanel != null)
                CustomThemePickersPanel.Visibility =
                    _draft.UseCustomTheme ? Visibility.Visible : Visibility.Collapsed;

            // Tray popup toggle
            if (TrayPopupToggle != null)
            {
                _loading = true;
                TrayPopupToggle.IsChecked = _draft.ShowTrayPopupOnSwitch;
                _loading = false;
            }

            // Notification duration picker
            if (NotifDurationPicker != null)
            {
                _loading = true;
                NotifDurationPicker.Items.Clear();
                foreach (var s in new[] { 3, 5, 10, 15, 30 })
                    NotifDurationPicker.Items.Add(new System.Windows.Controls.ComboBoxItem
                        { Content = $"{s}s", Tag = s });
                int cur = _draft.NotificationDurationSeconds;
                NotifDurationPicker.SelectedItem = NotifDurationPicker.Items
                    .OfType<System.Windows.Controls.ComboBoxItem>()
                    .FirstOrDefault(i => (int)i.Tag == cur)
                    ?? NotifDurationPicker.Items[1];
                _loading = false;
            }

            // Font override sync (PopulateFontPicker manages its own _loading guard)
            PopulateFontPicker();

            _themeSwitching = false;
        }

        private void DarkThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _themeSwitching) return;
            if (DarkThemePicker.SelectedItem is ThemePickerItem item)
            {
                _draft.ActiveDarkTheme = item.FolderName;
                _vm.ActiveDarkTheme    = item.FolderName;
                // Changing selection cancels any running preview so the user sees
                // the previous (committed) look before clicking Preview again.
                if (_themePreviewActive) CancelThemePreview();
            }
        }

        private void LightThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _themeSwitching) return;
            if (LightThemePicker.SelectedItem is ThemePickerItem item)
            {
                _draft.ActiveLightTheme = item.FolderName;
                _vm.ActiveLightTheme    = item.FolderName;
                if (_themePreviewActive) CancelThemePreview();
            }
        }

        /// <summary>Returns true when the current draft SystemThemeMode resolves to dark.</summary>
        private bool IsDraftDark() => (_draft.SystemThemeMode ?? "auto") switch
        {
            "light" => false,
            "dark"  => true,
            _       => ThemeManager.GetSystemIsDark()
        };

        private void SystemMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _themeSwitching) return;
            if (sender is not RadioButton rb || rb.Tag is not string tag) return;
            _draft.SystemThemeMode = tag;
            // No live apply — user clicks a preview button to see the result.
            if (_themePreviewActive) CancelThemePreview();
        }

        private void CustomTheme_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _themeSwitching) return;
            bool on = CustomThemeToggle?.IsChecked == true;
            _draft.UseCustomTheme = on;
            if (CustomThemePickersPanel != null)
                CustomThemePickersPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            // No live apply — user clicks a preview button to see the result.
            if (_themePreviewActive) CancelThemePreview();
        }

        // ── Theme live-preview ────────────────────────────────────────────────
        private Button? _themePreviewSourceBtn = null;   // which button is counting down

        private void DarkThemePreview_Click(object sender, RoutedEventArgs e)
            => StartThemePreview(forceLight: false, DarkThemePreviewBtn);

        private void LightThemePreview_Click(object sender, RoutedEventArgs e)
            => StartThemePreview(forceLight: true, LightThemePreviewBtn);

        private void StartThemePreview(bool forceLight, Button sourceBtn)
        {
            // Clicking the already-active button cancels it.
            if (_themePreviewActive && _themePreviewSourceBtn == sourceBtn)
            { CancelThemePreview(); return; }

            // Clicking the other button while one is running: cancel first, then start.
            if (_themePreviewActive) CancelThemePreview();

            ApplySpecificTheme(forceLight);

            _themePreviewActive      = true;
            _themePreviewSecondsLeft = 10;
            _themePreviewSourceBtn   = sourceBtn;
            UpdateThemePreviewBtn();

            _themePreviewTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _themePreviewTimer.Tick += (_, _) =>
            {
                _themePreviewSecondsLeft--;
                if (_themePreviewSecondsLeft <= 0)
                    CancelThemePreview();
                else
                    UpdateThemePreviewBtn();
            };
            _themePreviewTimer.Start();
        }

        /// <summary>
        /// Loads the draft dark or light theme file directly — regardless of the current system mode.
        /// Lets the user preview the light theme even while Windows is in dark mode and vice versa.
        /// </summary>
        private void ApplySpecificTheme(bool forceLight)
        {
            if (!_draft.UseCustomTheme)
            {
                ThemeManager.Instance.LoadSystem(!forceLight);  // false = dark, true = light
            }
            else
            {
                var target = forceLight ? _draft.ActiveLightTheme : _draft.ActiveDarkTheme;
                if (!string.IsNullOrEmpty(target))
                    try { ThemeManager.Instance.Load(target); } catch { }
                else
                    ThemeManager.Instance.LoadSystem(!forceLight);
            }
            // Apply draft font so the full preview is representative.
            ThemeManager.ApplyFontOverride(
                _draft.FontOverrideEnabled,
                _draft.FontOverrideFamily,
                _draft.FontOverrideSize);
        }

        private void CancelThemePreview()
        {
            _themePreviewTimer?.Stop();
            _themePreviewTimer     = null;
            _themePreviewActive    = false;
            _themePreviewSourceBtn = null;

            // Revert to the last saved theme + font (handles system colours and custom
            // theme files correctly, regardless of what was active before Settings opened).
            _main.ApplyThemeFromConfig();

            UpdateThemePreviewBtn();
        }

        private void UpdateThemePreviewBtn()
        {
            // Reset both buttons to idle state first.
            void ResetBtn(Button? btn)
            {
                if (btn == null) return;
                btn.Content    = "▶  Preview";
                btn.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted");
            }
            ResetBtn(DarkThemePreviewBtn);
            ResetBtn(LightThemePreviewBtn);

            // Light up whichever button is currently counting down.
            if (_themePreviewActive && _themePreviewSourceBtn != null)
            {
                _themePreviewSourceBtn.Content    = $"↩  {_themePreviewSecondsLeft}s";
                _themePreviewSourceBtn.Foreground =
                    (System.Windows.Media.Brush)FindResource("Accent");
            }
        }

        private void NotifDuration_Changed(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (NotifDurationPicker?.SelectedItem is System.Windows.Controls.ComboBoxItem ci)
                _draft.NotificationDurationSeconds = (int)ci.Tag;
        }

        private void TrayPopup_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _vm.ShowTrayPopup = TrayPopupToggle.IsChecked == true;
        }

        // ── Font override ─────────────────────────────────────────────────────
        // ── Font picker data item ─────────────────────────────────────────────
        private sealed class FontPickerItem
        {
            public string DisplayName { get; }
            public string FontName    { get; }
            public System.Windows.Media.FontFamily FontFamily { get; }

            public FontPickerItem(string displayName, string fontName)
            {
                DisplayName = displayName;
                FontName    = fontName;
                FontFamily  = string.IsNullOrEmpty(fontName)
                    ? new System.Windows.Media.FontFamily(
                          System.Windows.SystemFonts.MessageFontFamily?.Source ?? "Segoe UI")
                    : new System.Windows.Media.FontFamily(fontName);
            }

            // Used by IsEditable ComboBox to show the right text in the edit box
            public override string ToString() => DisplayName;
        }

        private void PopulateFontPicker()
        {
            if (FontFamilyPicker == null) return;
            _loading = true;

            // Sync toggle
            if (FontOverrideToggle != null)
                FontOverrideToggle.IsChecked = _draft.FontOverrideEnabled;

            // Show / hide the expanded picker panel
            if (FontPickerPanel != null)
                FontPickerPanel.Visibility =
                    _draft.FontOverrideEnabled ? Visibility.Visible : Visibility.Collapsed;

            // Populate the font list only once — it's an expensive enumeration
            if (!_fontPickerPopulated)
            {
                var items = new System.Collections.Generic.List<FontPickerItem>();
                items.Add(new FontPickerItem("(System UI font)", ""));

                var families = System.Windows.Media.Fonts.SystemFontFamilies
                    .Select(f => f.Source)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var name in families)
                    items.Add(new FontPickerItem(name, name));

                FontFamilyPicker.ItemsSource = items;
                _fontPickerPopulated = true;
            }

            // Pre-select the current font family
            var current = _draft.FontOverrideFamily ?? "";
            var match   = FontFamilyPicker.ItemsSource
                .OfType<FontPickerItem>()
                .FirstOrDefault(fi => string.Equals(
                    fi.FontName, current, StringComparison.OrdinalIgnoreCase));

            if (match != null)
                FontFamilyPicker.SelectedItem = match;
            else
                FontFamilyPicker.Text = current;   // typed name not in list

            // Sync size slider (0 = no override → show theme default as initial value)
            double sliderVal = _draft.FontOverrideSize > 0 ? _draft.FontOverrideSize : 12.0;
            if (FontSizeSlider != null)
                FontSizeSlider.Value = Math.Clamp(sliderVal, 8.0, 18.0);
            if (FontSizeValueLabel != null)
                FontSizeValueLabel.Text = $"{(int)sliderVal} pt";

            _loading = false;

            ApplyFontPreview();
        }

        private void ApplyFontPreview()
        {
            if (FontPreviewLabel == null) return;

            // Font family
            string family = _draft.FontOverrideEnabled
                ? (_draft.FontOverrideFamily ?? "")
                : "";

            if (string.IsNullOrWhiteSpace(family))
                family = System.Windows.SystemFonts.MessageFontFamily?.Source
                         ?? "Segoe UI";

            try   { FontPreviewLabel.FontFamily = new System.Windows.Media.FontFamily(family); }
            catch { FontPreviewLabel.FontFamily = new System.Windows.Media.FontFamily("Segoe UI"); }

            // Font size
            double size = _draft.FontOverrideEnabled && _draft.FontOverrideSize > 0
                ? _draft.FontOverrideSize
                : 12.0;
            FontPreviewLabel.FontSize = size;
        }

        private void FontOverride_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            bool on = FontOverrideToggle?.IsChecked == true;
            _draft.FontOverrideEnabled = on;
            if (FontPickerPanel != null)
                FontPickerPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            // Toggling the override cancels any active font preview.
            if (_fontPreviewActive) CancelFontPreview();
            if (on) PopulateFontPicker();
            else    ApplyFontPreview();   // reset label when override is turned off
        }

        private void FontFamilyPicker_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (FontFamilyPicker?.SelectedItem is FontPickerItem fi)
            {
                _draft.FontOverrideFamily = fi.FontName;
                // Changing the selection cancels any active preview so the interface
                // reverts to committed; the user then clicks Preview to see the new font.
                if (_fontPreviewActive) CancelFontPreview();
            }
        }

        private void FontFamilyPicker_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            // Handles manually typed font names in the editable ComboBox.
            var text = FontFamilyPicker?.Text.Trim() ?? "";
            if (text != (_draft.FontOverrideFamily ?? ""))
            {
                _draft.FontOverrideFamily = text;
                if (_fontPreviewActive) CancelFontPreview();
            }
        }

        private void FontSizeSlider_Changed(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            double size = Math.Round(e.NewValue);
            _draft.FontOverrideSize = size;
            if (FontSizeValueLabel != null)
                FontSizeValueLabel.Text = $"{(int)size} pt";
            if (_fontPreviewActive) CancelFontPreview();
        }

        // ── Font live-preview ─────────────────────────────────────────────────
        private void FontPreview_Click(object sender, RoutedEventArgs e)
        {
            // Already previewing → clicking the button reverts immediately
            if (_fontPreviewActive) { CancelFontPreview(); return; }

            // Update the in-settings sample label with the draft font first.
            ApplyFontPreview();

            // Apply draft font to the whole interface right now
            var family = string.IsNullOrEmpty(_draft.FontOverrideFamily)
                ? (System.Windows.SystemFonts.MessageFontFamily?.Source ?? "Segoe UI")
                : _draft.FontOverrideFamily;
            var size = _draft.FontOverrideSize > 0 ? _draft.FontOverrideSize : 12.0;
            ThemeManager.ApplyFontOverride(true, family, size);

            _fontPreviewActive      = true;
            _fontPreviewSecondsLeft = 10;
            UpdateFontPreviewBtn();

            _fontPreviewTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _fontPreviewTimer.Tick += (_, _) =>
            {
                _fontPreviewSecondsLeft--;
                if (_fontPreviewSecondsLeft <= 0)
                    CancelFontPreview();
                else
                    UpdateFontPreviewBtn();
            };
            _fontPreviewTimer.Start();
        }

        private void CancelFontPreview()
        {
            _fontPreviewTimer?.Stop();
            _fontPreviewTimer  = null;
            _fontPreviewActive = false;

            // Restore the committed theme and font override in one call.
            // ApplyThemeFromConfig reloads the theme file (resetting any preview font
            // baked into theme resources) and then re-applies the committed font override.
            _main.ApplyThemeFromConfig();

            // Reset the in-settings preview label to committed values.
            var cfg = _main.ConfigSvc.Config;
            if (FontPreviewLabel != null)
            {
                var family = cfg.FontOverrideEnabled ? (cfg.FontOverrideFamily ?? "") : "";
                if (string.IsNullOrWhiteSpace(family))
                    family = System.Windows.SystemFonts.MessageFontFamily?.Source ?? "Segoe UI";
                try   { FontPreviewLabel.FontFamily = new System.Windows.Media.FontFamily(family); }
                catch { FontPreviewLabel.FontFamily = new System.Windows.Media.FontFamily("Segoe UI"); }
                FontPreviewLabel.FontSize =
                    cfg.FontOverrideEnabled && cfg.FontOverrideSize > 0 ? cfg.FontOverrideSize : 12.0;
            }

            UpdateFontPreviewBtn();
        }

        private void UpdateFontPreviewBtn()
        {
            if (FontPreviewBtn == null) return;
            if (_fontPreviewActive)
            {
                FontPreviewBtn.Content    = $"↩  {_fontPreviewSecondsLeft}s";
                FontPreviewBtn.Foreground =
                    (System.Windows.Media.Brush)FindResource("Accent");
            }
            else
            {
                FontPreviewBtn.Content    = "▶  Preview";
                FontPreviewBtn.Foreground =
                    (System.Windows.Media.Brush)FindResource("TextMuted");
            }
        }

        // ── Default Action tab ────────────────────────────────────────────────
        private void RefreshAutomationControls()
        {
            var cfg = _draft;
            var tunnels = _main.GetTunnelNames();

            // Sync _vm.Rules from live config (may have changed via main window rule buttons)
            // and also sync _draft.Rules to match
            _loading = true;
            _vm.Rules.Clear();
            foreach (var r in _main.ConfigSvc.Config.Rules)
                _vm.Rules.Add(r);
            _draft.Rules = _main.ConfigSvc.Config.Rules.ToList();
            _loading = false;

            _loading = true;

            // Rules visibility toggles (moved here from General)
            if (HideWifiRulesToggle != null)
                HideWifiRulesToggle.IsChecked = !cfg.ShowWifiRulesOnMainWindow;
            if (ShowRulesColumnToggle != null)
                ShowRulesColumnToggle.IsChecked = cfg.ShowTunnelRulesColumn;

            // Rules
            if (RulesListView != null)
                RulesListView.ItemsSource = cfg.Rules;

            // Manual mode
            if (ManualModeToggle != null)
                ManualModeToggle.IsChecked = cfg.ManualMode;
            if (AutomationPanel != null)
            {
                AutomationPanel.IsEnabled = !cfg.ManualMode;
                AutomationPanel.Opacity   = cfg.ManualMode ? 0.4 : 1.0;
            }

            // WiFi default action radios
            if (ActionNone     != null) ActionNone.IsChecked     = cfg.DefaultAction == "none" || string.IsNullOrEmpty(cfg.DefaultAction);
            if (ActionDiscon   != null) ActionDiscon.IsChecked   = cfg.DefaultAction == "disconnect";
            if (ActionActivate != null) ActionActivate.IsChecked = cfg.DefaultAction == "activate";

            if (DefaultTunnelBox != null)
            {
                DefaultTunnelBox.Items.Clear();
                foreach (var t in tunnels) DefaultTunnelBox.Items.Add(t);
                DefaultTunnelBox.Text = cfg.DefaultTunnel ?? "";
            }

            // Open WiFi
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

        private void DefaultAction_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if      (ActionNone?.IsChecked   == true) _vm.DefaultAction = "none";
            else if (ActionDiscon?.IsChecked == true) _vm.DefaultAction = "disconnect";
            else                                      _vm.DefaultAction = "activate";
        }

        private void DefaultTunnelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (DefaultTunnelBox?.SelectedItem is string t)
            {
                _draft.DefaultTunnel = t;
                _draft.DefaultAction = "activate";
                _loading = true;
                ActionActivate.IsChecked = true;
                _loading = false;
                _main.SaveConfigPublic($"Default tunnel: {t}");
            }
        }

        private void DefaultTunnelBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var text = DefaultTunnelBox?.Text.Trim() ?? "";
            if (!string.IsNullOrEmpty(text) && text != _draft.DefaultTunnel)
            {
                _draft.DefaultTunnel = text;
                _draft.DefaultAction = "activate";
                _loading = true;
                ActionActivate.IsChecked = true;
                _loading = false;
                _main.SaveConfigPublic($"Default tunnel: {text}");
            }
        }

        private void OpenWifiTunnel_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            var none = Lang.T("OpenWifiNone");
            var sel  = OpenWifiTunnelBox?.SelectedItem as string;
            _vm.OpenWifiTunnel = sel == none ? "" : sel ?? "";
        }

        // ── WiFi Rules tab ────────────────────────────────────────────────────
        private void ManualMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            bool on = ManualModeToggle?.IsChecked == true;
            _vm.DisableWifiRules = on;
            // Dim the WiFi rules section when disabled
            if (AutomationPanel != null) AutomationPanel.Opacity = on ? 0.4 : 1.0;
            if (AutomationPanel != null) AutomationPanel.IsEnabled = !on;
        }

        private void OnAddRule()
        {
            var dlg = new RuleDialog(_main.WifiSvc.CurrentSsid,
                tunnels: _main.GetTunnelNames()) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            var rule = new TunnelRule { Ssid = dlg.ResultSsid, Tunnel = dlg.ResultTunnel };
            _vm.AddRule(rule);
            RefreshAutomationControls();
        }

        private void OnEditRule(TunnelRule rule)
        {
            var dlg = new RuleDialog(_main.WifiSvc.CurrentSsid,
                existingName:   rule.Name,
                existingSsid:   rule.Ssid,
                existingTunnel: rule.Tunnel,
                tunnels:        _main.GetTunnelNames()) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            rule.Ssid   = dlg.ResultSsid;
            rule.Tunnel = dlg.ResultTunnel;
            _vm.UpdateRule(rule);
            RefreshAutomationControls();
        }

        private void AddRule_Click(object sender, RoutedEventArgs e)    => _vm.AddRuleCommand.Execute(null);
        private void EditRule_Click(object sender, RoutedEventArgs e)   => _vm.EditRuleCommand.Execute(null);
        private void DeleteRule_Click(object sender, RoutedEventArgs e) => _vm.DeleteRuleCommand.Execute(null);

        private void SaveRules_Click(object sender, RoutedEventArgs e)  => _vm.SaveRulesCommand.Execute(null);

        private void RulesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _vm.SelectedRule = RulesListView?.SelectedItem as TunnelRule;
            if (EditBtn   != null) EditBtn.IsEnabled   = _vm.SelectedRule != null;
            if (DeleteBtn != null) DeleteBtn.IsEnabled = _vm.SelectedRule != null;
        }

        // ── Advanced tab ──────────────────────────────────────────────────────
        private void PopulateLogLevelPicker()
        {
            if (LogLevelPicker == null || LogLevelPicker.Items.Count > 0) return;
            _loading = true;
            LogLevelPicker.Items.Add(Lang.T("LogLevelNormal"));
            LogLevelPicker.Items.Add(Lang.T("LogLevelExtended"));
            LogLevelPicker.SelectedIndex = _draft.LogLevelSetting == "extended" ? 1 : 0;
            _loading = false;
        }

        private void LogLevel_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (LogLevelPicker?.SelectedIndex == 1)
                _vm.LogLevel = "extended";
            else
                _vm.LogLevel = "normal";
        }

        private void RefreshInstallState()
        {
            var mode      = _main.AppRunMode;
            var installed = _main.GetInstalledPath();

            string statusText;
            string? statusPath = null;
            string btnLabel;
            System.Windows.Media.Brush statusColor;

            switch (mode)
            {
                case MainWindow.AppRunModeKind.Managed:
                    statusText  = Lang.T("InstallStatusManaged");
                    statusPath  = installed;
                    btnLabel    = Lang.T("BtnUninstall");
                    statusColor = (System.Windows.Media.Brush)FindResource("Accent");
                    break;
                case MainWindow.AppRunModeKind.ManagedPortable:
                    statusText  = Lang.T("InstallStatusPortable", installed ?? "");
                    statusPath  = installed;
                    btnLabel    = Lang.T("BtnInstall");
                    statusColor = (System.Windows.Media.Brush)FindResource("Accent");
                    break;
                default: // Standalone
                    statusText  = Lang.T("InstallStatusNotInstalled");
                    btnLabel    = Lang.T("BtnInstall");
                    statusColor = (System.Windows.Media.Brush)FindResource("TextMuted");
                    break;
            }

            if (InstallStatusLabel != null)
            {
                InstallStatusLabel.Text       = statusText;
                InstallStatusLabel.Foreground = statusColor;
            }
            if (InstallPathLabel  != null)
                InstallPathLabel.Text = statusPath ?? "";
            if (FindName("InstallBtn") is System.Windows.Controls.Button btn)
                btn.Content = btnLabel;

            if (SuppressUpdatePromptToggle != null)
            {
                _loading = true;
                SuppressUpdatePromptToggle.IsChecked =
                    _draft.SuppressPortableUpdatePrompt;
                _loading = false;
            }

            // Sync frequency pills
            SyncFrequencyPills();
        }

        private void RefreshDllStatus()
        {
            var baseDir    = AppContext.BaseDirectory;
            var tunnelPath = System.IO.Path.Combine(baseDir, "tunnel.dll");
            var wgPath     = System.IO.Path.Combine(baseDir, "wireguard.dll");
            SetLabel("TunnelDllLabel", System.IO.File.Exists(tunnelPath)
                ? $"tunnel.dll  ({new System.IO.FileInfo(tunnelPath).Length / 1024} KB)"
                : Lang.T("SettingsDllMissing"));
            SetLabel("WgDllLabel", System.IO.File.Exists(wgPath)
                ? $"wireguard.dll  ({new System.IO.FileInfo(wgPath).Length / 1024} KB)"
                : Lang.T("SettingsDllMissing"));
        }

        private void RefreshWireGuardSection()
        {
            SetLabel("WgInstallLabel",
                MainWindow.DetectWireGuardInstallDir() ?? Lang.T("SettingsWgNotFound"));
        }

        private void ScanOrphans()
        {
            var orphans = _main.GetOrphanedServices();

            // Update status label (OrphanStatusLabel is defined in XAML)
            if (OrphanStatusLabel != null)
                OrphanStatusLabel.Text = orphans.Count == 0
                    ? Lang.T("SettingsNoOrphans")
                    : $"{orphans.Count} orphaned service{(orphans.Count == 1 ? "" : "s")} found";

            // Rebuild inline list (OrphanListPanel is a StackPanel in XAML)
            OrphanListPanel.Children.Clear();
            OrphanListPanel.Visibility = orphans.Count > 0
                ? Visibility.Visible : Visibility.Collapsed;

            foreach (var o in orphans)
            {
                var card = new System.Windows.Controls.Border
                {
                    Background      = (System.Windows.Media.Brush)Application.Current.Resources["Surface"],
                    BorderBrush     = (System.Windows.Media.Brush)Application.Current.Resources["BorderColor"],
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(3),
                    Padding         = new Thickness(10, 6, 10, 6),
                    Margin          = new Thickness(0, 0, 0, 4),
                };
                var row = new System.Windows.Controls.Grid();
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
                    { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
                    { Width = GridLength.Auto });

                // Show tunnel name + service name + running/stopped badge
                string statusBadge = o.TunnelActive ? "● Running" : "○ Stopped";
                var statusColor = o.TunnelActive
                    ? (System.Windows.Media.Brush)Application.Current.Resources["Danger"]
                    : (System.Windows.Media.Brush)Application.Current.Resources["TextMuted"];

                var namePanel = new System.Windows.Controls.StackPanel
                    { Orientation = System.Windows.Controls.Orientation.Vertical,
                      VerticalAlignment = VerticalAlignment.Center };
                namePanel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text       = o.TunnelName,
                    FontSize   = 10,
                    Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextPrimary"],
                });
                namePanel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text       = $"{o.ServiceName}  ·  {statusBadge}",
                    FontSize   = 9,
                    Foreground = statusColor,
                });
                System.Windows.Controls.Grid.SetColumn(namePanel, 0);

                var capturedOrphan = o;
                var removeBtn = new System.Windows.Controls.Button
                {
                    Content = "Remove",
                    Style   = (Style)Application.Current.Resources["DangerBtn"],
                    FontSize = 9,
                    Padding  = new Thickness(8, 3, 8, 3),
                };
                removeBtn.Click += (_, _) => { _main.RemoveOrphan(capturedOrphan); ScanOrphans(); };
                System.Windows.Controls.Grid.SetColumn(removeBtn, 1);

                row.Children.Add(namePanel);
                row.Children.Add(removeBtn);
                card.Child = row;
                OrphanListPanel.Children.Add(card);
            }

            if (RemoveAllOrphansBtn != null)
                RemoveAllOrphansBtn.Visibility = orphans.Count > 0
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetLabel(string name, string text)
        {
            if (FindName(name) is System.Windows.Controls.TextBlock tb) tb.Text = text;
        }

        private void RemoveAllOrphans_Click(object sender, RoutedEventArgs e)
        {
            foreach (var o in _main.GetOrphanedServices())
                _main.RemoveOrphan(o);
            ScanOrphans();
        }

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            // Delegate to existing install logic via main window
        }

        private void OpenWireGuard_Click(object sender, RoutedEventArgs e)      => _main.OpenWireGuardGui();

        private void ExportSettings_Click(object sender, RoutedEventArgs e)     => _vm.ExportCommand.Execute(null);
        private void ImportSettings_Click(object sender, RoutedEventArgs e)     => _vm.ImportCommand.Execute(null);

        private void OnExportSettings()
        {
            var warn = MessageBox.Show(Lang.T("SettingsExportWarning"),
                Lang.T("SettingsExportTitle"),
                MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (warn != MessageBoxResult.OK) return;

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
                _main.ConfigSvc.Export(dlg.FileName, UpdateChecker.CurrentVersionString);
                _main.LogInfoPublic(Lang.T("SettingsExportSuccess", dlg.FileName));
            }
            catch (Exception ex)
            {
                MessageBox.Show(Lang.T("SettingsExportError", ex.Message),
                    Lang.T("SettingsExportTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnImportSettings()
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
                        // Re-load the original config
                        _main.ConfigSvc.Load();
                        return;
                    }
                }

                _vm.LoadFromConfig();
                ShowTab(_activeTab);
                MessageBox.Show(Lang.T("SettingsImportSuccess"),
                    Lang.T("SettingsImportTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Lang.T("SettingsImportError", ex.Message),
                    Lang.T("SettingsImportTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── About tab ─────────────────────────────────────────────────────────
        private void RefreshUpdateState()
        {
            var cfg     = _main.ConfigSvc.Config;
            var current = UpdateChecker.CurrentVersionString;

            // Version label — large
            if (VersionLabel != null)
                VersionLabel.Text = $"v{current}";

            // Last checked label
            if (LastCheckedLabel != null)
                LastCheckedLabel.Text = cfg.LastUpdateCheck == default
                    ? Lang.T("SettingsUpdateNeverChecked")
                    : Lang.T("SettingsUpdateLastChecked",
                        cfg.LastUpdateCheck.ToLocalTime().ToString("g"));

            // Status badge: colour + text
            // Use proper version comparison (handles build numbers correctly).
            bool hasLatest   = !string.IsNullOrEmpty(cfg.LatestKnownVersion);
            bool updateAvail = hasLatest && UpdateChecker.IsNewerVersion(cfg.LatestKnownVersion);
            bool isAhead     = hasLatest && UpdateChecker.IsAheadOfLatest(cfg.LatestKnownVersion);

            if (UpdateStatusBadge != null && UpdateStatusLabel != null)
            {
                // All states use the theme Accent colour — icons distinguish them.
                var accentBg = (System.Windows.Media.Brush)Application.Current.Resources["Accent"];
                var onAccent = (System.Windows.Media.Brush)Application.Current.Resources["WindowBg"];

                UpdateStatusBadge.BorderBrush     = System.Windows.Media.Brushes.Transparent;
                UpdateStatusBadge.BorderThickness  = new Thickness(0);
                UpdateStatusBadge.Background       = accentBg;
                UpdateStatusLabel.Foreground       = onAccent;

                if (!hasLatest)
                {
                    // Never checked — muted pill until user hits Check Now
                    UpdateStatusBadge.Background      = (System.Windows.Media.Brush)
                        Application.Current.Resources["Surface"];
                    UpdateStatusBadge.BorderBrush     = (System.Windows.Media.Brush)
                        Application.Current.Resources["BorderColor"];
                    UpdateStatusBadge.BorderThickness = new Thickness(1);
                    UpdateStatusLabel.Foreground      = (System.Windows.Media.Brush)
                        Application.Current.Resources["TextMuted"];
                    UpdateStatusLabel.Text = "— " + Lang.T("SettingsUpdateUnknown");
                }
                else if (updateAvail)
                    UpdateStatusLabel.Text = "↑  " + Lang.T("SettingsUpdateAvailable", cfg.LatestKnownVersion!);
                else if (isAhead)
                    UpdateStatusLabel.Text = "🚀  " + Lang.T("SettingsUpdateAhead", cfg.LatestKnownVersion!);
                else
                    UpdateStatusLabel.Text = "✓  " + Lang.T("SettingsUpdateCurrent", current);
            }

            // Check Now button label
            if (CheckUpdateBtn != null)
                CheckUpdateBtn.Content = Lang.T("BtnCheckUpdate");

            // Download button — only appear after the user has pressed Check now this session.
            if (DoUpdateBtn != null)
            {
                bool showDownload = updateAvail && _updateCheckedThisSession;
                DoUpdateBtn.Visibility = showDownload ? Visibility.Visible : Visibility.Collapsed;
                if (showDownload)
                    DoUpdateBtn.Content = Lang.T("BtnDownloadUpdate", cfg.LatestKnownVersion!);
            }

            // What's New inline panel — fetch from GitHub; fall back to local file.
            if (WhatsNewText != null && !_whatsNewLoaded)
            {
                _whatsNewLoaded   = true;
                WhatsNewText.Text = "Loading…";
                _ = LoadWhatsNewAsync();
            }

            // Frequency pills
            SyncFrequencyPills();
        }

        private void SyncFrequencyPills()
        {
            var freq = _draft.UpdateCheckFrequency ?? "weekly";
            if (FreqOnStart != null) FreqOnStart.IsChecked = freq == "onstart";
            if (FreqDaily   != null) FreqDaily.IsChecked   = freq == "daily";
            if (FreqWeekly  != null) FreqWeekly.IsChecked  = freq == "weekly";
            if (FreqManual  != null) FreqManual.IsChecked  = freq == "manual";
        }

        private void UpdateFreq_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (sender is RadioButton rb && rb.Tag is string tag)
                _draft.UpdateCheckFrequency = tag;
        }

        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            // Shift+click: force-install whatever is on GitHub, bypassing version check.
            // Developer shortcut for testing the update pipeline.
            bool forceUpdate = (System.Windows.Input.Keyboard.Modifiers
                                & System.Windows.Input.ModifierKeys.Shift) != 0;

            if (CheckUpdateBtn != null)
            {
                CheckUpdateBtn.IsEnabled = false;
                CheckUpdateBtn.Content   = Lang.T("SettingsUpdateChecking");
            }
            var latest = await UpdateChecker.CheckNowAsync(
                _main.ConfigSvc.Config, _main.ConfigSvc.Save);
            if (CheckUpdateBtn != null)
            {
                CheckUpdateBtn.IsEnabled = true;
                CheckUpdateBtn.Content   = Lang.T("BtnCheckUpdate");
            }
            _latestRelease            = latest;
            _updateCheckedThisSession = true;
            RefreshUpdateState();

            if (forceUpdate && latest != null)
            {
                // Force mode: start update unconditionally (even if local build is newer).
                if (_main.ShowThemedYesNo(
                    $"Force-install {latest.TagName}?\n\nThis will overwrite your current build. Use this only to test the update pipeline.",
                    "MasselGUARD — Force Update"))
                    await StartUpdateAsync(latest);
                return;
            }

            if (latest != null && UpdateChecker.IsNewerVersion(latest.TagName))
            {
                var current = UpdateChecker.CurrentVersionString;
                if (_main.ShowThemedYesNo(
                    Lang.T("UpdateAvailableMsg", latest.TagName, current),
                    Lang.T("UpdateAvailableTitle")))
                    await StartUpdateAsync(latest);
            }
            else if (latest != null && UpdateChecker.IsAheadOfLatest(latest.TagName))
            {
                _main.ShowThemedInfo(
                    Lang.T("SettingsUpdateAheadMsg", latest.TagName),
                    Lang.T("SettingsUpdateAheadTitle"));
            }
        }

        // Downloads, extracts, and applies the update, then shuts down this instance.
        // Shows inline progress and re-enables buttons on failure.
        private async System.Threading.Tasks.Task StartUpdateAsync(ReleaseInfo release)
        {
            if (release.ZipUrl == null)
            {
                _main.ShowThemedInfo(
                    Lang.T("UpdateNoZipAsset", release.TagName),
                    "MasselGUARD");
                return;
            }

            if (CheckUpdateBtn != null)  CheckUpdateBtn.IsEnabled = false;
            if (DoUpdateBtn    != null)  DoUpdateBtn.IsEnabled    = false;
            if (UpdateProgressLabel != null)
            {
                UpdateProgressLabel.Text       = "";
                UpdateProgressLabel.Visibility = Visibility.Visible;
            }

            var progress = new Progress<string>(msg =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (UpdateProgressLabel != null) UpdateProgressLabel.Text = msg;
                });
            });

            try
            {
                await UpdateChecker.UpdateAsync(
                    release, progress, _main.ConfigSvc.Config, _main.ConfigSvc.Save);
                // UpdateAsync calls ShutdownApp() on success — execution never reaches here.
            }
            catch (Exception ex)
            {
                if (UpdateProgressLabel != null) UpdateProgressLabel.Visibility = Visibility.Collapsed;
                if (CheckUpdateBtn != null)  CheckUpdateBtn.IsEnabled = true;
                if (DoUpdateBtn    != null)  DoUpdateBtn.IsEnabled    = true;
                _main.ShowThemedInfo(
                    $"{Lang.T("UpdateFailed")}\n\n{ex.Message}",
                    "MasselGUARD — " + Lang.T("UpdateAvailableTitle"));
            }
        }

        private void RunWizard_Click(object sender, RoutedEventArgs e)
        {
            var wiz = new WizardWindow(_main) { Owner = this };
            wiz.ShowDialog();
            _vm.LoadFromConfig();
            ShowTab("General");
        }

        // ── Window chrome ─────────────────────────────────────────────────────
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
        // ── Handlers required by XAML ─────────────────────────────────────────
        private void Mode_Changed(object sender, System.Windows.RoutedEventArgs e)
            => AppMode_Changed(sender, e);

        private void SuppressUpdatePrompt_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.SuppressPortableUpdatePrompt =
                SuppressUpdatePromptToggle?.IsChecked == true;
        }

        private void SyncStartWithWindows()
        {
            if (StartWithWindowsToggle == null) return;
            _loading = true;
            StartWithWindowsToggle.IsChecked = _main.GetStartWithWindows();
            _loading = false;
        }

        private void StartWithWindows_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _main.SetStartWithWindows(StartWithWindowsToggle?.IsChecked == true);
        }

        private void SyncConfirmOnClose()
        {
            if (ConfirmOnCloseToggle == null) return;
            _loading = true;
            ConfirmOnCloseToggle.IsChecked = _draft.ConfirmOnClose;
            _loading = false;
        }

        private void ConfirmOnClose_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.ConfirmOnClose = ConfirmOnCloseToggle?.IsChecked == true;
        }

        private void InstallBtn_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            _main.RunInstallPublic();
            RefreshInstallState();
        }

        private void ScanOrphans_Click(object sender, System.Windows.RoutedEventArgs e)
            => ScanOrphans();

        private async void DoUpdate_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_latestRelease != null)
                await StartUpdateAsync(_latestRelease);
        }

        private void GithubLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://github.com/masselink/MasselGUARD") { UseShellExecute = true }); }
            catch { }
        }

        private void WhatsNewLink_GitHub_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://github.com/masselink/MasselGUARD") { UseShellExecute = true }); }
            catch { }
        }

        private void WhatsNewLink_Site_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://masselink.net/") { UseShellExecute = true }); }
            catch { }
        }

        /// <summary>
        /// Fetches WHATSNEW.md from the GitHub repo. Falls back to the local copy
        /// shipped alongside the exe. Updates WhatsNewText on the UI thread.
        /// </summary>
        private async System.Threading.Tasks.Task LoadWhatsNewAsync()
        {
            const string RemoteUrl =
                "https://raw.githubusercontent.com/masselink/MasselGUARD/main/docs/WHATSNEW.md";

            string? text = null;

            // Try GitHub (10-second timeout so the UI isn't stuck)
            try
            {
                using var http = new System.Net.Http.HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", "MasselGUARD");
                http.Timeout = TimeSpan.FromSeconds(10);
                text = await http.GetStringAsync(RemoteUrl);
            }
            catch { /* network unavailable */ }

            // Update the UI (we're back on the UI thread — no ConfigureAwait(false) used)
            if (text != null)
            {
                WhatsNewText.Text             = text;
                WhatsNewScroll.Visibility     = Visibility.Visible;
                WhatsNewErrorPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                WhatsNewScroll.Visibility     = Visibility.Collapsed;
                WhatsNewErrorPanel.Visibility = Visibility.Visible;
            }
        }

        private bool         _fontPickerPopulated      = false;
        private bool         _savedSuccessfully        = false;
        private bool         _updateCheckedThisSession = false;  // Download button only visible after manual check
        private bool         _whatsNewLoaded           = false;  // Fetch once per session
        private ReleaseInfo? _latestRelease;                     // Cached from last CheckNow — needed by DoUpdate button

        // ── Font live-preview timer ───────────────────────────────────────────
        private System.Windows.Threading.DispatcherTimer? _fontPreviewTimer;
        private int  _fontPreviewSecondsLeft;
        private bool _fontPreviewActive;

        // ── Theme live-preview timer ──────────────────────────────────────────
        private System.Windows.Threading.DispatcherTimer? _themePreviewTimer;
        private int  _themePreviewSecondsLeft;
        private bool _themePreviewActive;

        private void SaveBtn_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            _savedSuccessfully = true;

            // Snapshot BEFORE committing so diff is accurate
            var before = _main.ConfigSvc.Config.DeepClone();

            // Commit draft fields that bypass _vm (groups, hidden tabs, toggles, theme)
            _main.ConfigSvc.Config.TunnelGroups        = _draft.TunnelGroups;
            _main.ConfigSvc.Config.HiddenTabs          = _draft.HiddenTabs;
            _main.ConfigSvc.Config.DefaultGroup        = _draft.DefaultGroup;
            _main.ConfigSvc.Config.AlwaysHideTunnelCount = _draft.AlwaysHideTunnelCount;
            _main.ConfigSvc.Config.HideEmptyGroups     = _draft.HideEmptyGroups;
            _main.ConfigSvc.Config.ShowWifiRulesOnMainWindow = _draft.ShowWifiRulesOnMainWindow;
            _main.ConfigSvc.Config.ShowTunnelRulesColumn = _draft.ShowTunnelRulesColumn;
            _main.ConfigSvc.Config.ShowActivityLog        = _draft.ShowActivityLog;
            _main.ConfigSvc.Config.NotificationDurationSeconds = _draft.NotificationDurationSeconds;
            _main.ConfigSvc.Config.UseCustomTheme      = _draft.UseCustomTheme;
            _main.ConfigSvc.Config.SystemThemeMode     = _draft.SystemThemeMode;
            _main.ConfigSvc.Config.ConfirmOnClose      = _draft.ConfirmOnClose;
            _main.ConfigSvc.Config.FontOverrideEnabled = _draft.FontOverrideEnabled;
            _main.ConfigSvc.Config.FontOverrideFamily  = _draft.FontOverrideFamily;
            _main.ConfigSvc.Config.FontOverrideSize    = _draft.FontOverrideSize;

            // Sync _vm.TunnelGroups from _draft so DoSave doesn't overwrite with stale data
            _vm.TunnelGroups.Clear();
            foreach (var g in _draft.TunnelGroups) _vm.TunnelGroups.Add(g);

            _vm.DoSave();

            // Log only changed fields now that config is fully committed
            if (_main.LogSvc.IsExtended)
                LogChangedSettings(before, _main.ConfigSvc.Config);
            // Apply side effects immediately
            Lang.Instance.Load(_vm.Language);
            _main.LogSvc.IsExtended = _vm.LogLevel == "extended";
            _main.ApplyManualMode();
            _main._vm.RebuildTunnelList();
            _main.RebuildTunnelGroupsPublic();
            _main._vm.NotifyRulesColumnChanged();
            _main.RefreshWifiRulesPanel();
            // Apply the correct theme based on the new settings (overrides DoSave preview)
            _main.ApplyThemeFromConfig();
            Close();
        }

        private void LogChangedSettings(Models.AppConfig before, Models.AppConfig after)
        {
            var log = _main.LogSvc;

            void Check(string label, object? a, object? b)
            {
                string sa = (a?.ToString() ?? "—").ToLowerInvariant();
                string sb = (b?.ToString() ?? "—").ToLowerInvariant();
                if (sa != sb)
                    log.Debug($"[Settings] {label,-26} {a}  →  {b}");
            }

            Check("Language",              before.Language,              after.Language);
            Check("Mode",                  before.Mode,                  after.Mode);
            Check("Manual mode",           before.ManualMode,            after.ManualMode);
            Check("Default action",        before.DefaultAction,         after.DefaultAction);
            Check("Default tunnel",        before.DefaultTunnel,         after.DefaultTunnel);
            Check("Open network",          before.OpenWifiTunnel,        after.OpenWifiTunnel);
            Check("Theme (dark)",          before.ActiveDarkTheme,       after.ActiveDarkTheme);
            Check("Theme (light)",         before.ActiveLightTheme,      after.ActiveLightTheme);
            Check("Auto theme",            before.AutoTheme,             after.AutoTheme);
            Check("Custom theme",          before.UseCustomTheme,        after.UseCustomTheme);
            Check("System theme mode",     before.SystemThemeMode,       after.SystemThemeMode);
            Check("Log level",             before.LogLevelSetting,       after.LogLevelSetting);
            Check("Tray popups",           before.ShowTrayPopupOnSwitch, after.ShowTrayPopupOnSwitch);
            Check("Notif duration (s)",    before.NotificationDurationSeconds, after.NotificationDurationSeconds);
            Check("Show rules column",     before.ShowTunnelRulesColumn, after.ShowTunnelRulesColumn);
            Check("WiFi rules panel",      before.ShowWifiRulesOnMainWindow, after.ShowWifiRulesOnMainWindow);
            Check("Hide empty groups",     before.HideEmptyGroups,       after.HideEmptyGroups);
            Check("Hide count badge",      before.AlwaysHideTunnelCount, after.AlwaysHideTunnelCount);
            Check("Default group",         before.DefaultGroup,          after.DefaultGroup);
            Check("Font override",         before.FontOverrideEnabled,   after.FontOverrideEnabled);
            Check("Font family",           before.FontOverrideFamily,    after.FontOverrideFamily);
            Check("Font size",             before.FontOverrideSize,      after.FontOverrideSize);

            // Rules list: compare by count and content
            var rulesAdded   = after.Rules.Where(r => !before.Rules.Any(b => b.Ssid == r.Ssid)).ToList();
            var rulesRemoved = before.Rules.Where(r => !after.Rules.Any(a => a.Ssid == r.Ssid)).ToList();
            foreach (var r in rulesAdded)
                log.Debug($"[Settings] Rule added:   {r.Ssid,-20} → {(string.IsNullOrEmpty(r.Tunnel) ? "disconnect" : r.Tunnel)}");
            foreach (var r in rulesRemoved)
                log.Debug($"[Settings] Rule removed: {r.Ssid}");

            // Groups: added/removed
            var grpAdded   = after.TunnelGroups.Where(g => !before.TunnelGroups.Any(b => b.Name == g.Name)).ToList();
            var grpRemoved = before.TunnelGroups.Where(g => !after.TunnelGroups.Any(a => a.Name == g.Name)).ToList();
            foreach (var g in grpAdded)   log.Debug($"[Settings] Group added:   {g.Name}");
            foreach (var g in grpRemoved) log.Debug($"[Settings] Group removed: {g.Name}");
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            // Always stop any running preview timers — regardless of save/cancel.
            _fontPreviewTimer?.Stop();
            _fontPreviewTimer   = null;
            _fontPreviewActive  = false;
            _themePreviewTimer?.Stop();
            _themePreviewTimer     = null;
            _themePreviewActive    = false;
            _themePreviewSourceBtn = null;
            // If closed without saving, revert any live previews.
            if (!_savedSuccessfully)
            {
                // Revert theme + font to last saved state.
                _main.ApplyThemeFromConfig();
                // Revert log visibility to what was committed before Settings opened.
                _main.SetLogPanelVisible(_main.ConfigSvc.Config.ShowActivityLog);
            }
        }

    }
}