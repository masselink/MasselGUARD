using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MasselGUARD.Models;
using MasselGUARD.Services;

namespace MasselGUARD.Views
{
    /// <summary>
    /// "Test speed" for the profiles selected in the main-window DNS panel: the same measurement as the server picker
    /// (<see cref="DnsProbe"/>: a few queries from this PC to each server, four at a time), shown in a small themed window
    /// with the fastest profile first. The queries leave this PC only while this window is open. Code-only, same look as
    /// the other small dialogs. <see cref="TestName"/> is the host name that was resolved (the caller keeps it for next time).
    /// </summary>
    internal sealed class DnsSpeedWindow : Window
    {
        private readonly List<DnsListServer> _targets;
        private readonly Dictionary<string, DnsSpeed> _results = new();
        private readonly StackPanel _rows = new();
        private readonly TextBlock _status;
        private readonly TextBox _nameBox;
        private readonly Button _runBtn;
        private CancellationTokenSource? _cts;

        public string TestName { get; private set; }

        private static Brush Res(string key) => (Application.Current.Resources[key] as Brush) ?? Brushes.Gray;
        private static FontFamily Font => Application.Current.Resources["Theme.FontFamily"] as FontFamily ?? new FontFamily("Segoe UI");

        public DnsSpeedWindow(Window owner, IEnumerable<DnsProfile> profiles, string testName)
        {
            TestName = DnsProbe.ValidName(testName) ? testName : DnsProbe.DefaultTestName;
            _targets = profiles.Select(DnsServerList.FromProfile).ToList();

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Width = 620; SizeToContent = SizeToContent.Height;
            MaxHeight = SystemParameters.WorkArea.Height - 60;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Owner = owner;

            var border = new Border
            {
                Background = Res("WindowBg"), BorderBrush = Res("Accent"), BorderThickness = new Thickness(1),
                CornerRadius = Application.Current.Resources["Theme.CornerRadius"] is CornerRadius cr ? cr : new CornerRadius(6),
                Padding = new Thickness(20),
            };
            var panel = new StackPanel();

            var title = new TextBlock { Text = Lang.T("DnsSpeedTitle"), FontFamily = Font, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Res("Accent"), Margin = new Thickness(0, 0, 0, 10) };
            title.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
            panel.Children.Add(title);

            var nameRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = false };
            var label = new TextBlock { Text = Lang.T("DnsPickTestName"), FontFamily = Font, FontSize = 11, Foreground = Res("TextMuted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            _nameBox = new TextBox { Text = TestName, Width = 180, FontFamily = Font, FontSize = 12, Padding = new Thickness(4), ToolTip = Lang.T("DnsPickTestNameTip") };
            DockPanel.SetDock(label, Dock.Left); DockPanel.SetDock(_nameBox, Dock.Left);
            nameRow.Children.Add(label); nameRow.Children.Add(_nameBox);
            panel.Children.Add(nameRow);

            _status = new TextBlock { FontFamily = Font, FontSize = 10, Foreground = Res("TextMuted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(_status);

            // header + rows
            panel.Children.Add(MakeLine(Lang.T("DnsColName"), Lang.T("DnsSpeedColPlain"), Lang.T("DnsSpeedColDoh"), header: true));
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 320, Content = _rows };
            panel.Children.Add(new Border { BorderBrush = Res("TextMuted"), BorderThickness = new Thickness(1), Background = Res("CardBg"), Child = scroll });

            var btns = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
            _runBtn = new Button { Style = (Style)Application.Current.Resources["PrimaryBtn"], Padding = new Thickness(14, 6, 14, 6) };
            _runBtn.Click += (_, _) => { if (_cts != null) _cts.Cancel(); else _ = RunAsync(); };
            var close = new Button { Content = Lang.T("BtnClose"), Style = (Style)Application.Current.Resources["FlatBtn"], Padding = new Thickness(14, 6, 14, 6) };
            close.Click += (_, _) => Close();
            DockPanel.SetDock(_runBtn, Dock.Left); DockPanel.SetDock(close, Dock.Right);
            btns.Children.Add(_runBtn); btns.Children.Add(close);
            panel.Children.Add(btns);

            border.Child = panel;
            Content = border;
            Render();
            Loaded += (_, _) => _ = RunAsync();
            Closed += (_, _) => _cts?.Cancel();
        }

        private static Grid MakeLine(string name, string plain, string doh, bool header = false, Brush? nameBrush = null, Brush? valueBrush = null, string? tip = null, bool bold = false)
        {
            var g = new Grid { Margin = header ? new Thickness(8, 0, 8, 2) : new Thickness(8, 5, 8, 5), ToolTip = tip };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            TextBlock Cell(string t, int col, Brush fg)
            {
                var tb = new TextBlock
                {
                    Text = t, FontFamily = Font, FontSize = header ? 10 : 12, Foreground = fg, TextTrimming = TextTrimming.CharacterEllipsis,
                    FontWeight = bold && !header ? FontWeights.SemiBold : FontWeights.Normal,
                };
                Grid.SetColumn(tb, col); g.Children.Add(tb);
                return tb;
            }
            Cell(name, 0, nameBrush ?? Res(header ? "TextMuted" : "TextPrimary"));
            Cell(plain, 1, valueBrush ?? Res("TextMuted"));
            Cell(doh, 2, valueBrush ?? Res("TextMuted"));
            return g;
        }

        /// <summary>Measured ones by time (fastest first), then the ones still waiting, failed ones last.</summary>
        private void Render()
        {
            _rows.Children.Clear();
            int Group(DnsListServer s) => !_results.TryGetValue(s.Id, out var r) ? 1 : r.RankMs == null ? 2 : 0;
            var ordered = _targets.OrderBy(Group).ThenBy(s => _results.TryGetValue(s.Id, out var r) ? r.RankMs ?? int.MaxValue : int.MaxValue).ToList();
            string? fastest = ordered.Count > 0 && Group(ordered[0]) == 0 ? ordered[0].Id : null;
            foreach (var s in ordered)
            {
                if (!_results.TryGetValue(s.Id, out var r))
                    _rows.Children.Add(MakeLine(s.Name, "...", "...", valueBrush: Res("TextMuted")));
                else if (r.RankMs == null)
                    _rows.Children.Add(MakeLine(s.Name, Lang.T("DnsPickNoAnswer"), "", valueBrush: Res("Danger"), tip: r.Problem));
                else
                    _rows.Children.Add(MakeLine(s.Name, r.PlainMs is { } p ? p + " ms" : "-", r.DohMs is { } d ? d + " ms" : "-",
                        valueBrush: Res(s.Id == fastest ? "Success" : "Accent"), bold: s.Id == fastest, tip: r.Problem));
            }
            _runBtn.Content = Lang.T(_cts != null ? "DnsPickStop" : "DnsSpeedAgain");
        }

        private async System.Threading.Tasks.Task RunAsync()
        {
            var typed = _nameBox.Text.Trim();
            if (!DnsProbe.ValidName(typed)) { _status.Text = Lang.T("DnsPickTestNameBad"); _nameBox.Focus(); return; }
            TestName = typed.TrimEnd('.');
            _results.Clear();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            Render();
            int done = 0;
            _status.Text = Lang.T("DnsPickTesting", 0, _targets.Count);
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("MasselGUARD");
                using var gate = new SemaphoreSlim(4);
                await System.Threading.Tasks.Task.WhenAll(_targets.Select(async s =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        var r = await DnsProbe.TestAsync(s, http, TestName, 2500, ct, withDoh: true);
                        _results[s.Id] = r;
                        _status.Text = Lang.T("DnsPickTesting", ++done, _targets.Count);
                        Render();
                    }
                    finally { gate.Release(); }
                }));
                var best = _targets.Where(s => _results.TryGetValue(s.Id, out var x) && x.RankMs != null).OrderBy(s => _results[s.Id].RankMs).FirstOrDefault();
                _status.Text = best == null
                    ? Lang.T("DnsPickTestNone")
                    : Lang.T("DnsPickTestDone", _targets.Count, best.Name, _results[best.Id].RankMs!.Value, TestName);
            }
            catch (OperationCanceledException) { _status.Text = ""; }
            catch (Exception ex) { _status.Text = ex.Message; }
            finally
            {
                _cts?.Dispose(); _cts = null;
                if (IsLoaded) Render();
            }
        }
    }
}
