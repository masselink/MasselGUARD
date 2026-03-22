@echo off
title WireGuard Client and WiFi Switcher -- Build
setlocal enabledelayedexpansion
echo.
echo  ====================================
echo   WireGuard Client and WiFi Switcher
echo  ====================================
echo           v2.0  by Harold Masselink
echo  ====================================
echo           (Using Claude.ai)
echo  ====================================
echo.

rem ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
rem  Check .NET SDK
rem ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo  ERROR: .NET SDK not found.
    echo  Install .NET 10 SDK from: https://dotnet.microsoft.com/download/dotnet/10.0
    pause & exit /b 1
)

for /f "tokens=*" %%v in ('dotnet --version') do set DOTNET_VER=%%v
echo  .NET SDK detected: %DOTNET_VER%

for /f "tokens=1 delims=." %%m in ("%DOTNET_VER%") do set DOTNET_MAJOR=%%m
if "%DOTNET_MAJOR%" == "10" goto dotnet_ok
if %DOTNET_MAJOR% GTR 10 goto dotnet_ok
echo.
echo  ERROR: .NET 10 SDK is required (detected: %DOTNET_VER%).
echo  Download from: https://dotnet.microsoft.com/download/dotnet/10.0
echo.
pause & exit /b 1
:dotnet_ok

rem ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
rem  Ask: download WireGuard DLLs?
rem ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
echo.
echo  -------------------------------------------------------
echo   WireGuard DLLs (tunnel.dll + wireguard.dll)
echo  -------------------------------------------------------
echo.
echo   Local tunnels need two DLLs placed next to the exe:
echo.
echo     wireguard.dll -- downloaded from WireGuard LLC
echo     tunnel.dll    -- extracted from WireGuard MSI
echo.
echo   Downloads ~10 MB from download.wireguard.com.
echo   Without them, only imported WireGuard-client tunnels work.
echo.

set /p BUILD_DLLS="  Download and include DLLs? [Y/N]: "
if /i "!BUILD_DLLS!"=="Y"   goto download_dlls
if /i "!BUILD_DLLS!"=="YES" goto download_dlls
goto build_app

rem ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
rem  Download DLLs via PowerShell script
rem ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
:download_dlls
echo.
set DEPS=%~dp0deps
set DIST=%~dp0dist
if not exist "%DEPS%" mkdir "%DEPS%"
if not exist "%DIST%" mkdir "%DIST%"

echo  Downloading wireguard.dll and tunnel.dll...
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0get-wireguard-dlls.ps1" -Deps "%DEPS%" -Dist "%DIST%"
if errorlevel 1 (
    echo.
    echo  ERROR: Failed to get WireGuard DLLs. See output above.
    goto fail
)

if not exist "%DIST%\wireguard.dll" ( echo  ERROR: wireguard.dll missing. & goto fail )
if not exist "%DIST%\tunnel.dll"   ( echo  ERROR: tunnel.dll missing. & goto fail )

set DO_COPY_DLLS=1

rem ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
rem  Build WGClientWifiSwitcher
rem ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
:build_app
echo.
echo  Publishing WGClientWifiSwitcher (single-file, win-x64)...
echo.
dotnet publish "%~dp0WGClientWifiSwitcher.csproj" -c Release -o "%~dp0dist"
if errorlevel 1 (
    echo.
    echo  BUILD FAILED. See output above.
    pause & exit /b 1
)

rem ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
rem  Summary
rem ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
if exist "%~dp0dist\WGClientWifiSwitcher.exe" (
    echo.
    echo  ==========================================
    echo   BUILD SUCCESSFUL
    echo  ==========================================
    echo.
    echo   dist\WGClientWifiSwitcher.exe
    echo   dist\lang\
    if "!DO_COPY_DLLS!"=="1" (
        echo   dist\wireguard.dll
        echo   dist\tunnel.dll
    ) else (
        echo.
        echo   NOTE: DLLs not included. For local tunnel support,
        echo   place tunnel.dll + wireguard.dll next to the exe.
    )
    echo.
    echo   Target machine requires .NET 10 Desktop Runtime:
    echo   https://dotnet.microsoft.com/download/dotnet/10.0
) else (
    echo  ERROR: exe not found after build.
    pause & exit /b 1
)
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
