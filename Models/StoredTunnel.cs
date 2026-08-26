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

        // ── Data-usage warnings ──────────────────────────────────────────────
        // Advisory caps in megabytes; 0 = off. When the tunnel's Rx+Tx total for
        // the period crosses the cap, a one-time warning is raised (log + tray +
        // row highlight). These are warnings only — nothing is disconnected.
        /// <summary>Daily data-usage warning threshold in MB (UTC calendar day). 0 = off.</summary>
        public int DailyCapMB   { get; set; } = 0;
        /// <summary>Weekly data-usage warning threshold in MB (UTC calendar week, Mon-start). 0 = off.</summary>
        public int WeeklyCapMB  { get; set; } = 0;
        /// <summary>Monthly data-usage warning threshold in MB (UTC calendar month). 0 = off.</summary>
        public int MonthlyCapMB { get; set; } = 0;

        // (A tunnel's usage rings appear automatically whenever it has a cap set —
        //  the innermost ring is the day, the middle the week, the outer the month.)

        // Whether reaching each period's cap disconnects the tunnel (and blocks a new
        // connect while over budget, unless the user chooses to ignore the limit).
        // Default off — enforcement is opt-in.
        public bool DailyCapKill   { get; set; } = false;
        public bool WeeklyCapKill  { get; set; } = false;
        public bool MonthlyCapKill { get; set; } = false;
    }
}
