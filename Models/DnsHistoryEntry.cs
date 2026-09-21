using System;

namespace MasselGUARD.Models
{
    /// <summary>
    /// One interval during which a DNS profile was the active resolver (drawn as a band in the
    /// timeline / data-usage charts). Recorded to <c>dns_history.json</c>. WPF-free (CLI-shared
    /// via the Models glob). Mirrors <see cref="WifiHistoryEntry"/>.
    /// </summary>
    public class DnsHistoryEntry
    {
        /// <summary>Display name of the DNS profile that was active.</summary>
        public string    Name           { get; set; } = "";
        public DateTime  ConnectedAt    { get; set; }
        /// <summary>Null = still active.</summary>
        public DateTime? DisconnectedAt { get; set; }
    }
}
