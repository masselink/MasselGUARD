using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    // Phase 1 of the service back-end (docs/ServiceBackend-Design.md): the RPC between the UI
    // (RpcOps proxy) and the privileged service (RpcServer + RpcDispatcher). One JSON line per
    // request, one JSON line per reply, one connection per call. Every argument is validated
    // (RpcValidator) before anything touches the system. WPF-free and CLI-shared.

    /// <summary>One request. Only the fields of the called op are used.</summary>
    public sealed class RpcRequest
    {
        public string  Op        { get; set; } = "";
        public string? Name      { get; set; }
        public string? Conf      { get; set; }
        public string? Guid      { get; set; }
        public string? Families  { get; set; }
        public string? Endpoint  { get; set; }
        public List<string>? Bypass { get; set; }
        public DnsProfile? Profile  { get; set; }
        public string? ProfileId    { get; set; }
        public string? Hash         { get; set; }
        public string? Snapshot     { get; set; }
        public string? SplitMode    { get; set; }
        public List<string>? SplitRanges { get; set; }
        public List<string>? Names  { get; set; }
        public List<string>? Guids  { get; set; }
        public int Seconds          { get; set; }
    }

    public sealed class RpcResponse
    {
        public bool    Ok    { get; set; }
        public string  Error { get; set; } = "";
        public bool    Flag  { get; set; }
        public int     Count { get; set; }
        public string  Text  { get; set; } = "";

        public static RpcResponse Fail(string error) => new() { Ok = false, Error = error };
    }

    /// <summary>Strict argument validation. Nothing a client sends reaches netsh, the firewall or the
    /// service manager without passing here.</summary>
    public static class RpcValidator
    {
        public const int MaxLineBytes = 2 * 1024 * 1024;   // a pushed config snapshot is the largest message
        public const int MaxConfBytes = 64 * 1024;
        public const int MaxBypass    = 256;

        // Letters/digits plus a few separators; no path characters, no quotes, no control characters.
        private static readonly Regex NameRx = new(@"^[\p{L}\p{N}][\p{L}\p{N} _=+.\-]{0,62}[\p{L}\p{N}_=+.\-]?$", RegexOptions.Compiled);
        private static readonly string[] ForbiddenConfKeys = { "preup", "postup", "predown", "postdown" };

        private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
          "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
          "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        public static string? Name(string? name)
        {
            if (name == null || !NameRx.IsMatch(name) || name.EndsWith(' ')) return "invalid tunnel name";
            // "con", "nul.x" ... are Windows device names whatever the extension: never a tunnel/file name.
            var stem = name.Split('.')[0].TrimEnd(' ');
            if (ReservedDeviceNames.Contains(stem)) return "invalid tunnel name";
            return null;
        }

        public static string? Guid(string? s, out Guid g)
        {
            g = default;
            return s != null && System.Guid.TryParse(s, out g) && g != default ? null : "invalid interface id";
        }

        private static readonly Regex ProfileIdRx = new(@"^[A-Za-z0-9_\-]{1,64}$", RegexOptions.Compiled);

        private static readonly Regex HashRx = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
        public static string? Hash(string? h) => h != null && HashRx.IsMatch(h) ? null : "invalid hash";

        public static string? Split(string? mode, List<string>? ranges)
        {
            if (mode is not ("off" or "exclude" or "include")) return "invalid split mode";
            if (ranges == null) return null;
            if (ranges.Count > MaxBypass) return "too many split ranges";
            foreach (var r in ranges)
                if (r == null || r.Length > 64 || !(IPNetwork.TryParse(r, out _) || IPAddress.TryParse(r, out _))) return "invalid split range";
            return null;
        }

        public static string? NameList(List<string>? names)
        {
            if (names == null) return null;
            if (names.Count > 512) return "too many names";
            foreach (var n in names) { var e = Name(n); if (e != null) return e; }
            return null;
        }

        public static string? ProfileId(string? id) => id != null && ProfileIdRx.IsMatch(id) ? null : "invalid profile id";

        public static string? Hold(string? profileId, List<string>? guids, int seconds, out List<Guid> parsed)
        {
            parsed = new List<Guid>();
            var e = ProfileId(profileId);
            if (e != null) return e;
            if (seconds < 1 || seconds > DnsHoldKeeper.MaxSeconds) return "invalid hold length";
            if (guids == null || guids.Count == 0 || guids.Count > DnsHoldKeeper.MaxGuids) return "invalid interface list";
            foreach (var s in guids)
            {
                var ge = Guid(s, out var g);
                if (ge != null) return ge;
                parsed.Add(g);
            }
            return null;
        }

        public static string? Families(string? f) =>
            f is "both" or "v4" or "v6" ? null : "invalid address families";

        public static string? Endpoint(string? ip) =>
            string.IsNullOrEmpty(ip) || (ip.Length <= 64 && IPAddress.TryParse(ip, out _)) ? null : "invalid endpoint address";

        public static string? Bypass(List<string>? ranges)
        {
            if (ranges == null) return null;
            if (ranges.Count > MaxBypass) return "too many bypass ranges";
            foreach (var r in ranges)
            {
                if (r == null || r.Length > 64) return "invalid bypass range";
                if (IPNetwork.TryParse(r, out var net)) { if (net.PrefixLength == 0) return "a catch-all bypass range is not allowed"; }
                else if (!IPAddress.TryParse(r, out _)) return "invalid bypass range";   // a single address is fine (split ranges may be plain IPs)
            }
            return null;
        }

        /// <summary>A config the service may write and start as SYSTEM: bounded, no NUL, an [Interface] with a
        /// PrivateKey, and none of the script hooks (they would run commands as SYSTEM).</summary>
        public static string? Conf(string? conf)
        {
            if (string.IsNullOrWhiteSpace(conf)) return "empty config";
            if (Encoding.UTF8.GetByteCount(conf) > MaxConfBytes) return "config too large";
            if (conf.IndexOf('\0') >= 0) return "invalid config";
            bool hasInterface = false, hasKey = false;
            foreach (var raw in conf.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("[Interface]", StringComparison.OrdinalIgnoreCase)) hasInterface = true;
                int eq = line.IndexOf('=');
                if (eq <= 0 || line.StartsWith('#')) continue;
                var key = line[..eq].Trim().ToLowerInvariant();
                if (key == "privatekey") hasKey = true;
                if (Array.IndexOf(ForbiddenConfKeys, key) >= 0) return $"config key '{line[..eq].Trim()}' is not allowed";
            }
            return hasInterface && hasKey ? null : "config needs an [Interface] with a PrivateKey";
        }

        private static bool IsIp(string? s) => string.IsNullOrEmpty(s) || (s.Length <= 64 && IPAddress.TryParse(s, out _));

        public static string? Profile(DnsProfile? p)
        {
            if (p == null) return "missing DNS profile";
            if (!IsIp(p.V4Primary) || !IsIp(p.V4Secondary) || !IsIp(p.V6Primary) || !IsIp(p.V6Secondary))
                return "invalid DNS server address";
            if (p.Encryption is not ("plain" or "doh" or "auto")) return "invalid DNS encryption mode";
            var t = p.DohTemplate ?? "";
            if (t.Length > 0)
            {
                if (t.Length > 512 || t.Any(c => c <= ' ' || c == '"' || c == '\'' || c == '`' || c == '^' || c == '&' || c == '|' || c == '<' || c == '>' || c == '%'))
                    return "invalid DoH template";
                if (!Uri.TryCreate(t, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
                    return "invalid DoH template";
            }
            return null;
        }
    }

    /// <summary>Maps a validated request to the real operations. No I/O of its own besides the
    /// conf hand-off, which goes through <paramref name="stageConf"/> so tests can fake it.</summary>
    public sealed class RpcDispatcher
    {
        public const int ProtocolVersion = 1;

        private readonly ITunnelOps _tunnels;
        private readonly IKillSwitchOps _ks;
        private readonly IDnsOps _dns;
        private readonly Func<string, string, (string path, IDisposable? cleanup)> _stageConf;
        private readonly ITunnelStore? _store;
        private readonly AutomationState? _auto;
        private readonly Func<string, bool>? _tunnelAllowed;   // false = a service of that name exists that we did not create
        private readonly Func<Guid, bool>? _ifaceAllowed;      // false = not a usable physical adapter
        private readonly Action<string>? _persistOwner;
        private readonly Action<string>? _persistSnapshot;
        private readonly ISplitTunnelBackend _split = new RouteBasedBackend();
        private readonly DnsHoldKeeper? _hold;
        private readonly Func<IEnumerable<Guid>>? _overridden;
        private readonly Func<DateTime> _now;

        /// <param name="stageConf">(name, conf) -> writes the config where the tunnel service can read it and
        /// returns its path plus a handle that removes it again.</param>
        public RpcDispatcher(ITunnelOps tunnels, IKillSwitchOps ks, IDnsOps dns,
                             Func<string, string, (string path, IDisposable? cleanup)> stageConf,
                             DnsHoldKeeper? hold = null, Func<IEnumerable<Guid>>? overridden = null, Func<DateTime>? now = null, ITunnelStore? store = null,
                             AutomationState? auto = null, Action<string>? persistSnapshot = null,
                             Func<string, bool>? tunnelAllowed = null, Func<Guid, bool>? interfaceAllowed = null, Action<string>? persistOwner = null)
        { _tunnels = tunnels; _ks = ks; _dns = dns; _stageConf = stageConf; _hold = hold; _overridden = overridden; _now = now ?? (() => DateTime.UtcNow); _store = store; _auto = auto; _persistSnapshot = persistSnapshot;
          _tunnelAllowed = tunnelAllowed; _ifaceAllowed = interfaceAllowed; _persistOwner = persistOwner; }

        public RpcResponse Handle(RpcRequest r, RpcCaller? caller = null)
        {
            string? err;
            Guid g;
            bool admin = caller?.IsAdmin ?? true;       // no caller = an in-process call (tests): trusted
            string sid = caller?.Sid ?? "";
            const string NameClash = "a service with this tunnel name exists that MasselGUARD did not create";
            const string BadAdapter = "not a usable network adapter";
            switch (r.Op)
            {
                case "ping":
                    return new RpcResponse { Ok = true, Count = ProtocolVersion };

                case "tunnel.connect":
                    if ((err = RpcValidator.Name(r.Name) ?? RpcValidator.Conf(r.Conf)) != null) return RpcResponse.Fail(err);
                    if (_tunnelAllowed != null && !_tunnelAllowed(r.Name!)) return RpcResponse.Fail(NameClash);
                    var (confPath, cleanup) = _stageConf(r.Name!, r.Conf!);
                    using (cleanup)
                    {
                        bool ok = _tunnels.Connect(r.Name!, confPath, _ => { }, out var cerr);
                        return new RpcResponse { Ok = ok, Flag = ok, Error = cerr ?? "" };
                    }
                case "store.put":
                    if (_store == null) return RpcResponse.Fail("not supported");
                    if ((err = RpcValidator.Name(r.Name) ?? RpcValidator.Conf(r.Conf)) != null) return RpcResponse.Fail(err);
                    // A tunnel name belongs to the user who stored it first (the headless automation connects from here).
                    var prevOwner = _store.OwnerOf(r.Name!);
                    if (!admin && prevOwner != null && !string.Equals(prevOwner, sid, StringComparison.OrdinalIgnoreCase))
                        return RpcResponse.Fail("this tunnel name is stored by another user");
                    _store.Put(r.Name!, r.Conf!, prevOwner ?? (string.IsNullOrEmpty(sid) ? null : sid));
                    return new RpcResponse { Ok = true, Flag = true, Text = FileTunnelStore.HashOf(r.Conf!) };
                case "store.prune":
                    if (_store == null) return RpcResponse.Fail("not supported");
                    if ((err = RpcValidator.NameList(r.Names)) != null) return RpcResponse.Fail(err);
                    return new RpcResponse { Ok = true, Count = _store.Prune(r.Names ?? new List<string>(), string.IsNullOrEmpty(sid) ? null : sid) };
                case "tunnel.connectstored":
                {
                    if (_store == null) return RpcResponse.Fail("not supported");
                    if ((err = RpcValidator.Name(r.Name) ?? RpcValidator.Hash(r.Hash) ?? RpcValidator.Split(r.SplitMode, r.SplitRanges)) != null) return RpcResponse.Fail(err);
                    if (_tunnelAllowed != null && !_tunnelAllowed(r.Name!)) return RpcResponse.Fail(NameClash);
                    // Missing or out of date ("stale"): the window pushes the current config and asks again.
                    var sc = new SplitConfig { Mode = r.SplitMode!, Ranges = r.SplitRanges ?? new List<string>() };
                    return StoredConnect.Run(_store, _tunnels, null, _stageConf, r.Name!, sc, r.Hash, _tunnelAllowed);
                }
                case "auto.push":
                {
                    if (_auto == null) return RpcResponse.Fail("not supported");
                    // The snapshot is acted on with SYSTEM rights while nobody is signed in: it belongs to ONE user.
                    if (!admin && _auto.OwnerSid != null && !string.Equals(_auto.OwnerSid, sid, StringComparison.OrdinalIgnoreCase))
                        return RpcResponse.Fail("automation belongs to another user");
                    var snap = AutomationSnapshot.TryParse(r.Snapshot, out var perr);
                    if (snap == null) return RpcResponse.Fail(perr);
                    if (_auto.OwnerSid == null && snap.HeadlessAutomation && sid.Length > 0)
                    {
                        _auto.OwnerSid = sid;
                        try { _persistOwner?.Invoke(sid); } catch { }
                    }
                    _auto.Config = snap;
                    _auto.Lease.Touch(_now());   // a push comes from a live window
                    try { _persistSnapshot?.Invoke(r.Snapshot!); } catch { }
                    return new RpcResponse { Ok = true, Flag = true };
                }
                case "auto.lease":
                    // Only the owner (or an administrator) keeps the service from taking over, or hands it over.
                    if (_auto != null && (admin || _auto.OwnerSid == null || string.Equals(_auto.OwnerSid, sid, StringComparison.OrdinalIgnoreCase)))
                        _auto.Lease.Touch(_now());
                    return new RpcResponse { Ok = true, Flag = _auto != null };
                case "auto.release":
                    if (_auto != null && (admin || _auto.OwnerSid == null || string.Equals(_auto.OwnerSid, sid, StringComparison.OrdinalIgnoreCase)))
                        _auto.Lease.Release();
                    return new RpcResponse { Ok = true };
                case "tunnel.disconnect":
                    if ((err = RpcValidator.Name(r.Name)) != null) return RpcResponse.Fail(err);
                    if (_tunnelAllowed != null && !_tunnelAllowed(r.Name!)) return RpcResponse.Fail(NameClash);
                    { bool ok = _tunnels.Disconnect(r.Name!, out var derr); return new RpcResponse { Ok = ok, Flag = ok, Error = derr ?? "" }; }

                case "ks.enable":
                    if ((err = RpcValidator.Name(r.Name) ?? RpcValidator.Endpoint(r.Endpoint) ?? RpcValidator.Bypass(r.Bypass)) != null) return RpcResponse.Fail(err);
                    _ks.Enable(r.Name!, string.IsNullOrEmpty(r.Endpoint) ? null : r.Endpoint, r.Bypass);
                    return new RpcResponse { Ok = true };
                case "ks.disable":
                    if ((err = RpcValidator.Name(r.Name)) != null) return RpcResponse.Fail(err);
                    _ks.Disable(r.Name!);
                    return new RpcResponse { Ok = true };
                case "ks.disableall": _ks.DisableAll(); return new RpcResponse { Ok = true };
                case "ks.cleanup":    _ks.CleanupStaleRules(); return new RpcResponse { Ok = true };

                case "dns.apply":
                    if ((err = RpcValidator.Guid(r.Guid, out g) ?? RpcValidator.Families(r.Families) ?? RpcValidator.Profile(r.Profile)) != null) return RpcResponse.Fail(err);
                    if (_ifaceAllowed != null && !_ifaceAllowed(g)) return RpcResponse.Fail(BadAdapter);
                    { bool ok = _dns.ApplyProfile(g, r.Profile!, r.Families!); return new RpcResponse { Ok = true, Flag = ok }; }
                case "dns.auto":
                    if ((err = RpcValidator.Guid(r.Guid, out g) ?? RpcValidator.Families(r.Families)) != null) return RpcResponse.Fail(err);
                    if (_ifaceAllowed != null && !_ifaceAllowed(g)) return RpcResponse.Fail(BadAdapter);
                    return new RpcResponse { Ok = true, Flag = _dns.SetAutomatic(g, r.Families!) };
                case "dns.restore":
                    if ((err = RpcValidator.Guid(r.Guid, out g)) != null) return RpcResponse.Fail(err);
                    return new RpcResponse { Ok = true, Flag = _dns.Restore(g) };
                case "dns.restoreall": _dns.RestoreAll(); return new RpcResponse { Ok = true };
                case "dns.has":
                    if ((err = RpcValidator.Guid(r.Guid, out g)) != null) return RpcResponse.Fail(err);
                    return new RpcResponse { Ok = true, Flag = _dns.HasOverride(g) };
                case "dns.count": return new RpcResponse { Ok = true, Count = _dns.OverrideCount };

                case "dns.hold":
                    if (_hold == null) return RpcResponse.Fail("not supported");
                    if ((err = RpcValidator.Hold(r.ProfileId, r.Guids, r.Seconds, out var held)) != null) return RpcResponse.Fail(err);
                    if (_ifaceAllowed != null && held.Any(x => !_ifaceAllowed(x))) return RpcResponse.Fail(BadAdapter);
                    _hold.Register(r.ProfileId!, held, r.Seconds, _now());
                    return new RpcResponse { Ok = true, Flag = true };
                case "dns.hold.stop":
                    return new RpcResponse { Ok = true, Flag = _hold?.Stop(g => _dns.Restore(g)) ?? false };
                case "dns.hold.cancel":
                    _hold?.Cancel();
                    return new RpcResponse { Ok = true };
                case "dns.hold.status":
                {
                    var st = _hold?.Get(_now()) ?? default;
                    return new RpcResponse { Ok = true, Flag = st.Active, Count = st.RemainingSeconds, Text = st.ProfileId ?? "" };
                }
                case "dns.release":
                {
                    // The UI is leaving: put every interface back except those under a running hold.
                    var keep = new HashSet<Guid>(_hold?.Held(_now()) ?? new List<Guid>());
                    if (_overridden == null) _dns.RestoreAll();
                    else foreach (var g2 in _overridden().ToList()) if (!keep.Contains(g2)) _dns.Restore(g2);
                    return new RpcResponse { Ok = true };
                }

                default: return RpcResponse.Fail("unknown operation");
            }
        }
    }

    /// <summary>Line framing with a hard size cap (an unbounded ReadLine would let any client exhaust memory).</summary>
    public static class RpcWire
    {
        public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public static async Task<string?> ReadLineAsync(Stream s, int maxBytes, CancellationToken ct)
        {
            // Chunked reads; one request per connection, so bytes after the newline are not needed.
            var buf = new byte[4096];
            var ms = new MemoryStream();
            while (true)
            {
                int n = await s.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false);
                if (n == 0) return ms.Length == 0 ? null : Encoding.UTF8.GetString(ms.ToArray());
                int nl = Array.IndexOf(buf, (byte)0x0A, 0, n);
                int take = nl >= 0 ? nl : n;
                if (ms.Length + take > maxBytes) throw new InvalidDataException("request too large");
                ms.Write(buf, 0, take);
                if (nl >= 0) return Encoding.UTF8.GetString(ms.ToArray()).TrimEnd((char)13);
            }
        }

        public static async Task WriteLineAsync(Stream s, string line, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await s.WriteAsync(bytes, ct).ConfigureAwait(false);
            await s.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Who may call the service: Administrators (elevated token) or a SID listed by the installer.</summary>
    /// <summary>Who is calling: the user SID and whether the (elevated) token is an Administrator.</summary>
    public sealed record RpcCaller(string Sid, bool IsAdmin);

    public static class RpcAuth
    {
        public static string AllowedUsersFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MasselGUARD", "service-users.txt");

        public static bool SidAllowed(string? sid, IEnumerable<string> allowed) =>
            !string.IsNullOrEmpty(sid) && allowed.Any(a => string.Equals(a.Trim(), sid, StringComparison.OrdinalIgnoreCase));

        public static List<string> LoadAllowed(string? path = null)
        {
            try { return File.ReadAllLines(path ?? AllowedUsersFile).Select(l => l.Trim()).Where(l => l.StartsWith("S-1-")).ToList(); }
            catch { return new List<string>(); }
        }

        /// <summary>Production check, run inside RunAsClient (the thread carries the caller's token). Null = refused.</summary>
        public static RpcCaller? CallerInfo()
        {
            using var id = WindowsIdentity.GetCurrent();
            if (id.IsAnonymous || id.IsGuest) return null;
            var sid = id.User?.Value ?? "";
            if (new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator)) return new RpcCaller(sid, true);
            return SidAllowed(sid, LoadAllowed()) ? new RpcCaller(sid, false) : null;
        }

        public static bool IsCallerAllowed() => CallerInfo() != null;
    }

    public sealed class RpcServer : IDisposable
    {
        public const string PipeName = "MasselGUARD.Service";

        private readonly RpcDispatcher _dispatcher;
        private readonly Func<RpcCaller?> _authorize;   // evaluated on a thread impersonating the client
        private readonly string _pipeName;
        private readonly CancellationTokenSource _cts = new();
        private readonly Action<string>? _log;

        public RpcServer(RpcDispatcher dispatcher, Func<bool>? authorize = null, string pipeName = PipeName, Action<string>? log = null,
                         Func<RpcCaller?>? authorizeCaller = null)
        {
            _dispatcher = dispatcher; _pipeName = pipeName; _log = log;
            _authorize = authorizeCaller
                         ?? (authorize != null ? () => authorize() ? new RpcCaller("", true) : null : RpcAuth.CallerInfo);
        }

        public void Start() => Task.Run(Loop);
        public void Dispose() => _cts.Cancel();

        private static PipeSecurity BuildSecurity()
        {
            // Anyone authenticated may connect; the per-connection check in Handle() decides who may call.
            var s = new PipeSecurity();
            s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            // No remote (SMB) callers: the pipe is for this machine only.
            s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            return s;
        }

        private async Task Loop()
        {
            while (!_cts.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
                    pipe = NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, BuildSecurity());
                    await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                    var p = pipe; pipe = null;
                    _ = Task.Run(() => Serve(p));
                }
                catch (OperationCanceledException) { pipe?.Dispose(); break; }
                catch { pipe?.Dispose(); try { await Task.Delay(100, _cts.Token).ConfigureAwait(false); } catch { break; } }
            }
        }

        private async Task Serve(NamedPipeServerStream pipe)
        {
            using (pipe)
            {
                RpcResponse reply;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));   // a client must send its request promptly
                    var line = await RpcWire.ReadLineAsync(pipe, RpcValidator.MaxLineBytes, timeout.Token).ConfigureAwait(false);

                    RpcCaller? caller = null;
                    try { pipe.RunAsClient(() => { caller = _authorize(); }); } catch { caller = null; }

                    if (caller == null) { reply = RpcResponse.Fail("access denied"); _log?.Invoke("RPC: caller rejected"); }
                    else if (string.IsNullOrWhiteSpace(line)) reply = RpcResponse.Fail("empty request");
                    else
                    {
                        RpcRequest? req = null;
                        try { req = JsonSerializer.Deserialize<RpcRequest>(line, RpcWire.Json); } catch { }
                        reply = req == null ? RpcResponse.Fail("malformed request") : Safe(req, caller);
                    }
                }
                catch (Exception ex) { reply = RpcResponse.Fail(ex is InvalidDataException ? ex.Message : "request failed"); }

                try
                {
                    using var wto = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token); wto.CancelAfter(TimeSpan.FromSeconds(10));
                    await RpcWire.WriteLineAsync(pipe, JsonSerializer.Serialize(reply, RpcWire.Json), wto.Token).ConfigureAwait(false);
                    try { pipe.WaitForPipeDrain(); } catch { }
                }
                catch { }
            }
        }

        private RpcResponse Safe(RpcRequest req, RpcCaller? caller)
        {
            try { return _dispatcher.Handle(req, caller); }
            catch (Exception ex) { _log?.Invoke($"RPC {req.Op} failed: {ex.Message}"); return RpcResponse.Fail("operation failed"); }
        }
    }

    /// <summary>Client side: the UI calls the service through this. Tunnel/DNS/kill-switch ops all
    /// implement the same interfaces as the in-process versions, so callers do not change.</summary>
    public sealed class RpcOps : ITunnelOps, IKillSwitchOps, IDnsOps, IDnsHoldOps, IStoredConnectOps, IAutomationOps
    {
        private readonly string _pipeName;
        private readonly ITunnelOps _local;   // read-only status stays local (needs no privileges)
        public string LastError { get; private set; } = "";

        private readonly Func<int>? _expectedServerPid;   // production: the service process id, so a process that squatted the pipe name gets nothing
        private int _trustedPid;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverPid);

        public RpcOps(string pipeName = RpcServer.PipeName, ITunnelOps? local = null, Func<int>? expectedServerPid = null)
        { _pipeName = pipeName; _local = local ?? new TunnelDllOps(); _expectedServerPid = expectedServerPid; }

        public RpcResponse? Call(RpcRequest req, int connectMs = 1500, int replyMs = 90_000)
        {
            try
            {
                using var c = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                c.Connect(connectMs);
                if (_expectedServerPid != null)
                {
                    if (!GetNamedPipeServerProcessId(c.SafePipeHandle, out uint spid)) { LastError = "cannot identify the pipe server"; return null; }
                    if (spid != (uint)_trustedPid)
                    {
                        if (spid != (uint)_expectedServerPid()) { LastError = "pipe server is not the MasselGUARD service"; return null; }
                        _trustedPid = (int)spid;
                    }
                }
                using var cts = new CancellationTokenSource(replyMs);
                RpcWire.WriteLineAsync(c, JsonSerializer.Serialize(req, RpcWire.Json), cts.Token).GetAwaiter().GetResult();
                var line = RpcWire.ReadLineAsync(c, RpcValidator.MaxLineBytes, cts.Token).GetAwaiter().GetResult();
                var resp = line == null ? null : JsonSerializer.Deserialize<RpcResponse>(line, RpcWire.Json);
                LastError = resp == null ? "no reply" : resp.Error;
                return resp;
            }
            catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; return null; }
        }

        /// <summary>True when a service answers on the pipe with the expected protocol version.</summary>
        public bool IsAvailable() =>
            Call(new RpcRequest { Op = "ping" }, 400, 2000) is { Ok: true, Count: RpcDispatcher.ProtocolVersion };

        private bool Flag(RpcRequest r) => Call(r) is { Ok: true, Flag: true };
        private void Fire(RpcRequest r) => Call(r);

        // tunnels
        public bool Connect(string tunnelName, string confPath, Action<string> log, out string error)
        {
            error = "";
            string conf;
            try { conf = File.ReadAllText(confPath); }
            catch (Exception ex) { error = $"Config file not readable: {ex.Message}"; return false; }
            log("Connecting through the MasselGUARD service");
            var resp = Call(new RpcRequest { Op = "tunnel.connect", Name = tunnelName, Conf = conf });
            if (resp == null) { error = "MasselGUARD service not reachable: " + LastError; return false; }
            error = resp.Error;
            return resp.Ok && resp.Flag;
        }
        public bool ConnectStored(string name, string baseConf, SplitConfig split, Action<string> log, out string error)
        {
            log("Connecting through the MasselGUARD service (stored config)");
            var hash = FileTunnelStore.HashOf(baseConf);
            RpcResponse? Ask() => Call(new RpcRequest
            {
                Op = "tunnel.connectstored", Name = name, Hash = hash,
                SplitMode = split.Mode, SplitRanges = split.Ranges.ToList(),
            });
            var resp = Ask();
            if (resp is { Ok: false, Error: "stale" })
            {
                var put = Call(new RpcRequest { Op = "store.put", Name = name, Conf = baseConf });
                if (put is not { Ok: true }) { error = put?.Error is { Length: > 0 } e ? e : "could not store the config in the service: " + LastError; return false; }
                resp = Ask();
            }
            if (resp == null) { error = "MasselGUARD service not reachable: " + LastError; return false; }
            error = resp.Error;
            return resp.Ok && resp.Flag;
        }

        // snapshot handover
        public bool PushSnapshot(string json) => Call(new RpcRequest { Op = "auto.push", Snapshot = json }, 1500, 20_000) is { Ok: true };
        public bool PushTunnel(string name, string conf) => Call(new RpcRequest { Op = "store.put", Name = name, Conf = conf }) is { Ok: true };
        public bool RenewLease() => Call(new RpcRequest { Op = "auto.lease" }, 1500, 5000) is { Ok: true };
        public void ReleaseLease() => Fire(new RpcRequest { Op = "auto.release" });

        public void PruneStored(IEnumerable<string> keepNames) =>
            Fire(new RpcRequest { Op = "store.prune", Names = keepNames.ToList() });

        public bool Disconnect(string tunnelName, out string error)
        {
            var resp = Call(new RpcRequest { Op = "tunnel.disconnect", Name = tunnelName });
            error = resp == null ? "MasselGUARD service not reachable: " + LastError : resp.Error;
            return resp is { Ok: true, Flag: true };
        }
        public bool IsRunning(string tunnelName) => _local.IsRunning(tunnelName);
        public TunnelDll.TunnelStats GetTrafficStats(string tunnelName) => _local.GetTrafficStats(tunnelName);

        // kill switch
        public void Enable(string tunnelName, string? endpointIp, IReadOnlyList<string>? bypassRanges = null)
            => Fire(new RpcRequest { Op = "ks.enable", Name = tunnelName, Endpoint = endpointIp, Bypass = bypassRanges?.ToList() });
        public void Disable(string tunnelName) => Fire(new RpcRequest { Op = "ks.disable", Name = tunnelName });
        public void DisableAll() => Fire(new RpcRequest { Op = "ks.disableall" });
        public void CleanupStaleRules() => Fire(new RpcRequest { Op = "ks.cleanup" });

        // DNS
        public bool HasOverride(Guid g) => Flag(new RpcRequest { Op = "dns.has", Guid = g.ToString() });
        public int OverrideCount => Call(new RpcRequest { Op = "dns.count" })?.Count ?? 0;
        public bool ApplyProfile(Guid g, DnsProfile p, string families) => Flag(new RpcRequest { Op = "dns.apply", Guid = g.ToString(), Profile = p, Families = families });
        public bool SetAutomatic(Guid g, string families) => Flag(new RpcRequest { Op = "dns.auto", Guid = g.ToString(), Families = families });
        public bool Restore(Guid g) => Flag(new RpcRequest { Op = "dns.restore", Guid = g.ToString() });
        public void RestoreAll() => Fire(new RpcRequest { Op = "dns.restoreall" });

        // DNS hold (timed override kept by the service)
        public bool RegisterHold(string profileId, IReadOnlyList<Guid> interfaces, int seconds) =>
            Flag(new RpcRequest { Op = "dns.hold", ProfileId = profileId, Guids = interfaces.Select(g => g.ToString()).ToList(), Seconds = seconds });
        public void CancelHold() => Fire(new RpcRequest { Op = "dns.hold.cancel" });
        public bool StopHold() => Flag(new RpcRequest { Op = "dns.hold.stop" });
        public (bool active, string profileId, int remainingSeconds) GetHold()
        {
            var r = Call(new RpcRequest { Op = "dns.hold.status" });
            return r is { Ok: true, Flag: true } ? (true, r.Text, r.Count) : (false, "", 0);
        }
        public void ReleaseExceptHeld() => Fire(new RpcRequest { Op = "dns.release" });
    }

    public static class PrivilegedRpcTests
    {
        public static (int pass, int fail, List<string> failures) RunSelfTest()
        {
            int pass = 0; var fails = new List<string>();
            void Check(bool ok, string what) { if (ok) pass++; else fails.Add(what); }
            const string GoodConf = "[Interface]\nPrivateKey = abc=\nAddress = 10.0.0.2/32\n\n[Peer]\nPublicKey = def=\nAllowedIPs = 0.0.0.0/0\n";

            // validators
            Check(RpcValidator.Name("Home VPN") == null, "name: plain with space");
            Check(RpcValidator.Name("work_01-eu") == null, "name: separators");
            Check(RpcValidator.Name("Büro") == null, "name: unicode letters");
            Check(RpcValidator.Name("..\\evil") != null, "name: path chars rejected");
            Check(RpcValidator.Name("a/b") != null, "name: slash rejected");
            Check(RpcValidator.Name("x\"y") != null, "name: quote rejected");
            Check(RpcValidator.Name("") != null && RpcValidator.Name(null) != null, "name: empty/null rejected");
            Check(RpcValidator.Name(new string('a', 100)) != null, "name: too long rejected");
            Check(RpcValidator.Name("trail ") != null, "name: trailing space rejected");
            Check(RpcValidator.Conf(GoodConf) == null, "conf: good accepted");
            Check(RpcValidator.Conf(GoodConf.Replace("[Peer]", "PostUp = calc.exe\n[Peer]")) != null, "conf: PostUp rejected");
            Check(RpcValidator.Conf(GoodConf.Replace("PrivateKey", "preup")) != null, "conf: PreUp rejected");
            Check(RpcValidator.Conf("[Peer]\nPublicKey = x=") != null, "conf: no interface/key rejected");
            Check(RpcValidator.Conf(new string('a', 70_000)) != null, "conf: oversized rejected");
            Check(RpcValidator.Conf(GoodConf + "\0") != null, "conf: NUL rejected");
            Check(RpcValidator.Guid(System.Guid.NewGuid().ToString(), out _) == null, "guid: ok");
            Check(RpcValidator.Guid("nope", out _) != null && RpcValidator.Guid(System.Guid.Empty.ToString(), out _) != null, "guid: bad/empty rejected");
            Check(RpcValidator.Families("both") == null && RpcValidator.Families("v6") == null && RpcValidator.Families("x; calc") != null, "families");
            Check(RpcValidator.Endpoint("203.0.113.5") == null && RpcValidator.Endpoint("2001:db8::1") == null && RpcValidator.Endpoint("a b") != null, "endpoint");
            Check(RpcValidator.Bypass(new List<string> { "10.0.0.0/8", "fd00::/8" }) == null, "bypass: ok");
            Check(RpcValidator.Bypass(new List<string> { "10.0.0.0/8; calc" }) != null, "bypass: injection rejected");
            Check(RpcValidator.Bypass(Enumerable.Repeat("10.0.0.0/8", 300).ToList()) != null, "bypass: too many rejected");
            var good = new DnsProfile { Id = "p", Name = "P", V4Primary = "1.1.1.1", Encryption = "doh", DohTemplate = "https://cloudflare-dns.com/dns-query" };
            Check(RpcValidator.Profile(good) == null, "profile: ok");
            Check(RpcValidator.Profile(new DnsProfile { V4Primary = "1.1.1.1 & calc" }) != null, "profile: server injection rejected");
            Check(RpcValidator.Profile(new DnsProfile { Encryption = "doh", DohTemplate = "http://x/y" }) != null, "profile: non-https template rejected");
            Check(RpcValidator.Profile(new DnsProfile { Encryption = "doh", DohTemplate = "https://x/y\" & calc" }) != null, "profile: template quote rejected");
            Check(RpcValidator.Profile(null) != null, "profile: null rejected");

            // auth
            Check(RpcAuth.SidAllowed("S-1-5-21-1", new[] { "S-1-5-21-1", "S-1-5-21-2" }), "auth: listed sid allowed");
            Check(!RpcAuth.SidAllowed("S-1-5-21-3", new[] { "S-1-5-21-1" }) && !RpcAuth.SidAllowed(null, new[] { "S-1-5-21-1" }), "auth: unlisted/null denied");
            Check(RpcAuth.LoadAllowed(Path.Combine(Path.GetTempPath(), "mg-no-such-file-" + System.Guid.NewGuid())).Count == 0, "auth: missing file = nobody");

            // dispatcher against the fake
            var fake = new FakeOps();
            var disp = new RpcDispatcher(fake, fake, fake, (n, c) => (n + ".conf", null));
            Check(disp.Handle(new RpcRequest { Op = "ping" }).Count == RpcDispatcher.ProtocolVersion, "dispatch: ping");
            Check(disp.Handle(new RpcRequest { Op = "tunnel.connect", Name = "t1", Conf = GoodConf }) is { Ok: true, Flag: true }, "dispatch: connect");
            Check(fake.Calls.Contains("tunnel.connect t1"), "dispatch: connect reached ops");
            int before = fake.Calls.Count;
            Check(!disp.Handle(new RpcRequest { Op = "tunnel.connect", Name = "t1", Conf = "PostUp=x" }).Ok && fake.Calls.Count == before, "dispatch: invalid conf never reaches ops");
            Check(!disp.Handle(new RpcRequest { Op = "tunnel.disconnect", Name = "../x" }).Ok && fake.Calls.Count == before, "dispatch: invalid name never reaches ops");
            Check(!disp.Handle(new RpcRequest { Op = "format.disk" }).Ok, "dispatch: unknown op rejected");
            var gid = System.Guid.NewGuid().ToString();
            Check(disp.Handle(new RpcRequest { Op = "dns.apply", Guid = gid, Families = "both", Profile = good }).Flag, "dispatch: dns apply");
            Check(disp.Handle(new RpcRequest { Op = "dns.has", Guid = gid }).Flag, "dispatch: dns has");
            Check(disp.Handle(new RpcRequest { Op = "dns.count" }).Count == 1, "dispatch: dns count");
            disp.Handle(new RpcRequest { Op = "dns.restore", Guid = gid });
            Check(!disp.Handle(new RpcRequest { Op = "dns.has", Guid = gid }).Flag, "dispatch: dns restore");
            disp.Handle(new RpcRequest { Op = "ks.enable", Name = "t1", Endpoint = "203.0.113.5", Bypass = new List<string> { "10.0.0.0/8" } });
            Check(fake.Calls.Contains("ks.enable t1 203.0.113.5 bypass=1"), "dispatch: ks enable args");

            // DNS hold ops
            {
                var f6 = new FakeOps(); var keeper = new DnsHoldKeeper(); var clock = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
                var d6 = new RpcDispatcher(f6, f6, f6, (n, c) => ("", null), keeper, () => f6.OverriddenGuids, () => clock);
                var ga = System.Guid.NewGuid().ToString(); var gb = System.Guid.NewGuid();
                var held6 = new RpcRequest { Op = "dns.hold", ProfileId = "google", Guids = new List<string> { ga }, Seconds = 60 };
                Check(d6.Handle(held6) is { Ok: true, Flag: true }, "hold: register");
                Check(d6.Handle(new RpcRequest { Op = "dns.hold.status" }) is { Flag: true, Count: 60, Text: "google" }, "hold: status");
                Check(!d6.Handle(new RpcRequest { Op = "dns.hold", ProfileId = "a b", Guids = new List<string> { ga }, Seconds = 60 }).Ok, "hold: bad profile id refused");
                Check(!d6.Handle(new RpcRequest { Op = "dns.hold", ProfileId = "x", Guids = new List<string> { ga }, Seconds = 0 }).Ok, "hold: zero seconds refused");
                Check(!d6.Handle(new RpcRequest { Op = "dns.hold", ProfileId = "x", Guids = new List<string> { ga }, Seconds = 99999 }).Ok, "hold: too long refused");
                Check(!d6.Handle(new RpcRequest { Op = "dns.hold", ProfileId = "x", Guids = new List<string>(), Seconds = 5 }).Ok, "hold: no interfaces refused");
                Check(!d6.Handle(new RpcRequest { Op = "dns.hold", ProfileId = "x", Guids = new List<string> { "nope" }, Seconds = 5 }).Ok, "hold: bad interface refused");
                f6.ApplyProfile(System.Guid.Parse(ga), good, "both"); f6.ApplyProfile(gb, good, "both");
                d6.Handle(new RpcRequest { Op = "dns.release" });
                Check(f6.HasOverride(System.Guid.Parse(ga)) && !f6.HasOverride(gb), "hold: release keeps the held interface, restores the others");
                d6.Handle(new RpcRequest { Op = "dns.hold.cancel" });
                Check(!d6.Handle(new RpcRequest { Op = "dns.hold.status" }).Flag, "hold: cancel");
                d6.Handle(held6);
                Check(d6.Handle(new RpcRequest { Op = "dns.hold.stop" }).Flag && !f6.HasOverride(System.Guid.Parse(ga)) && !d6.Handle(new RpcRequest { Op = "dns.hold.status" }).Flag, "hold: stop restores the held interface and clears");
                Check(!d6.Handle(new RpcRequest { Op = "dns.hold.stop" }).Flag, "hold: stop with nothing held");
                var plain = new RpcDispatcher(f6, f6, f6, (n, c) => ("", null));
                Check(!plain.Handle(held6).Ok && !plain.Handle(new RpcRequest { Op = "dns.hold.status" }).Flag, "hold: not supported without a keeper");
            }

            // stored-config connect: stale -> push -> connect, split applied by the service
            {
                var f7 = new FakeOps();
                var dir7 = Path.Combine(Path.GetTempPath(), "mg-rpcstore-" + System.Guid.NewGuid().ToString("N"));
                try
                {
                    byte[] Xor(byte[] b) => b.Select(x => (byte)(x ^ 0x33)).ToArray();
                    var store7 = new FileTunnelStore(dir7, Xor, Xor);
                    string staged7 = "";
                    var d7 = new RpcDispatcher(f7, f7, f7, (n, c) => { staged7 = c; return (n + ".conf", null); }, store: store7);
                    var conf7 = GoodConf.Replace("AllowedIPs = 0.0.0.0/0", "AllowedIPs = 0.0.0.0/0, ::/0");
                    string h7 = FileTunnelStore.HashOf(conf7);
                    var ask = new RpcRequest { Op = "tunnel.connectstored", Name = "st1", Hash = h7, SplitMode = "off" };
                    Check(d7.Handle(ask) is { Ok: false, Error: "stale" }, "stored: unknown tunnel is stale");
                    Check(d7.Handle(new RpcRequest { Op = "store.put", Name = "st1", Conf = conf7 }) is { Ok: true, Text: var ph } && ph == h7, "stored: put returns the hash");
                    Check(d7.Handle(ask) is { Ok: true, Flag: true } && f7.Calls.Contains("tunnel.connect st1") && staged7 == conf7, "stored: connect uses the stored config");
                    var ex = new RpcRequest { Op = "tunnel.connectstored", Name = "st1", Hash = h7, SplitMode = "exclude", SplitRanges = new List<string> { "10.0.0.0/8" } };
                    d7.Handle(ex);
                    Check(staged7 != conf7 && staged7.Contains("AllowedIPs") && !staged7.Contains("PostUp"), "stored: split rewrite applied by the service");
                    Check(d7.Handle(new RpcRequest { Op = "tunnel.connectstored", Name = "st1", Hash = FileTunnelStore.HashOf("other"), SplitMode = "off" }) is { Ok: false, Error: "stale" }, "stored: a different hash is stale");
                    Check(!d7.Handle(new RpcRequest { Op = "tunnel.connectstored", Name = "st1", Hash = "xyz", SplitMode = "off" }).Ok, "stored: bad hash refused");
                    Check(!d7.Handle(new RpcRequest { Op = "tunnel.connectstored", Name = "st1", Hash = h7, SplitMode = "banana" }).Ok, "stored: bad split mode refused");
                    Check(!d7.Handle(new RpcRequest { Op = "tunnel.connectstored", Name = "st1", Hash = h7, SplitMode = "exclude", SplitRanges = new List<string> { "10.0.0.0/8; calc" } }).Ok, "stored: bad split range refused");
                    Check(!d7.Handle(new RpcRequest { Op = "store.put", Name = "../x", Conf = conf7 }).Ok, "stored: bad name refused on put");
                    Check(!d7.Handle(new RpcRequest { Op = "store.put", Name = "st2", Conf = "[Interface]\nPrivateKey = a=\nPostUp = calc" }).Ok, "stored: hook config refused on put");
                    d7.Handle(new RpcRequest { Op = "store.put", Name = "st2", Conf = conf7 });
                    Check(d7.Handle(new RpcRequest { Op = "store.prune", Names = new List<string> { "st2" } }) is { Ok: true, Count: 1 } && store7.Get("st1") == null, "stored: prune removes tunnels not listed");
                    Check(!new RpcDispatcher(f7, f7, f7, (n, c) => ("", null)).Handle(ask).Ok, "stored: not supported without a store");

                    // through a real pipe: the proxy pushes on stale, then connects by name
                    var pipe7 = "MasselGUARD.SelfTest.Rpc." + System.Guid.NewGuid().ToString("N");
                    store7.Prune(new string[0]); f7.Calls.Clear();
                    using (var srv7 = new RpcServer(d7, () => true, pipe7))
                    {
                        srv7.Start();
                        var ops7 = new RpcOps(pipe7, f7);
                        Check(ops7.ConnectStored("st3", conf7, new SplitConfig { Mode = "off" }, _ => { }, out var e7) && e7 == "", "stored pipe: first connect pushes the config, then connects");
                        Check(store7.Get("st3") == conf7 && f7.Calls.Contains("tunnel.connect st3"), "stored pipe: config stored and connected");
                        f7.Calls.Clear();
                        Check(ops7.ConnectStored("st3", conf7, new SplitConfig { Mode = "off" }, _ => { }, out _) && f7.Calls.Count == 1, "stored pipe: second connect needs no push");
                        var changed = conf7.Replace("10.0.0.2", "10.0.0.3");
                        Check(ops7.ConnectStored("st3", changed, new SplitConfig { Mode = "off" }, _ => { }, out _) && store7.Get("st3") == changed, "stored pipe: an edited config is pushed again");
                        ops7.PruneStored(new[] { "other" });
                        Check(store7.Get("st3") == null, "stored pipe: prune");
                    }
                }
                catch (Exception ex2) { fails.Add("stored threw: " + ex2.Message); }
                finally { try { Directory.Delete(dir7, true); } catch { } }
            }

            // snapshot handover ops
            {
                var f9 = new FakeOps(); var st9 = new AutomationState(); string persisted9 = "";
                var clock9 = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
                var d9 = new RpcDispatcher(f9, f9, f9, (n, c) => ("", null), now: () => clock9, auto: st9, persistSnapshot: s => persisted9 = s);
                var pushCfg = new AppConfig { HeadlessAutomation = true, Tunnels = new List<StoredTunnel> { new() { Name = "T", Source = "local", Path = "C:\\x.dpapi" } } };
                var pushJson = AutomationSnapshot.Serialize(pushCfg);
                Check(d9.Handle(new RpcRequest { Op = "auto.push", Snapshot = pushJson }) is { Ok: true }, "auto: push accepted");
                Check(st9.Config is { HeadlessAutomation: true } && st9.Config.Tunnels[0].Path == null, "auto: pushed config kept, file path dropped");
                Check(persisted9 == pushJson, "auto: pushed snapshot persisted");
                Check(st9.Lease.IsAlive(clock9), "auto: a push renews the lease");
                st9.Lease.Release();
                Check(d9.Handle(new RpcRequest { Op = "auto.lease" }) is { Ok: true, Flag: true } && st9.Lease.IsAlive(clock9), "auto: lease op renews");
                d9.Handle(new RpcRequest { Op = "auto.release" });
                Check(!st9.Lease.IsAlive(clock9), "auto: release ends the lease");
                Check(!d9.Handle(new RpcRequest { Op = "auto.push", Snapshot = "{ nope" }).Ok && st9.Config!.HeadlessAutomation, "auto: a bad snapshot is refused and the old one stays");
                Check(!d9.Handle(new RpcRequest { Op = "auto.push", Snapshot = new string('x', AutomationSnapshot.MaxBytes + 10) }).Ok, "auto: an oversized snapshot is refused");
                var none9 = new RpcDispatcher(f9, f9, f9, (n, c) => ("", null));
                Check(!none9.Handle(new RpcRequest { Op = "auto.push", Snapshot = pushJson }).Ok && !none9.Handle(new RpcRequest { Op = "auto.lease" }).Flag, "auto: not supported without a state");
            }

            // who may do what: users, owners, name clashes, adapters, catch-all ranges
            {
                var userA = new RpcCaller("S-1-5-21-1-1-1-1001", false);
                var userB = new RpcCaller("S-1-5-21-1-1-1-1002", false);
                var adminC = new RpcCaller("S-1-5-21-1-1-1-500", true);
                var fx = new FakeOps();
                var dirx = Path.Combine(Path.GetTempPath(), "mg-authz-" + System.Guid.NewGuid().ToString("N"));
                try
                {
                    byte[] Xor(byte[] b) => b.Select(x => (byte)(x ^ 0x21)).ToArray();
                    var stx = new FileTunnelStore(dirx, Xor, Xor);
                    var autox = new AutomationState(); string ownerSaved = "";
                    var okAdapter = System.Guid.NewGuid();
                    var dx = new RpcDispatcher(fx, fx, fx, (n, c) => (n + ".conf", null), store: stx, auto: autox,
                                               tunnelAllowed: n => n != "Foreign", interfaceAllowed: g => g == okAdapter, persistOwner: s => ownerSaved = s);

                    // tunnel names belong to the user who stored them
                    Check(dx.Handle(new RpcRequest { Op = "store.put", Name = "Mine", Conf = GoodConf }, userA).Ok, "authz: user A stores a tunnel");
                    Check(!dx.Handle(new RpcRequest { Op = "store.put", Name = "Mine", Conf = GoodConf.Replace("abc=", "evil=") }, userB).Ok && stx.Get("Mine") == GoodConf, "authz: user B cannot overwrite A's tunnel");
                    Check(dx.Handle(new RpcRequest { Op = "store.put", Name = "Mine", Conf = GoodConf + "# admin\n" }, adminC).Ok && stx.OwnerOf("Mine") == userA.Sid, "authz: an administrator can, the owner stays");
                    dx.Handle(new RpcRequest { Op = "store.put", Name = "Other", Conf = GoodConf }, userB);
                    dx.Handle(new RpcRequest { Op = "store.prune", Names = new List<string>() }, userB);
                    Check(stx.Get("Other") == null && stx.Get("Mine") != null, "authz: user B's prune removes only B's tunnels");

                    // a service name we did not create
                    Check(!dx.Handle(new RpcRequest { Op = "tunnel.connect", Name = "Foreign", Conf = GoodConf }, userA).Ok, "authz: connect refused for a name that belongs to another service");
                    Check(!dx.Handle(new RpcRequest { Op = "tunnel.disconnect", Name = "Foreign" }, userA).Ok, "authz: disconnect refused for a name that belongs to another service");
                    Check(!dx.Handle(new RpcRequest { Op = "tunnel.connectstored", Name = "Foreign", Hash = FileTunnelStore.HashOf(GoodConf), SplitMode = "off" }, userA).Ok, "authz: stored connect refused for a name that belongs to another service");
                    Check(dx.Handle(new RpcRequest { Op = "tunnel.disconnect", Name = "Mine" }, userA).Ok, "authz: disconnect of an own tunnel works");

                    // adapters
                    var gp = new DnsProfile { Id = "p", Name = "P", V4Primary = "1.1.1.1" };
                    Check(dx.Handle(new RpcRequest { Op = "dns.apply", Guid = okAdapter.ToString(), Families = "both", Profile = gp }, userA).Flag, "authz: DNS on a usable adapter");
                    Check(!dx.Handle(new RpcRequest { Op = "dns.apply", Guid = System.Guid.NewGuid().ToString(), Families = "both", Profile = gp }, userA).Ok, "authz: DNS refused on an unknown/unusable adapter");
                    Check(!dx.Handle(new RpcRequest { Op = "dns.auto", Guid = System.Guid.NewGuid().ToString(), Families = "both" }, userA).Ok, "authz: DNS automatic refused on an unusable adapter");

                    // validators
                    foreach (var bad in new[] { "con", "NUL", "com1", "lpt9", "aux.x", "Con.conf" })
                        Check(RpcValidator.Name(bad) != null, $"name: reserved device name '{bad}' refused");
                    Check(RpcValidator.Name("console") == null && RpcValidator.Name("com10") == null, "name: names that merely start like a device are fine");
                    Check(RpcValidator.Bypass(new List<string> { "0.0.0.0/0" }) != null && RpcValidator.Bypass(new List<string> { "::/0" }) != null, "bypass: a catch-all range is refused");
                    Check(RpcValidator.Bypass(new List<string> { "10.1.2.3" }) == null && RpcValidator.Bypass(new List<string> { "10.0.0.0/8" }) == null, "bypass: plain addresses and CIDRs are fine");

                    // automation belongs to one user
                    var pushA = AutomationSnapshot.Serialize(new AppConfig { HeadlessAutomation = true });
                    var pushOff = AutomationSnapshot.Serialize(new AppConfig { HeadlessAutomation = false });
                    Check(dx.Handle(new RpcRequest { Op = "auto.push", Snapshot = pushA }, userA).Ok && autox.OwnerSid == userA.Sid && ownerSaved == userA.Sid, "authz: the first user to enable it becomes the owner");
                    Check(!dx.Handle(new RpcRequest { Op = "auto.push", Snapshot = pushOff }, userB).Ok && autox.Config!.HeadlessAutomation, "authz: another user cannot replace the snapshot");
                    autox.Lease.Release();
                    dx.Handle(new RpcRequest { Op = "auto.lease" }, userB);
                    Check(!autox.Lease.IsAlive(DateTime.UtcNow), "authz: another user cannot hold the lease");
                    dx.Handle(new RpcRequest { Op = "auto.lease" }, userA);
                    Check(autox.Lease.IsAlive(DateTime.UtcNow), "authz: the owner renews the lease");
                    dx.Handle(new RpcRequest { Op = "auto.release" }, userB);
                    Check(autox.Lease.IsAlive(DateTime.UtcNow), "authz: another user cannot release it");
                    Check(dx.Handle(new RpcRequest { Op = "auto.push", Snapshot = pushOff }, adminC).Ok && !autox.Config!.HeadlessAutomation && autox.OwnerSid == userA.Sid, "authz: an administrator can replace it, the owner stays");
                }
                catch (Exception exz) { fails.Add("authz threw: " + exz.Message); }
                finally { try { Directory.Delete(dirx, true); } catch { } }
            }

            // bounded reader
            try
            {
                var big = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 5000) + "\n"));
                bool threw = false;
                try { RpcWire.ReadLineAsync(big, 1000, CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidDataException) { threw = true; }
                Check(threw, "wire: oversized line rejected");
                var ok = new MemoryStream(Encoding.UTF8.GetBytes("hello\r\nrest"));
                Check(RpcWire.ReadLineAsync(ok, 1000, CancellationToken.None).GetAwaiter().GetResult() == "hello", "wire: line read, CR trimmed");
            }
            catch (Exception ex) { fails.Add("wire threw: " + ex.Message); }

            // real pipe round trip (private pipe name; a running service is never touched)
            try
            {
                var pipe = "MasselGUARD.SelfTest.Rpc." + System.Guid.NewGuid().ToString("N");
                var f2 = new FakeOps();
                var d2 = new RpcDispatcher(f2, f2, f2, (n, c) => (n + ".conf", null));
                using (var srv = new RpcServer(d2, () => true, pipe))
                {
                    srv.Start();
                    var ops = new RpcOps(pipe, f2);
                    Check(ops.IsAvailable(), "pipe: ping answered");
                    var tmp = Path.Combine(Path.GetTempPath(), "mg-rpc-" + System.Guid.NewGuid().ToString("N") + ".conf");
                    File.WriteAllText(tmp, GoodConf);
                    try
                    {
                        Check(ops.Connect("pipe1", tmp, _ => { }, out var e) && e == "", "pipe: connect ok");
                        Check(f2.Calls.Contains("tunnel.connect pipe1"), "pipe: connect arrived");
                        Check(!ops.Connect("../bad", tmp, _ => { }, out var e2) && e2 != "", "pipe: invalid name refused with error");
                    }
                    finally { try { File.Delete(tmp); } catch { } }
                    var pg = System.Guid.NewGuid();
                    Check(ops.ApplyProfile(pg, good, "both") && ops.HasOverride(pg) && ops.OverrideCount == 1, "pipe: dns apply/has/count");
                    ops.Restore(pg);
                    Check(!ops.HasOverride(pg), "pipe: dns restore");
                    ops.Enable("pipe1", "203.0.113.5", new[] { "10.0.0.0/8" });
                    Check(f2.Calls.Contains("ks.enable pipe1 203.0.113.5 bypass=1"), "pipe: ks enable");

                    // oversized request is refused without crashing the server
                    using (var c = new NamedPipeClientStream(".", pipe, PipeDirection.InOut))
                    {
                        c.Connect(2000);
                        var junk = Encoding.UTF8.GetBytes(new string('x', RpcValidator.MaxLineBytes + 10) + "\n");
                        var wt = Task.Run(() => { try { c.Write(junk, 0, junk.Length); c.Flush(); } catch { } }); wt.Wait(3000);
                    }
                    { bool av = false; for (int i = 0; i < 6 && !av; i++) { av = ops.IsAvailable(); if (!av) Thread.Sleep(500); } Check(av, "pipe: server survives oversized request"); }
                }
                var pipe2 = "MasselGUARD.SelfTest.Rpc." + System.Guid.NewGuid().ToString("N");
                var f3 = new FakeOps();
                using (var denied = new RpcServer(new RpcDispatcher(f3, f3, f3, (n, c) => ("", null)), () => false, pipe2))
                {
                    denied.Start();
                    var ops = new RpcOps(pipe2, f3);
                    Check(!ops.IsAvailable(), "pipe: unauthorized caller gets no service");
                    Check(!ops.Disconnect("x", out _) && f3.Calls.Count == 0, "pipe: unauthorized call never reaches ops");
                }
                Check(!new RpcOps(pipe2 + ".none").IsAvailable(), "pipe: no listener = unavailable");
            }
            catch (Exception ex) { fails.Add("pipe round trip threw: " + ex.Message); }

            // server identity (a process that squatted the pipe name must get nothing) + the real caller check
            try
            {
                var pipe3 = "MasselGUARD.SelfTest.Rpc." + System.Guid.NewGuid().ToString("N");
                var f4 = new FakeOps();
                using (var srv = new RpcServer(new RpcDispatcher(f4, f4, f4, (n, c) => ("", null)), () => true, pipe3))
                {
                    srv.Start();
                    Check(new RpcOps(pipe3, f4, () => Environment.ProcessId).IsAvailable(), "pipe: expected server pid accepted");
                    var wrong = new RpcOps(pipe3, f4, () => Environment.ProcessId + 1);
                    Check(!wrong.IsAvailable() && wrong.LastError.Contains("not the MasselGUARD service"), "pipe: wrong server pid refused");
                    Check(!wrong.Disconnect("x", out _) && f4.Calls.Count == 0, "pipe: nothing sent to a wrong server");
                }
                var pipe4 = "MasselGUARD.SelfTest.Rpc." + System.Guid.NewGuid().ToString("N");
                var f5 = new FakeOps();
                using (var srv = new RpcServer(new RpcDispatcher(f5, f5, f5, (n, c) => ("", null)), null, pipe4))
                {
                    srv.Start();
                    bool expect;
                    using (var me = WindowsIdentity.GetCurrent())
                        expect = new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator) || RpcAuth.SidAllowed(me.User?.Value, RpcAuth.LoadAllowed());
                    Check(new RpcOps(pipe4, f5).IsAvailable() == expect, "pipe: real caller check matches admin/allowed-list rule");
                }
            }
            catch (Exception ex) { fails.Add("pipe identity/auth threw: " + ex.Message); }

            return (pass, fails.Count, fails);
        }
    }
}
