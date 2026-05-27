using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Security.AccessControl;
using System.Security.Cryptography;
using MasselGUARD;
using System.Security.Principal;
using System.ServiceProcess;
using MasselGUARD.Models;
using MasselGUARD.Infrastructure;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Connects and disconnects WireGuard tunnels.
    /// Supports two backends:
    ///   Local  — tunnel.dll + wireguard.dll (wireguard-NT)
    ///   WireGuard — WireGuard for Windows ServiceController
    ///
    /// All side-effect operations (DPAPI, ACL, services) live here.
    /// No UI references.
    /// </summary>
    public class TunnelService
    {
        private static readonly string TunnelDir = Path.Combine(
            AppContext.BaseDirectory, "tunnels");
        private static readonly string TempDir = Path.Combine(TunnelDir, "temp");

        private readonly LogService    _log;
        private readonly ScriptService _scripts;
        private readonly System.Collections.Generic.Dictionary<string,DateTime> _connectTimes = new();

        public TunnelService(LogService log, ScriptService scripts)
        {
            _log     = log;
            _scripts = scripts;
        }

        // ── Connect ───────────────────────────────────────────────────────────

        public bool Connect(StoredTunnel stored, AppConfig cfg)
        {
            try
            {
                RunScript(stored.PreConnectScript, "pre-connect", stored.Name);

                bool ok = stored.Source == "local"
                    ? ConnectLocal(stored, cfg)
                    : ConnectWireGuard(stored, cfg);

                if (ok)
                    RunScript(stored.PostConnectScript, "post-connect", stored.Name);

                return ok;
            }
            catch (Exception ex)
            {
                _log.Warn($"Connect failed ({stored.Name}): {ex.Message}");
                return false;
            }
        }

        private bool ConnectLocal(StoredTunnel stored, AppConfig cfg)
        {
            // Validate DLLs exist
            var tunnelDll   = Path.Combine(AppContext.BaseDirectory, "tunnel.dll");
            var wireGuardDll = Path.Combine(AppContext.BaseDirectory, "wireguard.dll");
            if (!File.Exists(tunnelDll) || !File.Exists(wireGuardDll))
            {
                _log.Warn("tunnel.dll or wireguard.dll not found");
                return false;
            }

            // Decrypt config
            string plaintext = DecryptConfig(stored);
            if (string.IsNullOrEmpty(plaintext))
            {
                _log.Warn($"Could not decrypt config for {stored.Name}");
                return false;
            }

            // Write to secure temp file
            Directory.CreateDirectory(TempDir);
            var tempPath = Path.Combine(TempDir, stored.Name + ".conf");
            WriteSecure(tempPath, plaintext);

            try
            {
                _log.Debug($"[DBG] Connecting local tunnel: {stored.Name}");
                bool ok2 = TunnelDll.Connect(stored.Name, tempPath, msg => _log.Debug(msg), out string err);
                if (ok2)
                {
                    _log.Ok($"Connected: {stored.Name}");
                    // Stamp service so orphan scan can identify it as MasselGUARD-managed
                    try
                    {
                        using var regKey = Microsoft.Win32.Registry.LocalMachine
                            .OpenSubKey($@"SYSTEM\CurrentControlSet\Services\WireGuardTunnel${stored.Name}",
                                        writable: true);
                        regKey?.SetValue("Description", $"MasselGUARD Tunnel: {stored.Name}");
                        regKey?.SetValue("DisplayName", $"WireGuard Tunnel: MasselGUARD - {stored.Name}");
                    }
                    catch { /* non-critical */ }
                    return true;
                }
                _log.Warn($"TunnelDll: {err}"); return false;
            }
            catch (Exception ex)
            {
                _log.Warn($"TunnelDll.Connect failed: {ex.Message}");
                return false;
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        private bool ConnectWireGuard(StoredTunnel stored, AppConfig cfg)
        {
            var svcName = "WireGuardTunnel$" + stored.Name;
            try
            {
                _log.Debug($"[DBG] Starting WireGuard service: {svcName}");
                using var sc = new ServiceController(svcName);
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running,
                    TimeSpan.FromSeconds(15));
                _log.Ok($"Connected: {stored.Name} (WireGuard)");
                _connectTimes[stored.Name] = DateTime.UtcNow;
                return true;
            }
            catch (Exception ex)
            {
                _log.Warn($"WireGuard service start failed: {ex.Message}");
                return false;
            }
        }

        // ── Disconnect ────────────────────────────────────────────────────────

        public bool Disconnect(StoredTunnel stored)
        {
            try
            {
                RunScript(stored.PreDisconnectScript, "pre-disconnect", stored.Name);

                bool ok = stored.Source == "local"
                    ? DisconnectLocal(stored)
                    : DisconnectWireGuard(stored);

                if (ok)
                    RunScript(stored.PostDisconnectScript, "post-disconnect", stored.Name);

                return ok;
            }
            catch (Exception ex)
            {
                _log.Warn($"Disconnect failed ({stored.Name}): {ex.Message}");
                return false;
            }
        }

        private bool DisconnectLocal(StoredTunnel stored)
        {
            try
            {
                TunnelDll.Disconnect(stored.Name, out string disconnErr);
                LogDisconnect(stored.Name);
                if (!string.IsNullOrEmpty(disconnErr)) _log.Warn(disconnErr);
                return true;
            }
            catch (Exception ex) { _log.Warn($"TunnelDll.Disconnect failed: {ex.Message}"); return false; }
        }

        private bool DisconnectWireGuard(StoredTunnel stored)
        {
            var svcName = "WireGuardTunnel$" + stored.Name;
            try
            {
                using var sc = new ServiceController(svcName);
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                LogDisconnect(stored.Name);
                return true;
            }
            catch (Exception ex) { _log.Warn($"WireGuard service stop failed: {ex.Message}"); return false; }
        }

        private void LogDisconnect(string name)
        {
            if (_log.IsExtended && _connectTimes.TryGetValue(name, out var connectedAt))
            {
                var elapsed = DateTime.UtcNow - connectedAt;
                string dur = elapsed.TotalSeconds < 60  ? $"{(int)elapsed.TotalSeconds}s"
                           : elapsed.TotalMinutes < 60  ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s"
                           : elapsed.TotalHours < 24    ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes:D2}m"
                           : $"{(int)elapsed.TotalDays}d {elapsed.Hours:D2}h {elapsed.Minutes:D2}m";
                _log.Ok(Lang.T("TunnelDisconnectedAfter", name, dur));
            }
            else
            {
                _log.Ok($"Disconnected: {name}");
            }
            _connectTimes.Remove(name);
        }

        // ── Status ────────────────────────────────────────────────────────────

        public bool IsActive(StoredTunnel stored)
        {
            try
            {
                if (stored.Source == "local")
                    return TunnelDll.IsRunning(stored.Name);

                var svcName = "WireGuardTunnel$" + stored.Name;
                using var sc = new ServiceController(svcName);
                return sc.Status == ServiceControllerStatus.Running;
            }
            catch { return false; }
        }

        // ── DPAPI helpers ─────────────────────────────────────────────────────

        private static string DecryptConfig(StoredTunnel stored)
        {
            if (!string.IsNullOrEmpty(stored.Config))
            {
                try
                {
                    var bytes = ProtectedData.Unprotect(
                        Convert.FromBase64String(stored.Config), null,
                        DataProtectionScope.CurrentUser);
                    return System.Text.Encoding.UTF8.GetString(bytes);
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(stored.Path) && File.Exists(stored.Path))
            {
                if (stored.Path.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var bytes = ProtectedData.Unprotect(
                            File.ReadAllBytes(stored.Path), null,
                            DataProtectionScope.CurrentUser);
                        return System.Text.Encoding.UTF8.GetString(bytes);
                    }
                    catch { }
                }
                return File.ReadAllText(stored.Path);
            }
            return "";
        }

        public static string EncryptConfig(string plaintext)
        {
            var bytes = ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(plaintext), null,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        // ── ACL secure write ─────────────────────────────────────────────────

        private static void WriteSecure(string path, string content)
        {
            // Create empty file first — inherits parent directory ACL
            File.Create(path).Dispose();

            // Lock down to SYSTEM + Admins + current user before writing
            var fi   = new FileInfo(path);
            var acl  = new FileSecurity();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            void Allow(IdentityReference id)
                => acl.AddAccessRule(new FileSystemAccessRule(id,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));

            Allow(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,    null));
            Allow(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            Allow(WindowsIdentity.GetCurrent().User!);
            fi.SetAccessControl(acl);

            using var sw = new StreamWriter(
                new FileStream(path, FileMode.Open, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough));
            sw.Write(content);
        }

        // ── Script helper ─────────────────────────────────────────────────────

        private void RunScript(string script, string hook, string tunnel)
        {
            if (string.IsNullOrWhiteSpace(script)) return;
            var result = _scripts.Run(script, hook, tunnel);
            _log.Debug($"[Script:{hook}] exit={result.ExitCode}" +
                (string.IsNullOrEmpty(result.Output) ? "" : $" — {result.Output}"));
            if (result.ExitCode != 0)
                _log.Warn($"Script {hook} exited {result.ExitCode}");
        }
    }
}
