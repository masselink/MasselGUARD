@echo off
title MasselGUARD -- Build
setlocal enabledelayedexpansion

rem ── Opt out of .NET CLI telemetry ────────────────────────────────────────────
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
echo.
echo  ====================================
echo              MasselGUARD
echo  ====================================
echo       v2.5.0  by Harold Masselink
echo  ====================================
echo           (Using Claude.ai)
echo  ====================================
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

rem ── Step 2: compile the application ─────────────────────────────────────────
echo  -------------------------------------------------------
echo   Compiling MasselGUARD...
echo  -------------------------------------------------------
echo.
dotnet publish "%~dp0MasselGUARD.csproj" -c Release -o "%~dp0dist"
if errorlevel 1 (
    echo.
    echo  ==========================================
    echo   BUILD FAILED
    echo  ==========================================
    echo.
    pause & exit /b 1
)

if not exist "%~dp0dist\MasselGUARD.exe" (
    echo  ERROR: exe not found after publish.
    pause & exit /b 1
)

echo.
echo  Compile OK -- MasselGUARD.exe ready.
echo.

rem ── Step 3: copy theme folder into dist ──────────────────────────────────────
echo  -------------------------------------------------------
echo   Copying theme folder...
echo  -------------------------------------------------------
if exist "%~dp0theme" (
    if exist "%~dp0dist\theme" rmdir /s /q "%~dp0dist\theme"
    xcopy /e /i /q "%~dp0theme" "%~dp0dist\theme" >nul
    echo  Theme folder copied to dist\theme\
) else (
    echo  WARNING: theme folder not found -- skipped.
)
echo.

rem ── Step 4: copy DLLs from wireguard-deps ────────────────────────────────────
echo  -------------------------------------------------------
echo   Copying tunnel DLLs from wireguard-deps\...
echo  -------------------------------------------------------
set DIST=%~dp0dist
set DEPS=%~dp0wireguard-deps
set DLL_OK=1

if exist "!DEPS!\tunnel.dll" (
    copy /y "!DEPS!\tunnel.dll" "!DIST!\tunnel.dll" >nul
    echo    Copied: tunnel.dll
) else (
    echo  WARNING: wireguard-deps\tunnel.dll not found.
    echo           Run tunnelbuild\\tunnelbuild.bat, then copy DLLs to wireguard-deps\.
    set DLL_OK=0
)

if exist "!DEPS!\wireguard.dll" (
    copy /y "!DEPS!\wireguard.dll" "!DIST!\wireguard.dll" >nul
    echo    Copied: wireguard.dll
) else (
    echo  WARNING: wireguard-deps\wireguard.dll not found.
    echo           Run tunnelbuild\\tunnelbuild.bat, then copy DLLs to wireguard-deps\.
    set DLL_OK=0
)

echo.
echo  ==========================================
echo   BUILD SUCCESSFUL
echo  ==========================================
echo.
echo   dist\MasselGUARD.exe
echo   dist\lang\
echo   dist\theme\
if "!DLL_OK!"=="1" (
    echo   dist\tunnel.dll
    echo   dist\wireguard.dll
    echo.
    echo   Standalone local tunnels: ready.
) else (
    echo.
    echo   NOTE: One or more DLLs not copied.
    echo   Run tunnelbuild\\tunnelbuild.bat to rebuild,
    echo   then re-run BUILD.bat, or copy DLLs manually.
)
echo.
echo   Target machine requires .NET 10 Desktop Runtime:
echo   https://dotnet.microsoft.com/download/dotnet/10.0
pause
exit /b 0
