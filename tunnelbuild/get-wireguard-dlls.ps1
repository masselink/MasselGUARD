param(
    # Target architecture in MasselGUARD terms: x64 | arm64
    [ValidateSet('x64','arm64')]
    [string]$Arch = 'x64',
    # Scratch/build directory (cloned repo, downloaded zips, intermediates).
    [string]$Work,
    # Final destination for the arch's DLLs, e.g. ...\wireguard-deps\arm64
    [string]$Out
)

$ErrorActionPreference = 'Continue'

# ── Arch mapping ────────────────────────────────────────────────────────────
# MasselGUARD arch -> wireguard-nt zip subfolder / Go GOARCH / PE machine / CC
# wgMinKb: floor for a valid wireguard-NT wireguard.dll (which embeds the driver). Size is
# arch-dependent (official v1.1: amd64 ~1321 KB, arm64 ~667 KB, x86 ~1857 KB); it only needs
# to sit above the driverless WireGuard-for-Windows dll (~400 KB).
switch ($Arch) {
    'x64'   { $wgFolder='amd64'; $goarch='amd64'; $peMachine=0x8664; $cc='';                          $wgMinKb=900 }
    'arm64' { $wgFolder='arm64'; $goarch='arm64'; $peMachine=0xAA64; $cc='aarch64-w64-mingw32-clang'; $wgMinKb=500 }
}

if (-not $Work) { throw 'Missing -Work directory.' }
if (-not $Out)  { throw 'Missing -Out directory.' }
if (-not (Test-Path $Work)) { New-Item $Work -ItemType Directory -Force | Out-Null }
if (-not (Test-Path $Out))  { New-Item $Out  -ItemType Directory -Force | Out-Null }

$wgDll = Join-Path $Out 'wireguard.dll'
$tnDll = Join-Path $Out 'tunnel.dll'

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

Write-Host "  === Building WireGuard DLLs for $Arch ($wgFolder / GOARCH=$goarch) ==="

# ── wireguard.dll (wireguard-NT, per-arch) ──────────────────────────────────
Write-Host '  [1/2] wireguard.dll (wireguard-NT)...'

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
    if (-not (Test-Path $ext)) { Expand-Archive $zip $ext -Force }

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

