@echo off
title MasselGUARD -- Build
setlocal enabledelayedexpansion

rem ── Build number: YYMMDDHHMM ────────────────────────────────────────────────
for /f %%a in ('powershell -NoProfile -Command "Get-Date -Format yyMMddHHmm"') do set BUILD_NUM=%%a
set VERSION=4.0.0
rem Update CODENAME here AND in UpdateChecker.cs when bumping VERSION.
set CODENAME=Forking Fox

rem ── Opt out of .NET CLI telemetry ────────────────────────────────────────────
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

set DIST=%~dp0dist
set DEPS=%~dp0wireguard-deps

rem ── Architecture selection ───────────────────────────────────────────────────
rem   BUILD.bat            -> builds all supported arches (x64 + arm64)
rem   BUILD.bat x64        -> x64 only
rem   BUILD.bat arm64      -> arm64 only
rem Each arch is published natively (framework-dependent single-file) into
rem dist\<arch>\ with its matching wireguard-deps\<arch>\ DLLs, then zipped to
rem dist\MasselGUARD-<arch>.zip for release. ARM64 native DLLs must be built
rem separately (tunnelbuild\tunnelbuild.bat arm64); without them the ARM64 exe
rem still builds but has no local-tunnel support.
set ARCHES=%~1
if "%ARCHES%"=="" set ARCHES=all
if /I "%ARCHES%"=="all" set ARCHES=x64 arm64

echo.
echo  --------------------------------------------------
echo  MasselGUARD  v%VERSION%  ^|  %CODENAME%
echo  Harold Masselink  ^|  https://masselink.net
echo  Building arch(es): %ARCHES%
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

rem ── Build each requested architecture ────────────────────────────────────────
for %%A in (%ARCHES%) do (
    call :build_arch %%A
    if errorlevel 1 (
        echo.
        echo  ==========================================
        echo   BUILD FAILED -- arch %%A
        echo  ==========================================
        echo.
        pause & exit /b 1
    )
)

rem ── Legacy auto-update bridge: dist\MasselGUARD.zip ──────────────────────────
rem Installs from releases <= 3.7.1 run the old updater, which looks ONLY for an
rem asset literally named MasselGUARD.zip. Ship a copy of the x64 zip under that
rem name so those installs can still auto-update to the arch-specific builds.
rem (Keep uploading it to every release until no pre-3.9.0 installs remain.)
if exist "%DIST%\MasselGUARD-x64.zip" (
    copy /y "%DIST%\MasselGUARD-x64.zip" "%DIST%\MasselGUARD.zip" >nul
    echo  Created dist\MasselGUARD.zip ^(x64 copy — legacy update bridge^)
)

echo.
echo  ==========================================
echo   BUILD SUCCESSFUL
echo  ==========================================
echo.
for %%A in (%ARCHES%) do (
    echo   dist\%%A\                     ^(native %%A build^)
    if exist "%DIST%\MasselGUARD-%%A.zip" echo   dist\MasselGUARD-%%A.zip     ^(release asset^)
)
if exist "%DIST%\MasselGUARD.zip" echo   dist\MasselGUARD.zip         ^(legacy update bridge = x64^)
echo.
echo   Target machine requires the .NET 10 Desktop Runtime for its architecture:
echo   https://dotnet.microsoft.com/download/dotnet/10.0
echo.
pause
exit /b 0

rem ═════════════════════════════════════════════════════════════════════════════
rem  :build_arch <x64|arm64>
rem ═════════════════════════════════════════════════════════════════════════════
:build_arch
setlocal enabledelayedexpansion
set ARCH=%~1
set RID=
if /I "%ARCH%"=="x64"   set RID=win-x64
if /I "%ARCH%"=="arm64" set RID=win-arm64
if not defined RID (
    echo  ERROR: unknown architecture "%ARCH%" ^(expected x64 or arm64^).
    endlocal & exit /b 1
)
set OUT=%DIST%\%ARCH%

echo  =======================================================
echo   [%ARCH%]  publishing %RID% ...
echo  =======================================================
echo.

