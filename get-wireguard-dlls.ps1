param(
    [string]$Deps,
    [string]$Dist
)

$ErrorActionPreference = 'Continue'

$wgDll = Join-Path $Deps 'wireguard.dll'
$tnDll = Join-Path $Deps 'tunnel.dll'

# ------ wireguard.dll -------------------------------------------------------
# IMPORTANT: MasselGUARD requires the wireguard-NT wireguard.dll (~1.3 MB),
# NOT the wireguard.dll from the WireGuard for Windows application (~400 KB).
#
# The WireGuard-for-Windows wireguard.dll calls into wireguard.sys, which is
# only present when WireGuard for Windows is installed. The wireguard-NT dll
# embeds the driver and installs it itself — no WireGuard app required.
#
# Download: https://download.wireguard.com/wireguard-nt/
Write-Host '  [1/2] wireguard.dll (wireguard-NT, not WireGuard for Windows)...'
if (Test-Path $wgDll) {
    $size = (Get-Item $wgDll).Length
    if ($size -gt 900000) {
        Write-Host '         Already cached (wireguard-NT).'
    } else {
        Write-Host "         WARNING: cached wireguard.dll is only $size bytes — this is the WireGuard-for-Windows"
        Write-Host '         version and will NOT work for standalone tunnels. Re-downloading wireguard-NT...'
        Remove-Item $wgDll -Force
    }
}

