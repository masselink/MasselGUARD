using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace MasselGUARD
{
    public static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        // Windows derives a taskbar/jump-list identity (AppUserModelID) from the exe's
        // file path by default, which means a portable build and an installed copy - or
        // even the same install path across reinstalls - can be treated as separate app
        // identities with independently cached taskbar icon state. Explorer normally
        // propagates a shortcut's own AppUserModelID to the process it launches, but that
        // doesn't happen for the managed-install path below, which relaunches itself via
        // Task Scheduler rather than Explorer directly. Setting an explicit, hardcoded ID
        // here - before any window exists, on every launch path - keeps the identity
        // stable regardless of how or from where the app was started.
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);

        [STAThread]
        public static int Main(string[] args)
        {
            // Resolve exe directory - same approach as the original.
            // Environment.ProcessPath is unreliable in some single-file publish configs.
            string exeDir;
            try
            {
                exeDir = Path.GetDirectoryName(
                    Process.GetCurrentProcess().MainModule?.FileName
                    ?? AppContext.BaseDirectory)
                    ?? AppContext.BaseDirectory;
            }
            catch { exeDir = AppContext.BaseDirectory; }

            // CRITICAL: set CWD and DLL search path BEFORE everything else.
            // When the SCM launches this process as a service child
            //   (MasselGUARD.exe /service "...")
            // CWD is System32. tunnel.dll calls LoadLibrary("wireguard.dll")
            // using CWD + DLL search order, so both must point at the exe dir.
            try { Directory.SetCurrentDirectory(exeDir); } catch { }
            SetDllDirectory(exeDir);

            // /service dispatch - must happen before any WPF initialisation.
            int svcResult = TunnelDll.HandleServiceArgs(args, exeDir);
            if (svcResult >= 0)
                return svcResult;

            // Privileged back-end service (MasselGUARD.exe /svc, LocalSystem) - also before any WPF.
            if (args.Length >= 1 && string.Equals(args[0], "/svc", StringComparison.OrdinalIgnoreCase))
                return Services.ServiceHost.Run();

            // ── Elevation (manifest is asInvoker) ──────────────────────────────
            // 1. The MasselGUARD service is installed and answering: run unelevated, the service does the
            //    privileged work (no UAC prompt at all).
            // 2. Direct mode: the app itself must be elevated. A scheduled task 'MasselGUARD' (managed
            //    installs) starts it at RunLevel=Highest without a UAC prompt; otherwise relaunch with UAC.
            if (!IsElevated() && !ServiceUsable())
            {
                if (ScheduledTaskExists("MasselGUARD"))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo("schtasks.exe",
                            "/run /tn MasselGUARD /i")
                        {
                            CreateNoWindow  = true,
                            UseShellExecute = false,
                        });
                        return 0; // exit this non-elevated instance
                    }
                    catch { /* fall through to the UAC relaunch */ }
                }
                return RelaunchElevated(args, exeDir);
            }

            // Normal GUI launch - WinExe subsystem means Windows never
            // allocates a console, so there is nothing to hide or free.
            try { SetCurrentProcessExplicitAppUserModelID("MasselGUARD.App"); } catch { }
            var app = new App();
            app.InitializeComponent();
            app.Run();
            return 0;
        }

        /// <summary>The service is installed, running and accepts this user (checked through the pipe, with the
        /// server's process id verified against the service's).</summary>
        private static bool ServiceUsable()
        {
            try
            {
                return Services.ServiceInstaller.Status() == System.ServiceProcess.ServiceControllerStatus.Running
                    && new Services.RpcOps(expectedServerPid: Services.ServiceInstaller.ServicePid).IsAvailable();
            }
            catch { return false; }
        }

        /// <summary>Starts this exe again with the UAC prompt and ends this instance. Declining the prompt
        /// ends the app too (direct mode cannot work without elevation).</summary>
        private static int RelaunchElevated(string[] args, string exeDir)
        {
            try
            {
                var exe = Environment.ProcessPath ?? Path.Combine(exeDir, "MasselGUARD.exe");
                Process.Start(new ProcessStartInfo(exe)
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    Arguments = string.Join(" ", args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)),
                    WorkingDirectory = exeDir,
                });
                return 0;
            }
            catch { return 1; }   // UAC declined (or the exe vanished)
        }

        private static bool IsElevated()
        {
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var p  = new System.Security.Principal.WindowsPrincipal(id);
                return p.IsInRole(
                    System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static bool ScheduledTaskExists(string taskName)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo(
                    "schtasks.exe", $"/query /tn \"{taskName}\"")
                {
                    CreateNoWindow         = true,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                });
                proc?.WaitForExit(3000);
                return proc?.ExitCode == 0;
            }
            catch { return false; }
        }
    }
}
