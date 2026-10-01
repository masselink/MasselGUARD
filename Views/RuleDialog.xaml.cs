using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using MasselGUARD.Models;
using MasselGUARD.Services;

namespace MasselGUARD.Views
{
    public partial class RuleDialog : Window
    {
        public string ResultName   { get; private set; } = "";
        /// <summary>The conditions of a network rule (all must hold), values in canonical form. Empty for
        /// schedule / trusted rules. Apply with <see cref="TunnelRule.SetConditions"/>.</summary>
        public List<RuleCondition> ResultConditions { get; private set; } = new();
        public string ResultTunnel { get; private set; } = "";
        /// <summary>"network" | "schedule" | "trusted" - which trigger type the user chose.</summary>
        public string ResultKind      { get; private set; } = "network";
        /// <summary>For a trusted rule: "untrusted" (activate off-list) or "trusted" (activate on-list).</summary>
        public string ResultTrustedWhen { get; private set; } = "untrusted";
        public string ResultStartTime { get; private set; } = "09:00";
        public string ResultEndTime   { get; private set; } = "17:00";
        public List<int> ResultDays   { get; private set; } = new();
        /// <summary>DNS profile id to apply on this rule ("" = none, <see cref="MasselGUARD.Models.DnsProfile.AutomaticId"/> = DHCP).</summary>
        public string ResultDnsProfileId { get; private set; } = "";
        /// <summary>
        /// New counter value to persist. -1 = no change; 0 = cleared; any positive = new value.
        /// </summary>
        public int ResultNewCounterValue { get; private set; } = -1;

        private readonly string? _currentSsid;
        private bool _nameManuallyEdited = false;
        private int  _displayCount;

