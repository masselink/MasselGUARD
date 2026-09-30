using System;
using System.Collections.Generic;
using System.Linq;

namespace MasselGUARD.Models
{
    /// <summary>String values for a network rule's "Match by" (string enums, like the rest of the config).</summary>
    public static class NetworkMatchBy
    {
        public const string Ssid       = "ssid";
        public const string DnsSuffix  = "dnssuffix";
        public const string GatewayMac = "gatewaymac";
        public const string Subnet     = "subnet";

        public static bool IsKnown(string? v) =>
            v is Ssid or DnsSuffix or GatewayMac or Subnet;
    }

    /// <summary>String values for <c>AppConfig.PrimaryNetworkMode</c>.</summary>
    public static class PrimaryNetworkModes
    {
        /// <summary>The adapter with the best default route wins (what Windows actually uses).</summary>
        public const string Windows = "windows";
        /// <summary>A connected wired adapter always wins over Wi-Fi.</summary>
        public const string Wired   = "wired";
        /// <summary>A connected Wi-Fi adapter always wins over wired.</summary>
        public const string Wifi    = "wifi";
    }

    /// <summary>
    /// Everything we can learn about one connected adapter without an outbound request
    /// (see docs/NetworkIdentity-Design.md, section 2.1). WPF-free; shared with the CLI.
    /// </summary>
    public record NetworkIdentity(
        string AdapterId,                 // NetworkInterface.Id ("{GUID}"): key for per-adapter DNS
        string AdapterName,               // display only ("Ethernet 2", "Wi-Fi")
        string Kind,                      // "wifi" | "wired"
        string? Ssid,                     // wifi only
        bool IsOpen,                      // wifi only; always false for wired
        string? DnsSuffix,                // connection-specific suffix, lower-cased ("corp.example.com")
        string? GatewayMac,               // normalised "aa:bb:cc:dd:ee:ff"; null until resolved
        string? Gateway,                  // default gateway IP, informational
        IReadOnlyList<string> Subnets,    // CIDRs in network form, e.g. "10.20.4.0/24"
        string? DhcpServer,               // informational
        int RouteMetric,                  // default-route metric; int.MaxValue = no default route
        bool IsPrimary)
    {
        public const string KindWifi  = "wifi";
        public const string KindWired = "wired";

        public bool IsWifi  => Kind == KindWifi;
        public bool IsWired => Kind == KindWired;

        /// <summary>Stable one-line summary of every identity field, used to detect that a new
        /// snapshot is identical to the previous one (no action, no log noise).</summary>
        public string Fingerprint() => string.Join("|",
            AdapterId, Kind, Ssid ?? "", IsOpen ? "1" : "0", DnsSuffix ?? "", GatewayMac ?? "",
            Gateway ?? "", string.Join(",", Subnets), IsPrimary ? "P" : "-");
    }

    /// <summary>The connected physical adapters at one moment, with the primary one flagged.</summary>
    public record NetworkSnapshot(IReadOnlyList<NetworkIdentity> Adapters)
    {
        public static readonly NetworkSnapshot Empty = new(Array.Empty<NetworkIdentity>());

        /// <summary>The network that decides the (global) tunnel action; null when nothing is connected.</summary>
        public NetworkIdentity? Primary => Adapters.FirstOrDefault(a => a.IsPrimary);

        public bool IsEmpty => Adapters.Count == 0;

        /// <summary>Same adapters, same identities, same primary as <paramref name="other"/>.</summary>
        public bool SameAs(NetworkSnapshot? other) =>
            other != null && Fingerprint() == other.Fingerprint();

        public string Fingerprint() => string.Join(";",
            Adapters.OrderBy(a => a.AdapterId, StringComparer.Ordinal).Select(a => a.Fingerprint()));
    }
}
