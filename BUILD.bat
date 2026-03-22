@echo off
title WireGuard Client and WiFi Switcher — Build
setlocal enabledelayedexpansion
echo.
echo  ============================================
echo   WireGuard Client and WiFi Switcher
echo   Full Build Script
echo  ============================================
echo.
echo  This script will:
echo    1. Check prerequisites (.NET 10, Go)
echo    2. Download wireguard.dll (pre-built, signed)
echo    3. Clone + build tunnel.dll from source
echo    4. Build WGClientWifiSwitcher.exe
echo    5. Package everything into dist\
echo.

rem ─────────────────────────────────────────────────────────────────────────────
rem  CONFIGURATION — edit these if needed
rem ─────────────────────────────────────────────────────────────────────────────
set WG_NT_URL=https://download.wireguard.com/wireguard-nt/
set WG_WINDOWS_REPO=https://git.zx2c4.com/wireguard-windows
set BUILD_ARCH=amd64
set DIST_DIR=%~dp0dist

rem ─────────────────────────────────────────────────────────────────────────────
rem  STEP 0 — Prerequisites
rem ─────────────────────────────────────────────────────────────────────────────
echo  [1/5] Checking prerequisites...
echo.

rem Check .NET 10
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo  ERROR: .NET SDK not found.
    echo  Install .NET 10 SDK from: https://dotnet.microsoft.com/download/dotnet/10.0
    goto :fail
)
for /f "tokens=1 delims=." %%m in ('dotnet --version') do set DOTNET_MAJOR=%%m
if "%DOTNET_MAJOR%" == "10" goto dotnet_ok
if %DOTNET_MAJOR% GTR 10 goto dotnet_ok
echo  ERROR: .NET 10+ SDK required.
goto :fail
:dotnet_ok
for /f "tokens=*" %%v in ('dotnet --version') do echo         .NET SDK: %%v

rem Check Go (required for tunnel.dll)
go version >nul 2>&1
if errorlevel 1 (
    echo.
    echo  ERROR: Go not found. Required to build tunnel.dll.
    echo  Install Go from: https://go.dev/dl/
    echo.
    echo  After installing Go, re-run this script.
    goto :fail
)
for /f "tokens=3" %%v in ('go version') do echo         Go:      %%v

rem Check git
git --version >nul 2>&1
if errorlevel 1 (
    echo.
    echo  ERROR: git not found. Required to clone wireguard-windows.
    echo  Install git from: https://git-scm.com/
    goto :fail
)
for /f "tokens=3" %%v in ('git --version') do echo         git:     %%v

rem Check PowerShell (for downloads)
powershell -command "exit 0" >nul 2>&1
if errorlevel 1 (
    echo  ERROR: PowerShell not available.
    goto :fail
)
echo         PowerShell: available
echo.

rem ─────────────────────────────────────────────────────────────────────────────
rem  STEP 1 — Download wireguard.dll (latest from official server)
rem ─────────────────────────────────────────────────────────────────────────────
echo  [2/5] Downloading wireguard.dll...

set WG_NT_DIR=%~dp0deps\wireguard-nt
if not exist "%WG_NT_DIR%" mkdir "%WG_NT_DIR%"

rem Find the latest version using PowerShell to parse the directory listing
echo         Fetching latest version from %WG_NT_URL%...
powershell -NoProfile -Command ^
  "$page = (Invoke-WebRequest -Uri '%WG_NT_URL%' -UseBasicParsing).Content;" ^
  "$matches = [regex]::Matches($page, 'wireguard-nt-([\d.]+)\.zip');" ^
  "$latest = $matches | ForEach-Object { $_.Groups[1].Value } | Sort-Object { [version]$_ } | Select-Object -Last 1;" ^
  "if ($latest) { Set-Content -Path '%WG_NT_DIR%\latest_version.txt' -Value $latest } else { exit 1 }"
if errorlevel 1 (
    echo  ERROR: Could not determine latest wireguard-nt version.
    echo  Check your internet connection or visit: %WG_NT_URL%
    goto :fail
)
set /p WG_NT_VER=<%WG_NT_DIR%\latest_version.txt
echo         Latest wireguard-nt version: %WG_NT_VER%

set WG_NT_ZIP=%WG_NT_DIR%\wireguard-nt-%WG_NT_VER%.zip
set WG_NT_EXTRACT=%WG_NT_DIR%\wireguard-nt-%WG_NT_VER%

rem Skip download if already present
if exist "%WG_NT_EXTRACT%\bin\%BUILD_ARCH%\wireguard.dll" (
    echo         Already downloaded, skipping.
) else (
    set WG_NT_DL_URL=%WG_NT_URL%wireguard-nt-%WG_NT_VER%.zip
    echo         Downloading !WG_NT_DL_URL!...
    powershell -NoProfile -Command ^
      "Invoke-WebRequest -Uri '!WG_NT_DL_URL!' -OutFile '%WG_NT_ZIP%' -UseBasicParsing"
    if errorlevel 1 (
        echo  ERROR: Failed to download wireguard-nt zip.
        goto :fail
    )
    powershell -NoProfile -Command ^
      "Expand-Archive -Path '%WG_NT_ZIP%' -DestinationPath '%WG_NT_DIR%' -Force"
    if errorlevel 1 (
        echo  ERROR: Failed to extract wireguard-nt zip.
        goto :fail
    )
    del "%WG_NT_ZIP%" 2>nul
)

