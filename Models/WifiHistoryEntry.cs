using System;
using System.Text.Json.Serialization;

namespace MasselGUARD.Models
{
    /// <summary>
    /// One network connection recorded for the activity chart: a Wi-Fi SSID, or (since wired support)
    /// a wired network. The chart shows the PRIMARY network only, one segment at a time, so entries
    /// never overlap. <see cref="Ssid"/> stays the display label for both kinds (older builds and the
    /// chart colour map read it): the SSID for Wi-Fi, the DNS suffix (else the adapter name) for wired.
    /// </summary>
    public class WifiHistoryEntry
    {
        /// <summary>Display label of the network: the SSID for Wi-Fi; for wired the DNS suffix,
        /// or the adapter name when the network hands out none.</summary>
        public string    Ssid           { get; set; } = "";

        /// <summary>UTC moment the device connected to this network.</summary>
        public DateTime  ConnectedAt    { get; set; }

        /// <summary>UTC moment the device left this network. Null = still connected.</summary>
        public DateTime? DisconnectedAt { get; set; }

        /// <summary>True when the network has no security (open/unencrypted). Wi-Fi only.</summary>
        public bool IsOpen { get; set; }

        /// <summary>"wifi" (also for entries saved before wired support) | "wired".</summary>
        public string Kind { get; set; } = NetworkIdentity.KindWifi;

        /// <summary>Adapter name for wired entries (e.g. "Ethernet 2"); empty for Wi-Fi.</summary>
        public string AdapterName { get; set; } = "";

        /// <summary>DNS suffix the network handed out, when known (wired entries).</summary>
        public string? DnsSuffix { get; set; }

        /// <summary>Gateway MAC when it was known at connect time (wired entries).</summary>
        public string? GatewayMac { get; set; }

        [JsonIgnore]
        public bool IsWired => string.Equals(Kind, NetworkIdentity.KindWired, StringComparison.OrdinalIgnoreCase);
    }
}
