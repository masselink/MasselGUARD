# MasselGUARD tunnel.dll debug script
# Run as Administrator in PowerShell:
#   & "C:\Program Files\MasselGUARD\debug-tunnel.ps1" -ConfPath "C:\ProgramData\MasselGUARD\tunnels\test.conf"

param(
    [string]$ExeDir  = "C:\Program Files\MasselGUARD",
    [string]$ConfPath = "C:\ProgramData\MasselGUARD\tunnels\test.conf"
)

Write-Host ""
Write-Host "====================================================="
Write-Host "  MasselGUARD tunnel.dll diagnostics"
Write-Host "====================================================="
Write-Host ""

# ?????? 1. Check files ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????
Write-Host "[1] Checking files in: $ExeDir"
$tunnelDll   = Join-Path $ExeDir "tunnel.dll"
$wireguardDll = Join-Path $ExeDir "wireguard.dll"

if (Test-Path $tunnelDll) {
    $fi = Get-Item $tunnelDll
    Write-Host "    tunnel.dll      : OK  ($([math]::Round($fi.Length/1KB)) KB, $($fi.LastWriteTime))"
    # Check architecture
    $bytes = [System.IO.File]::ReadAllBytes($tunnelDll)
    $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
    $machine  = [System.BitConverter]::ToUInt16($bytes, $peOffset + 4)
    $arch = switch ($machine) { 0x8664 {"x64"} 0x014C {"x86"} 0xAA64 {"ARM64"} default {"unknown (0x$($machine.ToString('X4')))"} }
    Write-Host "    tunnel.dll arch : $arch"
} else {
    Write-Host "    tunnel.dll      : MISSING!" -ForegroundColor Red
}

if (Test-Path $wireguardDll) {
    $fi = Get-Item $wireguardDll
    Write-Host "    wireguard.dll   : OK  ($([math]::Round($fi.Length/1KB)) KB, $($fi.LastWriteTime))"
    $bytes = [System.IO.File]::ReadAllBytes($wireguardDll)
    $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3C)
    $machine  = [System.BitConverter]::ToUInt16($bytes, $peOffset + 4)
    $arch = switch ($machine) { 0x8664 {"x64"} 0x014C {"x86"} 0xAA64 {"ARM64"} default {"unknown (0x$($machine.ToString('X4')))"} }
    Write-Host "    wireguard.dll arch: $arch"
} else {
    Write-Host "    wireguard.dll   : MISSING!" -ForegroundColor Red
}

Write-Host ""

# ?????? 2. Check .conf file ?????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????
Write-Host "[2] Checking conf file: $ConfPath"
if (Test-Path $ConfPath) {
    Write-Host "    File exists     : OK"
    $acl = (Get-Acl $ConfPath).Access | ForEach-Object { "$($_.IdentityReference) = $($_.FileSystemRights)" }
    Write-Host "    ACL             :"
    $acl | ForEach-Object { Write-Host "      $_" }
    Write-Host "    Content preview :"
    Get-Content $ConfPath | Select-Object -First 10 | ForEach-Object { Write-Host "      $_" }
} else {
    Write-Host "    File            : MISSING!" -ForegroundColor Red
}
Write-Host ""

# ?????? 3. Check process architecture ????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????
Write-Host "[3] Process info"
Write-Host "    PowerShell arch : $(if ([Environment]::Is64BitProcess) {'x64'} else {'x86'})"
Write-Host "    OS arch         : $(if ([Environment]::Is64BitOperatingSystem) {'x64'} else {'x86'})"
Write-Host "    Running as      : $([Security.Principal.WindowsIdentity]::GetCurrent().Name)"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]"Administrator")
Write-Host "    Is admin        : $isAdmin"
Write-Host ""

# ?????? 4. Try loading tunnel.dll via P/Invoke ?????????????????????????????????????????????????????????????????????????????????????????????????????????
Write-Host "[4] Loading wireguard.dll + tunnel.dll"

$code = @"
using System;
using System.Runtime.InteropServices;
public class WgLoader {
    [DllImport("kernel32", CharSet=CharSet.Unicode)] public static extern IntPtr LoadLibraryW(string s);
    [DllImport("kernel32", CharSet=CharSet.Unicode)] public static extern bool SetDllDirectory(string s);
    [DllImport("kernel32", CharSet=CharSet.Ansi)]   public static extern IntPtr GetProcAddress(IntPtr h, string s);
    [DllImport("kernel32")] public static extern int GetLastError();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate bool WireGuardTunnelServiceDelegate([MarshalAs(UnmanagedType.LPWStr)] string conf);