if ((Test-Path $tnDll) -and ((Get-PeMachine $tnDll) -eq $peMachine)) {
    Write-Host "        Already cached ($Arch)."
} else {
    if (Test-Path $tnDll) { Remove-Item $tnDll -Force }  # wrong-arch cache

    # Refresh PATH so freshly installed Go/toolchains are visible.
    $env:PATH = [Environment]::GetEnvironmentVariable('PATH','Machine') + ';' +
                [Environment]::GetEnvironmentVariable('PATH','User')

    if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
        Write-Host ''
        Write-Host '  ERROR: Go not found on PATH.  Install: https://go.dev/dl/'
        throw 'Go not found.'
    }
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Host '  ERROR: git not found.  Install: https://git-scm.com/'
        throw 'git not found.'
    }

    # ARM64 needs an aarch64 Windows C toolchain for CGO — plain amd64 MinGW
    # cannot cross-compile tunnel.dll. llvm-mingw provides aarch64-w64-mingw32-clang.
    if ($cc) {
        if (-not (Get-Command $cc -ErrorAction SilentlyContinue)) {
            Write-Host ''
            Write-Host "  ERROR: ARM64 C compiler '$cc' not found on PATH."
            Write-Host '         tunnel.dll for arm64 is a CGO build and needs an aarch64'
            Write-Host '         Windows toolchain. Install llvm-mingw and add its bin\ to PATH:'
            Write-Host '           https://github.com/mstorsjo/llvm-mingw/releases'
            Write-Host '         (provides aarch64-w64-mingw32-clang / -gcc).'
            Write-Host ''
            throw "ARM64 C toolchain '$cc' not found."
        }
    }

    Write-Host "        Go found: $(& go version 2>&1)"
    $wgWinDir = Join-Path $Work 'wireguard-windows'
    if (-not (Test-Path (Join-Path $wgWinDir '.git'))) {
        Write-Host '        Cloning wireguard-windows...'
        $p = Start-Process git -ArgumentList @('clone','--depth=1','https://git.zx2c4.com/wireguard-windows',"`"$wgWinDir`"") `
             -NoNewWindow -Wait -PassThru `
             -RedirectStandardOutput (Join-Path $Work 'git_out.txt') `
             -RedirectStandardError  (Join-Path $Work 'git_err.txt')
        if ($p.ExitCode -ne 0) { throw "git clone failed (exit $($p.ExitCode))." }
    } else {
        Start-Process git -ArgumentList @('-C',$wgWinDir,'pull','--ff-only') -NoNewWindow -Wait | Out-Null
    }

    $buildDir = Join-Path $wgWinDir 'embeddable-dll-service'
    Write-Host "        Building tunnel.dll (GOARCH=$goarch, CGO)..."

    # Export the target toolchain env, then invoke the repo's build.bat inside a
    # wrapper so we capture its exit code. GOARCH/CC/CGO drive the cross-build.
    $env:GOOS        = 'windows'
    $env:GOARCH      = $goarch
    $env:CGO_ENABLED = '1'
    if ($cc) { $env:CC = $cc } else { Remove-Item Env:CC -ErrorAction SilentlyContinue }

    $exitFile = Join-Path $Work 'build_exit.txt'
    $wrapBat  = Join-Path $Work 'run_build.bat'
    # Re-assert the env inside the child cmd shell in case build.bat spawns cleanly.
    $wrap = "@echo off`r`n" +
            "set GOOS=windows`r`nset GOARCH=$goarch`r`nset CGO_ENABLED=1`r`n"
    if ($cc) { $wrap += "set CC=$cc`r`n" }
    $wrap += "cd /d `"$buildDir`"`r`ncall build.bat`r`necho %ERRORLEVEL% > `"$exitFile`"`r`n"
    Set-Content -Path $wrapBat -Value $wrap -Encoding ASCII
    Remove-Item $exitFile -ErrorAction SilentlyContinue

    Start-Process cmd.exe -ArgumentList "/c `"$wrapBat`"" -Wait
    Start-Sleep -Seconds 2
    if (Test-Path $exitFile) { Write-Host "        build.bat exited with code $((Get-Content $exitFile).Trim())" }
    Remove-Item $wrapBat,$exitFile -ErrorAction SilentlyContinue

    # Pick the produced tunnel.dll whose PE machine matches the target arch.
    # (build.bat may emit several arches into arch-named subfolders; verifying the
    #  PE header guarantees we grab the right one regardless of layout.)
    $built = Get-ChildItem $buildDir -Recurse -Filter 'tunnel.dll' -ErrorAction SilentlyContinue |
             Where-Object { (Get-PeMachine $_.FullName) -eq $peMachine } |
             Select-Object -First 1

    if (-not $built) {
        Write-Host ''
        Write-Host "  ERROR: build.bat ran but no $Arch tunnel.dll (PE machine 0x$("{0:X4}" -f $peMachine)) was produced."
        if ($Arch -eq 'arm64') {
            Write-Host '         Ensure the aarch64 toolchain is on PATH and that build.bat'
            Write-Host '         honours GOARCH=arm64 / CC. You can also drop a prebuilt'
            Write-Host "         arm64 tunnel.dll into: $Out"
        }
        throw "No $Arch tunnel.dll produced."
    }
    Copy-Item $built.FullName $tnDll -Force
    Write-Host "        tunnel.dll built and cached ($Arch)."

    Remove-Item Env:CC -ErrorAction SilentlyContinue
}

# ── Summary ─────────────────────────────────────────────────────────────────
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
