using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Toggles the two machine-wide DNS Client Group Policy values that cause DNS
    /// leaks on a WireGuard SPLIT tunnel:
    ///   HKLM\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient
    ///     DisableSmartNameResolution = 1  -> stop "smart multi-homed name resolution"
    ///     DisableParallelAandAAAA    = 1  -> stop parallel A (IPv4) + AAAA (IPv6) queries
    ///
    /// Smart name resolution fires every DNS query out ALL active adapters in parallel
    /// and takes the first usable answer; parallel A/AAAA additionally fans the IPv4 and
    /// IPv6 lookups out at once. On a split tunnel both leak internet DNS to the local
    /// Wi-Fi/router resolver instead of the tunnel resolver, so filtering/ad-blocking on
    /// the tunnel DNS stops working. Disabling them keeps DNS pinned to the tunnel
    /// resolver (NRPT / interface order) while LAN access is unaffected. They are usually
    /// set together for full leak prevention.
    ///
    /// These are GLOBAL (all-users) settings, not per-tunnel. Writing under HKLM needs
    /// administrator rights — the GUI always runs elevated (requireAdministrator
    /// manifest), so no extra elevation is required here.
    ///
    /// The DNS Client (Dnscache) service reads these policies and generally cannot be
    /// restarted on demand on Win10/11, so changes apply most reliably after a reboot;
    /// reconnecting the tunnel is usually enough in practice. Each change does a
    /// best-effort `ipconfig /flushdns` to clear already-cached (leaked) entries.
    /// </summary>
    public static class DnsLeakService
    {
        private const string KeyPath           = @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient";
        private const string SmartNameValue    = "DisableSmartNameResolution";
        private const string ParallelAAAAValue = "DisableParallelAandAAAA";

        // ── Smart multi-homed name resolution ──────────────────────────────────
        public static bool IsSmartNameResolutionDisabled() => IsPolicySet(SmartNameValue);
        public static void DisableSmartNameResolution()     => SetPolicy(SmartNameValue);
        public static void EnableSmartNameResolution()      => ClearPolicy(SmartNameValue);

        // ── Parallel A (IPv4) + AAAA (IPv6) queries ────────────────────────────
        public static bool IsParallelQueriesDisabled() => IsPolicySet(ParallelAAAAValue);
        public static void DisableParallelQueries()     => SetPolicy(ParallelAAAAValue);
        public static void EnableParallelQueries()      => ClearPolicy(ParallelAAAAValue);

        // ── Registry core ──────────────────────────────────────────────────────
        private static bool IsPolicySet(string valueName)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
                return key?.GetValue(valueName) is int v && v == 1;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Sets the policy value to 1 (feature disabled).</summary>
        private static void SetPolicy(string valueName)
        {
            using (var key = Registry.LocalMachine.CreateSubKey(KeyPath))
            {
                key?.SetValue(valueName, 1, RegistryValueKind.DWord);
            }
            FlushDns();
        }

        /// <summary>Removes the policy value, restoring the Windows default (feature enabled).</summary>
        private static void ClearPolicy(string valueName)
        {
            using (var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true))
            {
                key?.DeleteValue(valueName, throwOnMissingValue: false);
            }
            FlushDns();
        }

        /// <summary>Best-effort DNS cache flush so already-leaked entries are dropped.</summary>
        private static void FlushDns()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName               = "ipconfig",
                    Arguments              = "/flushdns",
                    CreateNoWindow         = true,
                    UseShellExecute        = false,
                    WindowStyle            = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                });
                p?.WaitForExit(3000);
            }
            catch
            {
                // Flushing is a nicety; the policy change is what matters.
            }
        }
    }
}
