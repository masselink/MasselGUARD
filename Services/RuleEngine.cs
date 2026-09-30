using System;
using System.Collections.Generic;
using System.Linq;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Pure rule-evaluation logic.
    /// No UI references, no side-effects - returns the action to take.
    /// </summary>
    public class RuleEngine
    {
        public enum ActionKind { None, Disconnect, Activate }

        /// <param name="Details">Extra decision lines for the activity log (which rule won at which
        /// priority, which other rules also matched). Null when there is nothing to add.</param>
        public record RuleResult(ActionKind Action, string? TunnelName, string Reason,
                                 IReadOnlyList<string>? Details = null);

        private static readonly RuleResult DoNothing =
            new(ActionKind.None, null, "No matching rule");

        // ── Network evaluation ────────────────────────────────────────────────

        /// <summary>
        /// Compatibility entry point for callers that only know the SSID: bridges to
        /// <see cref="EvaluateNetwork"/> with a Wi-Fi identity. Same behaviour as before for SSID rules.
        /// </summary>
        public RuleResult EvaluateWifi(
            AppConfig cfg,
            string?   ssid,
            bool      isOpenNetwork)
            => EvaluateNetwork(cfg, NetworkMatcher.FromSsid(ssid, isOpenNetwork));

        /// <summary>
        /// Evaluate what should happen for the PRIMARY network (docs/NetworkIdentity-Design.md 2.3: the
        /// tunnel is one global decision, so it follows one network; DNS is evaluated per adapter).
        /// Precedence (first match wins):
        ///   1. Manual mode / tunnels off → do nothing (all automation off).
        ///   2. Open-network protection (open/passwordless Wi-Fi + OpenWifiTunnel set).
        ///   3. Network rules - enabled "network" (or legacy "wifi") rules whose match (SSID, DNS suffix,
        ///      gateway MAC, subnet) fits the network. Several may fit: the FIRST one in the rules table
        ///      (top-down, arranged by drag and drop) wins.
        ///   4. Trusted-network rules - enabled "trusted" rules, each firing only on its
        ///      side of the trusted list (TrustedWhen "trusted" = on the list, "untrusted"
        ///      = not on it); first match activates its tunnel / disconnects. Non-matching
        ///      side falls through. The list holds SSIDs and identity entries (suffix:/mac:/subnet:).
        ///   5. Default action - activate DefaultTunnel / disconnect / none.
        /// Schedule ("time") rules are evaluated separately on a timer (see EvaluateSchedules).
        /// A null <paramref name="primary"/> means no connected network.
        /// </summary>
        public RuleResult EvaluateNetwork(AppConfig cfg, NetworkIdentity? primary)
        {
            // The tunnel axis is inert when the tunnel feature is disabled (DNS-only install) -
            // a stored rule's tunnel must never activate. The DNS axis (DnsPolicy) is separate.
            if (cfg.ManualMode || !cfg.EnableTunnels)
                return DoNothing;

            // 1. Open network protection
            if (primary != null && primary.IsOpen && !string.IsNullOrEmpty(cfg.OpenWifiTunnel))
                return new(ActionKind.Activate, cfg.OpenWifiTunnel,
                    "Open network protection");

            if (primary == null)
                return DoNothing;

            string net = NetworkMatcher.NetName(primary);

            // 2. Network rules. ("schedule" is handled by EvaluateSchedules; "trusted" is the broad
            //    policy in step 3 below.) The Tunnel field alone decides the tunnel action: an empty
            //    Tunnel disconnects, even when the rule also carries a DNS profile (DNS applies in
            //    parallel via DnsPolicy).
            var matches = NetworkMatcher.MatchingRules(cfg.Rules, primary);   // table order: first hit wins

            if (matches.Count > 0)
            {
                var match = matches[0];
                match.ExecutionCount++;

                var mc = match.EffectiveConditions;
                string what = mc.Count == 1 && !mc[0].Not && mc[0].By == NetworkMatchBy.Ssid
                    ? net                                   // the classic "Rule: <SSID> → <tunnel>"
                    : match.ConditionsPlain;                // e.g. "DNS suffix corp.example.com AND NOT subnet 10.0.0.0/8"

                // The rules table is the only ordering: the first matching rule, top-down, wins.
                var details = new List<string>
                {
                    $"Matched rule \"{match.RuleName}\" ({match.ConditionsPlain}): " +
                    $"first match in the rules table (position {cfg.Rules.IndexOf(match) + 1})",
                };
                foreach (var other in matches.Skip(1))
                    details.Add($"Rule \"{other.RuleName}\" ({other.ConditionsPlain}) also " +
                                $"matched but is lower in the table (position {cfg.Rules.IndexOf(other) + 1})");

                if (string.IsNullOrEmpty(match.Tunnel))
                    return new(ActionKind.Disconnect, null, $"Rule: {what} → disconnect", details);
                return new(ActionKind.Activate, match.Tunnel, $"Rule: {what} → {match.Tunnel}", details);
            }

            // 3. Trusted-network rules - each fires only on its matching side of the
            //    shared TrustedNetworks list; the other side falls through to the next
            //    rule and finally the Default. This lets one rule bring a tunnel up on
            //    known networks (TrustedWhen="trusted", e.g. a split tunnel) and another
            //    protect on public ones (TrustedWhen="untrusted", a full tunnel).
            //    Explicit network rules above still win. An empty Tunnel = disconnect.
            bool? isTrusted = null;
            foreach (var r in cfg.Rules)
            {
                if (!r.Enabled || r.Kind != "trusted") continue;
                isTrusted ??= NetworkMatcher.IsTrusted(cfg.TrustedNetworks, primary);

                bool sideMatches = r.TrustedWhenOnList ? isTrusted.Value : !isTrusted.Value;
                if (!sideMatches) continue;

                r.ExecutionCount++;
                string side = isTrusted.Value ? "Trusted network" : "Untrusted network";
                return string.IsNullOrEmpty(r.Tunnel)
                    ? new(ActionKind.Disconnect, null, $"{side}: {net} → disconnect")
                    : new(ActionKind.Activate, r.Tunnel, $"{side}: {net} → {r.Tunnel}");
            }

            // 4. Default action
            return cfg.DefaultAction switch
            {
                "disconnect" => new(ActionKind.Disconnect, null, "Default action: disconnect"),
                "activate" when !string.IsNullOrEmpty(cfg.DefaultTunnel)
                             => new(ActionKind.Activate, cfg.DefaultTunnel,
                                   $"Default action: activate {cfg.DefaultTunnel}"),
                _            => DoNothing,
            };
        }

        // ── DNS evaluation (Model C - parallel axis) ──────────────────────────

        /// <summary>
        /// Evaluate the DNS action for the current network, independently of the tunnel
        /// action. Thin wrapper over the pure, self-tested <see cref="DnsPolicy.Evaluate"/>
        /// (CLI-visible); uses the current local time for schedule rules.
        /// </summary>
        public DnsPolicy.DnsResult EvaluateDns(AppConfig cfg, string? ssid, bool isOpenNetwork)
            => DnsPolicy.Evaluate(cfg, ssid, isOpenNetwork, DateTime.Now);

        /// <summary>DNS action for ONE adapter's network identity (DNS is evaluated per adapter).</summary>
        public DnsPolicy.DnsResult EvaluateDns(AppConfig cfg, NetworkIdentity? network)
            => DnsPolicy.Evaluate(cfg, network, DateTime.Now);

        /// <summary>
        /// Evaluate what should happen when the WiFi disconnects entirely.
        /// </summary>
        public RuleResult EvaluateWifiDisconnected(AppConfig cfg)
        {
            if (cfg.ManualMode || !cfg.EnableTunnels) return DoNothing;

            return cfg.DefaultAction switch
            {
                "disconnect" => new(ActionKind.Disconnect, null,
                    "Default action on WiFi disconnect"),
                _ => DoNothing,
            };
        }

        // ── Schedule evaluation ───────────────────────────────────────────────

        /// <summary>
        /// Evaluate schedule ("time-triggered") rules for the given moment.
        /// Returns Activate/Disconnect when a schedule window is currently open,
        /// otherwise None. The first matching schedule rule (in list order) wins.
        /// </summary>
        public RuleResult EvaluateSchedules(AppConfig cfg, DateTime now)
        {
            if (cfg.ManualMode || !cfg.EnableTunnels) return DoNothing;

            foreach (var r in cfg.Rules)
            {
                if (!r.Enabled || r.Kind != "schedule") continue;
                if (!IsWithinSchedule(r, now)) continue;

                return string.IsNullOrEmpty(r.Tunnel)
                    ? new(ActionKind.Disconnect, null, $"Schedule: {r.RuleName}")
                    : new(ActionKind.Activate, r.Tunnel, $"Schedule: {r.RuleName}");
            }
            return DoNothing;
        }

        /// <summary>True when <paramref name="now"/> falls inside the rule's day + time window.
        /// Delegates to <see cref="DnsPolicy.IsWithinSchedule"/> (the single implementation,
        /// shared with the CLI-visible DNS precedence).</summary>
        public static bool IsWithinSchedule(TunnelRule r, DateTime now)
            => DnsPolicy.IsWithinSchedule(r, now);

        // ── Self-test (run via `MasselGUARDcli selftest`) ─────────────────────

        /// <summary>Table-driven checks for the network-identity precedence (tunnel from the primary
        /// network, DNS per adapter, first match in the rules table wins). Pure: throwaway configs, no I/O.</summary>
        public static (int passed, int failed, List<string> failures) RunSelfTest()
        {
            int pass = 0, fail = 0;
            var failures = new List<string>();
            void Check(string name, bool ok) { if (ok) pass++; else { fail++; failures.Add(name); } }
            void Eq<T>(string name, T got, T want)
            {
                if (EqualityComparer<T>.Default.Equals(got, want)) pass++;
                else { fail++; failures.Add($"{name}: got '{got}' want '{want}'"); }
            }

            var engine = new RuleEngine();
            var now    = new DateTime(2026, 9, 14, 12, 0, 0);
            const string Mac = "aa:bb:cc:00:11:22";

            NetworkIdentity Id(string kind, string id, string? ssid = null, string? suffix = null,
                               string? mac = null, string[]? subnets = null, bool open = false, bool primary = true) =>
                new(id, id, kind, ssid, open, suffix, mac, null, subnets ?? Array.Empty<string>(), null, 25, primary);

            AppConfig Cfg() => new()
            {
                EnableTunnels = true, EnableDns = true, DnsAutomationEnabled = true,
                DnsProfiles = new()
                {
                    new DnsProfile { Id = "corp",  Name = "Corp",  V4Primary = "10.0.0.53" },
                    new DnsProfile { Id = "quad9", Name = "Quad9", V4Primary = "9.9.9.9" },
                },
            };

            // 1. Legacy SSID entry point keeps its exact behaviour.
            var legacy = Cfg();
            legacy.Rules.Add(new TunnelRule { Kind = "wifi", Ssid = "Home", Tunnel = "T" });
            var r1 = engine.EvaluateWifi(legacy, "home", false);
            Check("legacy-activate", r1.Action == ActionKind.Activate && r1.TunnelName == "T");
            Eq("legacy-reason", r1.Reason, "Rule: home → T");
            Check("legacy-no-ssid", engine.EvaluateWifi(legacy, null, false).Action == ActionKind.None);
            var openCfg = Cfg(); openCfg.OpenWifiTunnel = "Full";
            Check("legacy-open", engine.EvaluateWifi(openCfg, "Cafe", true).TunnelName == "Full");
            Check("kind-network-alias", new TunnelRule { Kind = "network" }.IsNetworkKind && new TunnelRule { Kind = "wifi" }.IsNetworkKind
                                        && !new TunnelRule { Kind = "trusted" }.IsNetworkKind);

            // 2. Office example: the rules TABLE decides - the first matching rule, top-down, wins; the
            //    other matches are reported with their position; only the winner's counter moves.
            var office = Cfg();
            var rSuffix = new TunnelRule { Kind = "network", MatchBy = "dnssuffix", MatchValue = "corp.example.com", Tunnel = "", DnsProfileId = "corp", Name = "Suffix" };
            var rSub    = new TunnelRule { Kind = "network", MatchBy = "subnet",    MatchValue = "10.0.0.0/8",        Tunnel = "Split-Corp", Name = "Subnet" };
            office.Rules.Add(rSub);        // broader rule first: it wins
            office.Rules.Add(rSuffix);
            var eth = Id("wired", "eth", suffix: "corp.example.com", mac: Mac, subnets: new[] { "10.20.4.0/24" });
            var rOffice = engine.EvaluateNetwork(office, eth);
            Check("office-first-row-wins", rOffice.Action == ActionKind.Activate && rOffice.TunnelName == "Split-Corp");
            Check("office-details-winner", rOffice.Details is { Count: 2 } && rOffice.Details[0].Contains("first match in the rules table (position 1)"));
            Check("office-details-loser", rOffice.Details![1].Contains("also matched") && rOffice.Details[1].Contains("position 2"));
            Check("office-counts-winner-only", rSub.ExecutionCount == 1 && rSuffix.ExecutionCount == 0);
            office.Rules.Reverse();        // the user drags the suffix row to the top
            Check("office-reordered-suffix-wins", engine.EvaluateNetwork(office, eth).Action == ActionKind.Disconnect);

            // 3. Gateway MAC vs SSID: no match-type ranking - whichever row is higher wins.
            var prio = Cfg();
            var ruleSsid = new TunnelRule { Kind = "network", Ssid = "Guest", Tunnel = "Full-VPN" };
            var ruleMac  = new TunnelRule { Kind = "network", MatchBy = "gatewaymac", MatchValue = "AA-BB-CC-00-11-22", Tunnel = "" };
            prio.Rules.Add(ruleSsid); prio.Rules.Add(ruleMac);          // SSID row first
            var guest = Id("wifi", "wlan", ssid: "Guest", mac: Mac);
            Check("order-ssid-row-first-wins", engine.EvaluateNetwork(prio, guest).TunnelName == "Full-VPN");
            prio.Rules.Reverse();                                        // MAC row first
            Check("order-mac-row-first-wins", engine.EvaluateNetwork(prio, guest).Action == ActionKind.Disconnect);
            var guestNoMac = Id("wifi", "wlan", ssid: "Guest");
            Check("order-mac-unresolved-falls-to-ssid", engine.EvaluateNetwork(prio, guestNoMac).TunnelName == "Full-VPN");

            // 4. Same match type follows the table too (no longest-prefix rule any more).
            var tie = Cfg();
            tie.Rules.Add(new TunnelRule { Kind = "network", MatchBy = "subnet", MatchValue = "10.0.0.0/8",  Tunnel = "Wide" });
            tie.Rules.Add(new TunnelRule { Kind = "network", MatchBy = "subnet", MatchValue = "10.20.4.0/24", Tunnel = "Narrow" });
            Eq("tie-table-order-wide-first", engine.EvaluateNetwork(tie, eth).TunnelName, "Wide");
            tie.Rules.Reverse();
            Eq("tie-table-order-narrow-first", engine.EvaluateNetwork(tie, eth).TunnelName, "Narrow");
            var tie2 = Cfg();
            tie2.Rules.Add(new TunnelRule { Kind = "network", Ssid = "Home", Tunnel = "First" });
            tie2.Rules.Add(new TunnelRule { Kind = "network", Ssid = "Home", Tunnel = "Second" });
            Eq("tie-list-order", engine.EvaluateNetwork(tie2, Id("wifi", "wlan", ssid: "Home")).TunnelName, "First");

            // 5. Trusted list with identities, wired network.
            var tr = Cfg();
            tr.TrustedNetworks.Add("suffix:corp.example.com");
            tr.Rules.Add(new TunnelRule { Kind = "trusted", TrustedWhen = "trusted",   Tunnel = "Split" });
            tr.Rules.Add(new TunnelRule { Kind = "trusted", TrustedWhen = "untrusted", Tunnel = "Full-VPN" });
            Eq("trusted-wired-office", engine.EvaluateNetwork(tr, eth).TunnelName, "Split");
            var hotel = Id("wired", "eth", suffix: "hotel.example", subnets: new[] { "192.168.1.0/24" });
            Eq("untrusted-wired-hotel", engine.EvaluateNetwork(tr, hotel).TunnelName, "Full-VPN");
            Check("trusted-reason-names-adapter", engine.EvaluateNetwork(tr, hotel).Reason.StartsWith("Untrusted network: eth"));

            // 6. Open network, no network, disabled rule, manual mode, tunnels off.
            var op = Cfg(); op.OpenWifiTunnel = "Full";
            Check("open-primary-wifi", engine.EvaluateNetwork(op, Id("wifi", "wlan", ssid: "Cafe", open: true)).TunnelName == "Full");
            Check("no-network-none", engine.EvaluateNetwork(office, null).Action == ActionKind.None);
            var dis = Cfg(); dis.Rules.Add(new TunnelRule { Kind = "network", Ssid = "Home", Tunnel = "T", Enabled = false });
            Check("disabled-ignored", engine.EvaluateNetwork(dis, Id("wifi", "wlan", ssid: "Home")).Action == ActionKind.None);
            var man = Cfg(); man.ManualMode = true; man.Rules.Add(new TunnelRule { Kind = "network", Ssid = "Home", Tunnel = "T" });
            Check("manual-none", engine.EvaluateNetwork(man, Id("wifi", "wlan", ssid: "Home")).Action == ActionKind.None);
            var off = Cfg(); off.EnableTunnels = false; off.Rules.Add(new TunnelRule { Kind = "network", Ssid = "Home", Tunnel = "T" });
            Check("tunnels-off-none", engine.EvaluateNetwork(off, Id("wifi", "wlan", ssid: "Home")).Action == ActionKind.None);

            // 7. DNS is evaluated per adapter: docked office with Wi-Fi Guest also on.
            var dns = Cfg();
            dns.Rules.Add(new TunnelRule { Kind = "network", MatchBy = "dnssuffix", MatchValue = "corp.example.com", DnsProfileId = "corp" });
            dns.Rules.Add(new TunnelRule { Kind = "network", MatchBy = "gatewaymac", MatchValue = Mac, DnsProfileId = "quad9" });
            var wifiGuest = Id("wifi", "wlan", ssid: "Guest", primary: false);
            var dEth = DnsPolicy.Evaluate(dns, eth, now);
            Check("dns-eth-first-row-wins", dEth.Action == DnsPolicy.DnsActionKind.Apply && dEth.ProfileId == "corp");   // suffix row is first
            var dWifi = DnsPolicy.Evaluate(dns, wifiGuest, now);
            Check("dns-wifi-guest-none", dWifi.Action == DnsPolicy.DnsActionKind.None);
            dns.Rules.Reverse();
            Eq("dns-reordered-mac-first", DnsPolicy.Evaluate(dns, eth, now).ProfileId, "quad9");
            var dnsSkip = Cfg();
            dnsSkip.Rules.Add(new TunnelRule { Kind = "network", MatchBy = "gatewaymac", MatchValue = Mac, Tunnel = "T" });                 // no DNS profile
            dnsSkip.Rules.Add(new TunnelRule { Kind = "network", MatchBy = "dnssuffix", MatchValue = "corp.example.com", DnsProfileId = "corp" });
            Eq("dns-skips-rule-without-profile", DnsPolicy.Evaluate(dnsSkip, eth, now).ProfileId, "corp");
            Check("dns-null-identity-default",
                DnsPolicy.Evaluate(new AppConfig { DnsAutomationEnabled = true, DefaultDnsProfileId = DnsProfile.AutomaticId }, (NetworkIdentity?)null, now).Action == DnsPolicy.DnsActionKind.Automatic);

            return (pass, fail, failures);
        }
    }
}
