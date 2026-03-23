using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MasselGUARD
{
    public static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        [STAThread]
        public static int Main(string[] args)
        {
            var exeDir = Path.GetDirectoryName(
                Environment.ProcessPath?.TrimEnd('\\', '/') ??
                AppContext.BaseDirectory.TrimEnd('\\', '/'))
                ?? AppContext.BaseDirectory;

            // Set DLL search path before anything loads
            Directory.SetCurrentDirectory(exeDir);
            SetDllDirectory(exeDir);

            // ── /service dispatch — must happen before WPF starts ────────────
            // args here are from Environment.GetCommandLineArgs() minus the exe name,
            // matching the reference implementation's HandleServiceArgs contract.
            var cmdArgs = Environment.GetCommandLineArgs();
            // cmdArgs[0] = exe, cmdArgs[1..] = actual args
            string[] serviceArgs = cmdArgs.Length > 1
                ? cmdArgs[1..]
                : Array.Empty<string>();

            int svcResult = TunnelDll.HandleServiceArgs(serviceArgs);
            if (svcResult >= 0)
                return svcResult;

            // ── Normal GUI launch ────────────────────────────────────────────
            var app = new App();
            app.InitializeComponent();
            app.Run();
            return 0;
        }
    }
}
