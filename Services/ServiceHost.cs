using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;

namespace MasselGUARD.Services
{
    /// <summary>
    /// The privileged back-end as a Windows service (<c>MasselGUARD.exe /svc</c>, LocalSystem). It runs the
    /// RPC server of <see cref="PrivilegedRpc"/> over the real tunnel / DNS / kill-switch implementations
    /// and nothing else: no UI, no rules, no config. See <c>docs/ServiceBackend-Design.md</c>.
    /// GUI-side only (the CLI does not host the service).
    /// </summary>
    public sealed class ServiceHost : ServiceBase
    {
        public const string SvcName = "MasselGUARDsvc";

        private RpcServer? _server;
        private KillSwitchService? _ks;
        private DnsService? _dns;
        private LogService? _log;
        private readonly DnsHoldKeeper _hold = new();
        private readonly AutomationState _auto = new();
        private HeadlessHost? _headless;
        private static string SnapshotFile => Path.Combine(DataDir, "automation.json");
        private System.Threading.Timer? _holdTimer;

        public static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MasselGUARD");
        private static string RunDir => Path.Combine(DataDir, "run");
        private static string StoreDir => Path.Combine(DataDir, "tunnels");
        private static string LogDir => Path.Combine(DataDir, "logs");

        public ServiceHost() { ServiceName = SvcName; CanStop = true; AutoLog = false; }

        /// <summary>Entry point for <c>/svc</c>. Returns the process exit code.</summary>
        public static int Run()
        {
            try { ServiceBase.Run(new ServiceHost()); return 0; }
            catch { return 1; }
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                // Refuse to run from a folder a standard user could write to (they could replace the exe or a DLL).
                var exeDir = AppContext.BaseDirectory.TrimEnd('\\');
                var exeErr = SecureFolders.CheckInstallFolder(exeDir, new[] { Environment.ProcessPath ?? "", Path.Combine(exeDir, "tunnel.dll"), Path.Combine(exeDir, "wireguard.dll") });
                if (exeErr != null) throw new InvalidOperationException(exeErr);

                // Every data folder must be created by an administrator/SYSTEM token and owned by one (a standard user
                // may have pre-created it), with a protected DACL. The log folder is the only one users may read.
                foreach (var (dir, usersRead) in new[] { (DataDir, false), (RunDir, false), (StoreDir, false), (LogDir, true) })
                {
                    var e = SecureFolders.Ensure(dir, usersRead);
                    if (e != null) throw new InvalidOperationException(e);
                }

                _log = new LogService { Enabled = true };
                _log.InitPersistence(Path.Combine(LogDir, "service.log"), 512, false);
                InstallFiles.CleanupOld(exeDir);   // leftovers of an install that replaced files in use
                _log.Info("MasselGUARD service starting.");

                _ks  = new KillSwitchService(_log);
                _dns = new DnsService(_log, Path.Combine(DataDir, "dns_state.json"));

                // Crash recovery (design decision 7): a previous run may have left firewall rules or DNS overrides.
                _ks.CleanupStaleRules();
                _dns.RestoreAll();
                SweepRunDir();

                var store = FileTunnelStore.ForMachine(StoreDir);
                LoadPersistedSnapshot();
                var disp = new RpcDispatcher(new TunnelDllOps(), _ks, _dns, StageConf, _hold, () => _dns.OverriddenGuids,
                                             store: store, auto: _auto, persistSnapshot: PersistSnapshot,
                                             tunnelAllowed: TunnelNameAllowed, interfaceAllowed: InterfaceAllowed, persistOwner: PersistOwner);
                // Automation without a window (only when the window's last pushed config asks for it).
                _headless = new HeadlessHost(_auto, new TunnelDllOps(), _ks, _dns, store, StageConf, _hold, _log);
                _headless.Start();
                // Backstop for timed DNS overrides: ends them when the UI is gone (see DnsHoldKeeper).
                _holdTimer = new System.Threading.Timer(_ =>
                {
                    try { if (_hold.Tick(DateTime.UtcNow, g => _dns!.Restore(g))) _log?.Ok("DNS: timed override ended by the service."); }
                    catch { }
                }, null, 1000, 1000);
                _server = new RpcServer(disp, log: m => _log?.Warn(m));
                _server.Start();
                _log.Ok("MasselGUARD service ready.");
            }
            catch (Exception ex)
            {
                try { System.Diagnostics.EventLog.WriteEntry("MasselGUARD", "Service start failed: " + ex, System.Diagnostics.EventLogEntryType.Error); } catch { }
                throw;
            }
        }

        protected override void OnStop()
        {
            try { _headless?.Dispose(); } catch { }
            try { _holdTimer?.Dispose(); } catch { }
            try { _server?.Dispose(); } catch { }
            // Same semantics as closing the elevated app today: put DNS and the firewall back.
            try { _dns?.RestoreAll(); } catch { }
            try { _ks?.DisableAll(); } catch { }
            try { _log?.Info("MasselGUARD service stopped."); } catch { }
        }

        // ── last pushed window config (so automation works after a reboot, before anyone signs in) ──

        private void PersistSnapshot(string json)
        {
            var tmp = SnapshotFile + ".tmp";
            File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
            File.Move(tmp, SnapshotFile, overwrite: true);
        }

        private static string OwnerFile => Path.Combine(DataDir, "automation.owner");

        private static void PersistOwner(string sid) => File.WriteAllText(OwnerFile, sid, new System.Text.UTF8Encoding(false));

        /// <summary>A tunnel name may only be used when no service of that name exists, or the existing one runs OUR exe
        /// (a service created by WireGuard for Windows, or by anything else, is not ours to stop or replace).</summary>
        private static bool TunnelNameAllowed(string name)
        {
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\WireGuardTunnel$" + name.Replace(' ', '_'));
                if (k == null) return true;
                var image = k.GetValue("ImagePath") as string ?? "";
                return image.Contains("MasselGUARD", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>DNS may only be changed on a real, connected adapter (not loopback or a tunnel interface).</summary>
        private static bool InterfaceAllowed(Guid id)
        {
            try
            {
                return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Any(ni =>
                    Guid.TryParse(ni.Id, out var g) && g == id
                    && ni.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                    && ni.NetworkInterfaceType is not (System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                                                      or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel));
            }
            catch { return false; }
        }

        private void LoadPersistedSnapshot()
        {
            try
            {
                if (File.Exists(OwnerFile))
                {
                    var o = File.ReadAllText(OwnerFile).Trim();
                    if (o.StartsWith("S-1-")) _auto.OwnerSid = o;
                }
                if (!File.Exists(SnapshotFile)) return;
                var cfg = AutomationSnapshot.TryParse(File.ReadAllText(SnapshotFile), out _);
                if (cfg != null) _auto.Config = cfg;   // the lease stays unset: until a window shows up, the service is in charge
            }
            catch { }
        }

        // ── conf hand-off ────────────────────────────────────────────────────────

        private static (string path, IDisposable? cleanup) StageConf(string name, string conf)
        {
            var dir = Path.Combine(RunDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name + ".conf");   // tunnel.dll derives the tunnel name from the file name
            File.WriteAllText(path, conf, new System.Text.UTF8Encoding(false));
            return (path, new Cleanup(dir));
        }

        private sealed class Cleanup : IDisposable
        {
            private readonly string _dir;
            public Cleanup(string dir) { _dir = dir; }
            public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
        }

        private static void SweepRunDir()
        {
            try { foreach (var d in Directory.GetDirectories(RunDir)) try { Directory.Delete(d, true); } catch { } }
            catch { }
        }
    }
}
