using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using MasselGUARD.Models;
using MasselGUARD.ViewModels;

namespace MasselGUARD.Services
{
    /// <summary>
    /// The Advanced → Diagnostics/Tester engine. Runs a live WireGuard connection test (connect an
    /// available local tunnel, confirm the peer handshake, then disconnect) and a DNS test that
    /// creates a throwaway test profile, applies it to the active interface, reads the resolvers
    /// back, and restores the previous settings. When both are selected it also tests a tunnel with
    /// a DNS profile applied on top. Every line is streamed to a sink so the UI shows a running log.
    ///
    /// GUI-side only (not in MasselGUARDcli.csproj): it drives the view-models and depends on
    /// <see cref="TunnelDll"/>/<see cref="DnsService"/>. Companion (WireGuard-for-Windows) mode is
    /// intentionally not tested.
    /// </summary>
    public sealed class DiagnosticsService
    {
        public enum Level { Info, Pass, Warn, Fail, Head }

        public delegate void Sink(Level level, string text);

        private readonly Sink _log;
        public DiagnosticsService(Sink log) => _log = log;

        private void Head(string t) => _log(Level.Head, t);
        private void Info(string t) => _log(Level.Info, t);
        private void Pass(string t) => _log(Level.Pass, t);
        private void Warn(string t) => _log(Level.Warn, t);
        private void Fail(string t) => _log(Level.Fail, t);

        /// <summary>Run the selected suites. Must be awaited on the UI thread (it drives the tunnel
        /// view-models); blocking work is offloaded internally so the UI stays responsive.</summary>
        public async Task RunAsync(bool testLocal, bool testDns, bool testCli,
                                   MainViewModel vm, TunnelService tunnels, DnsService dns,
                                   AppConfig cfg, Guid ifaceGuid, CancellationToken ct)
        {
            Environment_(cfg);

            if (testLocal)
            {
                await Task.Run(() => LocalReadiness(cfg), ct);
                await LiveTunnelTest(vm, tunnels, dns, cfg, ifaceGuid, withDns: testDns, ct);
            }

            if (testDns)
                await DnsTest(cfg, ifaceGuid, dns, ct);

            if (testCli)
                await CliTest(ct);

            Head("Done.");
        }

        // ── Command-line interface (MasselGUARDcli) ─────────────────────────────────
        /// <summary>Verifies the bundled CLI launches, reports its version, and passes its own
        /// built-in <c>selftest</c> (CIDR math, conf rewrite, export round-trip, DNS-policy
        /// precedence). Uses only non-elevated informational commands; the elevated GUI launches
        /// the child without a UAC prompt.</summary>
        private async Task CliTest(CancellationToken ct)
        {
            Head("Command-line interface (MasselGUARDcli)");

            string cli = System.IO.Path.Combine(TunnelDll.ExeDirPublic, "MasselGUARDcli.exe");
            if (!System.IO.File.Exists(cli))
            {
                Fail($"MasselGUARDcli.exe not found next to the app - {cli}");
                return;
            }
            Info($"CLI: {cli} ({new System.IO.FileInfo(cli).Length:N0} bytes)");

            // version - proves the exe launches and returns cleanly.
            var (vCode, vOut) = await RunCli(cli, "version", ct);
            if (vCode == 0 && vOut.Trim().Length > 0)
                Pass($"`version` OK - {FirstLine(vOut)}");
            else
                Fail($"`version` failed (exit {vCode}) - {FirstLine(vOut)}");

            // selftest - exercises the shared CIDR / conf-rewrite / export / DNS-policy logic.
            var (sCode, sOut) = await RunCli(cli, "selftest", ct);
            if (sCode == 0) Pass("`selftest` passed (exit 0).");
            else            Fail($"`selftest` FAILED (exit {sCode}).");
            foreach (var ln in sOut.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0))
                Info("  " + ln.TrimEnd());

            Info("Confirmation: the bundled CLI launches, reports its version, and passes its built-in self-test.");
        }