        public RuleDialog(string? currentSsid,
                          string existingName   = "",
                          string existingSsid   = "",
                          string existingTunnel = "",
                          int    executionCount = -1,   // -1 = add mode (counter hidden)
                          List<string>? tunnels = null,
                          string existingKind   = "wifi",
                          string existingStart  = "09:00",
                          string existingEnd    = "17:00",
                          List<int>? existingDays = null,
                          string existingTrustedWhen = "untrusted",
                          List<(string id, string name)>? dnsProfiles = null,
                          string existingDnsProfileId = "",
                          bool dnsEnabled = true,
                          bool tunnelsEnabled = true,
                          IReadOnlyList<RuleCondition>? existingConditions = null,
                          Func<NetworkSnapshot>? captureNetwork = null,
                          Func<IReadOnlyList<NetworkIdentity>>? listAdapters = null,
                          Func<IReadOnlyList<WifiHistoryEntry>>? recentNetworks = null,
                          bool simpleMode = false)
        {
            InitializeComponent();
            LocalizeDayButtons();
            _captureNetwork = captureNetwork;
            _listAdapters   = listAdapters;
            _recentNetworks = recentNetworks;

            // Condition rows: the rule's own (edit), or one empty row (add).
            _loadingConditions = true;
            if (existingConditions != null && existingConditions.Count > 0)
                foreach (var c in existingConditions) AddConditionRow(c);
            else
                AddConditionRow(null);
            _loadingConditions = false;

            // Simple Wi-Fi mode: a new rule (or an existing plain SSID rule) is just "SSID is X". A rule that
            // already has more than that is edited with the full controls so it can still be changed or fixed.
            bool plainSsid = existingConditions == null || existingConditions.Count == 0
                || (existingConditions.Count == 1 && !existingConditions[0].Not && existingConditions[0].By == NetworkMatchBy.Ssid);
            if (simpleMode && plainSsid)
            {
                var row = _rows[0];
                row.Root.ColumnDefinitions[0].Width = new GridLength(0);   // hide "is / is not"
                row.Root.ColumnDefinitions[1].Width = new GridLength(0);   // hide the match-by list: SSID only
                row.OpBox.Visibility = Visibility.Collapsed;
                row.ByBox.Visibility = Visibility.Collapsed;
                row.RemoveBtn.Visibility = Visibility.Collapsed;
                AddConditionBtn.Visibility = Visibility.Collapsed;
                CondHintText.Visibility    = Visibility.Collapsed;
                SimpleModeNote.Visibility  = Visibility.Visible;
            }

            // Hide the DNS picker when the DNS module is off (tunnels-only); hide the tunnel
            // picker when the tunnel module is off (DNS-only → the rule is trigger → DNS).
            if (!dnsEnabled)     DnsPickerPanel.Visibility    = Visibility.Collapsed;
            if (!tunnelsEnabled) TunnelPickerPanel.Visibility = Visibility.Collapsed;
            _currentSsid  = currentSsid;
            _displayCount = executionCount;

            // Populate tunnel dropdown
            TunnelBox.Items.Clear();
            TunnelBox.Items.Add("");   // blank = disconnect
            if (tunnels != null)
                foreach (var t in tunnels) TunnelBox.Items.Add(t);

            // Populate DNS profile dropdown (none / automatic / each profile). Tag carries the id.
            DnsProfileBox.Items.Clear();
            DnsProfileBox.Items.Add(new ComboBoxItem { Content = Lang.T("DnsProfileNone"),      Tag = MasselGUARD.Models.DnsProfile.NoneId });
            DnsProfileBox.Items.Add(new ComboBoxItem { Content = Lang.T("DnsProfileAutomatic"), Tag = MasselGUARD.Models.DnsProfile.AutomaticId });
            if (dnsProfiles != null)
                foreach (var (id, name) in dnsProfiles)
                    DnsProfileBox.Items.Add(new ComboBoxItem { Content = name, Tag = id });
            DnsProfileBox.SelectedIndex = 0;
            foreach (ComboBoxItem it in DnsProfileBox.Items)
                if ((it.Tag as string) == existingDnsProfileId) { DnsProfileBox.SelectedItem = it; break; }

            bool hasConditions = existingConditions is { Count: > 0 };
            bool editMode = existingKind == "schedule" || existingKind == "trusted" || hasConditions;

            if (hasConditions)
            {
                _nameManuallyEdited = !string.IsNullOrEmpty(existingName);
                NameBox.Text        = existingName;
            }

            // Schedule fields
            if (!string.IsNullOrEmpty(existingStart)) StartTimeBox.Text = existingStart;
            if (!string.IsNullOrEmpty(existingEnd))   EndTimeBox.Text   = existingEnd;
            if (existingDays != null) SetDayToggles(existingDays);

            if (existingKind == "schedule")
            {
                _nameManuallyEdited = !string.IsNullOrEmpty(existingName);
                NameBox.Text        = existingName;
                TypeScheduleRadio.IsChecked = true;   // fires RuleType_Changed → shows SchedulePanel
            }
            else if (existingKind == "trusted")
            {
                _nameManuallyEdited = !string.IsNullOrEmpty(existingName);
                NameBox.Text        = existingName;
                TypeTrustedRadio.IsChecked = true;    // fires RuleType_Changed → shows TrustedPanel
                if (string.Equals(existingTrustedWhen, "trusted", System.StringComparison.OrdinalIgnoreCase))
                    TrustedWhenTrustedRadio.IsChecked = true;
                else
                    TrustedWhenUntrustedRadio.IsChecked = true;
            }

            if (editMode) DialogTitle.Text = Lang.T("RuleDialogEditTitle");

            TunnelBox.Text = existingTunnel;

            // Show trigger counter only in edit mode
            if (executionCount >= 0)
            {
                CounterRow.Visibility = Visibility.Visible;
                UpdateCountLabel();
            }

            NameBox.Focus();
        }

        private void UpdateCountLabel()
        {
            if (TriggerCountLabel == null) return;
            TriggerCountLabel.Text = _displayCount == 1
                ? Lang.T("RuleDialogTriggerCountSingular")
                : Lang.T("RuleDialogTriggerCount", _displayCount);
        }

        private void SetCounter_Click(object sender, RoutedEventArgs e)
        {
            // Build a small themed input dialog inline (same pattern as ShowThemedYesNo)
            int? result = ShowCounterInputDialog(_displayCount);
            if (result == null) return;   // cancelled

            _displayCount        = result.Value;
            ResultNewCounterValue = result.Value;
            UpdateCountLabel();
        }

