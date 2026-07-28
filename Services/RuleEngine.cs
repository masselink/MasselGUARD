using System;
using System.Linq;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Pure rule-evaluation logic.
    /// No UI references, no side-effects — returns the action to take.
    /// </summary>
    public class RuleEngine
    {
        public enum ActionKind { None, Disconnect, Activate }

        public record RuleResult(ActionKind Action, string? TunnelName, string Reason);

        private static readonly RuleResult DoNothing =
            new(ActionKind.None, null, "No matching rule");

        // ── WiFi evaluation ───────────────────────────────────────────────────

        /// <summary>
        /// Evaluate what should happen when the WiFi network changes.
        /// Precedence (first match wins):
        ///   1. Manual mode → do nothing (all automation off).
        ///   2. Open-network protection (open/passwordless network + OpenWifiTunnel set).
        ///   3. SSID rules — an enabled "wifi" rule whose SSID equals the current network.
        ///   4. Trusted-network auto-protect — an enabled "trusted" rule (with a tunnel):
        ///      trusted SSID → disconnect, otherwise → activate its tunnel.
        ///   5. Default action — activate DefaultTunnel / disconnect / none.
        /// Schedule ("time") rules are evaluated separately on a timer (see EvaluateSchedules).
        /// </summary>
        public RuleResult EvaluateWifi(
            AppConfig cfg,
            string?   ssid,
            bool      isOpenNetwork)
        {
            if (cfg.ManualMode)
                return DoNothing;

            // 1. Open network protection
            if (isOpenNetwork && !string.IsNullOrEmpty(cfg.OpenWifiTunnel))
                return new(ActionKind.Activate, cfg.OpenWifiTunnel,
                    "Open network protection");

            if (string.IsNullOrEmpty(ssid))
                return DoNothing;

            // 2. SSID rules — only "wifi"-kind rules match an SSID. ("schedule" is handled
            //    by EvaluateSchedules; "trusted" is the broad policy in step 3 below.)
            var match = cfg.Rules.FirstOrDefault(r =>
                r.Enabled && r.Kind == "wifi" &&
                string.Equals(r.Ssid, ssid, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                match.ExecutionCount++;
                if (string.IsNullOrEmpty(match.Tunnel))
                    return new(ActionKind.Disconnect, null,
                        $"Rule: {ssid} → disconnect");
                return new(ActionKind.Activate, match.Tunnel,
                    $"Rule: {ssid} → {match.Tunnel}");
            }

            // 3. Trusted-network auto-protect (broad policy — explicit SSID rules above win).
            //    Enabled simply by the presence of a "trusted"-kind rule with a tunnel; that
            //    rule lives in the WiFi rules list and carries its own trigger counter.
            var trustedRule = cfg.Rules.FirstOrDefault(r =>
                r.Enabled && r.Kind == "trusted" && !string.IsNullOrEmpty(r.Tunnel));
            if (trustedRule != null)
            {
                trustedRule.ExecutionCount++;
                bool isTrusted = cfg.TrustedNetworks.Any(s =>
                    string.Equals(s, ssid, StringComparison.OrdinalIgnoreCase));
                return isTrusted
                    ? new(ActionKind.Disconnect, null, $"Trusted network: {ssid}")
                    : new(ActionKind.Activate, trustedRule.Tunnel,
                          $"Untrusted network protection: {ssid}");
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

        /// <summary>
        /// Evaluate what should happen when the WiFi disconnects entirely.
        /// </summary>
        public RuleResult EvaluateWifiDisconnected(AppConfig cfg)
        {
            if (cfg.ManualMode) return DoNothing;

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
            if (cfg.ManualMode) return DoNothing;

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

        /// <summary>True when <paramref name="now"/> falls inside the rule's day + time window.</summary>
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
    }
}
