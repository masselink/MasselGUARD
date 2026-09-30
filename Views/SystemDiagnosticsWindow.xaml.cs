using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MasselGUARD.Models;
using MasselGUARD.Services;

namespace MasselGUARD.Views
{
    /// <summary>
    /// Read-only "System diagnostics" snapshot: MasselGUARD environment, active tunnels, DNS in use,
    /// and full per-adapter network details. Opened from Settings → Diagnostics and from the tray.
    /// Everything is gathered live on open / Refresh; nothing is changed. Copy button dumps a plain-text
    /// version for support. Section/field labels are English (technical report), matching the tester log.
    /// </summary>
    public partial class SystemDiagnosticsWindow : Window
    {
        private readonly MainWindow _main;
        private StringBuilder _text = new();
        private System.Collections.Generic.Dictionary<string, (int metric, int mtu)> _ifMetrics = new(StringComparer.OrdinalIgnoreCase);
        private TextBlock? _publicIpResult;

        public SystemDiagnosticsWindow(MainWindow main)
        {
            InitializeComponent();
            _main = main;
            Owner = main;
            Build();
        }

        // ── Theme helpers ────────────────────────────────────────────────────────
        private Brush B(string key, Color fb) => TryFindResource(key) as Brush ?? new SolidColorBrush(fb);
        private FontFamily Fnt() => TryFindResource("Theme.FontFamily") as FontFamily ?? new FontFamily("Segoe UI");

        /// <summary>A label shown in the UI language on screen and in English in the "Copy all"
        /// report (support text stays English). A plain string is the same on both sides.</summary>
        private readonly record struct Txt(string Ui, string En)
        {
            public static implicit operator Txt(string? s) => new(s ?? "", s ?? "");
            public Txt Indent() => new("  " + Ui, "  " + En);
        }

        /// <summary>Localized pair for a lang key; <see cref="Txt"/> arguments format per side.</summary>
        private static Txt L(string key, params object[] a) =>
            new(Lang.T(key,  a.Select(x => x is Txt t ? (object)t.Ui : x).ToArray()),
                Lang.En(key, a.Select(x => x is Txt t ? (object)t.En : x).ToArray()));

        private StackPanel AddSection(Txt title)
        {
            _text.AppendLine().AppendLine("==== " + title.En + " ====");
            var body   = new StackPanel();
            var header = new TextBlock
            {
                Text = title.Ui, FontFamily = Fnt(), FontSize = 12, FontWeight = FontWeights.Bold,
                Foreground = B("Accent", Color.FromRgb(88, 166, 255)), Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap,
            };
            var inner = new StackPanel();
            inner.Children.Add(header);
            inner.Children.Add(body);
            ReportHost.Children.Add(new Border
            {
                Background = B("CardBg", Color.FromRgb(22, 27, 34)),
                BorderBrush = B("BorderColor", Color.FromRgb(48, 54, 61)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 12),
                Margin = new Thickness(0, 0, 0, 12),
                Child = inner,
            });
            return body;
        }