        /// <summary>
        /// Shows a themed modal input dialog for editing the counter value.
        /// Returns the entered value, or null if cancelled.
        /// </summary>
        private int? ShowCounterInputDialog(int currentValue)
        {
            Brush Res(string key) =>
                (Application.Current.Resources[key] as Brush)
                ?? Brushes.Gray;

            var ff = Application.Current.Resources["Theme.FontFamily"] as FontFamily
                     ?? new FontFamily("Segoe UI");

            int? result = null;

            var win = new Window
            {
                WindowStyle           = WindowStyle.None,
                AllowsTransparency    = true,
                Background            = Brushes.Transparent,
                Width                 = 320,
                SizeToContent         = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = this,
                ResizeMode            = ResizeMode.NoResize,
            };

            var border = new Border
            {
                Background      = Res("WindowBg"),
                BorderBrush     = Res("Accent"),
                BorderThickness = new Thickness(1),
                CornerRadius    = Application.Current.Resources["Theme.CornerRadius"] is CornerRadius cr ? cr : new CornerRadius(6),
                Padding         = new Thickness(20),
            };

            var panel = new StackPanel();

            // Title
            panel.Children.Add(new TextBlock
            {
                Text       = Lang.T("RuleDialogSetCounterTitle"),
                FontFamily = ff, FontSize = 12, FontWeight = FontWeights.Bold,
                Foreground = Res("TextPrimary"),
                Margin     = new Thickness(0, 0, 0, 8),
            });

            // Hint
            panel.Children.Add(new TextBlock
            {
                Text         = Lang.T("RuleDialogSetCounterHint"),
                FontFamily   = ff, FontSize = 10,
                Foreground   = Res("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 12),
            });

            // TextBox - pre-filled with current count, digits only
            var input = new TextBox
            {
                Text              = currentValue.ToString(),
                FontFamily        = ff, FontSize = 13,
                MaxLength         = 7,
                SelectionStart    = 0,
                SelectionLength   = currentValue.ToString().Length,
                Margin            = new Thickness(0, 0, 0, 16),
            };
            input.PreviewTextInput += (_, te) =>
                te.Handled = !System.Linq.Enumerable.All(te.Text, char.IsDigit);
            panel.Children.Add(input);

            // Buttons row
            var btns = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var btnCancel = new Button
            {
                Content = Lang.T("BtnCancel"),
                Style   = (Style)Application.Current.Resources["FlatBtn"],
                Padding = new Thickness(14, 6, 14, 6),
                Margin  = new Thickness(0, 0, 8, 0),
            };
            var btnApply = new Button
            {
                Content = Lang.T("BtnOk"),
                Style   = (Style)Application.Current.Resources["PrimaryBtn"],
                Padding = new Thickness(14, 6, 14, 6),
            };

            void Apply()
            {
                var text = input.Text.Trim();
                if (!int.TryParse(text, out var val) || val < 0) return;
                result = val;
                win.Close();
            }

            btnCancel.Click   += (_, _) => win.Close();
            btnApply.Click    += (_, _) => Apply();
            input.KeyDown     += (_, ke) => { if (ke.Key == Key.Return) Apply(); };

            btns.Children.Add(btnCancel);
            btns.Children.Add(btnApply);
            panel.Children.Add(btns);

            border.Child = panel;
            win.Content  = border;

            // Select all text when the dialog opens
            win.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };

            win.ShowDialog();
            return result;
        }

        /// <summary>Auto-generate name from SSID and tunnel unless user has typed one.</summary>
        /// <param name="tunnelOverride">
        /// Tunnel value to use instead of <c>TunnelBox.Text</c>. Needed when called from the
        /// ComboBox's SelectionChanged handler, where <c>TunnelBox.Text</c> still holds the
        /// previous value (editable ComboBoxes update Text only after the event completes) -
        /// passing the freshly-selected item avoids regenerating a stale "→ disconnect" name.
        /// </param>
        private void AutoGenerateName(string? tunnelOverride = null)
        {
            if (_nameManuallyEdited) return;
            // Controls may not exist yet if an initial IsChecked fires during InitializeComponent.
            if (_rows == null || TunnelBox == null || NameBox == null) return;
            // Schedule and trusted rules derive their name from the rule, not an SSID.
            if (TypeScheduleRadio?.IsChecked == true) return;
            if (TypeTrustedRadio?.IsChecked  == true) return;
            var ssid   = ConditionNamePart();
            var tunnel = (tunnelOverride ?? TunnelBox.Text).Trim();
            string generated;
            if (string.IsNullOrEmpty(ssid))
                generated = "";
            else if (string.IsNullOrEmpty(tunnel))
                generated = $"{ssid} → disconnect";
            else
                generated = $"{ssid} → {tunnel}";

            NameBox.TextChanged -= NameBox_TextChanged;
            NameBox.Text = generated;
            NameBox.TextChanged += NameBox_TextChanged;
        }

        private void NameBox_TextChanged(object sender,
            System.Windows.Controls.TextChangedEventArgs e)
            => _nameManuallyEdited = !string.IsNullOrEmpty(NameBox.Text);

