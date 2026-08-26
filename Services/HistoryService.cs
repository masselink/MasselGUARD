using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Persists a rolling log of tunnel connect/disconnect events.
    /// Thread-safe for reads; writes are serialised via lock.
    /// </summary>
    public class HistoryService
    {
        private const int MaxEntries = 500;

        private static readonly string HistoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MasselGUARD", "tunnel_history.json");

        // Legacy path — migrated on first Load()
        private static readonly string HistoryPathLegacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MasselGUARD", "history.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        private readonly object _lock = new();
        private List<ConnectionHistoryEntry> _entries = new();

        // ── Public read access ────────────────────────────────────────────────

        /// <summary>Snapshot of all history entries, newest first.</summary>
        public IReadOnlyList<ConnectionHistoryEntry> Entries
        {
            get { lock (_lock) { return _entries.AsReadOnly(); } }
        }

        // ── Load / Save ───────────────────────────────────────────────────────

        public void Load()
        {
            // Migrate legacy history.json → tunnel_history.json on first run
            if (!File.Exists(HistoryPath) && File.Exists(HistoryPathLegacy))
            {
                try { File.Move(HistoryPathLegacy, HistoryPath); } catch { }
            }

            if (!File.Exists(HistoryPath)) return;
            try
            {
                var list = JsonSerializer.Deserialize<List<ConnectionHistoryEntry>>(
                    File.ReadAllText(HistoryPath), JsonOpts);
                if (list != null)
                    lock (_lock) { _entries = list; }
            }
            catch { /* corrupt file — start fresh */ }
        }

        public void Save()
        {
            List<ConnectionHistoryEntry> snapshot;
            lock (_lock) { snapshot = new List<ConnectionHistoryEntry>(_entries); }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
                File.WriteAllText(HistoryPath,
                    JsonSerializer.Serialize(snapshot, JsonOpts));
            }
            catch { /* non-critical */ }
        }

        public void Clear()
        {
            lock (_lock) { _entries.Clear(); }
            Save();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  WiFi SSID history — separate file, same service
        // ══════════════════════════════════════════════════════════════════════

        private static readonly string SsidHistoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MasselGUARD", "wifi_history.json");

        // Legacy path — migrated on first LoadSsid()
        private static readonly string SsidHistoryPathLegacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MasselGUARD", "ssid_history.json");

        private const int MaxSsidEntries = 500;

        private readonly object _ssidLock = new();
        private List<MasselGUARD.Models.WifiHistoryEntry> _ssidEntries = new();

        public IReadOnlyList<MasselGUARD.Models.WifiHistoryEntry> SsidEntries
        {
            get { lock (_ssidLock) { return _ssidEntries.AsReadOnly(); } }
        }

        public void LoadSsid()
        {
            // Migrate legacy ssid_history.json → wifi_history.json on first run
            if (!File.Exists(SsidHistoryPath) && File.Exists(SsidHistoryPathLegacy))
            {
                try { File.Move(SsidHistoryPathLegacy, SsidHistoryPath); } catch { }
            }

            if (!File.Exists(SsidHistoryPath)) return;
            try
            {
                var list = System.Text.Json.JsonSerializer.Deserialize<
                    List<MasselGUARD.Models.WifiHistoryEntry>>(
                    File.ReadAllText(SsidHistoryPath), JsonOpts);
                if (list != null)
                    lock (_ssidLock) { _ssidEntries = list; }
            }
            catch { }
        }

        public void SaveSsid()
        {
            List<MasselGUARD.Models.WifiHistoryEntry> snap;
            lock (_ssidLock) { snap = new List<MasselGUARD.Models.WifiHistoryEntry>(_ssidEntries); }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SsidHistoryPath)!);
                File.WriteAllText(SsidHistoryPath,
                    System.Text.Json.JsonSerializer.Serialize(snap, JsonOpts));
            }
            catch { }
        }

        /// <summary>Record connection to a new SSID (closes any previously-open entry first).</summary>
        public void RecordSsidConnect(string ssid, bool isOpen = false)
        {
            if (string.IsNullOrWhiteSpace(ssid)) return;
            lock (_ssidLock)
            {
                // Already recording this exact SSID — skip duplicate
                if (_ssidEntries.Any(e => e.DisconnectedAt == null &&
                        e.Ssid.Equals(ssid, StringComparison.OrdinalIgnoreCase)))
                    return;

                // Close any other open entry
                foreach (var e in _ssidEntries.Where(e => e.DisconnectedAt == null))
                    e.DisconnectedAt = DateTime.UtcNow;

                _ssidEntries.Insert(0, new MasselGUARD.Models.WifiHistoryEntry
                {
                    Ssid        = ssid,
                    ConnectedAt = DateTime.UtcNow,
                    IsOpen      = isOpen,
                });

                if (_ssidEntries.Count > MaxSsidEntries)
                    _ssidEntries.RemoveRange(MaxSsidEntries, _ssidEntries.Count - MaxSsidEntries);
            }
            System.Threading.ThreadPool.QueueUserWorkItem(_ => SaveSsid());
        }

        /// <summary>Close the current open SSID entry (WiFi disconnected or SSID changed).</summary>
        public void RecordSsidDisconnect()
        {
            bool changed = false;
            lock (_ssidLock)
            {
                foreach (var e in _ssidEntries.Where(e => e.DisconnectedAt == null))
                { e.DisconnectedAt = DateTime.UtcNow; changed = true; }
            }
            if (changed)
                System.Threading.ThreadPool.QueueUserWorkItem(_ => SaveSsid());
        }

        // ── Data-usage aggregation ────────────────────────────────────────────

        /// <summary>
        /// Sums session Rx+Tx bytes for a tunnel within the calendar month of
        /// <paramref name="monthUtc"/> (UTC). Only cleanly-closed sessions carry byte
        /// totals; the in-progress session is added live by the caller. Pass a null/empty
        /// <paramref name="tunnelName"/> to sum across all tunnels.
        /// </summary>
        public (long rx, long tx) GetMonthlyUsage(string? tunnelName, DateTime monthUtc)
        {
            var start = new DateTime(monthUtc.Year, monthUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            return GetUsageInRange(tunnelName, start, start.AddMonths(1));
        }

        /// <summary>
        /// Sum the closed-session Rx/Tx bytes for a tunnel (or all tunnels when
        /// <paramref name="tunnelName"/> is null/empty) whose ConnectedAt falls in
        /// the half-open UTC range [startUtc, endUtc). Used for day / week / month
        /// data-usage accounting.
        /// </summary>
        public (long rx, long tx) GetUsageInRange(string? tunnelName, DateTime startUtc, DateTime endUtc)
        {
            long rx = 0, tx = 0;
            lock (_lock)
            {
                foreach (var e in _entries)
                {
                    if (e.ConnectedAt < startUtc || e.ConnectedAt >= endUtc) continue;
                    if (!string.IsNullOrEmpty(tunnelName) &&
                        !e.TunnelName.Equals(tunnelName, StringComparison.OrdinalIgnoreCase)) continue;
                    rx += e.SessionRxBytes;
                    tx += e.SessionTxBytes;
                }
            }
            return (rx, tx);
        }

        // ── Record events ─────────────────────────────────────────────────────

        /// <summary>Called when a tunnel successfully connects.</summary>
        public void RecordConnect(string tunnelName, string source)
        {
            var entry = new ConnectionHistoryEntry
            {
                TunnelName  = tunnelName,
                ConnectedAt = DateTime.UtcNow,
                Source      = source,
            };

            lock (_lock)
            {
                // Remove any dangling open session for this tunnel
                _entries.RemoveAll(e =>
                    e.TunnelName.Equals(tunnelName, StringComparison.OrdinalIgnoreCase)
                    && e.DisconnectedAt == null);

                _entries.Insert(0, entry);

                // Prune oldest entries beyond the cap
                if (_entries.Count > MaxEntries)
                    _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            }
            // Save asynchronously on a threadpool thread — don't block the UI thread.
            System.Threading.ThreadPool.QueueUserWorkItem(_ => Save());
        }

        /// <summary>
        /// Called at startup: closes any open history entries whose tunnel is no longer
        /// actually running. Covers tunnels that were active when the app crashed or was
        /// force-killed, and tunnels that have since been deleted from the config.
        /// <para>
        /// <paramref name="isRunning"/> is a delegate that returns true when a named tunnel
        /// is currently active — checked against the live SCM/kernel state, not config.
        /// </para>
        /// </summary>
        public void CloseStaleHistoryEntries(Func<string, bool> isRunning)
        {
            bool changed = false;
            lock (_lock)
            {
                foreach (var e in _entries.Where(e => e.DisconnectedAt == null).ToList())
                {
                    if (!isRunning(e.TunnelName))
                    {
                        e.DisconnectedAt = DateTime.UtcNow;
                        changed = true;
                    }
                }
            }
            if (changed)
                System.Threading.ThreadPool.QueueUserWorkItem(_ => Save());
        }

        /// <summary>Called when a tunnel disconnects (clean disconnect).</summary>
        public void RecordDisconnect(string tunnelName, long sessionRxBytes = 0, long sessionTxBytes = 0)
        {
            lock (_lock)
            {
                var entry = _entries.FirstOrDefault(e =>
                    e.TunnelName.Equals(tunnelName, StringComparison.OrdinalIgnoreCase)
                    && e.DisconnectedAt == null);
                if (entry != null)
                {
                    entry.DisconnectedAt  = DateTime.UtcNow;
                    entry.SessionRxBytes  = sessionRxBytes;
                    entry.SessionTxBytes  = sessionTxBytes;
                }
            }
            System.Threading.ThreadPool.QueueUserWorkItem(_ => Save());
        }
    }
}
