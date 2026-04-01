@echo off
title MasselGUARD -- Build
setlocal enabledelayedexpansion
echo.
echo  ====================================
echo              MasselGUARD
echo  ====================================
echo       v2.1  by Harold Masselink
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

rem ── Step 2: compile the application first ────────────────────────────────────
echo  -------------------------------------------------------
echo   Compiling MasselGUARD...
echo  -------------------------------------------------------
echo.
dotnet publish "%~dp0MasselGUARD.csproj" -c Release -o "%~dp0dist"
if errorlevel 1 (
    echo.
    echo  ==========================================
    echo   BUILD FAILED  -- fix errors before DLLs
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

rem ── Step 3: ask about DLLs (only after a successful compile) ─────────────────
echo  -------------------------------------------------------
echo   Local tunnel DLLs (tunnel.dll + wireguard.dll)
echo  -------------------------------------------------------
echo.
echo   Standalone local tunnels require two DLLs next to the
echo   exe. tunnel.dll bundles the WireGuardNT kernel driver
echo   so no WireGuard for Windows installation is needed.
echo.
echo   [Y] Build   -- build tunnel.dll from source (Go+gcc)
echo                  and download wireguard-NT wireguard.dll
echo   [N] Skip    -- add DLLs manually, or download below
echo.

set DLL_CHOICE=
set /p DLL_CHOICE="  Build DLLs from source? [Y/N]: "
if /i "!DLL_CHOICE!"=="Y"   goto do_build
if /i "!DLL_CHOICE!"=="YES" goto do_build
goto ask_dl

:do_build
set DEPS=%~dp0deps
set DIST=%~dp0dist
if not exist "!DEPS!" mkdir "!DEPS!"
echo.
echo  Building tunnel.dll from source and getting wireguard.dll...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0get-wireguard-dlls.ps1" -Deps "!DEPS!" -Dist "!DIST!"
if errorlevel 1 ( echo. & echo  ERROR: Failed to prepare DLLs. & goto dll_warn )
if not exist "!DIST!\wireguard.dll" ( echo  ERROR: wireguard.dll missing after build. & goto dll_warn )
if not exist "!DIST!\tunnel.dll"    ( echo  ERROR: tunnel.dll missing after build.    & goto dll_warn )
set DO_COPY_DLLS=1
goto done

:ask_dl
echo.
echo  -------------------------------------------------------
echo   Download pre-built DLLs from GitHub?
echo  -------------------------------------------------------
echo.
echo   Pre-built tunnel.dll + wireguard-NT wireguard.dll from:
echo   github.com/masselink/MasselGUARD (wireguard-deps)
echo.
echo   [Y] Download -- fast, no tools needed
echo   [N] Skip     -- add DLLs manually after building
echo.

set DL_CHOICE=
set /p DL_CHOICE="  Download pre-built DLLs from GitHub? [Y/N]: "
if /i "!DL_CHOICE!"=="Y"   goto do_dl
if /i "!DL_CHOICE!"=="YES" goto do_dl
goto done

:do_dl
set DIST=%~dp0dist
echo.
echo  Downloading DLLs from GitHub...
echo.
set REPO=https://raw.githubusercontent.com/masselink/MasselGUARD/e44f8ea672657bc23497d298742327b74ad84ec6/wireguard-deps
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "try {" ^
  "  Write-Host '  Downloading wireguard.dll...'; Invoke-WebRequest '%REPO%/wireguard.dll' -OutFile '%DIST%\wireguard.dll' -UseBasicParsing;" ^
  "  Write-Host '  Downloading tunnel.dll...';    Invoke-WebRequest '%REPO%/tunnel.dll'    -OutFile '%DIST%\tunnel.dll'    -UseBasicParsing;" ^
  "  Write-Host '  Done.'" ^
  "} catch { Write-Host ('ERROR: ' + $_.Exception.Message); exit 1 }"
if errorlevel 1 goto dll_warn
if not exist "!DIST!\wireguard.dll" goto dll_warn
if not exist "!DIST!\tunnel.dll"    goto dll_warn
echo.
echo  DLLs downloaded successfully.
set DO_COPY_DLLS=1
goto done

:dll_warn
echo.
echo  WARNING: DLLs not available.
echo  Place tunnel.dll + wireguard.dll next to MasselGUARD.exe manually.
echo  (Companion mode works without them; Standalone mode requires them.)
goto done

:done
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
    echo.
    echo   Standalone local tunnels: ready.
) else (
    echo.
    echo   NOTE: DLLs not included. For Standalone mode,
    echo   place tunnel.dll + wireguard.dll next to the exe.
)
echo.
echo   Target machine requires .NET 10 Desktop Runtime:
echo   https://dotnet.microsoft.com/download/dotnet/10.0
pause
exit /b 0
