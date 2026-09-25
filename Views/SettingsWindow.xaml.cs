using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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

        // _originalTheme removed - CancelThemePreview/CancelFontPreview/OnClosing
        // now call _main.ApplyThemeFromConfig() which reads the committed config and
        // correctly handles both system colours and custom theme files.
        private Models.AppConfig _draft = new(); // staged copy - only written to config on Save

        /// <summary>Set before ShowDialog() to open on a specific tab. Defaults to "General".</summary>
        public string InitialTab { get; set; } = "General";

        public SettingsWindow(MainWindow main)
        {
            _main = main;
            _vm   = new SettingsViewModel(main.ConfigSvc, main.LogSvc);

            // Wire ViewModel dialog requests
            // (Rule add/edit requests are handled on the main window now, not here.)
            _vm.ExportRequested     += OnExportSettings;
            _vm.ImportRequested     += OnImportSettings;
            _vm.LogLevelChanged     += v => main.LogSvc.IsExtended = v == "extended";

            InitializeComponent();
            DataContext = _vm;

            Loaded += (_, _) =>
            {
                _loading = false;
                // Create a deep copy of the LIVE config - all edits go here until Save is pressed
                _draft = _main.ConfigSvc.Config.DeepClone();
                // Shared-theme repo URL lives on the Advanced page - seed it once here so it
                // is populated regardless of which tab opens first.
                if (SharedThemesRepoBox != null)
                {
                    _loading = true;
                    SharedThemesRepoBox.Text = _draft.SharedThemesRepoUrl ?? "";
                    _loading = false;
                }
                ApplyFeatureTabVisibility();   // module tabs stay visible; sync their feature stubs
                ShowTab(InitialTab);
                RefreshUpdateState();
                RefreshLocalizedStrings();
            };

            Lang.Instance.LanguageChanged += OnLanguageChanged;
            Closed += (_, _) => Lang.Instance.LanguageChanged -= OnLanguageChanged;
        }

        private void OnLanguageChanged(object? sender, EventArgs e) =>
            RefreshLocalizedStrings();

        /// <summary>Refresh labels set from code-behind (not WPF Lang bindings).</summary>
        private void RefreshLocalizedStrings()
        {
            UpdateThemePreviewBtn();
            UpdateFontPreviewBtn();
            if (FontSizeSlider != null)
                UpdateFontSizeLabel(FontSizeSlider.Value);
        }

        private void UpdateFontSizeLabel(double size)
        {
            if (FontSizeValueLabel != null)
                FontSizeValueLabel.Text = string.Format(Lang.T("SettingsFontSizeUnit"), (int)Math.Round(size));
        }

        // ── Tab routing ───────────────────────────────────────────────────────
        private void TabBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            // Dispatch by control name - Tag can't be used here because ShowTab repurposes each
            // button's Tag for the "Active" highlight state. (TabBtnDns must be listed or the DNS
            // tab falls through to General.)
            string tab = btn.Name switch
            {
                "TabBtnTunnels"       => "Tunnels",
                "TabBtnWifi"          => "Wifi",
                "TabBtnDns"           => "Dns",
                "TabBtnAppearance"    => "Appearance",
                "TabBtnStartup"       => "Startup",
                "TabBtnDiagnostics"   => "Diagnostics",
                "TabBtnNotifications" => "Notifications",
                "TabBtnLog"           => "Log",
                "TabBtnHistory"       => "History",
                "TabBtnAbout"         => "About",
                _                     => "General",
            };
            ShowTab(tab);
        }

        public void ShowTab(string tab)
        {
            // Legacy tab names (pre-3.7 layout) map onto the merged pages
            tab = tab switch
            {
                "Groups"        => "Tunnels",
                "Rules"         => "Wifi",
                "DefaultAction" => "Wifi",
                _               => tab,
            };

            _activeTab = tab;

            PageGeneral.Visibility    = tab == "General"    ? Visibility.Visible : Visibility.Collapsed;
            PageTunnels.Visibility    = tab == "Tunnels"    ? Visibility.Visible : Visibility.Collapsed;
            PageWifi.Visibility       = tab == "Wifi"       ? Visibility.Visible : Visibility.Collapsed;
            PageDns.Visibility        = tab == "Dns"        ? Visibility.Visible : Visibility.Collapsed;
            PageAppearance.Visibility = tab == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
            PageStartup.Visibility    = tab == "Startup"    ? Visibility.Visible : Visibility.Collapsed;
            PageDiagnostics.Visibility= tab == "Diagnostics"? Visibility.Visible : Visibility.Collapsed;
            PageNotifications.Visibility = tab == "Notifications" ? Visibility.Visible : Visibility.Collapsed;
            PageLog.Visibility        = tab == "Log"        ? Visibility.Visible : Visibility.Collapsed;
            PageHistory.Visibility    = tab == "History"    ? Visibility.Visible : Visibility.Collapsed;
            PageAbout.Visibility      = tab == "About"      ? Visibility.Visible : Visibility.Collapsed;

            TabBtnGeneral.Tag    = tab == "General"    ? "Active" : null;
            TabBtnTunnels.Tag    = tab == "Tunnels"    ? "Active" : null;
            TabBtnWifi.Tag       = tab == "Wifi"       ? "Active" : null;
            TabBtnDns.Tag        = tab == "Dns"        ? "Active" : null;
            TabBtnAppearance.Tag = tab == "Appearance" ? "Active" : null;
            TabBtnStartup.Tag    = tab == "Startup"     ? "Active" : null;
            TabBtnDiagnostics.Tag= tab == "Diagnostics" ? "Active" : null;
            TabBtnNotifications.Tag = tab == "Notifications" ? "Active" : null;
            TabBtnLog.Tag        = tab == "Log"        ? "Active" : null;
            TabBtnHistory.Tag    = tab == "History"    ? "Active" : null;
            TabBtnAbout.Tag      = tab == "About"      ? "Active" : null;

            // Feature-off stubs: the WireGuard / DNS tabs stay visible but swap their content for
            // a short "enable this feature" panel when the module is turned off.
            ApplyFeatureStubs();

            if (tab == "General")    { RefreshFeatureControls(); RefreshGroupList(); }
            if (tab == "Tunnels")    { RefreshGroupList(); SyncArMode(); SyncKsMode(); SyncSkipTunnelValidation(); }
            if (tab == "Wifi")       { RefreshAutomationControls(); ApplyFeatureSectionVisibility(); }
            if (tab == "Dns")        { RefreshDnsControls(); RefreshDnsLeakSection(); }
            if (tab == "Appearance") { PopulateThemePicker(); SyncCapStyle(); ApplyFeatureSectionVisibility(); }
            if (tab == "Notifications") PopulateNotifSettings();
            if (tab == "History")    RefreshHistoryTab();
            if (tab == "Log")        { PopulateLogLevelPicker(); PopulateLogSettings(); }
            if (tab == "Startup")    { RefreshInstallState(); RefreshDllStatus(); SyncStartWithWindows(); SyncStartupOptions(); SyncConfirmOnClose(); }
            if (tab == "About")      RefreshUpdateState();

            // Managed-preset lock UI - runs last so it wins over the per-tab populate above.
            ApplyPresetLocks();
        }

        /// <summary>
        /// Greys out + 🔒-tooltips every control whose AppConfig field is locked by the managed
        /// preset, and shows the "managed by …" banner. The forcing itself is guaranteed by
        /// ConfigService (values re-asserted on save) - this pass is the visible signal.
        /// </summary>
        private void ApplyPresetLocks()
        {
            var cfg = _main.ConfigSvc;

            if (ManagedBanner != null)
            {
                ManagedBanner.Visibility = cfg.HasManagedPreset ? Visibility.Visible : Visibility.Collapsed;
                if (cfg.HasManagedPreset && ManagedBannerText != null)
                    ManagedBannerText.Text = string.IsNullOrWhiteSpace(cfg.PolicyName)
                        ? Lang.T("SettingsManagedBannerGeneric")
                        : Lang.T("SettingsManagedBanner", cfg.PolicyName);
            }

            if (!cfg.HasManagedPreset) return;

            // Lock + 🔒 badge (one representative control per setting).
            void L(FrameworkElement? c, string field)
            {
                if (c == null || !cfg.IsLocked(field)) return;
                c.IsEnabled = false;
                c.ToolTip   = Lang.T("SettingsLockedTip");
                MarkLocked(c);
            }
            // Disable only, no badge - for the sibling radios/buttons of an already-badged setting.
            void D(FrameworkElement? c, string field)
            {
                if (c == null || !cfg.IsLocked(field)) return;
                c.IsEnabled = false;
                c.ToolTip   = Lang.T("SettingsLockedTip");
            }

            // General
            L(LanguagePicker, "Language");
            L(StartWithWindowsToggle, "StartWithWindows");
            L(ConfirmOnCloseToggle, "ConfirmOnClose");

            // WiFi / automation
            L(FeatAutoEnableToggle, "ManualMode");
            L(ActionNone, "DefaultAction"); D(ActionDiscon, "DefaultAction"); D(ActionActivate, "DefaultAction");
            L(DefaultTunnelBox, "DefaultTunnel");
            L(OpenWifiTunnelBox, "OpenWifiTunnel");
            L(TrustedNetworksBox, "TrustedNetworks"); D(AddCurrentTrustedBtn, "TrustedNetworks");
            // (WiFi rules list moved to the main window - no rule buttons to gate here.)

            // Tunnels
            L(ArModeOff, "AutoReconnectMode"); D(ArModePerTunnel, "AutoReconnectMode"); D(ArModeAlways, "AutoReconnectMode");
            L(KsModePerTunnel, "KillSwitchMode"); D(KsModeAlways, "KillSwitchMode");
            L(SkipTunnelValidationToggle, "SkipTunnelValidation");

            // DNS
            L(ShowDnsIndicatorToggle, "ShowDnsIndicator");
            L(DnsLeakWarnLogToggle, "DnsLeakWarnLog");
            L(DnsLeakWarnToastToggle, "DnsLeakWarnToast");

            // Advanced
            L(SharedThemesRepoBox, "SharedThemesRepoUrl"); D(ResetThemesRepoBtn, "SharedThemesRepoUrl");
            L(FreqOnStart, "UpdateCheckFrequency"); D(FreqDaily, "UpdateCheckFrequency");
            D(FreqWeekly, "UpdateCheckFrequency"); D(FreqManual, "UpdateCheckFrequency");

            // Appearance
            L(ThemePicker, "ActiveTheme");
            L(SysModeLight, "SystemThemeMode"); D(SysModeDark, "SystemThemeMode"); D(SysModeAuto, "SystemThemeMode");

            // Notifications
            L(TrayPopupToggle, "ShowTrayPopupOnSwitch");
            L(NotifDurationPicker, "NotificationDurationSeconds");

            // Display
            L(HideWifiRulesToggle, "ShowWifiRulesOnMainWindow");
            L(ShowRulesColumnToggle, "ShowTunnelRulesColumn");
            L(ShowTimelineToggle, "ShowTimeline");
        }

        // Title elements that already carry a 🔒 badge (ApplyPresetLocks runs on every ShowTab).
        private readonly HashSet<UIElement> _lockAdorned = new();

        /// <summary>
        /// Marks a locked setting consistently: a 🔒 badge right after the setting's title, and a
        /// "not allowed" cursor over the whole setting row. The title and row are derived from the
        /// control's layout (see <see cref="FindBadgeLabel"/> / <see cref="FindLockRow"/>) so the lock
        /// always lands in the same place regardless of whether the control is a toggle, a field, a
        /// radio group or a whole card. Deferred to Loaded priority so the visual tree + adorner layer
        /// exist; re-runs harmlessly on every ShowTab (badge de-duped, cursor idempotent).
        /// </summary>
        private void MarkLocked(FrameworkElement control)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!control.IsVisible) return;   // control's tab is hidden - retry when it shows

                // "Not allowed" cursor over the locked row. A disabled control ignores its own
                // Cursor and passes mouse hits to its parent, so set it on the enabled row container.
                if (FindLockRow(control) is FrameworkElement row)
                    row.Cursor = Cursors.No;

                var label = FindBadgeLabel(control) ?? control;
                if (_lockAdorned.Contains(label)) return;
                var layer = AdornerLayer.GetAdornerLayer(label);
                if (layer == null) return;
                layer.Add(new LockAdorner(label));
                _lockAdorned.Add(label);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ── Locked-setting layout resolution ──────────────────────────────────
        // Every setting is a SettingsCard. A "row" setting (toggle/combo/field) keeps its own label
        // inside its row Grid; a "group" setting (radio group / whole card) is titled by the section
        // header just above its card. These helpers find the right title + row for either shape.

        private bool IsSettingsCard(DependencyObject o)
            => o is Border b && ReferenceEquals(b.Style, TryFindResource("SettingsCard"));

        /// <summary>The container to show the locked cursor over - the whole card if the locked
        /// element is itself a card, otherwise the control's immediate parent (its row).</summary>
        private FrameworkElement? FindLockRow(FrameworkElement control)
            => IsSettingsCard(control)
                ? control
                : VisualTreeHelper.GetParent(control) as FrameworkElement ?? control;

        /// <summary>Finds the TextBlock that visually titles a locked setting.</summary>
        private FrameworkElement? FindBadgeLabel(FrameworkElement control)
        {
            // 1) A label inside the control's own row (a child before the control).
            if (!IsSettingsCard(control) &&
                VisualTreeHelper.GetParent(control) is FrameworkElement row)
            {
                var inRow = FirstTextBlockBeforeChild(row, control);
                if (inRow != null) return inRow;
            }
            // 2) Otherwise the section header directly above the enclosing card.
            var card = IsSettingsCard(control) ? control : NearestCard(control);
            if (card != null)
            {
                var header = PrecedingSiblingTextBlock(card);
                if (header != null) return header;
            }
            return null;
        }

        private Border? NearestCard(DependencyObject start)
        {
            var style = TryFindResource("SettingsCard") as Style;
            for (var n = VisualTreeHelper.GetParent(start); n != null; n = VisualTreeHelper.GetParent(n))
                if (n is Border b && ReferenceEquals(b.Style, style)) return b;
            return null;
        }

        /// <summary>First TextBlock found among the row's children that come before <paramref name="control"/>.</summary>
        private static TextBlock? FirstTextBlockBeforeChild(FrameworkElement row, FrameworkElement control)
        {
            int n = VisualTreeHelper.GetChildrenCount(row);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(row, i);
                if (child == control || IsAncestorOf(child, control)) break;
                var tb = FindFirstTextBlock(child);
                if (tb != null) return tb;
            }
            return null;
        }

        /// <summary>The nearest TextBlock that is a direct sibling before <paramref name="card"/> (its section header).</summary>
        private static TextBlock? PrecedingSiblingTextBlock(FrameworkElement card)
        {
            var parent = VisualTreeHelper.GetParent(card);
            if (parent == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(parent), idx = -1;
            for (int i = 0; i < n; i++)
                if (VisualTreeHelper.GetChild(parent, i) == card) { idx = i; break; }
            for (int i = idx - 1; i >= 0; i--)
                if (VisualTreeHelper.GetChild(parent, i) is TextBlock tb) return tb;
            return null;
        }

        private static TextBlock? FindFirstTextBlock(DependencyObject root)
        {
            if (root is TextBlock tb) return tb;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
                if (FindFirstTextBlock(VisualTreeHelper.GetChild(root, i)) is TextBlock r) return r;
            return null;
        }

        private static bool IsAncestorOf(DependencyObject ancestor, DependencyObject node)
        {
            for (var cur = node; cur != null; cur = VisualTreeHelper.GetParent(cur))
                if (cur == ancestor) return true;
            return false;
        }

        /// <summary>Draws a small 🔒 immediately after the adorned title's text, vertically centred
        /// on its first line. Anchoring to the title (not the control) keeps the lock in the same
        /// place for every setting shape.</summary>
        private sealed class LockAdorner : Adorner
        {
            private static readonly Typeface Emoji =
                new(new FontFamily("Segoe UI Emoji"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            public LockAdorner(UIElement adorned) : base(adorned)
            {
                IsHitTestVisible = false;
                // The badge shares one adorner layer across all tabs; re-draw when the title's
                // tab shows/hides so a hidden-tab badge doesn't linger over the visible page.
                if (adorned is FrameworkElement fe)
                    fe.IsVisibleChanged += (_, _) => InvalidateVisual();
            }

            protected override void OnRender(DrawingContext dc)
            {
                if (AdornedElement is not FrameworkElement fe || !fe.IsVisible) return;

                double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var culture = System.Globalization.CultureInfo.CurrentUICulture;

                // Measure the title text so the lock sits right after the words, not at the far
                // (stretched) edge of the label column.
                double textW = fe.RenderSize.Width;
                double fontSize = 11;
                if (fe is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
                {
                    fontSize = tb.FontSize;
                    var face = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
                    var measure = new FormattedText(tb.Text, culture, FlowDirection.LeftToRight,
                        face, fontSize, Brushes.Black, ppd);
                    textW = Math.Min(measure.WidthIncludingTrailingWhitespace, fe.RenderSize.Width);
                }

                var lockFt = new FormattedText("🔒", culture, FlowDirection.LeftToRight,
                    Emoji, 11, Brushes.Gray, ppd);

                // Centre vertically on a single line even if the title happens to wrap.
                double lineH = Math.Min(fe.RenderSize.Height, fontSize * 1.4);
                double y = Math.Max(0, (lineH - lockFt.Height) / 2);
                dc.DrawText(lockFt, new Point(textW + 5, y));
            }
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
                RefreshLocalizedStrings();
            }
        }

        private void RefreshGroupList()
        {
            if (_loading) return;

            // ── General tab controls ──────────────────────────────────────────
            if (LanguagePicker != null && LanguagePicker.Items.Count == 0)
            {
                foreach (var (code, name, flag) in Lang.AvailableLanguages())
                    LanguagePicker.Items.Add(new LangItem(code, name, flag));
            }
            if (LanguagePicker != null)
                LanguagePicker.SelectedItem = LanguagePicker.Items
                    .OfType<LangItem>()
                    .FirstOrDefault(i => string.Equals(i.Code,
                        _draft.Language, StringComparison.OrdinalIgnoreCase));

            _loading = true;
            if (ShowTimelineToggle       != null) ShowTimelineToggle.IsChecked       = _draft.ShowTimeline;
            if (StoreConnectionHistoryToggle != null) StoreConnectionHistoryToggle.IsChecked = _draft.StoreConnectionHistory;
            _loading = false;
            UpdateTimelineShowEnabled();

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
                ("", "-"),               // no colour / transparent
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

                // Default star - marks which group opens on startup
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

        private void ShowTimeline_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.ShowTimeline = ShowTimelineToggle?.IsChecked == true;
            _main.ConfigSvc.Config.ShowTimeline = _draft.ShowTimeline;
            _main.ConfigSvc.Save();
            _main.ApplyInfoSectionMode();
        }

        private void StoreConnectionHistory_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.StoreConnectionHistory = StoreConnectionHistoryToggle?.IsChecked == true;
            _main.ConfigSvc.Config.StoreConnectionHistory = _draft.StoreConnectionHistory;
            _main.ConfigSvc.Save();
            UpdateTimelineShowEnabled();
            _main.ApplyInfoSectionMode();
        }

        private void UpdateTimelineShowEnabled()
        {
            // Each show-toggle is only available when its own capture is on.
            // The panel auto-hides when both show-toggles are off or disabled.
            if (ShowTimelineToggle    != null) ShowTimelineToggle.IsEnabled    = _draft.StoreConnectionHistory;
            if (ShowWifiInChartToggle != null) ShowWifiInChartToggle.IsEnabled = _draft.StoreWifiHistory;
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

            if (ThemePicker != null)
            {
                ThemePicker.Items.Clear();
                foreach (var f in ThemeManager.AvailableThemes())
                    ThemePicker.Items.Add(new ThemePickerItem(f, ThemeManager.GetThemeDisplayName(f)));
                ThemePicker.SelectedItem = ThemePicker.Items
                    .OfType<ThemePickerItem>()
                    .FirstOrDefault(i => i.FolderName == _draft.ActiveTheme);
                if (ThemePicker.SelectedItem == null && ThemePicker.Items.Count > 0)
                    ThemePicker.SelectedIndex = 0;
            }

            // Theme update badge - passive result of the last app-update check (same
            // frequency/trigger; see MainWindow.CheckForThemeUpdatesAsync). Never re-checks
            // itself here - that would mean a network call every time this tab is opened.
            if (ThemeUpdatesBadge != null)
            {
                var themeUpdates = _main.ConfigSvc.Config.ThemeUpdatesAvailable ?? new System.Collections.Generic.List<string>();
                int n = themeUpdates.Count;
                ThemeUpdatesBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (n > 0)
                {
                    ThemeUpdatesBadge.Text    = n == 1 ? "● 1 update" : $"● {n} updates";
                    ThemeUpdatesBadge.ToolTip = "Theme update" + (n == 1 ? "" : "s") + " available: " +
                        string.Join(", ", themeUpdates) + ". Click to open Community themes.";
                }
            }

            // System mode pills
            _loading = true;
            var sysMode = _draft.SystemThemeMode ?? "auto";
            if (SysModeLight != null) SysModeLight.IsChecked = sysMode == "light";
            if (SysModeDark  != null) SysModeDark.IsChecked  = sysMode == "dark";
            if (SysModeAuto  != null) SysModeAuto.IsChecked  = sysMode != "light" && sysMode != "dark";
            _loading = false;

            // Tray popup + notification duration moved to the "Notifications & history" tab
            // (see PopulateNotifSettings).

            // Font override sync (PopulateFontPicker manages its own _loading guard)
            PopulateFontPicker();

            _themeSwitching = false;
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
            if (_themePreviewActive) CancelThemePreview();
            // Apply immediately - no countdown, stays until Saved or the window closes
            // (OnClosing prompts to keep or discard if it's still unsaved by then).
            ApplySpecificTheme(forceLight: !IsDraftDark());
        }

        private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _themeSwitching) return;
            if (ThemePicker.SelectedItem is ThemePickerItem item)
            {
                _draft.ActiveTheme = item.FolderName;
                _vm.ActiveTheme    = item.FolderName;
                if (_themePreviewActive) CancelThemePreview();
                // Apply immediately, respecting the draft's Light/Dark/Auto mode - not
                // the raw Windows setting, which can disagree with what's picked here.
                ApplySpecificTheme(forceLight: !IsDraftDark());
            }
        }

        // ── Theme live-preview ────────────────────────────────────────────────
        private Button? _themePreviewSourceBtn = null;

        private void DarkThemePreview_Click(object sender, RoutedEventArgs e)
            => StartThemePreview(forceLight: false, DarkThemePreviewBtn);

        private void LightThemePreview_Click(object sender, RoutedEventArgs e)
            => StartThemePreview(forceLight: true, LightThemePreviewBtn);

        private void StartThemePreview(bool forceLight, Button sourceBtn)
        {
            if (_themePreviewActive && _themePreviewSourceBtn == sourceBtn)
            { CancelThemePreview(); return; }
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
                if (_themePreviewSecondsLeft <= 0) CancelThemePreview();
                else UpdateThemePreviewBtn();
            };
            _themePreviewTimer.Start();
        }

        private void ApplySpecificTheme(bool forceLight)
        {
            bool isDark = !forceLight;
            var target = _draft.ActiveTheme;
            if (string.IsNullOrEmpty(target) || target == "__system__")
                ThemeManager.Instance.LoadSystem(isDark);
            else
                try { ThemeManager.Instance.Load(target, isDark); } catch { }

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
            _main.ApplyThemeFromConfig();
            UpdateThemePreviewBtn();
        }

        private void UpdateThemePreviewBtn()
        {
            if (DarkThemePreviewBtn  != null) { DarkThemePreviewBtn.Content  = Lang.T("SettingsThemePreviewDarkBtn");  DarkThemePreviewBtn.Foreground  = (System.Windows.Media.Brush)FindResource("TextMuted"); }
            if (LightThemePreviewBtn != null) { LightThemePreviewBtn.Content = Lang.T("SettingsThemePreviewLightBtn"); LightThemePreviewBtn.Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"); }
            if (_themePreviewActive && _themePreviewSourceBtn != null)
            {
                _themePreviewSourceBtn.Content    = string.Format(Lang.T("SettingsFontPreviewActive"), _themePreviewSecondsLeft);
                _themePreviewSourceBtn.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
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

            // Populate the font list only once - it's an expensive enumeration
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
                UpdateFontSizeLabel(sliderVal);

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
                UpdateFontSizeLabel(size);
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
                FontPreviewBtn.Content    = string.Format(Lang.T("SettingsFontPreviewActive"), _fontPreviewSecondsLeft);
                FontPreviewBtn.Foreground =
                    (System.Windows.Media.Brush)FindResource("Accent");
            }
            else
            {
                FontPreviewBtn.Content    = Lang.T("SettingsFontPreviewBtn");
                FontPreviewBtn.Foreground =
                    (System.Windows.Media.Brush)FindResource("TextMuted");
            }
        }

        // ── WiFi tab (rules + default action + open network) ─────────────────
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

            // (Automation enable/disable lives in General → Features now.)

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

            // Trusted-network auto-protect - only the safe-SSID list lives here now;
            // enabling it and choosing the tunnel is done via the WiFi rule itself.
            if (TrustedNetworksBox != null)
                TrustedNetworksBox.Text = string.Join(Environment.NewLine, cfg.TrustedNetworks);

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

        private void TrustedNetworks_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            CommitTrustedNetworks();
        }

        private void CommitTrustedNetworks()
        {
            _draft.TrustedNetworks = (TrustedNetworksBox?.Text ?? "")
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void AddCurrentTrusted_Click(object sender, RoutedEventArgs e)
        {
            var ssid = _main.WifiSvc.CurrentSsid;
            if (string.IsNullOrWhiteSpace(ssid))
            {
                _main.LogInfoPublic("No current WiFi network to add to the trusted list.");
                return;
            }

            var lines = (TrustedNetworksBox?.Text ?? "")
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            // Don't add a duplicate (case-insensitive).
            if (lines.Any(l => l.Equals(ssid, StringComparison.OrdinalIgnoreCase)))
                return;

            lines.Add(ssid);
            _loading = true;
            if (TrustedNetworksBox != null)
                TrustedNetworksBox.Text = string.Join(Environment.NewLine, lines);
            _loading = false;
            CommitTrustedNetworks();
        }

        // ── DNS automation (Wifi page) ─────────────────────────────────────────
        // DNS state is independent of the settings draft/apply flow, so these persist
        // directly to the live config + Save (guarded so populating combos doesn't re-save).
        private bool _dnsLoading;

        private void RefreshDnsControls()
        {
            var cfg = _main.ConfigSvc.Config;
            _dnsLoading = true;

            if (DnsAutomationToggle != null) DnsAutomationToggle.IsChecked = cfg.DnsAutomationEnabled;

            PopulateDnsProfileCombo(DnsDefaultBox, cfg.DefaultDnsProfileId);
            PopulateDnsProfileCombo(DnsOpenBox,    cfg.OpenWifiDnsProfileId);

            if (DnsFamiliesBox != null)
            {
                DnsFamiliesBox.Items.Clear();
                DnsFamiliesBox.Items.Add(new ComboBoxItem { Content = Lang.T("DnsFamiliesBoth"), Tag = "both" });
                DnsFamiliesBox.Items.Add(new ComboBoxItem { Content = Lang.T("DnsFamiliesV4"),   Tag = "v4" });
                DnsFamiliesBox.Items.Add(new ComboBoxItem { Content = Lang.T("DnsFamiliesV6"),   Tag = "v6" });
                SelectComboByTag(DnsFamiliesBox, string.IsNullOrEmpty(cfg.DnsAddressFamilies) ? "both" : cfg.DnsAddressFamilies);
            }

            RefreshDnsProfilesList();
            _dnsLoading = false;
        }

        private void PopulateDnsProfileCombo(ComboBox? box, string? selectedId)
        {
            if (box == null) return;
            box.Items.Clear();
            box.Items.Add(new ComboBoxItem { Content = Lang.T("DnsProfileNone"),      Tag = Models.DnsProfile.NoneId });
            box.Items.Add(new ComboBoxItem { Content = Lang.T("DnsProfileAutomatic"), Tag = Models.DnsProfile.AutomaticId });
            foreach (var p in _main.ConfigSvc.Config.DnsProfiles)
                box.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
            SelectComboByTag(box, selectedId ?? "");
        }

        private static void SelectComboByTag(ComboBox box, string tag)
        {
            foreach (ComboBoxItem it in box.Items)
                if ((it.Tag as string) == tag) { box.SelectedItem = it; return; }
            if (box.Items.Count > 0) box.SelectedIndex = 0;   // falls back to "none"
        }

        private void RefreshDnsProfilesList()
        {
            if (DnsProfilesList == null) return;
            DnsProfilesList.ItemsSource = null;
            DnsProfilesList.ItemsSource = _main.ConfigSvc.Config.DnsProfiles;
            bool sel = DnsProfilesList.SelectedItem != null;
            if (DnsEditBtn   != null) DnsEditBtn.IsEnabled   = sel;
            if (DnsRemoveBtn != null) DnsRemoveBtn.IsEnabled = sel;
        }

        private void DnsAutomation_Changed(object sender, RoutedEventArgs e)
        {
            if (_dnsLoading) return;
            _main.ConfigSvc.Config.DnsAutomationEnabled = DnsAutomationToggle?.IsChecked == true;
            _main.ConfigSvc.Save();
        }

        private void DnsDefault_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_dnsLoading) return;
            _main.ConfigSvc.Config.DefaultDnsProfileId = (DnsDefaultBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            _main.ConfigSvc.Save();
        }

        private void DnsOpen_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_dnsLoading) return;
            _main.ConfigSvc.Config.OpenWifiDnsProfileId = (DnsOpenBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            _main.ConfigSvc.Save();
        }

        private void DnsFamilies_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_dnsLoading) return;
            _main.ConfigSvc.Config.DnsAddressFamilies = (DnsFamiliesBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "both";
            _main.ConfigSvc.Save();
        }

        private void DnsProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool sel = DnsProfilesList?.SelectedItem != null;
            if (DnsEditBtn   != null) DnsEditBtn.IsEnabled   = sel;
            if (DnsRemoveBtn != null) DnsRemoveBtn.IsEnabled = sel;
        }

        private void DnsProfilesList_DoubleClick(object sender, MouseButtonEventArgs e) => DnsEdit_Click(sender, e);

        private void DnsAdd_Click(object sender, RoutedEventArgs e)
        {
            var created = DnsProfileEditor.Show(this, null);
            if (created == null) return;
            _main.ConfigSvc.Config.DnsProfiles.Add(created);
            _main.ConfigSvc.Save();
            RefreshDnsControls();
            SelectDnsProfileById(created.Id);
        }

        private void DnsPresets_Click(object sender, RoutedEventArgs e)
        {
            var existingNames = _main.ConfigSvc.Config.DnsProfiles
                .Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var preset in Models.DnsProfile.BuiltInPresets())
                if (existingNames.Add(preset.Name)) { _main.ConfigSvc.Config.DnsProfiles.Add(preset); added++; }
            if (added == 0) return;
            _main.ConfigSvc.Save();
            RefreshDnsControls();
        }

        private void DnsEdit_Click(object sender, RoutedEventArgs e)
        {
            if (DnsProfilesList?.SelectedItem is not Models.DnsProfile selected) return;
            var edited = DnsProfileEditor.Show(this, selected);
            if (edited == null) return;
            int i = _main.ConfigSvc.Config.DnsProfiles.FindIndex(p => p.Id == selected.Id);
            if (i >= 0) _main.ConfigSvc.Config.DnsProfiles[i] = edited;
            _main.ConfigSvc.Save();
            RefreshDnsControls();
            SelectDnsProfileById(edited.Id);
        }

        private void DnsRemove_Click(object sender, RoutedEventArgs e)
        {
            if (DnsProfilesList?.SelectedItem is not Models.DnsProfile selected) return;
            var cfg = _main.ConfigSvc.Config;
            cfg.DnsProfiles.RemoveAll(p => p.Id == selected.Id);
            // Clear any references so nothing points at a deleted profile.
            if (cfg.DefaultDnsProfileId  == selected.Id) cfg.DefaultDnsProfileId  = "";
            if (cfg.OpenWifiDnsProfileId == selected.Id) cfg.OpenWifiDnsProfileId = "";
            foreach (var r in cfg.Rules) if (r.DnsProfileId == selected.Id) r.DnsProfileId = "";
            _main.ConfigSvc.Save();
            RefreshDnsControls();
        }

        private void SelectDnsProfileById(string id)
        {
            if (DnsProfilesList == null) return;
            foreach (Models.DnsProfile p in DnsProfilesList.Items)
                if (p.Id == id) { DnsProfilesList.SelectedItem = p; return; }
        }

        // ── Feature modules (General page) ─────────────────────────────────────
        // Two top-level features (WireGuard tunnels / DNS automation) can be turned on/off;
        // at least one stays on. Persist directly + re-gate the affected Settings tabs live.
        // See docs/FeatureModules-Design.md.
        private bool _featLoading;

        /// <summary>Populate the five unified Feature cards (General): enable, top-bar behaviour
        /// (show/hide vs disable) and show-button, for WireGuard / DNS / Automation / Activity log /
        /// Charts.</summary>
        private void RefreshFeatureControls()
        {
            var cfg = _main.ConfigSvc.Config;
            _featLoading = true;

            // Enable toggles
            if (FeatWgEnableToggle     != null) FeatWgEnableToggle.IsChecked     = cfg.EnableTunnels;
            if (FeatDnsEnableToggle    != null) FeatDnsEnableToggle.IsChecked    = cfg.EnableDns;
            if (FeatAutoEnableToggle   != null) FeatAutoEnableToggle.IsChecked   = !cfg.ManualMode;
            if (FeatLogEnableToggle    != null) FeatLogEnableToggle.IsChecked    = cfg.ActivityLogEnabled;
            if (FeatChartsEnableToggle != null) FeatChartsEnableToggle.IsChecked = cfg.ChartsEnabled;
            // The WireGuard/DNS module pair must keep at least one on - disable the sole-on toggle.
            if (FeatWgEnableToggle  != null) FeatWgEnableToggle.IsEnabled  = !(cfg.EnableTunnels && !cfg.EnableDns);
            if (FeatDnsEnableToggle != null) FeatDnsEnableToggle.IsEnabled = !(cfg.EnableDns && !cfg.EnableTunnels);

            // Behaviour radios (Show/hide vs Enable/disable)
            static void Beh(System.Windows.Controls.RadioButton? hide, System.Windows.Controls.RadioButton? dis, bool disables)
            { if (hide != null) hide.IsChecked = !disables; if (dis != null) dis.IsChecked = disables; }
            Beh(FeatWgBehHide,     FeatWgBehDisable,     cfg.TunnelToggleDisables);
            Beh(FeatDnsBehHide,    FeatDnsBehDisable,    cfg.DnsToggleDisables);
            Beh(FeatAutoBehHide,   FeatAutoBehDisable,   cfg.WifiToggleDisables);
            Beh(FeatLogBehHide,    FeatLogBehDisable,    cfg.LogToggleDisables);
            Beh(FeatChartsBehHide, FeatChartsBehDisable, cfg.ChartsToggleDisables);

            // Show-button toggles
            if (FeatWgShowBtn     != null) FeatWgShowBtn.IsChecked     = cfg.ShowTunnelToggleButton;
            if (FeatDnsShowBtn    != null) FeatDnsShowBtn.IsChecked    = cfg.ShowDnsToggleButton;
            if (FeatAutoShowBtn   != null) FeatAutoShowBtn.IsChecked   = cfg.ShowWifiToggleButton;
            if (FeatLogShowBtn    != null) FeatLogShowBtn.IsChecked    = cfg.ShowLogToggleButton;
            if (FeatChartsShowBtn != null) FeatChartsShowBtn.IsChecked = cfg.ShowChartsToggleButton;

            _featLoading = false;
        }

        /// <summary>A feature-area enable/disable toggle. Applies live + re-gates the main window
        /// (and stops the log/history writing when the log/charts areas are disabled).</summary>
        private void FeatEnable_Changed(object sender, RoutedEventArgs e)
        {
            if (_featLoading) return;
            if (sender is not System.Windows.Controls.Primitives.ToggleButton tb) return;
            var cfg = _main.ConfigSvc.Config;
            bool on = tb.IsChecked == true;
            switch (tb.Tag as string)
            {
                case "wg":
                case "dns":
                {
                    bool t = (tb.Tag as string) == "wg"  ? on : cfg.EnableTunnels;
                    bool d = (tb.Tag as string) == "dns" ? on : cfg.EnableDns;
                    if (!t && !d) { RefreshFeatureControls(); return; }   // keep at least one module on
                    cfg.EnableTunnels = t; cfg.EnableDns = d;
                    break;
                }
                case "auto":   cfg.ManualMode = !on; break;
                case "log":    cfg.ActivityLogEnabled = on; _main.LogSvc.Enabled = on; break;
                case "charts": cfg.ChartsEnabled = on; _main.HistorySvc.CaptureEnabled = on; break;
                default: return;
            }
            NormalizeFeatureBehaviour();
            _main.ConfigSvc.Save();
            RefreshFeatureControls();
            ApplyFeatureTabVisibility();          // stubs for WireGuard / DNS / Automation tabs
            _main.ApplyManualMode();              // re-evaluate WiFi automation from ManualMode
            _main.ApplyFeatureVisibility();       // re-gate the main window (tunnels/dns/log/layout)
            _main.RefreshWifiRulesPanel();        // automation rows
            _main.ApplyInfoSectionMode();         // charts panel
            _main.RefreshSectionToggleButtons();  // slash/dim on the title-bar icons
        }

        /// <summary>A feature-area top-bar behaviour radio (Show/hide vs Enable/disable). Staged
        /// directly to config (takes effect the next time the title-bar button is used).</summary>
        private void FeatBeh_Changed(object sender, RoutedEventArgs e)
        {
            if (_featLoading) return;
            if (sender is not System.Windows.Controls.RadioButton rb || rb.IsChecked != true) return;
            var parts = (rb.Tag as string ?? "").Split(':');
            if (parts.Length != 2) return;
            bool disables = parts[1] == "disable";
            var cfg = _main.ConfigSvc.Config;
            switch (parts[0])
            {
                case "wg":     cfg.TunnelToggleDisables = disables; break;
                case "dns":    cfg.DnsToggleDisables    = disables; break;
                case "auto":   cfg.WifiToggleDisables   = disables; break;
                case "log":    cfg.LogToggleDisables    = disables; break;
                case "charts": cfg.ChartsToggleDisables = disables; break;
                default: return;
            }
            NormalizeFeatureBehaviour();
            _main.ConfigSvc.Save();
            RefreshFeatureControls();   // reflect any behaviour that had to be forced to Enable/disable
        }

        /// <summary>A feature-area "show title-bar button" toggle. Applies live.</summary>
        private void FeatShowBtn_Changed(object sender, RoutedEventArgs e)
        {
            if (_featLoading) return;
            if (sender is not System.Windows.Controls.Primitives.ToggleButton tb) return;
            bool on = tb.IsChecked == true;
            var cfg = _main.ConfigSvc.Config;
            switch (tb.Tag as string)
            {
                case "wg":     cfg.ShowTunnelToggleButton = on; break;
                case "dns":    cfg.ShowDnsToggleButton    = on; break;
                case "auto":   cfg.ShowWifiToggleButton   = on; break;
                case "log":    cfg.ShowLogToggleButton    = on; break;
                case "charts": cfg.ShowChartsToggleButton = on; break;
                default: return;
            }
            NormalizeFeatureBehaviour();
            _main.ConfigSvc.Save();
            RefreshFeatureControls();
            _main.RefreshSectionToggleButtons();
        }

        /// <summary>Invariant: a disabled feature whose title-bar button is shown must use the
        /// Enable/disable behaviour - otherwise clicking the button (Show/hide) can't bring the
        /// feature back. Force it so there's never a "disabled + button does nothing" state.</summary>
        private void NormalizeFeatureBehaviour()
        {
            var cfg = _main.ConfigSvc.Config;
            if (!cfg.EnableTunnels     && cfg.ShowTunnelToggleButton) cfg.TunnelToggleDisables = true;
            if (!cfg.EnableDns         && cfg.ShowDnsToggleButton)    cfg.DnsToggleDisables    = true;
            if ( cfg.ManualMode        && cfg.ShowWifiToggleButton)   cfg.WifiToggleDisables   = true;
            if (!cfg.ActivityLogEnabled&& cfg.ShowLogToggleButton)    cfg.LogToggleDisables    = true;
            if (!cfg.ChartsEnabled     && cfg.ShowChartsToggleButton) cfg.ChartsToggleDisables = true;
        }

        /// <summary>The module-specific tabs (WireGuard / DNS) always stay visible; when their
        /// feature is off they show a "enable this feature" stub instead of the settings. Keeps the
        /// stub state in sync after a feature toggle without leaving the current tab.</summary>
        private void ApplyFeatureTabVisibility()
        {
            var cfg = _main.ConfigSvc.Config;
            // Feature tabs stay visible + clickable (so the stub's Enable button is reachable) but
            // dim when their feature is off, so they read as "feature settings, currently disabled".
            if (TabBtnTunnels != null) { TabBtnTunnels.Visibility = Visibility.Visible; TabBtnTunnels.Opacity = cfg.EnableTunnels ? 1.0 : 0.45; }
            if (TabBtnDns     != null) { TabBtnDns.Visibility     = Visibility.Visible; TabBtnDns.Opacity     = cfg.EnableDns     ? 1.0 : 0.45; }
            if (TabBtnWifi    != null) TabBtnWifi.Opacity = !cfg.ManualMode ? 1.0 : 0.45;
            if (TabBtnLog     != null) TabBtnLog.Opacity     = cfg.ActivityLogEnabled ? 1.0 : 0.45;
            if (TabBtnHistory != null) TabBtnHistory.Opacity = cfg.ChartsEnabled       ? 1.0 : 0.45;
            ApplyFeatureStubs();
        }

        /// <summary>On the WireGuard / DNS tabs, swap the real settings for a short "enable this
        /// feature" stub when the module is off (owner chose stub over hiding the tab).</summary>
        private void ApplyFeatureStubs()
        {
            var cfg = _main.ConfigSvc.Config;
            bool auto = !cfg.ManualMode;
            if (TunnelsFeatureStub     != null) TunnelsFeatureStub.Visibility     = cfg.EnableTunnels ? Visibility.Collapsed : Visibility.Visible;
            if (TunnelsFeatureContent  != null) TunnelsFeatureContent.Visibility  = cfg.EnableTunnels ? Visibility.Visible   : Visibility.Collapsed;
            if (DnsFeatureStub         != null) DnsFeatureStub.Visibility         = cfg.EnableDns     ? Visibility.Collapsed : Visibility.Visible;
            if (DnsFeatureContent      != null) DnsFeatureContent.Visibility      = cfg.EnableDns     ? Visibility.Visible   : Visibility.Collapsed;
            if (AutoFeatureStub        != null) AutoFeatureStub.Visibility        = auto ? Visibility.Collapsed : Visibility.Visible;
            if (AutoFeatureContent     != null) AutoFeatureContent.Visibility     = auto ? Visibility.Visible   : Visibility.Collapsed;
        }

        private void EnableAutomationFromStub_Click(object sender, RoutedEventArgs e)
        {
            _main.ConfigSvc.Config.ManualMode = false;
            _main.ConfigSvc.Save();
            RefreshFeatureControls();
            _main.RefreshWifiRulesPanel();
            _main.RefreshSectionToggleButtons();
            ApplyFeatureTabVisibility();   // un-dim the sidebar tab now that the feature is on
            ShowTab("Wifi");
        }

        // Stub "Enable" buttons: turn the feature on, persist, re-gate the main window, and refresh
        // the current tab so the real settings replace the stub immediately.
        private void EnableTunnelsFromStub_Click(object sender, RoutedEventArgs e)
        {
            var cfg = _main.ConfigSvc.Config;
            cfg.EnableTunnels = true;
            _main.ConfigSvc.Save();
            RefreshFeatureControls();
            _main.ApplyFeatureVisibility();
            ApplyFeatureTabVisibility();   // un-dim the sidebar tab now that the feature is on
            ShowTab("Tunnels");
        }

        private void EnableDnsFromStub_Click(object sender, RoutedEventArgs e)
        {
            var cfg = _main.ConfigSvc.Config;
            cfg.EnableDns = true;
            _main.ConfigSvc.Save();
            RefreshFeatureControls();
            _main.ApplyFeatureVisibility();
            ApplyFeatureTabVisibility();   // un-dim the sidebar tab now that the feature is on
            ShowTab("Dns");
        }

        /// <summary>Hide the tunnel-only sections on shared tabs (Wifi default-action / open-network
        /// tunnel; Appearance cap indicator) in DNS-only mode. Called when those tabs are shown.</summary>
        private void ApplyFeatureSectionVisibility()
        {
            var vis = _main.ConfigSvc.Config.EnableTunnels ? Visibility.Visible : Visibility.Collapsed;
            if (WifiTunnelOnlySections != null) WifiTunnelOnlySections.Visibility = vis;
            if (CapIndicatorSection    != null) CapIndicatorSection.Visibility    = vis;
        }

        // The WiFi rules list (add/edit/delete/enable) lives on the main window;
        // Settings no longer duplicates it, so the rule list handlers were removed.

        // ── Notifications & history tab ────────────────────────────────────────
        /// <summary>Populate the tray-popup toggle + notification-duration picker (moved here from
        /// the Appearance tab). Called when the "Notifications & history" tab is shown.</summary>
        private void PopulateNotifSettings()
        {
            if (TrayPopupToggle != null)
            {
                _loading = true;
                TrayPopupToggle.IsChecked = _draft.ShowTrayPopupOnSwitch;
                _loading = false;
            }

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
        }

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

        /// <summary>Populate the clear-on-start toggle + max-size box from the staged config.</summary>
        private void PopulateLogSettings()
        {
            _loading = true;
            if (ClearLogOnStartToggle != null) ClearLogOnStartToggle.IsChecked = _draft.ClearLogOnStart;
            if (MaxLogSizeBox != null)         MaxLogSizeBox.Text = _draft.MaxLogSizeKB.ToString();
            _loading = false;
        }

        private void ClearLogOnStart_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.ClearLogOnStart = ClearLogOnStartToggle?.IsChecked == true;
        }

        private void MaxLogSize_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            if (int.TryParse(MaxLogSizeBox?.Text, out int kb) && kb >= 0)
                _draft.MaxLogSizeKB = kb;
            else if (MaxLogSizeBox != null)
                MaxLogSizeBox.Text = _draft.MaxLogSizeKB.ToString();   // revert invalid input
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

        // ── DNS leak protection (smart name resolution + parallel A/AAAA) ───────
        private void RefreshDnsLeakSection()
        {
            UpdateDnsRow(Services.DnsLeakService.IsSmartNameResolutionDisabled(),
                         DnsLeakStatusLabel, DnsLeakDisableBtn, DnsLeakEnableBtn);
            UpdateDnsRow(Services.DnsLeakService.IsParallelQueriesDisabled(),
                         DnsParallelStatusLabel, DnsParallelDisableBtn, DnsParallelEnableBtn);

            // Possible-leak alert channels (icon / log / toast; all off = disabled).
            _loading = true;
            if (ShowDnsIndicatorToggle != null) ShowDnsIndicatorToggle.IsChecked = _draft.ShowDnsIndicator;
            if (DnsLeakWarnLogToggle   != null) DnsLeakWarnLogToggle.IsChecked   = _draft.DnsLeakWarnLog;
            if (DnsLeakWarnToastToggle != null) DnsLeakWarnToastToggle.IsChecked = _draft.DnsLeakWarnToast;
            _loading = false;
        }

        private void DnsLeakWarnLog_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.DnsLeakWarnLog = DnsLeakWarnLogToggle?.IsChecked == true;
        }

        private void DnsLeakWarnToast_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.DnsLeakWarnToast = DnsLeakWarnToastToggle?.IsChecked == true;
        }

        private void UpdateDnsRow(bool disabled, System.Windows.Controls.TextBlock? status,
                                  System.Windows.Controls.Button? disableBtn,
                                  System.Windows.Controls.Button? enableBtn)
        {
            if (status != null)
            {
                status.Text = disabled
                    ? Lang.T("SettingsDnsLeakStatusOn")
                    : Lang.T("SettingsDnsLeakStatusOff");
                status.Foreground = (System.Windows.Media.Brush)FindResource(
                    disabled ? "Success" : "TextMuted");
            }
            // Grey out the button matching the current state.
            if (disableBtn != null) disableBtn.IsEnabled = !disabled;
            if (enableBtn  != null) enableBtn.IsEnabled  = disabled;
        }

        /// <summary>Runs a DNS-policy change, reports the result, and refreshes the section.</summary>
        private void ApplyDnsPolicy(Action change)
        {
            try
            {
                change();
                _main.ShowThemedInfo(Lang.T("SettingsDnsLeakAppliedMsg"), Lang.T("SettingsSectionDnsLeak"));
            }
            catch (Exception ex)
            {
                _main.ShowThemedInfo(Lang.T("SettingsDnsLeakError", ex.Message), Lang.T("SettingsSectionDnsLeak"));
            }
            RefreshDnsLeakSection();
        }

        private void DnsLeakDisable_Click(object sender, RoutedEventArgs e) =>
            ApplyDnsPolicy(Services.DnsLeakService.DisableSmartNameResolution);

        private void DnsLeakEnable_Click(object sender, RoutedEventArgs e) =>
            ApplyDnsPolicy(Services.DnsLeakService.EnableSmartNameResolution);

        private void DnsParallelDisable_Click(object sender, RoutedEventArgs e) =>
            ApplyDnsPolicy(Services.DnsLeakService.DisableParallelQueries);

        private void DnsParallelEnable_Click(object sender, RoutedEventArgs e) =>
            ApplyDnsPolicy(Services.DnsLeakService.EnableParallelQueries);

        private void SetLabel(string name, string text)
        {
            if (FindName(name) is System.Windows.Controls.TextBlock tb) tb.Text = text;
        }

        private void ExportSettings_Click(object sender, RoutedEventArgs e)     => _vm.ExportCommand.Execute(null);
        private void ImportSettings_Click(object sender, RoutedEventArgs e)     => _vm.ImportCommand.Execute(null);

        // Export the current settings as a .masselguard file. All settings are written (for import);
        // the sections ticked in the dialog go under Locked and are what a preset enforces.
        private void ExportPreset_Click(object sender, RoutedEventArgs e)
        {
            var picker = new ExportPresetWindow { Owner = this };
            if (picker.ShowDialog() != true) return;

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title            = Lang.T("PresetExportTitle"),
                Filter           = "MasselGUARD policy (*.masselguard)|*.masselguard",
                FileName         = "MasselGUARD-policy",
                DefaultExt       = ".masselguard",
                InitialDirectory = Services.PresetService.ExeDirectory(),
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                _main.ConfigSvc.Export(dlg.FileName, UpdateChecker.CurrentVersionString,
                    picker.ResultPolicyName, lockedSettings: picker.ResultSettings);
                _main.LogInfoPublic(Lang.T("PresetExportSuccess", dlg.FileName));
                _main.ShowThemedInfo(Lang.T("PresetExportSuccess", dlg.FileName), Lang.T("PresetExportTitle"));
            }
            catch (Exception ex)
            {
                _main.ShowThemedInfo(Lang.T("PresetExportError", ex.Message), Lang.T("PresetExportTitle"));
            }
        }

        private void OnExportSettings()
        {
            if (!_main.ShowThemedYesNo(Lang.T("SettingsExportWarning"), Lang.T("SettingsExportTitle")))
                return;

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
                _main.ShowThemedInfo(Lang.T("SettingsExportError", ex.Message), Lang.T("SettingsExportTitle"));
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
                    if (!_main.ShowThemedYesNo(Lang.T(key, fileVersion, current), Lang.T("SettingsImportTitle")))
                    {
                        // Re-load the original config
                        _main.ConfigSvc.Load();
                        return;
                    }
                }

                _vm.LoadFromConfig();
                ShowTab(_activeTab);

                // Ask whether to restart now so the imported settings take full effect
                bool restart = _main.ShowThemedYesNo(
                    Lang.T("SettingsImportSuccess"),
                    Lang.T("SettingsImportTitle"));

                if (restart)
                {
                    // Config is already saved by Import(); restart the process
                    string? exe = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exe))
                        System.Diagnostics.Process.Start(
                            new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
                    ((App)System.Windows.Application.Current).ShutdownApp();
                }
                else
                {
                    // Config is saved; warn that some UI values may lag until restart
                    _main.ShowThemedInfo(
                        Lang.T("SettingsImportNoRestart"),
                        Lang.T("SettingsImportTitle"));
                }
            }
            catch (Exception ex)
            {
                _main.ShowThemedInfo(Lang.T("SettingsImportError", ex.Message), Lang.T("SettingsImportTitle"));
            }
        }

        // ── About tab ─────────────────────────────────────────────────────────
        private void RefreshUpdateState()
        {
            var cfg     = _main.ConfigSvc.Config;
            var current = UpdateChecker.CurrentVersionString;

            // Version label - large, with optional codename
            if (VersionLabel != null)
            {
                var codename = UpdateChecker.Codename;
                VersionLabel.Text = string.IsNullOrEmpty(codename)
                    ? $"MasselGUARD v{current}"
                    : $"MasselGUARD v{current}  |  {codename}";
            }

            // Build stamp + architecture - small muted line below the version
            if (BuildLabel != null)
            {
                var stamp = UpdateChecker.BuildStamp;
                var arch  = UpdateChecker.ArchMoniker;
                BuildLabel.Text = string.IsNullOrEmpty(stamp) ? arch : $"build {stamp}  ·  {arch}";
            }

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
                // All states use the theme Accent colour - icons distinguish them.
                var accentBg = (System.Windows.Media.Brush)Application.Current.Resources["Accent"];
                var onAccent = (System.Windows.Media.Brush)Application.Current.Resources["WindowBg"];

                UpdateStatusBadge.BorderBrush     = System.Windows.Media.Brushes.Transparent;
                UpdateStatusBadge.BorderThickness  = new Thickness(0);
                UpdateStatusBadge.Background       = accentBg;
                UpdateStatusLabel.Foreground       = onAccent;

                if (!hasLatest)
                {
                    // Never checked - muted pill until user hits Check Now
                    UpdateStatusBadge.Background      = (System.Windows.Media.Brush)
                        Application.Current.Resources["Surface"];
                    UpdateStatusBadge.BorderBrush     = (System.Windows.Media.Brush)
                        Application.Current.Resources["BorderColor"];
                    UpdateStatusBadge.BorderThickness = new Thickness(1);
                    UpdateStatusLabel.Foreground      = (System.Windows.Media.Brush)
                        Application.Current.Resources["TextMuted"];
                    UpdateStatusLabel.Text = "- " + Lang.T("SettingsUpdateUnknown");
                }
                else if (updateAvail)
                    UpdateStatusLabel.Text = "↑  " + Lang.T("SettingsUpdateAvailable", cfg.LatestKnownVersion!);
                else if (isAhead)
                    UpdateStatusLabel.Text = "🚀  " + Lang.T("SettingsUpdateAhead", cfg.LatestKnownVersion!);
                else
                    UpdateStatusLabel.Text = "✓  " + Lang.T("SettingsUpdateCurrent", current);
            }

            // Theme update indicator - same passive cache as the Appearance tab's badge,
            // just surfaced here too so "Check for update" guides the user toward it
            // instead of it only showing up on a tab they may never open.
            if (AboutThemeUpdatesRow != null && AboutThemeUpdatesLabel != null)
            {
                var themeUpdates = cfg.ThemeUpdatesAvailable ?? new System.Collections.Generic.List<string>();
                int n = themeUpdates.Count;
                AboutThemeUpdatesRow.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (n > 0)
                {
                    AboutThemeUpdatesLabel.Text = n == 1
                        ? Lang.T("AboutThemeUpdateOne", themeUpdates[0])
                        : Lang.T("AboutThemeUpdateMany", n, string.Join(", ", themeUpdates));
                }
            }

            // Check Now button label
            if (CheckUpdateBtn != null)
                CheckUpdateBtn.Content = Lang.T("BtnCheckUpdate");

            // Download button - only appear after the user has pressed Check now this session.
            if (DoUpdateBtn != null)
            {
                bool showDownload = updateAvail && _updateCheckedThisSession;
                DoUpdateBtn.Visibility = showDownload ? Visibility.Visible : Visibility.Collapsed;
                if (showDownload)
                    DoUpdateBtn.Content = Lang.T("BtnDownloadUpdate", cfg.LatestKnownVersion!);
            }

            // What's New inline panel - fetch from GitHub; fall back to local file.
            if (WhatsNewBox != null && !_whatsNewLoaded)
            {
                _whatsNewLoaded = true;
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
            _ = _main.CheckForThemeUpdatesAsync();   // piggyback theme-update check on the same trigger
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
                    "MasselGUARD - Force Update"))
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
                    release, progress, _main.ConfigSvc.Config, _main.ConfigSvc.Save,
                    onShutdown: () => System.Windows.Application.Current.Dispatcher.Invoke(
                        () => ((App)System.Windows.Application.Current).ShutdownApp()));
                // UpdateAsync calls onShutdown on success - execution never reaches here.
            }
            catch (Exception ex)
            {
                if (UpdateProgressLabel != null) UpdateProgressLabel.Visibility = Visibility.Collapsed;
                if (CheckUpdateBtn != null)  CheckUpdateBtn.IsEnabled = true;
                if (DoUpdateBtn    != null)  DoUpdateBtn.IsEnabled    = true;
                _main.ShowThemedInfo(
                    $"{Lang.T("UpdateFailed")}\n\n{ex.Message}",
                    "MasselGUARD - " + Lang.T("UpdateAvailableTitle"));
            }
        }

        private void OpenThemeBuilder_Click(object sender, RoutedEventArgs e)
        {
            // Close Settings first: the builder applies and saves themes directly,
            // and a Settings save afterwards would overwrite ActiveTheme with the
            // stale deferred draft. OnClosing also reverts any running preview, so
            // the builder starts from the committed theme.
            Close();
            var builder = new ThemeBuilderWindow(_main) { Owner = _main };
            // Return focus to Settings → Appearance once the manager closes.
            builder.Closed += (_, _) => _main.OpenSettings("Appearance");
            builder.Show();
        }

        /// <summary>Shortcut: opens the Theme Manager and immediately its community
        /// theme browser, skipping the extra click through "Manage themes…".</summary>
        private void DownloadThemes_Click(object sender, RoutedEventArgs e)
        {
            Close();
            var builder = new ThemeBuilderWindow(_main) { Owner = _main };
            builder.Closed += (_, _) => _main.OpenSettings("Appearance");
            builder.Show();
            builder.OpenCommunityThemes();
        }

        private void SharedThemesRepo_Changed(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            _draft.SharedThemesRepoUrl = SharedThemesRepoBox.Text.Trim();
        }

        /// <summary>Reset the repo URL to the official MasselGUARD themes repository.</summary>
        private void ResetThemesRepo_Click(object sender, RoutedEventArgs e)
        {
            SharedThemesRepoBox.Text = Models.AppConfig.DefaultSharedThemesRepoUrl;
            SharedThemesRepoBox.CaretIndex = SharedThemesRepoBox.Text.Length;
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

        // (View presets removed - features are configured individually in General → Features.)

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

        private void SyncStartupOptions()
        {
            if (StartMinimizedToggle == null) return;
            _loading = true;
            StartMinimizedToggle.IsChecked = _draft.StartMinimized;
            _loading = false;
        }

        private void StartMinimized_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.StartMinimized = StartMinimizedToggle?.IsChecked == true;
        }

        private void SyncArMode()
        {
            if (ArModeOff == null) return;
            _loading = true;
            var mode = _draft.AutoReconnectMode ?? "off";
            ArModeOff.IsChecked        = mode == "off";
            ArModePerTunnel.IsChecked  = mode == "per-tunnel";
            ArModeAlways.IsChecked     = mode == "always";
            _loading = false;
        }

        private void ArMode_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            if (sender is System.Windows.Controls.RadioButton rb && rb.Tag is string tag)
                _draft.AutoReconnectMode = tag;
        }

        private void ShowDnsIndicator_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.ShowDnsIndicator = ShowDnsIndicatorToggle?.IsChecked == true;
        }

        private void SyncKsMode()
        {
            if (KsModePerTunnel == null || KsModeAlways == null) return;
            _loading = true;
            var mode = _draft.KillSwitchMode ?? "per-tunnel";
            KsModePerTunnel.IsChecked = mode != "always";
            KsModeAlways.IsChecked    = mode == "always";
            _loading = false;
        }

        private void KsMode_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            if (sender is System.Windows.Controls.RadioButton rb && rb.Tag is string tag)
                _draft.KillSwitchMode = tag;
        }

        private void SyncCapStyle()
        {
            if (CapStyleRings == null || CapStyleBars == null) return;
            _loading = true;
            var style = _draft.CapIndicatorStyle ?? "rings";
            CapStyleBars.IsChecked  = style == "bars";
            CapStyleRings.IsChecked = style != "bars";
            _loading = false;
        }

        private void CapStyle_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            if (sender is System.Windows.Controls.RadioButton rb && rb.Tag is string tag)
                _draft.CapIndicatorStyle = tag;
        }

        private void SyncSkipTunnelValidation()
        {
            if (SkipTunnelValidationToggle == null) return;
            _loading = true;
            SkipTunnelValidationToggle.IsChecked = _draft.SkipTunnelValidation;
            _loading = false;
        }

        private void SkipTunnelValidation_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.SkipTunnelValidation = SkipTunnelValidationToggle?.IsChecked == true;
        }

        private void InstallBtn_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            _main.RunInstallPublic();
            RefreshInstallState();
        }

        // ── Diagnostics / Tester (Advanced) ──────────────────────────────────────
        private bool _testRunning;

        private async void RunTests_Click(object sender, RoutedEventArgs e)
        {
            if (_testRunning) return;

            bool local = TestLocalToggle?.IsChecked == true;
            bool dns   = TestDnsToggle?.IsChecked   == true;
            bool cli   = TestCliToggle?.IsChecked   == true;
            if (!local && !dns && !cli)
            {
                if (TestLogBox != null) TestLogBox.Text = Lang.T("TesterSelectMode") + System.Environment.NewLine;
                return;
            }

            _testRunning = true;
            if (RunTestsBtn != null) { RunTestsBtn.IsEnabled = false; RunTestsBtn.Content = Lang.T("TesterRunning"); }
            if (TestLogBox != null) TestLogBox.Clear();

            void Append(Services.DiagnosticsService.Level lvl, string text)
            {
                string glyph = lvl switch
                {
                    Services.DiagnosticsService.Level.Pass => "✓ ",   // ✓
                    Services.DiagnosticsService.Level.Fail => "✗ ",   // ✗
                    Services.DiagnosticsService.Level.Warn => "⚠ ",   // ⚠
                    Services.DiagnosticsService.Level.Head => "",
                    _                                       => "· ",  // ·
                };
                string line = lvl == Services.DiagnosticsService.Level.Head
                    ? $"{System.Environment.NewLine}==== {text} ===="
                    : $"[{System.DateTime.Now:HH:mm:ss}] {glyph}{text}";
                if (TestLogBox == null) return;
                TestLogBox.AppendText(line + System.Environment.NewLine);
                TestLogBox.ScrollToEnd();
            }

            // Sink is invoked from a background thread → marshal to the UI thread.
            void Sink(Services.DiagnosticsService.Level lvl, string text)
                => Dispatcher.Invoke(() => Append(lvl, text));

            var diag = new Services.DiagnosticsService(Sink);
            var cfg  = _main.ConfigSvc.Config;
            var guid = _main.WifiSvc.CurrentInterfaceGuid;

            try
            {
                // Awaited on the UI thread - RunAsync drives the tunnel view-models and offloads
                // its own blocking work, so the window stays responsive.
                await diag.RunAsync(local, dns, cli, _main._vm, _main.TunnelSvc, _main._vm.Dns,
                                    cfg, guid, System.Threading.CancellationToken.None);
            }
            catch (System.Exception ex)
            {
                Append(Services.DiagnosticsService.Level.Fail, "Tester crashed: " + ex.Message);
            }
            finally
            {
                _testRunning = false;
                if (RunTestsBtn != null) { RunTestsBtn.IsEnabled = true; RunTestsBtn.Content = Lang.T("BtnRunTests"); }
            }
        }

        private void CopyTestLog_Click(object sender, RoutedEventArgs e)
        {
            try { if (!string.IsNullOrEmpty(TestLogBox?.Text)) System.Windows.Clipboard.SetText(TestLogBox!.Text); }
            catch { /* clipboard may be locked by another app - ignore */ }
        }

        private void ClearTestLog_Click(object sender, RoutedEventArgs e) => TestLogBox?.Clear();

        private void OpenSystemDiagnostics_Click(object sender, RoutedEventArgs e)
            => _main.OpenSystemDiagnostics(this);

        // (Top-bar section-button controls moved to General → Features - see the Feat* handlers.)

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

        // Open the bundled licence / notices (shipped next to the exe by BUILD.bat);
        // fall back to the GitHub copy in dev builds where they aren't alongside the exe.
        private void LicenseLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => OpenLocalOrUrl("LICENSE.txt",
                "https://github.com/masselink/MasselGUARD/blob/main/LICENSE");

        private void NoticesLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => OpenLocalOrUrl("THIRD-PARTY-NOTICES.md",
                "https://github.com/masselink/MasselGUARD/blob/main/THIRD-PARTY-NOTICES.md");

        private static void OpenLocalOrUrl(string fileName, string fallbackUrl)
        {
            try
            {
                var local = System.IO.Path.Combine(System.AppContext.BaseDirectory, fileName);
                string target = System.IO.File.Exists(local) ? local : fallbackUrl;
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch { }
        }

        /// <summary>
        /// Fetches WHATSNEW.md from the GitHub repo and renders it as a FlowDocument
        /// in WhatsNewBox. Falls back to the error panel when the network is unavailable.
        /// </summary>
        private async System.Threading.Tasks.Task LoadWhatsNewAsync()
        {
            const string RemoteUrl =
                "https://raw.githubusercontent.com/masselink/MasselGUARD/main/docs/WHATSNEW.md";

            // Resolve theme resources before the async gap (we're on the UI thread here)
            var fontFamily  = TryFindResource("Theme.FontFamily") as System.Windows.Media.FontFamily
                              ?? new System.Windows.Media.FontFamily("Segoe UI");
            var textBrush   = (TryFindResource("TextPrimary") as System.Windows.Media.Brush)
                              ?? System.Windows.Media.Brushes.White;
            var mutedBrush  = (TryFindResource("TextMuted")   as System.Windows.Media.Brush)
                              ?? System.Windows.Media.Brushes.Gray;
            var accentBrush = (TryFindResource("Accent")      as System.Windows.Media.Brush)
                              ?? System.Windows.Media.Brushes.CornflowerBlue;
            var codeBgBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(40, 128, 128, 128));

            // Show a "Loading…" document immediately
            WhatsNewBox.Document  = MarkdownToFlowDocument.MakeSimple("Loading…", fontFamily, 10, mutedBrush);
            WhatsNewBox.Visibility            = Visibility.Visible;
            WhatsNewErrorPanel.Visibility     = Visibility.Collapsed;

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

            // Back on the UI thread - no ConfigureAwait(false) used
            if (text != null)
            {
                WhatsNewBox.Document          = MarkdownToFlowDocument.Render(
                    text, fontFamily, 10, textBrush, mutedBrush, accentBrush, codeBgBrush);
                WhatsNewBox.Visibility        = Visibility.Visible;
                WhatsNewErrorPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                WhatsNewBox.Visibility        = Visibility.Collapsed;
                WhatsNewErrorPanel.Visibility = Visibility.Visible;
            }
        }

        private bool         _fontPickerPopulated      = false;
        private bool         _savedSuccessfully        = false;
        private bool         _updateCheckedThisSession = false;  // Download button only visible after manual check
        private bool         _whatsNewLoaded           = false;  // Fetch once per session
        private ReleaseInfo? _latestRelease;                     // Cached from last CheckNow - needed by DoUpdate button

        // ── Theme live-preview timer ──────────────────────────────────────────
        private System.Windows.Threading.DispatcherTimer? _themePreviewTimer;
        private int  _themePreviewSecondsLeft;
        private bool _themePreviewActive;

        // ── Font live-preview timer ───────────────────────────────────────────
        private System.Windows.Threading.DispatcherTimer? _fontPreviewTimer;
        private int  _fontPreviewSecondsLeft;
        private bool _fontPreviewActive;

        private void SaveBtn_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            CommitDraft();
            Close();
        }

        /// <summary>Writes the whole draft to config + applies side effects. Does not close
        /// the window - used by both the Save button and the close-time "keep changes" prompt.</summary>
        private void CommitDraft()
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
            _main.ConfigSvc.Config.ClearLogOnStart        = _draft.ClearLogOnStart;
            _main.ConfigSvc.Config.MaxLogSizeKB           = _draft.MaxLogSizeKB;
            // (Top-bar toggle-button show/behaviour flags are written live by the General →
            //  Features cards, not staged in the draft, so they are not copied here.)
            _main.ConfigSvc.Config.ShowTimeline             = _draft.ShowTimeline;
            _main.ConfigSvc.Config.StoreConnectionHistory  = _draft.StoreConnectionHistory;
            _main.ConfigSvc.Config.InfoTimeRangeDays       = _draft.InfoTimeRangeDays;
            _main.ConfigSvc.Config.StoreWifiHistory        = _draft.StoreWifiHistory;
            _main.ConfigSvc.Config.ShowWifiInChart         = _draft.ShowWifiInChart;
            _main.ConfigSvc.Config.NotificationDurationSeconds = _draft.NotificationDurationSeconds;
            _main.ConfigSvc.Config.ActiveTheme         = _draft.ActiveTheme;
            _main.ConfigSvc.Config.SystemThemeMode     = _draft.SystemThemeMode;
            _main.ConfigSvc.Config.ConfirmOnClose      = _draft.ConfirmOnClose;
            _main.ConfigSvc.Config.StartMinimized      = _draft.StartMinimized;
            _main.ConfigSvc.Config.AutoReconnectMode   = _draft.AutoReconnectMode;
            _main.ConfigSvc.Config.ShowDnsIndicator    = _draft.ShowDnsIndicator;
            _main.ConfigSvc.Config.DnsLeakWarnLog      = _draft.DnsLeakWarnLog;
            _main.ConfigSvc.Config.DnsLeakWarnToast    = _draft.DnsLeakWarnToast;
            _main.ConfigSvc.Config.KillSwitchMode      = _draft.KillSwitchMode;
            _main.ConfigSvc.Config.SkipTunnelValidation = _draft.SkipTunnelValidation;
            _main.ConfigSvc.Config.CapIndicatorStyle   = _draft.CapIndicatorStyle;
            _main.ConfigSvc.Config.TrustedNetworks     = _draft.TrustedNetworks;
            _main.ConfigSvc.Config.FontOverrideEnabled    = _draft.FontOverrideEnabled;
            _main.ConfigSvc.Config.FontOverrideFamily    = _draft.FontOverrideFamily;
            _main.ConfigSvc.Config.FontOverrideSize      = _draft.FontOverrideSize;
            _main.ConfigSvc.Config.UpdateCheckFrequency  = _draft.UpdateCheckFrequency;

            // Sync _vm.TunnelGroups from _draft so DoSave doesn't overwrite with stale data
            _vm.TunnelGroups.Clear();
            foreach (var g in _draft.TunnelGroups) _vm.TunnelGroups.Add(g);

            // Log changed fields before DoSave so detail lines appear below "Settings saved" in the log
            if (_main.LogSvc.IsExtended)
                LogChangedSettings(before, _main.ConfigSvc.Config);

            _vm.DoSave();

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
            _main.ApplyInfoSectionMode();
        }

        private void LogChangedSettings(Models.AppConfig before, Models.AppConfig after)
        {
            var log = _main.LogSvc;

            void Check(string label, object? a, object? b)
            {
                string sa = (a?.ToString() ?? "-").ToLowerInvariant();
                string sb = (b?.ToString() ?? "-").ToLowerInvariant();
                if (sa != sb)
                    log.Debug($"[Settings] {label,-26} {a}  →  {b}");
            }

            Check("Language",              before.Language,              after.Language);
            Check("Manual mode",           before.ManualMode,            after.ManualMode);
            Check("Default action",        before.DefaultAction,         after.DefaultAction);
            Check("Default tunnel",        before.DefaultTunnel,         after.DefaultTunnel);
            Check("Open network",          before.OpenWifiTunnel,        after.OpenWifiTunnel);
            Check("Theme",                 before.ActiveTheme,           after.ActiveTheme);
            Check("System theme mode",     before.SystemThemeMode,       after.SystemThemeMode);
            Check("Log level",             before.LogLevelSetting,       after.LogLevelSetting);
            Check("Tray popups",           before.ShowTrayPopupOnSwitch, after.ShowTrayPopupOnSwitch);
            Check("Notif duration (s)",    before.NotificationDurationSeconds, after.NotificationDurationSeconds);
            Check("Show rules column",     before.ShowTunnelRulesColumn, after.ShowTunnelRulesColumn);
            Check("WiFi rules panel",      before.ShowWifiRulesOnMainWindow, after.ShowWifiRulesOnMainWindow);
            Check("Hide empty groups",     before.HideEmptyGroups,       after.HideEmptyGroups);
            Check("Hide count badge",      before.AlwaysHideTunnelCount, after.AlwaysHideTunnelCount);
            Check("Default group",         before.DefaultGroup,          after.DefaultGroup);
            Check("Show DNS indicator",    before.ShowDnsIndicator,      after.ShowDnsIndicator);
            Check(Lang.T("SettingsKillSwitchModeTitle"),      before.KillSwitchMode,        after.KillSwitchMode);
            Check(Lang.T("SettingsAutoReconnectModeTitle"),   before.AutoReconnectMode,     after.AutoReconnectMode);
            Check("Font override",         before.FontOverrideEnabled,   after.FontOverrideEnabled);
            Check("Font family",           before.FontOverrideFamily,    after.FontOverrideFamily);
            Check("Font size",             before.FontOverrideSize,      after.FontOverrideSize);
            Check("Update frequency",      before.UpdateCheckFrequency,  after.UpdateCheckFrequency);

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

        // ── History tab ───────────────────────────────────────────────────────

        private void RefreshHistoryTab()
        {
            // Sync chart option controls (suppress _loading guard - no draft needed for immediate-apply settings)
            _loading = true;
            if (ChartRange24h  != null) ChartRange24h.IsChecked  = _draft.InfoTimeRangeDays == 1;
            if (ChartRange7d   != null) ChartRange7d.IsChecked   = _draft.InfoTimeRangeDays == 7;
            if (ChartRange31d  != null) ChartRange31d.IsChecked  = _draft.InfoTimeRangeDays == 31;
            if (StoreWifiHistoryToggle       != null) StoreWifiHistoryToggle.IsChecked       = _draft.StoreWifiHistory;
            if (ShowWifiInChartToggle        != null) ShowWifiInChartToggle.IsChecked        = _draft.ShowWifiInChart;
            if (StoreDnsHistoryToggle        != null) StoreDnsHistoryToggle.IsChecked        = _draft.StoreDnsHistory;
            if (ShowDnsInChartToggle         != null) ShowDnsInChartToggle.IsChecked         = _draft.ShowDnsInChart;
            if (ShowTimelineToggle           != null) ShowTimelineToggle.IsChecked           = _draft.ShowTimeline;
            if (StoreConnectionHistoryToggle != null) StoreConnectionHistoryToggle.IsChecked = _draft.StoreConnectionHistory;
            _loading = false;
            UpdateTimelineShowEnabled();

            var entries = _main.HistorySvc.Entries;
            var items   = entries
                .Select(e => new ViewModels.HistoryEntryViewModel(e))
                .ToList();

            HistoryList.ItemsSource = items;

            int total = entries.Count;
            HistoryCountLabel.Text = total == 0
                ? Lang.T("HistoryEmpty")
                : Lang.T("HistoryCount", total);
        }

        private void StoreWifiHistory_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.StoreWifiHistory = StoreWifiHistoryToggle?.IsChecked == true;
            _main.ConfigSvc.Config.StoreWifiHistory = _draft.StoreWifiHistory;
            _main.ConfigSvc.Save();
            UpdateTimelineShowEnabled();
            _main.ApplyInfoSectionMode();
        }

        private void InfoTimeRange_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.InfoTimeRangeDays =
                ChartRange31d?.IsChecked == true ? 31 :
                ChartRange7d?.IsChecked  == true ?  7 : 1;
            _main.ConfigSvc.Config.InfoTimeRangeDays = _draft.InfoTimeRangeDays;
            _main.ConfigSvc.Save();
            _main.ApplyInfoSectionMode();
        }

        private void ShowWifiInChart_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.ShowWifiInChart = ShowWifiInChartToggle?.IsChecked == true;
            _main.ConfigSvc.Config.ShowWifiInChart = _draft.ShowWifiInChart;
            _main.ConfigSvc.Save();
            _main.ApplyInfoSectionMode();
        }

        private void StoreDnsHistory_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.StoreDnsHistory = StoreDnsHistoryToggle?.IsChecked == true;
            _main.ConfigSvc.Config.StoreDnsHistory = _draft.StoreDnsHistory;
            _main.ConfigSvc.Save();
            _main.ApplyInfoSectionMode();
        }

        private void ShowDnsInChart_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loading) return;
            _draft.ShowDnsInChart = ShowDnsInChartToggle?.IsChecked == true;
            _main.ConfigSvc.Config.ShowDnsInChart = _draft.ShowDnsInChart;
            _main.ConfigSvc.Save();
            _main.ApplyInfoSectionMode();
        }

        private void ClearHistory_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            _main.HistorySvc.Clear();
            RefreshHistoryTab();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            // Always stop any running preview timers - regardless of save/cancel.
            _themePreviewTimer?.Stop();
            _themePreviewTimer     = null;
            _themePreviewActive    = false;
            _themePreviewSourceBtn = null;
            _fontPreviewTimer?.Stop();
            _fontPreviewTimer   = null;
            _fontPreviewActive  = false;
            // If closed without saving, revert any live previews.
            if (!_savedSuccessfully)
            {
                // The System theme mode (Dark/Light/Follow system) applies live as soon as
                // it's changed (see SystemMode_Changed) - if that's still unsaved when the
                // window closes, ask whether to keep it instead of silently discarding it.
                bool systemModeChanged = _draft.SystemThemeMode != _main.ConfigSvc.Config.SystemThemeMode;
                if (systemModeChanged && ThemedMessageDialog.Confirm(this,
                        "You changed the appearance mode (Dark/Light/Follow system) without saving.\n\n" +
                        "Keep this change?", "Unsaved appearance change"))
                {
                    CommitDraft();   // saves everything staged, matching the Save button
                    return;
                }

                // Revert theme + font to last saved state.
                _main.ApplyThemeFromConfig();
                // Revert log visibility to what was committed before Settings opened.
                _main.SetLogPanelVisible(_main.ConfigSvc.Config.ShowActivityLog);
            }
        }

    }
}