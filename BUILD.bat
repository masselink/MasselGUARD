@echo off
title WireGuard Client and WiFi Switcher v1.1.2 — Build
setlocal
echo.
echo  ====================================
echo   WireGuard Client and WiFi Switcher
echo  ====================================
echo           v1.1.2  by Harold Masselink
echo  ====================================
echo           (Using Claude.ai)
echo  ====================================
echo.

dotnet --version >nul 2>&1
if errorlevel 1 (
    echo  ERROR: .NET SDK not found.
    echo  Install .NET 10 SDK from: https://dotnet.microsoft.com/download/dotnet/10.0
    pause & exit /b 1
)

for /f "tokens=*" %%v in ('dotnet --version') do set DOTNET_VER=%%v
echo  .NET SDK detected: %DOTNET_VER%

rem Check major version is 10 or higher
for /f "tokens=1 delims=." %%m in ("%DOTNET_VER%") do set DOTNET_MAJOR=%%m
if "%DOTNET_MAJOR%" == "10" goto version_ok
if %DOTNET_MAJOR% GTR 10 goto version_ok
echo.
echo  ERROR: .NET 10 SDK is required (detected: %DOTNET_VER%).
echo  Download from: https://dotnet.microsoft.com/download/dotnet/10.0
echo.
pause & exit /b 1
:version_ok

echo.
echo  Publishing (single-file, framework-dependent, win-x64)...
echo.
dotnet publish WGClientWifiSwitcher.csproj -c Release -o dist
if errorlevel 1 (
    echo.
    echo  BUILD FAILED. See output above.
    pause & exit /b 1
)

if exist "dist\WGClientWifiSwitcher.exe" (
    echo.
    echo  ==========================================
    echo   BUILD SUCCESSFUL
    echo  ==========================================
    echo.
    echo   Output: dist\WGClientWifiSwitcher.exe
    echo          dist\lang\
    echo.
    echo   NOTE: Target machine requires .NET 10 Desktop Runtime:
    echo   https://dotnet.microsoft.com/download/dotnet/10.0
) else (
    echo  ERROR: exe not found after build.
    pause & exit /b 1
)
pause
