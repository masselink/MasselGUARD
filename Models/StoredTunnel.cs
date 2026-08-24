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
        // Note: the former SkipValidation flag was removed — pre-flight config
        // validation is always enforced and cannot be overridden. Old config.json
        // values are simply ignored on load.

        // ── Data usage ───────────────────────────────────────────────────────
        /// <summary>
        /// Monthly data cap in megabytes for this tunnel. 0 = unlimited (no cap).
        /// When the month's Rx+Tx total crosses this, a one-time warning is raised.
        /// </summary>
        public int MonthlyCapMB { get; set; } = 0;
    }
}
