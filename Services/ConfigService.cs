using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Handles all AppConfig persistence: load, save, import, export.
    /// No UI references. Raises ConfigChanged when the config is replaced.
    /// </summary>
    public class ConfigService
    {
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MasselGUARD", "config.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        public AppConfig Config { get; private set; } = new();

        public event Action? ConfigChanged;

        /// <summary>
        /// True if no config.json existed when Load() was called
        /// (first run — show wizard).
        /// </summary>
        public bool IsFirstRun { get; private set; }

        // ── Managed preset (a .masselguard next to the exe = forced/locked config) ──
        private System.Text.Json.Nodes.JsonObject? _presetObj;   // cached for re-assert on save
        private HashSet<string> _lockedKeys = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when a *.masselguard file was found next to the exe.</summary>
        public bool    HasManagedPreset { get; private set; }
        /// <summary>Policy name from the preset (for the "managed by …" banner); may be null.</summary>
        public string? PolicyName       { get; private set; }

        /// <summary>True when the given AppConfig field is forced + locked by the managed preset.</summary>
        public bool IsLocked(string field) => _lockedKeys.Contains(field);
        /// <summary>True when the preset locks the tunnel list (only if a hand-authored preset
        /// includes a Tunnels value — the normal export never does).</summary>
        public bool TunnelsLocked => _lockedKeys.Contains("Tunnels");

        // ── Load ─────────────────────────────────────────────────────────────
        public void Load()
        {
            if (!File.Exists(ConfigPath))
            {
                IsFirstRun = true;
                Config     = new AppConfig();
                ApplyPreset();   // a managed preset still forces its values on first run
                return;
            }
            try
            {
                Config = JsonSerializer.Deserialize<AppConfig>(
                    File.ReadAllText(ConfigPath), JsonOpts) ?? new AppConfig();
            }
            catch
            {
                Config = new AppConfig();
            }

            // The shipped "grey" theme folder was renamed to "blueongrey" in 3.7 — keep
            // users who had it selected pointed at the right theme.
            if (string.Equals(Config.ActiveTheme, "grey", StringComparison.OrdinalIgnoreCase))
            {
                Config.ActiveTheme = "blueongrey";
                try { Save(); } catch { /* best-effort */ }
            }

            MigrateInlineConfigsToFiles();
            ApplyPreset();
        }

        /// <summary>
        /// Loads the managed preset (if present next to the exe), forces its values onto
        /// <see cref="Config"/>, and records what is locked. Called at the end of Load and
        /// re-asserted on every Save so a hand-edited config.json can never win.
        /// </summary>
        private void ApplyPreset()
        {
            _lockedKeys = new(StringComparer.OrdinalIgnoreCase);
            _presetObj  = null;
            PolicyName  = null;
            HasManagedPreset = false;

            try
            {
                var path = PresetService.FindPresetFile();
                if (path == null) return;
                var obj = PresetService.LoadObject(path);
                if (obj == null) return;

                var (name, locked) = PresetService.ApplyLocked(Config, obj);
                if (locked.Count == 0) return;   // a .masselguard with nothing marked Locked = not a policy

                _presetObj       = obj;
                _lockedKeys      = locked;
                PolicyName       = name;
                HasManagedPreset = true;
            }
            catch
            {
                // A broken preset must never prevent the app from starting — fail open.
                _presetObj = null;
                HasManagedPreset = false;
            }
        }

        /// <summary>
        /// One-time migration: move any inline Config blobs to .conf.dpapi files
        /// and clear the Config field so it no longer appears in config.json.
        /// </summary>
        private void MigrateInlineConfigsToFiles()
        {
            bool dirty = false;
            foreach (var t in Config.Tunnels)
            {
                if (string.IsNullOrEmpty(t.Config)) continue;

                // Decrypt the inline blob (DPAPI or legacy plaintext fallback).
                string plaintext;
                try
                {
                    var decrypted = ProtectedData.Unprotect(
                        Convert.FromBase64String(t.Config), null,
                        DataProtectionScope.CurrentUser);
                    plaintext = System.Text.Encoding.UTF8.GetString(decrypted);
                }
                catch
                {
                    plaintext = t.Config; // legacy plaintext stored by old GUI
                }

                // Only write a file when the existing path is missing or gone.
                if (string.IsNullOrEmpty(t.Path) || !File.Exists(t.Path))
                    t.Path = TunnelService.SaveConfigToFile(t.Name, plaintext);

                t.Config = null;
                dirty = true;
            }
            if (dirty) Save();
        }

        // ── Save ─────────────────────────────────────────────────────────────
        public void Save()
        {
            // Re-assert the managed preset's locked values so they're always persisted,
            // even if something tried to change them (or config.json was hand-edited).
            if (_presetObj != null) PresetService.ApplyLocked(Config, _presetObj);

            var dir = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(dir);
            EnsureDirectoryAcl(dir);
            File.WriteAllText(ConfigPath,
                JsonSerializer.Serialize(Config, JsonOpts));
            ConfigChanged?.Invoke();
        }

        // ── Directory ACL ─────────────────────────────────────────────────────

        // Applied once per process run; also picks up existing installations
        // that were created before this hardening was added.
        private static bool _aclApplied;

        /// <summary>
        /// Restricts <paramref name="dir"/> (and everything inside it via
        /// inheritable rules) to the current user only — removes the default
        /// Administrators read-access inherited from %APPDATA%.
        /// Safe to call on an already-restricted directory.
        /// </summary>
        private static void EnsureDirectoryAcl(string dir)
        {
            if (_aclApplied) return;
            _aclApplied = true;
            try
            {
                var userSid  = WindowsIdentity.GetCurrent().User!;
                var security = new DirectorySecurity();

                // Block inheritance from %APPDATA% so our explicit rules are the only ones.
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

                // Current user: full control, propagates into all subfolders and files.
                security.AddAccessRule(new FileSystemAccessRule(
                    userSid,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));

                new DirectoryInfo(dir).SetAccessControl(security);
            }
            catch
            {
                // Non-fatal — ACL tightening is best-effort.
                // Failure here does not affect functionality.
            }
        }

        // ── Export ───────────────────────────────────────────────────────────
        /// <summary>
        /// Writes a full <c>.masselguard</c> snapshot of every policy setting (defaults and
        /// empties included), plus rules and groups. Never includes tunnel configs or DPAPI
        /// material. A non-empty <paramref name="policyName"/> makes the file a managed policy:
        /// dropped next to the exe it forces + locks its settings and names the lock banner.
        /// </summary>
        public void Export(string path, string appVersion, string? policyName = null,
            IEnumerable<string>? lockedBlocks = null, IEnumerable<string>? lockedSettings = null)
        {
            var obj = PresetService.Build(Config, appVersion, policyName, lockedBlocks, lockedSettings);
            File.WriteAllText(path, PresetService.ToJson(obj));
        }

        // ── Import ───────────────────────────────────────────────────────────
        /// <summary>
        /// Reads a <c>.masselguard</c> file and applies its settings into Config (editable — no
        /// locking; that only happens for a file placed next to the exe). Returns the AppVersion
        /// string found in the file (empty if absent). Unknown fields are ignored.
        /// </summary>
        public string Import(string path)
        {
            var obj = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) as System.Text.Json.Nodes.JsonObject
                      ?? new System.Text.Json.Nodes.JsonObject();

            string fileVersion = "";
            try { fileVersion = obj["AppVersion"]?.GetValue<string>() ?? ""; } catch { }

            PresetService.ApplyAll(Config, obj);   // import applies all values; Locked is ignored

            Save();
            return fileVersion;
        }
    }
}
