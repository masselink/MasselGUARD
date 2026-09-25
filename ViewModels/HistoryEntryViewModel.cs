using System;
using MasselGUARD.Models;

namespace MasselGUARD.ViewModels
{
    /// <summary>
    /// Display wrapper for <see cref="ConnectionHistoryEntry"/> shown in the History tab.
    /// </summary>
    public class HistoryEntryViewModel
    {
        private readonly ConnectionHistoryEntry _entry;

        public HistoryEntryViewModel(ConnectionHistoryEntry entry) => _entry = entry;

        public string TunnelName => _entry.TunnelName;
        public string Source     => _entry.Source;

        /// <summary>
        /// Connected-at displayed as a friendly local-time string.
        /// Today's entries show time only; older entries also include date.
        /// </summary>
        public string WhenDisplay
        {
            get
            {
                var local = _entry.ConnectedAt.ToLocalTime();
                return local.Date == DateTime.Today
                    ? local.ToString("HH:mm:ss")
                    : local.ToString("dd MMM  HH:mm");
            }
        }

        /// <summary>
        /// Full date + time string always shown in the hover popup (never time-only).
        /// </summary>
        public string FullWhenDisplay =>
            _entry.ConnectedAt.ToLocalTime().ToString("dd MMM yyyy  HH:mm:ss");

        /// <summary>
        /// Session duration. Shows "active" when the tunnel is still connected,
        /// "–" when disconnected without a clean record, and formatted elapsed
        /// time otherwise.
        /// </summary>
        public string DurationDisplay
        {
            get
            {
                if (_entry.DisconnectedAt == null)
                    return "active";

                var span = _entry.DisconnectedAt.Value - _entry.ConnectedAt;
                if (span.TotalSeconds < 60)
                    return $"{(int)span.TotalSeconds}s";
                if (span.TotalMinutes < 60)
                    return $"{(int)span.TotalMinutes}m {span.Seconds:D2}s";
                if (span.TotalHours < 24)
                    return $"{(int)span.TotalHours}h {span.Minutes:D2}m";
                return $"{(int)span.TotalDays}d {span.Hours:D2}h";
            }
        }

        /// <summary>
        /// "HH:mm – HH:mm" (same day) or "dd MMM HH:mm – HH:mm" (cross-day).
        /// Shows "HH:mm – active" for sessions still open.
        /// </summary>
        public string TimeRangeDisplay
        {
            get
            {
                var connLocal = _entry.ConnectedAt.ToLocalTime();
                if (_entry.DisconnectedAt == null)
                    return $"{connLocal:HH:mm} – active";
                var discLocal = _entry.DisconnectedAt.Value.ToLocalTime();
                return connLocal.Date == discLocal.Date
                    ? $"{connLocal:HH:mm} – {discLocal:HH:mm}"
                    : $"{connLocal:dd MMM HH:mm} – {discLocal:dd MMM HH:mm}";
            }
        }

        /// <summary>Session traffic totals. "-" when not recorded.</summary>
        public string TrafficDisplay
        {
            get
            {
                if (_entry.SessionRxBytes == 0 && _entry.SessionTxBytes == 0) return "-";
                return $"↑ {FormatBytes(_entry.SessionTxBytes)}  ↓ {FormatBytes(_entry.SessionRxBytes)}";
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)              return $"{bytes} B";
            if (bytes < 1024 * 1024)       return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024*1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
