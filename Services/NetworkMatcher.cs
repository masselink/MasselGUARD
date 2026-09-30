using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>
    /// PURE network-identity logic (docs/NetworkIdentity-Design.md): matching a rule value against a
    /// <see cref="NetworkIdentity"/>, interpreting the trusted-networks list, the user-orderable match
    /// priority, and primary-network selection. No WPF, no network access, so it is shared with the CLI
    /// and exercised headlessly by <c>MasselGUARDcli selftest</c> (<see cref="RunSelfTest"/>).
    /// </summary>
    public static class NetworkMatcher
    {
        /// <summary>Default match-type priority, highest first (design 4.3).</summary>
        public static readonly string[] DefaultPriority =
        {
            NetworkMatchBy.GatewayMac, NetworkMatchBy.Ssid, NetworkMatchBy.DnsSuffix, NetworkMatchBy.Subnet,
        };

        // ── Normalisation ─────────────────────────────────────────────────────

        /// <summary>Any common MAC spelling (aa:bb.., aa-bb.., aabb.ccdd.eeff, aabbccddeeff) to
        /// "aa:bb:cc:dd:ee:ff". Null when it is not exactly 12 hex digits.</summary>
        public static string? NormalizeMac(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var hex = new StringBuilder(12);
            foreach (var c in s.Trim())
            {
                if (Uri.IsHexDigit(c)) hex.Append(char.ToLowerInvariant(c));
                else if (c is ':' or '-' or '.' or ' ') continue;
                else return null;
            }
            if (hex.Length != 12) return null;
            var h = hex.ToString();
            return string.Join(":", Enumerable.Range(0, 6).Select(i => h.Substring(i * 2, 2)));
        }

        /// <summary>Lower-cased, no leading/trailing dots or spaces. Null when empty.</summary>
        public static string? NormalizeSuffix(string? s)
        {
            if (s == null) return null;
            var t = s.Trim().Trim('.').ToLowerInvariant();
            return t.Length == 0 ? null : t;
        }

        // ── Matching ──────────────────────────────────────────────────────────

        /// <summary>Does a rule of <paramref name="matchBy"/> + <paramref name="value"/> match this network?
        /// Unknown match types, empty values and a null identity never match.</summary>
        public static bool Matches(string? matchBy, string? value, NetworkIdentity? id)
        {
            if (id == null || string.IsNullOrWhiteSpace(value)) return false;
            switch (matchBy)
            {
                case NetworkMatchBy.Ssid:
                    return !string.IsNullOrEmpty(id.Ssid)
                        && string.Equals(id.Ssid, value.Trim(), StringComparison.OrdinalIgnoreCase);

                case NetworkMatchBy.DnsSuffix:
                {
                    var want = NormalizeSuffix(value);
                    var have = NormalizeSuffix(id.DnsSuffix);
                    if (want == null || have == null) return false;
                    return have == want || have.EndsWith("." + want, StringComparison.Ordinal);
                }

                case NetworkMatchBy.GatewayMac:
                {
                    var want = NormalizeMac(value);
                    var have = NormalizeMac(id.GatewayMac);
                    return want != null && have != null && want == have;
                }

                case NetworkMatchBy.Subnet:
                    return id.Subnets.Any(s => CidrMath.ContainsCidr(value.Trim(), s));

                default:
                    return false;
            }
        }

        // ── Trusted-networks list ─────────────────────────────────────────────

        /// <summary>A trusted-list entry as (matchBy, value). A bare string is an SSID (the original
        /// behaviour); <c>suffix:</c> / <c>mac:</c> / <c>subnet:</c> prefixes select the other types.</summary>
        public static (string matchBy, string value) ParseTrustedEntry(string entry)
        {
            var e = (entry ?? "").Trim();
            if (e.StartsWith("suffix:", StringComparison.OrdinalIgnoreCase)) return (NetworkMatchBy.DnsSuffix,  e[7..].Trim());
            if (e.StartsWith("mac:",    StringComparison.OrdinalIgnoreCase)) return (NetworkMatchBy.GatewayMac, e[4..].Trim());
            if (e.StartsWith("subnet:", StringComparison.OrdinalIgnoreCase)) return (NetworkMatchBy.Subnet,     e[7..].Trim());
            return (NetworkMatchBy.Ssid, e);
        }

        /// <summary>Inverse of <see cref="ParseTrustedEntry"/>: the string stored in the list.</summary>
        public static string FormatTrustedEntry(string matchBy, string value) => matchBy switch
        {
            NetworkMatchBy.DnsSuffix  => "suffix:" + value.Trim(),
            NetworkMatchBy.GatewayMac => "mac:"    + (NormalizeMac(value) ?? value.Trim()),
            NetworkMatchBy.Subnet     => "subnet:" + (CidrMath.NormalizeCidr(value) ?? value.Trim()),
            _                         => value.Trim(),
        };

        /// <summary>True when any entry of the trusted list matches this network.</summary>
        public static bool IsTrusted(IEnumerable<string>? trustedList, NetworkIdentity? id)
        {
            if (trustedList == null || id == null) return false;
            foreach (var entry in trustedList)
            {
                var (by, value) = ParseTrustedEntry(entry);
                if (Matches(by, value, id)) return true;
            }
            return false;
        }

        // ── Match-type priority (user setting) ────────────────────────────────

        /// <summary>Repairs a saved priority list to a valid permutation of the four match types:
        /// unknown and duplicate entries are dropped, missing ones are appended in default order.
        /// A hand-edited config can therefore never disable a match type.</summary>
        public static string[] RepairPriority(IEnumerable<string>? saved)
        {
            var result = new List<string>(4);
            if (saved != null)
                foreach (var raw in saved)
                {
                    var v = (raw ?? "").Trim().ToLowerInvariant();
                    if (NetworkMatchBy.IsKnown(v) && !result.Contains(v)) result.Add(v);
                }
            foreach (var d in DefaultPriority)
                if (!result.Contains(d)) result.Add(d);
            return result.ToArray();
        }

        /// <summary>0 = highest priority. Unknown match types sort last.</summary>
        public static int PriorityOf(string? matchBy, IReadOnlyList<string>? priority)
        {
            var p = priority ?? DefaultPriority;
            for (int i = 0; i < p.Count; i++)
                if (string.Equals(p[i], matchBy, StringComparison.OrdinalIgnoreCase)) return i;
            return int.MaxValue;
        }

        /// <summary>Tie-break inside the subnet type: a longer (more specific) prefix first.
        /// Returns a value where LOWER = more specific (so it can be an ascending sort key).</summary>
        public static int SubnetSpecificity(string? cidr) => 128 - (CidrMath.PrefixLength(cidr) ?? 0);

        // ── Fetch (read a match value from a connected network) ───────────────

        /// <summary>The value(s) of <paramref name="matchBy"/> this network has: what the Fetch buttons
        /// offer. Subnets list IPv4 first (the /24 people mean), then IPv6 prefixes.</summary>
        public static List<string> ValuesFor(NetworkIdentity a, string matchBy)
        {
            switch (matchBy)
            {
                case NetworkMatchBy.Ssid:       return string.IsNullOrEmpty(a.Ssid) ? new() : new() { a.Ssid! };
                case NetworkMatchBy.DnsSuffix:  return string.IsNullOrEmpty(a.DnsSuffix) ? new() : new() { a.DnsSuffix! };
                case NetworkMatchBy.GatewayMac: return string.IsNullOrEmpty(a.GatewayMac) ? new() : new() { a.GatewayMac! };
                case NetworkMatchBy.Subnet:
                    return a.Subnets.OrderBy(s => s.Contains(':') ? 1 : 0).ToList();
                default: return new();
            }
        }

        // ── Rule selection ────────────────────────────────────────────────────

        /// <summary>Enabled network rules (<see cref="TunnelRule.IsNetworkKind"/>) that match this network,
        /// best first: match-type priority, then (for subnets) the longer prefix, then list order.
        /// <paramref name="filter"/> narrows the candidates (e.g. "has a DNS profile").
        /// Pure: does not touch execution counters.</summary>
        public static List<TunnelRule> MatchingRules(
            IEnumerable<TunnelRule> rules, NetworkIdentity? id,
            IReadOnlyList<string>? priority, Func<TunnelRule, bool>? filter = null)
        {
            if (id == null) return new List<TunnelRule>();
            var prio = priority ?? DefaultPriority;
            return rules
                .Select((r, i) => (r, i))
                .Where(x => x.r.Enabled && x.r.IsNetworkKind && (filter == null || filter(x.r))
                            && Matches(x.r.EffectiveMatchBy, x.r.EffectiveMatchValue, id))
                .OrderBy(x => PriorityOf(x.r.EffectiveMatchBy, prio))
                .ThenBy(x => x.r.EffectiveMatchBy == NetworkMatchBy.Subnet ? SubnetSpecificity(x.r.EffectiveMatchValue) : 0)
                .ThenBy(x => x.i)
                .Select(x => x.r)
                .ToList();
        }

        /// <summary>Compatibility bridge for callers that still only know an SSID: a Wi-Fi identity with
        /// just the SSID and open flag. Null when there is neither (no network).</summary>
        public static NetworkIdentity? FromSsid(string? ssid, bool isOpen)
        {
            if (string.IsNullOrEmpty(ssid) && !isOpen) return null;
            return new NetworkIdentity("wlan", "Wi-Fi", NetworkIdentity.KindWifi,
                string.IsNullOrEmpty(ssid) ? null : ssid, isOpen, null, null, null,
                Array.Empty<string>(), null, 0, true);
        }

        /// <summary>Label a network gets in the history/chart: the SSID for Wi-Fi, the DNS suffix for a
        /// wired network, and the adapter name when neither exists.</summary>
        public static string HistoryLabel(NetworkIdentity id)
        {
            if (id.IsWifi && !string.IsNullOrEmpty(id.Ssid)) return id.Ssid!;
            if (id.IsWired && !string.IsNullOrEmpty(id.DnsSuffix)) return id.DnsSuffix!;
            return id.AdapterName;
        }

        /// <summary>Identity of "the network the history is following": changes when the primary network
        /// really changes (kind, label or open flag), not when a MAC resolves or a lease renews.</summary>
        public static string HistoryKey(NetworkIdentity? id) =>
            id == null ? "" : $"{id.Kind}|{HistoryLabel(id)}|{(id.IsOpen ? 1 : 0)}";

        /// <summary>Name used in decision reasons: the SSID for Wi-Fi, the adapter name for wired.</summary>
        public static string NetName(NetworkIdentity id) =>
            id.IsWifi && !string.IsNullOrEmpty(id.Ssid) ? id.Ssid! : id.AdapterName;

        // ── Primary network ───────────────────────────────────────────────────

        /// <summary>Marks exactly one adapter as primary (none when the list is empty). Modes:
        /// <c>windows</c> = adapters that have a default route first, then lowest route metric, wired before
        /// Wi-Fi on a tie; <c>wired</c> / <c>wifi</c> = that kind first, then the same rules.
        /// Unknown modes behave as <c>windows</c>.</summary>
        public static IReadOnlyList<NetworkIdentity> SelectPrimary(
            IReadOnlyList<NetworkIdentity> adapters, string? mode)
        {
            if (adapters.Count == 0) return adapters;

            int KindPref(NetworkIdentity a) => mode switch
            {
                PrimaryNetworkModes.Wired => a.IsWired ? 0 : 1,
                PrimaryNetworkModes.Wifi  => a.IsWifi  ? 0 : 1,
                _                         => 0,
            };

            var best = adapters
                .OrderBy(KindPref)
                .ThenBy(a => a.RouteMetric == int.MaxValue ? 1 : 0)   // has a default route
                .ThenBy(a => a.RouteMetric)
                .ThenBy(a => a.IsWired ? 0 : 1)                       // wired wins a tie
                .ThenBy(a => a.AdapterId, StringComparer.Ordinal)     // deterministic
                .First();

            return adapters.Select(a => a with { IsPrimary = a.AdapterId == best.AdapterId }).ToList();
        }

        // ── Self-test (run via `MasselGUARDcli selftest`) ─────────────────────

        /// <summary>Table-driven checks for the pure logic. Returns (passed, failed, failure messages).</summary>
        public static (int passed, int failed, List<string> failures) RunSelfTest()
        {
            int pass = 0, fail = 0;
            var failures = new List<string>();

            void Check(string name, bool ok)
            {
                if (ok) pass++;
                else { fail++; failures.Add(name); }
            }
            void Eq<T>(string name, T got, T want)
            {
                bool ok = EqualityComparer<T>.Default.Equals(got, want);
                if (ok) pass++;
                else { fail++; failures.Add($"{name}: got '{got}' want '{want}'"); }
            }

            NetworkIdentity Id(string kind, string id, string? ssid = null, string? suffix = null,
                               string? mac = null, string[]? subnets = null, int metric = int.MaxValue,
                               bool open = false, bool primary = false) =>
                new(id, id, kind, ssid, open, suffix, mac, null,
                    subnets ?? Array.Empty<string>(), null, metric, primary);

            var office = Id("wired", "eth", suffix: "corp.example.com", mac: "aa:bb:cc:00:11:22",
                            subnets: new[] { "10.20.4.0/24" }, metric: 25);
            var cafe   = Id("wifi", "wlan", ssid: "Cafe-Free", open: true, subnets: new[] { "192.168.1.0/24" }, metric: 50);

            // 1. MAC normalisation: every spelling to one canonical form; junk and short/long to null.
            Eq("mac-colon",  NormalizeMac("AA:BB:CC:00:11:22"), "aa:bb:cc:00:11:22");
            Eq("mac-dash",   NormalizeMac("aa-bb-cc-00-11-22"), "aa:bb:cc:00:11:22");
            Eq("mac-cisco",  NormalizeMac("aabb.cc00.1122"),    "aa:bb:cc:00:11:22");
            Eq("mac-plain",  NormalizeMac("AABBCC001122"),      "aa:bb:cc:00:11:22");
            Eq("mac-short",  NormalizeMac("aa:bb:cc"),          (string?)null);
            Eq("mac-junk",   NormalizeMac("zz:bb:cc:00:11:22"), (string?)null);
            Eq("mac-null",   NormalizeMac(null),                (string?)null);

            // 2. Each match type.
            Check("ssid-match",        Matches(NetworkMatchBy.Ssid, "cafe-free", cafe));
            Check("ssid-no-match",     !Matches(NetworkMatchBy.Ssid, "Home", cafe));
            Check("ssid-wired-never",  !Matches(NetworkMatchBy.Ssid, "Cafe-Free", office));
            Check("suffix-exact",      Matches(NetworkMatchBy.DnsSuffix, "corp.example.com", office));
            Check("suffix-dot-case",   Matches(NetworkMatchBy.DnsSuffix, ".Corp.Example.COM.", office));
            Check("suffix-subdomain",  Matches(NetworkMatchBy.DnsSuffix, "example.com", office));
            Check("suffix-not-partial", !Matches(NetworkMatchBy.DnsSuffix, "ample.com", office));
            Check("suffix-none",       !Matches(NetworkMatchBy.DnsSuffix, "corp.example.com", cafe));
            Check("mac-match",         Matches(NetworkMatchBy.GatewayMac, "AA-BB-CC-00-11-22", office));
            Check("mac-no-match",      !Matches(NetworkMatchBy.GatewayMac, "aa:bb:cc:00:11:23", office));
            Check("mac-null-never",    !Matches(NetworkMatchBy.GatewayMac, "aa:bb:cc:00:11:22", cafe));
            Check("subnet-inside",     Matches(NetworkMatchBy.Subnet, "10.0.0.0/8", office));
            Check("subnet-equal",      Matches(NetworkMatchBy.Subnet, "10.20.4.0/24", office));
            Check("subnet-outside",    !Matches(NetworkMatchBy.Subnet, "172.16.0.0/12", office));
            Check("subnet-smaller",    !Matches(NetworkMatchBy.Subnet, "10.20.4.128/25", office));
            Check("unknown-by",        !Matches("bogus", "x", office));
            Check("empty-value",       !Matches(NetworkMatchBy.Ssid, "  ", cafe));
            Check("null-identity",     !Matches(NetworkMatchBy.Ssid, "Cafe-Free", null));

            // 3. Trusted list: bare SSID + prefixed identities + mixed.
            Check("trusted-bare-ssid", IsTrusted(new[] { "Cafe-Free" }, cafe));
            Check("trusted-suffix",    IsTrusted(new[] { "suffix:corp.example.com" }, office));
            Check("trusted-mac",       IsTrusted(new[] { "mac:aa:bb:cc:00:11:22" }, office));
            Check("trusted-subnet",    IsTrusted(new[] { "subnet:10.20.0.0/16" }, office));
            Check("trusted-mixed",     IsTrusted(new[] { "Home", "suffix:nope.local", "mac:aa:bb:cc:00:11:22" }, office));
            Check("trusted-none",      !IsTrusted(new[] { "Home", "suffix:nope.local" }, office));
            Check("trusted-empty",     !IsTrusted(Array.Empty<string>(), office));
            Check("trusted-null",      !IsTrusted(null, office));
            Eq("trusted-parse-bare",   ParseTrustedEntry("Cafe-Free").matchBy, NetworkMatchBy.Ssid);
            Eq("trusted-parse-mac",    ParseTrustedEntry("mac:aa:bb:cc:00:11:22").value, "aa:bb:cc:00:11:22");
            Eq("trusted-format-mac",   FormatTrustedEntry(NetworkMatchBy.GatewayMac, "AA-BB-CC-00-11-22"), "mac:aa:bb:cc:00:11:22");
            Eq("trusted-format-ssid",  FormatTrustedEntry(NetworkMatchBy.Ssid, " Home "), "Home");
            Eq("trusted-format-cidr",  FormatTrustedEntry(NetworkMatchBy.Subnet, "10.20.4.17/16"), "subnet:10.20.0.0/16");

            // 4. Priority: default order, custom order honoured, damaged value repaired.
            var def = RepairPriority(null);
            Eq("prio-default", string.Join(",", def), "gatewaymac,ssid,dnssuffix,subnet");
            Check("prio-mac-above-ssid", PriorityOf(NetworkMatchBy.GatewayMac, def) < PriorityOf(NetworkMatchBy.Ssid, def));
            var flipped = RepairPriority(new[] { "ssid", "gatewaymac", "dnssuffix", "subnet" });
            Check("prio-flipped", PriorityOf(NetworkMatchBy.Ssid, flipped) < PriorityOf(NetworkMatchBy.GatewayMac, flipped));
            Eq("prio-repair-missing", string.Join(",", RepairPriority(new[] { "subnet" })), "subnet,gatewaymac,ssid,dnssuffix");
            Eq("prio-repair-dup-unknown", string.Join(",", RepairPriority(new[] { "ssid", "SSID", "bogus", "subnet" })), "ssid,subnet,gatewaymac,dnssuffix");
            Eq("prio-repair-empty", string.Join(",", RepairPriority(Array.Empty<string>())), "gatewaymac,ssid,dnssuffix,subnet");
            Eq("prio-unknown-last", PriorityOf("bogus", def), int.MaxValue);
            Check("subnet-longer-first", SubnetSpecificity("10.20.4.0/24") < SubnetSpecificity("10.0.0.0/8"));

            // 5. Primary selection.
            var eth  = Id("wired", "eth",  metric: 25);
            var wifi = Id("wifi",  "wlan", metric: 50);
            string PrimaryId(IReadOnlyList<NetworkIdentity> l, string? mode) =>
                SelectPrimary(l, mode).FirstOrDefault(a => a.IsPrimary)?.AdapterId ?? "none";

            Eq("primary-windows-metric", PrimaryId(new[] { wifi, eth }, "windows"), "eth");
            Eq("primary-windows-wifi-lower", PrimaryId(new[] { Id("wired", "eth", metric: 80), wifi }, "windows"), "wlan");
            Eq("primary-windows-tie-wired", PrimaryId(new[] { Id("wifi", "wlan", metric: 25), Id("wired", "eth", metric: 25) }, "windows"), "eth");
            Eq("primary-wired-override", PrimaryId(new[] { Id("wired", "eth", metric: 80), wifi }, "wired"), "eth");
            Eq("primary-wifi-override", PrimaryId(new[] { eth, wifi }, "wifi"), "wlan");
            Eq("primary-wifi-mode-no-wifi", PrimaryId(new[] { eth }, "wifi"), "eth");
            Eq("primary-no-route-last", PrimaryId(new[] { Id("wired", "eth"), Id("wifi", "wlan", metric: 60) }, "windows"), "wlan");
            Eq("primary-single", PrimaryId(new[] { wifi }, null), "wlan");
            Eq("primary-empty", PrimaryId(Array.Empty<NetworkIdentity>(), "windows"), "none");
            Eq("primary-exactly-one", SelectPrimary(new[] { eth, wifi }, "windows").Count(a => a.IsPrimary), 1);
            Eq("primary-unknown-mode", PrimaryId(new[] { wifi, eth }, "bogus"), "eth");

            // 5b. History label / key: SSID, else DNS suffix (wired), else adapter name; MAC/lease churn keeps the key.
            Eq("hist-wifi",           HistoryLabel(cafe), "Cafe-Free");
            Eq("hist-wired-suffix",   HistoryLabel(office), "corp.example.com");
            Eq("hist-wired-adapter",  HistoryLabel(Id("wired", "eth")), "eth");
            Eq("hist-wifi-no-ssid",   HistoryLabel(Id("wifi", "wlan")), "wlan");
            Eq("hist-key-none",       HistoryKey(null), "");
            Check("hist-key-stable-mac", HistoryKey(office) == HistoryKey(office with { GatewayMac = null, Subnets = new[] { "10.9.9.0/24" } }));
            Check("hist-key-differs-label", HistoryKey(office) != HistoryKey(Id("wired", "eth", suffix: "hotel.example")));
            Check("hist-key-differs-kind",  HistoryKey(Id("wired", "x", ssid: "A")) != HistoryKey(Id("wifi", "x", ssid: "A")));

            // 6. Snapshot fingerprint: identical = same, any identity change = different.
            var s1 = new NetworkSnapshot(SelectPrimary(new[] { eth, wifi }, "windows"));
            var s2 = new NetworkSnapshot(SelectPrimary(new[] { wifi, eth }, "windows"));   // order must not matter
            Check("snapshot-same-any-order", s1.SameAs(s2));
            var s3 = new NetworkSnapshot(SelectPrimary(new[] { eth with { GatewayMac = "aa:bb:cc:00:11:22" }, wifi }, "windows"));
            Check("snapshot-mac-differs", !s1.SameAs(s3));
            var s4 = new NetworkSnapshot(SelectPrimary(new[] { eth, wifi }, "wifi"));
            Check("snapshot-primary-differs", !s1.SameAs(s4));
            Check("snapshot-empty-same", NetworkSnapshot.Empty.SameAs(new NetworkSnapshot(Array.Empty<NetworkIdentity>())));
            Eq("snapshot-primary", s1.Primary?.AdapterId, (string?)"eth");

            return (pass, fail, failures);
        }
    }
}
