@echo off
title MasselGUARD -- Tunnel DLL Builder
setlocal enabledelayedexpansion

rem ── Opt out of .NET CLI telemetry ────────────────────────────────────────────
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

echo.
echo  ====================================
echo       MasselGUARD -- Tunnel DLLs
echo  ====================================
echo   Builds tunnel.dll from source and
echo   fetches wireguard-NT wireguard.dll
echo  ====================================
echo.
echo  Requires: Go 1.21+   https://go.dev/dl/
echo            git         https://git-scm.com/
echo            x64  : gcc/MinGW   https://www.mingw-w64.org/
echo            arm64: llvm-mingw  https://github.com/mstorsjo/llvm-mingw/releases
echo                   (provides aarch64-w64-mingw32-clang)
echo.

rem ── Architecture selection ───────────────────────────────────────────────────
rem Pass an argument to skip the prompt (scriptable):
rem   tunnelbuild.bat x64 | arm64 | all | both
set "ARCHES=%~1"
if not "%ARCHES%"=="" goto arch_resolve

:arch_menu
echo  Which architecture(s) to build DLLs for?
echo.
echo    [1]  x64    (amd64)
echo    [2]  arm64
echo    [3]  both   (default)
echo    [Q]  quit
echo.
set "CHOICE="
set /p "CHOICE=  Enter choice [3]: "
if not defined CHOICE set "CHOICE=3"
if /I "%CHOICE%"=="1"     ( set "ARCHES=x64"       & goto arch_resolve )
if /I "%CHOICE%"=="2"     ( set "ARCHES=arm64"     & goto arch_resolve )
if /I "%CHOICE%"=="3"     ( set "ARCHES=x64 arm64" & goto arch_resolve )
if /I "%CHOICE%"=="x64"   ( set "ARCHES=x64"       & goto arch_resolve )
if /I "%CHOICE%"=="arm64" ( set "ARCHES=arm64"     & goto arch_resolve )
if /I "%CHOICE%"=="both"  ( set "ARCHES=x64 arm64" & goto arch_resolve )
if /I "%CHOICE%"=="all"   ( set "ARCHES=x64 arm64" & goto arch_resolve )
if /I "%CHOICE%"=="Q"     ( echo  Cancelled. & exit /b 0 )
echo.
echo  Invalid choice "%CHOICE%" -- enter 1, 2, 3 or Q.
echo.
goto arch_menu

:arch_resolve
if /I "%ARCHES%"=="all"  set "ARCHES=x64 arm64"
if /I "%ARCHES%"=="both" set "ARCHES=x64 arm64"

rem Validate every requested token is a known arch.
for %%A in (%ARCHES%) do (
    if /I not "%%A"=="x64" if /I not "%%A"=="arm64" (
        echo  ERROR: unknown architecture "%%A" ^(expected x64, arm64, all/both^).
        pause & exit /b 1
    )
)

echo.
echo  Building arch(es): %ARCHES%
echo.

set DEPS_ROOT=%~dp0..\wireguard-deps
set WORK=%~dp0build-temp
if not exist "%WORK%" mkdir "%WORK%"

for %%A in (%ARCHES%) do (
    echo.
    echo  -------------------------------------------------------
    echo   Building tunnel DLLs for %%A ...
    echo  -------------------------------------------------------
    set OUT=%DEPS_ROOT%\%%A
    if not exist "!OUT!" mkdir "!OUT!"
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0get-wireguard-dlls.ps1" -Arch %%A -Work "%WORK%" -Out "!OUT!"
    if errorlevel 1 (
        echo.
        echo  ERROR: %%A build failed. See output above.
        pause & exit /b 1
    )
    if not exist "!OUT!\tunnel.dll"    ( echo  ERROR: %%A tunnel.dll missing.    & pause & exit /b 1 )
    if not exist "!OUT!\wireguard.dll" ( echo  ERROR: %%A wireguard.dll missing. & pause & exit /b 1 )
)

echo.
echo  -------------------------------------------------------
echo   Cleaning up build-temp...
echo  -------------------------------------------------------
if exist "%WORK%" ( rmdir /s /q "%WORK%" & echo   build-temp removed. )

echo.
echo  ==========================================
echo   DLLs ready under wireguard-deps\<arch>\
echo  ==========================================
for %%A in (%ARCHES%) do (
    echo   wireguard-deps\%%A\tunnel.dll
    echo   wireguard-deps\%%A\wireguard.dll
)
echo.
echo  Now run BUILD.bat to compile and package MasselGUARD.
echo.
pause
exit /b 0
