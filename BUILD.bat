@echo off
title MasselGUARD -- Build
setlocal enabledelayedexpansion
echo.
echo  ====================================
echo              MasselGUARD             
echo  ====================================
echo       v2.0  by Harold Masselink      
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
rem  Ask: create or download WireGuard DLLs?
rem ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
echo.
echo  -------------------------------------------------------
echo   WireGuard DLLs (tunnel.dll + wireguard.dll)
echo  -------------------------------------------------------
echo.
echo   Local tunnels need two DLLs placed next to the exe.
echo.
echo   [Y] Create  -- build tunnel.dll (requires Go) and
echo                  download wireguard.dll from WireGuard LLC
echo.
echo   [N] Download -- download pre-built DLLs from the
echo                  MasselGUARD repository (fast, no tools needed)
echo.

set DLL_CHOICE=
set /p DLL_CHOICE="  Create WireGuard DLLs (tunnel.dll + wireguard.dll)? [Y/N]: "
if /i "!DLL_CHOICE!"=="Y"   goto create_dlls
if /i "!DLL_CHOICE!"=="YES" goto create_dlls
if /i "!DLL_CHOICE!"=="N"   goto ask_repo_download
if /i "!DLL_CHOICE!"=="NO"  goto ask_repo_download
goto build_app

rem ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
rem  Create DLLs from source (Go required)
rem ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
:create_dlls
echo.
set DEPS=%~dp0deps
set DIST=%~dp0dist
if not exist "!DEPS!" mkdir "!DEPS!"
if not exist "!DIST!" mkdir "!DIST!"

echo  Building WireGuard DLLs from source...
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0get-wireguard-dlls.ps1" -Deps "!DEPS!" -Dist "!DIST!"
if errorlevel 1 (
    echo.
    echo  ERROR: Failed to create WireGuard DLLs. See output above.
    goto fail
)

if not exist "!DIST!\wireguard.dll" ( echo  ERROR: wireguard.dll missing. & goto fail )
if not exist "!DIST!\tunnel.dll"   ( echo  ERROR: tunnel.dll missing. & goto fail )

set DO_COPY_DLLS=1
goto build_app

rem ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
rem  Ask to download pre-built DLLs from MasselGUARD repo
rem ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
:ask_repo_download
echo.
set REPO_CHOICE=
set /p REPO_CHOICE="  Download pre-built DLLs from MasselGUARD repository? [Y/N]: "
if /i "!REPO_CHOICE!"=="Y"   goto repo_download
if /i "!REPO_CHOICE!"=="YES" goto repo_download
goto build_app

:repo_download
echo.
set DIST=%~dp0dist
if not exist "!DIST!" mkdir "!DIST!"

echo  Downloading tunnel.dll and wireguard.dll from MasselGUARD repository...
echo.

set REPO_BASE=https://raw.githubusercontent.com/masselink/MasselGUARD/e8708f27771dce784ac2a9b14436bb64891799ec/wireguard-deps

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Write-Host '  Downloading wireguard.dll...';" ^
  "Invoke-WebRequest '!REPO_BASE!/wireguard.dll' -OutFile '!DIST!\wireguard.dll' -UseBasicParsing;" ^
  "Write-Host '  Downloading tunnel.dll...';" ^
  "Invoke-WebRequest '!REPO_BASE!/tunnel.dll' -OutFile '!DIST!\tunnel.dll' -UseBasicParsing;" ^
  "Write-Host '  Done.'"
if errorlevel 1 (
    echo.
    echo  ERROR: Download from repository failed.
    echo  Check your internet connection or try the [Y] option to build from source.
    goto fail
)

if not exist "!DIST!\wireguard.dll" ( echo  ERROR: wireguard.dll missing. & goto fail )
if not exist "!DIST!\tunnel.dll"   ( echo  ERROR: tunnel.dll missing. & goto fail )

echo.
echo  DLLs downloaded successfully.
set DO_COPY_DLLS=1

rem ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
rem  Build MasselGUARD
rem ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
:build_app
echo.
echo  Publishing MasselGUARD (single-file, win-x64)...
echo.
dotnet publish "%~dp0MasselGUARD.csproj" -c Release -o "%~dp0dist"
if errorlevel 1 (
    echo.
    echo  BUILD FAILED. See output above.
    pause & exit /b 1
)

rem ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
rem  Summary
rem ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
if exist "%~dp0dist\MasselGUARD.exe" (
    echo.
    echo  ==========================================
    echo   BUILD SUCCESSFUL
    echo  ==========================================
    echo.
    echo   dist\MasselGUARD.exe
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
