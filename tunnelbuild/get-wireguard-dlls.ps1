param(
    # Target architecture in MasselGUARD terms: x64 | arm64
    [ValidateSet('x64','arm64')]
    [string]$Arch = 'x64',
    # Scratch/build directory (cloned repo, downloaded zips, intermediates).
    [string]$Work,
    # Final destination for the arch's DLLs, e.g. ...\wireguard-deps\arm64
    [string]$Out,
    # Discard any cached DLLs and rebuild/re-fetch from scratch.
    [switch]$Force
)

$ErrorActionPreference = 'Continue'

# ── Arch mapping ────────────────────────────────────────────────────────────
# MasselGUARD arch -> wireguard-nt zip subfolder / Go GOARCH (display) / PE machine
# wgMinKb: floor for a valid wireguard-NT wireguard.dll (which embeds the driver). Size is
# arch-dependent (official v1.1: amd64 ~1321 KB, arm64 ~667 KB, x86 ~1857 KB); it only needs
# to sit above the driverless WireGuard-for-Windows dll (~400 KB).
switch ($Arch) {
    'x64'   { $wgFolder='amd64'; $goarch='amd64'; $peMachine=0x8664; $wgMinKb=900 }
    'arm64' { $wgFolder='arm64'; $goarch='arm64'; $peMachine=0xAA64; $wgMinKb=500 }
}

if (-not $Work) { throw 'Missing -Work directory.' }
if (-not $Out)  { throw 'Missing -Out directory.' }
if (-not (Test-Path $Work)) { New-Item $Work -ItemType Directory -Force | Out-Null }
if (-not (Test-Path $Out))  { New-Item $Out  -ItemType Directory -Force | Out-Null }

$wgDll = Join-Path $Out 'wireguard.dll'
$tnDll = Join-Path $Out 'tunnel.dll'

# On any fatal error during a -Force rebuild, roll back: if a DLL was set aside
# (.forcebak) and its rebuild never produced a replacement, restore the backup so
# a failed rebuild never leaves you worse off than before (these DLLs aren't in git).
trap {
    foreach ($f in @($wgDll,$tnDll)) {
        if ((Test-Path "$f.forcebak") -and -not (Test-Path $f)) {
            Move-Item "$f.forcebak" $f -Force
            Write-Host "  [rollback] restored cached $(Split-Path $f -Leaf) after failure."
        }
    }
    break
}

# Reads the PE Machine field of a DLL/EXE (or $null on failure).
function Get-PeMachine([string]$Path) {
    try {
        $fs = [System.IO.File]::OpenRead($Path)
        try {
            $br = New-Object System.IO.BinaryReader($fs)
            if ($fs.Length -lt 0x40) { return $null }
            $fs.Position = 0x3C
            $peOff = $br.ReadInt32()
            if ($peOff -le 0 -or ($peOff + 6) -gt $fs.Length) { return $null }
            $fs.Position = $peOff
            if ($br.ReadUInt32() -ne 0x00004550) { return $null }  # 'PE\0\0'
            return $br.ReadUInt16()
        } finally { $fs.Dispose() }
    } catch { return $null }
}

# SHA256 of a file as lowercase hex, via .NET. Avoids the Get-FileHash cmdlet, which
# has been observed "not recognized" in some locked-down corporate PowerShell hosts.
function Get-Sha256([string]$Path) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $fs = [System.IO.File]::OpenRead($Path)
        try { return ([BitConverter]::ToString($sha.ComputeHash($fs)) -replace '-','').ToLower() }
        finally { $fs.Dispose() }
    } finally { $sha.Dispose() }
}

# wireguard-windows' build.bat fetches wireguard-tools from git.zx2c4.com via curl.
# That cgit snapshot endpoint drops the connection on some networks (curl error 56,
# "server closed abruptly"), while every download.wireguard.com file and the GitHub
# mirror download fine. Rewrite that one line to pull wireguard-tools from the official
# GitHub mirror instead, computing the mirror archive's SHA256 dynamically so build.bat's
# integrity check still passes. Idempotent; no-op if already patched, the line's format
# changed, or the mirror can't be reached (then build.bat tries the original URL).
function Repair-WgToolsDownload([string]$buildBat) {
    if (-not (Test-Path $buildBat)) { return }
    $text = [System.IO.File]::ReadAllText($buildBat)
    $rx = 'call :download wireguard-tools\.zip https://git\.zx2c4\.com/wireguard-tools/snapshot/wireguard-tools-([0-9a-f]{40})\.zip ([0-9a-f]{64}) (".*?")'
    $m = [regex]::Match($text, $rx)
    if (-not $m.Success) { return }
    $commit = $m.Groups[1].Value
    $rest   = $m.Groups[3].Value
    $ghUrl  = "https://github.com/WireGuard/wireguard-tools/archive/$commit.zip"
    Write-Host '        [workaround] git.zx2c4.com snapshot is unreliable here; sourcing wireguard-tools from the GitHub mirror...'
    $tmp = Join-Path $Work 'wt-mirror.zip'
    try {
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $ghUrl -OutFile $tmp -UseBasicParsing -TimeoutSec 120
    } catch {
        Write-Host "        [workaround] GitHub mirror fetch failed ($($_.Exception.Message)); leaving build.bat unchanged."
        return
    }
    $ghHash = Get-Sha256 $tmp
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    $newLine = "call :download wireguard-tools.zip $ghUrl $ghHash $rest"
    $text = $text.Replace($m.Value, $newLine)
    [System.IO.File]::WriteAllText($buildBat, $text)
    Write-Host "        [workaround] build.bat now fetches wireguard-tools $($commit.Substring(0,10)) from GitHub (sha256 $($ghHash.Substring(0,12))...)."
}

