using System;
using System.Collections.Generic;
using System.Linq;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Pure DNS-rule precedence — the DNS axis of Model C (see
    /// <c>docs/DnsAutomation-Design.md</c>). Decides which resolver a network should use,
    /// independently of any tunnel action. No side-effects, no UI, no Windows calls — the
    /// applying/reverting lives in <c>DnsService</c> (GUI-side).
    ///
    /// This lives in a CLI-compiled file (added to <c>MasselGUARDcli.csproj</c>) so the
    /// precedence is exercised headlessly by <c>MasselGUARDcli selftest</c>
    /// (<see cref="RunSelfTest"/>) — <c>RuleEngine</c> itself is GUI-only. <c>RuleEngine.EvaluateDns</c>
    /// is a thin wrapper over <see cref="Evaluate"/>; <c>RuleEngine.EvaluateWifi</c> uses
    /// <see cref="IsDnsOnly"/> to skip DNS-only rules on the tunnel axis, and
    /// <see cref="IsWithinSchedule"/> is the single schedule-window implementation.
    /// </summary>
    public static class DnsPolicy
    {
        public enum DnsActionKind
        {
            /// <summary>Make no DNS change.</summary>
            None,
            /// <summary>Apply the resolver in <see cref="DnsResult.ProfileId"/>.</summary>
            Apply,
            /// <summary>Revert the interface to DHCP / network-provided DNS.</summary>
            Automatic,
        }

        public record DnsResult(DnsActionKind Action, string? ProfileId, string Reason);

        private static readonly DnsResult NoAction = new(DnsActionKind.None, null, "No DNS rule");

        /// <summary>
        /// True when a rule expresses a DNS action but NO tunnel action (empty Tunnel +
        /// non-empty DnsProfileId). The tunnel engine (<c>RuleEngine.EvaluateWifi</c>) skips
        /// these so a DNS-only rule never disconnects a tunnel — an empty Tunnel means
        /// "disconnect" only when the rule carries no DNS profile.
        /// </summary>
        public static bool IsDnsOnly(TunnelRule r) =>
            string.IsNullOrEmpty(r.Tunnel) && !string.IsNullOrEmpty(r.DnsProfileId);

        /// <summary>
        /// Evaluate the DNS action for the current network. Precedence (first match wins),
        /// mirroring <c>RuleEngine.EvaluateWifi</c> so the two axes are predictable:
        ///   1. DNS automation off (ManualMode or !DnsAutomationEnabled) → None.
        ///   2. Open-network profile (OpenWifiDnsProfileId) on an open network.
        ///   3. SSID rule — enabled "wifi" rule matching the SSID with a DnsProfileId.
        ///   4. Trusted rule — enabled "trusted" rule firing on its side of the list.
        ///   5. Schedule rule — enabled "schedule" rule currently in-window.
        ///   6. Default (DefaultDnsProfileId).
        /// Steps 3–4 need an SSID; 5–6 apply regardless. Pure — does not mutate ExecutionCount.
        /// </summary>
        public static DnsResult Evaluate(AppConfig cfg, string? ssid, bool isOpenNetwork, DateTime now)
        {
            // Off when: manual mode, DNS automation not running, or the DNS feature module is
            // disabled entirely (tunnels-only install).
            if (cfg.ManualMode || !cfg.DnsAutomationEnabled || !cfg.EnableDns)
                return NoAction;

            // 2. Open-network protection
            if (isOpenNetwork && !string.IsNullOrEmpty(cfg.OpenWifiDnsProfileId))
                return Resolve(cfg, cfg.OpenWifiDnsProfileId, "Open network");

            bool haveSsid = !string.IsNullOrEmpty(ssid);

            // 3. Explicit SSID rules
            if (haveSsid)
            {
                var m = cfg.Rules.FirstOrDefault(r =>
                    r.Enabled && r.Kind == "wifi" && !string.IsNullOrEmpty(r.DnsProfileId) &&
                    string.Equals(r.Ssid, ssid, StringComparison.OrdinalIgnoreCase));
                if (m != null)
                    return Resolve(cfg, m.DnsProfileId, $"Rule: {ssid}");
            }

            // 4. Trusted / untrusted rules (each fires only on its side of the shared list)
            if (haveSsid)
            {
                bool? isTrusted = null;
                foreach (var r in cfg.Rules)
                {
                    if (!r.Enabled || r.Kind != "trusted" || string.IsNullOrEmpty(r.DnsProfileId)) continue;
                    isTrusted ??= cfg.TrustedNetworks.Any(s =>
                        string.Equals(s, ssid, StringComparison.OrdinalIgnoreCase));

                    bool sideMatches = r.TrustedWhenOnList ? isTrusted.Value : !isTrusted.Value;
                    if (!sideMatches) continue;

                    string side = isTrusted.Value ? "Trusted network" : "Untrusted network";
                    return Resolve(cfg, r.DnsProfileId, $"{side}: {ssid}");
                }
            }

            // 5. Schedule rules
            foreach (var r in cfg.Rules)
            {
                if (!r.Enabled || r.Kind != "schedule" || string.IsNullOrEmpty(r.DnsProfileId)) continue;
                if (!IsWithinSchedule(r, now)) continue;
                return Resolve(cfg, r.DnsProfileId, $"Schedule: {r.RuleName}");
            }

            // 6. Default
            return string.IsNullOrEmpty(cfg.DefaultDnsProfileId)
                ? NoAction
                : Resolve(cfg, cfg.DefaultDnsProfileId, "Default DNS");
        }

        /// <summary>
        /// Turn a profile id into a concrete action: <see cref="DnsProfile.NoneId"/> → None,
        /// <see cref="DnsProfile.AutomaticId"/> → Automatic, a known profile → Apply. An id
        /// that names no existing profile fails safe to None (a dangling reference must never
        /// silently apply the wrong resolver).
        /// </summary>
        private static DnsResult Resolve(AppConfig cfg, string profileId, string reason)
        {
            if (string.IsNullOrEmpty(profileId))
                return new(DnsActionKind.None, null, $"{reason} → no DNS change");
            if (profileId == DnsProfile.AutomaticId)
                return new(DnsActionKind.Automatic, null, $"{reason} → automatic (DHCP)");

            var profile = cfg.DnsProfiles.FirstOrDefault(p =>
                string.Equals(p.Id, profileId, StringComparison.Ordinal));
            if (profile == null)
                return new(DnsActionKind.None, null, $"{reason} → unknown DNS profile '{profileId}'");

            return new(DnsActionKind.Apply, profile.Id, $"{reason} → {profile.Name}");
        }

        /// <summary>
        /// True when <paramref name="now"/> falls inside a schedule rule's day + time window.
        /// The single canonical implementation — <c>RuleEngine.IsWithinSchedule</c> delegates here.
        /// </summary>
        public static bool IsWithinSchedule(TunnelRule r, DateTime now)
        {
            if (r.Days != null && r.Days.Count > 0 &&
                !r.Days.Contains((int)now.DayOfWeek))
                return false;

            if (!TimeSpan.TryParse(r.StartTime, out var start)) return false;
            if (!TimeSpan.TryParse(r.EndTime,   out var end))   return false;

            var t = now.TimeOfDay;
            if (start == end) return false;               // zero-length window
            if (start <  end) return t >= start && t < end;
            return t >= start || t < end;                 // overnight window (e.g. 22:00–06:00)
        }

        // ── Self-test (design §13 step 2; run via `MasselGUARDcli selftest`) ──────
        /// <summary>
        /// Table-driven precedence checks. Returns (passed, failed, failureMessages).
        /// Pure — builds throwaway <see cref="AppConfig"/>s; touches no config file or network.
        /// </summary>
        public static (int passed, int failed, List<string> failures) RunSelfTest()
        {
            int pass = 0, fail = 0;
            var failures = new List<string>();

            // Fixed "now" inside a Mon–Fri 09:00–17:00 window for schedule cases.
            var mondayNoon = new DateTime(2026, 9, 14, 12, 0, 0);   // 2026-09-14 is a Monday
            var saturday   = new DateTime(2026, 9, 19, 12, 0, 0);

            void Check(string name, DnsResult got, DnsActionKind wantKind, string? wantProfileId)
            {
                bool ok = got.Action == wantKind &&
                          string.Equals(got.ProfileId ?? "", wantProfileId ?? "", StringComparison.Ordinal);
                if (ok) pass++;
                else { fail++; failures.Add($"{name}: got ({got.Action},{got.ProfileId ?? "null"}) want ({wantKind},{wantProfileId ?? "null"})"); }
            }

            var cf = new DnsProfile { Id = "cf",   Name = "Cloudflare", V4Primary = "1.1.1.1" };
            var work = new DnsProfile { Id = "wrk", Name = "Work",       V4Primary = "10.0.0.53" };

            AppConfig Base() => new()
            {
                DnsAutomationEnabled = true,
                DnsProfiles = new() { cf, work },
            };

            // 1. Master off / manual mode → None.
            var offCfg = Base(); offCfg.DnsAutomationEnabled = false;
            Check("automation-off", Evaluate(offCfg, "Cafe", false, mondayNoon), DnsActionKind.None, null);
            var manCfg = Base(); manCfg.ManualMode = true;
            Check("manual-mode", Evaluate(manCfg, "Cafe", false, mondayNoon), DnsActionKind.None, null);
            // DNS feature module disabled → never acts, even with a default set.
            var noModuleCfg = Base(); noModuleCfg.EnableDns = false; noModuleCfg.DefaultDnsProfileId = "cf";
            Check("dns-module-off", Evaluate(noModuleCfg, "Cafe", false, mondayNoon), DnsActionKind.None, null);

            // 2. Open-network profile.
            var openCfg = Base(); openCfg.OpenWifiDnsProfileId = "cf";
            Check("open-network", Evaluate(openCfg, "FreeWifi", true, mondayNoon), DnsActionKind.Apply, "cf");
            Check("open-flag-off-falls-through", Evaluate(openCfg, "FreeWifi", false, mondayNoon), DnsActionKind.None, null);

            // 3. SSID rule.
            var ssidCfg = Base();
            ssidCfg.Rules.Add(new TunnelRule { Kind = "wifi", Ssid = "Home", DnsProfileId = "cf" });
            Check("ssid-match", Evaluate(ssidCfg, "Home", false, mondayNoon), DnsActionKind.Apply, "cf");
            Check("ssid-nomatch", Evaluate(ssidCfg, "Other", false, mondayNoon), DnsActionKind.None, null);
            // Disabled SSID rule is ignored.
            var disCfg = Base();
            disCfg.Rules.Add(new TunnelRule { Kind = "wifi", Ssid = "Home", DnsProfileId = "cf", Enabled = false });
            Check("ssid-disabled", Evaluate(disCfg, "Home", false, mondayNoon), DnsActionKind.None, null);

            // 4. Trusted / untrusted sides.
            var trustCfg = Base();
            trustCfg.TrustedNetworks.Add("Home");
            trustCfg.Rules.Add(new TunnelRule { Kind = "trusted", TrustedWhen = "untrusted", DnsProfileId = "cf" });
            trustCfg.Rules.Add(new TunnelRule { Kind = "trusted", TrustedWhen = "trusted",   DnsProfileId = "wrk" });
            Check("untrusted-side", Evaluate(trustCfg, "Cafe", false, mondayNoon), DnsActionKind.Apply, "cf");
            Check("trusted-side",   Evaluate(trustCfg, "Home", false, mondayNoon), DnsActionKind.Apply, "wrk");

            // 5. Precedence: SSID beats trusted.
            var precCfg = Base();
            precCfg.TrustedNetworks.Add("Cafe");
            precCfg.Rules.Add(new TunnelRule { Kind = "wifi", Ssid = "Cafe", DnsProfileId = "cf" });
            precCfg.Rules.Add(new TunnelRule { Kind = "trusted", TrustedWhen = "trusted", DnsProfileId = "wrk" });
            Check("ssid-beats-trusted", Evaluate(precCfg, "Cafe", false, mondayNoon), DnsActionKind.Apply, "cf");

            // 6. Schedule window (in / out) + no-ssid still evaluates schedule.
            var schedCfg = Base();
            schedCfg.Rules.Add(new TunnelRule { Kind = "schedule", DnsProfileId = "wrk",
                Days = new() { 1, 2, 3, 4, 5 }, StartTime = "09:00", EndTime = "17:00" });
            Check("schedule-in-window",  Evaluate(schedCfg, null, false, mondayNoon), DnsActionKind.Apply, "wrk");
            Check("schedule-out-of-day", Evaluate(schedCfg, null, false, saturday),   DnsActionKind.None, null);

            // 7. Default profile + automatic + none + dangling id.
            var defCfg = Base(); defCfg.DefaultDnsProfileId = "cf";
            Check("default-apply", Evaluate(defCfg, "Whatever", false, mondayNoon), DnsActionKind.Apply, "cf");
            var autoCfg = Base(); autoCfg.DefaultDnsProfileId = DnsProfile.AutomaticId;
            Check("default-automatic", Evaluate(autoCfg, "Whatever", false, mondayNoon), DnsActionKind.Automatic, null);
            Check("no-default", Evaluate(Base(), "Whatever", false, mondayNoon), DnsActionKind.None, null);
            var danglCfg = Base(); danglCfg.DefaultDnsProfileId = "ghost";
            Check("dangling-id-fails-safe", Evaluate(danglCfg, "Whatever", false, mondayNoon), DnsActionKind.None, null);

            // 8. IsDnsOnly helper.
            void CheckBool(string name, bool got, bool want)
            { if (got == want) pass++; else { fail++; failures.Add($"{name}: got {got} want {want}"); } }
            CheckBool("dnsonly-empty-tunnel-with-dns", IsDnsOnly(new TunnelRule { Tunnel = "", DnsProfileId = "cf" }), true);
            CheckBool("dnsonly-with-tunnel",           IsDnsOnly(new TunnelRule { Tunnel = "T", DnsProfileId = "cf" }), false);
            CheckBool("dnsonly-disconnect-rule",       IsDnsOnly(new TunnelRule { Tunnel = "", DnsProfileId = "" }),   false);

            return (pass, fail, failures);
        }
    }
}
