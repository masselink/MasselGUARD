@echo off
title MasselGUARD -- Build
setlocal enabledelayedexpansion

rem ── Build number: YYMMDDHHMM ────────────────────────────────────────────────
for /f %%a in ('powershell -NoProfile -Command "Get-Date -Format yyMMddHHmm"') do set BUILD_NUM=%%a
set VERSION=3.7.0
rem Update CODENAME here AND in UpdateChecker.cs when bumping VERSION.
set CODENAME=Chromatic Chameleon

rem ── Opt out of .NET CLI telemetry ────────────────────────────────────────────
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

set DIST=%~dp0dist
set DEPS=%~dp0wireguard-deps

echo.
echo  --------------------------------------------------
echo  MasselGUARD  v%VERSION%  ^|  %CODENAME%
echo  Harold Masselink  ^|  https://masselink.net
echo  --------------------------------------------------
echo.

rem ── Step 1: verify .NET SDK ──────────────────────────────────────────────────
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo  ERROR: .NET SDK not found.
    echo  Install .NET 10 SDK from: https://dotnet.microsoft.com/download/dotnet/10.0
    pause & exit /b 1
)

for /f "tokens=*" %%v in ('dotnet --version') do set DOTNET_VER=%%v
echo  .NET SDK detected: %DOTNET_VER%

for /f "tokens=1 delims=." %%m in ("%DOTNET_VER%") do set DOTNET_MAJOR=%%m
if "%DOTNET_MAJOR%" == "10" goto sdk_ok
if %DOTNET_MAJOR% GTR 10 goto sdk_ok
echo.
echo  ERROR: .NET 10 SDK is required (detected: %DOTNET_VER%).
echo  Download from: https://dotnet.microsoft.com/download/dotnet/10.0
echo.
pause & exit /b 1

:sdk_ok
echo.

rem ── Step 2a: pre-flight — fail if either exe is locked by a running process ──
if exist "!DIST!\MasselGUARD.exe" (
    ren "!DIST!\MasselGUARD.exe" "MasselGUARD.exe.__chk" >nul 2>&1
    if errorlevel 1 (
        echo  ==========================================
        echo   BUILD FAILED -- EXE IS STILL RUNNING
        echo  ==========================================
        echo.
        echo   Close MasselGUARD.exe before building,
        echo   then run BUILD.bat again.
        echo.
        pause & exit /b 1
    )
    ren "!DIST!\MasselGUARD.exe.__chk" "MasselGUARD.exe" >nul 2>&1
)
if exist "!DIST!\MasselGUARDcli.exe" (
    ren "!DIST!\MasselGUARDcli.exe" "MasselGUARDcli.exe.__chk" >nul 2>&1
    if errorlevel 1 (
        echo  ==========================================
        echo   BUILD FAILED -- CLI EXE IS STILL RUNNING
        echo  ==========================================
        echo.
        echo   Close MasselGUARDcli.exe before building,
        echo   then run BUILD.bat again.
        echo.
        pause & exit /b 1
    )
    ren "!DIST!\MasselGUARDcli.exe.__chk" "MasselGUARDcli.exe" >nul 2>&1
)

rem ── Step 2b: compile MasselGUARD (GUI) ───────────────────────────────────────
echo  -------------------------------------------------------
echo   Compiling MasselGUARD (GUI)...
echo  -------------------------------------------------------
echo.
dotnet publish "%~dp0MasselGUARD.csproj" -c Release -o "!DIST!" ^
    -p:Version=%VERSION% ^
    -p:AssemblyVersion=%VERSION%.0 ^
    -p:FileVersion=%VERSION%.0 ^
    -p:InformationalVersion=%VERSION%.%BUILD_NUM%
if not exist "!DIST!\MasselGUARD.exe" (
    echo.
    echo  ==========================================
    echo   BUILD FAILED -- MasselGUARD.exe not produced
    echo  ==========================================
    echo.
    pause & exit /b 1
)
echo.
echo  OK  MasselGUARD.exe
echo.

