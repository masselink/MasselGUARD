using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Abstraction over the two split-tunneling mechanisms (design §4):
    /// <list type="bullet">
    ///   <item><see cref="RouteBasedBackend"/> — 4.0.0. Rewrites the peer's <c>AllowedIPs</c>
    ///         so wireguard-NT programs the routes; no packet steering.</item>
    ///   <item><c>WinDivertBackend</c> — later 4.x. Steers per-app flows in user mode;
    ///         leaves <c>AllowedIPs</c> alone. Not implemented in 4.0.0.</item>
    /// </list>
    /// WPF-free and CLI-shared (listed in <c>MasselGUARDcli.csproj</c>).
    /// </summary>
    public interface ISplitTunnelBackend
    {
        /// <summary>Rewrites the plaintext <c>.conf</c> to reflect <paramref name="split"/>.
        /// Route-based backend rewrites <c>AllowedIPs</c>; a per-app backend returns it unchanged.</summary>
        string ApplyToConfig(string plaintextConf, SplitConfig split);

        /// <summary>Ranges that must stay reachable off-tunnel while a kill switch is active
        /// (the excluded set in exclude-mode). Empty for include/off. See design §6.</summary>
        IReadOnlyList<string> KillSwitchBypassRanges(SplitConfig split);

        /// <summary>Per-app steering hooks — no-ops for the route-based backend, implemented by
        /// the future WinDivert backend.</summary>
        void OnConnected(string tunnelName, SplitConfig split);
        void OnDisconnected(string tunnelName);
    }

    /// <summary>
    /// 4.0.0 route/IP-based split. Computes an effective <c>AllowedIPs</c> from the config's base
    /// list + the split ranges (<see cref="CidrMath"/>) and patches it into the <c>[Peer]</c>
    /// section via <see cref="Cli.WireGuardConf.Patch"/>. wireguard-NT does all route programming.
    /// </summary>
    public sealed class RouteBasedBackend : ISplitTunnelBackend
    {
        private static readonly Regex AllowedIpsLine =
            new(@"^\s*AllowedIPs\s*=\s*(.+?)\s*$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public string ApplyToConfig(string plaintextConf, SplitConfig split)
        {
            if (plaintextConf == null) return plaintextConf!;
            if (split == null || !split.HasRouteSplit) return plaintextConf;

            // Base AllowedIPs = the first [Peer]'s current value (empty if none present —
            // CidrMath treats a wholly-empty base as a full tunnel for exclude mode).
            var m       = AllowedIpsLine.Match(plaintextConf);
            var baseIps = m.Success ? m.Groups[1].Value : "";

            var effective = CidrMath.ComputeEffectiveAllowedIPs(baseIps, split.Mode, split.Ranges);

            // No base line and nothing computed → leave the config untouched.
            if (!m.Success && string.IsNullOrEmpty(effective)) return plaintextConf;

            return Cli.WireGuardConf.Patch(plaintextConf,
                new Dictionary<string, string> { ["AllowedIPs"] = effective });
        }

        public IReadOnlyList<string> KillSwitchBypassRanges(SplitConfig split)
        {
            if (split != null &&
                string.Equals(split.Mode, "exclude", StringComparison.OrdinalIgnoreCase))
                return new List<string>(split.Ranges);
            return Array.Empty<string>();
        }

        // Route-based split needs no packet steering — wireguard-NT owns the routes.
        public void OnConnected(string tunnelName, SplitConfig split) { }
        public void OnDisconnected(string tunnelName) { }

        // ── Self-test (design §13 step 3; run via `MasselGUARDcli selftest`) ───

        /// <summary>Verifies the conf-rewrite integration end to end. Returns (passed, failed, msgs).</summary>
        public static (int passed, int failed, List<string> failures) SelfTest()
        {
            int passed = 0, failed = 0;
            var failures = new List<string>();
            var backend  = new RouteBasedBackend();

            const string conf =
                "[Interface]\n" +
                "PrivateKey = ABC\n" +
                "Address = 10.9.0.2/32\n" +
                "DNS = 1.1.1.1\n\n" +
                "[Peer]\n" +
                "PublicKey = DEF\n" +
                "Endpoint = vpn.example.com:51820\n" +
                "AllowedIPs = 0.0.0.0/0, ::/0\n" +
                "PersistentKeepalive = 25\n";

            string AllowedOf(string c)
            {
                var m = AllowedIpsLine.Match(c);
                return m.Success ? m.Groups[1].Value.Trim() : "(none)";
            }

            void Check(string label, bool ok, string? detail = null)
            {
                if (ok) passed++;
                else { failed++; failures.Add($"{label}{(detail != null ? ": " + detail : "")}"); }
            }

            // off → conf untouched
            var offCfg = new SplitConfig { Mode = "off", Ranges = new[] { "10.0.0.0/8" } };
            Check("backend-off-unchanged", backend.ApplyToConfig(conf, offCfg) == conf);

            // no ranges → conf untouched (HasRouteSplit false)
            var emptyCfg = new SplitConfig { Mode = "exclude", Ranges = new string[0] };
            Check("backend-noranges-unchanged", backend.ApplyToConfig(conf, emptyCfg) == conf);

            // include → AllowedIPs becomes just the includes; other lines survive
            var incCfg  = new SplitConfig { Mode = "include", Ranges = new[] { "10.0.0.0/8" } };
            var incOut  = backend.ApplyToConfig(conf, incCfg);
            Check("backend-include-allowedips", AllowedOf(incOut) == "10.0.0.0/8", AllowedOf(incOut));
            Check("backend-include-keeps-endpoint", incOut.Contains("Endpoint = vpn.example.com:51820"));
            Check("backend-include-keeps-privkey",  incOut.Contains("PrivateKey = ABC"));
            Check("backend-include-keeps-keepalive",incOut.Contains("PersistentKeepalive = 25"));

            // exclude → AllowedIPs no longer routes the excluded block, v6 preserved
            var excCfg = new SplitConfig { Mode = "exclude", Ranges = new[] { "10.0.0.0/8" } };
            var excOut = backend.ApplyToConfig(conf, excCfg);
            var excAllowed = AllowedOf(excOut);
            Check("backend-exclude-drops-10", !excAllowed.Contains("10.0.0.0/8") && excAllowed.Contains("11.0.0.0/8"),
                  excAllowed);
            Check("backend-exclude-keeps-v6", excAllowed.Contains("::/0"));

            // bypass ranges: excludes only in exclude mode
            Check("backend-bypass-exclude", backend.KillSwitchBypassRanges(excCfg).Count == 1);
            Check("backend-bypass-include", backend.KillSwitchBypassRanges(incCfg).Count == 0);
            Check("backend-bypass-off",     backend.KillSwitchBypassRanges(offCfg).Count == 0);

            return (passed, failed, failures);
        }
    }
}
