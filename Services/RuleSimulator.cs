using System;
using System.Collections.Generic;
using System.Linq;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>One line of the "Advanced test" reasoning panel.</summary>
    public enum SimLevel { Info, Good, Bad, Muted, Result }
    public record SimStep(string Text, SimLevel Level, int Indent = 0);

    public record SimResult(
        RuleEngine.RuleResult Tunnel,
        DnsPolicy.DnsResult   Dns,
        TunnelRule?           Winner,
        IReadOnlyList<SimStep> Steps);

    /// <summary>
    /// PURE "what would the automation do on THIS network?" used by the Advanced test window: describe a
    /// network (SSID, DNS suffix, gateway MAC, subnets, open or not), and get the tunnel and DNS decision plus a
    /// step-by-step reasoning that follows the real precedence. The decision itself always comes from the real
    /// <see cref="RuleEngine"/> / <see cref="DnsPolicy"/> on a CLONE of the config (so execution counters never move);
    /// the narrative is built from the same matching helpers. WPF-free, CLI-shared, covered by <see cref="RunSelfTest"/>.
    /// Text is English on purpose (support data, like the rule/DNS reasons).
    /// </summary>
    public static class RuleSimulator
    {
        public static SimResult Run(AppConfig cfg, NetworkIdentity net, DateTime now)
        {
            var sim    = cfg.DeepClone();                 // the real engine increments ExecutionCount: do it on a copy
            var engine = new RuleEngine();
            var tunnel = engine.EvaluateNetwork(sim, net);
            var dns    = DnsPolicy.Evaluate(sim, net, now);
            var steps  = new List<SimStep>();
            void S(string t, SimLevel l = SimLevel.Info, int i = 0) => steps.Add(new SimStep(t, l, i));

            // ── The network being tested ──────────────────────────────────────
            S($"Testing {RuleTester.Label(net)}" + (net.IsOpen ? " (open network)" : ""), SimLevel.Info);
            S($"DNS suffix: {net.DnsSuffix ?? "-"}   gateway MAC: {net.GatewayMac ?? "-"}   " +
              $"subnets: {(net.Subnets.Count == 0 ? "-" : string.Join(", ", net.Subnets))}", SimLevel.Muted, 1);

            // ── 1. Automation switches ────────────────────────────────────────
            S("1. Automation", SimLevel.Info);
            bool tunnelAxis = true;
            if (cfg.ManualMode)        { S("Manual mode is ON: all automation is off, nothing would happen.", SimLevel.Bad, 1); tunnelAxis = false; }
            else if (!cfg.EnableTunnels) { S("The WireGuard feature is OFF: tunnel rules are inert (DNS rules can still apply).", SimLevel.Bad, 1); tunnelAxis = false; }
            else S("Automation is on and tunnels are enabled.", SimLevel.Good, 1);

            TunnelRule? winner = null;
            if (tunnelAxis)
            {
                // ── 2. Open network protection ────────────────────────────────
                S("2. Open-network protection", SimLevel.Info);
                bool openDecided = false;
                if (!net.IsOpen)                                 S("The network is not open: skipped.", SimLevel.Muted, 1);
                else if (string.IsNullOrEmpty(cfg.OpenWifiTunnel)) S("The network is open, but no open-network tunnel is set: skipped.", SimLevel.Muted, 1);
                else { S($"The network is open and the open-network tunnel is '{cfg.OpenWifiTunnel}' -> this decides. Later steps are not reached.", SimLevel.Good, 1); openDecided = true; }

                // ── 3. Network rules, in table order ──────────────────────────
                S("3. Network rules (rules table, top-down, first match wins)", SimLevel.Info);
                if (openDecided) S("Not reached (open-network protection already decided).", SimLevel.Muted, 1);
                else
                {
                    bool decided = false;
                    bool any = false;
                    for (int i = 0; i < cfg.Rules.Count; i++)
                    {
                        var r = cfg.Rules[i];
                        if (!r.IsNetworkKind) continue;
                        any = true;
                        string head = $"#{i + 1} \"{r.RuleName}\"";
                        if (!r.Enabled) { S($"{head}: disabled, skipped.", SimLevel.Muted, 1); continue; }
                        var conds = r.EffectiveConditions;
                        if (conds.Count == 0) { S($"{head}: has no conditions, never matches.", SimLevel.Muted, 1); continue; }

                        S($"{head}:", SimLevel.Info, 1);
                        foreach (var c in conds)
                        {
                            bool holds = NetworkMatcher.ConditionHolds(c, net);
                            string why = c.Not && c.By == NetworkMatchBy.GatewayMac && string.IsNullOrEmpty(net.GatewayMac)
                                ? " (the gateway MAC is unknown, so a negation cannot be confirmed)" : "";
                            S($"{c.ToPlain().Replace("NOT ", "")} {(c.Not ? "is not" : "is")}: {(holds ? "holds" : "fails")}{why}",
                              holds ? SimLevel.Good : SimLevel.Bad, 2);
                        }
                        bool all = NetworkMatcher.RuleMatches(r, net);
                        if (!all)                       S("not all conditions hold: no match.", SimLevel.Bad, 2);
                        else if (!decided)              { S("all conditions hold: FIRST MATCH, this rule decides.", SimLevel.Good, 2); decided = true; winner = r; }
                        else                            S("all conditions hold, but a rule higher in the table already matched: ignored.", SimLevel.Muted, 2);
                    }
                    if (!any) S("There are no network rules.", SimLevel.Muted, 1);
                    else if (!decided) S("No network rule matched.", SimLevel.Info, 1);

                    // ── 4. Trusted-network rules ──────────────────────────────
                    S("4. Trusted-network rules", SimLevel.Info);
                    if (decided) S("Not reached (a network rule already decided).", SimLevel.Muted, 1);
                    else
                    {
                        bool trusted = NetworkMatcher.IsTrusted(cfg.TrustedNetworks, net);
                        var tr = cfg.Rules.Select((r, i) => (r, i)).Where(x => x.r.Kind == "trusted").ToList();
                        if (tr.Count == 0) S("There are no trusted-network rules.", SimLevel.Muted, 1);
                        else
                        {
                            S($"The network is {(trusted ? "ON" : "NOT on")} the trusted list ({cfg.TrustedNetworks.Count} entries).", SimLevel.Info, 1);
                            foreach (var (r, i) in tr)
                            {
                                string head = $"#{i + 1} \"{r.RuleName}\"";
                                if (!r.Enabled) { S($"{head}: disabled, skipped.", SimLevel.Muted, 1); continue; }
                                bool side = r.TrustedWhenOnList ? trusted : !trusted;
                                string need = r.TrustedWhenOnList ? "on the list" : "not on the list";
                                if (!decided && side) { S($"{head}: fires for networks {need}: this rule decides.", SimLevel.Good, 1); decided = true; winner = r; }
                                else if (side)        S($"{head}: would fire ({need}), but a rule above already decided.", SimLevel.Muted, 1);
                                else                  S($"{head}: needs a network {need}: skipped.", SimLevel.Bad, 1);
                            }
                        }
                    }

                    // ── 5. Default action ─────────────────────────────────────
                    S("5. Default action", SimLevel.Info);
                    if (decided) S("Not reached.", SimLevel.Muted, 1);
                    else S(cfg.DefaultAction switch
                    {
                        "disconnect" => "No rule decided: the default action is DISCONNECT.",
                        "activate" when !string.IsNullOrEmpty(cfg.DefaultTunnel) => $"No rule decided: the default action is ACTIVATE '{cfg.DefaultTunnel}'.",
                        _ => "No rule decided and no default action is set: nothing happens.",
                    }, SimLevel.Info, 1);
                }
                S("Schedule rules run on their own timer and are not part of this test.", SimLevel.Muted);
            }

            // ── Result ────────────────────────────────────────────────────────
            S("Result", SimLevel.Info);
            string tunnelText = tunnel.Action switch
            {
                RuleEngine.ActionKind.Activate   => $"Tunnel: CONNECT '{tunnel.TunnelName}'  ({tunnel.Reason})",
                RuleEngine.ActionKind.Disconnect => $"Tunnel: DISCONNECT  ({tunnel.Reason})",
                _                                => $"Tunnel: no action  ({tunnel.Reason})",
            };
            S(tunnelText, SimLevel.Result, 1);

            string dnsName = dns.Action switch
            {
                DnsPolicy.DnsActionKind.Apply     => "apply '" + (cfg.DnsProfiles.FirstOrDefault(p => p.Id == dns.ProfileId)?.Name ?? dns.ProfileId) + "'",
                DnsPolicy.DnsActionKind.Automatic => "automatic (DHCP)",
                _                                 => "no change",
            };
            S($"DNS for this adapter: {dnsName}  ({dns.Reason})", SimLevel.Result, 1);
            if (!cfg.EnableDns || !cfg.DnsAutomationEnabled)
                S("DNS automation is off, so no DNS rule would be applied.", SimLevel.Muted, 1);

            return new SimResult(tunnel, dns, winner, steps);
        }

        /// <summary>Builds the tested network from typed values. Null value fields are simply absent.</summary>
        public static NetworkIdentity Describe(bool wired, string? ssid, bool open, string? dnsSuffix,
                                               string? gatewayMac, IEnumerable<string>? subnets) =>
            new("simulated", wired ? "Wired (simulated)" : "Wi-Fi (simulated)",
                wired ? NetworkIdentity.KindWired : NetworkIdentity.KindWifi,
                wired || string.IsNullOrWhiteSpace(ssid) ? null : ssid!.Trim(),
                !wired && open,
                NetworkMatcher.NormalizeSuffix(dnsSuffix),
                NetworkMatcher.NormalizeMac(gatewayMac),
                null,
                (subnets ?? Array.Empty<string>()).ToList(),
                null, 25, true);

        // ── Self-test (run via `MasselGUARDcli selftest`) ─────────────────────

        public static (int passed, int failed, List<string> failures) RunSelfTest()
        {
            int pass = 0, fail = 0;
            var failures = new List<string>();
            void Check(string name, bool ok) { if (ok) pass++; else { fail++; failures.Add(name); } }
            var now = new DateTime(2026, 9, 14, 12, 0, 0);

            RuleCondition C(string by, string v, bool not = false) => new() { By = by, Value = v, Not = not };
            TunnelRule Net(string tunnel, string name, params RuleCondition[] cs)
            { var r = new TunnelRule { Kind = "network", Tunnel = tunnel, Name = name }; r.SetConditions(cs); return r; }

            var cfg = new AppConfig { EnableTunnels = true, EnableDns = true, DnsAutomationEnabled = true };
            var rGuest  = Net("Full", "Guest-not-hotel", C("ssid", "Guest"), C("gatewaymac", "aa:bb:cc:00:99:99", true));
            var rOffice = Net("Split", "Office", C("dnssuffix", "corp.example.com"), C("subnet", "10.0.0.0/8"));
            var rAny    = Net("Other", "Any-10", C("subnet", "10.0.0.0/8"));
            cfg.Rules.AddRange(new[] { rGuest, rOffice, rAny });

            var office = Describe(true, null, false, "corp.example.com", "aa:bb:cc:00:11:22", new[] { "10.20.4.0/24" });
            var r1 = Run(cfg, office, now);
            Check("sim-office-winner",  r1.Winner == rOffice && r1.Tunnel.TunnelName == "Split");
            Check("sim-office-ignores-lower", r1.Steps.Any(s => s.Text.Contains("a rule higher in the table already matched")));
            Check("sim-office-first-match-line", r1.Steps.Any(s => s.Text.Contains("FIRST MATCH")));
            Check("sim-office-result-line", r1.Steps.Any(s => s.Level == SimLevel.Result && s.Text.Contains("CONNECT 'Split'")));
            Check("sim-counters-untouched", rOffice.ExecutionCount == 0 && cfg.Rules.All(r => r.ExecutionCount == 0));

            var guestOk = Describe(false, "Guest", false, null, "aa:bb:cc:00:11:22", new[] { "192.168.1.0/24" });
            Check("sim-guest-not-hotel-wins", Run(cfg, guestOk, now).Winner == rGuest);
            var guestHotel = Describe(false, "Guest", false, null, "aa:bb:cc:00:99:99", new[] { "192.168.1.0/24" });
            var rh = Run(cfg, guestHotel, now);
            Check("sim-guest-hotel-blocked", rh.Winner == null && rh.Steps.Any(s => s.Text.Contains("fails")));
            var guestNoMac = Describe(false, "Guest", false, null, null, new[] { "192.168.1.0/24" });
            Check("sim-negation-needs-known-mac", Run(cfg, guestNoMac, now).Steps.Any(s => s.Text.Contains("cannot be confirmed")));

            // gates
            var manual = cfg.DeepClone(); manual.ManualMode = true;
            var rm = Run(manual, office, now);
            Check("sim-manual-mode", rm.Tunnel.Action == RuleEngine.ActionKind.None && rm.Steps.Any(s => s.Text.Contains("Manual mode is ON")));
            var open = cfg.DeepClone(); open.OpenWifiTunnel = "OpenVPN";
            var ro = Run(open, Describe(false, "Cafe", true, null, null, null), now);
            Check("sim-open-protection", ro.Tunnel.TunnelName == "OpenVPN" && ro.Steps.Any(s => s.Text.Contains("this decides")));

            // trusted + default
            var tc = new AppConfig { EnableTunnels = true, DefaultAction = "disconnect" };
            tc.TrustedNetworks.Add("Home");
            tc.Rules.Add(new TunnelRule { Kind = "trusted", TrustedWhen = "untrusted", Tunnel = "Full", Name = "Protect" });
            var rt = Run(tc, Describe(false, "Cafe", false, null, null, null), now);
            Check("sim-trusted-decides", rt.Winner != null && rt.Tunnel.TunnelName == "Full" && rt.Steps.Any(s => s.Text.Contains("this rule decides")));
            var rd = Run(tc, Describe(false, "Home", false, null, null, null), now);
            Check("sim-default-action", rd.Winner == null && rd.Tunnel.Action == RuleEngine.ActionKind.Disconnect && rd.Steps.Any(s => s.Text.Contains("default action is DISCONNECT")));

            // Describe normalises
            var d = Describe(false, " Home ", true, ".Corp.Example.com.", "AA-BB-CC-00-11-22", null);
            Check("sim-describe-normalises", d.Ssid == "Home" && d.IsOpen && d.DnsSuffix == "corp.example.com" && d.GatewayMac == "aa:bb:cc:00:11:22");
            var dw = Describe(true, "Ignored", true, null, null, null);
            Check("sim-describe-wired-no-ssid", dw.Ssid == null && !dw.IsOpen && dw.IsWired);

            return (pass, fail, failures);
        }
    }
}
