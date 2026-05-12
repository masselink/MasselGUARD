param(
    [string]$Deps,
    [string]$Dist
)

$ErrorActionPreference = 'Continue'

$wgDll = Join-Path $Deps 'wireguard.dll'
$tnDll = Join-Path $Deps 'tunnel.dll'

# ------ wireguard.dll -------------------------------------------------------
# MasselGUARD requires the wireguard-NT wireguard.dll (~1.3 MB).
# The WireGuard-for-Windows wireguard.dll (~400 KB) will NOT work -- it
# requires wireguard.sys to be pre-installed by the WireGuard app.
# Download: https://download.wireguard.com/wireguard-nt/

Write-Host '  [1/2] wireguard.dll (wireguard-NT)...'

if (Test-Path $wgDll) {
    $size = (Get-Item $wgDll).Length
    if ($size -gt 900000) {
        Write-Host '        Already cached (wireguard-NT).'
    } else {
        Write-Host "        WARNING: cached wireguard.dll is $size bytes -- wrong version. Re-downloading..."
        Remove-Item $wgDll -Force
    }
}

if (-not (Test-Path $wgDll)) {
    Write-Host '        Downloading from download.wireguard.com/wireguard-nt/ ...'
    $page = (Invoke-WebRequest 'https://download.wireguard.com/wireguard-nt/' -UseBasicParsing).Content
    $matches2 = [regex]::Matches($page, 'wireguard-nt-([\d.]+)\.zip')
    $ver = $matches2 | ForEach-Object { $_.Groups[1].Value } |
           Sort-Object { [version]$_ } | Select-Object -Last 1
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
    $kb = [math]::Round((Get-Item $wgDll).Length / 1KB)
    Write-Host "        wireguard.dll ready (wireguard-NT v$ver, $kb KB)."
}

# ------ tunnel.dll ----------------------------------------------------------
Write-Host '  [2/2] tunnel.dll (build from source)...'

if (Test-Path $tnDll) {
    Write-Host '        Already cached.'
} else {
    # Refresh PATH so a freshly installed Go is visible
    $machinePath = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine')
    $userPath    = [System.Environment]::GetEnvironmentVariable('PATH', 'User')
    $env:PATH    = $machinePath + ';' + $userPath

    $goExe = Get-Command go -ErrorAction SilentlyContinue
    if (-not $goExe) {
        Write-Host ''
        Write-Host '  ERROR: Go not found on PATH.'
        Write-Host '  tunnel.dll must be compiled using Go + gcc (MinGW).'
        Write-Host ''
        Write-Host '  Install Go:  https://go.dev/dl/'
        Write-Host '  Install gcc: https://www.mingw-w64.org/ or use Git for Windows'
        Write-Host '               (enable "Add to PATH" during Git install)'
        Write-Host ''
        throw 'Go not found. Install Go and re-run tunnelbuild.bat.'
    }

    $gitExe = Get-Command git -ErrorAction SilentlyContinue
    if (-not $gitExe) {
        Write-Host '  ERROR: git not found. Required to clone wireguard-windows.'
        Write-Host '  Install git: https://git-scm.com/'
        throw 'git not found.'
    }

    Write-Host "        Go found: $(& go version 2>&1)"
    Write-Host '        Cloning wireguard-windows...'
    $wgWinDir = Join-Path $Deps 'wireguard-windows'

    if (-not (Test-Path (Join-Path $wgWinDir '.git'))) {
        # Use Start-Process to avoid PowerShell treating git's stderr progress
        # output as a NativeCommandError (git writes info to stderr by design)
        $proc = Start-Process -FilePath 'git' `
            -ArgumentList @('clone','--depth=1','https://git.zx2c4.com/wireguard-windows',$wgWinDir) `
            -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput (Join-Path $Deps 'git_out.txt') `
            -RedirectStandardError  (Join-Path $Deps 'git_err.txt')
        if (Test-Path (Join-Path $Deps 'git_err.txt')) {
            Get-Content (Join-Path $Deps 'git_err.txt') | ForEach-Object { Write-Host "        $_" }
        }
        if ($proc.ExitCode -ne 0) { throw "git clone failed (exit $($proc.ExitCode))." }
    } else {
        $proc = Start-Process -FilePath 'git' `
            -ArgumentList @('-C',$wgWinDir,'pull','--ff-only') `
            -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput (Join-Path $Deps 'git_out.txt') `
            -RedirectStandardError  (Join-Path $Deps 'git_err.txt')
        if ($proc.ExitCode -ne 0) { throw "git pull failed (exit $($proc.ExitCode))." }
    }

    $buildDir = Join-Path $wgWinDir 'embeddable-dll-service'
    Write-Host '        Building tunnel.dll (may take a minute on first run)...'

    $exitFile = Join-Path $Deps 'build_exit.txt'
    $wrapBat  = Join-Path $Deps 'run_build.bat'
    $wrapContent = "@echo off`r`ncd /d `"$buildDir`"`r`ncall build.bat`r`necho %ERRORLEVEL% > `"$exitFile`"`r`n"
    Set-Content -Path $wrapBat -Value $wrapContent -Encoding ASCII
    Remove-Item $exitFile -ErrorAction SilentlyContinue

    Start-Process cmd.exe -ArgumentList "/c `"$wrapBat`"" -Wait
    Start-Sleep -Seconds 2

    $exitCode = 0
    if (Test-Path $exitFile) { $exitCode = [int](Get-Content $exitFile).Trim() }
    Write-Host "        build.bat exited with code $exitCode"
    Remove-Item $wrapBat -ErrorAction SilentlyContinue
    Remove-Item $exitFile -ErrorAction SilentlyContinue

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
        Write-Host '  Place tunnel.dll (x64) manually into:'
        Write-Host "    $Deps"
        Write-Host ''
        throw 'tunnel.dll not produced by build.bat.'
    }

    Copy-Item $builtDll $tnDll -Force
    Write-Host '        tunnel.dll built and cached.'
}

# ------ Copy to Dist --------------------------------------------------------
Write-Host ''
Write-Host '  Copying DLLs to output folder...'
if (-not (Test-Path $Dist)) { New-Item $Dist -ItemType Directory | Out-Null }
Copy-Item $wgDll (Join-Path $Dist 'wireguard.dll') -Force
Copy-Item $tnDll (Join-Path $Dist 'tunnel.dll')    -Force

$wgKb = [math]::Round((Get-Item (Join-Path $Dist 'wireguard.dll')).Length / 1KB)
$tnKb = [math]::Round((Get-Item (Join-Path $Dist 'tunnel.dll')).Length    / 1KB)
Write-Host "        wireguard.dll  ($wgKb KB)"
Write-Host "        tunnel.dll     ($tnKb KB)"

if ($wgKb -lt 900) {
    Write-Host ''
    Write-Host '  WARNING: wireguard.dll is smaller than expected for wireguard-NT.'
    Write-Host '  Do not use the DLL from C:\Program Files\WireGuard\ -- that version'
    Write-Host '  requires the WireGuard app to be installed.'
}
