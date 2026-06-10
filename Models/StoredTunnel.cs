using System.Text.Json.Serialization;

namespace MasselGUARD.Models
{
    /// <summary>
    /// A tunnel configuration entry as stored in config.json.
    /// Source="local" means it is managed by MasselGUARD (tunnel.dll).
    /// Any other source means it is a WireGuard-for-Windows profile link.
    /// </summary>
    public class StoredTunnel
    {
        public string  Name   { get; set; } = "";
        /// <summary>
        /// Legacy inline DPAPI blob. Kept for deserialization during migration only.
        /// New saves always use Path to a .conf.dpapi file; this is null and omitted from JSON.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Config { get; set; } = null;
        /// <summary>"local" | "wireguard" — determines which backend handles this tunnel.</summary>
        public string  Source { get; set; } = "local";
        /// <summary>File path to the .conf.dpapi file.</summary>
        public string? Path   { get; set; } = null;
        public string  Group  { get; set; } = "";
        public string  Notes  { get; set; } = "";

        // ── Scripts ──────────────────────────────────────────────────────────
        public string PreConnectScript    { get; set; } = "";
        public string PostConnectScript   { get; set; } = "";
        public string PreDisconnectScript { get; set; } = "";
        public string PostDisconnectScript{ get; set; } = "";

        // ── Advanced ─────────────────────────────────────────────────────────
        public bool KillSwitch      { get; set; } = false;
        public bool AutoReconnect   { get; set; } = false;
        public int  RetryCount      { get; set; } = 0;
        public int  RetryDelaySec   { get; set; } = 5;
        /// <summary>
        /// When true, pre-flight config validation is skipped for this tunnel.
        /// Use only when the config is known to be valid but uses constructs
        /// the validator does not yet understand.
        /// </summary>
        public bool SkipValidation  { get; set; } = false;
    }
}
