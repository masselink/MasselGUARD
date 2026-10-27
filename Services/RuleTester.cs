using System;
using System.Collections.Generic;
using System.Linq;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>Outcome of testing one rule: whether its requirement holds right now, one line saying
    /// why, and optional engine notes (rule disabled, manual mode, feature off).</summary>
    public record RuleTestResult(bool Met, string Summary, IReadOnlyList<string> Notes,
                                 IReadOnlyList<RuleCheck>? Checks = null, IReadOnlyList<string>? Actions = null);

    /// <summary>One line of the test: what was checked and whether it holds (<c>Ok</c> null = information only).</summary>
    public record RuleCheck(bool? Ok, string Text);

    /// <summary>
    /// PURE "is this rule's requirement met right now?" check behind the Automation pane's Test button and
    /// the diagnostics rules section. It only tests the rule's trigger condition against the current
    /// network snapshot and clock; it does not run the precedence (another rule may still win) and it
    /// performs no action. WPF-free and shared with the CLI; covered by <see cref="RunSelfTest"/>.
    /// The text is English on purpose (activity-log line / support data).
    /// </summary>
    public static class RuleTester
    {
        public static RuleTestResult Test(TunnelRule rule, AppConfig cfg, NetworkSnapshot net, DateTime now)
        {
            var (met, summary) = rule.Kind switch
            {
                "schedule" => TestSchedule(rule, now),
                "trusted"  => TestTrusted(rule, cfg, net),
                "wifi" or "network" => TestNetwork(rule, net),
                _          => (false, $"unknown rule kind '{rule.Kind}'"),
            };

            var notes = new List<string>();
            if (!rule.Enabled)
                notes.Add("The rule is disabled: the engine ignores it.");
            if (cfg.ManualMode)
                notes.Add("Manual mode is on: all automation is off.");
            if (cfg.SimpleWifiMode && rule.IsNetworkKind && !rule.IsSimpleSsidRule)
                notes.Add("Simple Wi-Fi mode is on: this rule is more than a plain SSID rule, so it is not used.");
            if (!DnsPolicy.IsDnsOnly(rule) && !cfg.EnableTunnels)
                notes.Add("Tunnel automation is off (WireGuard feature disabled): the tunnel action would not run.");
            if (!string.IsNullOrEmpty(rule.DnsProfileId) && (!cfg.EnableDns || !cfg.DnsAutomationEnabled))
                notes.Add("DNS automation is off: the DNS part of this rule would not apply.");

            var checks = rule.Kind switch
            {
                "schedule" => ScheduleChecks(rule, now),
                "trusted"  => TrustedChecks(rule, cfg, net),
                "wifi" or "network" => NetworkChecks(rule, net, met),
                _          => new List<RuleCheck> { new(false, summary) },
            };
            return new RuleTestResult(met, summary, notes, checks, Actions(rule, cfg));
        }

        // ── What was checked, line by line (the popup) ────────────────────────

        /// <summary>What the rule does when its requirement is met (tunnel and DNS part).</summary>
        public static List<string> Actions(TunnelRule rule, AppConfig cfg)
        {
            var l = new List<string>();
            if (!DnsPolicy.IsDnsOnly(rule))
                l.Add(string.IsNullOrEmpty(rule.Tunnel) ? "Disconnect the active tunnel" : $"Connect the tunnel \"{rule.Tunnel}\"");
            if (!string.IsNullOrEmpty(rule.DnsProfileId))
            {
                var name = rule.DnsProfileId == DnsProfile.AutomaticId ? "automatic (network-provided) DNS"
                         : cfg.DnsProfiles.FirstOrDefault(p => p.Id == rule.DnsProfileId)?.Name is { } n ? $"\"{n}\"" : "(a profile that no longer exists)";
                l.Add($"Use the DNS profile {name}");
            }
            return l;
        }

        /// <summary>The value a network has for a match type, for the "this network:" part of a check.</summary>
        private static string Actual(string by, NetworkIdentity a)
        {
            string? v = by switch
            {
                NetworkMatchBy.Ssid           => a.Ssid,
                NetworkMatchBy.DnsSuffix      => a.DnsSuffix,
                NetworkMatchBy.GatewayMac     => a.GatewayMac,
                NetworkMatchBy.ConnectionType => a.Kind,
                NetworkMatchBy.AdapterName    => a.AdapterName,
                NetworkMatchBy.AdapterDesc    => a.AdapterDescription,
                NetworkMatchBy.AdapterMac     => a.AdapterMac,
                _                             => a.Subnets.Count == 0 ? null : string.Join("/", a.Subnets),
            };
            return string.IsNullOrEmpty(v) ? "none" : v;
        }

        private static List<RuleCheck> NetworkChecks(TunnelRule rule, NetworkSnapshot net, bool met)
        {
            var list = new List<RuleCheck>();
            var hit = met ? net.Adapters.OrderByDescending(a => a.IsPrimary).FirstOrDefault(a => NetworkMatcher.RuleMatches(rule, a)) : null;
            var basis = hit ?? net.Primary ?? net.Adapters.FirstOrDefault();
            if (basis == null) { list.Add(new RuleCheck(false, "There is no connected network to test.")); return list; }
            list.Add(new RuleCheck(null, $"Network tested: {Label(basis)}" + (met ? "" : basis.IsPrimary ? " (the primary network)" : "")));
            foreach (var c in rule.EffectiveConditions)
                list.Add(new RuleCheck(NetworkMatcher.ConditionHolds(c, basis), $"{c.ToPlain()}   (this network: {Actual(c.By, basis)})"));
            if (hit != null)
                list.Add(new RuleCheck(hit.IsPrimary, hit.IsPrimary
                    ? "It is the primary network, so the rule decides the tunnel"
                    : $"It is not the primary network ({Label(net.Primary)} is), so this rule would not drive the tunnel"));
            return list;
        }

        private static List<RuleCheck> TrustedChecks(TunnelRule rule, AppConfig cfg, NetworkSnapshot net)
        {
            var primary = net.Primary;
            if (primary == null) return new List<RuleCheck> { new(false, "There is no connected network to test.") };
            bool trusted = NetworkMatcher.IsTrusted(cfg.TrustedNetworks, primary);
            bool needs = rule.TrustedWhenOnList;
            return new List<RuleCheck>
            {
                new(null, $"Network tested: {Label(primary)} (the primary network)"),
                new(trusted == needs, $"The rule needs a network that is {(needs ? "on" : "not on")} the trusted list; this one is {(trusted ? "on" : "not on")} it"
                                      + (cfg.TrustedNetworks.Count == 0 ? " (the list is empty)" : "")),
            };
        }

        private static List<RuleCheck> ScheduleChecks(TunnelRule rule, DateTime now)
        {
            if (!TimeSpan.TryParse(rule.StartTime, out var start) || !TimeSpan.TryParse(rule.EndTime, out var end))
                return new List<RuleCheck> { new(false, $"The start or end time is not a valid time of day ({rule.StartTime} - {rule.EndTime})") };
            if (start == end) return new List<RuleCheck> { new(false, "The window has zero length (start equals end)") };
            bool allDays = rule.Days is not { Count: > 0 };
            bool dayOk = allDays || rule.Days!.Contains((int)now.DayOfWeek);
            var t = now.TimeOfDay;
            bool timeOk = start < end ? t >= start && t < end : t >= start || t < end;   // a window that passes midnight
            return new List<RuleCheck>
            {
                new(dayOk, allDays ? $"Day: the rule runs every day (today is {now:dddd})" : $"Day: today is {now:dddd}; the rule runs on {rule.ScheduleSummary}"),
                new(timeOk, $"Time: it is {now:HH:mm}; the rule runs from {rule.StartTime} to {rule.EndTime}"),
            };
        }

        // ── Kinds ─────────────────────────────────────────────────────────────

        private static (bool, string) TestNetwork(TunnelRule rule, NetworkSnapshot net)
        {
            var conds = rule.EffectiveConditions;
            if (conds.Count == 0)
                return (false, "the rule has no conditions set");

            // One plain condition keeps the detailed single-match messages.
            if (conds.Count == 1 && !conds[0].Not)
                return TestSingle(conds[0].By, conds[0].Value, net);

            // Several conditions and/or a NOT: all must hold on the SAME network.
            var hit = net.Adapters.OrderByDescending(a => a.IsPrimary)
                .FirstOrDefault(a => NetworkMatcher.RuleMatches(rule, a));
            if (hit != null)
            {
                string where = hit.IsPrimary
                    ? "the primary network"
                    : $"not the primary network ({Label(net.Primary)} is primary, so this rule would not drive the tunnel)";
                return (true, $"{(conds.Count == 1 ? "the condition holds" : $"all {conds.Count} conditions hold")} on {Label(hit)}: {where}");
            }

            // Not met: say which conditions hold or fail on the network that matters (the primary one).
            var basis = net.Primary ?? net.Adapters.FirstOrDefault();
            if (basis == null) return (false, "no connected network to judge");
            var parts = conds.Select(c => $"{c.ToPlain()}: {(NetworkMatcher.ConditionHolds(c, basis) ? "holds" : "fails")}");
            return (false, $"not all conditions hold on {Label(basis)} ({string.Join("; ", parts)})");
        }

        private static (bool, string) TestSingle(string by, string value, NetworkSnapshot net)
        {
            string label = TunnelRule.MatchByLabel(by);
            if (string.IsNullOrWhiteSpace(value))
                return (false, $"the rule has no {label} set");

            var hit = net.Adapters.OrderByDescending(a => a.IsPrimary)
                .FirstOrDefault(a => NetworkMatcher.Matches(by, value, a));
            if (hit != null)
            {
                string where = hit.IsPrimary
                    ? "the primary network"
                    : $"not the primary network ({Label(net.Primary)} is primary, so this rule would not drive the tunnel)";
                return (true, by == NetworkMatchBy.Ssid
                    ? $"connected to {Label(hit)}: {where}"
                    : $"{Label(hit)} matches {label} {value}: {where}");
            }

            if (by == NetworkMatchBy.Ssid)
            {
                var current = net.Adapters.Where(a => !string.IsNullOrEmpty(a.Ssid)).Select(a => $"\"{a.Ssid}\"").ToList();
                return (false, current.Count == 0
                    ? $"not connected to any Wi-Fi network (the rule needs \"{value}\")"
                    : $"connected to {string.Join(", ", current)} (the rule needs \"{value}\")");
            }

            // Show what the connected networks actually have, so a mismatch is obvious.
            var have = net.Adapters.Select(a => by switch
            {
                NetworkMatchBy.DnsSuffix  => a.DnsSuffix,
                NetworkMatchBy.GatewayMac => a.GatewayMac,
                NetworkMatchBy.ConnectionType => a.Kind,
                NetworkMatchBy.AdapterName    => a.AdapterName,
                NetworkMatchBy.AdapterDesc    => a.AdapterDescription,
                NetworkMatchBy.AdapterMac     => a.AdapterMac,
                _                         => a.Subnets.Count == 0 ? null : string.Join("/", a.Subnets),
            }).Where(s => !string.IsNullOrEmpty(s)).ToList();
            return (false, have.Count == 0
                ? $"no connected network has a {label} (the rule needs {value})"
                : $"connected networks have {string.Join(", ", have)} (the rule needs {value})");
        }

        private static (bool, string) TestTrusted(TunnelRule rule, AppConfig cfg, NetworkSnapshot net)
        {
            var primary = net.Primary;
            if (primary == null)
                return (false, "no connected network to judge");

            bool trusted = NetworkMatcher.IsTrusted(cfg.TrustedNetworks, primary);
            bool needsTrusted = rule.TrustedWhenOnList;
            bool met = needsTrusted ? trusted : !trusted;

            string have = trusted ? "on the trusted list" : "not on the trusted list";
            string want = needsTrusted ? "on the list" : "not on the list";
            string extra = cfg.TrustedNetworks.Count == 0 ? " (the trusted list is empty)" : "";
            return (met, $"{Label(primary)} is {have}; the rule needs a network {want}{extra}");
        }

        private static (bool, string) TestSchedule(TunnelRule rule, DateTime now)
        {
            string stamp = $"{now:ddd HH:mm}";
            if (!TimeSpan.TryParse(rule.StartTime, out var start) || !TimeSpan.TryParse(rule.EndTime, out var end))
                return (false, $"the start or end time is not a valid HH:mm ({rule.StartTime} - {rule.EndTime})");
            if (start == end)
                return (false, "the window has zero length (start equals end)");

            if (DnsPolicy.IsWithinSchedule(rule, now))
                return (true, $"now ({stamp}) is inside {rule.ScheduleSummary}");

            if (rule.Days is { Count: > 0 } && !rule.Days.Contains((int)now.DayOfWeek))
                return (false, $"today ({now:ddd}) is not one of the rule's days ({rule.ScheduleSummary})");
            return (false, $"now ({stamp}) is outside {rule.StartTime}-{rule.EndTime}");
        }

        // ── Formatting ────────────────────────────────────────────────────────

        /// <summary>Short human label for a network: 'Wi-Fi "Home"' or 'Ethernet 2 (wired)'.</summary>
        public static string Label(NetworkIdentity? id)
        {
            if (id == null) return "no network";
            if (id.IsWifi) return string.IsNullOrEmpty(id.Ssid) ? $"{id.AdapterName} (Wi-Fi)" : $"Wi-Fi \"{id.Ssid}\"";
            return $"{id.AdapterName} (wired)";
        }

        // ── Self-test (run via `MasselGUARDcli selftest`) ─────────────────────

        public static (int passed, int failed, List<string> failures) RunSelfTest()
        {
            int pass = 0, fail = 0;
            var failures = new List<string>();
            void Check(string name, bool ok) { if (ok) pass++; else { fail++; failures.Add(name); } }

            NetworkIdentity Wifi(string ssid, bool primary) =>
                new("wlan", "Wi-Fi", "wifi", ssid, false, null, null, null, Array.Empty<string>(), null, 50, primary);
            NetworkIdentity Wired(string suffix, bool primary) =>
                new("eth", "Ethernet 2", "wired", null, false, suffix, "aa:bb:cc:00:11:22", null,
                    new[] { "10.20.4.0/24" }, null, 25, primary);
            NetworkSnapshot Snap(params NetworkIdentity[] a) => new(a);

            var cfg = new AppConfig { EnableTunnels = true };
            var monday = new DateTime(2026, 9, 14, 12, 0, 0);     // Monday noon
            var saturday = new DateTime(2026, 9, 19, 12, 0, 0);

            // SSID rule
            var home = new TunnelRule { Kind = "wifi", Ssid = "Home", Tunnel = "T" };
            Check("ssid-met",        Test(home, cfg, Snap(Wifi("home", true)), monday).Met);
            Check("ssid-not-met",    !Test(home, cfg, Snap(Wifi("Cafe", true)), monday).Met);
            Check("ssid-no-wifi",    !Test(home, cfg, Snap(Wired("corp.example.com", true)), monday).Met);
            Check("ssid-empty-net",  !Test(home, cfg, NetworkSnapshot.Empty, monday).Met);
            Check("ssid-empty-rule", !Test(new TunnelRule { Kind = "wifi", Ssid = "" }, cfg, Snap(Wifi("Home", true)), monday).Met);
            Check("ssid-secondary-still-met",
                Test(home, cfg, Snap(Wifi("Home", false), Wired(null!, true)), monday).Met);
            Check("ssid-secondary-says-so",
                Test(home, cfg, Snap(Wifi("Home", false), Wired(null!, true)), monday).Summary.Contains("not the primary"));

            // the line-by-line checks and the actions (the popup)
            var rHome = Test(home, cfg, Snap(Wifi("Home", true)), monday);
            Check("checks-met",        rHome.Checks is { Count: 3 } && rHome.Checks.All(c => c.Ok != false) && rHome.Checks[1].Text.Contains("Home") && rHome.Checks[2].Ok == true);
            var rCafe = Test(home, cfg, Snap(Wifi("Cafe", true)), monday);
            Check("checks-not-met",    rCafe.Checks is { Count: 2 } && rCafe.Checks[1].Ok == false && rCafe.Checks[1].Text.Contains("Cafe"));
            var rNone = Test(home, cfg, NetworkSnapshot.Empty, monday);
            Check("checks-no-network", rNone.Checks is { Count: 1 } && rNone.Checks[0].Ok == false);
            var rSec = Test(home, cfg, Snap(Wifi("Home", false), Wired(null!, true)), monday);
            Check("checks-secondary",  rSec.Met && rSec.Checks != null && rSec.Checks[^1].Ok == false && rSec.Checks[^1].Text.Contains("not the primary"));
            Check("actions-tunnel",    rHome.Actions is { Count: 1 } && rHome.Actions[0].Contains("\"T\""));
            Check("actions-disconnect", Test(new TunnelRule { Kind = "wifi", Ssid = "Home" }, cfg, Snap(Wifi("Home", true)), monday).Actions![0].StartsWith("Disconnect"));
            var dnsCfg = new AppConfig { EnableTunnels = true }; dnsCfg.DnsProfiles.Add(new DnsProfile { Id = "p1", Name = "Cloudflare" });
            var dnsRule = new TunnelRule { Kind = "wifi", Ssid = "Home", Tunnel = "T", DnsProfileId = "p1" };
            Check("actions-dns",       Test(dnsRule, dnsCfg, Snap(Wifi("Home", true)), monday).Actions is { Count: 2 } a2 && a2[1].Contains("Cloudflare"));
            Check("actions-dns-only",  Test(new TunnelRule { Kind = "wifi", Ssid = "Home", DnsProfileId = DnsProfile.AutomaticId }, dnsCfg, Snap(Wifi("Home", true)), monday).Actions is { Count: 1 } a3 && a3[0].Contains("automatic"));
            var rSched = new TunnelRule { Kind = "schedule", StartTime = "08:00", EndTime = "17:00", Days = new List<int> { 1, 2, 3, 4, 5 }, Tunnel = "T" };
            var sOk = Test(rSched, cfg, NetworkSnapshot.Empty, monday).Checks!;
            Check("checks-schedule-ok", sOk.Count == 2 && sOk.All(c => c.Ok == true));
            var sSat = Test(rSched, cfg, NetworkSnapshot.Empty, saturday).Checks!;
            Check("checks-schedule-day", sSat[0].Ok == false && sSat[1].Ok == true);
            var sNight = Test(new TunnelRule { Kind = "schedule", StartTime = "22:00", EndTime = "06:00", Tunnel = "T" }, cfg, NetworkSnapshot.Empty, new DateTime(2026, 9, 14, 1, 0, 0)).Checks!;
            Check("checks-schedule-midnight", sNight.All(c => c.Ok == true));
            var tr = Test(new TunnelRule { Kind = "trusted", TrustedWhen = "untrusted", Tunnel = "T" }, cfg, Snap(Wifi("Cafe", true)), monday).Checks!;
            Check("checks-trusted",    tr.Count == 2 && tr[1].Ok == true);

            // Non-SSID network rules (kind "network")
            var bySuffix = new TunnelRule { Kind = "network", MatchBy = "dnssuffix", MatchValue = "corp.example.com" };
            var byMac    = new TunnelRule { Kind = "network", MatchBy = "gatewaymac", MatchValue = "AA-BB-CC-00-11-22" };
            var bySubnet = new TunnelRule { Kind = "network", MatchBy = "subnet", MatchValue = "10.0.0.0/8" };
            var office   = Wired("corp.example.com", true);
            Check("net-suffix-met",   Test(bySuffix, cfg, Snap(office), monday).Met);
            Check("net-mac-met",      Test(byMac, cfg, Snap(office), monday).Met);
            Check("net-subnet-met",   Test(bySubnet, cfg, Snap(office), monday).Met);
            Check("net-suffix-not-met-says-have", Test(bySuffix, cfg, Snap(Wired("hotel.example", true)), monday).Summary.Contains("hotel.example"));
            Check("net-mac-none",     Test(byMac, cfg, Snap(Wifi("Home", true)), monday).Summary.Contains("no connected network has a gateway MAC"));
            Check("net-empty-value",  !Test(new TunnelRule { Kind = "network", MatchBy = "subnet", MatchValue = "" }, cfg, Snap(office), monday).Met);

            // Conditions: AND + NOT
            TunnelRule Conds(params RuleCondition[] cs) { var r = new TunnelRule { Kind = "network" }; r.SetConditions(cs); return r; }
            RuleCondition C(string by, string v, bool not = false) => new() { By = by, Value = v, Not = not };
            var andRule = Conds(C("dnssuffix", "corp.example.com"), C("subnet", "10.0.0.0/8"));
            var notRule = Conds(C("dnssuffix", "corp.example.com"), C("subnet", "10.0.0.0/8", true));
            var r1c = Test(andRule, cfg, Snap(office), monday);
            Check("cond-tester-and-met",     r1c.Met && r1c.Summary.Contains("all 2 conditions hold"));
            var r2c = Test(notRule, cfg, Snap(office), monday);
            Check("cond-tester-not-fails",   !r2c.Met && r2c.Summary.Contains("NOT subnet 10.0.0.0/8: fails") && r2c.Summary.Contains("DNS suffix corp.example.com: holds"));
            Check("cond-tester-only-not",    Test(Conds(C("ssid", "Guest", true)), cfg, Snap(Wifi("Home", true)), monday).Met);
            Check("cond-tester-same-network", !Test(Conds(C("ssid", "Home"), C("dnssuffix", "corp.example.com")),
                                                   cfg, Snap(Wifi("Home", true), Wired("corp.example.com", false)), monday).Met);   // conditions split over two adapters
            Check("cond-tester-none",        !Test(new TunnelRule { Kind = "network" }, cfg, Snap(office), monday).Met);

            // Simple Wi-Fi mode note
            var simpleCfg = new AppConfig { EnableTunnels = true, SimpleWifiMode = true };
            Check("note-simple-mode-advanced-rule", Test(andRule, simpleCfg, Snap(office), monday).Notes.Any(n => n.Contains("Simple Wi-Fi mode")));
            Check("note-simple-mode-plain-ssid-clean", !Test(home, simpleCfg, Snap(Wifi("Home", true)), monday).Notes.Any(n => n.Contains("Simple Wi-Fi mode")));

            // Trusted rule (uses the primary network)
            var cfgT = new AppConfig { EnableTunnels = true };
            cfgT.TrustedNetworks.Add("Home");
            var untrustedRule = new TunnelRule { Kind = "trusted", TrustedWhen = "untrusted" };
            var trustedRule   = new TunnelRule { Kind = "trusted", TrustedWhen = "trusted" };
            Check("trusted-untrusted-rule-on-cafe", Test(untrustedRule, cfgT, Snap(Wifi("Cafe", true)), monday).Met);
            Check("trusted-untrusted-rule-on-home", !Test(untrustedRule, cfgT, Snap(Wifi("Home", true)), monday).Met);
            Check("trusted-trusted-rule-on-home",   Test(trustedRule, cfgT, Snap(Wifi("Home", true)), monday).Met);
            Check("trusted-trusted-rule-on-cafe",   !Test(trustedRule, cfgT, Snap(Wifi("Cafe", true)), monday).Met);
            Check("trusted-no-network",             !Test(untrustedRule, cfgT, NetworkSnapshot.Empty, monday).Met);
            var cfgT2 = new AppConfig { EnableTunnels = true };
            cfgT2.TrustedNetworks.Add("suffix:corp.example.com");
            Check("trusted-wired-suffix",           Test(trustedRule, cfgT2, Snap(Wired("corp.example.com", true)), monday).Met);

            // Schedule rule
            var sched = new TunnelRule { Kind = "schedule", StartTime = "09:00", EndTime = "17:00",
                                         Days = new List<int> { 1, 2, 3, 4, 5 } };
            Check("sched-in-window",   Test(sched, cfg, NetworkSnapshot.Empty, monday).Met);
            Check("sched-wrong-day",   !Test(sched, cfg, NetworkSnapshot.Empty, saturday).Met);
            Check("sched-wrong-day-says-so", Test(sched, cfg, NetworkSnapshot.Empty, saturday).Summary.Contains("not one of the rule's days"));
            Check("sched-outside-time", !Test(sched, cfg, NetworkSnapshot.Empty, monday.AddHours(7)).Met);
            Check("sched-zero-length", !Test(new TunnelRule { Kind = "schedule", StartTime = "09:00", EndTime = "09:00" }, cfg, NetworkSnapshot.Empty, monday).Met);
            Check("sched-bad-time",    !Test(new TunnelRule { Kind = "schedule", StartTime = "x", EndTime = "y" }, cfg, NetworkSnapshot.Empty, monday).Met);
            Check("sched-overnight",   Test(new TunnelRule { Kind = "schedule", StartTime = "22:00", EndTime = "06:00", Days = new() }, cfg, NetworkSnapshot.Empty, monday.AddHours(11)).Met);

            // Notes
            var disabled = new TunnelRule { Kind = "wifi", Ssid = "Home", Enabled = false };
            Check("note-disabled", Test(disabled, cfg, Snap(Wifi("Home", true)), monday).Notes.Any(n => n.Contains("disabled")));
            var manual = new AppConfig { EnableTunnels = true, ManualMode = true };
            Check("note-manual", Test(home, manual, Snap(Wifi("Home", true)), monday).Notes.Any(n => n.Contains("Manual mode")));
            Check("note-tunnels-off", Test(home, new AppConfig { EnableTunnels = false }, Snap(Wifi("Home", true)), monday).Notes.Any(n => n.Contains("Tunnel automation is off")));
            Check("note-clean", Test(home, cfg, Snap(Wifi("Home", true)), monday).Notes.Count == 0);
            Check("unknown-kind", !Test(new TunnelRule { Kind = "bogus" }, cfg, NetworkSnapshot.Empty, monday).Met);

            return (pass, fail, failures);
        }
    }
}
