// TunnelDll.cs — Local tunnel management via tunnel.dll + wireguard.dll
//
// This file handles ONLY local (standalone) tunnels.
// WireGuard companion tunnels (managed by the official WireGuard client) are
// handled separately in MainWindow.xaml.cs via ServiceController.
//
// Implementation mirrors WireGuardClient/TunnelService.cs exactly.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace MasselGUARD
{
    // ═══════════════════════════════════════════════════════════════════════════
    //  Native Win32 — mirrors WireGuardClient reference exactly
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
        internal struct SERVICE_SID_INFO { public uint dwServiceSidType; }

        // tunnel.dll entry point — blocks for the lifetime of the tunnel
        [DllImport("tunnel.dll", EntryPoint = "WireGuardTunnelService",
                   CallingConvention = CallingConvention.Cdecl,
                   CharSet = CharSet.Unicode)]
        internal static extern bool WireGuardTunnelService(
            [MarshalAs(UnmanagedType.LPWStr)] string configFile);

        // tunnel.dll keypair generation
        [DllImport("tunnel.dll", EntryPoint = "WireGuardGenerateKeypair",
                   CallingConvention = CallingConvention.Cdecl)]
        internal static extern bool WireGuardGenerateKeypair(
            [Out] byte[] publicKey, [Out] byte[] privateKey);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool SetDllDirectory(string lpPathName);

        internal const uint SC_MANAGER_ALL_ACCESS           = 0xF003F;
        internal const uint SERVICE_ALL_ACCESS              = 0xF01FF;
        internal const uint SERVICE_WIN32_OWN_PROCESS       = 0x00000010;
        internal const uint SERVICE_DEMAND_START            = 0x00000003;
        internal const uint SERVICE_ERROR_NORMAL            = 0x00000001;
        internal const uint SERVICE_CONFIG_SERVICE_SID_INFO = 5;
        internal const uint SERVICE_SID_TYPE_UNRESTRICTED   = 1;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  TunnelDll — public API for local tunnels
    // ═══════════════════════════════════════════════════════════════════════════
    public static class TunnelDll
    {
        private const string ServicePrefix = "WireGuardTunnel$";

        // Tunnels currently connected, tracked in memory.
        // (The service process exits as soon as the tunnel is up in the kernel,
        //  so SCM status is Stopped even when the tunnel is live.)
        private static readonly HashSet<string> _connected =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new();

        // Exe directory — computed once from MainModule (reliable across all
        // publish configs). Cached so service-child and GUI both agree.
        private static string? _exeDir;
        private static string ExeDir
        {
            get
            {
                if (_exeDir != null) return _exeDir;
                try
                {
                    _exeDir = Path.GetDirectoryName(
                        Process.GetCurrentProcess().MainModule?.FileName
                        ?? AppContext.BaseDirectory)
                        ?? AppContext.BaseDirectory;
                }
                catch { _exeDir = AppContext.BaseDirectory; }
                return _exeDir;
            }
        }

        public static string ExeDirPublic     => ExeDir;
        public static string TunnelDllPath    => Path.Combine(ExeDir, "tunnel.dll");
        public static string WireGuardDllPath => Path.Combine(ExeDir, "wireguard.dll");

        public static bool IsTunnelDllAvailable() =>
            File.Exists(TunnelDllPath) && File.Exists(WireGuardDllPath);

        // Minimum size (bytes) for the wireguard-NT wireguard.dll.
        // The wireguard-NT dll (~1.3 MB) embeds its own kernel driver.
        // The WireGuard-for-Windows wireguard.dll (~400 KB) does NOT — it
        // requires wireguard.sys to be pre-installed by the WireGuard app and
        // will fail with "cannot find file" when starting the tunnel service.
        private const long WireGuardNtMinBytes = 900_000;

        /// <summary>
        /// Returns null if DLLs are present and appear correct.
        /// Returns an error string if DLLs are missing or the wrong version.
        /// </summary>
        public static string? ValidateDlls()
        {
            if (!File.Exists(TunnelDllPath))
                return $"tunnel.dll not found in: {ExeDir}";
            if (!File.Exists(WireGuardDllPath))
                return $"wireguard.dll not found in: {ExeDir}";

            try
            {
                var wgSize = new FileInfo(WireGuardDllPath).Length;
                if (wgSize < WireGuardNtMinBytes)
                    return $"Wrong wireguard.dll — this copy is {wgSize / 1024} KB and appears to be " +
                           $"the WireGuard-for-Windows version, which requires the WireGuard app to be " +
                           $"installed. Standalone mode needs the wireguard-NT version (~1.3 MB). " +
                           $"Run get-wireguard-dlls.ps1 to download the correct file, or download it from " +
                           $"https://download.wireguard.com/wireguard-nt/";
            }
            catch { /* best-effort */ }

            return null;
        }

        // ── /service entry point ──────────────────────────────────────────────
        // Called by Program.Main before WPF starts.
        // exeDir is passed in from Program so both agree on the path.
        public static int HandleServiceArgs(string[] args, string exeDir)
        {
            if (args.Length >= 2 &&
                string.Equals(args[0], "/service", StringComparison.OrdinalIgnoreCase))
            {
                string conf = args[1];

                // Cache the exe dir so TunnelDll helpers work correctly in this process.
                _exeDir = exeDir;

                // CWD and DLL directory were already set in Program.Main, but
                // repeat here defensively in case this method is called directly.
                try { Directory.SetCurrentDirectory(exeDir); } catch { }
                NativeMethods.SetDllDirectory(exeDir);

                WriteDebug($"HandleServiceArgs: conf={conf}");
                WriteDebug($"ExeDir        = {exeDir}");
                WriteDebug($"CWD           = {Directory.GetCurrentDirectory()}");
                WriteDebug($"tunnel.dll    = {File.Exists(Path.Combine(exeDir, "tunnel.dll"))}");
                WriteDebug($"wireguard.dll = {File.Exists(Path.Combine(exeDir, "wireguard.dll"))}");
                WriteDebug($"conf exists   = {File.Exists(conf)}");

                try
                {
                    WriteDebug("Calling WireGuardTunnelService…");
                    bool ok = NativeMethods.WireGuardTunnelService(conf);
                    WriteDebug($"WireGuardTunnelService returned {ok}");
                    return ok ? 0 : 1;
                }
                catch (Exception ex)
                {
                    WriteDebug($"WireGuardTunnelService threw: {ex}");
                    try { System.Diagnostics.EventLog.WriteEntry("MasselGUARD",
                        $"WireGuardTunnelService failed: {ex}",
                        System.Diagnostics.EventLogEntryType.Error); }
                    catch { }
                    return 1;
                }
            }
            return -1;
        }

        // ── Connect ───────────────────────────────────────────────────────────
        // confPath must be a plaintext .conf file readable by LocalSystem.
        // The caller is responsible for writing the file before calling Connect
        // and deleting it after Disconnect.
        public static bool Connect(string tunnelName, string confPath,
            Action<string> log, out string error)
        {
            error = "";
            if (!File.Exists(confPath))
            { error = $"Config file not found: {confPath}"; return false; }
            if (!IsTunnelDllAvailable())
            { error = $"tunnel.dll or wireguard.dll not found in: {ExeDir}"; return false; }

            // Service names must not contain spaces (SCM restriction).
            string safeKey     = tunnelName.Replace(' ', '_');
            string serviceName = ServicePrefix + safeKey;
            log($"Tunnel name  : {tunnelName}");
            log($"Service name : {serviceName}");
            log($"Config       : {confPath}");
            log($"Exe dir      : {ExeDir}");

            // Stop + delete any stale service (matches WireGuardClient exactly)
            EnsureStopped(serviceName, log);

            try
            {
                InstallAndStart(serviceName, tunnelName, confPath, log);
                lock (_lock) { _connected.Add(tunnelName); } // display name
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
            lock (_lock) { _connected.Remove(tunnelName); }
            string serviceName = ServicePrefix + tunnelName.Replace(' ', '_');
            WriteDebug($"Disconnect: stopping {serviceName}");
            EnsureStopped(serviceName, _ => { });
            WriteDebug($"Disconnect: {serviceName} done");
            return true;
        }

        // ── DisconnectAll ─────────────────────────────────────────────────────
        public static void DisconnectAll()
        {
            List<string> tunnels;
            lock (_lock) { tunnels = new List<string>(_connected); }
            foreach (var name in tunnels)
                Disconnect(name, out _);
        }

        // ── IsRunning ─────────────────────────────────────────────────────────
        public static bool IsRunning(string tunnelName)
        {
            lock (_lock) { return _connected.Contains(tunnelName); }
        }

        // ── Log file path ─────────────────────────────────────────────────────
        // tunnel.dll writes <tunnelName>.log next to the exe.
        public static string GetLogFilePath(string tunnelName, string confPath) =>
            Path.Combine(ExeDir, tunnelName + ".log");

        // ── Keypair generation ────────────────────────────────────────────────
        public static (string privateKey, string publicKey) GenerateKeypair()
        {
            if (IsTunnelDllAvailable())
            {
                try
                {
                    NativeMethods.SetDllDirectory(ExeDir);
                    var pub  = new byte[32];
                    var priv = new byte[32];
                    if (NativeMethods.WireGuardGenerateKeypair(pub, priv))
                        return (Convert.ToBase64String(priv), Convert.ToBase64String(pub));
                }
                catch { }
            }
            return GenerateKeypairPure();
        }

        private static (string, string) GenerateKeypairPure()
        {
            var priv = new byte[32];
            System.Security.Cryptography.RandomNumberGenerator.Fill(priv);
            priv[0] &= 248; priv[31] &= 127; priv[31] |= 64;
            return (Convert.ToBase64String(priv),
                    Convert.ToBase64String(Curve25519.ScalarMultBase(priv)));
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Private — mirrors WireGuardClient.TunnelService.InstallAndStartService
        // ══════════════════════════════════════════════════════════════════════
        private static void InstallAndStart(string serviceName, string tunnelName,
            string confPath, Action<string> log)
        {
            // Use MainModule.FileName — same as working WireGuardClient.
            // Environment.ProcessPath is unreliable in some publish configs.
            string exePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Cannot determine exe path.");

            // Both paths must be quoted in case they contain spaces.
            string binaryPath = $"\"{exePath}\" /service \"{confPath}\"";

            log("Installing service…");
            log($"  BinaryPath : {binaryPath}");
            log($"  Conf exists: {File.Exists(confPath)}");

            IntPtr scm = NativeMethods.OpenSCManager(null, null,
                NativeMethods.SC_MANAGER_ALL_ACCESS);
            if (scm == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed.");
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
                    throw new Win32Exception(err, $"CreateService failed (win32={err}).");
                }
                try
                {
                    // CRITICAL: set SERVICE_SID_TYPE_UNRESTRICTED (required by wireguard-nt)
                    var sidInfo = new NativeMethods.SERVICE_SID_INFO
                        { dwServiceSidType = NativeMethods.SERVICE_SID_TYPE_UNRESTRICTED };
                    if (!NativeMethods.ChangeServiceConfig2(svc,
                            NativeMethods.SERVICE_CONFIG_SERVICE_SID_INFO, ref sidInfo))
                        log($"[WARN] ChangeServiceConfig2 failed ({Marshal.GetLastWin32Error()})");
                    else
                        log("  SERVICE_SID_TYPE_UNRESTRICTED set ✓");

                    log("Starting service…");
                    using var sc = new ServiceController(serviceName);
                    sc.Start();

                    // wireguard-NT's WireGuardTunnelService() installs the kernel tunnel,
                    // then returns immediately — the service process exits in ~50-100 ms.
                    // The SCM races through StartPending → Running → Stopped so fast that
                    // WaitForStatus(Running) almost never catches the Running state.
                    //
                    // Strategy: poll every 50 ms for up to 10 s.
                    //   • Running  → tunnel is up, service still alive  (rare but possible)
                    //   • Stopped  → tunnel is up, service exited cleanly (normal case)
                    //   • Anything else after 10 s → real failure
                    //
                    // We deliberately do NOT use WaitForStatus(Running) because that call
                    // throws TimeoutException when it misses the brief Running window,
                    // which confuses diagnostics and causes unnecessary 30-second waits.
                    {
                        var deadline = DateTime.UtcNow.AddSeconds(10);
                        bool tunnelUp = false;
                        while (DateTime.UtcNow < deadline)
                        {
                            System.Threading.Thread.Sleep(50);
                            try { sc.Refresh(); } catch { }
                            var status = sc.Status;
                            if (status == ServiceControllerStatus.Running)
                            {
                                log($"Service running ✓  (status: Running)");
                                tunnelUp = true;
                                break;
                            }
                            if (status == ServiceControllerStatus.Stopped)
                            {
                                log("Service exited cleanly — tunnel is up in kernel.");
                                tunnelUp = true;
                                break;
                            }
                        }
                        if (!tunnelUp)
                        {
                            sc.Refresh();
                            throw new InvalidOperationException(
                                $"Service did not start within 10 s (status: {sc.Status}).");
                        }
                    }
                }
                finally { NativeMethods.CloseServiceHandle(svc); }
            }
            finally { NativeMethods.CloseServiceHandle(scm); }
        }

        // Stop (if running) then delete the SCM entry.
        // Mirrors WireGuardClient.TunnelService.EnsureStopped exactly.
        private static void EnsureStopped(string serviceName, Action<string> log)
        {
            try
            {
                using var sc = new ServiceController(serviceName);
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    log($"  Stopping existing service {serviceName}…");
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped,
                        TimeSpan.FromSeconds(15));
                }
            }
            catch { /* service may not exist — that is fine */ }

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
                try   { NativeMethods.DeleteService(svc); }
                finally { NativeMethods.CloseServiceHandle(svc); }
            }
            finally { NativeMethods.CloseServiceHandle(scm); }
        }

        internal static void WriteDebug(string message)
        {
            try { File.AppendAllText(Path.Combine(ExeDir, "service-debug.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}"); }
            catch { }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Ringlogger — reads tunnel.dll memory-mapped ring-log
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
            new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly string _path;
        private MemoryMappedFile? _mmf;
        private MemoryMappedViewAccessor? _view;
        private uint _cursor = uint.MaxValue;

        public Ringlogger(string path) { _path = path; }

        public (DateTime ts, string text)[] CollectNewLines()
        {
            try
            {
                EnsureOpen();
                if (_view == null) return Array.Empty<(DateTime, string)>();
                uint magic = _view.ReadUInt32(0);
                if (magic != MagicNumber) return Array.Empty<(DateTime, string)>();
                uint write = _view.ReadUInt32(4);
                if (_cursor == uint.MaxValue) { _cursor = write; return Array.Empty<(DateTime, string)>(); }
                if (_cursor == write) return Array.Empty<(DateTime, string)>();
                uint count = write >= _cursor ? write - _cursor : (uint)MaxEntries - _cursor + write;
                if (count > MaxEntries) count = (uint)MaxEntries;
                var lines = new List<(DateTime, string)>();
                for (uint i = 0; i < count; i++)
                {
                    uint slot = (_cursor + i) % MaxEntries;
                    long off  = HeaderSize + slot * LineSize;
                    long ticks = _view.ReadInt64(off);
                    if (ticks == 0) continue;
                    var ts = UnixEpoch.AddTicks(ticks).ToLocalTime();
                    var buf = new byte[TextSize];
                    _view.ReadArray(off + TimestampSize, buf, 0, TextSize);
                    int len = Array.IndexOf(buf, (byte)0);
                    string t = Encoding.UTF8.GetString(buf, 0, len < 0 ? TextSize : len).Trim();
                    if (!string.IsNullOrWhiteSpace(t)) lines.Add((ts, t));
                }
                _cursor = write;
                return lines.ToArray();
            }
            catch { Close(); return Array.Empty<(DateTime, string)>(); }
        }

        private void EnsureOpen()
        {
            if (_view != null) return;
            if (!File.Exists(_path)) return;
            long sz = HeaderSize + (long)MaxEntries * LineSize;
            _mmf  = MemoryMappedFile.CreateFromFile(_path, FileMode.Open, null, sz,
                        MemoryMappedFileAccess.Read);
            _view = _mmf.CreateViewAccessor(0, sz, MemoryMappedFileAccess.Read);
        }

        private void Close() { _view?.Dispose(); _view = null; _mmf?.Dispose(); _mmf = null; }
        public void Dispose() => Close();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  Curve25519 — pure-C# fallback for keypair generation when DLLs absent
    // ═══════════════════════════════════════════════════════════════════════════
    internal static class Curve25519
    {
        public static byte[] ScalarMultBase(byte[] s)
        { var b = new byte[32]; b[0] = 9; return ScalarMult(s, b); }

        public static byte[] ScalarMult(byte[] s, byte[] p)
        {
            long[] x1=Fe.FB(p),x2=Fe.One(),z2=Fe.Zero(),x3=Fe.FB(p),z3=Fe.One();
            int sw=0;
            for(int pos=254;pos>=0;--pos)
            {
                int b=(s[pos/8]>>(pos&7))&1;sw^=b;
                Fe.CS(x2,x3,sw);Fe.CS(z2,z3,sw);sw=b;
                long[]A=Fe.Add(x2,z2),AA=Fe.Sq(A),B=Fe.Sub(x2,z2),BB=Fe.Sq(B);
                long[]E=Fe.Sub(AA,BB),C=Fe.Add(x3,z3),D=Fe.Sub(x3,z3);
                long[]DA=Fe.Mul(D,A),CB=Fe.Mul(C,B);
                x3=Fe.Sq(Fe.Add(DA,CB));z3=Fe.Mul(x1,Fe.Sq(Fe.Sub(DA,CB)));
                x2=Fe.Mul(AA,BB);z2=Fe.Mul(E,Fe.Add(AA,Fe.M121666(E)));
            }
            Fe.CS(x2,x3,sw);Fe.CS(z2,z3,sw);
            return Fe.TB(Fe.Mul(x2,Fe.Inv(z2)));
        }

        private static class Fe
        {
            public static long[]Zero()=>new long[10];
            public static long[]One(){var f=new long[10];f[0]=1;return f;}
            public static long[]FB(byte[]s){var h=new long[10];h[0]=L4(s,0);h[1]=L3(s,4)<<6;h[2]=L3(s,7)<<5;h[3]=L3(s,10)<<3;h[4]=L3(s,13)<<2;h[5]=L4(s,16);h[6]=L3(s,20)<<7;h[7]=L3(s,23)<<5;h[8]=L3(s,26)<<4;h[9]=(L3(s,29)&8388607)<<2;Cr(h);return h;}
            public static byte[]TB(long[]h){Cr(h);long q=(19*h[9]+(1L<<24))>>25;for(int i=0;i<9;i++){q+=h[i];q>>=i%2==0?26:25;}h[0]+=19*q;Cr(h);var s=new byte[32];s[0]=(byte)h[0];s[1]=(byte)(h[0]>>8);s[2]=(byte)(h[0]>>16);s[3]=(byte)((h[0]>>24)|(h[1]<<2));s[4]=(byte)(h[1]>>6);s[5]=(byte)(h[1]>>14);s[6]=(byte)((h[1]>>22)|(h[2]<<3));s[7]=(byte)(h[2]>>5);s[8]=(byte)(h[2]>>13);s[9]=(byte)((h[2]>>21)|(h[3]<<5));s[10]=(byte)(h[3]>>3);s[11]=(byte)(h[3]>>11);s[12]=(byte)((h[3]>>19)|(h[4]<<6));s[13]=(byte)(h[4]>>2);s[14]=(byte)(h[4]>>10);s[15]=(byte)(h[4]>>18);s[16]=(byte)h[5];s[17]=(byte)(h[5]>>8);s[18]=(byte)(h[5]>>16);s[19]=(byte)((h[5]>>24)|(h[6]<<1));s[20]=(byte)(h[6]>>7);s[21]=(byte)(h[6]>>15);s[22]=(byte)((h[6]>>23)|(h[7]<<3));s[23]=(byte)(h[7]>>5);s[24]=(byte)(h[7]>>13);s[25]=(byte)((h[7]>>21)|(h[8]<<4));s[26]=(byte)(h[8]>>4);s[27]=(byte)(h[8]>>12);s[28]=(byte)((h[8]>>20)|(h[9]<<6));s[29]=(byte)(h[9]>>2);s[30]=(byte)(h[9]>>10);s[31]=(byte)(h[9]>>18);return s;}
            public static long[]Add(long[]f,long[]g){var h=new long[10];for(int i=0;i<10;i++)h[i]=f[i]+g[i];return h;}
            public static long[]Sub(long[]f,long[]g){var h=new long[10];for(int i=0;i<10;i++)h[i]=f[i]-g[i];return h;}
            public static long[]Sq(long[]f)=>Mul(f,f);
            public static long[]M121666(long[]f){var h=new long[10];for(int i=0;i<10;i++)h[i]=f[i]*121666;return h;}
            public static long[]Mul(long[]f,long[]g){long f0=f[0],f1=f[1],f2=f[2],f3=f[3],f4=f[4],f5=f[5],f6=f[6],f7=f[7],f8=f[8],f9=f[9],g0=g[0],g1=g[1],g2=g[2],g3=g[3],g4=g[4],g5=g[5],g6=g[6],g7=g[7],g8=g[8],g9=g[9],g1_19=19*g1,g2_19=19*g2,g3_19=19*g3,g4_19=19*g4,g5_19=19*g5,g6_19=19*g6,g7_19=19*g7,g8_19=19*g8,g9_19=19*g9,f1_2=2*f1,f3_2=2*f3,f5_2=2*f5,f7_2=2*f7,f9_2=2*f9;var h=new long[10];h[0]=f0*g0+f1_2*g9_19+f2*g8_19+f3_2*g7_19+f4*g6_19+f5_2*g5_19+f6*g4_19+f7_2*g3_19+f8*g2_19+f9_2*g1_19;h[1]=f0*g1+f1*g0+f2*g9_19+f3*g8_19+f4*g7_19+f5*g6_19+f6*g5_19+f7*g4_19+f8*g3_19+f9*g2_19;h[2]=f0*g2+f1_2*g1+f2*g0+f3_2*g9_19+f4*g8_19+f5_2*g7_19+f6*g6_19+f7_2*g5_19+f8*g4_19+f9_2*g3_19;h[3]=f0*g3+f1*g2+f2*g1+f3*g0+f4*g9_19+f5*g8_19+f6*g7_19+f7*g6_19+f8*g5_19+f9*g4_19;h[4]=f0*g4+f1_2*g3+f2*g2+f3_2*g1+f4*g0+f5_2*g9_19+f6*g8_19+f7_2*g7_19+f8*g6_19+f9_2*g5_19;h[5]=f0*g5+f1*g4+f2*g3+f3*g2+f4*g1+f5*g0+f6*g9_19+f7*g8_19+f8*g7_19+f9*g6_19;h[6]=f0*g6+f1_2*g5+f2*g4+f3_2*g3+f4*g2+f5_2*g1+f6*g0+f7_2*g9_19+f8*g8_19+f9_2*g7_19;h[7]=f0*g7+f1*g6+f2*g5+f3*g4+f4*g3+f5*g2+f6*g1+f7*g0+f8*g9_19+f9*g8_19;h[8]=f0*g8+f1_2*g7+f2*g6+f3_2*g5+f4*g4+f5_2*g3+f6*g2+f7_2*g1+f8*g0+f9_2*g9_19;h[9]=f0*g9+f1*g8+f2*g7+f3*g6+f4*g5+f5*g4+f6*g3+f7*g2+f8*g1+f9*g0;Cr(h);return h;}
            public static long[]Inv(long[]z){long[]t=Sq(z),z9=Mul(Sq(Sq(t)),z),z11=Mul(z9,t),a=Mul(Sq(z11),z9);t=a;for(int i=0;i<4;i++)t=Sq(t);long[]b=Mul(t,a);t=b;for(int i=0;i<9;i++)t=Sq(t);long[]c=Mul(t,b);t=c;for(int i=0;i<19;i++)t=Sq(t);t=Mul(t,c);for(int i=0;i<10;i++)t=Sq(t);long[]d=Mul(t,b);t=d;for(int i=0;i<39;i++)t=Sq(t);t=Mul(t,d);for(int i=0;i<10;i++)t=Sq(t);t=Mul(t,b);for(int i=0;i<5;i++)t=Sq(t);return Mul(t,z9);}
            public static void CS(long[]f,long[]g,int b){long m=-(long)b;for(int i=0;i<10;i++){long x=m&(f[i]^g[i]);f[i]^=x;g[i]^=x;}}
            private static void Cr(long[]h){for(int i=0;i<10;i++){int sh=i%2==0?26:25;long c=(h[i]+(1L<<(sh-1)))>>sh;h[(i+1)%10]+=c*(i==9?19:1);h[i]-=c<<sh;}}
            private static long L4(byte[]s,int o)=>(long)(uint)(s[o]|s[o+1]<<8|s[o+2]<<16|s[o+3]<<24);
            private static long L3(byte[]s,int o)=>(long)(uint)(s[o]|s[o+1]<<8|s[o+2]<<16);
        }
    }
}
