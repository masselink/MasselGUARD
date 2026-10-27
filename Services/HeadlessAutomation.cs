using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    // Step 4 of the post-phase-2 plan (docs/ServiceBackend-Design.md): the SERVICE runs the network rules when no
    // MasselGUARD window is around ("snapshot handover"). The window stays the owner of config.json; it pushes a
    // snapshot of its AppConfig and a lease heartbeat. Everything here is WPF-free, pure where possible, and
    // CLI-shared so it can be selftested.

    /// <summary>"Is a window alive?" - the window renews it every few seconds; the service takes over automation
    /// only once it has lapsed.</summary>
    public sealed class UiLease
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(20);
        private readonly object _lock = new();
        private DateTime _lastUtc = DateTime.MinValue;

        public void Touch(DateTime nowUtc) { lock (_lock) _lastUtc = nowUtc; }
        public void Release() { lock (_lock) _lastUtc = DateTime.MinValue; }
        public bool IsAlive(DateTime nowUtc) { lock (_lock) return _lastUtc != DateTime.MinValue && nowUtc - _lastUtc < Lifetime; }
    }

    /// <summary>What the service knows about the window: the last pushed config and whether a window is alive.</summary>
    public sealed class AutomationState
    {
        public UiLease Lease { get; } = new();
        /// <summary>The user (SID) the pushed snapshot belongs to; set by the first push that turns the feature on.</summary>
        public string? OwnerSid { get; set; }
        private AppConfig? _cfg;
        public AppConfig? Config { get => Volatile.Read(ref _cfg); set => Volatile.Write(ref _cfg, value); }
    }

    /// <summary>Connect a tunnel from the service's own store. Shared by the window's "connect by name" request
    /// and by the headless automation, so both run exactly the same checks.</summary>
    public static class StoredConnect
    {
        private static readonly ISplitTunnelBackend Split = new RouteBasedBackend();

        /// <param name="expectedHash">the base-config hash the caller expects (a different stored config = "stale"); null = take what is stored</param>
        /// <param name="killSwitch">non-null = enable the kill switch for this tunnel after a successful connect</param>
        public static RpcResponse Run(ITunnelStore store, ITunnelOps tunnels, IKillSwitchOps? killSwitch,
                                      Func<string, string, (string path, IDisposable? cleanup)> stage,
                                      string name, SplitConfig split, string? expectedHash,
                                      Func<string, bool>? tunnelAllowed = null)
        {
            if (RpcValidator.Name(name) != null) return RpcResponse.Fail("invalid tunnel name");
            // A service of that name that MasselGUARD did not create (WireGuard for Windows, ...) is not ours to stop or replace.
            if (tunnelAllowed != null && !tunnelAllowed(name)) return RpcResponse.Fail("a service with this tunnel name exists that MasselGUARD did not create");
            var baseConf = store.Get(name);
            if (baseConf == null || (expectedHash != null && FileTunnelStore.HashOf(baseConf) != expectedHash))
                return RpcResponse.Fail("stale");
            var final = baseConf;
            if (split.HasRouteSplit)
            {
                try { final = Split.ApplyToConfig(baseConf, split); }
                catch { final = baseConf; }   // same fail-safe as the window: connect with the original
            }
            var err = RpcValidator.Conf(final);
            if (err != null) return RpcResponse.Fail(err);
            var (path, cleanup) = stage(name, final);
            using (cleanup)
            {
                bool ok = tunnels.Connect(name, path, _ => { }, out var cerr);
                if (ok && killSwitch != null)
                    killSwitch.Enable(name, KillSwitchService.ParseEndpointIp(final), Split.KillSwitchBypassRanges(split));
                return new RpcResponse { Ok = ok, Flag = ok, Error = cerr ?? "" };
            }
        }
    }

    /// <summary>The window's AppConfig as pushed to the service.</summary>
    public static class AutomationSnapshot
    {
        public const int MaxBytes = 1024 * 1024;
        public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

        public static string Serialize(AppConfig cfg) => JsonSerializer.Serialize(cfg, Json);

        public static AppConfig? TryParse(string? json, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(json)) { error = "empty snapshot"; return null; }
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes) { error = "snapshot too large"; return null; }
            try
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, Json);
                if (cfg == null) { error = "invalid snapshot"; return null; }
                // The service never needs (or keeps) inline tunnel configs or file paths from a client.
                foreach (var t in cfg.Tunnels) { t.Config = null; t.Path = null; }
                // A tunnel name becomes a service name, a file name and an adapter name: only names the RPC would accept.
                cfg.Tunnels = cfg.Tunnels.Where(t => RpcValidator.Name(t.Name) == null).ToList();
                return cfg;
            }
            catch { error = "invalid snapshot"; return null; }
        }
    }

    public enum HeadlessActionKind { Connect, Disconnect }
    public sealed record HeadlessAction(HeadlessActionKind Kind, string Tunnel, string Reason);

    public enum HeadlessDnsKind { Apply, Automatic, Restore }
    public sealed record HeadlessDnsAction(HeadlessDnsKind Kind, Guid Interface, DnsProfile? Profile, string Families, string Reason);

    /// <summary>The decisions of the window's <c>ApplyNetworkSnapshot</c>, minus everything that needs a UI
    /// (toasts, history, cap prompts): which tunnel to connect or disconnect and which DNS to apply per adapter.
    /// It only acts on CHANGES after <see cref="Seed"/>, so taking over from a window never undoes what the user did by hand.</summary>
    public sealed class HeadlessPlanner
    {
        private readonly RuleEngine _rules = new();
        private bool _seeded;
        private NetworkSnapshot _last = NetworkSnapshot.Empty;
        private string _lastPrimaryKey = "";
        private Dictionary<string, string> _dnsKeys = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Remember the current network without acting on it.</summary>
        /// <param name="seedDns">false = the DNS of the current adapters is still to be applied (DNS follows the
        /// network and is safe to apply on takeover); tunnels are never acted on for the network that is already there.</param>
        public void Seed(NetworkSnapshot snap, bool seedDns = true)
        {
            _seeded = true;
            _last = snap;
            _lastPrimaryKey = snap.Primary?.Fingerprint() ?? "";
            _dnsKeys = seedDns
                ? snap.Adapters.ToDictionary(a => a.AdapterId, a => a.Fingerprint(), StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public bool IsSeeded => _seeded;
        public void Reset() { _seeded = false; _last = NetworkSnapshot.Empty; _lastPrimaryKey = ""; _dnsKeys.Clear(); }

        /// <summary>Tunnel actions for a new network snapshot. <paramref name="active"/> = local tunnels running now.</summary>
        public List<HeadlessAction> PlanTunnel(AppConfig cfg, NetworkSnapshot snap, IReadOnlyCollection<string> active)
        {
            var actions = new List<HeadlessAction>();
            if (_seeded && snap.SameAs(_last)) return actions;
            bool first = !_seeded;
            var prev = _last;
            _last = snap; _seeded = true;

            RuleEngine.RuleResult? r = null;
            if (snap.IsEmpty)
            {
                if (!first && !prev.IsEmpty) r = _rules.EvaluateWifiDisconnected(cfg);
                _lastPrimaryKey = "";
            }
            else
            {
                var key = snap.Primary?.Fingerprint() ?? "";
                if (first || key != _lastPrimaryKey) { _lastPrimaryKey = key; r = _rules.EvaluateNetwork(cfg, snap.Primary); }
            }
            if (r == null || r.Action == RuleEngine.ActionKind.None) return actions;

            if (r.Action == RuleEngine.ActionKind.Disconnect)
            {
                actions.AddRange(active.Select(n => new HeadlessAction(HeadlessActionKind.Disconnect, n, r.Reason)));
                return actions;
            }

            // Activate: only a local tunnel that exists in the snapshot; others are stopped first.
            var target = cfg.Tunnels.FirstOrDefault(t => (string.IsNullOrEmpty(t.Source) || t.Source == "local")
                                                         && string.Equals(t.Name, r.TunnelName, StringComparison.OrdinalIgnoreCase));
            if (target == null) return actions;
            foreach (var n in active.Where(n => !string.Equals(n, target.Name, StringComparison.OrdinalIgnoreCase)))
                actions.Add(new HeadlessAction(HeadlessActionKind.Disconnect, n, r.Reason));
            if (!active.Contains(target.Name, StringComparer.OrdinalIgnoreCase))
                actions.Add(new HeadlessAction(HeadlessActionKind.Connect, target.Name, r.Reason));
            return actions;
        }

        /// <summary>DNS actions per adapter that changed or appeared, and a restore for adapters that left.
        /// While a tunnel owns DNS, or an adapter is under a bypass hold, nothing is planned for it.</summary>
        public List<HeadlessDnsAction> PlanDns(AppConfig cfg, NetworkSnapshot snap, DateTime nowUtc, bool tunnelOwnsDns,
                                               IReadOnlyCollection<Guid> held, Func<Guid, bool> hasOverride)
        {
            var list = new List<HeadlessDnsAction>();
            if (tunnelOwnsDns) return list;   // keys stay as they were, so the actions come back when the tunnel drops
            var fam = cfg.DnsAddressFamilies is "v4" or "v6" ? cfg.DnsAddressFamilies : "both";
            var newKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var a in snap.Adapters)
            {
                var key = a.Fingerprint();
                newKeys[a.AdapterId] = key;
                if (!Guid.TryParse(a.AdapterId, out var g) || g == Guid.Empty) continue;
                if (_dnsKeys.TryGetValue(a.AdapterId, out var old) && old == key) continue;
                if (held.Contains(g)) continue;

                var res = DnsPolicy.Evaluate(cfg, a, nowUtc);
                switch (res.Action)
                {
                    case DnsPolicy.DnsActionKind.Apply:
                        var p = cfg.DnsProfiles.FirstOrDefault(x => string.Equals(x.Id, res.ProfileId, StringComparison.Ordinal));
                        if (p != null) list.Add(new HeadlessDnsAction(HeadlessDnsKind.Apply, g, p, fam, res.Reason));
                        break;
                    case DnsPolicy.DnsActionKind.Automatic:
                        list.Add(new HeadlessDnsAction(HeadlessDnsKind.Automatic, g, null, fam, res.Reason));
                        break;
                    default:
                        if (hasOverride(g)) list.Add(new HeadlessDnsAction(HeadlessDnsKind.Restore, g, null, fam, "no matching rule"));
                        break;
                }
            }
            foreach (var kv in _dnsKeys)
                if (!newKeys.ContainsKey(kv.Key) && Guid.TryParse(kv.Key, out var gone) && hasOverride(gone) && !held.Contains(gone))
                    list.Add(new HeadlessDnsAction(HeadlessDnsKind.Restore, gone, null, fam, "network left"));
            _dnsKeys = newKeys;
            return list;
        }

        // ── self-test ───────────────────────────────────────────────────────────

        private static NetworkIdentity Net(string ssid, bool primary = true, string? id = null) =>
            new(id ?? Guid.NewGuid().ToString("B"), "Wi-Fi", "wifi", ssid, false, null, null, null, new List<string>(), null, 25, primary);

        public static (int pass, int fail, List<string> failures) RunSelfTest()
        {
            int pass = 0; var fails = new List<string>();
            void Check(bool ok, string what) { if (ok) pass++; else fails.Add(what); }

            AppConfig Cfg() => new()
            {
                EnableTunnels = true, EnableDns = true, DnsAutomationEnabled = true,
                Tunnels = new List<StoredTunnel> { new() { Name = "Work", Source = "local" }, new() { Name = "Home", Source = "local" } },
                Rules = new List<TunnelRule>
                {
                    new() { Kind = "network", Ssid = "OfficeWifi", Tunnel = "Work", Enabled = true },
                    new() { Kind = "network", Ssid = "HomeWifi", Tunnel = "", Enabled = true },
                },
                DefaultAction = "none",
            };
            var none = new List<string>();

            // taking over never acts on the network that is already there
            var p = new HeadlessPlanner();
            var office = new NetworkSnapshot(new[] { Net("OfficeWifi") });
            p.Seed(office);
            Check(p.PlanTunnel(Cfg(), office, none).Count == 0, "planner: seeded network is not acted on");

            // moving to a network with a rule connects its tunnel
            var home = new NetworkSnapshot(new[] { Net("HomeWifi") });
            var a1 = p.PlanTunnel(Cfg(), home, new[] { "Work" });
            Check(a1.Count == 1 && a1[0].Kind == HeadlessActionKind.Disconnect && a1[0].Tunnel == "Work", "planner: empty-tunnel rule disconnects the active tunnel");

            // same snapshot again = nothing
            Check(p.PlanTunnel(Cfg(), home, none).Count == 0, "planner: unchanged snapshot does nothing");

            // activate: connects the target, stops the others
            var office2 = new NetworkSnapshot(new[] { Net("OfficeWifi") });
            var a2 = p.PlanTunnel(Cfg(), office2, new[] { "Home" });
            Check(a2.Count == 2 && a2[0] is { Kind: HeadlessActionKind.Disconnect, Tunnel: "Home" } && a2[1] is { Kind: HeadlessActionKind.Connect, Tunnel: "Work" }, "planner: activate stops others then connects the target");
            Check(p.PlanTunnel(Cfg(), new NetworkSnapshot(new[] { Net("OfficeWifi") }), new[] { "Work" }).Count == 0, "planner: same primary identity needs no action");

            // already connected target: nothing to do
            var p2 = new HeadlessPlanner(); p2.Seed(home);
            Check(p2.PlanTunnel(Cfg(), office2, new[] { "Work" }).Count == 0, "planner: target already active");

            // unknown tunnel in the rule
            var cfgBad = Cfg(); cfgBad.Rules[0].Tunnel = "Nope";
            var p3 = new HeadlessPlanner(); p3.Seed(home);
            Check(p3.PlanTunnel(cfgBad, office2, none).Count == 0, "planner: a rule pointing at a missing tunnel does nothing");

            // companion tunnel in the rule is not ours to connect
            var cfgComp = Cfg(); cfgComp.Tunnels[0].Source = "companion";
            var p4 = new HeadlessPlanner(); p4.Seed(home);
            Check(p4.PlanTunnel(cfgComp, office2, none).Count == 0, "planner: only local tunnels are connected");

            // leaving every network: the default action on disconnect
            var cfgDisc = Cfg(); cfgDisc.DefaultAction = "disconnect";
            var p5 = new HeadlessPlanner(); p5.Seed(office);
            var a5 = p5.PlanTunnel(cfgDisc, NetworkSnapshot.Empty, new[] { "Work" });
            Check(a5.Count == 1 && a5[0].Kind == HeadlessActionKind.Disconnect, "planner: network gone, default action disconnects");

            // manual mode switches all automation off
            var cfgManual = Cfg(); cfgManual.ManualMode = true;
            var p6 = new HeadlessPlanner(); p6.Seed(home);
            Check(p6.PlanTunnel(cfgManual, office2, none).Count == 0, "planner: manual mode = no automation");

            // not seeded: the first evaluation acts (used when the planner starts cold)
            Check(new HeadlessPlanner().PlanTunnel(Cfg(), office2, none).Count == 1, "planner: a cold start evaluates once");

            // ---- DNS
            var dnsProf = new DnsProfile { Id = "corp", Name = "Corp", V4Primary = "10.0.0.53" };
            AppConfig DnsCfg()
            {
                var c = Cfg();
                c.DnsProfiles = new List<DnsProfile> { dnsProf };
                c.Rules.Add(new TunnelRule { Kind = "network", Ssid = "OfficeWifi", Tunnel = "", DnsProfileId = "corp", Enabled = true });
                return c;
            }
            var now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
            var ifOffice = Net("OfficeWifi"); var gOffice = Guid.Parse(ifOffice.AdapterId);
            var pd = new HeadlessPlanner(); pd.Seed(NetworkSnapshot.Empty);
            var d1 = pd.PlanDns(DnsCfg(), new NetworkSnapshot(new[] { ifOffice }), now, false, new Guid[0], _ => false);
            Check(d1.Count == 1 && d1[0].Kind == HeadlessDnsKind.Apply && d1[0].Profile == dnsProf && d1[0].Interface == gOffice, "dns: a matching rule applies its profile to the adapter");
            Check(pd.PlanDns(DnsCfg(), new NetworkSnapshot(new[] { ifOffice }), now, false, new Guid[0], _ => true).Count == 0, "dns: unchanged adapter = nothing");
            var d3 = pd.PlanDns(DnsCfg(), NetworkSnapshot.Empty, now, false, new Guid[0], g => g == gOffice);
            Check(d3.Count == 1 && d3[0].Kind == HeadlessDnsKind.Restore && d3[0].Interface == gOffice, "dns: adapter gone -> restore");

            var pd2 = new HeadlessPlanner(); pd2.Seed(NetworkSnapshot.Empty);
            Check(pd2.PlanDns(DnsCfg(), new NetworkSnapshot(new[] { ifOffice }), now, true, new Guid[0], _ => false).Count == 0, "dns: held while a tunnel owns DNS");
            Check(pd2.PlanDns(DnsCfg(), new NetworkSnapshot(new[] { ifOffice }), now, false, new Guid[0], _ => false).Count == 1, "dns: re-planned when the tunnel drops");
            var pd3 = new HeadlessPlanner(); pd3.Seed(NetworkSnapshot.Empty);
            Check(pd3.PlanDns(DnsCfg(), new NetworkSnapshot(new[] { ifOffice }), now, false, new[] { gOffice }, _ => false).Count == 0, "dns: an adapter under a bypass hold is left alone");
            var ifOther = Net("Cafe");
            var pd4 = new HeadlessPlanner(); pd4.Seed(NetworkSnapshot.Empty);
            Check(pd4.PlanDns(DnsCfg(), new NetworkSnapshot(new[] { ifOther }), now, false, new Guid[0], _ => false).Count == 0, "dns: no rule and no override = nothing");
            Check(pd4.PlanDns(DnsCfg(), new NetworkSnapshot(new[] { Net("Cafe2", id: ifOther.AdapterId) }), now, false, new Guid[0], g => true).Single().Kind == HeadlessDnsKind.Restore, "dns: no rule but an override of ours -> restore");

            // Seeded without DNS: the first plan applies DNS for the adapters that are there (takeover), tunnels stay put.
            var ps = new HeadlessPlanner(); ps.Seed(new NetworkSnapshot(new[] { ifOffice }), seedDns: false);
            Check(ps.PlanTunnel(Cfg(), new NetworkSnapshot(new[] { ifOffice }), none).Count == 0, "takeover: tunnels are not touched for the current network");
            Check(ps.PlanDns(DnsCfg(), new NetworkSnapshot(new[] { ifOffice }), now, false, new Guid[0], _ => false).Count == 1, "takeover: DNS for the current network is applied once");

            // ---- lease
            var lease = new UiLease(); var t0 = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
            Check(!lease.IsAlive(t0), "lease: not alive before the first touch");
            lease.Touch(t0);
            Check(lease.IsAlive(t0.AddSeconds(19)) && !lease.IsAlive(t0.AddSeconds(20)), "lease: alive for its lifetime only");
            lease.Touch(t0.AddSeconds(15));
            Check(lease.IsAlive(t0.AddSeconds(30)), "lease: a touch renews it");
            lease.Release();
            Check(!lease.IsAlive(t0.AddSeconds(16)), "lease: release ends it at once");

            // ---- snapshot
            var cfgS = Cfg(); cfgS.Tunnels[0].Path = @"C:\Users\x\AppData\Roaming\MasselGUARD\tunnels\Work.conf.dpapi"; cfgS.Tunnels[0].Config = "BLOB";
            var json = AutomationSnapshot.Serialize(cfgS);
            var back = AutomationSnapshot.TryParse(json, out var err);
            Check(back != null && back.Tunnels.Count == 2 && back.Rules.Count == 2, "snapshot: round trip keeps tunnels and rules");
            Check(back != null && back.Tunnels.All(t => t.Config == null && t.Path == null), "snapshot: inline configs and file paths are dropped");
            Check(AutomationSnapshot.TryParse("{ not json", out _) == null && AutomationSnapshot.TryParse("", out _) == null, "snapshot: garbage refused");
            var cfgNames = Cfg(); cfgNames.Tunnels.Add(new StoredTunnel { Name = @"..\evil", Source = "local" }); cfgNames.Tunnels.Add(new StoredTunnel { Name = "con", Source = "local" });
            var parsedNames = AutomationSnapshot.TryParse(AutomationSnapshot.Serialize(cfgNames), out _);
            Check(parsedNames != null && parsedNames.Tunnels.Count == 2 && parsedNames.Tunnels.All(t => t.Name is "Work" or "Home"), "snapshot: tunnels with invalid names are dropped");

            // StoredConnect: the same name rules as the RPC, for every caller
            {
                var fx = new FakeOps();
                var dir = Path.Combine(Path.GetTempPath(), "mg-sc-" + Guid.NewGuid().ToString("N"));
                try
                {
                    byte[] Xor(byte[] b) => b.Select(x => (byte)(x ^ 0x17)).ToArray();
                    var st = new FileTunnelStore(dir, Xor, Xor);
                    const string C = "[Interface]\nPrivateKey = a=\n";
                    st.Put("Office", C); st.Put("Mine", C);
                    var off = new SplitConfig { Mode = "off" };
                    Func<string, string, (string, IDisposable?)> stage = (n, c) => (n + ".conf", null);
                    var foreign = StoredConnect.Run(st, fx, null, stage, "Office", off, null, n => n != "Office");
                    Check(!foreign.Ok && fx.Calls.Count == 0, "storedconnect: a name that belongs to a foreign service is refused, nothing is connected");
                    Check(StoredConnect.Run(st, fx, null, stage, "Mine", off, null, n => n != "Office") is { Ok: true } && fx.Calls.Contains("tunnel.connect Mine"), "storedconnect: an allowed name connects");
                    Check(!StoredConnect.Run(st, fx, null, stage, @"..\x", off, null, null).Ok, "storedconnect: an invalid name is refused");
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            }
            Check(AutomationSnapshot.TryParse(new string('x', AutomationSnapshot.MaxBytes + 1), out var e2) == null && e2.Contains("large"), "snapshot: oversized refused");
            return (pass, fails.Count, fails);
        }
    }
}
