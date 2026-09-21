using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MasselGUARD.Models
{
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

        // ── Trusted-network auto-protect ─────────────────────────────────────
        // The feature is driven entirely by a single "trusted"-kind TunnelRule in
        // <see cref="Rules"/>: its existence enables protection and it carries the tunnel
        // to bring up on an untrusted network. The only extra state is the safe-SSID list.
        /// <summary>SSIDs considered safe; protection stands down (disconnects) on these.</summary>
        public List<string> TrustedNetworks { get; set; } = new();

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
        /// <summary>Start with the main window hidden in the tray (no window shown on launch).</summary>
        public bool    StartMinimized               { get; set; } = false;
        /// <summary>Name of the tunnel to connect automatically on startup ("" = none). A per-tunnel
        /// behaviour, like <see cref="OpenWifiTunnel"/>.</summary>
        public string  ConnectOnStartTunnel         { get; set; } = "";

        // ── App settings ─────────────────────────────────────────────────────
        public string  Language           { get; set; } = "en";
        public string  LogLevelSetting    { get; set; } = "normal";
        public bool    ShowTrayPopupOnSwitch { get; set; } = true;
        public int     NotificationDurationSeconds { get; set; } = 5;
        public bool    SuppressPortableUpdatePrompt { get; set; } = false;
        /// <summary>"onstart" | "daily" | "weekly" | "monthly" | "never"</summary>
        public string  UpdateCheckFrequency { get; set; } = "weekly";
        /// <summary>Version string of the last run that completed the wizard. Used to detect upgrades.</summary>
        public string? LastRunVersion { get; set; } = null;

        // ── Feature modules ───────────────────────────────────────────────────
        // MasselGUARD's two top-level features can each be turned on/off (wizard + Settings),
        // so it can run as a full tunnel manager, a DNS-only resolver switcher, or both.
        // Invariant: at least one is always on (see ConfigService.EnsureAtLeastOneModule).
        // Both default true → upgraders keep tunnels AND see the DNS section (DnsAutomationEnabled
        // still gates whether DNS automation actually runs). See docs/FeatureModules-Design.md.

        /// <summary>Show/run the WireGuard tunnel feature (tunnel list, connect, kill switch,
        /// auto-reconnect, split, caps, Mode, tunnel rules/timeline).</summary>
        public bool EnableTunnels { get; set; } = true;

        /// <summary>Show the DNS-automation feature (Settings section, rule DNS pickers, engine).
        /// <see cref="DnsAutomationEnabled"/> remains the runtime on/off within it.</summary>
        public bool EnableDns { get; set; } = true;

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

        // ── DNS automation ────────────────────────────────────────────────────
        // Rule-driven resolver policy, independent of tunnels (see
        // docs/DnsAutomation-Design.md, Model C). All defaults keep existing behaviour:
        // the engine is off and no profiles exist until the user opts in.

        /// <summary>Master switch for the DNS rule engine (also gated by <see cref="ManualMode"/>).
        /// Off by default so upgrades change nothing.</summary>
        public bool DnsAutomationEnabled { get; set; } = false;

        /// <summary>Named resolver profiles; rules/config reference these by <see cref="DnsProfile.Id"/>.</summary>
        public List<DnsProfile> DnsProfiles { get; set; } = new();

        /// <summary>DNS profile id applied when no DNS rule matches. "" = leave alone;
        /// <see cref="DnsProfile.AutomaticId"/> = force DHCP on unmatched networks.</summary>
        public string DefaultDnsProfileId { get; set; } = "";

        /// <summary>DNS profile applied on an OPEN/unsecured network (parallels
        /// <see cref="OpenWifiTunnel"/>). Lets "any open Wi-Fi → encrypted DoH" work with no
        /// per-SSID rule.</summary>
        public string OpenWifiDnsProfileId { get; set; } = "";

        /// <summary>Which address families a DNS profile touches: "both" | "v4" | "v6".
        /// Default both (setting only v4 leaves v6 resolving via the network resolver).</summary>
        public string DnsAddressFamilies { get; set; } = "both";

        // ── Info / statistics section ─────────────────────────────────────────
        /// <summary>Show the timeline/statistics panel above the footer.</summary>
        public bool ShowTimeline             { get; set; } = true;
        /// <summary>Record tunnel connection history (uptime, traffic).</summary>
        public bool StoreConnectionHistory   { get; set; } = true;
        /// <summary>Record WiFi SSID connection timestamps to ssid_history.json.</summary>
        public bool StoreWifiHistory         { get; set; } = true;
        /// <summary>Draw the WiFi SSID rows in the activity chart (requires StoreWifiHistory).</summary>
        public bool ShowWifiInChart          { get; set; } = true;
        /// <summary>Record which DNS profile is active over time to dns_history.json.</summary>
        public bool StoreDnsHistory          { get; set; } = true;
        /// <summary>Draw the active-DNS-profile band in the charts (requires StoreDnsHistory).</summary>
        public bool ShowDnsInChart           { get; set; } = true;
        /// <summary>1 = last 24 h, 7 = last 7 days, 31 = last 31 days.</summary>
        public int  InfoTimeRangeDays        { get; set; } = 1;
        /// <summary>Legacy single-view selector ("timeline"/"usage"); superseded by the two
        /// independent pane toggles below. Kept for one-time migration on load.</summary>
        public string InfoPanelMode          { get; set; } = "timeline";
        /// <summary>Info panel: show the Timeline pane. Timeline + Data-usage are independent —
        /// both on = the layered (stacked) view; both off = the panel is hidden.</summary>
        public bool ShowTimelinePane         { get; set; } = true;
        /// <summary>Info panel: show the Data-usage pane (see <see cref="ShowTimelinePane"/>).</summary>
        public bool ShowUsagePane            { get; set; } = false;
        /// <summary>Info panel: show the DNS pane (active DNS profile / server over time).</summary>
        public bool ShowDnsPane              { get; set; } = false;
        /// <summary>How per-tunnel data-cap usage is shown on the tunnel row:
        /// "bars" (slim horizontal progress bars, breakdown on hover — the default) or
        /// "rings" (compact concentric-style arcs).</summary>
        public string CapIndicatorStyle      { get; set; } = "bars";

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
        public double WifiColDnsW    { get; set; } = 0;
        public double DnsColNameW    { get; set; } = 0;
        public double DnsColTypeW    { get; set; } = 0;
        public double DnsColServerW  { get; set; } = 0;
        public double DnsColRulesW   { get; set; } = 0;
        public double DnsColEnableW  { get; set; } = 0;

        // ── Kill switch ───────────────────────────────────────────────────────
        /// <summary>
        /// "per-tunnel" — each tunnel controls its own kill switch toggle (default).
        /// "always"     — kill switch is always active for every tunnel regardless of
        ///                the per-tunnel setting.
        /// </summary>
        public string KillSwitchMode { get; set; } = "per-tunnel";

        // ── Validation ────────────────────────────────────────────────────────
        /// <summary>
        /// Bypass switch for pre-flight WireGuard config validation.
        /// <para>
        /// Default <c>false</c> = validation is ACTIVE. Setting it true bypasses
        /// validation for all tunnels — a last-resort escape hatch for a valid-but-unusual
        /// config the validator rejects. There is deliberately no per-tunnel equivalent.
        /// </para>
        /// </summary>
        public bool SkipTunnelValidation { get; set; } = false;

        // ── Update checker ───────────────────────────────────────────────────
        public DateTime LastUpdateCheck    { get; set; } = DateTime.MinValue;
        public string?  LatestKnownVersion { get; set; } = null;

        /// <summary>Set once the user picks "Don't remind me" on the startup notice that
        /// offers to switch an emulated x64 build to the native ARM64 build (shown only when
        /// running the x64 build on an ARM64 system). Suppresses the notice on later launches.</summary>
        public bool ArmSwitchDismissed { get; set; } = false;

        /// <summary>Ids of installed themes whose repo "version" was newer than the installed
        /// one, as of the last check. Checked at the same time as the app update (same
        /// frequency setting) — see MainWindow.CheckForUpdatesAsync.</summary>
        public List<string> ThemeUpdatesAvailable { get; set; } = new();

    }
}
