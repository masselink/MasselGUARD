@echo off
title MasselGUARD -- Tunnel DLL Builder
setlocal enabledelayedexpansion

rem ── Opt out of .NET CLI telemetry ────────────────────────────────────────────
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
echo.
echo  ====================================
echo       MasselGUARD -- Tunnel DLL
echo  ====================================
echo   Builds tunnel.dll from source and
echo   downloads wireguard-NT wireguard.dll
echo  ====================================
echo.
echo  Requires: Go 1.21+  https://go.dev/dl/
echo            gcc/MinGW  https://www.mingw-w64.org/
echo            git        https://git-scm.com/
echo.
echo  DLLs will be placed in:
echo    tunnelbuild\wireguard-deps\tunnel.dll
echo    tunnelbuild\wireguard-deps\wireguard.dll
echo.

rem All paths relative to this script's location (tunnelbuild\)
set DEPS=%~dp0wireguard-deps
set TEMP_BUILD=%~dp0build-temp

if not exist "!DEPS!" mkdir "!DEPS!"
if not exist "!TEMP_BUILD!" mkdir "!TEMP_BUILD!"

echo  -------------------------------------------------------
echo   Building tunnel.dll from source...
echo  -------------------------------------------------------
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0get-wireguard-dlls.ps1" -Deps "!TEMP_BUILD!" -Dist "!DEPS!"
if errorlevel 1 (
    echo.
    echo  ERROR: Build failed. Check output above.
    pause & exit /b 1
)

if not exist "!DEPS!\tunnel.dll" (
    echo  ERROR: tunnel.dll missing after build.
    pause & exit /b 1
)
if not exist "!DEPS!\wireguard.dll" (
    echo  ERROR: wireguard.dll missing after build.
    pause & exit /b 1
)

echo.
echo  -------------------------------------------------------
echo   Cleaning up build-temp...
echo  -------------------------------------------------------
if exist "!TEMP_BUILD!" (
    rmdir /s /q "!TEMP_BUILD!"
    echo   build-temp removed.
)

echo.
echo  ==========================================
echo   DLLs ready in tunnelbuild\wireguard-deps\
echo  ==========================================
echo.
echo   tunnelbuild\wireguard-deps\tunnel.dll
echo   tunnelbuild\wireguard-deps\wireguard.dll
echo.
echo  Copy these to the root wireguard-deps\ folder,
echo  then run BUILD.bat to compile and package MasselGUARD.
echo.
pause
exit /b 0