rem ── Pre-flight: fail if a target exe is locked by a running process ──────────
rem A running .exe cannot be deleted or overwritten -- but it CAN be renamed, so the
rem old ren-based probe missed it and the build failed later at the bundle/zip step
rem while still leaving a STALE exe behind. Probe by deleting instead (we rebuild it
rem anyway): if the file survives the delete, it is in use -- fail now, clearly.
if exist "%OUT%\MasselGUARD.exe" (
    del /f /q "%OUT%\MasselGUARD.exe" >nul 2>&1
    if exist "%OUT%\MasselGUARD.exe" (
        echo   BUILD FAILED -- %ARCH% MasselGUARD.exe is running. Close MasselGUARD ^(including the tray icon^) and retry.
        endlocal & exit /b 1
    )
)
if exist "%OUT%\MasselGUARDcli.exe" (
    del /f /q "%OUT%\MasselGUARDcli.exe" >nul 2>&1
    if exist "%OUT%\MasselGUARDcli.exe" (
        echo   BUILD FAILED -- %ARCH% MasselGUARDcli.exe is running. Close it and retry.
        endlocal & exit /b 1
    )
)

rem ── Clean intermediates (per-arch: obj\ caches the previous RID) ─────────────
rem WPF bakes AssemblyVersion into compiled resource pack-URIs; stale obj\ from a
rem different RID or version produces a mixed-arch/mixed-version exe. Clean is
rem cheap insurance and mandatory between architectures.
echo   Cleaning obj/bin intermediates...
if exist "%~dp0obj"                rmdir /s /q "%~dp0obj"
if exist "%~dp0bin"                rmdir /s /q "%~dp0bin"
if exist "%~dp0MasselGUARDcli\obj" rmdir /s /q "%~dp0MasselGUARDcli\obj"
if exist "%~dp0MasselGUARDcli\bin" rmdir /s /q "%~dp0MasselGUARDcli\bin"
if exist "%OUT%"                   rmdir /s /q "%OUT%"

rem ── Compile GUI ─────────────────────────────────────────────────────────────
echo   Compiling MasselGUARD (GUI) [%ARCH%]...
dotnet publish "%~dp0MasselGUARD.csproj" -c Release -r %RID% --self-contained false -o "%OUT%" ^
    -p:RuntimeIdentifier=%RID% ^
    -p:Version=%VERSION% ^
    -p:AssemblyVersion=%VERSION%.0 ^
    -p:FileVersion=%VERSION%.0 ^
    -p:InformationalVersion=%VERSION%.%BUILD_NUM%
if errorlevel 1 (
    echo   BUILD FAILED -- MasselGUARD publish failed for %ARCH% ^(see errors above^).
    endlocal & exit /b 1
)
if not exist "%OUT%\MasselGUARD.exe" (
    echo   BUILD FAILED -- MasselGUARD.exe not produced for %ARCH%.
    endlocal & exit /b 1
)
echo   OK  %ARCH%\MasselGUARD.exe

rem ── Compile CLI ─────────────────────────────────────────────────────────────
echo   Compiling MasselGUARDcli (CLI) [%ARCH%]...
dotnet publish "%~dp0MasselGUARDcli\MasselGUARDcli.csproj" -c Release -r %RID% --self-contained false -o "%OUT%" ^
    -p:RuntimeIdentifier=%RID% ^
    -p:Version=%VERSION% ^
    -p:AssemblyVersion=%VERSION%.0 ^
    -p:FileVersion=%VERSION%.0 ^
    -p:InformationalVersion=%VERSION%.%BUILD_NUM%
if errorlevel 1 (
    echo   BUILD FAILED -- MasselGUARDcli publish failed for %ARCH% ^(see errors above^).
    endlocal & exit /b 1
)
if not exist "%OUT%\MasselGUARDcli.exe" (
    echo   BUILD FAILED -- MasselGUARDcli.exe not produced for %ARCH%.
    endlocal & exit /b 1
)
echo   OK  %ARCH%\MasselGUARDcli.exe

