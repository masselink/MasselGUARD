using System;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;

namespace WGClientWifiSwitcher
{
    /// <summary>
    /// Wrapper around tunnel.dll (WireGuard embeddable-dll-service).
    ///
    /// HOW IT WORKS
    /// ─────────────────────────────────────────────────────────────────────────
    /// Two DLL files sit next to WGClientWifiSwitcher.exe:
    ///
    ///   tunnel.dll    — built from github.com/WireGuard/wireguard-windows/embeddable-dll-service
    ///                   Contains WireGuardTunnelService(confPath) and WireGuardGenerateKeypair().
    ///                   Source: build with `build.bat` in that repo (requires Go + MinGW).
    ///
    ///   wireguard.dll — the WireGuardNT kernel driver wrapper, pre-built by WireGuard LLC.
    ///                   Download: https://download.wireguard.com/wireguard-nt/wireguard-nt-*.zip
    ///                   Extract amd64/wireguard.dll next to the exe.
    ///
    /// UPGRADE WITHOUT RECOMPILE
    /// ─────────────────────────────────────────────────────────────────────────
    /// Replace tunnel.dll and/or wireguard.dll next to the exe.
    /// The app loads them at runtime via NativeLibrary — no compile-time binding.
    ///
    /// SERVICE HOST PATTERN
    /// ─────────────────────────────────────────────────────────────────────────
    /// Windows registers the tunnel as a service:
    ///   Name:       WireGuardTunnel$<TunnelName>
    ///   Executable: WGClientWifiSwitcher.exe /service "<conf path>"
    ///
    /// When Windows starts that service, OnStartup detects /service, calls
    /// TunnelDll.RunAsService(confPath) which loads tunnel.dll and invokes
    /// WireGuardTunnelService(confPath). That function runs the VPN tunnel
    /// and blocks until the service is stopped.
    ///
    /// WHAT THE C# APP DOES
    /// ─────────────────────────────────────────────────────────────────────────
    ///   - StartTunnel(name, confPath)  → creates/starts the Windows service
    ///   - StopTunnel(name)             → stops and removes the service
    ///   - IsRunning(name)              → checks service status
    ///   - GenerateKeypair()            → calls WireGuardGenerateKeypair in tunnel.dll
    ///   - IsTunnelDllAvailable()       → returns true if both DLLs are present
    /// </summary>
    public static class TunnelDll
    {
        // ── Service control P/Invoke ──────────────────────────────────────────

        private const uint SERVICE_WIN32_OWN_PROCESS  = 0x00000010;
        private const uint SERVICE_DEMAND_START        = 0x00000003;
        private const uint SERVICE_ERROR_NORMAL        = 0x00000001;
        private const uint SC_MANAGER_ALL_ACCESS       = 0x000F003F;
        private const uint SERVICE_ALL_ACCESS          = 0x000F01FF;
        private const uint SERVICE_SID_TYPE_UNRESTRICTED = 0x00000001;
        private const uint SERVICE_CONFIG_SERVICE_SID_INFO = 5;
        private const uint SERVICE_RUNNING             = 0x00000004;
        private const uint SERVICE_STOPPED             = 0x00000001;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateService(IntPtr scm, string serviceName, string displayName,
            uint desiredAccess, uint serviceType, uint startType, uint errorControl,
            string binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies,
            string? serviceStartName, string? password);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenService(IntPtr scm, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, ref ServiceSidInfo info);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool StartService(IntPtr service, uint numArgs, string[]? args);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ControlService(IntPtr service, uint control, ref ServiceStatus status);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DeleteService(IntPtr service);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatus(IntPtr service, ref ServiceStatus status);

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceSidInfo { public uint dwServiceSidType; }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            public uint dwServiceType, dwCurrentState, dwControlsAccepted,
                        dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
        }

        // ── DLL loading ───────────────────────────────────────────────────────

        private static string ExeDir =>
            Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!;

        public static string TunnelDllPath   => Path.Combine(ExeDir, "tunnel.dll");
        public static string WireGuardDllPath => Path.Combine(ExeDir, "wireguard.dll");

        public static bool IsTunnelDllAvailable() =>
            File.Exists(TunnelDllPath) && File.Exists(WireGuardDllPath);

        // ── Called from App.OnStartup when launched as a service ─────────────

