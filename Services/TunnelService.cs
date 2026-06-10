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
        /// <summary>Persistent storage for .conf.dpapi files — one per tunnel.</summary>
        public static readonly string TunnelStorageDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MasselGUARD", "tunnels");

        private static readonly string TunnelDir = Path.Combine(
            AppContext.BaseDirectory, "tunnels");
        private static readonly string TempDir = Path.Combine(TunnelDir, "temp");

        private readonly LogService         _log;
        private readonly ScriptService      _scripts;
        private readonly HistoryService     _history;
        private readonly KillSwitchService? _ks;
        private readonly System.Collections.Generic.Dictionary<string, DateTime>      _connectTimes = new();
        /// <summary>Byte counts snapshotted at connect time, for session-delta reporting on disconnect.</summary>
        private readonly System.Collections.Generic.Dictionary<string, (long rx, long tx)> _connectBytes = new();
        /// <summary>
        /// Tunnels that were deliberately stopped by MasselGUARD (user click, WiFi rule, CLI).
        /// Consumed by TunnelEntryViewModel.RefreshStatus() to suppress auto-reconnect.
        /// </summary>
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _intentionalDisconnects =
            new(StringComparer.OrdinalIgnoreCase);

        public TunnelService(LogService log, ScriptService scripts, HistoryService history,
                             KillSwitchService? killSwitch = null)
        {
            _log     = log;
            _scripts = scripts;
            _history = history;
            _ks      = killSwitch;
        }

        private static bool ShouldKillSwitch(StoredTunnel s, AppConfig cfg) =>
            cfg.KillSwitchMode == "always" || s.KillSwitch;

        public static bool ShouldAutoReconnect(StoredTunnel s, AppConfig cfg) =>
            cfg.AutoReconnectMode switch
            {
                "always"     => true,
                "per-tunnel" => s.AutoReconnect,
                _            => false,
            };

        /// <summary>
        /// Returns true and removes the entry when the named tunnel was intentionally
        /// disconnected by MasselGUARD (user, WiFi rule, CLI). Used by
        /// TunnelEntryViewModel to suppress auto-reconnect on deliberate stops.
        /// </summary>
        public bool ConsumeIntentionalDisconnect(string name) =>
            _intentionalDisconnects.TryRemove(name, out _);

        /// <summary>
        /// Records a connect that happened outside MasselGUARD (WireGuard app, CLI).
        /// Opens a history entry and snapshots the byte counters so the session shows
        /// up in the timeline and a later disconnect can report traffic.
        /// </summary>
        public void RecordExternalConnect(string name, string source)
        {
            _intentionalDisconnects.TryRemove(name, out _);
            _connectTimes[name] = DateTime.UtcNow;
            var s0 = TunnelDll.GetTrafficStats(name);
            if (s0.AdapterFound)
                _connectBytes[name] = (s0.RxBytes, s0.TxBytes);
            _history.RecordConnect(name, source);
        }

        /// <summary>
        /// Records a disconnect that happened outside MasselGUARD (WireGuard app
        /// deactivate, CLI, service crash). Closes the open history entry, writes the
        /// extended-log continuation lines, and logs "Disconnected: name{logSuffix}".
        /// Traffic deltas are zero — the adapter is already gone when the poll notices.
        /// </summary>
        public void RecordExternalDisconnect(string name, string logSuffix = "") =>
            LogDisconnect(name, logSuffix: logSuffix);

        // ── Connect ───────────────────────────────────────────────────────────

        /// <param name="source">Human-readable trigger, e.g. "Manual", "Auto-reconnect",
        /// "Rule: HomeNet → Work VPN". Stored in connection history.</param>
        public bool Connect(StoredTunnel stored, AppConfig cfg, string source = "Manual")
        {
            try
            {
                RunScript(stored.PreConnectScript, "pre-connect", stored.Name);

                bool ok = stored.Source == "local"
                    ? ConnectLocal(stored, cfg)
                    : ConnectWireGuard(stored, cfg);

                if (ok)
                {
                    // A successful connect supersedes any earlier intentional-disconnect
                    // mark. Without this, a stale entry from a previous GUI/rule disconnect
                    // (never consumed — the poll misses MasselGUARD's own transitions)
                    // would swallow the next external-drop event.
                    _intentionalDisconnects.TryRemove(stored.Name, out _);
                    _history.RecordConnect(stored.Name, source);
                    RunScript(stored.PostConnectScript, "post-connect", stored.Name);
                }

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
            // Validate DLLs exist and appear to be the correct (wireguard-NT) version
            var dllError = TunnelDll.ValidateDlls();
            if (dllError != null)
            {
                _log.Warn(dllError);
                return false;
            }

            // Decrypt config
            string plaintext = DecryptConfig(stored);
            if (string.IsNullOrEmpty(plaintext))
            {
                _log.Warn($"Could not decrypt config for {stored.Name}");
                return false;
            }

            // Validate before writing — tunnel.dll exits with code 2 for any parse error.
            // Skipped when the per-tunnel flag or the global override is set.
            if (!stored.SkipValidation && !cfg.SkipTunnelValidation)
            {
                var validationError = Cli.WireGuardConf.Validate(plaintext);
                if (validationError != null)
                {
                    // Main message in accent colour, detail hint in grey below
                    _log.Ok($"Config validation failed — {stored.Name}: {validationError.Message}");
                    if (validationError.Detail != null)
                        _log.Write(LogLevel.Debug, validationError.Detail, isContinuation: true);
                    return false;
                }
            }
            else if (stored.SkipValidation || cfg.SkipTunnelValidation)
            {
                _log.Debug($"Config validation skipped for {stored.Name}" +
                           (cfg.SkipTunnelValidation ? " (global override)" : " (per-tunnel flag)") + ".");
            }

            // Write to secure temp file
            Directory.CreateDirectory(TempDir);
            var tempPath = Path.Combine(TempDir, stored.Name + ".conf");
            WriteSecure(tempPath, plaintext);

            // Verify the written file — log size and first line so we can diagnose parse failures.
            try
            {
                var raw   = File.ReadAllBytes(tempPath);
                var check = System.Text.Encoding.UTF8.GetString(raw);
                var first = check.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                 .FirstOrDefault()?.Trim() ?? "(empty)";
                bool hasBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
                _log.Debug($"Conf written: {raw.Length} bytes, BOM={hasBom}, first line={first}");
            }
            catch (Exception ex) { _log.Warn($"Conf verify failed: {ex.Message}"); }

            try
            {
                _log.Debug($"Connecting local tunnel: {stored.Name}");
                bool ok2 = TunnelDll.Connect(stored.Name, tempPath, msg => _log.Debug(msg), out string err);
                if (ok2)
                {
                    _log.Ok($"Connected: {stored.Name}");
                    _connectTimes[stored.Name] = DateTime.UtcNow;
                    // Snapshot initial bytes so we can compute session totals on disconnect
                    var s0 = TunnelDll.GetTrafficStats(stored.Name);
                    _connectBytes[stored.Name] = (s0.RxBytes, s0.TxBytes);
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
                    // Enable kill switch after the tunnel adapter is up
                    if (_ks != null && ShouldKillSwitch(stored, cfg))
                        _ks.Enable(stored.Name, KillSwitchService.ParseEndpointIp(plaintext));
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
            var wgExe = GetWireGuardExe(cfg);
            if (wgExe == null) return false;

            var confPath = stored.Path;
            if (string.IsNullOrEmpty(confPath) || !File.Exists(confPath))
            {
                _log.Warn($"Cannot connect '{stored.Name}': config file not found at '{confPath}'. " +
                          "Re-import the tunnel from the WireGuard client.");
                return false;
            }

            _log.Debug($"wireguard.exe /installtunnelservice \"{confPath}\"");
            var (exit, stderr) = RunWireGuard(wgExe, $"/installtunnelservice \"{confPath}\"");
            if (exit != 0)
            {
                _log.Warn($"WireGuard /installtunnelservice failed (exit {exit})" +
                          (string.IsNullOrEmpty(stderr) ? "" : $": {stderr}"));
                return false;
            }

            // /installtunnelservice returns before the service reaches Running — poll until it does.
            var svcName = "WireGuardTunnel$" + stored.Name;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var sc = new ServiceController(svcName);
                    if (sc.Status == ServiceControllerStatus.Running) break;
                }
                catch { }
                System.Threading.Thread.Sleep(250);
            }

            _log.Ok($"Connected: {stored.Name} (WireGuard)");
            _connectTimes[stored.Name] = DateTime.UtcNow;
            var sw0 = TunnelDll.GetTrafficStats(stored.Name);
            _connectBytes[stored.Name] = (sw0.RxBytes, sw0.TxBytes);
            if (_ks != null && ShouldKillSwitch(stored, cfg))
                _ks.Enable(stored.Name, null);
            return true;
        }

        /// <summary>
        /// Returns true when a <c>WireGuardTunnel$&lt;name&gt;</c> SCM service entry exists
        /// (regardless of running state). Used by the auto-reconnect poll to detect whether
        /// the WireGuard client removed the entry intentionally.
        /// </summary>
        public static bool WireGuardServiceExists(string serviceName)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine
                    .OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
                return key != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// Resolves the wireguard.exe path from config, logs a warning and returns null if absent.
        /// </summary>
        private string? GetWireGuardExe(AppConfig cfg)
        {
            var dir = cfg.WireGuardInstallDirectory?.TrimEnd('\\', '/') ?? @"C:\Program Files\WireGuard";
            var exe = Path.Combine(dir, "wireguard.exe");
            if (File.Exists(exe)) return exe;
            _log.Warn($"wireguard.exe not found at '{exe}'. Check the WireGuard install path in Settings → Advanced.");
            return null;
        }

        /// <summary>
        /// Runs <c>wireguard.exe</c> with the given arguments, waits up to 30 s, and returns
        /// the exit code plus any stderr output.
        /// </summary>
        private static (int exit, string stderr) RunWireGuard(string wgExe, string args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = wgExe,
                Arguments              = args,
                UseShellExecute        = false,
                RedirectStandardError  = true,
                RedirectStandardOutput = false,
                CreateNoWindow         = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30_000);
            return (proc.ExitCode, stderr.Trim());
        }

        // ── Disconnect ────────────────────────────────────────────────────────

        public bool Disconnect(StoredTunnel stored, AppConfig? cfg = null)
        {
            // Mark as intentional before the stop so RefreshStatus() doesn't trigger auto-reconnect.
            _intentionalDisconnects[stored.Name] = true;
            try
            {
                RunScript(stored.PreDisconnectScript, "pre-disconnect", stored.Name);

                bool storeTraffic = cfg?.StoreConnectionHistory != false;
                bool ok = stored.Source == "local"
                    ? DisconnectLocal(stored, storeTraffic)
                    : DisconnectWireGuard(stored, cfg, storeTraffic);

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

        private bool DisconnectLocal(StoredTunnel stored, bool storeTraffic)
        {
            try
            {
                // Snapshot bytes BEFORE the adapter is torn down — it disappears on disconnect
                var finalStats = TunnelDll.GetTrafficStats(stored.Name);
                TunnelDll.Disconnect(stored.Name, out string disconnErr);
                LogDisconnect(stored.Name, finalStats, storeTraffic);
                if (!string.IsNullOrEmpty(disconnErr)) _log.Warn(disconnErr);
                _ks?.Disable(stored.Name);
                return true;
            }
            catch (Exception ex) { _log.Warn($"TunnelDll.Disconnect failed: {ex.Message}"); return false; }
        }

        private bool DisconnectWireGuard(StoredTunnel stored, AppConfig? cfg, bool storeTraffic)
        {
            // Snapshot bytes before the tunnel goes down
            var finalStats = TunnelDll.GetTrafficStats(stored.Name);

            var wgExe = GetWireGuardExe(cfg ?? new AppConfig());
            if (wgExe != null)
            {
                _log.Debug($"wireguard.exe /uninstalltunnelservice \"{stored.Name}\"");
                var (exit, stderr) = RunWireGuard(wgExe, $"/uninstalltunnelservice \"{stored.Name}\"");
                if (exit != 0)
                    _log.Warn($"WireGuard /uninstalltunnelservice failed (exit {exit})" +
                              (string.IsNullOrEmpty(stderr) ? "" : $": {stderr}"));
            }

            LogDisconnect(stored.Name, finalStats, storeTraffic);
            _ks?.Disable(stored.Name);
            return true;
        }

        private void LogDisconnect(string name,
            TunnelDll.TunnelStats finalStats = default, bool storeTraffic = true,
            string logSuffix = "")
        {
            // Compute session byte delta unconditionally — used for history and extended log
            long sessionRx = 0, sessionTx = 0;
            if (finalStats.AdapterFound && _connectBytes.TryGetValue(name, out var startBytes))
            {
                sessionTx = Math.Max(0, finalStats.TxBytes - startBytes.tx);
                sessionRx = Math.Max(0, finalStats.RxBytes - startBytes.rx);
            }

            _history.RecordDisconnect(name,
                storeTraffic ? sessionRx : 0,
                storeTraffic ? sessionTx : 0);

            // Write continuation lines BEFORE the "Disconnected" message.
            // The log displays newest-at-top, so writing first = appears BELOW the disconnect.
            if (_log.IsExtended && _connectTimes.TryGetValue(name, out var connectedAt))
            {
                var now     = DateTime.UtcNow;
                var elapsed = now - connectedAt;
                string dur = elapsed.TotalSeconds < 60  ? $"{(int)elapsed.TotalSeconds}s"
                           : elapsed.TotalMinutes < 60  ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s"
                           : elapsed.TotalHours < 24    ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes:D2}m"
                           : $"{(int)elapsed.TotalDays}d {elapsed.Hours:D2}h {elapsed.Minutes:D2}m";

                // Line 2 (traffic) — logged first so it appears lowest
                if (finalStats.AdapterFound)
                    _log.Write(LogLevel.Debug,
                        $"Traffic: ↑ {FormatBytes(sessionTx)}  ↓ {FormatBytes(sessionRx)}",
                        isContinuation: true);

                // Line 1 (time) — logged second so it appears directly below disconnect
                _log.Write(LogLevel.Debug,
                    $"Time: {connectedAt.ToLocalTime():HH:mm:ss} → {now.ToLocalTime():HH:mm:ss}  |  {dur}",
                    isContinuation: true);
            }

            // Log disconnect last so it sits on top in newest-at-top view
            _log.Ok($"Disconnected: {name}{logSuffix}");

            _connectTimes.Remove(name);
            _connectBytes.Remove(name);
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

        public static string DecryptConfig(StoredTunnel stored)
        {
            // ── Primary source: .conf.dpapi file ──────────────────────────────
            // This is the canonical location for all new/migrated tunnels.
            // Check Path before Config so that even if a stale Config blob exists
            // (e.g. migration didn't null it out), the file always wins.
            if (!string.IsNullOrEmpty(stored.Path) && File.Exists(stored.Path))
            {
                if (stored.Path.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase))
                {
                    byte[]? raw = null;
                    try { raw = File.ReadAllBytes(stored.Path); } catch { return ""; }

                    // Try CurrentUser scope first (MasselGUARD's own tunnel storage).
                    try
                    {
                        var bytes = ProtectedData.Unprotect(raw, null,
                            DataProtectionScope.CurrentUser);
                        return StripBom(System.Text.Encoding.UTF8.GetString(bytes));
                    }
                    catch { }

                    // Try LocalMachine scope — WireGuard for Windows encrypts its own
                    // configs with LocalMachine DPAPI.  Succeeds when running as admin.
                    try
                    {
                        var bytes = ProtectedData.Unprotect(raw, null,
                            DataProtectionScope.LocalMachine);
                        return StripBom(System.Text.Encoding.UTF8.GetString(bytes));
                    }
                    catch { }

                    return "";
                }
                // Plain .conf file
                try
                {
                    return StripBom(File.ReadAllText(stored.Path,
                        new System.Text.UTF8Encoding(false)));
                }
                catch { return ""; }
            }

            // ── Legacy fallback: inline DPAPI blob in config.json ─────────────
            // Only reached when migration has not yet run or Config was not nulled.
            if (!string.IsNullOrEmpty(stored.Config))
            {
                try
                {
                    var bytes = ProtectedData.Unprotect(
                        Convert.FromBase64String(stored.Config), null,
                        DataProtectionScope.CurrentUser);
                    return StripBom(System.Text.Encoding.UTF8.GetString(bytes));
                }
                catch { }
                // Last resort: Config stored as raw plaintext by very old builds.
                return StripBom(stored.Config);
            }

            return "";
        }

        /// <summary>Removes a leading UTF-8 BOM (U+FEFF) if present.</summary>
        private static string StripBom(string s) =>
            s.Length > 0 && s[0] == '﻿' ? s.Substring(1) : s;

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)          return $"{bytes} B";
            if (bytes < 1_048_576)     return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1_073_741_824) return $"{bytes / 1_048_576.0:F1} MB";
            return                             $"{bytes / 1_073_741_824.0:F2} GB";
        }

        public static string EncryptConfig(string plaintext)
        {
            var bytes = ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(plaintext), null,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        /// <summary>
        /// DPAPI-encrypts <paramref name="plaintext"/> and writes it to
        /// TunnelStorageDir\{safeName}.conf.dpapi. Returns the full path.
        /// </summary>
        public static string SaveConfigToFile(string tunnelName, string plaintext)
        {
            Directory.CreateDirectory(TunnelStorageDir);
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var safeName = new string(tunnelName.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
            var path = System.IO.Path.Combine(TunnelStorageDir, safeName + ".conf.dpapi");
            var bytes = ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(plaintext), null,
                DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, bytes);
            return path;
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

            // Explicitly UTF-8 without BOM — WireGuard's config parser rejects a BOM.
            using var sw = new StreamWriter(
                new FileStream(path, FileMode.Open, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
