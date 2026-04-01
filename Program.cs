using System;
using System.Diagnostics;
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
            // Resolve exe dir via MainModule.FileName — same approach as working
            // WireGuardClient. Environment.ProcessPath is unreliable in some
            // self-contained publish configurations.
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

            // /service dispatch — must happen before any WPF initialisation.
            int svcResult = TunnelDll.HandleServiceArgs(args, exeDir);
            if (svcResult >= 0)
                return svcResult;

            // Normal GUI launch.
            var app = new App();
            app.InitializeComponent();
            app.Run();
            return 0;
        }
    }
}
