using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>
    /// The unified <c>.masselguard</c> file — a full settings snapshot that doubles as a portable
    /// settings export AND a managed policy.
    /// <list type="bullet">
    /// <item><b>Import</b> (wizard / Advanced) → every setting value is applied and stays editable.</item>
    /// <item><b>Next to the exe</b> → only the sections/settings listed under <c>Locked</c> are
    ///   forced + locked; all other values are ignored. No <c>Locked</c> ⇒ nothing is locked.</item>
    /// </list>
    /// Tunnel definitions and DPAPI material are never included; WiFi rules and groups are.
    /// WPF-free so the CLI shares it (add new Services files to MasselGUARDcli.csproj).
    /// </summary>
    public static class PresetService
    {
        public const string Extension = ".masselguard";

        private static readonly HashSet<string> MetaKeys =
            new(StringComparer.OrdinalIgnoreCase) { "ExportVersion", "AppVersion", "ExportedAt", "PolicyName", "Locked" };

        /// <summary>Every policy/settings field written to a snapshot (defaults + empties included).
        /// Excludes tunnel definitions, window/column state and runtime bookkeeping.</summary>
        public static readonly string[] PolicyFields =
        {
            "Mode", "Language", "StartWithWindows", "ConfirmOnClose",
            "Rules", "TunnelGroups", "DefaultGroup",
            "DefaultAction", "DefaultTunnel", "OpenWifiTunnel", "TrustedNetworks", "ManualMode",
            "AutoReconnectMode", "KillSwitchMode", "SkipTunnelValidation",
            "ShowDnsIndicator", "DnsLeakWarnLog", "DnsLeakWarnToast",
            "UpdateCheckFrequency", "WireGuardInstallDirectory",
            "ActiveTheme", "SystemThemeMode", "SharedThemesRepoUrl",
            "ShowTrayPopupOnSwitch", "NotificationDurationSeconds", "LogLevelSetting",
            "ShowWifiRulesOnMainWindow", "ShowTunnelRulesColumn", "ShowActivityLog",
            "ShowTimeline", "StoreConnectionHistory", "StoreWifiHistory", "ShowWifiInChart", "InfoTimeRangeDays",
            "AlwaysHideTunnelCount", "HideEmptyGroups",
        };

        /// <summary>A lockable section → the AppConfig fields it covers. Used to expand a locked
        /// <c>block</c> into individual fields when enforcing a preset, and to drive the export UI.</summary>
        public static readonly (string Key, string[] Fields)[] Blocks =
        {
            ("appMode",       new[] { "Mode" }),
            ("language",      new[] { "Language" }),
            ("startup",       new[] { "StartWithWindows", "ConfirmOnClose" }),
            ("automation",    new[] { "Rules", "DefaultAction", "DefaultTunnel", "OpenWifiTunnel", "TrustedNetworks", "ManualMode" }),
            ("autoReconnect", new[] { "AutoReconnectMode" }),
            ("killSwitch",    new[] { "KillSwitchMode" }),
            ("validation",    new[] { "SkipTunnelValidation" }),
            ("dns",           new[] { "ShowDnsIndicator", "DnsLeakWarnLog", "DnsLeakWarnToast" }),
            ("updates",       new[] { "UpdateCheckFrequency" }),
            ("wireguardPath", new[] { "WireGuardInstallDirectory" }),
            ("themes",        new[] { "ActiveTheme", "SystemThemeMode", "SharedThemesRepoUrl" }),
        };

        private static readonly Dictionary<string, string[]> BlockFields =
            Blocks.ToDictionary(b => b.Key, b => b.Fields, StringComparer.OrdinalIgnoreCase);

        private static readonly JsonSerializerOptions Opts = new()
        {
            WriteIndented               = true,
            PropertyNameCaseInsensitive = true,
            Converters                  = { new JsonStringEnumConverter() },   // Mode as "Companion"
        };

        // ── Build (export) ────────────────────────────────────────────────────

        /// <summary>Builds a full snapshot of every policy field. When <paramref name="lockedBlocks"/>
        /// / <paramref name="lockedSettings"/> are supplied they are recorded under <c>Locked</c>,
        /// which is what a preset enforces (values alone don't lock anything).</summary>
        public static JsonObject Build(AppConfig cfg, string appVersion, string? policyName,
            IEnumerable<string>? lockedBlocks = null, IEnumerable<string>? lockedSettings = null)
        {
            var o = new JsonObject
            {
                ["ExportVersion"] = 3,
                ["AppVersion"]    = appVersion,
                ["ExportedAt"]    = DateTime.UtcNow.ToString("o"),
            };
            if (!string.IsNullOrWhiteSpace(policyName))
                o["PolicyName"] = policyName.Trim();

            var lb = (lockedBlocks   ?? Enumerable.Empty<string>()).ToArray();
            var ls = (lockedSettings ?? Enumerable.Empty<string>()).ToArray();
            if (lb.Length > 0 || ls.Length > 0)
            {
                var locked = new JsonObject();
                if (lb.Length > 0) { var a = new JsonArray(); foreach (var x in lb) a.Add(JsonValue.Create(x)); locked["blocks"]   = a; }
                if (ls.Length > 0) { var a = new JsonArray(); foreach (var x in ls) a.Add(JsonValue.Create(x)); locked["settings"] = a; }
                o["Locked"] = locked;
            }

            foreach (var field in PolicyFields)
            {
                var prop = typeof(AppConfig).GetProperty(field);
                if (prop == null) continue;
                o[field] = JsonSerializer.SerializeToNode(prop.GetValue(cfg), prop.PropertyType, Opts);
            }
            return o;
        }

        public static string ToJson(JsonObject o) => o.ToJsonString(Opts);

        // ── Apply: import (all) ───────────────────────────────────────────────

        /// <summary>Applies every setting value in the file (metadata + Locked ignored). Editable —
        /// used for a normal import.</summary>
        public static void ApplyAll(AppConfig cfg, JsonObject obj)
        {
            foreach (var kv in obj)
            {
                if (MetaKeys.Contains(kv.Key)) continue;
                SetProp(cfg, kv.Key, kv.Value);
            }
        }

        // ── Apply: preset (only the locked subset) ────────────────────────────

        /// <summary>Forces + locks ONLY the fields named by the file's <c>Locked</c> section
        /// (blocks expanded to their fields + any explicit settings). All other values are ignored.
        /// Returns the policy name and the set of locked field names. Safe to call repeatedly.</summary>
        public static (string? policyName, HashSet<string> locked) ApplyLocked(AppConfig cfg, JsonObject obj)
        {
            var locked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? policyName = SafeStr(GetVal(obj, "PolicyName"));

            foreach (var field in LockedFields(obj))
            {
                var prop = typeof(AppConfig).GetProperty(field,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop == null || !prop.CanWrite) continue;

                var node = GetVal(obj, field);
                if (node != null)
                {
                    try { prop.SetValue(cfg, JsonSerializer.Deserialize(node.ToJsonString(), prop.PropertyType, Opts)); }
                    catch { continue; }        // bad value — don't lock what we couldn't apply
                }
                locked.Add(prop.Name);
            }
            return (policyName, locked);
        }

        /// <summary>The field names a file locks (from <c>Locked.blocks</c> + <c>Locked.settings</c>).</summary>
        private static HashSet<string> LockedFields(JsonObject obj)
        {
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (GetVal(obj, "Locked") is not JsonObject lk) return fields;

            if (lk["blocks"] is JsonArray ba)
                foreach (var n in ba)
                {
                    var b = SafeStr(n);
                    if (b != null && BlockFields.TryGetValue(b, out var fs))
                        foreach (var f in fs) fields.Add(f);
                }
            if (lk["settings"] is JsonArray sa)
                foreach (var n in sa)
                {
                    var s = SafeStr(n);
                    if (s != null) fields.Add(s);
                }
            return fields;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void SetProp(AppConfig cfg, string name, JsonNode? node)
        {
            if (node == null) return;
            var prop = typeof(AppConfig).GetProperty(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop == null || !prop.CanWrite) return;
            try { prop.SetValue(cfg, JsonSerializer.Deserialize(node.ToJsonString(), prop.PropertyType, Opts)); }
            catch { }
        }

        private static JsonNode? GetVal(JsonObject obj, string name)
        {
            if (obj.TryGetPropertyValue(name, out var v)) return v;
            foreach (var kv in obj)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        private static string? SafeStr(JsonNode? n)
        {
            try { return n?.GetValue<string>(); } catch { return null; }
        }

        // ── File discovery / IO ───────────────────────────────────────────────

        public static string ExeDirectory()
            => Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)
               ?? AppContext.BaseDirectory;

        /// <summary>First <c>*.masselguard</c> next to the exe (alphabetical, deterministic), or null.</summary>
        public static string? FindPresetFile()
        {
            try
            {
                var files = Directory.GetFiles(ExeDirectory(), "*" + Extension);
                if (files.Length == 0) return null;
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                return files[0];
            }
            catch { return null; }
        }

        public static JsonObject? LoadObject(string path)
        {
            try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
            catch { return null; }
        }
    }
}
