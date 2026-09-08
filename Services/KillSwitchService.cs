using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Manages a kill-switch via Windows Firewall: when active, all outbound traffic
    /// is blocked except through the WireGuard tunnel interface and the endpoint IP.
    /// Uses the Windows Firewall COM API (HNetCfg.FwPolicy2 / HNetCfg.FwRule) via
    /// late binding — no COM reference needed in the project.
    ///
    /// Reference-counted: the global block policy is applied on the first Enable()
    /// and restored on the last Disable() / DisableAll().
    /// </summary>
    public class KillSwitchService
    {
        private readonly LogService  _log;
        private readonly object      _lock   = new();
        private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
        // Exact firewall-rule names for each tunnel's split-bypass allows, so removal is
        // precise (tunnel names can be prefixes of one another). See design §6.
        private readonly Dictionary<string, List<string>> _splitRuleNames =
            new(StringComparer.OrdinalIgnoreCase);

        // Saved per-profile outbound actions (1 = Allow, 0 = Block)
        private int _savedDomain  = 1;
        private int _savedPrivate = 1;
        private int _savedPublic  = 1;

        private const string Prefix  = "MasselGUARD_KS_";
        private const int    AllProf = 2147483647;  // NET_FW_PROFILE2_ALL
        private const int    DirOut  = 2;            // NET_FW_RULE_DIR_OUT
        private const int    Allow   = 1;            // NET_FW_ACTION_ALLOW
        private const int    UDP     = 17;

        // NET_FW_PROFILE_TYPE2_ values
        private const int ProfDomain  = 1;
        private const int ProfPrivate = 2;
        private const int ProfPublic  = 4;

        public KillSwitchService(LogService log) { _log = log; }

        // ── Startup cleanup ───────────────────────────────────────────────────

        /// <summary>
        /// Removes any leftover MasselGUARD_KS_ firewall rules and restores the
        /// outbound default policy to Allow. Handles the crash-without-cleanup case.
        /// </summary>
        public void CleanupStaleRules()
        {
            try
            {
                var policy = OpenPolicy();
                if (policy == null) return;

                // Restore outbound default to Allow for all profiles
                try { policy.DefaultOutboundAction[ProfDomain]  = Allow; } catch { }
                try { policy.DefaultOutboundAction[ProfPrivate] = Allow; } catch { }
                try { policy.DefaultOutboundAction[ProfPublic]  = Allow; } catch { }

                // Remove all MasselGUARD_KS_ rules
                var rules    = policy.Rules;
                var toRemove = new List<string>();
                foreach (dynamic rule in rules)
                {
                    try
                    {
                        string rName = rule.Name;
                        if (rName != null && rName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                            toRemove.Add(rName);
                    }
                    catch { }
                }
                foreach (var n in toRemove)
                {
                    try { rules.Remove(n); } catch { }
                }

                lock (_lock) { _active.Clear(); }
                if (toRemove.Count > 0)
                    _log.Debug($"[KillSwitch] Removed {toRemove.Count} stale rule(s).");
            }
            catch (Exception ex)
            {
                _log.Debug($"[KillSwitch] CleanupStaleRules: {ex.Message}");
            }
        }

        // ── Public API ────────────────────────────────────────────────────────

        public void Enable(string tunnelName, string? endpointIp,
                           IReadOnlyList<string>? bypassRanges = null)
        {
            try
            {
                lock (_lock)
                {
                    if (_active.Contains(tunnelName)) return;
                    if (_active.Count == 0) ApplyGlobalBlock();
                    _active.Add(tunnelName);
                }
                AddTunnelRules(tunnelName, endpointIp, bypassRanges);
                _log.Ok($"[KillSwitch] Enabled — {tunnelName}");
            }
            catch (Exception ex)
            {
                _log.Warn($"[KillSwitch] Enable failed ({tunnelName}): {ex.Message}");
            }
        }

        public void Disable(string tunnelName)
        {
            // Nothing to do if KillSwitch was never enabled for this tunnel.
            // Without this guard, Disable logs "[KillSwitch] Disabled" on every
            // disconnect even for tunnels that never had KillSwitch turned on.
            lock (_lock)
            {
                if (!_active.Contains(tunnelName)) return;
            }

            try
            {
                RemoveTunnelRules(tunnelName);
                bool shouldRestore;
                lock (_lock)
                {
                    _active.Remove(tunnelName);
                    shouldRestore = _active.Count == 0;
                }
                if (shouldRestore) RestoreGlobalPolicy();
                _log.Ok($"[KillSwitch] Disabled — {tunnelName}");
            }
            catch (Exception ex)
            {
                _log.Warn($"[KillSwitch] Disable failed ({tunnelName}): {ex.Message}");
            }
        }

        public void DisableAll()
        {
            try
            {
                List<string> tunnels;
                lock (_lock) { tunnels = new List<string>(_active); _active.Clear(); }
                foreach (var t in tunnels)
                    try { RemoveTunnelRules(t); } catch { }
                RestoreGlobalPolicy();
            }
            catch (Exception ex)
            {
                _log.Debug($"[KillSwitch] DisableAll: {ex.Message}");
            }
        }

        // ── Firewall policy helpers ────────────────────────────────────────────

        private void ApplyGlobalBlock()
        {
            var policy = OpenPolicy();
            if (policy == null) return;

            // Save current outbound actions before overriding
            try { _savedDomain  = policy.DefaultOutboundAction[ProfDomain];  } catch { _savedDomain  = Allow; }
            try { _savedPrivate = policy.DefaultOutboundAction[ProfPrivate]; } catch { _savedPrivate = Allow; }
            try { _savedPublic  = policy.DefaultOutboundAction[ProfPublic];  } catch { _savedPublic  = Allow; }

            // Block all outbound on every profile
            try { policy.DefaultOutboundAction[ProfDomain]  = 0; } catch { }  // 0 = NET_FW_ACTION_BLOCK
            try { policy.DefaultOutboundAction[ProfPrivate] = 0; } catch { }
            try { policy.DefaultOutboundAction[ProfPublic]  = 0; } catch { }

            // Allow loopback so local services keep working
            AddFwRule(policy.Rules, Prefix + "Allow_Loopback", AllProf,
                remoteAddr: "127.0.0.0/8,::1/128");
        }

        private void RestoreGlobalPolicy()
        {
            var policy = OpenPolicy();
            if (policy == null) return;

            try { policy.DefaultOutboundAction[ProfDomain]  = _savedDomain;  } catch { }
            try { policy.DefaultOutboundAction[ProfPrivate] = _savedPrivate; } catch { }
            try { policy.DefaultOutboundAction[ProfPublic]  = _savedPublic;  } catch { }

            try { policy.Rules.Remove(Prefix + "Allow_Loopback"); } catch { }
        }

        private void AddTunnelRules(string tunnelName, string? endpointIp,
                                    IReadOnlyList<string>? bypassRanges = null)
        {
            var policy = OpenPolicy();
            if (policy == null) return;
            var rules = policy.Rules;

            // Allow all traffic through the WireGuard adapter (identified by adapter friendly name)
            AddFwRule(rules, Prefix + "Allow_WG_" + tunnelName, AllProf,
                iface: tunnelName);

            // Allow UDP to the server endpoint IP (WireGuard handshake channel)
            if (!string.IsNullOrEmpty(endpointIp))
                AddFwRule(rules, Prefix + "Allow_EP_" + tunnelName, AllProf,
                    remoteAddr: endpointIp, protocol: UDP);

            // Split exclude-mode: let the excluded destination ranges out past the global
            // block so they reach the physical NIC instead of being dropped (design §6).
            if (bypassRanges != null && bypassRanges.Count > 0)
            {
                var names = new List<string>();
                for (int i = 0; i < bypassRanges.Count; i++)
                {
                    var range = bypassRanges[i]?.Trim();
                    if (string.IsNullOrEmpty(range)) continue;
                    var name = Prefix + "Allow_Split_" + tunnelName + "_" + i;
                    AddFwRule(rules, name, AllProf, remoteAddr: range);
                    names.Add(name);
                }
                if (names.Count > 0)
                {
                    lock (_lock) { _splitRuleNames[tunnelName] = names; }
                    _log.Debug($"[KillSwitch] {names.Count} split-bypass allow(s) — {tunnelName}");
                }
            }
        }

        private void RemoveTunnelRules(string tunnelName)
        {
            var policy = OpenPolicy();
            if (policy == null) return;
            var rules = policy.Rules;
            try { rules.Remove(Prefix + "Allow_WG_" + tunnelName); } catch { }
            try { rules.Remove(Prefix + "Allow_EP_" + tunnelName); } catch { }

            // Remove this tunnel's split-bypass allows by their exact recorded names.
            List<string>? splitNames;
            lock (_lock) { _splitRuleNames.TryGetValue(tunnelName, out splitNames); _splitRuleNames.Remove(tunnelName); }
            if (splitNames != null)
                foreach (var n in splitNames)
                    try { rules.Remove(n); } catch { }
        }

        // ── COM helpers ───────────────────────────────────────────────────────

        private static dynamic? OpenPolicy()
        {
            try
            {
                var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (type == null) return null;
                return Activator.CreateInstance(type);
            }
            catch { return null; }
        }

        private static void AddFwRule(dynamic rulesCollection, string name, int profiles,
            string? remoteAddr = null, string? iface = null, int protocol = 256)
        {
            try
            {
                var ruleType = Type.GetTypeFromProgID("HNetCfg.FwRule");
                if (ruleType == null) return;
                dynamic rule   = Activator.CreateInstance(ruleType)!;
                rule.Name      = name;
                rule.Direction = DirOut;
                rule.Action    = Allow;
                rule.Enabled   = true;
                rule.Profiles  = profiles;
                if (protocol   != 256)  rule.Protocol        = protocol;
                if (remoteAddr != null) rule.RemoteAddresses = remoteAddr;
                if (iface      != null) rule.Interfaces      = new string[] { iface };
                rulesCollection.Add(rule);
            }
            catch { /* firewall COM unavailable — silently skip */ }
        }

        // ── Utility ───────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts the server IP from a WireGuard config (Endpoint = host:port).
        /// Handles both IPv4 (1.2.3.4:51820) and IPv6 ([::1]:51820).
        /// Returns null when no Endpoint line is present.
        /// </summary>
        public static string? ParseEndpointIp(string wireguardConfig)
        {
            var m = Regex.Match(wireguardConfig,
                @"^\s*Endpoint\s*=\s*\[?([0-9A-Fa-f:.]+)",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
