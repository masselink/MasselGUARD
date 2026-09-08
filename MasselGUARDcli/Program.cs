using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MasselGUARD
{
    /// <summary>
    /// MasselGUARDcli entry point.
    /// Pure console application — no WPF, no GUI dependencies.
    /// Manifest is asInvoker: runs inline in any terminal and prints a clean
    /// error when Administrator rights are missing (no popup window).
    /// </summary>
    public static class CliProgram
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        [STAThread]
        public static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            // Informational commands touch no driver, service or elevated state, so they
            // run in any terminal without Administrator rights (help, version, self-tests).
            if (args.Length > 0 && IsNonElevatedCommand(args[0]))
                return Cli.CliRunner.Run(args);

            // ── Elevation check ───────────────────────────────────────────────
            // The manifest is asInvoker so non-admin terminals get an inline
            // error rather than a UAC popup that spawns a new window.
            if (!IsElevated())
            {
                Console.Error.WriteLine("MasselGUARDcli requires Administrator privileges.");
                Console.Error.WriteLine("Run from an elevated terminal, or use: sudo MasselGUARDcli <command>");
                return 1;
            }

            // Resolve exe directory and set DLL/working paths so tunnel.dll can
            // find wireguard.dll regardless of where the CLI is invoked from.
            string exeDir;
            try
            {
                exeDir = Path.GetDirectoryName(
                    Process.GetCurrentProcess().MainModule?.FileName
                    ?? AppContext.BaseDirectory)
                    ?? AppContext.BaseDirectory;
            }
            catch { exeDir = AppContext.BaseDirectory; }

            try { Directory.SetCurrentDirectory(exeDir); } catch { }
            SetDllDirectory(exeDir);

            if (args.Length == 0)
            {
                Cli.CliOutput.Info("MasselGUARDcli — WireGuard tunnel manager (command-line interface)");
                Cli.CliOutput.Info("Run 'MasselGUARDcli help' for a list of commands.");
                return 0;
            }

            return Cli.CliRunner.Run(args);
        }

        /// <summary>Commands that need no Administrator rights (no driver/service access).</summary>
        private static bool IsNonElevatedCommand(string cmd) => cmd.ToLowerInvariant() switch
        {
            "help" or "--help" or "-h" or "-?"     => true,
            "version" or "--version" or "-v"       => true,
            "selftest"                             => true,
            _                                      => false,
        };

        private static bool IsElevated()
        {
            try
            {
                var identity  = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }
}