        /// <summary>Run a MasselGUARDcli command, capturing stdout+stderr and the exit code, with a
        /// 30 s watchdog. Returns (-1, message) on timeout or launch failure.</summary>
        private static async Task<(int code, string output)> RunCli(string exe, string args, CancellationToken ct)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                    // The CLI writes UTF-8 (✓/✗ glyphs); decode it as UTF-8 or they arrive as mojibake.
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding  = System.Text.Encoding.UTF8,
                };
                using var p = new System.Diagnostics.Process { StartInfo = psi };
                var sb = new System.Text.StringBuilder();
                p.OutputDataReceived += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                p.ErrorDataReceived  += (_, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
                to.CancelAfter(TimeSpan.FromSeconds(30));
                try { await p.WaitForExitAsync(to.Token); }
                catch (OperationCanceledException)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    return (-1, sb + Environment.NewLine + "(timed out after 30s)");
                }
                return (p.ExitCode, sb.ToString());
            }
            catch (Exception ex) { return (-1, ex.Message); }
        }

        private static string FirstLine(string s)
            => s.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "(no output)";

        // ── Environment ────────────────────────────────────────────────────────────
        private void Environment_(AppConfig cfg)
        {
            Head("Environment");
            Info($"MasselGUARD {UpdateChecker.VersionWithCodename}");
            Info($"Build stamp: {UpdateChecker.BuildStamp}");
            Info($"OS: {Environment.OSVersion.VersionString} (build {Environment.OSVersion.Version.Build})");
            Info($"Process arch: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}   " +
                 $"OS arch: {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
            if (IsElevated()) Pass("Running elevated (administrator).");
            else              Fail("NOT elevated - driver/service operations will fail. MasselGUARD normally auto-elevates.");
            Info($"Exe directory: {TunnelDll.ExeDirPublic}");
            if (IsCloudSyncedPath(TunnelDll.ExeDirPublic))
                Warn("Exe is in a cloud-synced (OneDrive) path - local tunnels run as LocalSystem and cannot read it. Move to e.g. C:\\MasselGUARD.");
            else
                Pass("Exe is in a local (non-cloud) path.");
            Info($"Local tunnels configured: {cfg.Tunnels.Count(IsLocal)}.   DNS profiles: {cfg.DnsProfiles.Count}.   " +
                 $"DNS automation: {(cfg.DnsAutomationEnabled ? "on" : "off")}.");
        }

        // ── Local WireGuard readiness (static) ───────────────────────────────────────
        private void LocalReadiness(AppConfig cfg)
        {
            Head("WireGuard client - readiness");

            var archErr = TunnelDll.ArchSupportError();
            if (archErr != null) { Fail($"Architecture: {archErr}"); return; }
            Pass("Architecture supported for local tunnels.");

            foreach (var (label, path) in new[] { ("tunnel.dll", TunnelDll.TunnelDllPath), ("wireguard.dll", TunnelDll.WireGuardDllPath) })
            {
                if (System.IO.File.Exists(path))
                    Info($"{label}: present ({new System.IO.FileInfo(path).Length:N0} bytes)");
                else Fail($"{label}: MISSING - {path}");
            }

            var dllErr = TunnelDll.ValidateDlls();
            if (dllErr == null) Pass("DLL validation passed (present + correct machine type).");
            else                Fail($"DLL validation failed: {dllErr}");

            try
            {
                var (priv, pub) = TunnelDll.GenerateKeypair();
                if (TryKey(priv) && TryKey(pub) && priv != pub) Pass("Curve25519 key generation self-test passed.");
                else                                            Fail("Curve25519 key generation self-test FAILED.");
            }
            catch (Exception ex) { Fail($"Crypto self-test threw: {ex.Message}"); }
        }

        // ── Live tunnel connection test ──────────────────────────────────────────────
        private async Task LiveTunnelTest(MainViewModel vm, TunnelService tunnels, DnsService? dns,
                                          AppConfig cfg, Guid guid, bool withDns, CancellationToken ct)
        {
            Head("WireGuard client - live connection test");

            var entry = vm.TunnelList.FirstOrDefault(t => t.IsLocal && t.IsAvailable)
                     ?? vm.TunnelList.FirstOrDefault(t => t.IsLocal);
            if (entry == null)
            {
                Warn("No tunnel available to test - add a tunnel first.");
                return;
            }
            Info($"Using tunnel: {entry.Name}");

            bool wasActive = entry.IsActive;
            if (wasActive)
                Info("Tunnel already connected - testing the live connection without reconnecting.");
            else
            {
                Info("Connecting…");
                try { await entry.ConnectAsync(); }   // ConnectAsync offloads the blocking P/Invoke itself
                catch (Exception ex) { Fail($"Connect threw: {ex.Message}"); return; }
            }

            // Poll up to ~12 s for the service to run and the peer to hand-shake.
            bool running = false; DateTime? hs = null; long rx = 0, tx = 0;
            for (int i = 0; i < 24 && !ct.IsCancellationRequested; i++)
            {
                var st = await Task.Run(() => { running = SafeIsRunning(entry.Name); return SafeStats(entry.Name); }, ct);
                hs = st.LastHandshakeUtc; rx = st.RxBytes; tx = st.TxBytes;
                if (running && hs != null) break;
                await Task.Delay(500, ct);
            }

            if (running) Pass("Tunnel service is running.");
            else         Fail("Tunnel did not reach the running state.");

            if (hs != null)
            {
                double age = (DateTime.UtcNow - hs.Value).TotalSeconds;
                if (age < 180) Pass($"Handshake OK ({(int)age}s ago) - the peer responded.  ↑{Bytes(tx)}  ↓{Bytes(rx)}");
                else           Warn($"Last handshake was {(int)(age / 60)}m ago - the connection may be stale.");
            }
            else
                Warn($"No handshake - the tunnel is up locally but the peer has not responded (check endpoint/keys/network).  ↑{Bytes(tx)}  ↓{Bytes(rx)}");

            // Optional: test the tunnel together with a DNS profile applied on top.
            if (withDns && dns != null && guid != Guid.Empty && running)
            {
                Head("Tunnel + DNS profile");
                var tp = TestProfile();
                Info($"Applying test DNS profile '{tp.Name}' while the tunnel is up…");
                await Task.Run(() => DnsApplyReadRestore(dns, guid, tp, cfg.DnsAddressFamilies), ct);
                Info("A manually-enabled DNS profile is designed to override the tunnel's own DNS - the round-trip above confirms it applied and restored.");
            }
            else if (withDns && guid == Guid.Empty)
                Warn("Tunnel + DNS: no active interface to apply the DNS profile to - skipped.");

            // Restore prior state: disconnect only if we connected it.
            if (!wasActive)
            {
                Info("Disconnecting (restoring previous state)…");
                bool stopped = await Task.Run(() =>
                {
                    entry.UserDisconnected = true;          // mark intentional so auto-reconnect stays off
                    tunnels.Disconnect(entry.StoredTunnel, cfg);
                    Thread.Sleep(400);
                    return !SafeIsRunning(entry.Name);
                }, ct);
                if (stopped) Pass("Disconnected cleanly.");
                else         Warn("Tunnel still appears to be running after disconnect - check its status.");
            }
            else
                Info("Left the tunnel connected (it was already up before the test).");

            Info("Confirmation: the connection works when the service runs AND a recent handshake is reported (the peer answered).");
        }

        // ── DNS test (single throwaway profile - not every configured profile) ───────
        private async Task DnsTest(AppConfig cfg, Guid guid, DnsService? dns, CancellationToken ct)
        {
            Head("DNS profile test");

            bool doh = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
            if (doh) Pass($"Encrypted DNS (DoH) supported (Windows build {Environment.OSVersion.Version.Build}).");
            else     Warn($"No per-interface DoH on this build ({Environment.OSVersion.Version.Build}) - encrypted profiles apply plain servers unless they require encryption.");

            Info($"{cfg.DnsProfiles.Count} DNS profile(s) configured (not applied - only a throwaway test profile is exercised).");

            if (guid == Guid.Empty) { Warn("No active network interface - DNS apply/restore skipped (nothing to target)."); return; }
            if (dns == null)        { Warn("DNS service unavailable - skipped."); return; }

            string alias = AliasFor(guid);
            Info($"Active interface: {alias} ({guid:B})");
            Info($"Current resolvers: {CurrentResolvers(guid)}");

            var tp = TestProfile();
            Info($"Test profile: {tp.Name}  →  {tp.ServersDisplay}");
            await Task.Run(() => DnsApplyReadRestore(dns, guid, tp, cfg.DnsAddressFamilies), ct);

            Info("Confirmation: the DNS pipeline works when the resolvers change to the test servers, then restore to the original values.");
        }

        /// <summary>Apply a profile, read resolvers back, verify the servers appear, then restore.
        /// Runs on a background thread (netsh is blocking). Logs each step via the sink.</summary>
        private void DnsApplyReadRestore(DnsService dns, Guid guid, DnsProfile p, string families)
        {
            string before = CurrentResolvers(guid);
            if (!dns.ApplyProfile(guid, p, families)) { Warn("  apply returned false (see activity log for the netsh error) - restoring."); dns.Restore(guid); return; }
            Thread.Sleep(500);
            string after = CurrentResolvers(guid);

            var expect = new[] { p.V4Primary, p.V6Primary }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray();
            bool applied = expect.Length == 0 || expect.Any(e => after.Contains(e, StringComparison.OrdinalIgnoreCase));
            if (applied) Pass($"  applied OK - resolvers now: {after}");
            else         Warn($"  read-back did not show {string.Join(", ", expect)} - got: {after} (a tunnel or automation may have re-asserted DNS).");

            dns.Restore(guid);
            Thread.Sleep(300);
            string restored = CurrentResolvers(guid);
            if (restored == before) Pass($"  restored to original: {restored}");
            else                    Warn($"  after restore: {restored} (was: {before}) - verify the interface returned to its previous setting.");
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────
        private static DnsProfile TestProfile() => new()
        {
            Name        = "MasselGUARD Test (Cloudflare 1.1.1.1)",
            V4Primary   = "1.1.1.1",
            V4Secondary = "1.0.0.1",
            Encryption  = "plain",
        };

        private static bool IsLocal(StoredTunnel t) => string.Equals(t.Source, "local", StringComparison.OrdinalIgnoreCase);

        private static bool TryKey(string b64)
        { try { return !string.IsNullOrWhiteSpace(b64) && Convert.FromBase64String(b64).Length == 32; } catch { return false; } }

        private static bool SafeIsRunning(string name)
        { try { return TunnelDll.IsRunning(name); } catch { return false; } }

        private static TunnelDll.TunnelStats SafeStats(string name)
        { try { return TunnelDll.GetStats(name); } catch { return default; } }

        private static string Bytes(long b)
        {
            if (b < 1024) return $"{b} B";
            if (b < 1024 * 1024) return $"{b / 1024.0:F1} KB";
            if (b < 1024L * 1024 * 1024) return $"{b / (1024.0 * 1024):F1} MB";
            return $"{b / (1024.0 * 1024 * 1024):F2} GB";
        }

        private static bool IsElevated()
        {
            try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        private static bool IsCloudSyncedPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string full = System.IO.Path.GetFullPath(path);
                foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
                {
                    var root = Environment.GetEnvironmentVariable(v);
                    if (!string.IsNullOrEmpty(root) && full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
                }
                return full.Split(System.IO.Path.DirectorySeparatorChar)
                           .Any(seg => seg.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        private static string AliasFor(Guid guid)
        {
            try
            {
                var id = guid.ToString("B");
                return NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase))?.Name ?? "(unknown)";
            }
            catch { return "(unknown)"; }
        }

        private static string CurrentResolvers(Guid guid)
        {
            try
            {
                var id = guid.ToString("B");
                var ni = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
                if (ni == null) return "(interface not found)";
                var addrs = ni.GetIPProperties().DnsAddresses.Select(a => a.ToString()).ToArray();
                return addrs.Length == 0 ? "(none)" : string.Join(", ", addrs);
            }
            catch (Exception ex) { return $"(read failed: {ex.Message})"; }
        }
    }
}