set WIREGUARD_DLL=%WG_NT_EXTRACT%\bin\%BUILD_ARCH%\wireguard.dll
if not exist "%WIREGUARD_DLL%" (
    echo  ERROR: wireguard.dll not found after extraction at:
    echo         %WIREGUARD_DLL%
    goto :fail
)
echo         wireguard.dll: OK (%WIREGUARD_DLL%)
echo.

rem ─────────────────────────────────────────────────────────────────────────────
rem  STEP 2 — Clone / update wireguard-windows and build tunnel.dll
rem ─────────────────────────────────────────────────────────────────────────────
echo  [3/5] Building tunnel.dll from source...

set WG_WIN_DIR=%~dp0deps\wireguard-windows
set TUNNEL_DLL_SRC=%WG_WIN_DIR%\embeddable-dll-service\%BUILD_ARCH%\tunnel.dll

rem Clone if not present, otherwise pull latest
if not exist "%WG_WIN_DIR%\.git" (
    echo         Cloning wireguard-windows from %WG_WINDOWS_REPO%...
    git clone --depth=1 "%WG_WINDOWS_REPO%" "%WG_WIN_DIR%"
    if errorlevel 1 (
        echo  ERROR: git clone failed.
        goto :fail
    )
) else (
    echo         Updating wireguard-windows (git pull)...
    git -C "%WG_WIN_DIR%" pull --ff-only
    if errorlevel 1 (
        echo  WARNING: git pull failed. Building with existing sources.
    )
)

rem Build tunnel.dll using the official build.bat
echo         Building tunnel.dll (this may take a minute on first run)...
pushd "%WG_WIN_DIR%\embeddable-dll-service"
call build.bat
if errorlevel 1 (
    popd
    echo  ERROR: tunnel.dll build failed.
    echo  Make sure Go and gcc/MinGW are on your PATH.
    echo.
    echo  Required:
    echo    - Go:   https://go.dev/dl/
    echo    - MinGW: included with Git for Windows, or https://www.mingw-w64.org/
    goto :fail
)
popd

if not exist "%TUNNEL_DLL_SRC%" (
    echo  ERROR: tunnel.dll not found after build at:
    echo         %TUNNEL_DLL_SRC%
    goto :fail
)
echo         tunnel.dll: OK (%TUNNEL_DLL_SRC%)
echo.

rem ─────────────────────────────────────────────────────────────────────────────
rem  STEP 3 — Build WGClientWifiSwitcher
rem ─────────────────────────────────────────────────────────────────────────────
echo  [4/5] Building WGClientWifiSwitcher...
echo.

dotnet publish "%~dp0WGClientWifiSwitcher.csproj" -c Release -o "%DIST_DIR%"
if errorlevel 1 (
    echo.
    echo  BUILD FAILED. See output above.
    goto :fail
)
echo.

rem ─────────────────────────────────────────────────────────────────────────────
rem  STEP 4 — Copy DLLs to dist
rem ─────────────────────────────────────────────────────────────────────────────
echo  [5/5] Packaging DLLs into dist\...

copy /Y "%WIREGUARD_DLL%" "%DIST_DIR%\wireguard.dll" >nul
if errorlevel 1 (
    echo  ERROR: Failed to copy wireguard.dll to dist\
    goto :fail
)

copy /Y "%TUNNEL_DLL_SRC%" "%DIST_DIR%\tunnel.dll" >nul
if errorlevel 1 (
    echo  ERROR: Failed to copy tunnel.dll to dist\
    goto :fail
)

echo         wireguard.dll   %WG_NT_VER%
for /f "tokens=*" %%v in ('go version') do set GO_VER=%%v
echo         tunnel.dll      built from wireguard-windows HEAD
echo.

rem ─────────────────────────────────────────────────────────────────────────────
rem  Summary
rem ─────────────────────────────────────────────────────────────────────────────
echo  ==========================================
echo   BUILD SUCCESSFUL
echo  ==========================================
echo.
echo   Output files in dist\:
echo     WGClientWifiSwitcher.exe
echo     tunnel.dll
echo     wireguard.dll
echo     lang\
echo.
echo   wireguard.dll source : %WG_NT_URL%  (v%WG_NT_VER%)
echo   tunnel.dll    source : %WG_WINDOWS_REPO%
echo.
echo   NOTE: Target machine requires .NET 10 Desktop Runtime:
echo   https://dotnet.microsoft.com/download/dotnet/10.0
echo.
pause
exit /b 0

:fail
echo.
echo  ==========================================
echo   BUILD FAILED
echo  ==========================================
echo.
pause
exit /b 1
