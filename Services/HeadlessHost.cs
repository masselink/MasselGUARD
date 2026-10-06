using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Runs inside the service (GUI project only: it uses the network watcher and the WLAN API). When the
    /// window's snapshot says "keep automation running" and no window renewed its lease, it follows network
    /// changes and applies the network rules - tunnel and DNS - through the same privileged operations the
    /// window would use. It never touches anything while a window is alive, and on takeover it only
    /// remembers the current network (see <see cref="HeadlessPlanner.Seed"/>).
    /// Not done here on purpose: history entries and toasts (no UI), cap checks, schedule rules.
    /// </summary>
    public sealed class HeadlessHost : IDisposable
    {
        private readonly AutomationState _state;
        private readonly ITunnelOps _tunnels;
        private readonly IKillSwitchOps _ks;
        private readonly IDnsOps _dns;
        private readonly ITunnelStore _store;
        private readonly Func<string, string, (string path, IDisposable? cleanup)> _stage;
        private readonly DnsHoldKeeper _hold;
        private readonly LogService _log;
        private readonly Func<string, bool> _tunnelAllowed;
        private readonly HashSet<string> _warnedNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HeadlessPlanner _planner = new();
        private readonly WiFiService _wifi = new();
        private readonly object _lock = new();
        private NetworkWatcher? _watcher;
        private Timer? _tick;
        private bool _running;

        public HeadlessHost(AutomationState state, ITunnelOps tunnels, IKillSwitchOps ks, IDnsOps dns, ITunnelStore store,
                            Func<string, string, (string path, IDisposable? cleanup)> stage, DnsHoldKeeper hold, LogService log,
                            Func<string, bool>? tunnelAllowed = null)
        { _state = state; _tunnels = tunnels; _ks = ks; _dns = dns; _store = store; _stage = stage; _hold = hold; _log = log; _tunnelAllowed = tunnelAllowed ?? (_ => true); }

        public bool IsRunning => _running;

        public void Start()
        {
            try { _wifi.Start(); } catch { /* no WLAN service: SSID rules simply do not match */ }
            _watcher = new NetworkWatcher(() => _state.Config?.NetworkSettleMs ?? 2000);
            _watcher.Settled += () => { if (_running) ThreadPool.QueueUserWorkItem(_ => Handle()); };
            _watcher.Start();
            _tick = new Timer(_ => Tick(), null, 2000, 2000);
        }

        public void Dispose()
        {
            try { _tick?.Dispose(); } catch { }
            try { _watcher?.Dispose(); } catch { }
            try { _wifi.Dispose(); } catch { }
        }

        // ── takeover / hand-back ────────────────────────────────────────────────

        private void Tick()
        {
            try
            {
                var cfg = _state.Config;
                bool should = cfg is { HeadlessAutomation: true } && !_state.Lease.IsAlive(DateTime.UtcNow);
                lock (_lock)
                {
                    if (should && !_running)
                    {
                        _running = true;
                        // Tunnel rules act on changes only (a manual disconnect before closing the window stays),
                        // DNS is derived from the network, so it is applied once for what is connected now.
                        _planner.Seed(Capture(cfg!), seedDns: false);
                        _log.Info("Automation: the service took over (no MasselGUARD window).");
                        ThreadPool.QueueUserWorkItem(_ => Handle());
                    }
                    else if (!should && _running)
                    {
                        _running = false;
                        _planner.Reset();
                        _log.Info("Automation: handed back to the MasselGUARD window.");
                    }
                }
            }
            catch (Exception ex) { _log.Debug($"Automation tick failed: {ex.Message}"); }
        }

        private NetworkSnapshot Capture(AppConfig cfg) =>
            NetworkMonitor.Capture(cfg.PrimaryNetworkMode,
                guid => guid == _wifi.CurrentInterfaceGuid ? (_wifi.CurrentSsid, _wifi.IsOpenNetwork) : (null, false),
                resolveGatewayMac: cfg.Rules.Any(r => r.Enabled && r.IsNetworkKind && r.EffectiveConditions.Any(c => c.By == NetworkMatchBy.GatewayMac))
                                   || cfg.TrustedNetworks.Any(t => t.TrimStart().StartsWith("mac:", StringComparison.OrdinalIgnoreCase)),
                wifiOnly: cfg.SimpleWifiMode);

        // ── one decision ────────────────────────────────────────────────────────

        private void Handle()
        {
            lock (_lock)
            {
                try
                {
                    var cfg = _state.Config;
                    if (!_running || cfg == null) return;
                    var snap = Capture(cfg);

                    // Only tunnels this service may touch: a valid name, and no foreign service (WireGuard for Windows, ...)
                    // of that name. Everything below (running state, disconnect, connect) works on this list only.
                    var local = cfg.Tunnels.Where(t => (string.IsNullOrEmpty(t.Source) || t.Source == "local") && IsOurs(t.Name)).ToList();
                    var active = local.Where(t => SafeRunning(t.Name)).Select(t => t.Name).ToList();

                    foreach (var a in _planner.PlanTunnel(cfg, snap, active))
                    {
                        if (a.Kind == HeadlessActionKind.Disconnect)
                        {
                            if (!local.Any(t => string.Equals(t.Name, a.Tunnel, StringComparison.OrdinalIgnoreCase))) continue;
                            _log.Info($"Automation: disconnect {a.Tunnel} ({a.Reason})");
                            _tunnels.Disconnect(a.Tunnel, out _);
                            _ks.Disable(a.Tunnel);
                        }
                        else
                        {
                            var target = local.FirstOrDefault(t => string.Equals(t.Name, a.Tunnel, StringComparison.OrdinalIgnoreCase));
                            if (target != null) Connect(cfg, target, a.Reason);   // null = filtered out above
                        }
                    }

                    bool tunnelOwnsDns = local.Any(t => SafeRunning(t.Name));
                    var held = _hold.Held(DateTime.UtcNow);
                    foreach (var d in _planner.PlanDns(cfg, snap, DateTime.UtcNow, tunnelOwnsDns, held, _dns.HasOverride))
                    {
                        switch (d.Kind)
                        {
                            case HeadlessDnsKind.Apply:     _log.Info($"Automation: DNS '{d.Profile!.Name}' ({d.Reason})"); _dns.ApplyProfile(d.Interface, d.Profile, d.Families); break;
                            case HeadlessDnsKind.Automatic: _log.Info($"Automation: DNS automatic ({d.Reason})"); _dns.SetAutomatic(d.Interface, d.Families); break;
                            default:                        _log.Info($"Automation: DNS restored ({d.Reason})"); _dns.Restore(d.Interface); break;
                        }
                    }
                }
                catch (Exception ex) { _log.Warn($"Automation failed: {ex.Message}"); }
            }
        }

        private bool IsOurs(string name)
        {
            bool ok = RpcValidator.Name(name) == null;
            if (ok) { try { ok = _tunnelAllowed(name); } catch { ok = false; } }
            if (!ok && _warnedNames.Add(name))
                _log.Warn($"Automation: ignoring tunnel '{name}' (invalid name, or a service of that name exists that MasselGUARD did not create).");
            return ok;
        }

        private bool SafeRunning(string name) { try { return _tunnels.IsRunning(name); } catch { return false; } }

        private void Connect(AppConfig cfg, StoredTunnel t, string reason)
        {
            _log.Info($"Automation: connect {t.Name} ({reason})");
            bool ks = cfg.KillSwitchMode == "always" || t.KillSwitch;
            var resp = StoredConnect.Run(_store, _tunnels, ks ? _ks : null, _stage, t.Name, SplitConfig.From(t), expectedHash: null, tunnelAllowed: _tunnelAllowed);
            if (!resp.Ok || !resp.Flag) _log.Warn($"Automation: could not connect {t.Name}: {(resp.Error.Length > 0 ? resp.Error : "failed")}");
            else _log.Ok($"Automation: connected {t.Name}");
        }
    }
}