        private void AddKv(StackPanel body, Txt key, Txt value, bool mono = false)
        {
            _text.Append("  ").Append(key.En).Append(": ").AppendLine(value.En);
            var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var k = new TextBlock
            {
                Text = key.Ui, FontFamily = Fnt(), FontSize = 11,
                Foreground = B("TextMuted", Color.FromRgb(110, 118, 129)),
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top,
            };
            var v = new TextBlock
            {
                Text = value.Ui, FontFamily = mono ? new FontFamily("Consolas") : Fnt(), FontSize = 11,
                Foreground = B("TextPrimary", Color.FromRgb(230, 237, 243)), TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(k, 0); Grid.SetColumn(v, 1);
            g.Children.Add(k); g.Children.Add(v);
            body.Children.Add(g);
        }

        // ── Report ─────────────────────────────────────────────────────────────
        private void Build()
        {
            ReportHost.Children.Clear();
            _text = new StringBuilder();
            _text.AppendLine($"MasselGUARD system diagnostics - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            try { BuildSummary(); }      catch (Exception ex) { AddKv(AddSection(L("DiagSecEnvironment")), L("DiagError"), ex.Message); }
            try { BuildConnectivity(); } catch (Exception ex) { AddKv(AddSection(L("DiagSecConnectivity")), L("DiagError"), ex.Message); }
            try { BuildTunnels(); }      catch (Exception ex) { AddKv(AddSection(L("DiagSecTunnels")), L("DiagError"), ex.Message); }
            try { BuildDns(); }          catch (Exception ex) { AddKv(AddSection("DNS"), L("DiagError"), ex.Message); }
            try { BuildAdapters(); }     catch (Exception ex) { AddKv(AddSection(L("DiagSecAdapters")), L("DiagError"), ex.Message); }
        }

        private void BuildConnectivity()
        {
            var body = AddSection(L("DiagSecConnectivity"));
            var btn = new Button
            {
                Content = Lang.T("SysDiagCheckIp"),
                Style = TryFindResource("FlatBtn") as Style,
                FontSize = 11, Padding = new Thickness(14, 5, 14, 5),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 6),
            };
            btn.Click += async (_, _) => await CheckPublicIpAsync();
            body.Children.Add(btn);

            _publicIpResult = new TextBlock
            {
                Text = Lang.T("DiagIpNotChecked"),
                FontFamily = Fnt(), FontSize = 11,
                Foreground = B("TextMuted", Color.FromRgb(110, 118, 129)),
                TextWrapping = TextWrapping.Wrap,
            };
            body.Children.Add(_publicIpResult);
        }

        private async System.Threading.Tasks.Task CheckPublicIpAsync()
        {
            if (_publicIpResult == null) return;
            _publicIpResult.Foreground = B("TextMuted", Color.FromRgb(110, 118, 129));
            _publicIpResult.Text = Lang.T("DiagIpChecking");
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                string ip = (await http.GetStringAsync("https://api.ipify.org")).Trim();
                int active = _main._vm.TunnelList.Count(t => t.IsActive);
                var note = L(active > 0 ? "DiagIpNoteTunnel" : "DiagIpNoteIsp");
                _publicIpResult.Foreground = B("TextPrimary", Color.FromRgb(230, 237, 243));
                _publicIpResult.Text = Lang.T("DiagIpResult", ip, note.Ui);
                _text.AppendLine($"  Public IP check: {ip} ({note.En})");
            }
            catch (Exception ex)
            {
                _publicIpResult.Foreground = B("TextPrimary", Color.FromRgb(230, 237, 243));
                _publicIpResult.Text = Lang.T("DiagIpFailed", ex.Message);
            }
        }