        private void TunnelBox_Changed(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            // On an editable ComboBox, TunnelBox.Text still holds the previous value while
            // SelectionChanged fires. Read the freshly-selected item so the auto-generated
            // name reflects the tunnel just picked, not a stale "→ disconnect".
            string tunnel = e.AddedItems.Count > 0
                ? (e.AddedItems[0] as string ?? "")
                : (TunnelBox.SelectedItem as string ?? "");
            AutoGenerateName(tunnel);
        }

        // ── Network conditions: rows of [is / is not] [SSID / DNS suffix / gateway MAC / subnet] [value] ───

        private readonly Func<NetworkSnapshot>? _captureNetwork;
        private readonly Func<IReadOnlyList<NetworkIdentity>>? _listAdapters;
        private readonly Func<IReadOnlyList<WifiHistoryEntry>>? _recentNetworks;
        private readonly List<CondRow> _rows = new();
        private bool _loadingConditions;

        /// <summary>One editable condition row.</summary>
        private sealed class CondRow
        {
            public Grid Root = null!;
            public ComboBox OpBox = null!, ByBox = null!, TypeBox = null!;
            public TextBox ValueBox = null!;
            public Button FetchBtn = null!, RemoveBtn = null!;
            public string By => (ByBox.SelectedItem as ComboBoxItem)?.Tag as string ?? NetworkMatchBy.Ssid;
            public bool Not   => (OpBox.SelectedItem as ComboBoxItem)?.Tag is true;
            public bool IsType => By == NetworkMatchBy.ConnectionType;
            /// <summary>What the user entered: the typed text, or "wifi" / "wired" for a connection-type row.</summary>
            public string Raw => IsType ? (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "wifi" : ValueBox.Text.Trim();
            /// <summary>The same, as shown to the user (for the automatic rule name).</summary>
            public string Display => IsType ? (TypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "" : ValueBox.Text.Trim();
        }

        private static string HintKey(string by) => by switch
        {
            NetworkMatchBy.DnsSuffix  => "RuleMatchHintSuffix",
            NetworkMatchBy.GatewayMac => "RuleMatchHintMac",
            NetworkMatchBy.Subnet     => "RuleMatchHintSubnet",
            NetworkMatchBy.ConnectionType => "RuleMatchHintConnType",
            NetworkMatchBy.AdapterName => "RuleMatchHintAdapterName",
            NetworkMatchBy.AdapterDesc => "RuleMatchHintAdapterDesc",
            NetworkMatchBy.AdapterMac  => "RuleMatchHintAdapterMac",
            _                         => "RuleMatchHintSsid",
        };

        private static string PlaceholderKey(string by) => by switch
        {
            NetworkMatchBy.DnsSuffix  => "RuleMatchValueSuffix",
            NetworkMatchBy.GatewayMac => "RuleMatchValueMac",
            NetworkMatchBy.Subnet     => "RuleMatchValueSubnet",
            NetworkMatchBy.ConnectionType => "DiagConnType",
            NetworkMatchBy.AdapterName => "DiagAdapterName",
            NetworkMatchBy.AdapterDesc => "DiagAdapterDesc",
            NetworkMatchBy.AdapterMac  => "DiagAdapterMac",
            _                         => "RuleDialogSsidLabel",
        };

        private void AddConditionRow(RuleCondition? init)
        {
            var row = new CondRow();
            row.Root = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
            row.Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.OpBox = new ComboBox { Margin = new Thickness(0, 0, 6, 0) };
            row.OpBox.Items.Add(new ComboBoxItem { Content = Lang.T("RuleCondIs"),    Tag = false });
            row.OpBox.Items.Add(new ComboBoxItem { Content = Lang.T("RuleCondIsNot"), Tag = true  });
            row.OpBox.SelectedIndex = init?.Not == true ? 1 : 0;

            row.ByBox = new ComboBox { Margin = new Thickness(0, 0, 6, 0) };
            foreach (var (by, key) in new[]
            {
                (NetworkMatchBy.Ssid, "DiagSsid"), (NetworkMatchBy.DnsSuffix, "DiagDnsSuffix"),
                (NetworkMatchBy.GatewayMac, "DiagGatewayMac"), (NetworkMatchBy.Subnet, "DiagSubnet"),
                (NetworkMatchBy.ConnectionType, "DiagConnType"),
                (NetworkMatchBy.AdapterName, "DiagAdapterName"), (NetworkMatchBy.AdapterDesc, "DiagAdapterDesc"),
                (NetworkMatchBy.AdapterMac, "DiagAdapterMac"),
            })
                row.ByBox.Items.Add(new ComboBoxItem { Content = Lang.T(key), Tag = by });
            string startBy = init != null && NetworkMatchBy.IsKnown(init.By) ? init.By : NetworkMatchBy.Ssid;
            row.ByBox.SelectedIndex = startBy switch
            {
                NetworkMatchBy.DnsSuffix => 1, NetworkMatchBy.GatewayMac => 2, NetworkMatchBy.Subnet => 3,
                NetworkMatchBy.ConnectionType => 4, NetworkMatchBy.AdapterName => 5,
                NetworkMatchBy.AdapterDesc => 6, NetworkMatchBy.AdapterMac => 7, _ => 0,
            };

            bool startType = startBy == NetworkMatchBy.ConnectionType;
            row.ValueBox = new TextBox { Margin = new Thickness(0, 0, 6, 0), Text = startType ? "" : init?.Value ?? "" };
            row.ValueBox.ToolTip = Lang.T(PlaceholderKey(startBy));

            // A connection-type row picks Wi-Fi or Wired from a list instead of typing a value.
            row.TypeBox = new ComboBox { Margin = new Thickness(0, 0, 6, 0) };
            row.TypeBox.Items.Add(new ComboBoxItem { Content = Lang.T("DiagKindWifi"),  Tag = NetworkIdentity.KindWifi });
            row.TypeBox.Items.Add(new ComboBoxItem { Content = Lang.T("DiagKindWired"), Tag = NetworkIdentity.KindWired });
            row.TypeBox.SelectedIndex = startType && NetworkMatcher.NormalizeConnectionType(init?.Value) == NetworkIdentity.KindWired ? 1 : 0;

            row.FetchBtn = new Button
            {
                Content = Lang.T("BtnFetchNetwork"), ToolTip = Lang.T("RuleFetchTip"),
                Style = (Style)Application.Current.Resources["FlatBtn"], FontSize = 10, Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 4, 0),
            };
            row.RemoveBtn = new Button
            {
                Content = "✕", ToolTip = Lang.T("RuleCondRemoveTip"),
                Style = (Style)Application.Current.Resources["FlatBtn"], FontSize = 10, Padding = new Thickness(8, 6, 8, 6),
            };

            Grid.SetColumn(row.OpBox, 0); Grid.SetColumn(row.ByBox, 1); Grid.SetColumn(row.ValueBox, 2);
            Grid.SetColumn(row.TypeBox, 2);
            Grid.SetColumn(row.FetchBtn, 3); Grid.SetColumn(row.RemoveBtn, 4);
            foreach (UIElement el in new UIElement[] { row.OpBox, row.ByBox, row.ValueBox, row.TypeBox, row.FetchBtn, row.RemoveBtn })
                row.Root.Children.Add(el);

            // Show the value box (or the Wi-Fi/Wired list) and the Fetch button that fit the chosen match type.
            void ApplyKind()
            {
                bool type = row.IsType;
                row.ValueBox.Visibility = type ? Visibility.Collapsed : Visibility.Visible;
                row.FetchBtn.Visibility = type ? Visibility.Collapsed : Visibility.Visible;
                row.TypeBox.Visibility  = type ? Visibility.Visible   : Visibility.Collapsed;
            }
            ApplyKind();

            row.ByBox.SelectionChanged += (_, _) =>
            {
                ApplyKind();
                row.ValueBox.ToolTip = Lang.T(PlaceholderKey(row.By));
                if (MatchHint != null) MatchHint.Text = Lang.T(HintKey(row.By));
                if (!_loadingConditions) AutoGenerateName();
            };
            row.TypeBox.SelectionChanged += (_, _) => { if (!_loadingConditions) AutoGenerateName(); };
            row.OpBox.SelectionChanged += (_, _) => { if (!_loadingConditions) AutoGenerateName(); };
            row.ValueBox.TextChanged   += (_, _) => { if (!_loadingConditions) AutoGenerateName(); };
            row.ValueBox.GotKeyboardFocus += (_, _) => { if (MatchHint != null) MatchHint.Text = Lang.T(HintKey(row.By)); };
            row.FetchBtn.Click  += (_, _) => Fetch(row);
            row.RemoveBtn.Click += (_, _) =>
            {
                if (_rows.Count <= 1) return;
                _rows.Remove(row); ConditionsHost.Children.Remove(row.Root);
                UpdateRemoveButtons(); AutoGenerateName();
            };

            _rows.Add(row);
            ConditionsHost.Children.Add(row.Root);
            UpdateRemoveButtons();
            if (MatchHint != null && string.IsNullOrEmpty(MatchHint.Text)) MatchHint.Text = Lang.T(HintKey(row.By));
        }

