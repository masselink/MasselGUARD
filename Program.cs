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

            // Windowless DNS bypass client (Explorer right-click entries): MasselGUARD.exe --bypass <seconds|stop|toggle>.
            // The GUI exe has no console, so no terminal window flashes; it never elevates and never opens a window.
            if (args.Length >= 1 && string.Equals(args[0], "--bypass", StringComparison.OrdinalIgnoreCase))
                return RunBypassLauncher(args);

            // ── Elevation (manifest is asInvoker) ──────────────────────────────
            // 1. The MasselGUARD service is installed and answering: run unelevated, the service does the
            //    privileged work (no UAC prompt at all).
            // 2. Direct mode: the app itself must be elevated. A scheduled task 'MasselGUARD' (managed
            //    installs) starts it at RunLevel=Highest without a UAC prompt; otherwise relaunch with UAC.
            // An elevated start with the service installed: make sure this user may call the service, so the NEXT start
            // can run unelevated (the service lets administrators in, but an unelevated administrator carries a filtered token
            // that does not count as one, so the account itself must be on the service's list).
            if (IsElevated()) TryAllowCurrentUser();

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

        /// <summary>Asks the running window, else the MasselGUARD service, to start/stop the DNS bypass. Silent on
        /// success; a failure (nothing to talk to, no bypass profile marked, ...) is shown in a message box.</summary>
        private static int RunBypassLauncher(string[] args)
        {
            string? error = null;
            var req = Services.BypassClient.ParseLauncherArgs(args);
            if (req.Action == "invalid")
                error = $"Usage: MasselGUARD.exe --bypass [seconds|stop|toggle]   (seconds {Services.CommandPipe.MinSeconds}-{Services.CommandPipe.MaxSeconds})";
            else
            {
                try
                {
                    var cs = new Services.ConfigService();
                    cs.Load();
                    var result = Services.BypassClient.Run(req, args.Length > 1 ? args[1] : "", cs.Config);
                    if (result == null) error = "MasselGUARD is not running and the MasselGUARD service is not available, so there is nothing to switch DNS.";
                    else if (!result.Value.ok) error = result.Value.message.StartsWith("error: ") ? result.Value.message[7..] : result.Value.message;
                }
                catch (Exception ex) { error = ex.Message; }
            }
            if (error == null) return 0;
            try { System.Windows.Forms.MessageBox.Show(error, "MasselGUARD - DNS bypass", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning); }
            catch { }
            return 1;
        }

        /// <summary>The service is installed, running and accepts this user (checked through the pipe, with the
        /// server's process id verified against the service's).</summary>
        /// <summary>Elevated and the service is installed: adds this account to the service's allowed users when it is missing.</summary>
        private static void TryAllowCurrentUser()
        {
            try
            {
                if (!Services.ServiceInstaller.IsInstalled()) return;
                var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
                if (sid == null || Services.RpcAuth.LoadAllowed().Contains(sid, StringComparer.OrdinalIgnoreCase)) return;
                var err = Services.ServiceInstaller.AllowUser(sid);
                WriteStartupNote(err == null ? "this account was not on the service's list of allowed users and has been added" : "could not add this account to the service's allowed users: " + err);
            }
            catch { }
        }

        /// <summary>One line in %APPDATA%\MasselGUARD\startup.log (kept small): why the app did not use the service, so a UAC prompt at
        /// start can be explained.</summary>
        private static void WriteStartupNote(string note)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MasselGUARD");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, "startup.log");
                if (File.Exists(path) && new FileInfo(path).Length > 40_000) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {note}{Environment.NewLine}");
            }
            catch { }
        }

        private static bool ServiceUsable()
        {
            var ok = ServiceUsableCore(out var why);
            if (!ok && Services.ServiceInstaller.IsInstalled()) WriteStartupNote("service is installed but not used: " + why);
            return ok;
        }

        private static bool ServiceUsableCore(out string why)
        {
            why = "";
            try
            {
                var rpc = new Services.RpcOps(expectedServerPid: Services.ServiceInstaller.ServicePid);
                // At logon (start with Windows) the service may still be starting: give it a few seconds
                // instead of falling back to a UAC prompt. A stopped service or a refused caller is no reason to wait.
                for (int i = 0; i < 20; i++)
                {
                    var st = Services.ServiceInstaller.Status();
                    if (st is not (System.ServiceProcess.ServiceControllerStatus.Running or System.ServiceProcess.ServiceControllerStatus.StartPending))
                    { why = "the service is " + (st?.ToString() ?? "missing"); return false; }
                    if (st == System.ServiceProcess.ServiceControllerStatus.Running && rpc.IsAvailable()) return true;
                    if (rpc.LastError == "access denied") { why = "the service refused this account (not on its allowed users)"; return false; }
                    why = "no answer from the service: " + (string.IsNullOrEmpty(rpc.LastError) ? "unknown" : rpc.LastError);
                    System.Threading.Thread.Sleep(500);
                }
                return false;
            }
            catch (Exception ex) { why = "error: " + ex.Message; return false; }
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
