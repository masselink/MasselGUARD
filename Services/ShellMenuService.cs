using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Adds a "DNS bypass" cascade to Windows Explorer's right-click menu (desktop / folder background and
    /// folders): <c>For 10 seconds</c>, <c>1 minute</c>, <c>5 minutes</c>, <c>15 minutes</c> and <c>Stop now</c>.
    /// <para>Each entry runs <c>MasselGUARDcli.exe dns bypass &lt;seconds&gt;</c>, which talks to the running window
    /// over <see cref="CommandPipe"/>; the CLI needs no elevation, so there is no UAC prompt per click.</para>
    /// Registered per user (HKCU\Software\Classes), no administrator rights needed, removed again by
    /// <see cref="Unregister"/>. On Windows 11 a classic verb like this appears under "Show more options"
    /// (Shift+F10); a top-level entry in the new menu would need a packaged shell extension.
    /// GUI-only.
    /// </summary>
    public static class ShellMenuService
    {
        private const string VerbName   = "MasselGUARD.DnsBypass";
        private const string SubKeyName = "MasselGUARD.DnsBypass";   // holds the cascade's sub-commands

        // Where the verb is shown: the empty background of a folder / the desktop, and on a folder itself.
        private static readonly string[] Parents =
        {
            @"Software\Classes\Directory\Background\shell\" + VerbName,
            @"Software\Classes\Directory\shell\" + VerbName,
        };

        public static bool IsRegistered
        {
            get
            {
                try { using var k = Registry.CurrentUser.OpenSubKey(Parents[0]); return k != null; }
                catch { return false; }
            }
        }

        /// <summary>The CLI path currently registered (to notice a moved install), or null.</summary>
        public static string? RegisteredCliPath()
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{SubKeyName}\shell\02_60\command");
                var cmd = k?.GetValue(null) as string;
                if (string.IsNullOrEmpty(cmd) || cmd[0] != '"') return null;
                int end = cmd.IndexOf('"', 1);
                return end > 1 ? cmd.Substring(1, end - 1) : null;
            }
            catch { return null; }
        }

        /// <summary>Writes (or refreshes) the menu entries. <paramref name="label"/> maps a language key to its text.</summary>
        public static void Register(string cliPath, string iconExePath, Func<string, string> label)
        {
            Unregister();   // start clean so a changed install path or language never leaves stale entries

            foreach (var parent in Parents)
            {
                using var k = Registry.CurrentUser.CreateSubKey(parent);
                k.SetValue("MUIVerb", label("ShellMenuTitle"));
                k.SetValue("Icon", $"\"{iconExePath}\",0");
                k.SetValue("ExtendedSubCommandsKey", SubKeyName);
            }

            var items = new List<(string key, string text, string args, bool separator)>();
            foreach (var s in Models.TempOverride.PresetSeconds)
                items.Add(($"{items.Count + 1:00}_{s}", label(Models.TempOverride.LabelKey(s)), $"dns bypass {s}", false));
            items.Add(("99_stop", label("DnsTempStop"), "dns bypass stop", true));

            foreach (var (key, text, args, sep) in items)
            {
                using var verb = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{SubKeyName}\shell\{key}");
                verb.SetValue("MUIVerb", text);
                if (sep) verb.SetValue("CommandFlags", 0x20, RegistryValueKind.DWord);   // separator above
                using var cmd = verb.CreateSubKey("command");
                cmd.SetValue(null, $"\"{cliPath}\" {args}");
            }
        }

        public static void Unregister()
        {
            foreach (var parent in Parents)
                try { Registry.CurrentUser.DeleteSubKeyTree(parent, throwOnMissingSubKey: false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{SubKeyName}", throwOnMissingSubKey: false); } catch { }
        }
    }
}
