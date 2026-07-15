using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MasselGUARD.Models
{
    public enum AppMode { Standalone, Companion, Mixed }

    /// <summary>
    /// Controls the info/statistics section displayed above the footer.
    /// </summary>
    public enum InfoSectionMode
    {
        /// <summary>Panel visible; session traffic stored in history.</summary>
        Show,
        /// <summary>Panel hidden; traffic data is still stored.</summary>
        Hide,
        /// <summary>Panel hidden; no traffic data is recorded.</summary>
        HideAndNoStore,
    }

    /// <summary>
    /// Root configuration object serialised to %APPDATA%\MasselGUARD\config.json.
    /// Pure data — no UI, no logic.
    /// </summary>
    public class AppConfig
    {
        /// <summary>Returns an independent deep copy via JSON round-trip.</summary>
        public AppConfig DeepClone()
        {
            var json = JsonSerializer.Serialize(this);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }

        // ── Automation ───────────────────────────────────────────────────────
        public List<TunnelRule>  Rules       { get; set; } = new();
        public string DefaultAction          { get; set; } = "none";
        public string DefaultTunnel          { get; set; } = "";
        public string OpenWifiTunnel         { get; set; } = "";
        public bool   ManualMode             { get; set; } = false;

        // ── Tunnels ──────────────────────────────────────────────────────────
        public List<StoredTunnel>  Tunnels      { get; set; } = new();
        public List<TunnelGroup>   TunnelGroups { get; set; } = new()
        {
            new("Work"), new("Personal"), new("Travel")
        };
        /// <summary>Tab names that are hidden — includes "All", "Uncategorized", and custom group names.</summary>
        public System.Collections.Generic.HashSet<string> HiddenTabs { get; set; } = new();
        /// <summary>Which group tab is selected on startup. Empty = show All.</summary>
        public string  DefaultGroup          { get; set; } = "";
        public bool    AlwaysHideTunnelCount { get; set; } = false;
        public bool    HideEmptyGroups              { get; set; } = false;
        public bool    ShowWifiRulesOnMainWindow    { get; set; } = true;
        public bool    ShowTunnelRulesColumn        { get; set; } = true;
        public bool    ShowActivityLog              { get; set; } = true;
        public bool    StartWithWindows             { get; set; } = false;

        // ── App settings ─────────────────────────────────────────────────────
        public AppMode Mode               { get; set; } = AppMode.Standalone;        public string  Language           { get; set; } = "en";
        public string  LogLevelSetting    { get; set; } = "normal";
        public bool    ShowTrayPopupOnSwitch { get; set; } = true;
        public int     NotificationDurationSeconds { get; set; } = 5;
        public bool    SuppressPortableUpdatePrompt { get; set; } = false;
        /// <summary>"onstart" | "daily" | "weekly" | "monthly" | "never"</summary>
        public string  UpdateCheckFrequency { get; set; } = "weekly";
        /// <summary>Version string of the last run that completed the wizard. Used to detect upgrades.</summary>
        public string? LastRunVersion { get; set; } = null;

        // ── Font override ────────────────────────────────────────────────────
        /// <summary>When true the user-chosen font replaces the theme's own font.</summary>
        public bool   FontOverrideEnabled { get; set; } = false;
        /// <summary>Font family name. Empty string = use the Windows UI system font.</summary>
        public string FontOverrideFamily  { get; set; } = "";
        /// <summary>Base font size in points. 0 = use theme default (11 pt).</summary>
        public double FontOverrideSize    { get; set; } = 0.0;

        // ── Theme ────────────────────────────────────────────────────────────
        /// <summary>Active theme folder name, or "__system__" for Windows system colours.</summary>
        public string ActiveTheme { get; set; } = "__system__";
        /// <summary>"auto" (follow Windows) | "light" | "dark"</summary>
        public string SystemThemeMode  { get; set; } = "auto";
        /// <summary>The official shared-themes repository — the default value and the
        /// target of the Settings "Default" button.</summary>
        public const string DefaultSharedThemesRepoUrl = "https://github.com/masselink/MasselGUARD-themes";

        /// <summary>Git/HTTPS repo URL the "Download shared themes" button fetches from
        /// (a GitHub repo URL or a direct .zip archive URL). Defaults to the official repo.</summary>
        public string SharedThemesRepoUrl { get; set; } = DefaultSharedThemesRepoUrl;
        /// <summary>When true (default) clicking ✕ shows a confirm dialog before closing.</summary>
        public bool   ConfirmOnClose   { get; set; } = true;

        // ── WireGuard install ────────────────────────────────────────────────
        public string  WireGuardInstallDirectory { get; set; } = @"C:\Program Files\WireGuard";
        public string? InstalledPath    { get; set; } = null;

        // ── Auto-reconnect ────────────────────────────────────────────────────
        /// <summary>
        /// Controls when auto-reconnect is active.
        /// "off"        — disabled globally.
        /// "per-tunnel" — each tunnel controls its own toggle.
        /// "always"     — every tunnel reconnects regardless of the per-tunnel toggle.
        /// </summary>
        public string AutoReconnectMode { get; set; } = "always";

        // ── DNS leak indicator ────────────────────────────────────────────────
        /// <summary>
        /// When true (default), a DNS leak status badge (🔒/⚠/ⓘ) is shown
        /// inline next to each active tunnel's status.
        /// </summary>
        public bool ShowDnsIndicator { get; set; } = true;

        // ── Possible-DNS-leak alerts ──────────────────────────────────────────
        // Three independent delivery channels for "this active tunnel may be leaking
        // DNS". The icon (ShowDnsIndicator above) is an always-on status badge; the
        // log + toast warnings are edge-triggered (once per leak episode) and only
        // fire while the leak is UNMITIGATED — i.e. smart name resolution is still
        // enabled. Turn all three off to disable DNS-leak surfacing entirely.

        /// <summary>Write a warning line to the activity log on a possible (unmitigated) DNS leak.</summary>
        public bool DnsLeakWarnLog { get; set; } = true;

        /// <summary>Show a tray toast on a possible (unmitigated) DNS leak.</summary>
        public bool DnsLeakWarnToast { get; set; } = true;

        // ── Info / statistics section ─────────────────────────────────────────
        /// <summary>Show the timeline/statistics panel above the footer.</summary>
        public bool ShowTimeline             { get; set; } = true;
        /// <summary>Record tunnel connection history (uptime, traffic).</summary>
        public bool StoreConnectionHistory   { get; set; } = true;
        /// <summary>Record WiFi SSID connection timestamps to ssid_history.json.</summary>
        public bool StoreWifiHistory         { get; set; } = true;
        /// <summary>Draw the WiFi SSID rows in the activity chart (requires StoreWifiHistory).</summary>
        public bool ShowWifiInChart          { get; set; } = true;
        /// <summary>1 = last 24 h, 7 = last 7 days, 31 = last 31 days.</summary>
        public int  InfoTimeRangeDays        { get; set; } = 1;

        // Legacy — kept for JSON backwards-compat deserialization only; not used by code.
        // The setter migrates old configs to the two new bools.
        [System.Text.Json.Serialization.JsonInclude]
        public InfoSectionMode InfoSection
        {
            get => ShowTimeline ? InfoSectionMode.Show
                 : StoreConnectionHistory ? InfoSectionMode.Hide
                 : InfoSectionMode.HideAndNoStore;
            set
            {
                ShowTimeline           = value == InfoSectionMode.Show;
                StoreConnectionHistory = value != InfoSectionMode.HideAndNoStore;
            }
        }

        // ── Column widths (pixels; 0 = derive from proportional defaults) ────
        public double TunColNameW    { get; set; } = 0;
        public double TunColStatusW  { get; set; } = 0;
        public double TunColRulesW   { get; set; } = 0;
        public double TunColActionW  { get; set; } = 0;
        public double WifiColNameW   { get; set; } = 0;
        public double WifiColSsidW   { get; set; } = 0;
        public double WifiColActionW { get; set; } = 0;
        public double WifiColCountW  { get; set; } = 0;
        public double WifiColTunnelW { get; set; } = 0;

        // ── Kill switch ───────────────────────────────────────────────────────
        /// <summary>
        /// "per-tunnel" — each tunnel controls its own kill switch toggle (default).
        /// "always"     — kill switch is always active for every tunnel regardless of
        ///                the per-tunnel setting.
        /// </summary>
        public string KillSwitchMode { get; set; } = "per-tunnel";

        // ── Validation ────────────────────────────────────────────────────────
        /// <summary>
        /// When true, pre-flight WireGuard config validation is skipped for ALL tunnels.
        /// Overrides the per-tunnel SkipValidation flag. Use only as a last resort
        /// when a valid-but-unusual config is incorrectly rejected by the validator.
        /// </summary>
        public bool SkipTunnelValidation { get; set; } = false;

        // ── Update checker ───────────────────────────────────────────────────
        public DateTime LastUpdateCheck    { get; set; } = DateTime.MinValue;
        public string?  LatestKnownVersion { get; set; } = null;

        /// <summary>Ids of installed themes whose repo "version" was newer than the installed
        /// one, as of the last check. Checked at the same time as the app update (same
        /// frequency setting) — see MainWindow.CheckForUpdatesAsync.</summary>
        public List<string> ThemeUpdatesAvailable { get; set; } = new();

        // ── Computed (not serialised) ────────────────────────────────────────
        [JsonIgnore]
        public string ConfDirectory => string.IsNullOrWhiteSpace(WireGuardInstallDirectory)
            ? ""
            : Path.Combine(WireGuardInstallDirectory, @"Data\Configurations");

        [JsonIgnore]
        public string WgExePath => string.IsNullOrWhiteSpace(WireGuardInstallDirectory)
            ? "wireguard"
            : Path.Combine(WireGuardInstallDirectory, "wireguard.exe");

        // ── Backward-compat shim ─────────────────────────────────────────────
        [JsonIgnore] private bool _legacyLocalTunnels = true;
        public bool EnableLocalTunnels
        {
            get => Mode != AppMode.Companion;
            set
            {
                _legacyLocalTunnels = value;
                if (!value && Mode == AppMode.Mixed) Mode = AppMode.Companion;
            }
        }
    }
}
