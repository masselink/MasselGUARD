param(
    [string]$Deps,
    [string]$Dist
)

$ErrorActionPreference = 'Continue'

$wgDll = Join-Path $Deps 'wireguard.dll'
$tnDll = Join-Path $Deps 'tunnel.dll'

# ------ wireguard.dll ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
# Source: https://download.wireguard.com/wireguard-nt/
# Pre-built and signed by WireGuard LLC.
Write-Host '  [1/2] wireguard.dll...'
if (Test-Path $wgDll) {
    Write-Host '         Already cached.'
} else {
    Write-Host '         Finding latest wireguard-nt version...'
    $page = (Invoke-WebRequest 'https://download.wireguard.com/wireguard-nt/' -UseBasicParsing).Content
    $ver  = [regex]::Matches($page, 'wireguard-nt-([\d.]+)\.zip') |
            ForEach-Object { $_.Groups[1].Value } |
            Sort-Object { [version]$_ } |
            Select-Object -Last 1
    if (-not $ver) { throw 'Cannot determine latest wireguard-nt version.' }

    Write-Host "         Downloading wireguard-nt-$ver.zip..."
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
    Write-Host "         wireguard.dll ready (v$ver)"
}

# ------ tunnel.dll ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
# tunnel.dll has no pre-built binary. It must be built from the wireguard-windows
# embeddable-dll-service source using Go (https://go.dev/dl/).
#
# Strategy A: build from source using Go (automatic)
# Strategy C: place tunnel.dll manually in deps\ and re-run
Write-Host '  [2/2] tunnel.dll...'
if (Test-Path $tnDll) {
    Write-Host '         Already cached.'
} else {
    # Strategy A: build from source
    # Refresh PATH so newly installed Go is visible without reopening the terminal
    $machinePath = [System.Environment]::GetEnvironmentVariable('PATH', 'Machine')
    $userPath    = [System.Environment]::GetEnvironmentVariable('PATH', 'User')
    $env:PATH    = "$machinePath;$userPath"

    $goExe = Get-Command go -ErrorAction SilentlyContinue
    if ($goExe) {
        $goVer = & go version 2>$null
        Write-Host "         Go found: $goVer"
        Write-Host "         Location: $($goExe.Source)"
    } else {
        Write-Host ''
        Write-Host '  ERROR: Go not found on PATH.'
        Write-Host "         Searched PATH: $env:PATH"
        Write-Host ''
        Write-Host '  Option A -- Install Go and re-run BUILD.bat:'
        Write-Host '    https://go.dev/dl/'
        Write-Host ''
        Write-Host '  Option C -- Place tunnel.dll manually and re-run BUILD.bat:'
        Write-Host "    Copy tunnel.dll into: $Deps"
        Write-Host ''
        throw 'Go not found. See instructions above.'
    }

    $gitExe = Get-Command git -ErrorAction SilentlyContinue
    if (-not $gitExe) {
        Write-Host '  ERROR: git not found. Required to clone wireguard-windows.'
        Write-Host '    https://git-scm.com/'
        throw 'git not found.'
    }

    Write-Host '         Cloning wireguard-windows...'
    $wgWinDir = Join-Path $Deps 'wireguard-windows'

    if (-not (Test-Path (Join-Path $wgWinDir '.git'))) {
        git clone --depth=1 https://git.zx2c4.com/wireguard-windows $wgWinDir 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'git clone failed.' }
    } else {
        Write-Host '         Updating wireguard-windows...'
        git -C $wgWinDir pull --ff-only 2>&1 | Out-Null
    }

    $buildDir = Join-Path $wgWinDir 'embeddable-dll-service'
    Write-Host '         Building tunnel.dll (may take a minute on first run)...'
    # Write a small wrapper batch that runs build.bat and writes the exit code to a file.
    # This avoids PowerShell intercepting stderr from Go's toolchain downloader.
    $exitFile = Join-Path $Deps 'build_exit.txt'
    $wrapBat  = Join-Path $Deps 'run_build.bat'
    Set-Content $wrapBat "@echo off`r`ncd /d `"$buildDir`"`r`ncall build.bat`r`necho %ERRORLEVEL% > `"$exitFile`"`r`n"
    Remove-Item $exitFile -ErrorAction SilentlyContinue

    Write-Host '         Running build.bat in a separate window...'
    $p = Start-Process cmd.exe -ArgumentList "/c `"$wrapBat`"" -Wait -PassThru
    Start-Sleep -Seconds 2

    $exitCode = 0
    if (Test-Path $exitFile) {
        $exitCode = [int](Get-Content $exitFile).Trim()
    }
    Write-Host "         build.bat exited with code $exitCode"
    Remove-Item $wrapBat, $exitFile -ErrorAction SilentlyContinue

    $builtDll = Join-Path $buildDir 'amd64\tunnel.dll'
    if (-not (Test-Path $builtDll)) {
        # Try x86_64 subfolder name variant some versions use
        $builtDll = Join-Path $buildDir 'x86_64\tunnel.dll'
    }
    if (-not (Test-Path $builtDll)) {
        # Search entire build dir
        $found = Get-ChildItem $buildDir -Recurse -Filter 'tunnel.dll' -ErrorAction SilentlyContinue |
                 Select-Object -First 1
        if ($found) { $builtDll = $found.FullName }
    }
    if (-not (Test-Path $builtDll)) {
        Write-Host ''
        Write-Host '  ERROR: build.bat ran but tunnel.dll was not produced.'
        Write-Host '         Make sure gcc/MinGW is on your PATH.'
        Write-Host '         Git for Windows includes gcc -- enable "Add to PATH" during install.'
        Write-Host ''
        Write-Host '  Option C -- place tunnel.dll manually and re-run BUILD.bat:'
        Write-Host "    Copy tunnel.dll into: $Deps"
        Write-Host '    (build it on a machine with Go + gcc, or use any existing WireGuard install)'
        Write-Host ''
        throw 'tunnel.dll not produced by build.bat.'
    }

    Copy-Item $builtDll $tnDll -Force
    Write-Host '         tunnel.dll built and cached.'
}

# ------ Copy to dist ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
Write-Host ''
Write-Host '  Copying DLLs to dist...'
if (-not (Test-Path $Dist)) { New-Item $Dist -ItemType Directory | Out-Null }
Copy-Item $wgDll (Join-Path $Dist 'wireguard.dll') -Force
Copy-Item $tnDll (Join-Path $Dist 'tunnel.dll') -Force
Write-Host '         dist\wireguard.dll'
Write-Host '         dist\tunnel.dll'
