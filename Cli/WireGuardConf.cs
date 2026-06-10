using System;
using System.Collections.Generic;
using System.Text;

namespace MasselGUARD.Cli
{
    /// <summary>
    /// Helpers for building and patching WireGuard .conf text.
    /// Used by CLI rawconnect (build from scratch) and connect overrides (patch existing).
    ///
    /// WireGuard .conf format:
    ///   [Interface]
    ///   PrivateKey = &lt;base64&gt;
    ///   Address    = 10.0.0.2/32
    ///   DNS        = 1.1.1.1
    ///
    ///   [Peer]
    ///   PublicKey       = &lt;base64&gt;
    ///   PresharedKey    = &lt;base64&gt;   (optional)
    ///   Endpoint        = host:port
    ///   AllowedIPs      = 0.0.0.0/0, ::/0
    ///   PersistentKeepalive = 25      (optional)
    /// </summary>
    internal static class WireGuardConf
    {
        // ── Known field → section mapping (case-insensitive key) ─────────────
        private static readonly HashSet<string> InterfaceFields =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "PrivateKey", "Address", "DNS", "ListenPort",
                "Table", "MTU", "PreUp", "PostUp", "PreDown", "PostDown",
            };

        private static readonly HashSet<string> PeerFields =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "PublicKey", "PresharedKey", "Endpoint", "AllowedIPs",
                "PersistentKeepalive",
            };

        // ── Build a .conf from scratch ────────────────────────────────────────

        /// <summary>
        /// Builds a complete WireGuard .conf text from the supplied parameters.
        /// Only non-null/non-empty values are written.
        /// </summary>
        public static string Build(
            string  privateKey,
            string  publicKey,
            string  endpoint,
            string? address      = null,
            string? dns          = null,
            string? allowedIPs   = null,
            string? presharedKey = null,
            int?    keepalive    = null)
        {
            var sb = new StringBuilder();

            sb.AppendLine("[Interface]");
            sb.AppendLine($"PrivateKey = {privateKey.Trim()}");
            if (!string.IsNullOrWhiteSpace(address))
                sb.AppendLine($"Address = {address.Trim()}");
            if (!string.IsNullOrWhiteSpace(dns))
                sb.AppendLine($"DNS = {dns.Trim()}");

            sb.AppendLine();

            sb.AppendLine("[Peer]");
            sb.AppendLine($"PublicKey = {publicKey.Trim()}");
            if (!string.IsNullOrWhiteSpace(presharedKey))
                sb.AppendLine($"PresharedKey = {presharedKey.Trim()}");
            sb.AppendLine($"Endpoint = {endpoint.Trim()}");
            sb.AppendLine($"AllowedIPs = {(string.IsNullOrWhiteSpace(allowedIPs) ? "0.0.0.0/0, ::/0" : allowedIPs.Trim())}");
            if (keepalive.HasValue)
                sb.AppendLine($"PersistentKeepalive = {keepalive.Value}");

            return sb.ToString();
        }

        // ── Patch fields in an existing .conf ─────────────────────────────────

        /// <summary>
        /// Patches specific key=value pairs in an existing .conf string.
        /// Keys that already exist are replaced in-place.
        /// Keys that don't exist are appended to the correct section.
        /// Keys not in either known set are appended to [Interface].
        /// </summary>
        public static string Patch(string conf, Dictionary<string, string> overrides)
        {
            if (overrides.Count == 0) return conf;

            var lines      = new List<string>(conf.Split('\n'));
            var remaining  = new Dictionary<string, string>(overrides, StringComparer.OrdinalIgnoreCase);

            // First pass: replace existing lines
            for (int i = 0; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimStart();
                var eqPos   = trimmed.IndexOf('=');
                if (eqPos <= 0) continue;

                var key = trimmed[..eqPos].Trim();
                if (remaining.TryGetValue(key, out var newVal))
                {
                    lines[i] = $"{key} = {newVal}";
                    remaining.Remove(key);
                    if (remaining.Count == 0) break;
                }
            }

            // Second pass: append any fields that didn't exist yet
            if (remaining.Count > 0)
            {
                // Find section boundaries
                int interfaceLine = -1, peerLine = -1;
                for (int i = 0; i < lines.Count; i++)
                {
                    var t = lines[i].Trim();
                    if (t.Equals("[Interface]", StringComparison.OrdinalIgnoreCase)) interfaceLine = i;
                    if (t.Equals("[Peer]",      StringComparison.OrdinalIgnoreCase)) peerLine = i;
                }

                foreach (var kv in remaining)
                {
                    bool isPeer = PeerFields.Contains(kv.Key);
                    int  insertAfter;

                    if (isPeer && peerLine >= 0)
                    {
                        // Insert at end of [Peer] section (before next empty line or end of file)
                        insertAfter = FindSectionEnd(lines, peerLine);
                    }
                    else if (!isPeer && interfaceLine >= 0)
                    {
                        insertAfter = FindSectionEnd(lines, interfaceLine);
                    }
                    else
                    {
                        // Append to end
                        lines.Add($"{kv.Key} = {kv.Value}");
                        continue;
                    }

                    lines.Insert(insertAfter, $"{kv.Key} = {kv.Value}");
                }
            }

            return string.Join('\n', lines);
        }

        // ── Read a private key from --privkey or --privkeyfile ────────────────

        /// <summary>
        /// Resolves a private-key argument.
        /// If value starts with '@', treats the rest as a file path and reads from it.
        /// Otherwise returns the value directly (and warns that it appears in ps/logs).
        /// Returns (key, warning) where warning is non-null when the key was inline.
        /// </summary>
        public static (string key, string? warning) ResolveKey(string value)
        {
            if (value.StartsWith("@", StringComparison.Ordinal))
            {
                var path = value[1..].Trim();
                if (!System.IO.File.Exists(path))
                    throw new System.IO.FileNotFoundException($"Key file not found: {path}");
                return (System.IO.File.ReadAllText(path).Trim(), null);
            }
            return (value,
                "⚠  WARNING: private key supplied on the command line is visible in process " +
                "listings and shell history. Use --privkeyfile <path> for safer key loading.");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static int FindSectionEnd(List<string> lines, int sectionStart)
        {
            for (int i = sectionStart + 1; i < lines.Count; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith("[")) return i;       // next section — insert before it
                if (string.IsNullOrEmpty(t)) return i; // blank line — insert before it
            }
            return lines.Count; // end of file
        }

        // ── Pre-flight validation ─────────────────────────────────────────────

        /// <summary>
        /// A validation failure: a short main <see cref="Message"/> (shown in accent colour)
        /// and an optional longer <see cref="Detail"/> with fix suggestions (shown in grey).
        /// </summary>
        public sealed record ValidationError(string Message, string? Detail = null);

        /// <summary>
        /// Validates a plaintext WireGuard config before writing the temp file.
        /// Returns null on success, or a <see cref="ValidationError"/> describing what failed.
        /// </summary>
        public static ValidationError? Validate(string conf)
        {
            if (string.IsNullOrWhiteSpace(conf))
                return new ValidationError("Config is empty.");

            // Strip BOM if present
            conf = conf.TrimStart('﻿');

            var lines        = conf.Split('\n');
            var firstContent = Array.Find(lines, l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? "";

            if (!firstContent.Equals("[Interface]", StringComparison.OrdinalIgnoreCase))
                return new ValidationError(
                    $"Config must start with [Interface] — found: '{firstContent}'.",
                    "Ensure there are no blank lines or BOM characters before the header.");

            bool hasInterface  = false;
            bool hasPrivateKey = false;
            bool hasAddress    = false;
            bool hasPeer       = false;
            bool hasPublicKey  = false;
            bool hasEndpoint   = false;
            string section     = "";

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                if (line.StartsWith('['))
                {
                    section = line.ToLowerInvariant();
                    if (section == "[interface]") hasInterface  = true;
                    if (section == "[peer]")      hasPeer       = true;
                    continue;
                }

                if (line.StartsWith('#') || !line.Contains('=')) continue;

                var eq  = line.IndexOf('=');
                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim();
                if (string.IsNullOrEmpty(val)) continue;

                // ── [Interface] fields ────────────────────────────────────────
                if (section == "[interface]")
                {
                    if (key.Equals("PrivateKey", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPrivateKey = true;
                        var err = ValidateBase64Key(key, val, 32);
                        if (err != null) return err;
                    }
                    else if (key.Equals("Address", StringComparison.OrdinalIgnoreCase))
                    {
                        hasAddress = true;
                        var err = ValidateCidrList(key, val);
                        if (err != null) return err;
                    }
                    else if (key.Equals("DNS", StringComparison.OrdinalIgnoreCase))
                    {
                        var err = ValidateIpList(key, val);
                        if (err != null) return err;
                    }
                    else if (key.Equals("MTU", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!int.TryParse(val, out var mtu) || mtu < 576 || mtu > 9000)
                            return new ValidationError(
                                $"MTU = '{val}' is invalid. Must be 576–9000.",
                                "Typical WireGuard MTU is 1420; use 1280 for IPv6 compatibility.");
                    }
                    else if (key.Equals("ListenPort", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!int.TryParse(val, out var port) || port < 1 || port > 65535)
                            return new ValidationError(
                                $"ListenPort = '{val}' is invalid.",
                                "Must be a number between 1 and 65535.");
                    }
                }

                // ── [Peer] fields ─────────────────────────────────────────────
                else if (section == "[peer]")
                {
                    if (key.Equals("PublicKey", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPublicKey = true;
                        var err = ValidateBase64Key(key, val, 32);
                        if (err != null) return err;
                    }
                    else if (key.Equals("PresharedKey", StringComparison.OrdinalIgnoreCase))
                    {
                        var err = ValidateBase64Key(key, val, 32);
                        if (err != null) return err;
                    }
                    else if (key.Equals("AllowedIPs", StringComparison.OrdinalIgnoreCase))
                    {
                        var err = ValidateCidrList(key, val);
                        if (err != null) return err;
                    }
                    else if (key.Equals("Endpoint", StringComparison.OrdinalIgnoreCase))
                    {
                        hasEndpoint = true;
                        var err = ValidateEndpoint(val);
                        if (err != null) return err;
                    }
                    else if (key.Equals("PersistentKeepalive", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!int.TryParse(val, out var ka) || ka < 1 || ka > 65535)
                            return new ValidationError(
                                $"PersistentKeepalive = '{val}' is invalid.",
                                "Must be 1–65535 (seconds). Typical value: 25.");
                    }
                }
            }

            // Required field checks
            if (!hasInterface)  return new ValidationError("Config is missing the [Interface] section.");
            if (!hasPrivateKey) return new ValidationError(
                "Missing PrivateKey in [Interface].",
                "Generate a key pair via Settings → Keypairs.");
            if (!hasAddress)    return new ValidationError(
                "Missing Address in [Interface].",
                "Example: Address = 10.0.0.2/32");
            if (!hasPeer)       return new ValidationError("Config is missing at least one [Peer] section.");
            if (!hasPublicKey)  return new ValidationError("Missing PublicKey in [Peer].");
            if (!hasEndpoint)   return new ValidationError(
                "Missing Endpoint in [Peer].",
                "Example: Endpoint = vpn.example.com:51820");

            return null; // all good
        }

        // ── Validation helpers ────────────────────────────────────────────────

        private static ValidationError? ValidateCidrList(string fieldName, string value)
        {
            foreach (var part in value.Split(','))
            {
                var cidr = part.Trim();
                if (string.IsNullOrEmpty(cidr)) continue;

                var slash = cidr.IndexOf('/');
                if (slash < 0)
                {
                    if (!System.Net.IPAddress.TryParse(cidr, out _))
                        return new ValidationError($"{fieldName}: '{cidr}' is not a valid IP address.");
                    continue;
                }

                var ipPart     = cidr[..slash];
                var prefixPart = cidr[(slash + 1)..];

                if (!System.Net.IPAddress.TryParse(ipPart, out var ip))
                {
                    if (ipPart.Contains(':') && !ipPart.Contains("::"))
                    {
                        var groups = ipPart.Split(':').Length;
                        if (groups < 8)
                        {
                            var suggestion = SuggestIpv6Fix(ipPart, prefixPart);
                            return new ValidationError(
                                $"{fieldName}: '{cidr}' — '{ipPart}' is not valid ({groups} of 8 IPv6 groups).",
                                $"Use '::' to fill missing groups." +
                                (suggestion != null ? $" Did you mean: {suggestion}?" : ""));
                        }
                    }
                    return new ValidationError($"{fieldName}: '{cidr}' — '{ipPart}' is not a valid IP address.");
                }

                if (!int.TryParse(prefixPart, out var prefix))
                    return new ValidationError($"{fieldName}: '{cidr}' — '/{prefixPart}' is not a valid prefix length.");

                int maxPrefix = ip.AddressFamily ==
                    System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
                if (prefix < 0 || prefix > maxPrefix)
                    return new ValidationError(
                        $"{fieldName}: '{cidr}' — /{prefix} is out of range.",
                        $"Valid range: 0–{maxPrefix} for {(maxPrefix == 128 ? "IPv6" : "IPv4")}.");
            }
            return null;
        }

        private static ValidationError? ValidateIpList(string fieldName, string value)
        {
            foreach (var part in value.Split(','))
            {
                var addr = part.Trim();
                if (string.IsNullOrEmpty(addr)) continue;
                if (!System.Net.IPAddress.TryParse(addr, out _))
                    return new ValidationError($"{fieldName}: '{addr}' is not a valid IP address.");
            }
            return null;
        }

        private static ValidationError? ValidateBase64Key(string fieldName, string value, int expectedBytes)
        {
            if (value.Length != 44)
                return new ValidationError(
                    $"{fieldName} has {value.Length} characters — expected 44.",
                    "WireGuard keys are base64-encoded 32-byte values. Generate a key pair via Settings → Keypairs.");
            try { Convert.FromBase64String(value); }
            catch
            {
                return new ValidationError(
                    $"{fieldName} is not valid base64.",
                    "Generate a valid key pair via Settings → Keypairs.");
            }
            return null;
        }

        private static ValidationError? ValidateEndpoint(string value)
        {
            var lastColon = value.LastIndexOf(':');
            if (lastColon < 0)
                return new ValidationError(
                    $"Endpoint '{value}' is invalid — expected host:port format.",
                    "Example: vpn.example.com:51820 or 192.0.2.1:51820");

            var portStr = value[(lastColon + 1)..];
            if (!int.TryParse(portStr, out var port) || port < 1 || port > 65535)
                return new ValidationError(
                    $"Endpoint '{value}' — port '{portStr}' is invalid.",
                    "Port must be a number between 1 and 65535.");

            var host = value[..lastColon].Trim('[', ']');
            if (string.IsNullOrWhiteSpace(host))
                return new ValidationError($"Endpoint '{value}' is missing the host part.");

            return null;
        }

        /// <summary>
        /// Tries to suggest a corrected IPv6 address by inserting '::' before the last group.
        /// E.g. 'fd00:dead:beef:4' → 'fd00:dead:beef::4'.
        /// </summary>
        private static string? SuggestIpv6Fix(string ipPart, string prefixPart)
        {
            var groups = ipPart.Split(':');
            if (groups.Length < 2) return null;
            // Insert :: between everything before the last group and the last group
            var prefix   = string.Join(":", groups[..^1]);
            var lastGroup = groups[^1];
            var candidate = $"{prefix}::{lastGroup}/{prefixPart}";
            // Verify the suggestion is actually valid
            if (System.Net.IPAddress.TryParse($"{prefix}::{lastGroup}", out _))
                return candidate;
            return null;
        }
    }
}
