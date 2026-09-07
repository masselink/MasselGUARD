using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Portable tunnel export/import.
    ///
    /// Two independent concerns, both WPF-free so the CLI can share them:
    ///
    ///  1. <b>MasselGUARD settings passthrough</b> — the per-tunnel MasselGUARD
    ///     extras (group, scripts, kill-switch, auto-reconnect, data cap, notes)
    ///     that WireGuard itself knows nothing about. When "include settings" is
    ///     on they are serialized to a <i>single</i> comment line
    ///     (<c># MasselGUARD-Settings: &lt;base64-json&gt;</c>) appended to the
    ///     config. WireGuard's parser ignores unknown comments, so the file stays
    ///     a valid, importable <c>.conf</c> everywhere; MasselGUARD's own importer
    ///     recognises the line and restores the extras.
    ///
    ///  2. <b>Password-based encryption</b> — a shareable, machine-independent
    ///     encrypted container (unlike DPAPI, which is bound to the local user +
    ///     machine and cannot be decrypted elsewhere). AES-256-GCM with a key
    ///     derived from the user's passphrase via PBKDF2-SHA256. The plaintext
    ///     inside is exactly the same "config (+ optional settings comment)" text,
    ///     so decryption feeds straight back into <see cref="ParseImportText"/>.
    ///
    /// Everything here is from the .NET base class library — no extra dependency.
    /// </summary>
    public static class TunnelExportService
    {
        /// <summary>File extension for a password-encrypted export.</summary>
        public const string EncryptedExtension = ".mgconf";

        // ── 1. MasselGUARD settings passthrough ──────────────────────────────

        // Readable one-setting-per-line format: "# MasselGUARD-<Key>: <value>".
        // A value that contains a newline or edge whitespace (embedded multi-line
        // scripts, notes) is base64-encoded and the key gains a "!b64" suffix.
        private const string KeyPrefix = "# MasselGUARD-";
        // Legacy single-line base64-JSON blob — still parsed for backward-compat.
        private const string LegacyPrefix = "# MasselGUARD-Settings: ";

        /// <summary>
        /// The subset of <see cref="StoredTunnel"/> that travels with an export.
        /// Every field is nullable so <see cref="ApplyTo"/> only overwrites what
        /// the export actually carried. Never includes <c>Name</c>/<c>Path</c>/
        /// <c>Source</c> — those are decided at import time.
        /// </summary>
        public sealed class TunnelSettings
        {
            public string? Group                { get; set; }
            public string? Notes                { get; set; }
            public bool?   KillSwitch           { get; set; }
            public bool?   AutoReconnect        { get; set; }
            public int?    RetryCount           { get; set; }
            public int?    RetryDelaySec        { get; set; }
            public int?    DailyCapMB           { get; set; }
            public int?    WeeklyCapMB          { get; set; }
            public int?    MonthlyCapMB         { get; set; }
            public string? PreConnectScript     { get; set; }
            public string? PostConnectScript    { get; set; }
            public string? PreDisconnectScript  { get; set; }
            public string? PostDisconnectScript { get; set; }

            public static TunnelSettings From(StoredTunnel t) => new()
            {
                Group                = t.Group,
                Notes                = t.Notes,
                KillSwitch           = t.KillSwitch,
                AutoReconnect        = t.AutoReconnect,
                RetryCount           = t.RetryCount,
                RetryDelaySec        = t.RetryDelaySec,
                DailyCapMB           = t.DailyCapMB,
                WeeklyCapMB          = t.WeeklyCapMB,
                MonthlyCapMB         = t.MonthlyCapMB,
                PreConnectScript     = t.PreConnectScript,
                PostConnectScript    = t.PostConnectScript,
                PreDisconnectScript  = t.PreDisconnectScript,
                PostDisconnectScript = t.PostDisconnectScript,
            };

            public void ApplyTo(StoredTunnel t)
            {
                if (Group                != null) t.Group                = Group;
                if (Notes                != null) t.Notes                = Notes;
                if (KillSwitch    .HasValue)      t.KillSwitch           = KillSwitch.Value;
                if (AutoReconnect .HasValue)      t.AutoReconnect        = AutoReconnect.Value;
                if (RetryCount    .HasValue)      t.RetryCount           = RetryCount.Value;
                if (RetryDelaySec .HasValue)      t.RetryDelaySec        = RetryDelaySec.Value;
                if (DailyCapMB    .HasValue)      t.DailyCapMB           = DailyCapMB.Value;
                if (WeeklyCapMB   .HasValue)      t.WeeklyCapMB          = WeeklyCapMB.Value;
                if (MonthlyCapMB  .HasValue)      t.MonthlyCapMB         = MonthlyCapMB.Value;
                if (PreConnectScript     != null) t.PreConnectScript     = PreConnectScript;
                if (PostConnectScript    != null) t.PostConnectScript    = PostConnectScript;
                if (PreDisconnectScript  != null) t.PreDisconnectScript  = PreDisconnectScript;
                if (PostDisconnectScript != null) t.PostDisconnectScript = PostDisconnectScript;
            }
        }

        /// <summary>
        /// Build the plaintext export: the WireGuard config, plus — when
        /// <paramref name="includeSettings"/> is set — one trailing
        /// <c># MasselGUARD-Settings:</c> comment line. Line endings are
        /// normalised to LF (WireGuard is happy with LF and it keeps the QR /
        /// hash payload stable).
        /// </summary>
        public static string BuildExportText(string config, StoredTunnel tunnel, bool includeSettings)
            => BuildExportText(config, includeSettings ? TunnelSettings.From(tunnel) : null);

        /// <summary>
        /// Build the plaintext export: the WireGuard config, plus — when
        /// <paramref name="settings"/> is non-null — a trailing block of readable
        /// <c># MasselGUARD-&lt;Key&gt;:</c> comment lines. Line endings are
        /// normalised to LF.
        /// </summary>
        public static string BuildExportText(string config, TunnelSettings? settings)
        {
            var conf = (config ?? "").Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd('\n');
            if (settings == null) return conf + "\n";

            var block = BuildSettingsBlock(settings);
            return string.IsNullOrEmpty(block) ? conf + "\n" : conf + "\n\n" + block;
        }

        /// <summary>Render a settings object as readable comment lines (ends with a newline, or empty).</summary>
        public static string BuildSettingsBlock(TunnelSettings s)
        {
            var sb = new StringBuilder();
            void Str(string key, string? v) { if (!string.IsNullOrEmpty(v)) sb.Append(Emit(key, v!)); }
            void Bool(string key, bool? v)  { if (v.HasValue) sb.Append(Emit(key, v.Value ? "true" : "false")); }
            void Int(string key, int? v)    { if (v.HasValue) sb.Append(Emit(key, v.Value.ToString(CultureInfo.InvariantCulture))); }

            Str("Group",          s.Group);
            Str("Notes",          s.Notes);
            Bool("KillSwitch",    s.KillSwitch);
            Bool("AutoReconnect", s.AutoReconnect);
            Int("RetryCount",     s.RetryCount);
            Int("RetryDelaySec",  s.RetryDelaySec);
            Int("DailyCapMB",     s.DailyCapMB);
            Int("WeeklyCapMB",    s.WeeklyCapMB);
            Int("MonthlyCapMB",   s.MonthlyCapMB);
            Str("PreConnect",     s.PreConnectScript);
            Str("PostConnect",    s.PostConnectScript);
            Str("PreDisconnect",  s.PreDisconnectScript);
            Str("PostDisconnect", s.PostDisconnectScript);
            return sb.ToString();
        }

        // One "# MasselGUARD-<Key>: <value>" line. Values with a newline or edge
        // whitespace are base64-encoded, signalled by a "!b64" suffix on the key.
        private static string Emit(string key, string val)
        {
            bool needsB64 = val.IndexOf('\n') >= 0 || val.IndexOf('\r') >= 0 || val != val.Trim();
            return needsB64
                ? $"{KeyPrefix}{key}!b64: {Convert.ToBase64String(Encoding.UTF8.GetBytes(val))}\n"
                : $"{KeyPrefix}{key}: {val}\n";
        }

        /// <summary>
        /// Split imported text into the clean WireGuard config and any embedded
        /// MasselGUARD settings. Recognises both the readable per-line format and
        /// the legacy single-line base64-JSON blob; malformed lines are dropped.
        /// The recognised comment lines are stripped from the returned config.
        /// </summary>
        public static (string config, TunnelSettings? settings) ParseImportText(string text)
        {
            TunnelSettings? settings = null;
            TunnelSettings Ensure() => settings ??= new TunnelSettings();

            var lines = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            var kept  = new List<string>(lines.Length);

            foreach (var line in lines)
            {
                var trimmed = line.TrimStart();

                // Legacy base64-JSON — must be tested before the generic prefix.
                if (trimmed.StartsWith(LegacyPrefix, StringComparison.Ordinal))
                {
                    var b64 = trimmed.Substring(LegacyPrefix.Length).Trim();
                    try
                    {
                        var json = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                        settings = JsonSerializer.Deserialize<TunnelSettings>(json) ?? settings;
                    }
                    catch { }
                    continue;
                }

                if (trimmed.StartsWith(KeyPrefix, StringComparison.Ordinal))
                {
                    var rest  = trimmed.Substring(KeyPrefix.Length);
                    var colon = rest.IndexOf(':');
                    if (colon > 0)
                    {
                        var key = rest.Substring(0, colon).Trim();
                        var val = rest.Substring(colon + 1).Trim();
                        bool b64 = key.EndsWith("!b64", StringComparison.Ordinal);
                        if (b64) key = key.Substring(0, key.Length - 4);
                        if (b64)
                        {
                            try { val = Encoding.UTF8.GetString(Convert.FromBase64String(val)); }
                            catch { continue; }
                        }
                        ApplyKey(Ensure(), key, val);
                    }
                    continue;
                }

                kept.Add(line);
            }

            var conf = string.Join("\n", kept).TrimEnd('\n');
            if (conf.Length > 0) conf += "\n";
            return (conf, settings);
        }

        private static void ApplyKey(TunnelSettings s, string key, string val)
        {
            switch (key)
            {
                case "Group":          s.Group = val; break;
                case "Notes":          s.Notes = val; break;
                case "KillSwitch":     s.KillSwitch    = ParseBool(val); break;
                case "AutoReconnect":  s.AutoReconnect = ParseBool(val); break;
                case "RetryCount":     if (int.TryParse(val, out var rc)) s.RetryCount    = rc; break;
                case "RetryDelaySec":  if (int.TryParse(val, out var rd)) s.RetryDelaySec = rd; break;
                case "DailyCapMB":     if (int.TryParse(val, out var dc)) s.DailyCapMB    = dc; break;
                case "WeeklyCapMB":    if (int.TryParse(val, out var wc)) s.WeeklyCapMB   = wc; break;
                case "MonthlyCapMB":   if (int.TryParse(val, out var mc)) s.MonthlyCapMB  = mc; break;
                case "PreConnect":     s.PreConnectScript     = val; break;
                case "PostConnect":    s.PostConnectScript    = val; break;
                case "PreDisconnect":  s.PreDisconnectScript  = val; break;
                case "PostDisconnect": s.PostDisconnectScript = val; break;
            }
        }

        private static bool ParseBool(string v)
        {
            v = v.Trim();
            return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
        }

        // ── 2. Password-based AES-256-GCM encryption ─────────────────────────
        //
        // Container layout (all binary, no base64):
        //   magic "MGENC1" (6) | version (1) | salt (16) | nonce (12) | tag (16) | ciphertext (rest)

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MGENC1");
        private const byte FormatVersion = 1;
        private const int  SaltLen    = 16;
        private const int  NonceLen   = 12;      // AES-GCM standard nonce size
        private const int  TagLen     = 16;      // AES-GCM standard tag size
        private const int  KeyLen     = 32;      // AES-256
        private const int  Iterations = 210_000; // PBKDF2-SHA256 work factor

        /// <summary>True if <paramref name="data"/> begins with the MasselGUARD encrypted-config magic.</summary>
        public static bool IsEncrypted(byte[] data)
        {
            if (data == null || data.Length < Magic.Length) return false;
            for (int i = 0; i < Magic.Length; i++)
                if (data[i] != Magic[i]) return false;
            return true;
        }

        /// <summary>Encrypt <paramref name="plaintext"/> under <paramref name="password"/> into the container format.</summary>
        public static byte[] Encrypt(string plaintext, string password)
        {
            var salt  = RandomNumberGenerator.GetBytes(SaltLen);
            var nonce = RandomNumberGenerator.GetBytes(NonceLen);
            var key   = DeriveKey(password, salt);

            var plain  = Encoding.UTF8.GetBytes(plaintext);
            var cipher = new byte[plain.Length];
            var tag    = new byte[TagLen];
            try
            {
                using var gcm = new AesGcm(key, TagLen);
                gcm.Encrypt(nonce, plain, cipher, tag);
            }
            finally { CryptographicOperations.ZeroMemory(key); }

            using var ms = new MemoryStream(Magic.Length + 1 + SaltLen + NonceLen + TagLen + cipher.Length);
            ms.Write(Magic, 0, Magic.Length);
            ms.WriteByte(FormatVersion);
            ms.Write(salt,   0, salt.Length);
            ms.Write(nonce,  0, nonce.Length);
            ms.Write(tag,    0, tag.Length);
            ms.Write(cipher, 0, cipher.Length);
            return ms.ToArray();
        }

        /// <summary>
        /// Decrypt a container produced by <see cref="Encrypt"/>. Throws
        /// <see cref="CryptographicException"/> on a wrong password or tampered
        /// data, and <see cref="FormatException"/> if it is not our format.
        /// </summary>
        public static string Decrypt(byte[] data, string password)
        {
            if (!IsEncrypted(data))
                throw new FormatException("Not a MasselGUARD encrypted config.");

            int off = Magic.Length;
            byte ver = data[off++];
            if (ver != FormatVersion)
                throw new FormatException($"Unsupported encrypted config version {ver}.");
            if (data.Length < off + SaltLen + NonceLen + TagLen)
                throw new FormatException("Encrypted config is truncated.");

            var salt   = new byte[SaltLen];  Array.Copy(data, off, salt,  0, SaltLen);  off += SaltLen;
            var nonce  = new byte[NonceLen]; Array.Copy(data, off, nonce, 0, NonceLen); off += NonceLen;
            var tag    = new byte[TagLen];   Array.Copy(data, off, tag,   0, TagLen);   off += TagLen;
            var cipher = new byte[data.Length - off]; Array.Copy(data, off, cipher, 0, cipher.Length);

            var key   = DeriveKey(password, salt);
            var plain = new byte[cipher.Length];
            try
            {
                using var gcm = new AesGcm(key, TagLen);
                gcm.Decrypt(nonce, cipher, tag, plain);
            }
            catch (CryptographicException)
            {
                throw new CryptographicException("Wrong password or corrupted file.");
            }
            finally { CryptographicOperations.ZeroMemory(key); }

            return Encoding.UTF8.GetString(plain);
        }

        private static byte[] DeriveKey(string password, byte[] salt) =>
            Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password ?? ""), salt,
                Iterations, HashAlgorithmName.SHA256, KeyLen);
    }
}