rem ── Step 2c: compile MasselGUARDcli (CLI) ────────────────────────────────────
echo  -------------------------------------------------------
echo   Compiling MasselGUARDcli (CLI)...
echo  -------------------------------------------------------
echo.
dotnet publish "%~dp0MasselGUARDcli\MasselGUARDcli.csproj" -c Release -o "!DIST!" ^
    -p:Version=%VERSION% ^
    -p:AssemblyVersion=%VERSION%.0 ^
    -p:FileVersion=%VERSION%.0 ^
    -p:InformationalVersion=%VERSION%.%BUILD_NUM%
if not exist "!DIST!\MasselGUARDcli.exe" (
    echo.
    echo  ==========================================
    echo   BUILD FAILED -- MasselGUARDcli.exe not produced
    echo  ==========================================
    echo.
    pause & exit /b 1
)
echo.
echo  OK  MasselGUARDcli.exe
echo.

rem ── Step 3a: copy install helper ─────────────────────────────────────────────
if exist "%~dp0install-dotnet.bat" (
    copy /y "%~dp0install-dotnet.bat" "!DIST!\install-dotnet.bat" >nul
    echo  OK  install-dotnet.bat
) else (
    echo  WARNING: install-dotnet.bat not found -- skipped.
)
echo.

rem ── Step 3b: copy lang + theme folders into dist ──────────────────────────────
echo  -------------------------------------------------------
echo   Copying lang + theme folders...
echo  -------------------------------------------------------
if exist "%~dp0lang" (
    if exist "!DIST!\lang" rmdir /s /q "!DIST!\lang"
    xcopy /e /i /q "%~dp0lang" "!DIST!\lang" >nul
    echo  lang folder copied to dist\lang\
) else (
    echo  WARNING: lang folder not found -- skipped.
)
if exist "%~dp0theme" (
    if exist "!DIST!\theme" rmdir /s /q "!DIST!\theme"
    xcopy /e /i /q "%~dp0theme" "!DIST!\theme" >nul
    echo  theme folder copied to dist\theme\
) else (
    echo  WARNING: theme folder not found -- skipped.
)
echo.

rem ── Step 4: copy DLLs from wireguard-deps ────────────────────────────────────
echo  -------------------------------------------------------
echo   Copying tunnel DLLs from wireguard-deps\...
echo  -------------------------------------------------------
set DLL_OK=1

if exist "!DEPS!\tunnel.dll" (
    copy /y "!DEPS!\tunnel.dll" "!DIST!\tunnel.dll" >nul
    echo    Copied: tunnel.dll
) else (
    echo  WARNING: wireguard-deps\tunnel.dll not found.
    echo           Run tunnelbuild\tunnelbuild.bat, then copy DLLs to wireguard-deps\.
    set DLL_OK=0
)

if exist "!DEPS!\wireguard.dll" (
    copy /y "!DEPS!\wireguard.dll" "!DIST!\wireguard.dll" >nul
    echo    Copied: wireguard.dll
) else (
    echo  WARNING: wireguard-deps\wireguard.dll not found.
    echo           Run tunnelbuild\tunnelbuild.bat, then copy DLLs to wireguard-deps\.
    set DLL_OK=0
)

echo.
echo  ==========================================
echo   BUILD SUCCESSFUL
echo  ==========================================
echo.
echo   dist\MasselGUARD.exe        (GUI application)
echo   dist\MasselGUARDcli.exe     (command-line interface)
echo   dist\install-dotnet.bat     (.NET 10 install helper)
echo   dist\lang\
echo   dist\theme\
if "!DLL_OK!"=="1" (
    echo   dist\tunnel.dll
    echo   dist\wireguard.dll
    echo.
    echo   Standalone local tunnels: ready.
) else (
    echo.
    echo   NOTE: One or more WireGuard DLLs were not copied.
    echo   Run tunnelbuild\tunnelbuild.bat to rebuild,
    echo   then re-run BUILD.bat, or copy DLLs manually.
)
echo.
echo   Target machine requires .NET 10 Desktop Runtime:
echo   https://dotnet.microsoft.com/download/dotnet/10.0
echo.
pause
exit /b 0
