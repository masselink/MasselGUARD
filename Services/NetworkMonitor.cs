using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Builds a <see cref="NetworkSnapshot"/> of the connected physical adapters (Wi-Fi + Ethernet) from
    /// local adapter properties only: no outbound probe. WPF-free and shared with the CLI
    /// (docs/NetworkIdentity-Design.md sections 2.1 and 2.2). The pure decisions (primary selection,
    /// matching) live in <see cref="NetworkMatcher"/>; this class only gathers the facts.
    /// </summary>
    public static class NetworkMonitor
    {
        // ── Gateway MAC (ARP) ─────────────────────────────────────────────────

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint macLen);

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int GetIpForwardTable(IntPtr table, ref int size, bool order);

        private static readonly TimeSpan MacCacheTtl     = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan ArpTimeout      = TimeSpan.FromMilliseconds(1500);
        private static readonly ConcurrentDictionary<string, (string mac, DateTime at)> _macCache = new();

        /// <summary>Resolves the MAC of an IPv4 gateway via ARP; null when it cannot be resolved (not on
        /// the local segment, ARP timeout, IPv6). Successes are cached for a minute; failures are not, so
        /// the caller's retry (design section 6) really retries.</summary>
        public static string? ResolveGatewayMac(IPAddress? gateway)
        {
            if (gateway == null || gateway.AddressFamily != AddressFamily.InterNetwork) return null;
            var key = gateway.ToString();
            if (_macCache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.at < MacCacheTtl)
                return hit.mac;

            string? mac = null;
            try
            {
                var task = Task.Run(() =>
                {
                    var buf = new byte[6];
                    uint len = 6;
                    uint dest = BitConverter.ToUInt32(gateway.GetAddressBytes(), 0);
                    return SendARP(dest, 0, buf, ref len) == 0 && len == 6
                        ? NetworkMatcher.NormalizeMac(BitConverter.ToString(buf))
                        : null;
                });
                if (task.Wait(ArpTimeout)) mac = task.Result;
            }
            catch { /* unresolved */ }

            if (mac != null) _macCache[key] = (mac, DateTime.UtcNow);
            return mac;
        }

        // ── Default-route metric ──────────────────────────────────────────────

        /// <summary>Lowest IPv4 default-route metric per interface index, as Windows reports it
        /// (the value <c>route print</c> shows, i.e. what decides which adapter carries traffic).
        /// Interfaces without a default route are absent.</summary>
        private static Dictionary<int, int> ReadDefaultRouteMetrics()
        {
            var result = new Dictionary<int, int>();
            IntPtr buf = IntPtr.Zero;
            try
            {
                int size = 0;
                GetIpForwardTable(IntPtr.Zero, ref size, false);          // ask for the size
                if (size <= 0) return result;
                buf = Marshal.AllocHGlobal(size);
                if (GetIpForwardTable(buf, ref size, false) != 0) return result;

                // MIB_IPFORWARDTABLE: DWORD count, then MIB_IPFORWARDROW[count], 56 bytes each:
                //   +0 dest, +4 mask, +16 ifIndex, +36 metric1
                int count = Marshal.ReadInt32(buf, 0);
                for (int i = 0; i < count; i++)
                {
                    IntPtr row = buf + 4 + i * 56;
                    if (Marshal.ReadInt32(row, 0) != 0 || Marshal.ReadInt32(row, 4) != 0) continue;   // default route only
                    int ifIndex = Marshal.ReadInt32(row, 16);
                    int metric  = Marshal.ReadInt32(row, 36);
                    if (!result.TryGetValue(ifIndex, out var cur) || metric < cur) result[ifIndex] = metric;
                }
            }
            catch { /* no metrics: primary falls back to kind order */ }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
            return result;
        }

        // ── Adapter filtering ─────────────────────────────────────────────────

        /// <summary>Physical Wi-Fi / Ethernet only. Excludes WireGuard and other tunnel adapters, so our own
        /// connect can never look like a network change (design 2.2), plus common virtual adapters.</summary>
        private static bool IsCandidate(NetworkInterface n, out string kind)
        {
            kind = "";
            if (n.OperationalStatus != OperationalStatus.Up) return false;

            switch (n.NetworkInterfaceType)
            {
                case NetworkInterfaceType.Wireless80211:
                    kind = NetworkIdentity.KindWifi; break;
                case NetworkInterfaceType.Ethernet:
                case NetworkInterfaceType.FastEthernetT:
                case NetworkInterfaceType.FastEthernetFx:
                case NetworkInterfaceType.GigabitEthernet:
                    kind = NetworkIdentity.KindWired; break;
                default:
                    return false;
            }

            var text = n.Name + " " + n.Description;
            string[] virtualMarkers =
                { "WireGuard", "Hyper-V", "vEthernet", "VMware", "VirtualBox", "TAP-", "Bluetooth", "Virtual", "Loopback" };
            if (virtualMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase))) return false;

            try { return n.GetIPProperties().UnicastAddresses.Any(a => IsUsableAddr(a.Address)); }
            catch { return false; }
        }

        private static bool IsUsableAddr(IPAddress a)
        {
            if (IPAddress.IsLoopback(a)) return false;
            if (a.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = a.GetAddressBytes();
                return !(b[0] == 169 && b[1] == 254);          // drop APIPA
            }
            if (a.AddressFamily == AddressFamily.InterNetworkV6)
                return !a.IsIPv6LinkLocal;                      // drop fe80::, keep global + ULA
            return false;
        }

        // ── Snapshot ──────────────────────────────────────────────────────────

        /// <summary>Captures the connected adapters with the primary one flagged.</summary>
        /// <param name="primaryMode"><see cref="PrimaryNetworkModes"/> value (null = windows).</param>
        /// <param name="wifiLookup">Optional: adapter GUID to (SSID, isOpen). The GUI passes its
        /// <c>WiFiService</c>; without it Wi-Fi adapters have no SSID (the CLI has no WLAN handle).</param>
        /// <param name="resolveGatewayMac">Set false to skip the ARP lookup (faster, MAC stays null).</param>
        public static NetworkSnapshot Capture(
            string? primaryMode = null,
            Func<Guid, (string? ssid, bool isOpen)>? wifiLookup = null,
            bool resolveGatewayMac = true)
        {
            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); }
            catch { return NetworkSnapshot.Empty; }

            var metrics = ReadDefaultRouteMetrics();
            var list    = new List<NetworkIdentity>();

            foreach (var n in all)
            {
                if (!IsCandidate(n, out var kind)) continue;
                try { list.Add(Describe(n, kind, metrics, wifiLookup, resolveGatewayMac)); }
                catch { /* skip an adapter that vanished mid-read */ }
            }

            return new NetworkSnapshot(NetworkMatcher.SelectPrimary(list, primaryMode));
        }

        private static NetworkIdentity Describe(NetworkInterface n, string kind,
            Dictionary<int, int> metrics, Func<Guid, (string? ssid, bool isOpen)>? wifiLookup, bool resolveMac)
        {
            var ip = n.GetIPProperties();

            string? ssid = null;
            bool    open = false;
            if (kind == NetworkIdentity.KindWifi && wifiLookup != null && Guid.TryParse(n.Id, out var guid))
                (ssid, open) = wifiLookup(guid);

            var gateway = ip.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

            var subnets = new List<string>();
            foreach (var u in ip.UnicastAddresses)
            {
                if (!IsUsableAddr(u.Address)) continue;
                // Skip IPv6 host entries (/128 - SLAAC "temporary" privacy addresses rotate daily) so a
                // rotating address never looks like a network change; the stable /64 prefix stays.
                if (u.Address.AddressFamily == AddressFamily.InterNetworkV6 && u.PrefixLength >= 128) continue;
                var cidr = CidrMath.NormalizeCidr($"{u.Address}/{u.PrefixLength}");
                if (cidr != null && !subnets.Contains(cidr)) subnets.Add(cidr);
            }

            int metric = int.MaxValue;
            try
            {
                int idx = ip.GetIPv4Properties().Index;
                if (metrics.TryGetValue(idx, out var m)) metric = m;
            }
            catch { /* IPv6-only adapter: no IPv4 index */ }

            return new NetworkIdentity(
                AdapterId:  n.Id,
                AdapterName: n.Name,
                Kind:       kind,
                Ssid:       string.IsNullOrEmpty(ssid) ? null : ssid,
                IsOpen:     open,
                DnsSuffix:  NetworkMatcher.NormalizeSuffix(ip.DnsSuffix),
                GatewayMac: resolveMac ? ResolveGatewayMac(gateway) : null,
                Gateway:    gateway?.ToString(),
                Subnets:    subnets,
                DhcpServer: ip.DhcpServerAddresses.FirstOrDefault()?.ToString(),
                RouteMetric: metric,
                IsPrimary:  false);
        }
    }
}
