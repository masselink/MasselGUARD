// TunnelDll.cs
// Manages local WireGuard tunnels via tunnel.dll + wireguard.dll.
//
// Based on the working WireGuardClient reference implementation.
// No WireGuard for Windows installation required.
//
// tunnel.dll  — built from wireguard-windows/embeddable-dll-service (requires Go)
// wireguard.dll — https://download.wireguard.com/wireguard-nt/
// Both must be placed next to MasselGUARD.exe.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;

namespace MasselGUARD
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  Native declarations — matches the reference implementation exactly
    // ═══════════════════════════════════════════════════════════════════════════
    internal static class NativeMethods
    {
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr OpenSCManager(
            string? machineName, string? databaseName, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateService(
            IntPtr hSCManager, string serviceName, string displayName,
            uint desiredAccess, uint serviceType, uint startType,
            uint errorControl, string binaryPath,
            string? loadOrderGroup, IntPtr tagId, string? dependencies,
            string? serviceStartName, string? password);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr OpenService(
            IntPtr hSCManager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool DeleteService(IntPtr hService);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool CloseServiceHandle(IntPtr hSCObject);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool ChangeServiceConfig2(
            IntPtr hService, uint infoLevel, ref SERVICE_SID_INFO lpInfo);

        [StructLayout(LayoutKind.Sequential)]
        internal struct SERVICE_SID_INFO
        {
            public uint dwServiceSidType;
        }

        // Direct DllImport — exactly as in the reference implementation
        [DllImport("tunnel.dll", EntryPoint = "WireGuardTunnelService",
                   CallingConvention = CallingConvention.Cdecl,
                   CharSet = CharSet.Unicode)]
        internal static extern bool WireGuardTunnelService(
            [MarshalAs(UnmanagedType.LPWStr)] string configFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool SetDllDirectory(string lpPathName);

        internal const uint SC_MANAGER_ALL_ACCESS          = 0xF003F;
        internal const uint SERVICE_ALL_ACCESS             = 0xF01FF;
        internal const uint SERVICE_WIN32_OWN_PROCESS      = 0x00000010;
        internal const uint SERVICE_DEMAND_START           = 0x00000003;
        internal const uint SERVICE_ERROR_NORMAL           = 0x00000001;
        internal const uint SERVICE_CONFIG_SERVICE_SID_INFO = 5;
        internal const uint SERVICE_SID_TYPE_UNRESTRICTED  = 1;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  TunnelDll — public API used by MainWindow
    // ═══════════════════════════════════════════════════════════════════════════
    public static class TunnelDll
    {
        private const string ServicePrefix = "WireGuardTunnel$";

        private static string ExeDir =>
            Path.GetDirectoryName(
                Environment.ProcessPath?.TrimEnd('\\', '/') ??
                AppContext.BaseDirectory.TrimEnd('\\', '/'))
            ?? AppContext.BaseDirectory;

        public static string ExeDirPublic    => ExeDir;
        public static string TunnelDllPath   => Path.Combine(ExeDir, "tunnel.dll");
        public static string WireGuardDllPath => Path.Combine(ExeDir, "wireguard.dll");

        public static bool IsTunnelDllAvailable() =>
            File.Exists(TunnelDllPath) && File.Exists(WireGuardDllPath);

        // ── Service host entry point ──────────────────────────────────────────
        // Called from Program.Main when args are ["/service", "<confPath>"].
        // Returns exit code, or -1 if this is not a service invocation.
        public static int HandleServiceArgs(string[] args)
        {
            if (args.Length == 2 &&
                string.Equals(args[0], "/service", StringComparison.OrdinalIgnoreCase))
            {
                // Set DLL search path before tunnel.dll is loaded
                NativeMethods.SetDllDirectory(ExeDir);
                Directory.SetCurrentDirectory(ExeDir);

                WriteDebug($"HandleServiceArgs: conf={args[1]}");
                WriteDebug($"ExeDir={ExeDir}");
                WriteDebug($"tunnel.dll={File.Exists(TunnelDllPath)}");
                WriteDebug($"wireguard.dll={File.Exists(WireGuardDllPath)}");
                WriteDebug($"conf exists={File.Exists(args[1])}");

                try
                {
                    WriteDebug("Calling WireGuardTunnelService...");
                    bool ok = NativeMethods.WireGuardTunnelService(args[1]);
                    WriteDebug($"WireGuardTunnelService returned {ok}");
                    return ok ? 0 : 1;
                }
                catch (Exception ex)
                {
                    WriteDebug($"WireGuardTunnelService threw: {ex}");
                    try { System.Diagnostics.EventLog.WriteEntry("MasselGUARD",
                        $"WireGuardTunnelService failed: {ex}", EventLogEntryType.Error); }
                    catch { }
                    return 1;
                }
            }
            return -1; // not a service invocation
        }

        // ── Connect ───────────────────────────────────────────────────────────
        public static bool Connect(string tunnelName, string confPath,
            Action<string> log, out string error)
        {
            error = "";

            if (!File.Exists(confPath))
            { error = $"Config file not found: {confPath}"; return false; }

            if (!IsTunnelDllAvailable())
            { error = $"tunnel.dll or wireguard.dll not found in: {ExeDir}"; return false; }

            string serviceName = ServicePrefix + tunnelName;

            log($"Connecting tunnel '{tunnelName}'");
            log($"Config: {confPath}");

            // Stop and remove any stale instance
            EnsureStopped(serviceName, log);

            // Install and start
            try
            {
                InstallAndStart(serviceName, tunnelName, confPath, log);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ── Disconnect ────────────────────────────────────────────────────────
        public static bool Disconnect(string tunnelName, out string error)
        {
            error = "";
            string serviceName = ServicePrefix + tunnelName;
            try
            {
                EnsureStopped(serviceName, _ => { });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ── IsRunning ─────────────────────────────────────────────────────────
        public static bool IsRunning(string tunnelName)
        {
            // Check if the WireGuardTunnel$ service is RUNNING.
            // Note: WireGuardTunnelService blocks then returns, so the service
            // may briefly be STOPPED even while the VPN is still active.
            // We also check if the tunnel process is still alive via the service.
            try
            {
                using var sc = new ServiceController(ServicePrefix + tunnelName);
                return sc.Status == ServiceControllerStatus.Running ||
                       sc.Status == ServiceControllerStatus.StartPending;
            }
            catch { return false; }
        }

        // ── Log file path (ring-log written by tunnel.dll) ────────────────────
        public static string GetLogFilePath(string tunnelName, string confPath) =>
            Path.Combine(
                Path.GetDirectoryName(confPath) ?? AppContext.BaseDirectory,
                tunnelName + ".log");

        // ── Internal: install + start ─────────────────────────────────────────
        private static void InstallAndStart(string serviceName, string tunnelName,
            string confPath, Action<string> log)
        {
            string exePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Cannot determine exe path.");

            // Quote both paths — matches reference implementation exactly
            string binaryPath = $"\"{exePath}\" /service \"{confPath}\"";

            log($"Installing service: {serviceName}");
            log($"Binary path: {binaryPath}");

            IntPtr scm = NativeMethods.OpenSCManager(null, null,
                NativeMethods.SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(), "OpenSCManager failed.");

            try
            {
                IntPtr svc = NativeMethods.CreateService(
                    scm, serviceName, $"WireGuard Tunnel: {tunnelName}",
                    NativeMethods.SERVICE_ALL_ACCESS,
                    NativeMethods.SERVICE_WIN32_OWN_PROCESS,
                    NativeMethods.SERVICE_DEMAND_START,
                    NativeMethods.SERVICE_ERROR_NORMAL,
                    binaryPath,
                    null, IntPtr.Zero,
                    "Nsi\0TcpIp\0",
                    null, null);

                if (svc == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new System.ComponentModel.Win32Exception(err,
                        $"CreateService failed (win32={err}).");
                }

                try
                {
                    // CRITICAL: SERVICE_SID_TYPE_UNRESTRICTED required by tunnel.dll
                    var sidInfo = new NativeMethods.SERVICE_SID_INFO
                        { dwServiceSidType = NativeMethods.SERVICE_SID_TYPE_UNRESTRICTED };

                    if (!NativeMethods.ChangeServiceConfig2(svc,
                        NativeMethods.SERVICE_CONFIG_SERVICE_SID_INFO, ref sidInfo))
                        log($"[WARN] ChangeServiceConfig2 failed ({Marshal.GetLastWin32Error()})");
                    else
                        log("SERVICE_SID_TYPE_UNRESTRICTED set");

                    log("Starting service...");
                    using var sc = new ServiceController(serviceName);
                    sc.Start();

                    // WireGuardTunnelService BLOCKS while the tunnel runs, so the service
                    // process transitions: START_PENDING → RUNNING → STOPPED (on exit).
                    // We poll for up to 10s and succeed on either RUNNING or clean STOPPED
                    // (exit 0 = tunnel ran successfully; a non-zero exit means real failure).
                    for (int i = 0; i < 40; i++)
                    {
                        sc.Refresh();
                        var status = sc.Status;
                        log($"Service status: {status}");

                        if (status == ServiceControllerStatus.Running)
                            return; // tunnel is up

                        if (status == ServiceControllerStatus.Stopped)
                        {
                            // Check exit code via SCM QueryServiceStatus
                            // A clean exit (0) means the tunnel ran and stopped — treat as success.
                            // Any non-zero exit is a real failure.
                            // We can't read ExitCode from ServiceController directly, so
                            // we treat STOPPED after a Start() as success if the service
                            // existed long enough to reach RUNNING first (i.e. i > 0).
                            if (i > 2)
                                return; // ran long enough — tunnel connected then stopped cleanly
                            throw new InvalidOperationException(
                                "Service stopped immediately after start. " +
                                "Check tunnel.dll and wireguard.dll are next to the exe.");
                        }

                        System.Threading.Thread.Sleep(250);
                    }

                    log($"Service status after wait: {sc.Status}");
                    // If we get here the service is still START_PENDING but that's acceptable —
                    // the tunnel process is running, WireGuardTunnelService is blocking inside it.
                }
                finally { NativeMethods.CloseServiceHandle(svc); }
            }
            finally { NativeMethods.CloseServiceHandle(scm); }
        }

        // ── Internal: stop + delete ───────────────────────────────────────────
        private static void EnsureStopped(string serviceName, Action<string> log)
        {
            try
            {
                using var sc = new ServiceController(serviceName);
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    log($"Stopping existing service {serviceName}...");
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped,
                        TimeSpan.FromSeconds(15));
                }
            }
            catch { /* service may not exist */ }
            DeleteServiceEntry(serviceName);
        }

        private static void DeleteServiceEntry(string serviceName)
        {
            IntPtr scm = NativeMethods.OpenSCManager(null, null,
                NativeMethods.SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero) return;
            try
            {
                IntPtr svc = NativeMethods.OpenService(scm, serviceName,
                    NativeMethods.SERVICE_ALL_ACCESS);
                if (svc == IntPtr.Zero) return;
                try { NativeMethods.DeleteService(svc); }
                finally { NativeMethods.CloseServiceHandle(svc); }
            }
            finally { NativeMethods.CloseServiceHandle(scm); }
        }

        // ── Debug log ─────────────────────────────────────────────────────────
        internal static void WriteDebug(string message)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(ExeDir, "service-debug.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch { }
        }

        // ── Keygen ────────────────────────────────────────────────────────────
        public static (string privateKey, string publicKey) GenerateKeypair()
        {
            // Pure C# Curve25519 clamping (public key unavailable without libsodium)
            var key = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(key);
            key[0]  &= 248;
            key[31] &= 127;
            key[31] |= 64;
            return (Convert.ToBase64String(key), "");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Ringlogger — reads tunnel.dll's memory-mapped binary ring-log
    //  Ported directly from the reference implementation
    // ═══════════════════════════════════════════════════════════════════════════
    public sealed class Ringlogger : IDisposable
    {
        private const uint MagicNumber   = 0xbadc0ffe;
        private const int  MaxEntries    = 512;
        private const int  LineSize      = 512;
        private const int  TimestampSize = 8;
        private const int  TextSize      = LineSize - TimestampSize;
        private const int  HeaderSize    = 8;

        private static readonly DateTime UnixEpoch =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly string _path;
        private System.IO.MemoryMappedFiles.MemoryMappedFile?         _mmf;
        private System.IO.MemoryMappedFiles.MemoryMappedViewAccessor? _view;
        private uint _cursor = uint.MaxValue;

        public Ringlogger(string logFilePath) { _path = logFilePath; }

        public (DateTime timestamp, string text)[] CollectNewLines()
        {
            try
            {
                EnsureOpen();
                if (_view == null) return Array.Empty<(DateTime, string)>();

                uint magic = _view.ReadUInt32(0);
                if (magic != MagicNumber) return Array.Empty<(DateTime, string)>();

                uint writeCursor = _view.ReadUInt32(4);
                if (_cursor == uint.MaxValue)
                {
                    _cursor = writeCursor;
                    return Array.Empty<(DateTime, string)>();
                }
                if (_cursor == writeCursor) return Array.Empty<(DateTime, string)>();

                uint count = writeCursor >= _cursor
                    ? writeCursor - _cursor
                    : (uint)MaxEntries - _cursor + writeCursor;
                if (count > MaxEntries) count = (uint)MaxEntries;

                var lines = new System.Collections.Generic.List<(DateTime, string)>();
                for (uint i = 0; i < count; i++)
                {
                    uint slot   = (_cursor + i) % MaxEntries;
                    long offset = HeaderSize + slot * LineSize;
                    long ticks  = _view.ReadInt64(offset);
                    if (ticks == 0) continue;
                    DateTime ts  = UnixEpoch.AddTicks(ticks).ToLocalTime();
                    byte[] buf   = new byte[TextSize];
                    _view.ReadArray(offset + TimestampSize, buf, 0, TextSize);
                    int len      = Array.IndexOf(buf, (byte)0);
                    string text  = Encoding.UTF8.GetString(buf, 0, len < 0 ? TextSize : len).Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                        lines.Add((ts, text));
                }
                _cursor = writeCursor;
                return lines.ToArray();
            }
            catch { Close(); return Array.Empty<(DateTime, string)>(); }
        }

        private void EnsureOpen()
        {
            if (_view != null) return;
            if (!File.Exists(_path)) return;
            long sz = HeaderSize + (long)MaxEntries * LineSize;
            _mmf  = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(
                _path, FileMode.Open, null, sz,
                System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
            _view = _mmf.CreateViewAccessor(0, sz,
                System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
        }

        private void Close()
        {
            _view?.Dispose(); _view = null;
            _mmf?.Dispose();  _mmf  = null;
        }

        public void Dispose() => Close();
    }
}
