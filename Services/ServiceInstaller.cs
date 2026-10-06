using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.ServiceProcess;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Installs / removes / queries the privileged back-end service (<c>MasselGUARDsvc</c>) through sc.exe.
    /// Needs administrator rights (the CLI elevates for the <c>service</c> command). WPF-free and CLI-shared.
    /// </summary>
    public static class ServiceInstaller
    {
        public const string Name = "MasselGUARDsvc";

        /// <summary>A LocalSystem service cannot read a per-user cloud-synced folder, and a service pointing at a
        /// folder that later moves breaks, so the exe must sit in a normal local folder.</summary>
        public static string? CheckExePath(string exePath)
        {
            if (!File.Exists(exePath)) return $"Executable not found: {exePath}";
            foreach (var v in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
            {
                var root = Environment.GetEnvironmentVariable(v);
                if (!string.IsNullOrEmpty(root) && exePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    return "The service cannot run from a cloud-synced (OneDrive) folder. Copy MasselGUARD to a local folder such as C:\\MasselGUARD or Program Files first.";
            }
            if (exePath.StartsWith(@"\\")) return "The service cannot run from a network path.";
            return null;
        }

        public static bool IsInstalled() => Status() != null;

        public static ServiceControllerStatus? Status()
        {
            try { using var sc = new ServiceController(Name); return sc.Status; }
            catch { return null; }
        }

        /// <summary>Process id of the running service (from <c>sc queryex</c>, works without elevation); 0 when not running.</summary>
        public static int ServicePid()
        {
            var (ok, o) = Sc($"queryex {Name}");
            if (!ok) return 0;
            foreach (var line in o.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("PID", StringComparison.OrdinalIgnoreCase))
                {
                    var v = t[(t.IndexOf(':') + 1)..].Trim();
                    return int.TryParse(v, out var pid) ? pid : 0;
                }
            }
            return 0;
        }

        public static (bool ok, string message) Install(string exePath, IEnumerable<string>? allowUserSids = null)
        {
            var bad = CheckExePath(exePath);
            if (bad != null) return (false, bad);

            // Who may call the service besides Administrators: the installing user (and any extras).
            var sids = new List<string>();
            var me = WindowsIdentity.GetCurrent().User?.Value;
            if (me != null) sids.Add(me);
            if (allowUserSids != null) sids.AddRange(allowUserSids);
            WriteAllowedUsers(sids);

            if (IsInstalled()) Uninstall();   // replace a stale registration (e.g. moved install)

            var (ok, msg) = Sc($"create {Name} binPath= \"\\\"{exePath}\\\" /svc\" start= auto obj= LocalSystem DisplayName= \"MasselGUARD Service\"");
            if (!ok) return (false, "sc create failed: " + msg);
            Sc($"description {Name} \"MasselGUARD privileged back-end (tunnels, DNS, kill switch).\"");
            Sc($"failure {Name} reset= 86400 actions= restart/5000/restart/5000/restart/30000");
            var (sok, smsg) = Sc($"start {Name}");
            return sok ? (true, "Service installed and started.") : (false, "Service installed but did not start: " + smsg);
        }

        public static (bool ok, string message) Uninstall()
        {
            if (!IsInstalled()) return (true, "Service is not installed.");
            Sc($"stop {Name}");
            for (int i = 0; i < 20 && Status() is { } st && st != ServiceControllerStatus.Stopped; i++) System.Threading.Thread.Sleep(250);
            var (ok, msg) = Sc($"delete {Name}");
            return ok ? (true, "Service removed.") : (false, "sc delete failed: " + msg);
        }

        // ── Elevation-aware entry points for the GUI ─────────────────────────────

        public static bool IsElevated()
        {
            try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        /// <summary>Stops the service when it runs (so its exe can be replaced). Needs admin rights.</summary>
        public static void Stop()
        {
            if (Status() is { } st && st != ServiceControllerStatus.Stopped)
            {
                Sc($"stop {Name}");
                for (int i = 0; i < 20 && Status() is { } s2 && s2 != ServiceControllerStatus.Stopped; i++) System.Threading.Thread.Sleep(250);
            }
        }

        public static void Start() { if (Status() == ServiceControllerStatus.Stopped) Sc($"start {Name}"); }

        /// <summary>Installs from the GUI: directly when this process is elevated, otherwise by starting
        /// <c>MasselGUARDcli.exe service install</c> with a UAC prompt (the calling user is recorded as allowed).</summary>
        public static (bool ok, string message) InstallAuto(string exePath)
        {
            if (IsElevated()) return Install(exePath);
            var user = Environment.UserDomainName + "\\" + Environment.UserName;
            return RunElevatedCli(exePath, $"service install --user \"{user}\"", () => Status() == ServiceControllerStatus.Running, "Service installed and started.");
        }

        public static (bool ok, string message) UninstallAuto(string exePath)
        {
            if (!IsInstalled()) return (true, "Service is not installed.");
            if (IsElevated()) return Uninstall();
            return RunElevatedCli(exePath, "service uninstall", () => !IsInstalled(), "Service removed.");
        }

        private static (bool ok, string message) RunElevatedCli(string guiExePath, string args, Func<bool> verify, string okMessage)
        {
            var cli = Path.Combine(Path.GetDirectoryName(guiExePath) ?? "", "MasselGUARDcli.exe");
            if (!File.Exists(cli)) return (false, "MasselGUARDcli.exe not found next to MasselGUARD.exe.");
            try
            {
                using var p = Process.Start(new ProcessStartInfo(cli, args)
                { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden })!;
                p.WaitForExit(60000);
            }
            catch (System.ComponentModel.Win32Exception) { return (false, "Administrator approval was cancelled."); }
            catch (Exception ex) { return (false, ex.Message); }
            return verify() ? (true, okMessage) : (false, "The elevated step did not complete. Run 'MasselGUARDcli service status' in an administrator terminal for details.");
        }

        /// <summary>The SID of a user name ("DOMAIN\user" or "user"), for <c>--user</c>.</summary>
        public static string? ResolveSid(string account)
        {
            try { return new NTAccount(account).Translate(typeof(SecurityIdentifier)).Value; }
            catch { return null; }
        }

        private static void WriteAllowedUsers(List<string> sids)
        {
            var dir = Path.GetDirectoryName(RpcAuth.AllowedUsersFile)!;
            Directory.CreateDirectory(dir);
            var existing = RpcAuth.LoadAllowed();
            File.WriteAllLines(RpcAuth.AllowedUsersFile, existing.Concat(sids).Distinct(StringComparer.OrdinalIgnoreCase));
        }

        private static (bool ok, string output) Sc(string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("sc.exe", args)
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
                var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                return (p.ExitCode == 0, o.Trim());
            }
            catch (Exception ex) { return (false, ex.Message); }
        }
    }
}