        /// <summary>Read per-interface routing metric + MTU from <c>netsh</c> (the .NET API doesn't
        /// expose the interface metric). Keyed by interface name (alias). Best-effort.</summary>
        private void LoadInterfaceMetrics()
        {
            _ifMetrics = new System.Collections.Generic.Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("netsh", "interface ipv4 show interfaces")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) return;
                string outp = p.StandardOutput.ReadToEnd();
                p.WaitForExit(4000);
                foreach (var line in outp.Replace("\r", "").Split('\n'))
                {
                    var parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    // Columns: Idx  Met  MTU  State  Name…
                    if (parts.Length < 5) continue;
                    if (!int.TryParse(parts[0], out _)) continue;             // skip header / separators
                    if (!int.TryParse(parts[1], out int met)) continue;
                    if (!int.TryParse(parts[2], out int mtu)) continue;
                    string name = string.Join(' ', parts.Skip(4));
                    if (name.Length > 0) _ifMetrics[name] = (met, mtu);
                }
            }
            catch { /* netsh unavailable → metrics simply omitted */ }
        }

        private void BuildSummary()
        {
            var cfg  = _main.ConfigSvc.Config;
            var body = AddSection(L("DiagSecEnvironment"));
            AddKv(body, "MasselGUARD", UpdateChecker.VersionWithCodename);
            AddKv(body, L("DiagBuild"), string.IsNullOrEmpty(UpdateChecker.BuildStamp) ? L("DiagDevBuild") : UpdateChecker.BuildStamp);
            AddKv(body, L("DiagArch"), L("DiagArchValue", RuntimeInformation.ProcessArchitecture, RuntimeInformation.OSArchitecture));
            AddKv(body, "OS", L("DiagOsValue", Environment.OSVersion.VersionString, Environment.OSVersion.Version.Build));
            AddKv(body, L("DiagElevated"), IsElevated() ? L("DiagYesAdmin") : L("DiagNo"));
            AddKv(body, L("DiagExeDir"), TunnelDll.ExeDirPublic, mono: true);
            AddKv(body, L("DiagSsid"), string.IsNullOrEmpty(_main.WifiSvc.CurrentSsid) ? "-" : _main.WifiSvc.CurrentSsid!);
            AddKv(body, L("DiagSecTunnels"), _main._vm.TunnelList.Count(t => t.IsActive).ToString());
            AddKv(body, L("DiagDnsProfiles"), L(cfg.DnsAutomationEnabled ? "DiagDnsProfilesOn" : "DiagDnsProfilesOff", cfg.DnsProfiles.Count));
        }

        private void BuildTunnels()
        {
            var cfg  = _main.ConfigSvc.Config;
            var body = AddSection(L("DiagSecTunnels"));
            var active = _main._vm.TunnelList.Where(t => t.IsActive).ToList();
            if (active.Count == 0) { AddKv(body, L("DiagStatus"), L("DiagNoActiveTunnels")); return; }

            foreach (var t in active)
            {
                TunnelDll.TunnelStats st = default;
                try { st = TunnelDll.GetStats(t.Name); } catch { }
                Txt hs = st.LastHandshakeUtc is DateTime h
                    ? L("DiagSecondsAgo", (int)(DateTime.UtcNow - h).TotalSeconds)
                    : "-";

                var stored = t.StoredTunnel;
                bool ks = cfg.KillSwitchMode == "always" || stored.KillSwitch;
                Txt killSwitch = ks
                    ? L(cfg.KillSwitchMode == "always" && !stored.KillSwitch ? "DiagOnGlobal" : "DiagOn")
                    : L("DiagOff");
                Txt split = stored.SplitMode == "off"
                    ? L("DiagOff")
                    : L("DiagSplitValue", stored.SplitMode, stored.SplitRanges.Count);

                AddKv(body, t.Name, L("DiagHandshake", hs, Bytes(st.TxBytes), Bytes(st.RxBytes)));
                AddKv(body, L("DiagEndpoint").Indent(), EndpointOf(stored), mono: true);
                AddKv(body, L("DiagKillSwitch").Indent(), killSwitch);
                AddKv(body, L("DiagSplit").Indent(), split);
            }
        }

        /// <summary>Decrypt the stored config just enough to read the peer Endpoint (we own the key).</summary>
        private static string EndpointOf(StoredTunnel stored)
        {
            try
            {
                var conf = TunnelService.DecryptConfig(stored);
                if (string.IsNullOrEmpty(conf)) return "-";
                foreach (var raw in conf.Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("Endpoint", StringComparison.OrdinalIgnoreCase))
                    {
                        int eq = line.IndexOf('=');
                        if (eq >= 0) return line[(eq + 1)..].Trim();
                    }
                }
                return "-";
            }
            catch { return "-"; }
        }

        private void BuildDns()
        {
            var cfg  = _main.ConfigSvc.Config;
            var body = AddSection("DNS");

            Txt state;
            var manual = _main._vm.ManualDnsProfileId;
            if (manual != null)
                state = manual == DnsProfile.AutomaticId
                    ? L("DiagManualAuto")
                    : L("DiagManual", cfg.DnsProfiles.FirstOrDefault(p => p.Id == manual)?.Name ?? manual);
            else if (_main._vm.DnsActive)      state = L("DiagDnsAutoApplied");
            else if (cfg.DnsAutomationEnabled) state = L("DiagDnsFollowing");
            else                               state = L("DiagOff");
            AddKv(body, "MasselGUARD DNS", state);

            bool doh = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
            AddKv(body, L("DiagDoh"), doh ? L("DiagSupported") : L("DiagDohUnsupported"));

            var guid = _main.WifiSvc.CurrentInterfaceGuid;
            var active = guid != Guid.Empty
                ? NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => string.Equals(n.Id, guid.ToString("B"), StringComparison.OrdinalIgnoreCase))
                : null;
            if (active != null)
            {
                AddKv(body, L("DiagActiveIf"), active.Name);
                AddKv(body, L("DiagResolvers"), Resolvers(active), mono: true);
            }
            else
                AddKv(body, L("DiagActiveIf"), "-");
        }

        private void BuildAdapters()
        {
            LoadInterfaceMetrics();
            var all = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            // Relevant = operationally up AND has a real (routable/ULA) address. This drops the
            // noise: down/not-present NICs, WAN Miniport pseudo-adapters, NDIS filter/scheduler
            // sub-interfaces (…-QoS Packet Scheduler-0000, …-LightWeight Filter-0000) and virtual
            // Wi-Fi adapters that sit on APIPA (169.254.x) / link-local (fe80::) only.
            var nics = all
                .Where(IsRelevantAdapter)
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var head = AddSection(L("DiagSecAdapters"));
            AddKv(head, L("DiagShown"), L("DiagShownValue", nics.Count, all.Count));

            foreach (var n in nics)
            {
                bool up = n.OperationalStatus == OperationalStatus.Up;
                Txt state = up ? L("DiagUp") : n.OperationalStatus.ToString().ToLowerInvariant();
                var body = AddSection(new Txt($"{n.Name}  ·  {FriendlyType(n)}  ·  {state.Ui}", $"{n.Name}  ·  {FriendlyType(n)}  ·  {state.En}"));
                try
                {
                    AddKv(body, L("DiagDescription"), n.Description);
                    AddKv(body, "MAC", FormatMac(n.GetPhysicalAddress()), mono: true);
                    if (n.Speed > 0) AddKv(body, L("DiagLinkSpeed"), $"{n.Speed / 1_000_000.0:0.#} Mbps");

                    var ip = n.GetIPProperties();

                    int mtuVal = 0;
                    try { mtuVal = ip.GetIPv4Properties()?.Mtu ?? 0; } catch { }
                    _ifMetrics.TryGetValue(n.Name, out var mm);
                    if (mtuVal <= 0 && mm.mtu > 0) mtuVal = mm.mtu;   // fall back to the netsh MTU
                    Txt routing = mtuVal > 0 ? $"MTU {mtuVal}" : "";
                    if (mm.metric > 0)
                    {
                        var metric = L("DiagMetric", mm.metric);
                        routing = routing.Ui.Length > 0
                            ? new Txt(routing.Ui + "   " + metric.Ui, routing.En + "   " + metric.En)
                            : metric;
                    }
                    // AddKv writes to both the UI and the copy-all buffer.
                    if (routing.Ui.Length > 0) AddKv(body, L("DiagMtuMetric"), routing);

                    var v4 = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        .Select(a => $"{a.Address}/{a.PrefixLength}").ToArray();
                    if (v4.Length > 0) AddKv(body, "IPv4", string.Join(", ", v4), mono: true);

                    var v6 = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                        .Select(a => a.Address.ToString()).ToArray();
                    if (v6.Length > 0) AddKv(body, "IPv6", string.Join(", ", v6), mono: true);

                    var gw = ip.GatewayAddresses.Select(g => g.Address.ToString()).Where(s => s != "0.0.0.0").ToArray();
                    if (gw.Length > 0) AddKv(body, L("DiagGateway"), string.Join(", ", gw), mono: true);

                    try
                    {
                        var v4p = ip.GetIPv4Properties();
                        if (v4p != null)
                        {
                            AddKv(body, "DHCP", v4p.IsDhcpEnabled ? L("DiagEnabled") : L("DiagStatic"));
                            var dhcp = ip.DhcpServerAddresses.Select(d => d.ToString()).ToArray();
                            if (v4p.IsDhcpEnabled && dhcp.Length > 0) AddKv(body, L("DiagDhcpServer"), string.Join(", ", dhcp), mono: true);
                        }
                    }
                    catch { }

                    var dns = ip.DnsAddresses.Select(d => d.ToString()).ToArray();
                    if (dns.Length > 0) AddKv(body, L("DiagDnsServers"), string.Join(", ", dns), mono: true);
                    if (!string.IsNullOrEmpty(ip.DnsSuffix)) AddKv(body, L("DiagDnsSuffix"), ip.DnsSuffix);
                }
                catch (Exception ex) { AddKv(body, L("DiagError"), ex.Message); }
            }
        }

        // ── Small helpers ────────────────────────────────────────────────────────
        /// <summary>An adapter worth showing: up, and carrying at least one real (non-APIPA,
        /// non-link-local) unicast address. Filters out down/not-present NICs, WAN Miniport
        /// pseudo-adapters and NDIS filter sub-interfaces (which have no usable address).</summary>
        private static bool IsRelevantAdapter(NetworkInterface n)
        {
            if (n.OperationalStatus != OperationalStatus.Up) return false;
            try { return n.GetIPProperties().UnicastAddresses.Any(a => IsUsableAddr(a.Address)); }
            catch { return false; }
        }

        private static bool IsUsableAddr(System.Net.IPAddress a)
        {
            if (System.Net.IPAddress.IsLoopback(a)) return false;
            if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var b = a.GetAddressBytes();
                return !(b[0] == 169 && b[1] == 254);   // drop APIPA 169.254.x.x
            }
            if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                return !a.IsIPv6LinkLocal;               // drop fe80::, keep global + ULA (fd00::)
            return false;
        }

        private static string Resolvers(NetworkInterface ni)
        {
            try
            {
                var a = ni.GetIPProperties().DnsAddresses.Select(x => x.ToString()).ToArray();
                return a.Length == 0 ? Lang.T("DiagNone") : string.Join(", ", a);
            }
            catch (Exception ex) { return Lang.T("DiagReadFailed", ex.Message); }
        }

        private static string FriendlyType(NetworkInterface n)
        {
            if (n.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
                || n.Name.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)) return "tunnel";
            return n.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211                                  => "Wi-Fi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "Ethernet",
                NetworkInterfaceType.Ppp                                            => "PPP",
                NetworkInterfaceType.Tunnel                                         => "tunnel",
                _                                                                   => n.NetworkInterfaceType.ToString(),
            };
        }

        private static string FormatMac(PhysicalAddress mac)
        {
            var b = mac.GetAddressBytes();
            return b.Length == 0 ? "-" : string.Join(":", b.Select(x => x.ToString("X2")));
        }

        private static string Bytes(long b)
        {
            if (b < 1024) return $"{b} B";
            if (b < 1024 * 1024) return $"{b / 1024.0:F1} KB";
            if (b < 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024):F1} MB";
            return $"{b / (1024.0 * 1024 * 1024):F2} GB";
        }

        private static bool IsElevated()
        {
            try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        // ── Buttons ────────────────────────────────────────────────────────────
        private void RefreshBtn_Click(object sender, RoutedEventArgs e) => Build();

        private void CopyBtn_Click(object sender, RoutedEventArgs e)
        {
            try { if (_text.Length > 0) Clipboard.SetText(_text.ToString()); } catch { }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }
    }
}