        /// <summary>
        /// Invoked when the process is started by Windows as a service:
        ///   WGClientWifiSwitcher.exe /service "path\to\tunnel.conf"
        /// Loads tunnel.dll and calls WireGuardTunnelService which blocks
        /// until the service is stopped.
        /// Returns the exit code to pass to Environment.Exit().
        /// </summary>
        public static int RunAsService(string confPath)
        {
            var lib = NativeLibrary.Load(TunnelDllPath);
            var fn  = NativeLibrary.GetExport(lib, "WireGuardTunnelService");

            // delegate: BOOL __cdecl WireGuardTunnelService(LPCWSTR confFile)
            var proc = Marshal.GetDelegateForFunctionPointer<WireGuardTunnelServiceDelegate>(fn);
            return proc(confPath) ? 0 : 1;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool WireGuardTunnelServiceDelegate(
            [MarshalAs(UnmanagedType.LPWStr)] string confFile);

        // ── Key generation via tunnel.dll ─────────────────────────────────────

        /// <summary>
        /// Uses tunnel.dll's WireGuardGenerateKeypair to generate a proper
        /// Curve25519 keypair. Returns (privateKeyBase64, publicKeyBase64).
        /// Falls back to the pure C# implementation if tunnel.dll is absent.
        /// </summary>
        public static (string privateKey, string publicKey) GenerateKeypair()
        {
            if (IsTunnelDllAvailable())
            {
                try
                {
                    var lib = NativeLibrary.Load(TunnelDllPath);
                    var fn  = NativeLibrary.GetExport(lib, "WireGuardGenerateKeypair");
                    var proc = Marshal.GetDelegateForFunctionPointer<WireGuardGenerateKeypairDelegate>(fn);
                    var pub  = new byte[32];
                    var priv = new byte[32];
                    if (proc(pub, priv))
                        return (Convert.ToBase64String(priv), Convert.ToBase64String(pub));
                }
                catch { }
            }

            // Pure C# fallback (Curve25519 clamping)
            var key = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(key);
            key[0]  &= 248;
            key[31] &= 127;
            key[31] |= 64;
            return (Convert.ToBase64String(key), "");   // public key computation requires libsodium
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate bool WireGuardGenerateKeypairDelegate(
            [MarshalAs(UnmanagedType.LPArray, SizeConst = 32)] byte[] publicKey,
            [MarshalAs(UnmanagedType.LPArray, SizeConst = 32)] byte[] privateKey);

        // ── Service management ────────────────────────────────────────────────

        private static string ServiceName(string tunnelName) => "WireGuardTunnel$" + tunnelName;

        /// <summary>
        /// Registers and starts a Windows service that runs this exe in /service mode.
        /// The service loads tunnel.dll and runs the WireGuard tunnel.
        /// </summary>
        public static bool StartTunnel(string tunnelName, string confPath, out string error)
        {
            error = "";
            var svcName = ServiceName(tunnelName);
            var exePath = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "WGClientWifiSwitcher.exe");
            var binPath = $"\"{exePath}\" /service \"{confPath}\"";

            var scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero) { error = $"OpenSCManager failed: {Marshal.GetLastWin32Error()}"; return false; }

            try
            {
                // Create or open the service
                var svc = OpenService(scm, svcName, SERVICE_ALL_ACCESS);
                if (svc == IntPtr.Zero)
                {
                    svc = CreateService(scm, svcName, $"WireGuard Tunnel: {tunnelName}",
                        SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS,
                        SERVICE_DEMAND_START, SERVICE_ERROR_NORMAL,
                        binPath, null, IntPtr.Zero, "Nsi\0TcpIp\0\0", null, null);

                    if (svc == IntPtr.Zero)
                    {
                        error = $"CreateService failed: {Marshal.GetLastWin32Error()}";
                        return false;
                    }

                    // SERVICE_SID_TYPE_UNRESTRICTED is required by tunnel.dll
                    var sidInfo = new ServiceSidInfo { dwServiceSidType = SERVICE_SID_TYPE_UNRESTRICTED };
                    ChangeServiceConfig2(svc, SERVICE_CONFIG_SERVICE_SID_INFO, ref sidInfo);
                }

                try
                {
                    if (!StartService(svc, 0, null))
                    {
                        var err = Marshal.GetLastWin32Error();
                        if (err != 1056) // 1056 = already running
                        {
                            error = $"StartService failed: {err}";
                            DeleteService(svc);
                            return false;
                        }
                    }
                    return true;
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        /// <summary>Stops and removes the tunnel service.</summary>
        public static bool StopTunnel(string tunnelName, out string error)
        {
            error = "";
            var svcName = ServiceName(tunnelName);
            var scm = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero) { error = $"OpenSCManager failed: {Marshal.GetLastWin32Error()}"; return false; }
            try
            {
                var svc = OpenService(scm, svcName, SERVICE_ALL_ACCESS);
                if (svc == IntPtr.Zero) return true; // already gone
                try
                {
                    var status = new ServiceStatus();
                    ControlService(svc, 1 /*SERVICE_CONTROL_STOP*/, ref status);
                    // Wait for stop (up to 10s)
                    for (int i = 0; i < 20; i++)
                    {
                        QueryServiceStatus(svc, ref status);
                        if (status.dwCurrentState == SERVICE_STOPPED) break;
                        System.Threading.Thread.Sleep(500);
                    }
                    DeleteService(svc);
                    return true;
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        /// <summary>Returns true if the tunnel service is currently running.</summary>
        public static bool IsRunning(string tunnelName)
        {
            var scm = OpenSCManager(null, null, 0x0001 /*SC_MANAGER_CONNECT*/);
            if (scm == IntPtr.Zero) return false;
            try
            {
                var svc = OpenService(scm, ServiceName(tunnelName), 0x0004 /*SERVICE_QUERY_STATUS*/);
                if (svc == IntPtr.Zero) return false;
                try
                {
                    var status = new ServiceStatus();
                    QueryServiceStatus(svc, ref status);
                    return status.dwCurrentState == SERVICE_RUNNING;
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }
    }
}