        private void UpdateRemoveButtons()
        {
            foreach (var r in _rows)
                r.RemoveBtn.Visibility = _rows.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddCondition_Click(object sender, RoutedEventArgs e)
        {
            AddConditionRow(null);
            _rows[^1].ValueBox.Focus();
            AutoGenerateName();
        }

        /// <summary>The values of the conditions joined for the automatic rule name ("Home + NOT 10.0.0.0/8").</summary>
        private string ConditionNamePart() =>
            string.Join(" + ", _rows.Select(r => (r.Not ? "NOT " : "") + r.Display)
                                    .Where(s => s.Length > 0 && s != "NOT "));

        /// <summary>A themed one-button notice owned by this dialog (the system MessageBox ignores the theme).</summary>
        private void Notice(string message, string title)
        {
            if (Owner is MainWindow main) main.ShowThemedInfo(message, title, this);
            else MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>Puts a fetched value into the row. For types that take a list (subnets, device conditions), holding
        /// Shift adds it to the existing list instead of replacing it.</summary>
        private static void ApplyFetched(CondRow row, string value, bool listable)
        {
            string current = row.ValueBox.Text.Trim();
            bool add = listable && current.Length > 0 && (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (!add) { row.ValueBox.Text = value; return; }
            var have = row.By == NetworkMatchBy.Subnet ? NetworkMatcher.SplitCidrs(current) : NetworkMatcher.SplitAlternatives(current);
            if (!have.Contains(value, StringComparer.OrdinalIgnoreCase))
                row.ValueBox.Text = current.TrimEnd(',', ';', ' ') + ", " + value;
        }

        /// <summary>Fetch: read the value of this row's match type from a connected network. One hit fills
        /// the box; several (Wi-Fi + wired, or several subnets) open a small menu to pick from.</summary>
        private async void Fetch(CondRow row)
        {
            string by = row.By;
            NetworkSnapshot? snap = null;
            if (_captureNetwork != null)
            {
                row.FetchBtn.IsEnabled = false;
                try { snap = await System.Threading.Tasks.Task.Run(_captureNetwork); }
                catch { /* fall back to the SSID the window already knows */ }
                finally { row.FetchBtn.IsEnabled = true; }
            }

            var entries = new List<FetchEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // a value is offered once

            void AddGroup(string header, List<FetchEntry> group)
            {
                if (!group.Any(e => e.Kind == FetchEntryKind.Item)) return;
                if (entries.Count > 0) entries.Add(FetchEntry.Separator());
                entries.Add(FetchEntry.Header(header));
                entries.AddRange(group);
            }

            bool deviceType = by is NetworkMatchBy.AdapterName or NetworkMatchBy.AdapterDesc or NetworkMatchBy.AdapterMac;
            var connected   = snap?.Adapters.OrderByDescending(x => x.IsPrimary).ToList() ?? new List<NetworkIdentity>();

            if (deviceType)
            {
                // The device conditions describe hardware, so offer EVERY adapter of this PC, not only the
                // connected one: a docking-station card that is unplugged right now is still a valid rule target.
                string Sub(NetworkIdentity a) => by switch
                {
                    NetworkMatchBy.AdapterName => a.AdapterDescription ?? "",
                    NetworkMatchBy.AdapterDesc => a.AdapterName,
                    _                          => $"{a.AdapterName}  {a.AdapterDescription}".Trim(),
                };
                List<FetchEntry> Rows(IEnumerable<NetworkIdentity> adapters, bool primaryTag)
                {
                    var rows = new List<FetchEntry>();
                    foreach (var a in adapters)
                        foreach (var v in NetworkMatcher.ValuesFor(a, by))
                            if (seen.Add(v))
                                rows.Add(FetchEntry.Item(v, v, Sub(a) is { Length: > 0 } s && !s.Equals(v, StringComparison.OrdinalIgnoreCase) ? s : null,
                                                         emphasis: primaryTag && a.IsPrimary));
                    return rows;
                }
                AddGroup(Lang.T("FetchConnectedNow"), Rows(connected, true));
                var connectedIds = new HashSet<string>(connected.Select(a => a.AdapterId), StringComparer.OrdinalIgnoreCase);
                var others = (_listAdapters?.Invoke() ?? Array.Empty<NetworkIdentity>()).Where(a => !connectedIds.Contains(a.AdapterId));
                AddGroup(Lang.T("FetchOtherAdapters"), Rows(others, false));
            }
            else
            {
                // One group per connected network: its name as a header, then what it offers for this match type.
                foreach (var a in connected)
                {
                    var values = NetworkMatcher.ValuesFor(a, by);
                    if (values.Count == 0) continue;
                    var group = new List<FetchEntry>();
                    if (by == NetworkMatchBy.Subnet) group.AddRange(FetchMenu.SubnetEntries(values));
                    else foreach (var v in values) group.Add(FetchEntry.Item(v, v));
                    foreach (var v in values) seen.Add(v);
                    AddGroup($"{a.AdapterName}{(a.IsPrimary ? $" ({Lang.T("DiagPrimaryTag")})" : "")}", group);
                }
                if (!entries.Any(e => e.Kind == FetchEntryKind.Item) && by == NetworkMatchBy.Ssid && !string.IsNullOrEmpty(_currentSsid))
                { entries.Add(FetchEntry.Item(_currentSsid!, _currentSsid!)); seen.Add(_currentSsid!); }

                // Recently connected networks (from the history): SSIDs, DNS suffixes and gateway MACs seen before.
                var recent = _recentNetworks?.Invoke() ?? Array.Empty<WifiHistoryEntry>();
                IEnumerable<(string value, string sub)> past = by switch
                {
                    NetworkMatchBy.Ssid       => recent.Where(e => !e.IsWired && !string.IsNullOrEmpty(e.Ssid)).Select(e => (e.Ssid, e.ConnectedAt.ToLocalTime().ToString("yyyy-MM-dd"))),
                    NetworkMatchBy.DnsSuffix  => recent.Where(e => !string.IsNullOrEmpty(e.DnsSuffix)).Select(e => (e.DnsSuffix!, e.Ssid)),
                    NetworkMatchBy.GatewayMac => recent.Where(e => !string.IsNullOrEmpty(e.GatewayMac)).Select(e => (e.GatewayMac!, e.Ssid)),
                    _                         => Array.Empty<(string, string)>(),
                };
                AddGroup(Lang.T("FetchRecent"),
                    past.Where(p => seen.Add(p.value)).Take(10)
                        .Select(p => FetchEntry.Item(p.value, p.value, string.IsNullOrEmpty(p.sub) ? null : p.sub)).ToList());
            }

            var items = entries.Where(e => e.Kind == FetchEntryKind.Item).ToList();
            if (items.Count == 0)
            {
                Notice(Lang.T("RuleFetchNone"), Lang.T("BtnFetchNetwork"));
                return;
            }
            bool listable = deviceType || by == NetworkMatchBy.Subnet;
            if (items.Count == 1) { ApplyFetched(row, items[0].Value, listable); return; }

            if (listable) { entries.Add(FetchEntry.Separator()); entries.Add(FetchEntry.Section(Lang.T("FetchShiftHint"))); }
            FetchMenu.Show(row.FetchBtn, entries, v => ApplyFetched(row, v, listable));
        }

        /// <summary>Weekday buttons show the UI language's abbreviated day names (Tag = DayOfWeek).</summary>
        private void LocalizeDayButtons()
        {
            var names = Lang.Culture.DateTimeFormat.AbbreviatedDayNames;
            foreach (var b in new[] { DayMon, DayTue, DayWed, DayThu, DayFri, DaySat, DaySun })
                if (int.TryParse(b.Tag as string, out var d) && d is >= 0 and < 7) b.Content = names[d];
        }

        private void RuleType_Changed(object sender, RoutedEventArgs e)
        {
            bool schedule = TypeScheduleRadio?.IsChecked == true;
            bool trusted  = TypeTrustedRadio?.IsChecked  == true;
            bool wifi     = !schedule && !trusted;

            if (SsidPanel     != null) SsidPanel.Visibility     = wifi     ? Visibility.Visible : Visibility.Collapsed;
            if (SchedulePanel != null) SchedulePanel.Visibility = schedule ? Visibility.Visible : Visibility.Collapsed;
            if (TrustedPanel  != null) TrustedPanel.Visibility  = trusted  ? Visibility.Visible : Visibility.Collapsed;
            AutoGenerateName();
        }

        private List<ToggleButton> DayToggles() =>
            new() { DayMon, DayTue, DayWed, DayThu, DayFri, DaySat, DaySun };

        private void SetDayToggles(List<int> days)
        {
            foreach (var tb in DayToggles())
                tb.IsChecked = days.Contains(int.Parse((string)tb.Tag));
        }

        private List<int> GatherDays() =>
            DayToggles().Where(t => t.IsChecked == true)
                        .Select(t => int.Parse((string)t.Tag))
                        .OrderBy(d => d).ToList();

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            bool schedule = TypeScheduleRadio?.IsChecked == true;
            bool trusted  = TypeTrustedRadio?.IsChecked  == true;
            ResultKind   = schedule ? "schedule" : trusted ? "trusted" : "network";
            ResultTunnel = TunnelBox.Text.Trim();
            ResultDnsProfileId = (DnsProfileBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

            if (trusted)
            {
                // The trusted-SSID list itself lives in Settings; the rule carries the
                // direction (on-list vs off-list) and the tunnel to bring up on its side
                // (an empty tunnel = disconnect, consistent with the other rule kinds).
                ResultTrustedWhen = TrustedWhenTrustedRadio?.IsChecked == true ? "trusted" : "untrusted";
                ResultConditions = new();
                ResultName   = NameBox.Text.Trim();   // empty → RuleName auto-summarises
                DialogResult = true;
                return;
            }

            if (schedule)
            {
                if (!System.TimeSpan.TryParse(StartTimeBox.Text.Trim(), out _) ||
                    !System.TimeSpan.TryParse(EndTimeBox.Text.Trim(), out _))
                {
                    Notice(Lang.T("RuleDialogTimeInvalid"), Lang.T("RuleDialogValidationTitle"));
                    return;
                }
                var days = GatherDays();
                if (days.Count == 0)
                {
                    Notice(Lang.T("RuleDialogDaysRequired"), Lang.T("RuleDialogValidationTitle"));
                    return;
                }
                ResultStartTime = StartTimeBox.Text.Trim();
                ResultEndTime   = EndTimeBox.Text.Trim();
                ResultDays      = days;
                ResultConditions = new();
                ResultName      = NameBox.Text.Trim();   // empty → RuleName auto-summarises
                DialogResult    = true;
                return;
            }

            // Network rule: every non-empty row becomes a condition, stored in canonical form so matching and
            // the rule list are consistent (a subnet row may list several CIDRs, IPv4 and IPv6).
            var conditions = new List<RuleCondition>();
            foreach (var r in _rows)
            {
                string by  = r.By;
                string raw = r.Raw;
                if (raw.Length == 0) continue;                      // an empty row is ignored

                string? canonical = by switch
                {
                    NetworkMatchBy.GatewayMac => NetworkMatcher.NormalizeMac(raw),
                    NetworkMatchBy.Subnet     => NetworkMatcher.NormalizeCidrList(raw),
                    NetworkMatchBy.DnsSuffix  => NetworkMatcher.NormalizeSuffix(raw),
                    NetworkMatchBy.ConnectionType => NetworkMatcher.NormalizeConnectionType(raw),
                    NetworkMatchBy.AdapterMac => NetworkMatcher.NormalizeAlternatives(raw, NetworkMatcher.NormalizeMac),
                    NetworkMatchBy.AdapterName or NetworkMatchBy.AdapterDesc => NetworkMatcher.NormalizeAlternatives(raw),
                    _                         => raw,                       // SSID
                };
                if (canonical == null)
                {
                    Notice(Lang.T(by is NetworkMatchBy.GatewayMac or NetworkMatchBy.AdapterMac ? "RuleMacInvalid"
                                : by == NetworkMatchBy.Subnet     ? "RuleSubnetInvalid" : "RuleValueRequired"),
                           Lang.T("RuleDialogValidationTitle"));
                    r.ValueBox.Focus();
                    return;
                }
                conditions.Add(new RuleCondition { By = by, Value = canonical, Not = r.Not });
            }
            if (conditions.Count == 0)
            {
                Notice(Lang.T(_rows.Count == 1 && _rows[0].By == NetworkMatchBy.Ssid ? "RuleDialogSsidRequired" : "RuleValueRequired"),
                       Lang.T("RuleDialogValidationTitle"));
                _rows[0].ValueBox.Focus();
                return;
            }

            ResultConditions = conditions;
            var name = NameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                string part = ConditionNamePart();
                name = string.IsNullOrEmpty(ResultTunnel) ? $"{part} → disconnect" : $"{part} → {ResultTunnel}";
            }
            ResultName   = name;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Title_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }
    }
}
