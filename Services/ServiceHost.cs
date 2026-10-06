using System;
using System.IO;
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
        private System.Threading.Timer? _holdTimer;

        public static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MasselGUARD");
        private static string RunDir => Path.Combine(DataDir, "run");

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
                SecureDirectory(DataDir, usersCanRead: true);   // service-users.txt must stay readable for diagnostics only
                SecureDirectory(RunDir, usersCanRead: false);   // plaintext configs live here briefly: SYSTEM + Administrators only

                _log = new LogService { Enabled = true };
                _log.InitPersistence(Path.Combine(DataDir, "service.log"), 512, false);
                _log.Info("MasselGUARD service starting.");

                _ks  = new KillSwitchService(_log);
                _dns = new DnsService(_log, Path.Combine(DataDir, "dns_state.json"));

                // Crash recovery (design decision 7): a previous run may have left firewall rules or DNS overrides.
                _ks.CleanupStaleRules();
                _dns.RestoreAll();
                SweepRunDir();

                var disp = new RpcDispatcher(new TunnelDllOps(), _ks, _dns, StageConf, _hold, () => _dns.OverriddenGuids);
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
            try { _holdTimer?.Dispose(); } catch { }
            try { _server?.Dispose(); } catch { }
            // Same semantics as closing the elevated app today: put DNS and the firewall back.
            try { _dns?.RestoreAll(); } catch { }
            try { _ks?.DisableAll(); } catch { }
            try { _log?.Info("MasselGUARD service stopped."); } catch { }
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

        private static void SecureDirectory(string dir, bool usersCanRead)
        {
            var di = Directory.CreateDirectory(dir);
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);   // no inheritance from ProgramData
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            if (usersCanRead)
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                    FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            di.SetAccessControl(sec);
        }
    }
}
