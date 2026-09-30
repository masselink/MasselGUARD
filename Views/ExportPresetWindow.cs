using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MasselGUARD.Views
{
    /// <summary>
    /// Asks for a policy name and which settings to LOCK - grouped by section, each item
    /// individually checkable, with a section header that selects/clears its items. The exported
    /// <c>.masselguard</c> always contains all settings (for import); the ticked items go under
    /// <c>Locked.settings</c> and are what a preset enforces. Labels are lang keys.
    /// </summary>
    internal sealed class ExportPresetWindow : Window
    {
        public string       ResultPolicyName { get; private set; } = "";
        public List<string> ResultSettings   { get; private set; } = new();

        // Section label → items (AppConfig property name, label). Property names must match PolicyFields.
        private static readonly (string Section, (string Prop, string Label)[] Items)[] Groups =
        {
            ("PresetSecGeneral", new[] {
                ("Mode", "PresetItemMode"),
                ("Language", "PresetItemLanguage"),
                ("StartWithWindows", "PresetItemStartWithWindows"),
                ("ConfirmOnClose", "PresetItemConfirmOnClose"),
            }),
            ("PresetSecAutomation", new[] {
                ("ManualMode", "PresetItemManualMode"),
                ("DefaultAction", "PresetItemDefaultAction"),
                ("DefaultTunnel", "PresetItemDefaultTunnel"),
                ("OpenWifiTunnel", "PresetItemOpenWifiTunnel"),
                ("TrustedNetworks", "PresetItemTrustedNetworks"),
                ("Rules", "PresetItemRules"),
            }),
            ("WireGuard", new[] {
                ("AutoReconnectMode", "PresetItemAutoReconnectMode"),
                ("KillSwitchMode", "PresetItemKillSwitchMode"),
                ("SkipTunnelValidation", "PresetItemSkipTunnelValidation"),
            }),
            ("PresetSecDnsLeak", new[] {
                ("ShowDnsIndicator", "PresetItemShowDnsIndicator"),
                ("DnsLeakWarnLog", "PresetItemDnsLeakWarnLog"),
                ("DnsLeakWarnToast", "PresetItemDnsLeakWarnToast"),
            }),
            ("PresetSecNotifications", new[] {
                ("ShowTrayPopupOnSwitch", "PresetItemShowTrayPopupOnSwitch"),
                ("NotificationDurationSeconds", "PresetItemNotificationDurationSeconds"),
            }),
            ("PresetSecAppearance", new[] {
                ("ActiveTheme", "PresetItemActiveTheme"),
                ("SystemThemeMode", "PresetItemSystemThemeMode"),
                ("SharedThemesRepoUrl", "PresetItemSharedThemesRepoUrl"),
            }),
            ("PresetSecUpdates", new[] {
                ("UpdateCheckFrequency", "PresetItemUpdateCheckFrequency"),
                ("WireGuardInstallDirectory", "PresetItemWireGuardInstallDirectory"),
            }),
            ("PresetSecDisplay", new[] {
                ("ShowWifiRulesOnMainWindow", "PresetItemShowWifiRulesOnMainWindow"),
                ("ShowTunnelRulesColumn", "PresetItemShowTunnelRulesColumn"),
                ("ShowActivityLog", "PresetItemShowActivityLog"),
                ("ShowTimeline", "PresetItemShowTimeline"),
            }),
        };

        public ExportPresetWindow()
        {
            Brush Res(string key) => (Application.Current.Resources[key] as Brush) ?? Brushes.Gray;
            var ff = Application.Current.Resources["Theme.FontFamily"] as FontFamily ?? new FontFamily("Segoe UI");

            WindowStyle           = WindowStyle.None;
            AllowsTransparency    = true;
            Background            = Brushes.Transparent;
            Width                 = 440;
            SizeToContent         = SizeToContent.Height;
            ResizeMode            = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var border = new Border
            {
                Background      = Res("WindowBg"),
                BorderBrush     = Res("Accent"),
                BorderThickness = new Thickness(1),
                CornerRadius    = Application.Current.Resources["Theme.CornerRadius"] is CornerRadius cr ? cr : new CornerRadius(6),
                Padding         = new Thickness(20),
            };
            var panel = new StackPanel();

            var title = new TextBlock
            {
                Text       = Lang.T("PresetExportTitle"),
                FontFamily = ff, FontSize = 13, FontWeight = FontWeights.Bold,
                Foreground = Res("Accent"), Margin = new Thickness(0, 0, 0, 10),
            };
            title.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
            panel.Children.Add(title);

            panel.Children.Add(new TextBlock
            {
                Text = Lang.T("PresetExportHint"),
                FontFamily = ff, FontSize = 10, Foreground = Res("TextMuted"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            });

            panel.Children.Add(new TextBlock
            {
                Text = Lang.T("PresetPolicyNameLabel"),
                FontFamily = ff, FontSize = 10, Foreground = Res("TextMuted"),
                Margin = new Thickness(0, 0, 0, 4),
            });
            var nameBox = new TextBox { FontFamily = ff, FontSize = 12, Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(nameBox);

            panel.Children.Add(new TextBlock
            {
                Text = Lang.T("PresetExportLockLabel"),
                FontFamily = ff, FontSize = 10, Foreground = Res("TextMuted"),
                Margin = new Thickness(0, 0, 0, 4),
            });

            var itemChecks = new List<CheckBox>();
            var listPanel  = new StackPanel();
            foreach (var (section, items) in Groups)
            {
                var sectionItems = new List<CheckBox>();
                var header = new CheckBox
                {
                    Content    = section == "WireGuard" ? section : Lang.T(section),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Res("TextPrimary"),
                    FontFamily = ff, FontSize = 11,
                    Margin     = new Thickness(0, 6, 0, 2),
                };
                header.Checked   += (_, _) => { foreach (var c in sectionItems) c.IsChecked = true; };
                header.Unchecked += (_, _) => { foreach (var c in sectionItems) c.IsChecked = false; };
                listPanel.Children.Add(header);

                foreach (var (prop, label) in items)
                {
                    var cb = new CheckBox
                    {
                        Content    = Lang.T(label),
                        Tag        = prop,
                        Foreground = Res("TextMuted"),
                        FontFamily = ff, FontSize = 10,
                        Margin     = new Thickness(18, 1, 0, 1),
                    };
                    sectionItems.Add(cb);
                    itemChecks.Add(cb);
                    listPanel.Children.Add(cb);
                }
            }
            panel.Children.Add(new ScrollViewer
            {
                Content = listPanel,
                MaxHeight = 300,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            });

            var hint = new TextBlock
            {
                FontFamily = ff, FontSize = 9, Foreground = Res("WarningColor"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            panel.Children.Add(hint);

            var btns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0),
            };
            var cancel = new Button
            {
                Content = Lang.T("BtnCancel"),
                Style   = Application.Current.Resources["FlatBtn"] as Style,
                Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0),
            };
            var ok = new Button
            {
                Content = Lang.T("BtnOk"),
                Style   = Application.Current.Resources["PrimaryBtn"] as Style,
                Padding = new Thickness(14, 6, 14, 6),
            };
            cancel.Click += (_, _) => { DialogResult = false; };
            ok.Click += (_, _) =>
            {
                var name = nameBox.Text.Trim();
                var sel  = itemChecks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();
                if (string.IsNullOrEmpty(name)) { hint.Text = Lang.T("PresetExportNeedName");  hint.Visibility = Visibility.Visible; nameBox.Focus(); return; }
                if (sel.Count == 0)             { hint.Text = Lang.T("PresetExportNeedBlock"); hint.Visibility = Visibility.Visible; return; }
                ResultPolicyName = name;
                ResultSettings   = sel;
                DialogResult     = true;
            };
            btns.Children.Add(cancel);
            btns.Children.Add(ok);
            panel.Children.Add(btns);

            border.Child = panel;
            Content = border;
            Loaded += (_, _) => nameBox.Focus();
        }
    }
}
