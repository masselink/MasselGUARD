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

        private StackPanel AddSection(string title)
        {
            _text.AppendLine().AppendLine("==== " + title + " ====");
            var body   = new StackPanel();
            var header = new TextBlock
            {
                Text = title, FontFamily = Fnt(), FontSize = 12, FontWeight = FontWeights.Bold,
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

        private void AddKv(StackPanel body, string key, string? value, bool mono = false)
        {
            value ??= "";
            _text.Append("  ").Append(key).Append(": ").AppendLine(value);
            var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var k = new TextBlock
            {
                Text = key, FontFamily = Fnt(), FontSize = 11,
                Foreground = B("TextMuted", Color.FromRgb(110, 118, 129)),
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top,
            };
            var v = new TextBlock
            {
                Text = value, FontFamily = mono ? new FontFamily("Consolas") : Fnt(), FontSize = 11,
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

            try { BuildSummary(); }      catch (Exception ex) { AddKv(AddSection("Environment"), "error", ex.Message); }
            try { BuildConnectivity(); } catch (Exception ex) { AddKv(AddSection("Connectivity"), "error", ex.Message); }
            try { BuildTunnels(); }      catch (Exception ex) { AddKv(AddSection("Active tunnels"), "error", ex.Message); }
            try { BuildDns(); }          catch (Exception ex) { AddKv(AddSection("DNS"), "error", ex.Message); }
            try { BuildAdapters(); }     catch (Exception ex) { AddKv(AddSection("Network adapters"), "error", ex.Message); }
        }

        private void BuildConnectivity()
        {
            var body = AddSection("Connectivity");
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
                Text = "Public IP not checked (this sends one request that leaves your machine).",
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
            _publicIpResult.Text = "Checking…";
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                string ip = (await http.GetStringAsync("https://api.ipify.org")).Trim();
                int active = _main._vm.TunnelList.Count(t => t.IsActive);
                string note = active > 0
                    ? "a tunnel is active - this should be the tunnel's exit IP"
                    : "no tunnel active - this is your ISP's IP";
                _publicIpResult.Foreground = B("TextPrimary", Color.FromRgb(230, 237, 243));
                _publicIpResult.Text = $"Public IP: {ip}   ({note})";
                _text.AppendLine($"  Public IP check: {ip} ({note})");
            }
            catch (Exception ex)
            {
                _publicIpResult.Foreground = B("TextPrimary", Color.FromRgb(230, 237, 243));
                _publicIpResult.Text = "Check failed: " + ex.Message;
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
            var body = AddSection("Environment");
            AddKv(body, "MasselGUARD", UpdateChecker.VersionWithCodename);
            AddKv(body, "Build", string.IsNullOrEmpty(UpdateChecker.BuildStamp) ? "(dev build)" : UpdateChecker.BuildStamp);
            AddKv(body, "Architecture", $"process {RuntimeInformation.ProcessArchitecture}, OS {RuntimeInformation.OSArchitecture}");
            AddKv(body, "OS", $"{Environment.OSVersion.VersionString} (build {Environment.OSVersion.Version.Build})");
            AddKv(body, "Elevated", IsElevated() ? "yes (administrator)" : "no");
            AddKv(body, "Exe directory", TunnelDll.ExeDirPublic, mono: true);
            AddKv(body, "Wi-Fi SSID", string.IsNullOrEmpty(_main.WifiSvc.CurrentSsid) ? "-" : _main.WifiSvc.CurrentSsid!);
            AddKv(body, "Active tunnels", _main._vm.TunnelList.Count(t => t.IsActive).ToString());
            AddKv(body, "DNS profiles", $"{cfg.DnsProfiles.Count} configured, automation {(cfg.DnsAutomationEnabled ? "on" : "off")}");
        }

        private void BuildTunnels()
        {
            var cfg  = _main.ConfigSvc.Config;
            var body = AddSection("Active tunnels");
            var active = _main._vm.TunnelList.Where(t => t.IsActive).ToList();
            if (active.Count == 0) { AddKv(body, "status", "no active tunnels"); return; }

            foreach (var t in active)
            {
                TunnelDll.TunnelStats st = default;
                try { st = TunnelDll.GetStats(t.Name); } catch { }
                string hs = st.LastHandshakeUtc is DateTime h
                    ? $"{(int)(DateTime.UtcNow - h).TotalSeconds}s ago"
                    : "-";

                var stored = t.StoredTunnel;
                bool ks = cfg.KillSwitchMode == "always" || stored.KillSwitch;
                string killSwitch = ks
                    ? (cfg.KillSwitchMode == "always" && !stored.KillSwitch ? "on (global)" : "on")
                    : "off";
                string split = stored.SplitMode == "off"
                    ? "off"
                    : $"{stored.SplitMode} ({stored.SplitRanges.Count} range{(stored.SplitRanges.Count == 1 ? "" : "s")})";

                AddKv(body, t.Name, $"handshake {hs}   ↑{Bytes(st.TxBytes)}   ↓{Bytes(st.RxBytes)}");
                AddKv(body, "  endpoint", EndpointOf(stored), mono: true);
                AddKv(body, "  kill switch", killSwitch);
                AddKv(body, "  split tunnel", split);
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

            string state;
            var manual = _main._vm.ManualDnsProfileId;
            if (manual != null)
                state = manual == DnsProfile.AutomaticId
                    ? "manual override: automatic (DHCP)"
                    : "manual override: " + (cfg.DnsProfiles.FirstOrDefault(p => p.Id == manual)?.Name ?? manual);
            else if (_main._vm.DnsActive)      state = "profile applied by automation";
            else if (cfg.DnsAutomationEnabled) state = "following automation (no profile active)";
            else                               state = "off";
            AddKv(body, "MasselGUARD DNS", state);

            bool doh = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
            AddKv(body, "Encrypted DNS (DoH)", doh ? "supported" : "not on this Windows build");

            var guid = _main.WifiSvc.CurrentInterfaceGuid;
            var active = guid != Guid.Empty
                ? NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => string.Equals(n.Id, guid.ToString("B"), StringComparison.OrdinalIgnoreCase))
                : null;
            if (active != null)
            {
                AddKv(body, "Active interface", active.Name);
                AddKv(body, "Resolvers in use", Resolvers(active), mono: true);
            }
            else
                AddKv(body, "Active interface", "-");
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

            var head = AddSection("Network adapters");
            AddKv(head, "shown", $"{nics.Count} connected of {all.Count} total (inactive / virtual hidden)");

            foreach (var n in nics)
            {
                bool up = n.OperationalStatus == OperationalStatus.Up;
                var body = AddSection($"{n.Name}  ·  {FriendlyType(n)}  ·  {(up ? "up" : n.OperationalStatus.ToString().ToLowerInvariant())}");
                try
                {
                    AddKv(body, "Description", n.Description);
                    AddKv(body, "MAC", FormatMac(n.GetPhysicalAddress()), mono: true);
                    if (n.Speed > 0) AddKv(body, "Link speed", $"{n.Speed / 1_000_000.0:0.#} Mbps");

                    var ip = n.GetIPProperties();

                    int mtuVal = 0;
                    try { mtuVal = ip.GetIPv4Properties()?.Mtu ?? 0; } catch { }
                    _ifMetrics.TryGetValue(n.Name, out var mm);
                    if (mtuVal <= 0 && mm.mtu > 0) mtuVal = mm.mtu;   // fall back to the netsh MTU
                    string routing = "";
                    if (mtuVal > 0)    routing = $"MTU {mtuVal}";
                    if (mm.metric > 0) routing += (routing.Length > 0 ? "   " : "") + $"metric {mm.metric}";
                    // AddKv writes to both the UI and the copy-all buffer.
                    if (routing.Length > 0) AddKv(body, "MTU / metric", routing);

                    var v4 = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        .Select(a => $"{a.Address}/{a.PrefixLength}").ToArray();
                    if (v4.Length > 0) AddKv(body, "IPv4", string.Join(", ", v4), mono: true);

                    var v6 = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                        .Select(a => a.Address.ToString()).ToArray();
                    if (v6.Length > 0) AddKv(body, "IPv6", string.Join(", ", v6), mono: true);

                    var gw = ip.GatewayAddresses.Select(g => g.Address.ToString()).Where(s => s != "0.0.0.0").ToArray();
                    if (gw.Length > 0) AddKv(body, "Gateway", string.Join(", ", gw), mono: true);

                    try
                    {
                        var v4p = ip.GetIPv4Properties();
                        if (v4p != null)
                        {
                            AddKv(body, "DHCP", v4p.IsDhcpEnabled ? "enabled" : "static");
                            var dhcp = ip.DhcpServerAddresses.Select(d => d.ToString()).ToArray();
                            if (v4p.IsDhcpEnabled && dhcp.Length > 0) AddKv(body, "DHCP server", string.Join(", ", dhcp), mono: true);
                        }
                    }
                    catch { }

                    var dns = ip.DnsAddresses.Select(d => d.ToString()).ToArray();
                    if (dns.Length > 0) AddKv(body, "DNS servers", string.Join(", ", dns), mono: true);
                    if (!string.IsNullOrEmpty(ip.DnsSuffix)) AddKv(body, "DNS suffix", ip.DnsSuffix);
                }
                catch (Exception ex) { AddKv(body, "error", ex.Message); }
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
                return a.Length == 0 ? "(none)" : string.Join(", ", a);
            }
            catch (Exception ex) { return $"(read failed: {ex.Message})"; }
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
