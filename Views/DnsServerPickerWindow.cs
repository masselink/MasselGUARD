using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MasselGUARD.Models;
using MasselGUARD.Services;

namespace MasselGUARD.Views
{
    /// <summary>
    /// "Browse DNS servers": a searchable list of public resolvers with a checkbox each, an explanation and
    /// the facts (what it blocks, what it logs, addresses) in a details pane. The user ticks entries and
    /// "Add selected" returns new <see cref="DnsProfile"/>s; nothing is applied here. Shows the downloaded or
    /// built-in list at once and refreshes it in the background (see <see cref="DnsListService"/>). Code-only,
    /// same themed-window pattern as <see cref="DnsProfileEditor"/>. Returns an empty list when cancelled.
    /// </summary>
    /// <summary>What the picker asks for: new profiles to add and the ids of profiles to delete.</summary>
    public sealed class DnsPickResult
    {
        public List<DnsProfile> Added { get; init; } = new();
        public List<string> RemovedIds { get; init; } = new();
        /// <summary>The name the user last tested with (kept for next time).</summary>
        public string TestName { get; set; } = DnsProbe.DefaultTestName;
    }

    public sealed class DnsServerPickerWindow : Window
    {
        private readonly List<DnsProfile> _existing;
        private readonly string _repoUrl;
        private readonly Dictionary<string, string> _words = new();   // translated block/logging words per entry, for searching in the UI language
        private readonly CheckBox _onlyTicked;
        private readonly HashSet<string> _checked = new();
        private readonly CancellationTokenSource _cts = new();
        private DnsListResult _list;
        private DnsListServer? _selected;
        private DnsPickResult _result = new();
        private readonly HashSet<string> _initial = new();   // entries that were already in the user's list when the window opened
        private readonly HashSet<string> _seen = new();
        private readonly Dictionary<string, Dictionary<string, string>> _values = new();   // entry id -> parameter token -> typed value

        private readonly TextBox _search;
        private readonly ComboBox _tagBox;
        private readonly ComboBox _sortBox;
        private readonly Button _testBtn;
        private readonly TextBox _nameBox;
        private string _testName;
        private readonly Dictionary<string, DnsSpeed> _speed = new();
        private CancellationTokenSource? _testCts;
        private readonly StackPanel _rows = new();
        private readonly TextBlock _status;
        private readonly StackPanel _details = new();
        private readonly Button _addBtn;

        private static Brush Res(string key) => (Application.Current.Resources[key] as Brush) ?? Brushes.Gray;
        private static FontFamily Font => Application.Current.Resources["Theme.FontFamily"] as FontFamily ?? new FontFamily("Segoe UI");

