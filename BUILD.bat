@echo off
title MasselGUARD -- Build
setlocal enabledelayedexpansion

rem ── Build number: YYMMDDHHMM ────────────────────────────────────────────────
for /f %%a in ('powershell -NoProfile -Command "Get-Date -Format yyMMddHHmm"') do set BUILD_NUM=%%a
set VERSION=3.1.0
set FULL_VERSION=%VERSION%.%BUILD_NUM%

rem ── Inject build number into UpdateChecker.cs ───────────────────────────────
echo $f='UpdateChecker.cs'; (Get-Content $f) -replace 'private const string CurrentVersion = ".*?"', 'private const string CurrentVersion = "%FULL_VERSION%"' ^| Set-Content $f > "%TEMP%\mg_build.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File "%TEMP%\mg_build.ps1"
del "%TEMP%\mg_build.ps1" >nul 2>&1

rem ── Opt out of .NET CLI telemetry ────────────────────────────────────────────
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
echo.
echo   ----------------------------------------
echo     MasselGUARD  v%FULL_VERSION%
echo     Harold Masselink  ^|  Claude.ai
echo   ----------------------------------------
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

rem ── Step 2a: pre-flight check — fail fast if previous exe is still running ───
if exist "%~dp0dist\MasselGUARD.exe" (
    ren "%~dp0dist\MasselGUARD.exe" "MasselGUARD.exe.__chk" >nul 2>&1
    if errorlevel 1 (
        echo  ==========================================
        echo   BUILD FAILED -- EXE IS STILL RUNNING
        echo  ==========================================
        echo.
        echo   Close MasselGUARD.exe before building,
        echo   then run build.bat again.
        echo.
        pause & exit /b 1
    )
    ren "%~dp0dist\MasselGUARD.exe.__chk" "MasselGUARD.exe" >nul 2>&1
)

rem ── Step 2b: compile the application ────────────────────────────────────────
echo  -------------------------------------------------------
echo   Compiling MasselGUARD...
echo  -------------------------------------------------------
echo.
dotnet publish "%~dp0MasselGUARD.csproj" -c Release -o "%~dp0dist" -v:minimal
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

rem ── Step 3b: copy WHATSNEW.md into dist ─────────────────────────────────────
if exist "%~dp0docs\WHATSNEW.md" (
    copy /y "%~dp0docs\WHATSNEW.md" "%~dp0dist\WHATSNEW.md" >nul
) else (
    echo  WARNING: docs\WHATSNEW.md not found -- skipped.
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
echo   dist\WHATSNEW.md
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
