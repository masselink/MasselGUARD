using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MasselGUARD.Models
{
    public enum AppMode { Standalone, Companion, Mixed }

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
        public string ActiveTheme      { get; set; } = "default-dark";
        public string ActiveDarkTheme  { get; set; } = "default-dark";
        public string ActiveLightTheme { get; set; } = "default-light";
        public bool   AutoTheme        { get; set; } = false;
        /// <summary>When false (default) Windows 11 system colors are used; when true the theme-file pickers apply.</summary>
        public bool   UseCustomTheme   { get; set; } = false;
        /// <summary>"auto" (follow Windows) | "light" | "dark"</summary>
        public string SystemThemeMode  { get; set; } = "auto";
        /// <summary>When true (default) clicking ✕ shows a confirm dialog before closing.</summary>
        public bool   ConfirmOnClose   { get; set; } = true;

        // ── WireGuard install ────────────────────────────────────────────────
        public string  WireGuardInstallDirectory { get; set; } = @"C:\Program Files\WireGuard";
        public string? InstalledPath    { get; set; } = null;

        // ── Update checker ───────────────────────────────────────────────────
        public DateTime LastUpdateCheck    { get; set; } = DateTime.MinValue;
        public string?  LatestKnownVersion { get; set; } = null;

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
