using System;
using System.Collections.Generic;
using System.Linq;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    // Phase 0 of the service back-end (docs/ServiceBackend-Design.md): the operations that need
    // administrator rights, behind small interfaces. Arguments are plain data so a later pipe
    // proxy can carry them. WPF-free and CLI-shared.

    /// <summary>Local (wireguard-NT) tunnel operations.</summary>
    public interface ITunnelOps
    {
        bool Connect(string tunnelName, string confPath, Action<string> log, out string error);
        bool Disconnect(string tunnelName, out string error);
        bool IsRunning(string tunnelName);
        TunnelDll.TunnelStats GetTrafficStats(string tunnelName);
    }

    /// <summary>Firewall kill switch.</summary>
    public interface IKillSwitchOps
    {
        void Enable(string tunnelName, string? endpointIp, IReadOnlyList<string>? bypassRanges = null);
        void Disable(string tunnelName);
        void DisableAll();
        void CleanupStaleRules();
    }

    /// <summary>Per-interface DNS overrides.</summary>
    public interface IDnsOps
    {
        bool HasOverride(Guid interfaceGuid);
        int  OverrideCount { get; }
        bool ApplyProfile(Guid interfaceGuid, DnsProfile profile, string addressFamilies);
        bool SetAutomatic(Guid interfaceGuid, string addressFamilies);
        bool Restore(Guid interfaceGuid);
        void RestoreAll();
    }

    /// <summary>Connect a tunnel by NAME from the service's own encrypted store (service mode only). The
    /// caller passes the base config (pushed to the store when its hash differs) and the split-tunnel
    /// intent, which the service applies itself.</summary>
    public interface IStoredConnectOps
    {
        bool ConnectStored(string name, string baseConf, SplitConfig split, Action<string> log, out string error);
        /// <summary>Drops stored tunnels that no longer exist in the window's list.</summary>
        void PruneStored(IEnumerable<string> keepNames);
    }

    /// <summary>The window's side of the snapshot handover (service mode only): push the config, push tunnel
    /// configs into the service's store, and renew the "a window is alive" lease.</summary>
    public interface IAutomationOps
    {
        bool PushSnapshot(string json);
        bool PushTunnel(string name, string conf);
        bool RenewLease();
        void ReleaseLease();
    }

    /// <summary>Timed DNS override held by the service (service mode only; null in direct mode).</summary>
    public interface IDnsHoldOps
    {
        bool RegisterHold(string profileId, IReadOnlyList<Guid> interfaces, int seconds);
        void CancelHold();
        /// <summary>Ends the hold now and restores its interfaces; true when one was running.</summary>
        bool StopHold();
        (bool active, string profileId, int remainingSeconds) GetHold();
        /// <summary>The UI is leaving: restore every override except interfaces under a running hold.</summary>
        void ReleaseExceptHeld();
    }

    /// <summary>Today's behaviour: forwards to the static <see cref="TunnelDll"/>.</summary>
    public sealed class TunnelDllOps : ITunnelOps
    {
        public bool Connect(string tunnelName, string confPath, Action<string> log, out string error)
        {
            var ok = TunnelDll.Connect(tunnelName, confPath, log, out error);
            if (ok) StampService(tunnelName);
            return ok;
        }

        /// <summary>Marks the tunnel's service as MasselGUARD-managed so the orphan scan can recognise it.
        /// Done here (not in the window) because only the process that connects has the rights.</summary>
        private static void StampService(string tunnelName)
        {
            try
            {
                using var regKey = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey($@"SYSTEM\CurrentControlSet\Services\WireGuardTunnel${tunnelName}", writable: true);
                regKey?.SetValue("Description", $"MasselGUARD Tunnel: {tunnelName}");
                regKey?.SetValue("DisplayName", $"WireGuard Tunnel: MasselGUARD - {tunnelName}");
            }
            catch { /* non-critical */ }
        }
        public bool Disconnect(string tunnelName, out string error)
            => TunnelDll.Disconnect(tunnelName, out error);
        public bool IsRunning(string tunnelName) => TunnelDll.IsRunning(tunnelName);
        public TunnelDll.TunnelStats GetTrafficStats(string tunnelName) => TunnelDll.GetTrafficStats(tunnelName);
    }

    /// <summary>Records every call as text, for selftests of call sequences. Never touches the system.</summary>
    public sealed class FakeOps : ITunnelOps, IKillSwitchOps, IDnsOps
    {
        public readonly List<string> Calls = new();
        public bool ConnectResult = true;
        public bool DnsResult = true;
        private readonly HashSet<Guid> _overrides = new();
        private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);

        public bool Connect(string tunnelName, string confPath, Action<string> log, out string error)
        {
            Calls.Add($"tunnel.connect {tunnelName}");
            error = ConnectResult ? "" : "fake connect failure";
            if (ConnectResult) _running.Add(tunnelName);
            return ConnectResult;
        }
        public bool Disconnect(string tunnelName, out string error)
        {
            Calls.Add($"tunnel.disconnect {tunnelName}");
            _running.Remove(tunnelName);
            error = "";
            return true;
        }
        public bool IsRunning(string tunnelName) => _running.Contains(tunnelName);
        public TunnelDll.TunnelStats GetTrafficStats(string tunnelName) => default;

        public void Enable(string tunnelName, string? endpointIp, IReadOnlyList<string>? bypassRanges = null)
            => Calls.Add($"ks.enable {tunnelName} {endpointIp ?? "-"} bypass={bypassRanges?.Count ?? 0}");
        public void Disable(string tunnelName) => Calls.Add($"ks.disable {tunnelName}");
        public void DisableAll() => Calls.Add("ks.disableall");
        public void CleanupStaleRules() => Calls.Add("ks.cleanup");

        public IEnumerable<Guid> OverriddenGuids => _overrides.ToList();
        public bool HasOverride(Guid g) => _overrides.Contains(g);
        public int OverrideCount => _overrides.Count;
        public bool ApplyProfile(Guid g, DnsProfile p, string families)
        {
            Calls.Add($"dns.apply {p.Id} {families}");
            if (DnsResult) _overrides.Add(g);
            return DnsResult;
        }
        public bool SetAutomatic(Guid g, string families)
        {
            Calls.Add($"dns.auto {families}");
            _overrides.Add(g);
            return DnsResult;
        }
        public bool Restore(Guid g) { Calls.Add("dns.restore"); _overrides.Remove(g); return true; }
        public void RestoreAll() { Calls.Add("dns.restoreall"); _overrides.Clear(); }

        // ── selftest ─────────────────────────────────────────────────────────────

        public static (int pass, int fail, List<string> failures) RunSelfTest()
        {
            int pass = 0; var fails = new List<string>();
            void Check(bool ok, string what) { if (ok) pass++; else fails.Add(what); }

            // Fake records calls and tracks state.
            var f = new FakeOps();
            Check(f.Connect("t1", "x.conf", _ => { }, out var e1) && e1 == "", "fake connect ok");
            Check(f.IsRunning("T1"), "fake running after connect (case-insensitive)");
            f.Disconnect("t1", out _);
            Check(!f.IsRunning("t1"), "fake not running after disconnect");
            f.ConnectResult = false;
            Check(!f.Connect("t2", "x.conf", _ => { }, out var e2) && e2 != "", "fake connect failure reports error");
            Check(!f.IsRunning("t2"), "failed connect is not running");

            var g = Guid.NewGuid();
            var p = new DnsProfile { Id = "p1", Name = "P1" };
            Check(!f.HasOverride(g) && f.OverrideCount == 0, "no dns override initially");
            f.ApplyProfile(g, p, "both");
            Check(f.HasOverride(g) && f.OverrideCount == 1, "dns override recorded");
            f.Restore(g);
            Check(!f.HasOverride(g), "dns restore clears override");
            Check(f.Calls.Contains("dns.apply p1 both") && f.Calls.Contains("dns.restore"), "dns calls recorded");

            // Real tunnel dll adapter is a pure forwarder: just check the type wires up.
            Check(new TunnelDllOps() is ITunnelOps, "TunnelDllOps implements ITunnelOps");
            return (pass, fails.Count, fails);
        }
    }
}
