using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Win32;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Applies / reverts a DNS resolver on a physical network interface (see
    /// <c>docs/DnsAutomation-Design.md</c> §5). The pure precedence that decides *which*
    /// resolver lives in <see cref="DnsPolicy"/>; this is the side-effecting half.
    ///
    /// Step 3 uses <c>netsh</c> for plain (Do53) IPv4/IPv6 - reliable across Win10/11 and
    /// trivial to revert to DHCP. DoH (<c>SetInterfaceDnsSettings</c> / <c>netsh dns add
    /// encryption</c>) arrives in step 6; an encrypted profile currently applies its plain
    /// servers and logs that encryption is pending.
    ///
    /// Before the first override of an interface, the interface's current static/DHCP DNS is
    /// snapshotted to <c>%APPDATA%\MasselGUARD\dns_state.json</c> (keyed by adapter GUID) so it
    /// can be restored exactly - on leaving the network, on app exit, and on the next launch
    /// after a crash (see <see cref="Restore"/> / <see cref="RestoreAll"/>). GUI-side only
    /// (not in <c>MasselGUARDcli.csproj</c>); the app is always elevated so the writes succeed.
    /// </summary>
    public sealed class DnsService
    {
        /// <summary>Captured pre-override DNS for one interface. A null static list = the
        /// family was DHCP-managed (restore = back to DHCP); a value = restore that static list.</summary>
        private sealed class DnsSnapshot
        {
            public string? V4Static    { get; set; }
            public string? V6Static    { get; set; }
            public string  Alias       { get; set; } = "";
            public DateTime CapturedUtc { get; set; }
        }

        private static readonly string StatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MasselGUARD", "dns_state.json");

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        private readonly LogService? _log;
        private readonly object _lock = new();
        private Dictionary<string, DnsSnapshot> _state;   // key = GUID "B" form, upper-cased

        public DnsService(LogService? log = null)
        {
            _log   = log;
            _state = Load();
        }

        // ── Public API ───────────────────────────────────────────────────────────

        /// <summary>True when this interface currently has a MasselGUARD DNS override on record.</summary>
        public bool HasOverride(Guid interfaceGuid)
        {
            lock (_lock) return _state.ContainsKey(Key(interfaceGuid));
        }

        /// <summary>Number of interfaces currently recorded as overridden (0 = nothing to restore).</summary>
        public int OverrideCount
        {
            get { lock (_lock) return _state.Count; }
        }

        /// <summary>Apply a profile to the interface (families = "both"|"v4"|"v6").
        /// Encrypted profiles (DoH) register auto-upgrade templates for their server IPs, gated on
        /// Windows-11 DoH support; a profile that <see cref="DnsProfile.RequireEncryption"/> on a
        /// build without DoH support is refused (fail-closed) rather than applied in the clear.
        /// Snapshots the original DNS first (once). Returns true on success.</summary>
        public bool ApplyProfile(Guid interfaceGuid, DnsProfile profile, string addressFamilies)
        {
            var alias = AliasFor(interfaceGuid);
            if (alias == null) { _log?.Warn($"DNS: no active interface for {interfaceGuid}"); return false; }

            bool wantDoh = profile.IsEncrypted;
            if (wantDoh && !IsDohSupported())
            {
                if (profile.RequireEncryption)
                {
                    _log?.Warn($"DNS: '{profile.Name}' requires encrypted DNS, but Windows build {OsBuild} has no per-interface DoH - not applied (fail-closed).");
                    return false;
                }
                _log?.Info($"DNS: '{profile.Name}' requested DoH; unsupported on build {OsBuild} - applying plain servers.");
                wantDoh = false;
            }

            bool v4 = addressFamilies != "v6";
            bool v6 = addressFamilies != "v4";
            lock (_lock)
            {
                Snapshot(interfaceGuid, alias);

                if (wantDoh)
                {
                    // Register the DoH template for each server IP (required for custom resolvers;
                    // harmless for well-known ones Windows already knows). autoupgrade=yes makes a
                    // plain `set dnsservers` resolve over DoH; udpfallback=no = fail-closed.
                    RegisterDoh(profile.V4Primary,   profile.DohTemplate, profile.RequireEncryption);
                    RegisterDoh(profile.V4Secondary, profile.DohTemplate, profile.RequireEncryption);
                    RegisterDoh(profile.V6Primary,   profile.DohTemplate, profile.RequireEncryption);
                    RegisterDoh(profile.V6Secondary, profile.DohTemplate, profile.RequireEncryption);
                }

                bool ok = true;
                if (v4) ok &= SetStatic(alias, "ipv4", profile.V4Primary, profile.V4Secondary);
                if (v6) ok &= SetStatic(alias, "ipv6", profile.V6Primary, profile.V6Secondary);
                Flush();
                _log?.Info($"DNS: applied '{profile.Name}' to {alias} ({addressFamilies}{(wantDoh ? ", DoH" : "")}).");
                return ok;
            }
        }

        /// <summary>Revert the interface to DHCP-provided DNS (the "Automatic" action).
        /// Snapshots the original DNS first (once), so a later <see cref="Restore"/> still works.</summary>
        public bool SetAutomatic(Guid interfaceGuid, string addressFamilies)
        {
            var alias = AliasFor(interfaceGuid);
            if (alias == null) { _log?.Warn($"DNS: no active interface for {interfaceGuid}"); return false; }

            bool v4 = addressFamilies != "v6";
            bool v6 = addressFamilies != "v4";
            lock (_lock)
            {
                Snapshot(interfaceGuid, alias);
                bool ok = true;
                if (v4) ok &= SetDhcp(alias, "ipv4");
                if (v6) ok &= SetDhcp(alias, "ipv6");
                Flush();
                _log?.Info($"DNS: set {alias} to automatic (DHCP).");
                return ok;
            }
        }

        /// <summary>Restore the interface to its captured pre-override settings and forget it.
        /// A no-op (returns true) when nothing was overridden.</summary>
        public bool Restore(Guid interfaceGuid)
        {
            lock (_lock)
            {
                var key = Key(interfaceGuid);
                if (!_state.TryGetValue(key, out var snap)) return true;

                var alias = AliasFor(interfaceGuid) ?? snap.Alias;
                bool ok = RestoreFamily(alias, "ipv4", snap.V4Static)
                        & RestoreFamily(alias, "ipv6", snap.V6Static);
                _state.Remove(key);
                Save();
                Flush();
                _log?.Info($"DNS: restored {alias} to its previous settings.");
                return ok;
            }
        }

        /// <summary>Restore every interface recorded in <c>dns_state.json</c>. Call on app exit
        /// and once at startup (crash recovery - a prior run may have left an override).</summary>
        public void RestoreAll()
        {
            List<string> keys;
            lock (_lock) keys = _state.Keys.ToList();
            foreach (var k in keys)
                if (Guid.TryParse(k, out var g)) Restore(g);
        }

        // ── Snapshot ───────────────────────────────────────────────────────────────

        private void Snapshot(Guid guid, string alias)
        {
            var key = Key(guid);
            if (_state.ContainsKey(key)) return;   // keep the ORIGINAL - never overwrite with our own override
            _state[key] = new DnsSnapshot
            {
                V4Static    = ReadStaticNameServer(guid, ipv6: false),
                V6Static    = ReadStaticNameServer(guid, ipv6: true),
                Alias       = alias,
                CapturedUtc = DateTime.UtcNow,
            };
            Save();
        }

        private bool RestoreFamily(string alias, string family, string? staticList)
        {
            if (string.IsNullOrWhiteSpace(staticList)) return SetDhcp(alias, family);
            var parts = staticList.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return SetStatic(alias, family,
                parts.ElementAtOrDefault(0) ?? "", parts.ElementAtOrDefault(1) ?? "");
        }

        /// <summary>Reads the interface's *static* DNS from the registry. Empty/absent = the
        /// family is DHCP-managed → returns null.</summary>
        private static string? ReadStaticNameServer(Guid guid, bool ipv6)
        {
            try
            {
                string baseKey = ipv6
                    ? @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\"
                    : @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
                using var k = Registry.LocalMachine.OpenSubKey(baseKey + guid.ToString("B"));
                var ns = k?.GetValue("NameServer") as string;
                return string.IsNullOrWhiteSpace(ns) ? null : ns;
            }
            catch { return null; }
        }

        // ── netsh operations ─────────────────────────────────────────────────────

        private bool SetStatic(string alias, string family, string primary, string secondary)
        {
            if (string.IsNullOrWhiteSpace(primary))
                return SetDhcp(alias, family);   // no server for this family → leave it on DHCP

            bool ok = RunNetsh("interface", family, "set", "dnsservers",
                               $"name={alias}", "static", primary.Trim(), "primary", "validate=no");
            if (ok && !string.IsNullOrWhiteSpace(secondary))
                RunNetsh("interface", family, "add", "dnsservers",
                         $"name={alias}", secondary.Trim(), "index=2", "validate=no");
            return ok;
        }

        private bool SetDhcp(string alias, string family)
            => RunNetsh("interface", family, "set", "dnsservers", $"name={alias}", "dhcp");

        // ── DoH (DNS-over-HTTPS) ───────────────────────────────────────────────────

        /// <summary>Per-interface DoH is a Windows 11 (build 22000+) client feature; Windows 10
        /// has no native DoH client.</summary>
        private static bool IsDohSupported() => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

        private static int OsBuild => Environment.OSVersion.Version.Build;

        /// <summary>Registers (machine-wide) a DoH URI template for a server IP so Windows resolves
        /// it over HTTPS. Re-registers cleanly so the strictness (udpfallback) reflects the current
        /// profile. No-op without both an IP and a template (well-known IPs carry built-in templates).</summary>
        private void RegisterDoh(string ip, string template, bool strict)
        {
            if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(template)) return;
            ip = ip.Trim();
            RunNetsh(allowFailure: true, "dns", "delete", "encryption", $"server={ip}");   // may not exist
            RunNetsh("dns", "add", "encryption", $"server={ip}",
                     $"dohtemplate={template.Trim()}", "autoupgrade=yes",
                     $"udpfallback={(strict ? "no" : "yes")}");
        }

        private bool RunNetsh(params string[] args) => RunNetsh(false, args);

        private bool RunNetsh(bool allowFailure, params string[] args)
        {
            try
            {
                // Full System32 path so an elevated process can't pick up a planted netsh.exe.
                var netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
                if (!File.Exists(netsh)) { _log?.Warn("DNS: netsh.exe not found."); return false; }

                var psi = new ProcessStartInfo
                {
                    FileName               = netsh,
                    CreateNoWindow         = true,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    WindowStyle            = ProcessWindowStyle.Hidden,
                };
                foreach (var a in args) psi.ArgumentList.Add(a);   // ArgumentList quotes spaces (aliases) for us

                using var p = Process.Start(psi);
                if (p == null) return false;
                string err = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } _log?.Warn("DNS: netsh timed out."); return false; }

                if (p.ExitCode != 0)
                {
                    if (!allowFailure)
                        _log?.Warn($"DNS: netsh {string.Join(' ', args)} → exit {p.ExitCode} {err.Trim()}");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                _log?.Warn($"DNS: netsh failed - {ex.Message}");
                return false;
            }
        }

        /// <summary>Best-effort DNS cache flush so already-resolved (old-resolver) entries drop.</summary>
        private void Flush()
        {
            try
            {
                var ipconfig = Path.Combine(Environment.SystemDirectory, "ipconfig.exe");
                if (!File.Exists(ipconfig)) return;
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName               = ipconfig,
                    Arguments              = "/flushdns",
                    CreateNoWindow         = true,
                    UseShellExecute        = false,
                    WindowStyle            = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                });
                p?.WaitForExit(3000);
            }
            catch { /* flushing is a nicety; the DNS change is what matters */ }
        }

        // ── Interface + state helpers ──────────────────────────────────────────────

        private static string Key(Guid g) => g.ToString("B").ToUpperInvariant();

        /// <summary>Maps an adapter GUID to its friendly alias (what netsh's name= wants).</summary>
        private static string? AliasFor(Guid guid)
        {
            var id = guid.ToString("B");
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
            return ni?.Name;
        }

        private static Dictionary<string, DnsSnapshot> Load()
        {
            try
            {
                if (!File.Exists(StatePath)) return new();
                var json = File.ReadAllText(StatePath);
                return JsonSerializer.Deserialize<Dictionary<string, DnsSnapshot>>(json) ?? new();
            }
            catch { return new(); }
        }

        private void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(StatePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (_state.Count == 0) { if (File.Exists(StatePath)) File.Delete(StatePath); return; }
                File.WriteAllText(StatePath, JsonSerializer.Serialize(_state, JsonOpts));
            }
            catch (Exception ex) { _log?.Warn($"DNS: could not persist state - {ex.Message}"); }
        }
    }
}