rem ── Copy install helper ─────────────────────────────────────────────────────
if exist "%~dp0install-dotnet.bat" copy /y "%~dp0install-dotnet.bat" "%OUT%\install-dotnet.bat" >nul

rem ── Copy lang folder ────────────────────────────────────────────────────────
if exist "%~dp0lang" (
    if exist "%OUT%\lang" rmdir /s /q "%OUT%\lang"
    xcopy /e /i /q "%~dp0lang" "%OUT%\lang" >nul
    echo   OK  %ARCH%\lang\
)

rem ── Copy licence + third-party notices (required in every release) ──────────
rem LICENSE ships as LICENSE.txt so it opens in Notepad on double-click / from About.
if exist "%~dp0LICENSE" (
    copy /y "%~dp0LICENSE" "%OUT%\LICENSE.txt" >nul
    echo   OK  %ARCH%\LICENSE.txt
)
if exist "%~dp0THIRD-PARTY-NOTICES.md" (
    copy /y "%~dp0THIRD-PARTY-NOTICES.md" "%OUT%\THIRD-PARTY-NOTICES.md" >nul
    echo   OK  %ARCH%\THIRD-PARTY-NOTICES.md
)

rem ── Copy the matching-arch WireGuard DLLs ───────────────────────────────────
set DLL_OK=1
if exist "%DEPS%\%ARCH%\tunnel.dll" (
    copy /y "%DEPS%\%ARCH%\tunnel.dll" "%OUT%\tunnel.dll" >nul
    echo   OK  %ARCH%\tunnel.dll
) else (
    echo   WARNING: wireguard-deps\%ARCH%\tunnel.dll not found -- local tunnels disabled for %ARCH%.
    set DLL_OK=0
)
if exist "%DEPS%\%ARCH%\wireguard.dll" (
    copy /y "%DEPS%\%ARCH%\wireguard.dll" "%OUT%\wireguard.dll" >nul
    echo   OK  %ARCH%\wireguard.dll
) else (
    echo   WARNING: wireguard-deps\%ARCH%\wireguard.dll not found -- local tunnels disabled for %ARCH%.
    set DLL_OK=0
)
if "!DLL_OK!"=="0" (
    echo   NOTE: build tunnel DLLs with  tunnelbuild\tunnelbuild.bat %ARCH%
)

rem ── Strip debug symbols — .pdb files are not needed to run and don't ship ────
if exist "%OUT%\*.pdb" (
    del /q "%OUT%\*.pdb"
    echo   Removed .pdb debug symbols
)

rem ── Package release zip: dist\MasselGUARD-<arch>.zip ────────────────────────
rem Delete any stale zip first so the existence check reflects THIS run only, then
rem fail the build if packaging did not produce it (a missing release asset is fatal).
echo   Packaging dist\MasselGUARD-%ARCH%.zip ...
if exist "%DIST%\MasselGUARD-%ARCH%.zip" del /f /q "%DIST%\MasselGUARD-%ARCH%.zip" >nul 2>&1
rem Retry the zip: cloud sync (OneDrive) / AV can briefly lock a freshly-copied
rem file in dist\%ARCH%\lang\ right as Compress-Archive reads it. Ride it out.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$src='%OUT%\*'; $dst='%DIST%\MasselGUARD-%ARCH%.zip'; for($i=1;$i -le 8;$i++){ try { Compress-Archive -Path $src -DestinationPath $dst -Force -ErrorAction Stop; break } catch { if($i -eq 8){ throw }; Write-Host ('  zip source locked (attempt ' + $i + '/8) - retrying in 3s...'); Start-Sleep -Seconds 3 } }"
if exist "%DIST%\MasselGUARD-%ARCH%.zip" (
    echo   OK  dist\MasselGUARD-%ARCH%.zip
) else (
    echo   BUILD FAILED -- could not create MasselGUARD-%ARCH%.zip for %ARCH%.
    endlocal & exit /b 1
)

echo.
endlocal & exit /b 0