if (-not (Test-Path $wgDll)) {
    Write-Host '         Downloading from download.wireguard.com/wireguard-nt/ ...'
    $page = (Invoke-WebRequest 'https://download.wireguard.com/wireguard-nt/' -UseBasicParsing).Content
    $ver  = [regex]::Matches($page, 'wireguard-nt-([\d.]+)\.zip') |
            ForEach-Object { $_.Groups[1].Value } |
            Sort-Object { [version]$_ } |
            Select-Object -Last 1
    if (-not $ver) { throw 'Cannot determine latest wireguard-nt version.' }

    $zip = Join-Path $Deps "wireguard-nt-$ver.zip"
    Invoke-WebRequest "https://download.wireguard.com/wireguard-nt/wireguard-nt-$ver.zip" `
        -OutFile $zip -UseBasicParsing

    $ext = Join-Path $Deps "wireguard-nt-$ver"
    Expand-Archive $zip $ext -Force
    Remove-Item $zip -Force

    $dll = Get-ChildItem $ext -Recurse -Filter 'wireguard.dll' |
           Where-Object { $_.DirectoryName -match 'amd64' } |
           Select-Object -First 1
    if (-not $dll) { throw 'wireguard.dll (amd64) not found in wireguard-nt zip.' }
    Copy-Item $dll.FullName $wgDll -Force
    Write-Host "         wireguard.dll ready (wireguard-NT v$ver, $([math]::Round((Get-Item $wgDll).Length/1KB)) KB)."
}

# ------ tunnel.dll ----------------------------------------------------------
Write-Host '  [2/2] tunnel.dll...'
if (Test-Path $tnDll) {
    Write-Host '         Already cached.'
} else {
    # Refresh PATH so a freshly installed Go is visible
    $machinePath = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine')
    $userPath    = [System.Environment]::GetEnvironmentVariable('PATH', 'User')
    $env:PATH    = "$machinePath;$userPath"

    $goExe = Get-Command go -ErrorAction SilentlyContinue
    if (-not $goExe) {
        Write-Host ''
        Write-Host '  ERROR: Go not found on PATH.'
        Write-Host '  tunnel.dll must be compiled from source using Go + gcc (MinGW).'
        Write-Host ''
        Write-Host '  Option A -- Install Go, then re-run BUILD.bat:'
        Write-Host '    https://go.dev/dl/'
        Write-Host '    (Git for Windows includes gcc -- enable "Add to PATH" during install)'
        Write-Host ''
        Write-Host '  Option B -- Place tunnel.dll manually and re-run BUILD.bat:'
        Write-Host "    Copy tunnel.dll (x64) into: $Deps"
        Write-Host ''
        throw 'Go not found. See instructions above.'
    }

    $gitExe = Get-Command git -ErrorAction SilentlyContinue
    if (-not $gitExe) {
        Write-Host '  ERROR: git not found. Required to clone wireguard-windows.'
        Write-Host '    https://git-scm.com/'
        throw 'git not found.'
    }

    Write-Host "         Go found: $(& go version)"
    Write-Host '         Cloning wireguard-windows...'
    $wgWinDir = Join-Path $Deps 'wireguard-windows'

    if (-not (Test-Path (Join-Path $wgWinDir '.git'))) {
        git clone --depth=1 https://git.zx2c4.com/wireguard-windows $wgWinDir 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'git clone failed.' }
    } else {
        git -C $wgWinDir pull --ff-only 2>&1 | Out-Null
    }

    $buildDir = Join-Path $wgWinDir 'embeddable-dll-service'
    Write-Host '         Building tunnel.dll (may take a minute on first run)...'

    $exitFile = Join-Path $Deps 'build_exit.txt'
    $wrapBat  = Join-Path $Deps 'run_build.bat'
    Set-Content $wrapBat "@echo off`r`ncd /d `"$buildDir`"`r`ncall build.bat`r`necho %ERRORLEVEL% > `"$exitFile`"`r`n"
    Remove-Item $exitFile -ErrorAction SilentlyContinue

    $p = Start-Process cmd.exe -ArgumentList "/c `"$wrapBat`"" -Wait -PassThru
    Start-Sleep -Seconds 2

    $exitCode = 0
    if (Test-Path $exitFile) { $exitCode = [int](Get-Content $exitFile).Trim() }
    Write-Host "         build.bat exited with code $exitCode"
    Remove-Item $wrapBat, $exitFile -ErrorAction SilentlyContinue

    $builtDll = @(
        (Join-Path $buildDir 'amd64\tunnel.dll'),
        (Join-Path $buildDir 'x86_64\tunnel.dll')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $builtDll) {
        $found = Get-ChildItem $buildDir -Recurse -Filter 'tunnel.dll' -ErrorAction SilentlyContinue |
                 Select-Object -First 1
        if ($found) { $builtDll = $found.FullName }
    }

    if (-not $builtDll) {
        Write-Host ''
        Write-Host '  ERROR: build.bat ran but tunnel.dll was not produced.'
        Write-Host '         Make sure gcc/MinGW is on your PATH.'
        Write-Host ''
        Write-Host '  Option B -- place tunnel.dll manually and re-run BUILD.bat:'
        Write-Host "    Copy tunnel.dll (x64) into: $Deps"
        Write-Host ''
        throw 'tunnel.dll not produced by build.bat.'
    }

    Copy-Item $builtDll $tnDll -Force
    Write-Host '         tunnel.dll built and cached.'
}

# ------ Copy to dist --------------------------------------------------------
Write-Host ''
Write-Host '  Copying DLLs to dist...'
if (-not (Test-Path $Dist)) { New-Item $Dist -ItemType Directory | Out-Null }
Copy-Item $wgDll (Join-Path $Dist 'wireguard.dll') -Force
Copy-Item $tnDll (Join-Path $Dist 'tunnel.dll')    -Force
$wgSize = [math]::Round((Get-Item (Join-Path $Dist 'wireguard.dll')).Length / 1KB)
$tnSize = [math]::Round((Get-Item (Join-Path $Dist 'tunnel.dll')).Length    / 1KB)
Write-Host "         dist\wireguard.dll  ($wgSize KB)"
Write-Host "         dist\tunnel.dll     ($tnSize KB)"
if ($wgSize -lt 900) {
    Write-Host ''
    Write-Host '  WARNING: wireguard.dll is smaller than expected for wireguard-NT.'
    Write-Host '  Make sure you are NOT using the DLL from C:\Program Files\WireGuard\.'
    Write-Host '  That version requires the WireGuard app to be installed and will fail'
    Write-Host '  with "cannot find file" when starting the tunnel service.'
}
