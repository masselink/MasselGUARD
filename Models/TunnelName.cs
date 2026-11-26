using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MasselGUARD.Models
{
    /// <summary>
    /// Tunnel names. tunnel.dll takes the tunnel name from the config FILE name and refuses anything outside
    /// WireGuard's own rule (A-Z, a-z, 0-9 and _ = + . -, at most 32 characters, no leading or trailing dot,
    /// no Windows device name): it then exits before it reaches the service manager, and Windows reports
    /// "cannot start service". The app's names may contain spaces: everything that talks to Windows about a
    /// tunnel uses <see cref="Wire"/> (spaces become "_"), and <see cref="IsValid"/> applies WireGuard's rule
    /// to that form.
    /// </summary>
    public static class TunnelName
    {
        public const int MaxLength = 32;

        // \z, not $: "$" would also accept a trailing newline.
        private static readonly Regex Allowed = new(@"^[a-zA-Z0-9_=+.\-]{1,32}\z", RegexOptions.Compiled);

        private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL",
          "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
          "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

        /// <summary>The name Windows and tunnel.dll see (config file, service, adapter, pipe): spaces become "_".</summary>
        public static string Wire(string name) => name.Replace(' ', '_');

        /// <summary>May this be used as a tunnel name? Spaces are allowed (not at the start or end); with them
        /// replaced the name must pass WireGuard's rule.</summary>
        public static bool IsValid(string? name)
        {
            if (string.IsNullOrEmpty(name) || name[0] == ' ' || name[^1] == ' ') return false;
            var wire = Wire(name);
            return Allowed.IsMatch(wire) && wire[0] != '.' && wire[^1] != '.' && !Reserved.Contains(wire.Split('.')[0]);
        }

        private const string StampPrefix = "MasselGUARD Tunnel: ";

        /// <summary>The service description MasselGUARD writes on a tunnel service: it records the app's own name,
        /// which the service name cannot ("a b" and "a_b" share one).</summary>
        public static string ServiceStamp(string name) => StampPrefix + name;

        /// <summary>The tunnel a service belongs to when that is NOT <paramref name="name"/>, else null. A service
        /// without our stamp gives null: there is nothing to tell them apart by.</summary>
        public static string? OtherOwner(string? serviceDescription, string name)
        {
            if (serviceDescription == null || !serviceDescription.StartsWith(StampPrefix, StringComparison.Ordinal)) return null;
            var owner = serviceDescription[StampPrefix.Length..];
            return string.Equals(owner, name, StringComparison.OrdinalIgnoreCase) ? null : owner;
        }

        /// <summary>Two names that would end up as the same service and adapter.</summary>
        public static bool Clashes(string a, string b) =>
            string.Equals(Wire(a), Wire(b), StringComparison.OrdinalIgnoreCase);

        /// <summary>The closest valid name: accents are dropped, every other refused character becomes "_".</summary>
        public static string Sanitize(string? name)
        {
            var sb = new StringBuilder();
            foreach (var c in (name ?? "").Trim().Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(char.IsAsciiLetterOrDigit(c) || c is ' ' or '_' or '=' or '+' or '.' or '-' ? c : '_');
            }
            var s = sb.ToString();
            if (s.Length > MaxLength) s = s[..MaxLength];
            s = s.Trim('.', ' ');
            if (s.Length == 0) return "tunnel";
            if (Reserved.Contains(s.Split('.')[0])) s = "_" + (s.Length >= MaxLength ? s[..(MaxLength - 1)] : s);
            return s;
        }

        /// <summary><see cref="Sanitize"/>, with "_2", "_3" ... added when it clashes with a name in <paramref name="taken"/>.</summary>
        public static string Unique(string? name, IEnumerable<string> taken)
        {
            var list = taken.ToList();
            var first = Sanitize(name);
            var candidate = first;
            for (int i = 2; list.Any(t => Clashes(t, candidate)); i++)
            {
                var tail = "_" + i;
                candidate = (first.Length + tail.Length > MaxLength ? first[..(MaxLength - tail.Length)] : first) + tail;
            }
            return candidate;
        }
    }
}
