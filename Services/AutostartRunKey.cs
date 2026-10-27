using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Per-user "start with Windows" through <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> (docs/
    /// ServiceBackend-Design.md, phase 4). Needs no administrator rights, and the app then starts unelevated:
    /// with the MasselGUARD service the privileged work is done by the service, so the elevated scheduled task
    /// (RunLevel Highest, needs admin to register) is no longer required. Without the service the old
    /// scheduled task stays the mechanism. WPF-free and CLI-shared; the key path is injectable for the self-test.
    /// </summary>
    public static class AutostartRunKey
    {
        public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string ValueName = "MasselGUARD";

        /// <summary>The command stored in the Run value: the quoted exe path.</summary>
        public static string BuildValue(string exePath) => "\"" + exePath.Trim().Trim('"') + "\"";

        public static bool IsEnabled(string keyPath = DefaultKeyPath)
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(keyPath); return k?.GetValue(ValueName) is string s && s.Length > 0; }
            catch { return false; }
        }

        public static string? CurrentValue(string keyPath = DefaultKeyPath)
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(keyPath); return k?.GetValue(ValueName) as string; }
            catch { return null; }
        }

        public static bool Enable(string exePath, string keyPath = DefaultKeyPath)
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(keyPath);
                k.SetValue(ValueName, BuildValue(exePath), RegistryValueKind.String);
                return true;
            }
            catch { return false; }
        }

        public static bool Disable(string keyPath = DefaultKeyPath)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
                k?.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }
            catch { return false; }
        }

        public static (int pass, int fail, List<string> failures) RunSelfTest()
        {
            int pass = 0; var fails = new List<string>();
            void Check(bool ok, string what) { if (ok) pass++; else fails.Add(what); }

            Check(BuildValue(@"C:\Program Files\MasselGUARD\MasselGUARD.exe") == "\"C:\\Program Files\\MasselGUARD\\MasselGUARD.exe\"", "run: value is the quoted path");
            Check(BuildValue("\"C:\\x\\a.exe\"") == "\"C:\\x\\a.exe\"", "run: an already quoted path is not quoted twice");

            // Real registry round trip on a private key (never the real Run key).
            var key = @"Software\MasselGUARD.SelfTest\" + Guid.NewGuid().ToString("N");
            try
            {
                Check(!IsEnabled(key) && CurrentValue(key) == null, "run: absent at start");
                Check(Enable(@"C:\a\MasselGUARD.exe", key) && IsEnabled(key), "run: enable");
                Check(CurrentValue(key) == "\"C:\\a\\MasselGUARD.exe\"", "run: value stored");
                Check(Enable(@"C:\b\MasselGUARD.exe", key) && CurrentValue(key) == "\"C:\\b\\MasselGUARD.exe\"", "run: enable again replaces the path");
                Check(Disable(key) && !IsEnabled(key), "run: disable");
                Check(Disable(key), "run: disabling twice is fine");
            }
            catch (Exception ex) { fails.Add("run threw: " + ex.Message); }
            finally { try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\MasselGUARD.SelfTest", false); } catch { } }
            return (pass, fails.Count, fails);
        }
    }
}
