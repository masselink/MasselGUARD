@echo off
title WireGuard WiFi Switcher — Build
setlocal

echo.
echo  ==========================================
echo   WireGuard WiFi Switcher — EXE Builder
echo  ==========================================
echo.

dotnet --version >nul 2>&1
if errorlevel 1 (
    echo  ERROR: .NET SDK not found.
    echo  Install from: https://dotnet.microsoft.com/download
    pause & exit /b 1
)

for /f "tokens=*" %%v in ('dotnet --version') do set DOTNET_VER=%%v
echo  .NET SDK: %DOTNET_VER%
echo.
echo  Building...
echo.

dotnet build WGWifiSwitcher.csproj -c Release -o dist

if errorlevel 1 (
    echo.
    echo  BUILD FAILED. See output above.
    pause & exit /b 1
)

if exist "dist\WGWifiSwitcher.exe" (
    echo.
    echo  ==========================================
    echo   BUILD SUCCESSFUL
    echo  ==========================================
    echo.
    echo   Output: dist\WGWifiSwitcher.exe
    echo.
    echo   NOTE: The target machine needs .NET 8 Desktop Runtime:
    echo   https://dotnet.microsoft.com/download/dotnet/8.0
) else (
    echo  ERROR: exe not found after build.
    pause & exit /b 1
)

pause