    public static string Test(string exeDir, string confPath) {
        System.IO.Directory.SetCurrentDirectory(exeDir);
        SetDllDirectory(exeDir);
        var wg = LoadLibraryW(System.IO.Path.Combine(exeDir, "wireguard.dll"));
        if (wg == IntPtr.Zero) return "LoadLibrary(wireguard.dll) failed: " + GetLastError();
        var tn = LoadLibraryW("tunnel.dll");
        if (tn == IntPtr.Zero) return "LoadLibrary(tunnel.dll) failed: " + GetLastError();
        var fn = GetProcAddress(tn, "WireGuardTunnelService");
        if (fn == IntPtr.Zero) return "GetProcAddress(WireGuardTunnelService) failed: " + GetLastError();
        return "DLLs loaded OK. Calling WireGuardTunnelService...";
    }
}
"@

try {
    Add-Type -TypeDefinition $code -Language CSharp
    $result = [WgLoader]::Test($ExeDir, $ConfPath)
    Write-Host "    $result"

    if ($result -like "DLLs loaded OK*") {
        Write-Host ""
        Write-Host "[5] Calling WireGuardTunnelService (will block if successful, Ctrl+C to stop)"
        Write-Host "    Watch the Windows Event Log > System for WireGuard entries"
        Write-Host "    Run in a separate window: Get-EventLog System -Source WireGuard -Newest 20"
        Write-Host ""
        # Actually call the function
        $tnLib = [WgLoader]::LoadLibraryW("tunnel.dll")
        $fn    = [WgLoader]::GetProcAddress($tnLib, "WireGuardTunnelService")
        $del   = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer($fn, [WgLoader+WireGuardTunnelServiceDelegate])
        Write-Host "    Calling now..."
        $ok = $del.Invoke($ConfPath)
        Write-Host "    Result: $ok (exit code 0=success)"
    }
} catch {
    Write-Host "    ERROR: $_" -ForegroundColor Red
}

Write-Host ""

# ?????? 5. Check Windows Event Log for WireGuard ???????????????????????????????????????????????????????????????????????????????????????????????????
Write-Host "[6] Recent WireGuard events in Windows Event Log:"
try {
    Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='WireGuard*'} -MaxEvents 10 -ErrorAction Stop |
        ForEach-Object { Write-Host "    $($_.TimeCreated) [$($_.LevelDisplayName)] $($_.Message)" }
} catch {
    Write-Host "    (no WireGuard events found or log unavailable)"
}

Write-Host ""
Write-Host "====================================================="
Write-Host "  Done. If WireGuardTunnelService returned exit 2:"
Write-Host "  - Check Windows Event Log > System for details"
Write-Host "  - Verify the .conf PrivateKey is a valid base64 key"
Write-Host "  - Verify tunnel.dll and wireguard.dll are both x64"
Write-Host "  - Try: Get-WinEvent System | Where-Object {`$_.Message -like '*WireGuard*'}"
Write-Host "====================================================="

# ?????? 7. Check WireGuard/Wintun kernel drivers ??????????????????????????????????????????????????????????????????????????????????????????????????????
Write-Host ""
Write-Host "[7] Kernel driver status:"
@('wintun', 'wireguard', 'WireGuardTunnel*') | ForEach-Object {
    $svc = Get-Service -Name $_ -ErrorAction SilentlyContinue
    if ($svc) { Write-Host "    $($svc.Name): $($svc.Status)" }
    else       { Write-Host "    ${_}: not found" }
}

Write-Host ""
Write-Host "[8] All WireGuardTunnel services:"
Get-Service -Name "WireGuardTunnel*" -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "    $($_.Name): $($_.Status)" }
if (-not (Get-Service -Name "WireGuardTunnel*" -ErrorAction SilentlyContinue)) {
    Write-Host "    (none)"
}

Write-Host ""
Write-Host "[9] Recent System + Application event log (last 60s):"
$cutoff = (Get-Date).AddSeconds(-60)
Get-EventLog -LogName System -After $cutoff -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "    SYS [$($_.Source)] $($_.Message -replace '\n',' ')" }
Get-EventLog -LogName Application -After $cutoff -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "    APP [$($_.Source)] $($_.Message -replace '\n',' ')" }
