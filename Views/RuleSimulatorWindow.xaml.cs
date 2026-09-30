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
    /// <summary>
    /// "Advanced test": describe a network (or fetch the current one) and see which rule the automation would
    /// pick, with a step-by-step reasoning. Read-only: nothing is connected, changed or counted. The logic is the
    /// pure <see cref="RuleSimulator"/>; this window only collects the inputs and draws the steps.
    /// </summary>
    public partial class RuleSimulatorWindow : Window
    {
        private readonly MainWindow _main;

        public RuleSimulatorWindow(MainWindow main)
        {
            InitializeComponent();
            _main = main;
            Owner = main;
            KindBox.Items.Add(new ComboBoxItem { Content = Lang.T("DiagKindWifi"),  Tag = false });
            KindBox.Items.Add(new ComboBoxItem { Content = Lang.T("DiagKindWired"), Tag = true  });
            KindBox.SelectedIndex = 0;
        }

        private bool IsWired => (KindBox.SelectedItem as ComboBoxItem)?.Tag is true;

        /// <summary>SSID and "open" only make sense for Wi-Fi.</summary>
        private void Kind_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (SsidBox == null || OpenCheck == null) return;
            bool wifi = !IsWired;
            SsidBox.IsEnabled = wifi; OpenCheck.IsEnabled = wifi;
        }

        private void Notice(string message) => _main.ShowThemedInfo(message, Lang.T("TestAdvTitle"), this);

        // ── Fetch the parameters from a connected network ─────────────────────────────

        private async void Fetch_Click(object sender, RoutedEventArgs e)
        {
            FetchBtn.IsEnabled = false;
            NetworkSnapshot snap;
            try { snap = await System.Threading.Tasks.Task.Run(_main.CaptureNetworkSnapshot); }
            catch { snap = NetworkSnapshot.Empty; }
            finally { FetchBtn.IsEnabled = true; }

            if (snap.IsEmpty) { Notice(Lang.T("TestAdvFetchNone")); return; }
            if (snap.Adapters.Count == 1) { FillFrom(snap.Adapters[0]); Run_Click(this, new RoutedEventArgs()); return; }

            var ordered = snap.Adapters.OrderByDescending(x => x.IsPrimary).ToList();
            var entries = ordered.Select((a, i) => FetchEntry.Item(
                $"{RuleTester.Label(a)}{(a.IsPrimary ? $" ({Lang.T("DiagPrimaryTag")})" : "")}", i.ToString())).ToList();
            FetchMenu.Show(FetchBtn, entries, idx =>
            {
                FillFrom(ordered[int.Parse(idx)]);
                Run_Click(this, new RoutedEventArgs());
            });
        }

        private void FillFrom(NetworkIdentity a)
        {
            KindBox.SelectedIndex = a.IsWired ? 1 : 0;
            SsidBox.Text   = a.Ssid ?? "";
            OpenCheck.IsChecked = a.IsOpen;
            SuffixBox.Text = a.DnsSuffix ?? "";
            MacBox.Text    = a.GatewayMac ?? "";
            SubnetsBox.Text = string.Join(", ", a.Subnets);
        }

        // ── Run the test ──────────────────────────────────────────────────────────────

        private void Run_Click(object sender, RoutedEventArgs e)
        {
            string mac = MacBox.Text.Trim();
            if (mac.Length > 0 && NetworkMatcher.NormalizeMac(mac) == null) { Notice(Lang.T("RuleMacInvalid")); MacBox.Focus(); return; }

            List<string> subnets = new();
            string rawSubnets = SubnetsBox.Text.Trim();
            if (rawSubnets.Length > 0)
            {
                var norm = NetworkMatcher.NormalizeCidrList(rawSubnets);
                if (norm == null) { Notice(Lang.T("RuleSubnetInvalid")); SubnetsBox.Focus(); return; }
                subnets = NetworkMatcher.SplitCidrs(norm);
            }

            var net = RuleSimulator.Describe(IsWired, SsidBox.Text, OpenCheck.IsChecked == true,
                                             SuffixBox.Text, mac, subnets);
            var result = RuleSimulator.Run(_main.ConfigSvc.Config, net, DateTime.Now);
            Render(result);
        }

        private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

        private void Render(SimResult result)
        {
            StepsHost.Children.Clear();
            foreach (var s in result.Steps)
            {
                var brush = s.Level switch
                {
                    SimLevel.Good   => Res("Success",     Brushes.LightGreen),
                    SimLevel.Bad    => Res("Danger",      Brushes.IndianRed),
                    SimLevel.Muted  => Res("TextMuted",   Brushes.Gray),
                    SimLevel.Result => Res("Accent",      Brushes.CornflowerBlue),
                    _               => Res("TextPrimary", Brushes.White),
                };
                bool heading = s.Level == SimLevel.Info && s.Indent == 0 && s.Text.Length > 1 && char.IsDigit(s.Text[0]);
                StepsHost.Children.Add(new TextBlock
                {
                    Text         = (s.Indent > 0 && s.Level != SimLevel.Info ? "• " : "") + s.Text,
                    Foreground   = brush,
                    FontFamily   = TryFindResource("Theme.FontFamily") as FontFamily ?? new FontFamily("Segoe UI"),
                    FontSize     = 11,
                    FontWeight   = heading || s.Level == SimLevel.Result ? FontWeights.Bold : FontWeights.Normal,
                    TextWrapping = TextWrapping.Wrap,
                    Margin       = new Thickness(s.Indent * 16, heading ? 10 : 1, 0, 1),
                });
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Title_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }
    }
}