Write-Host "  === Building WireGuard DLLs for $Arch ($wgFolder / GOARCH=$goarch) ==="

# ── wireguard.dll (wireguard-NT, per-arch) ──────────────────────────────────
Write-Host '  [1/2] wireguard.dll (wireguard-NT)...'

if ($Force -and (Test-Path $wgDll)) {
    Write-Host '        -Force: setting aside cached wireguard.dll and re-fetching...'
    Move-Item $wgDll "$wgDll.forcebak" -Force
}

if (Test-Path $wgDll) {
    $m = Get-PeMachine $wgDll
    if (((Get-Item $wgDll).Length -gt ($wgMinKb*1024)) -and ($m -eq $peMachine)) {
        Write-Host "        Already cached (wireguard-NT $Arch)."
    } else {
        Write-Host "        Cached wireguard.dll is wrong size/arch -- re-extracting..."
        Remove-Item $wgDll -Force
    }
}

if (-not (Test-Path $wgDll)) {
    Write-Host '        Resolving latest wireguard-nt release...'
    $page = (Invoke-WebRequest 'https://download.wireguard.com/wireguard-nt/' -UseBasicParsing).Content
    $ver = [regex]::Matches($page, 'wireguard-nt-([\d.]+)\.zip') |
           ForEach-Object { $_.Groups[1].Value } |
           Sort-Object { [version]$_ } | Select-Object -Last 1
    if (-not $ver) { throw 'Cannot determine latest wireguard-nt version.' }

    $zip = Join-Path $Work "wireguard-nt-$ver.zip"
    if (-not (Test-Path $zip)) {
        Invoke-WebRequest "https://download.wireguard.com/wireguard-nt/wireguard-nt-$ver.zip" `
            -OutFile $zip -UseBasicParsing
    }
    $ext = Join-Path $Work "wireguard-nt-$ver"
    if (-not (Test-Path $ext)) {
        # Use tar (bundled on Win10+, same tool build.bat uses) rather than the
        # Expand-Archive cmdlet, which autoloads on demand and can be unavailable
        # in locked-down hosts. bsdtar extracts .zip fine.
        New-Item $ext -ItemType Directory -Force | Out-Null
        & tar.exe -xf $zip -C $ext
        if ($LASTEXITCODE -ne 0) { throw "Failed to extract $zip (tar exit $LASTEXITCODE)." }
    }

    # The zip ships bin\{amd64,arm64,x86,arm}\wireguard.dll — pick our arch.
    $dll = Get-ChildItem $ext -Recurse -Filter 'wireguard.dll' |
           Where-Object { $_.DirectoryName -match "[\\/]$wgFolder([\\/]|$)" } |
           Select-Object -First 1
    if (-not $dll) { throw "wireguard.dll ($wgFolder) not found in wireguard-nt zip." }
    Copy-Item $dll.FullName $wgDll -Force
    $kb = [math]::Round((Get-Item $wgDll).Length / 1KB)
    Write-Host "        wireguard.dll ready (wireguard-NT v$ver, $Arch, $kb KB)."
}

# ── tunnel.dll (built from source, per-arch) ────────────────────────────────
Write-Host '  [2/2] tunnel.dll (build from source)...'

if ($Force -and (Test-Path $tnDll)) {
    Write-Host '        -Force: setting aside cached tunnel.dll and rebuilding...'
    Move-Item $tnDll "$tnDll.forcebak" -Force
}

if ((Test-Path $tnDll) -and ((Get-PeMachine $tnDll) -eq $peMachine)) {
    Write-Host "        Already cached ($Arch)."
} else {
    if (Test-Path $tnDll) { Remove-Item $tnDll -Force }  # wrong-arch cache

    # Refresh PATH so freshly installed Go/toolchains are visible.
    $env:PATH = [Environment]::GetEnvironmentVariable('PATH','Machine') + ';' +
                [Environment]::GetEnvironmentVariable('PATH','User')

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Host '  ERROR: git not found.  Install: https://git-scm.com/'
        throw 'git not found.'
    }

    # tunnel.dll is built by wireguard-windows' own hermetic build.bat, which
    # downloads its own Go + llvm-mingw toolchain (x86/amd64/arm64) into .deps and
    # sets GOROOT / GOARCH / CC itself. So NO system Go or C compiler is required here
    # -- only git (to fetch the source) and network access (build.bat pulls its deps).
    # It builds all arches every run; we select the PE-matching tunnel.dll afterwards.
    $wgWinDir = Join-Path $Work 'wireguard-windows'
    if (Test-Path (Join-Path $wgWinDir '.git')) {
        Write-Host '        Reusing existing wireguard-windows clone (keeps its .deps cache).'
    } else {
        Write-Host '        Cloning wireguard-windows...'
        $p = Start-Process git -ArgumentList @('clone','--depth=1','https://git.zx2c4.com/wireguard-windows',"`"$wgWinDir`"") `
             -NoNewWindow -Wait -PassThru `
             -RedirectStandardOutput (Join-Path $Work 'git_out.txt') `
             -RedirectStandardError  (Join-Path $Work 'git_err.txt')
        if ($p.ExitCode -ne 0) { throw "git clone failed (exit $($p.ExitCode))." }
    }

    # Work around the unreliable git.zx2c4.com wireguard-tools snapshot download.
    Repair-WgToolsDownload (Join-Path $wgWinDir 'build.bat')

    $buildDir = Join-Path $wgWinDir 'embeddable-dll-service'

    # Invoke the repo's own build.bat via a wrapper that captures its exit code and
    # redirects output to a log. We intentionally do NOT set GOOS/GOARCH/CC here --
    # build.bat overrides them from its self-downloaded .deps toolchain. Its dependency
    # downloads can hit transient TLS drops (schannel "server closed abruptly"), so we
    # retry a few times; on a retry build.bat reuses whatever .deps it already fetched.
    $exitFile = Join-Path $Work 'build_exit.txt'
    $logFile  = Join-Path $Work 'build_log.txt'
    $wrapBat  = Join-Path $Work 'run_build.bat'
    $wrap = "@echo off`r`ncd /d `"$buildDir`"`r`n" +
            "call build.bat > `"$logFile`" 2>&1`r`necho %ERRORLEVEL% > `"$exitFile`"`r`n"
    Set-Content -Path $wrapBat -Value $wrap -Encoding ASCII

    $built = $null
    $maxTries = 3
    for ($try = 1; $try -le $maxTries; $try++) {
        Write-Host "        Building tunnel.dll via build.bat (attempt $try/$maxTries; it downloads its own toolchain)..."
        Remove-Item $exitFile -ErrorAction SilentlyContinue
        Start-Process cmd.exe -ArgumentList "/c `"$wrapBat`"" -Wait -NoNewWindow
        Start-Sleep -Seconds 2
        $rc = if (Test-Path $exitFile) { (Get-Content $exitFile).Trim() } else { '?' }
        Write-Host "        build.bat exited with code $rc"
        $built = Get-ChildItem $buildDir -Recurse -Filter 'tunnel.dll' -ErrorAction SilentlyContinue |
                 Where-Object { (Get-PeMachine $_.FullName) -eq $peMachine } |
                 Select-Object -First 1
        if ($built) { break }
        if ($try -lt $maxTries) {
            Write-Host '        No matching tunnel.dll yet (often a transient dependency download) -- retrying in 5s...'
            Start-Sleep -Seconds 5
        }
    }
    Remove-Item $wrapBat,$exitFile -ErrorAction SilentlyContinue

    if (-not $built) {
        Write-Host ''
        Write-Host "  ERROR: build.bat ran but no $Arch tunnel.dll (PE machine 0x$("{0:X4}" -f $peMachine)) was produced after $maxTries attempt(s)."
        if (Test-Path $logFile) {
            Write-Host '         ---- build.bat output (last 30 lines) ----'
            Get-Content $logFile -Tail 30 | ForEach-Object { Write-Host "         | $_" }
            Write-Host '         -------------------------------------------'
        }
        Write-Host '         If the tail shows a curl/download error, it is usually transient -- re-run.'
        Write-Host "         You can also drop a prebuilt $Arch tunnel.dll into: $Out"
        throw "No $Arch tunnel.dll produced."
    }
    Copy-Item $built.FullName $tnDll -Force
    Write-Host "        tunnel.dll built and cached ($Arch)."
}

# ── Summary ─────────────────────────────────────────────────────────────────
# Reached the end successfully — both DLLs are in place, so drop any -Force backups.
foreach ($f in @($wgDll,$tnDll)) {
    if (Test-Path "$f.forcebak") { Remove-Item "$f.forcebak" -Force }
}

$wgKb = [math]::Round((Get-Item $wgDll).Length / 1KB)
$tnKb = [math]::Round((Get-Item $tnDll).Length / 1KB)
Write-Host ''
Write-Host "  $Arch DLLs ready in $Out"
Write-Host "        wireguard.dll  ($wgKb KB, PE 0x$("{0:X4}" -f (Get-PeMachine $wgDll)))"
Write-Host "        tunnel.dll     ($tnKb KB, PE 0x$("{0:X4}" -f (Get-PeMachine $tnDll)))"
if ($wgKb -lt $wgMinKb) {
    Write-Host ''
    Write-Host "  WARNING: wireguard.dll ($wgKb KB) is smaller than expected for wireguard-NT $Arch."
}