        private DnsServerPickerWindow(Window? owner, List<DnsProfile> existing, string repoUrl, string testName)
        {
            _testName = DnsProbe.ValidName(testName) ? testName : DnsProbe.DefaultTestName;
            _existing = existing;
            _repoUrl = repoUrl;
            _list = DnsListService.LoadLocal();

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Width = 780; Height = 560; MinWidth = 600; MinHeight = 420;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            if (owner != null) Owner = owner;

            var border = new Border
            {
                Background = Res("WindowBg"), BorderBrush = Res("Accent"), BorderThickness = new Thickness(1),
                CornerRadius = Application.Current.Resources["Theme.CornerRadius"] is CornerRadius cr ? cr : new CornerRadius(6),
                Padding = new Thickness(18),
            };
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Title (draggable)
            var title = new TextBlock
            {
                Text = Lang.T("DnsPickTitle"), FontFamily = Font, FontSize = 14, FontWeight = FontWeights.Bold,
                Foreground = Res("Accent"), Margin = new Thickness(0, 0, 0, 10),
            };
            title.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
            Grid.SetRow(title, 0); root.Children.Add(title);

            // Search + tag filter
            var bar = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            _search = new TextBox
            {
                FontFamily = Font, FontSize = 12, Padding = new Thickness(6, 4, 6, 4),
                ToolTip = Lang.T("DnsPickSearchHint"),
            };
            _search.TextChanged += (_, _) => RebuildRows();
            _tagBox = new ComboBox { FontFamily = Font, FontSize = 12 };
            _tagBox.Items.Add(new ComboBoxItem { Content = Lang.T("DnsPickAllTags"), Tag = "" });
            foreach (var b in DnsServerList.KnownBlocks)
                _tagBox.Items.Add(new ComboBoxItem { Content = Lang.T("DnsPickBlocks") + ": " + BlockText(b), Tag = "block:" + b });
            _tagBox.Items.Add(new ComboBoxItem { Content = Lang.T("DnsPickFilterEnc"), Tag = "enc" });
            _tagBox.Items.Add(new ComboBoxItem { Content = Lang.T("DnsPickFilterParam"), Tag = "param" });
            _tagBox.SelectedIndex = 0;
            _tagBox.SelectionChanged += (_, _) => RebuildRows();
            _sortBox = new ComboBox { FontFamily = Font, FontSize = 12 };
            _sortBox.Items.Add(new ComboBoxItem { Content = Lang.T("DnsPickSortList") });
            _sortBox.Items.Add(new ComboBoxItem { Content = Lang.T("DnsPickSortFast") });
            _sortBox.SelectedIndex = 0;
            _sortBox.SelectionChanged += (_, _) => RebuildRows();
            Grid.SetColumn(_search, 0); Grid.SetColumn(_tagBox, 2); Grid.SetColumn(_sortBox, 4);
            bar.Children.Add(_search); bar.Children.Add(_tagBox); bar.Children.Add(_sortBox);
            Grid.SetRow(bar, 1); root.Children.Add(bar);

            _status = new TextBlock { FontFamily = Font, FontSize = 10, Foreground = Res("TextMuted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
                        var legend = new TextBlock { Text = Lang.T("DnsPickLegend"), FontFamily = Font, FontSize = 9, Foreground = Res("TextMuted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -4, 0, 8) };
            var statusPanel = new StackPanel();
            statusPanel.Children.Add(_status); statusPanel.Children.Add(legend);
            Grid.SetRow(statusPanel, 2); root.Children.Add(statusPanel);

            // List | details
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 220 });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star), MinWidth = 240 });
            var listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _rows };
            var listBox = new Border { BorderBrush = Res("TextMuted"), BorderThickness = new Thickness(1), Background = Res("CardBg"), Child = listScroll };
            var detailScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _details };
            var detailBox = new Border { BorderBrush = Res("TextMuted"), BorderThickness = new Thickness(1), Background = Res("CardBg"), Padding = new Thickness(12), Child = detailScroll };
            Grid.SetColumn(listBox, 0); Grid.SetColumn(detailBox, 2);
            body.Children.Add(listBox); body.Children.Add(detailBox);
            Grid.SetRow(body, 3); root.Children.Add(body);

            // Buttons
            var btns = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
            var refresh = new Button { Content = Lang.T("BtnRefresh"), Style = (Style)Application.Current.Resources["FlatBtn"], Padding = new Thickness(14, 6, 14, 6) };
            refresh.Click += (_, _) => _ = RefreshAsync(force: true);
            DockPanel.SetDock(refresh, Dock.Left);
            _testBtn = new Button { Content = Lang.T("DnsPickTest"), Style = (Style)Application.Current.Resources["FlatBtn"], Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0), ToolTip = Lang.T("DnsPickTestTip") };
            _testBtn.Click += (_, _) => { if (_testCts != null) _testCts.Cancel(); else _ = TestSpeedAsync(); };
            DockPanel.SetDock(_testBtn, Dock.Left);
            var nameLabel = new TextBlock { Text = Lang.T("DnsPickTestName"), FontFamily = Font, FontSize = 11, Foreground = Res("TextMuted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 4, 0) };
            _nameBox = new TextBox { Text = _testName, Width = 150, FontFamily = Font, FontSize = 12, Padding = new Thickness(4, 4, 4, 4), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = Lang.T("DnsPickTestNameTip") };
            _onlyTicked = new CheckBox { Content = Lang.T("DnsPickTestTicked"), FontFamily = Font, FontSize = 11, Foreground = Res("TextPrimary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), ToolTip = Lang.T("DnsPickTestTickedTip") };
            DockPanel.SetDock(nameLabel, Dock.Left); DockPanel.SetDock(_nameBox, Dock.Left); DockPanel.SetDock(_onlyTicked, Dock.Left);
            var cancel = new Button { Content = Lang.T("BtnCancel"), Style = (Style)Application.Current.Resources["FlatBtn"], Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
            _addBtn = new Button { Style = (Style)Application.Current.Resources["PrimaryBtn"], Padding = new Thickness(14, 6, 14, 6), IsEnabled = false };
            cancel.Click += (_, _) => Close();
            _addBtn.Click += (_, _) => Apply();
            DockPanel.SetDock(_addBtn, Dock.Right); DockPanel.SetDock(cancel, Dock.Right);
            btns.Children.Add(refresh); btns.Children.Add(_testBtn); btns.Children.Add(nameLabel); btns.Children.Add(_nameBox); btns.Children.Add(_onlyTicked); btns.Children.Add(_addBtn); btns.Children.Add(cancel);
            Grid.SetRow(btns, 4); root.Children.Add(btns);

            border.Child = root;
            Content = border;
            Loaded += (_, _) =>
            {
                _search.Focus();
                RebuildRows();
                ShowDetails(null);
                UpdateStatus(null);
                if (DnsListService.RefreshDue()) _ = RefreshAsync(force: false);
            };
            Closed += (_, _) => { _cts.Cancel(); _testCts?.Cancel(); };
        }

        /// <summary>Opens the picker; returns what to add and remove (empty when cancelled or nothing changed).</summary>
        public static DnsPickResult Pick(Window? owner, List<DnsProfile> existing, string repoUrl, string testName)
        {
            var w = new DnsServerPickerWindow(owner, existing, repoUrl, testName);
            w.ShowDialog();
            w._result.TestName = w._testName;
            return w._result;
        }

        /// <summary>Opens the picker for the user's config and adds what was ticked. Returns how many profiles were added (the caller saves and refreshes).</summary>
        public static (int added, int removed) PickAndAdd(Window? owner, AppConfig cfg)
        {
            var r = Pick(owner, cfg.DnsProfiles, cfg.DnsListRepoUrl, cfg.DnsTestName);
            cfg.DnsTestName = r.TestName;   // the caller saves
            if (r.RemovedIds.Count > 0)
            {
                var ids = r.RemovedIds.ToHashSet();
                cfg.DnsProfiles.RemoveAll(p => ids.Contains(p.Id));
                // nothing may point at a deleted profile
                if (ids.Contains(cfg.DefaultDnsProfileId))  cfg.DefaultDnsProfileId  = "";
                if (ids.Contains(cfg.OpenWifiDnsProfileId)) cfg.OpenWifiDnsProfileId = "";
                if (ids.Contains(cfg.BypassDnsProfileId))   cfg.BypassDnsProfileId   = "";
                foreach (var rule in cfg.Rules) if (ids.Contains(rule.DnsProfileId)) rule.DnsProfileId = "";
            }
            cfg.DnsProfiles.AddRange(r.Added);
            return (r.Added.Count, r.RemovedIds.Count);
        }

        // ── Refresh ───────────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task RefreshAsync(bool force)
        {
            _status.Text = Lang.T("DnsPickRefreshing");
            try
            {
                var r = await DnsListService.RefreshAsync(_repoUrl, _cts.Token);
                if (!IsLoaded) return;
                bool changed = r.Data != _list.Data;
                _list = r;
                if (changed) RebuildRows();
                UpdateStatus(r.RefreshNote);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (IsLoaded) UpdateStatus(ex.Message); }
        }

        private void UpdateStatus(string? note)
        {
            int n = _list.Data.Servers.Count;
            var text = _list.Source switch
            {
                DnsListSource.Online => Lang.T("DnsPickSourceOnline", n),
                DnsListSource.Cache => Lang.T("DnsPickSourceCache", (_list.FetchedUtc ?? DateTime.UtcNow).ToLocalTime().ToString("d", Lang.Culture), n),
                _ => Lang.T("DnsPickSourceBuiltIn", n),
            };
            if (_list.Data.Skipped > 0) text += "  " + Lang.T("DnsPickSkipped", _list.Data.Skipped);
            if (!string.IsNullOrEmpty(note)) text += "\n" + Lang.T("DnsPickRefreshFailed", note);
            _status.Text = text;
        }

        // ── List ──────────────────────────────────────────────────────────────

        private void RebuildRows()
        {
            if (_rows == null || _search == null) return;
            var tag = (_tagBox.SelectedItem as ComboBoxItem)?.Tag as string;
            foreach (var sv in _list.Data.Servers)   // entries already in the user's list start ticked
                if (_seen.Add(sv.Id) && DnsServerList.IsAdded(sv, _existing)) { _initial.Add(sv.Id); _checked.Add(sv.Id); }
            _rows.Children.Clear();
            int shown = 0;
            IEnumerable<DnsListServer> view = _list.Data.Servers.Where(s => DnsServerList.Matches(s, _search.Text, tag, Words(s)));
            if (_sortBox.SelectedIndex == 1)   // fastest first: measured ones by time, then unmeasured, failed last
                view = view.OrderBy(s => !_speed.TryGetValue(s.Id, out var sp) ? 1 : sp.RankMs == null ? 2 : 0)
                           .ThenBy(s => _speed.TryGetValue(s.Id, out var sp) ? sp.RankMs ?? int.MaxValue : int.MaxValue);
            foreach (var s in view)
            {
                _rows.Children.Add(MakeRow(s));
                shown++;
            }
            if (shown == 0)
                _rows.Children.Add(new TextBlock { Text = Lang.T("DnsPickNoneFound"), FontFamily = Font, FontSize = 11, Foreground = Res("TextMuted"), Margin = new Thickness(12) });
            UpdateAddButton();
        }

        private UIElement MakeRow(DnsListServer s)
        {
            var chk = new CheckBox
            {
                IsChecked = _checked.Contains(s.Id), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            var bar = new Border { Width = 5, Background = Brushes.Transparent };
            var stateTag = new TextBlock { FontFamily = Font, FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 8, 0) };
            var name = new TextBlock { Text = s.Name, FontFamily = Font, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Res("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis };

            // Parameter fields (for example the NextDNS configuration id), shown for entries that are not in the list yet.
            var paramPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 2) };
            var paramBoxes = new List<TextBox>();
            if (s.Parameters.Count > 0 && !_initial.Contains(s.Id))
            {
                if (!_values.TryGetValue(s.Id, out var vals)) _values[s.Id] = vals = new Dictionary<string, string>();
                foreach (var prm in s.Parameters)
                {
                    paramPanel.Children.Add(new TextBlock { Text = prm.Token, FontFamily = Font, FontSize = 10, Foreground = Res("TextMuted"), Margin = new Thickness(0, 2, 0, 2) });
                    var box = new TextBox
                    {
                        Text = vals.TryGetValue(prm.Token, out var cur) ? cur : "", Width = 200, HorizontalAlignment = HorizontalAlignment.Left,
                        FontFamily = Font, FontSize = 12, Padding = new Thickness(4, 3, 4, 3), MaxLength = 64,
                        ToolTip = Lang.T("DnsPickInputError"),
                    };
                    box.TextChanged += (_, _) => { vals[prm.Token] = box.Text.Trim(); PaintParams(); };
                    paramPanel.Children.Add(box);
                    paramBoxes.Add(box);
                }
            }
            // a ticked entry with an empty or invalid field shows it in red
            void PaintParams()
            {
                foreach (var box in paramBoxes)
                    box.BorderBrush = _checked.Contains(s.Id) && !DnsServerList.ValidInput(box.Text.Trim()) ? Res("Danger") : null;
            }

            // What happens to this entry on Apply: stays (grey bar), new (accent bar), will be removed (red bar, struck through), or nothing.
            void Paint()
            {
                bool on = _checked.Contains(s.Id), was = _initial.Contains(s.Id);
                var (brush, label) = on ? (was ? (Res("TextMuted"), Lang.T("DnsPickInList")) : (Res("Accent"), Lang.T("DnsPickNew")))
                                        : (was ? (Res("Danger"), Lang.T("DnsPickWillRemove")) : ((Brush)Brushes.Transparent, ""));
                bar.Background = brush;
                chk.BorderBrush = on || was ? brush : null;
                stateTag.Text = label; stateTag.Foreground = brush;
                stateTag.Visibility = label.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                name.TextDecorations = !on && was ? TextDecorations.Strikethrough : null;
                name.Foreground = Res(!on && was ? "TextMuted" : "TextPrimary");
                PaintParams();
            }
            chk.Checked += (_, _) => { _checked.Add(s.Id); Paint(); UpdateAddButton(); };
            chk.Unchecked += (_, _) => { _checked.Remove(s.Id); Paint(); UpdateAddButton(); };
            Paint();
            var facts = new List<string>();
            if (s.Blocks.Count > 0) facts.Add(string.Join(", ", s.Blocks.Select(BlockText)));
            if (s.EncryptedOnly) facts.Add("DoH");
            else if (s.Doh.Length > 0) facts.Add("DoH");
            _speed.TryGetValue(s.Id, out var speed);
            var speedText = speed == null ? "" : speed.RankMs is { } ms ? ms + " ms" : Lang.T("DnsPickNoAnswer");
            var sub = new TextBlock { Text = string.Join("  |  ", facts), FontFamily = Font, FontSize = 10, Foreground = Res("TextMuted"), TextTrimming = TextTrimming.CharacterEllipsis };

            var text = new StackPanel();
            text.Children.Add(name);
            var line2 = new StackPanel { Orientation = Orientation.Horizontal };
            line2.Children.Add(stateTag);
            if (facts.Count > 0) line2.Children.Add(sub);
            text.Children.Add(line2);
            if (paramBoxes.Count > 0) text.Children.Add(paramPanel);

            var dock = new DockPanel { Margin = new Thickness(8, 6, 8, 6), Background = Brushes.Transparent };
            DockPanel.SetDock(chk, Dock.Left);
            if (speedText.Length > 0)
            {
                var sp = new TextBlock
                {
                    Text = speedText, FontFamily = Font, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0), Foreground = Res(speed!.Failed ? "Danger" : "Accent"), ToolTip = speed.Problem,
                };
                DockPanel.SetDock(sp, Dock.Right);
                dock.Children.Add(sp);
            }
            dock.Children.Add(chk); dock.Children.Add(text);

            var inner = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(bar, Dock.Left);
            inner.Children.Add(bar); inner.Children.Add(dock);
            var row = new Border { Child = inner, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Res("WindowBg"), Tag = s.Id };
            if (_selected?.Id == s.Id) row.Background = Res("WindowBg");
            row.MouseLeftButtonDown += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && IsInside<CheckBox>(d)) return;
                _selected = s;
                foreach (Border b in _rows.Children.OfType<Border>()) b.Background = (string?)b.Tag == s.Id ? Res("WindowBg") : Brushes.Transparent;
                ShowDetails(s);
            };
            return row;
        }

        private static bool IsInside<T>(DependencyObject d) where T : DependencyObject
        {
            for (; d != null; d = (d is Visual or System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
                if (d is T) return true;
            return false;
        }

        private void UpdateAddButton()
        {
            int added = _list.Data.Servers.Count(s => _checked.Contains(s.Id) && !_initial.Contains(s.Id));
            int removed = _list.Data.Servers.Count(s => !_checked.Contains(s.Id) && _initial.Contains(s.Id));
            _addBtn.Content = Lang.T("DnsPickApply", added, removed);
            _addBtn.IsEnabled = added + removed > 0;
        }

        // ── Details ───────────────────────────────────────────────────────────

        private void ShowDetails(DnsListServer? s)
        {
            _details.Children.Clear();
            if (s == null)
            {
                _details.Children.Add(Text(Lang.T("DnsPickNoSelection"), 11, "TextMuted"));
                return;
            }
            _details.Children.Add(Text(s.Name, 14, "Accent", bold: true));
            var who = s.Provider + (s.Country.Length > 0 ? "  (" + CountryName(s.Country) + ")" : "");
            if (who.Trim().Length > 0) _details.Children.Add(Text(who, 10, "TextMuted", margin: new Thickness(0, 0, 0, 8)));
            if (s.Description.Length > 0) _details.Children.Add(Text(s.Description, 12, "TextPrimary", margin: new Thickness(0, 0, 0, 10)));

            Fact("DnsPickBlocks", s.Blocks.Count == 0 ? Lang.T("DnsPickBlocksNothing") : string.Join("\n", s.Blocks.Select(BlockText)));
            Fact("DnsPickLogging", LoggingText(s.Logging));
            if (_speed.TryGetValue(s.Id, out var sd))
            {
                var parts = new List<string>();
                if (sd.PlainMs is { } pm) parts.Add(Lang.T("DnsPickPlainMs", pm));
                if (sd.DohMs is { } dm) parts.Add(Lang.T("DnsPickDohMs", dm));
                if (parts.Count == 0) parts.Add(Lang.T("DnsPickNoAnswer"));
                if (sd.Problem != null) parts.Add(sd.Problem);
                Fact("DnsPickResponse", string.Join("\n", parts));
            }
            var addrs = s.V4.Concat(s.V6).ToList();
            if (addrs.Count > 0) Fact("DnsPickAddresses", string.Join("\n", addrs));
            if (s.Doh.Length > 0) Fact("DnsPickDoh", s.Doh);
            if (s.EncryptedOnly) _details.Children.Add(Text(Lang.T("DnsPickEncOnly"), 10, "WarningColor", margin: new Thickness(0, 2, 0, 6)));
            foreach (var prm in s.Parameters)
                _details.Children.Add(Text(Lang.T("DnsPickNeeds", prm.Token), 10, "TextMuted", margin: new Thickness(0, 2, 0, 6)));

            var links = new TextBlock { FontFamily = Font, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
            AddLink(links, Lang.T("DnsPickWebsite"), s.Website);
            AddLink(links, Lang.T("DnsPickPrivacy"), s.PrivacyPolicy);
            if (links.Inlines.Count > 0) _details.Children.Add(links);
        }

        private TextBlock Text(string t, double size, string brush, bool bold = false, Thickness? margin = null) => new()
        {
            Text = t, FontFamily = Font, FontSize = size, Foreground = Res(brush), TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, Margin = margin ?? new Thickness(0, 0, 0, 4),
        };

        private void Fact(string labelKey, string value)
        {
            _details.Children.Add(Text(Lang.T(labelKey), 10, "TextMuted", margin: new Thickness(0, 4, 0, 0)));
            var tb = Text(value, 11, "TextPrimary");
            _details.Children.Add(tb);
        }

        private static void AddLink(TextBlock host, string label, string url)
        {
            if (url.Length == 0) return;
            if (host.Inlines.Count > 0) host.Inlines.Add(new Run("    "));
            var link = new Hyperlink(new Run(label)) { Foreground = Res("Accent"), ToolTip = url };
            link.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { /* no browser */ }
            };
            host.Inlines.Add(link);
        }

        /// <summary>The words for a block code (the codes the app knows; any other code shows as written).</summary>
        private static string BlockText(string b) => b switch
        {
            "malware" => Lang.T("DnsPickBlockMalware"),
            "ads" => Lang.T("DnsPickBlockAds"),
            "trackers" => Lang.T("DnsPickBlockTrackers"),
            "adult" => Lang.T("DnsPickBlockAdult"),
            _ => b,
        };

        /// <summary>The translated words of an entry (blocks, logging, encrypted only), so searching works in the UI language.</summary>
        private string Words(DnsListServer s)
        {
            if (!_words.TryGetValue(s.Id, out var w))
                _words[s.Id] = w = string.Join(' ', s.Blocks.Select(BlockText)) + " " + LoggingText(s.Logging) + (s.EncryptedOnly ? " " + Lang.T("DnsPickFilterEnc") : "");
            return w;
        }

        /// <summary>The country name in the UI language (the two-letter code when Windows does not know it).</summary>
        private static string CountryName(string code)
        {
            var prev = System.Globalization.CultureInfo.CurrentUICulture;
            try
            {
                System.Globalization.CultureInfo.CurrentUICulture = Lang.Culture;
                return new System.Globalization.RegionInfo(code).DisplayName;
            }
            catch { return code; }
            finally { System.Globalization.CultureInfo.CurrentUICulture = prev; }
        }
        private static string LoggingText(string l) => l switch
        {
            "none" => Lang.T("DnsPickLogNone"),
            "minimal" => Lang.T("DnsPickLogMinimal"),
            "short-term" => Lang.T("DnsPickLogShortTerm"),
            "anonymized" => Lang.T("DnsPickLogAnonymized"),
            "configurable" => Lang.T("DnsPickLogConfigurable"),
            _ => Lang.T("DnsPickLogUnknown"),
        };

        // ── Speed test ────────────────────────────────────────────────────────

        /// <summary>Times the servers currently listed (after search/filter): a few DNS queries from this PC to each
        /// of them, four servers at a time. Results show per row and can sort the list.</summary>
        private async System.Threading.Tasks.Task TestSpeedAsync()
        {
            var typed = _nameBox.Text.Trim();
            if (!DnsProbe.ValidName(typed)) { _status.Text = Lang.T("DnsPickTestNameBad"); _nameBox.Focus(); return; }
            _testName = typed.TrimEnd('.');
            _speed.Clear();
            var tag = (_tagBox.SelectedItem as ComboBoxItem)?.Tag as string;
            var targets = _list.Data.Servers.Where(s => DnsServerList.Matches(s, _search.Text, tag, Words(s))).ToList();
            if (_onlyTicked.IsChecked == true)   // only the servers the user ticked (that are visible with the current search and filter)
            {
                targets = targets.Where(s => _checked.Contains(s.Id)).ToList();
                if (targets.Count == 0) { _status.Text = Lang.T("DnsPickTestTickedNone"); return; }
            }
            if (targets.Count == 0) return;
            _testCts = new CancellationTokenSource();
            var ct = _testCts.Token;
            _testBtn.Content = Lang.T("DnsPickStop");
            int done = 0;
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("MasselGUARD");
                using var gate = new System.Threading.SemaphoreSlim(4);
                await System.Threading.Tasks.Task.WhenAll(targets.Select(async s =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        // DoH is only timed when there is no plain address (or it is DoH-only): the figure to compare is plain DNS
                        var r = await DnsProbe.TestAsync(s, http, _testName, 2500, ct, withDoh: true);
                        _speed[s.Id] = r;
                        _status.Text = Lang.T("DnsPickTesting", ++done, targets.Count);
                    }
                    finally { gate.Release(); }
                }));
                var best = targets.Where(s => _speed.TryGetValue(s.Id, out var x) && x.RankMs != null)
                                  .OrderBy(s => _speed[s.Id].RankMs).FirstOrDefault();
                _status.Text = best == null
                    ? Lang.T("DnsPickTestNone")
                    : Lang.T("DnsPickTestDone", targets.Count, best.Name, _speed[best.Id].RankMs!.Value, _testName);
            }
            catch (OperationCanceledException) { UpdateStatus(null); }
            catch (Exception ex) { _status.Text = ex.Message; }
            finally
            {
                _testCts?.Dispose(); _testCts = null;
                if (IsLoaded)
                {
                    _testBtn.Content = Lang.T("DnsPickTest");
                    _sortBox.SelectedIndex = 1;   // shows the fastest first (also rebuilds the rows)
                    RebuildRows();
                    ShowDetails(_selected);
                }
            }
        }

        // ── Add ───────────────────────────────────────────────────────────────

        private void Apply()
        {
            var picked = _list.Data.Servers.Where(s => _checked.Contains(s.Id) && !_initial.Contains(s.Id)).ToList();
            var profiles = new List<DnsProfile>();
            foreach (var s in picked)
            {
                _values.TryGetValue(s.Id, out var vals);
                var p = DnsServerList.ToProfile(s, vals);
                if (p == null)   // a parameter field is empty or not valid: show which entry
                {
                    _selected = s;
                    foreach (Border b in _rows.Children.OfType<Border>())
                    {
                        b.Background = (string?)b.Tag == s.Id ? Res("WindowBg") : Brushes.Transparent;
                        if ((string?)b.Tag == s.Id) b.BringIntoView();
                    }
                    ShowDetails(s);
                    MessageBox.Show(this, Lang.T("DnsPickParamMissing", s.Name), Lang.T("DnsPickTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                profiles.Add(p);
            }

            // entries to remove: ticked off again, and the profiles they stand for
            var gone = _list.Data.Servers.Where(s => _initial.Contains(s.Id) && !_checked.Contains(s.Id))
                .SelectMany(s => DnsServerList.FindAdded(s, _existing)).GroupBy(p => p.Id).Select(g => g.First()).ToList();
            if (gone.Count > 0 && MessageBox.Show(this, Lang.T("DnsPickConfirmRemove", gone.Count, string.Join("\n", gone.Select(p => "- " + p.Name))),
                    Lang.T("DnsPickTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            _result = new DnsPickResult { Added = profiles, RemovedIds = gone.Select(p => p.Id).ToList() };
            Close();
        }
    }
}
