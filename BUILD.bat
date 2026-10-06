@echo off
title MasselGUARD -- Build
setlocal enabledelayedexpansion

rem ── Build number: YYMMDDHHMM ────────────────────────────────────────────────
for /f %%a in ('powershell -NoProfile -Command "Get-Date -Format yyMMddHHmm"') do set BUILD_NUM=%%a
set VERSION=4.6.0
rem Update CODENAME here AND in UpdateChecker.cs when bumping VERSION.
set CODENAME=Wired Weasel

rem ── Opt out of .NET CLI telemetry ────────────────────────────────────────────
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1

set DIST=%~dp0dist
set DEPS=%~dp0wireguard-deps

rem ── Arguments (any order) ────────────────────────────────────────────────────
rem   BUILD.bat                  -> builds all supported arches (x64 + arm64) + release zips
rem   BUILD.bat x64 | arm64      -> that arch only (all = both)
rem   nozip                      -> skip the release zip (quick test build; removes a stale zip)
rem   run                        -> after a successful build, start MasselGUARD.exe for this
rem                                 PC's architecture and don't wait for a key press. With no arch
rem                                 given, "run" builds (and starts) just this PC's arch (x64 on
rem                                 an x64 PC) instead of all arches.
rem   noui (alias nogui)         -> build only the CLI (MasselGUARDcli.exe). Together with "run" it
rem                                 starts the LATEST GUI already in dist\<arch>\ (not rebuilt).
rem   nocli                      -> build only the GUI (MasselGUARD.exe)
rem                                 A partial build replaces just that exe in dist\<arch>\ and
rem                                 never packages a zip (a release zip needs both from one build).
rem   e.g.  BUILD.bat x64 nozip run      BUILD.bat x64 nocli run      BUILD.bat noui run
rem Each arch is published natively (framework-dependent single-file) into
rem dist\<arch>\ with its matching wireguard-deps\<arch>\ DLLs, then zipped to
rem dist\MasselGUARD-<arch>.zip for release. ARM64 native DLLs must be built
rem separately (tunnelbuild\tunnelbuild.bat arm64); without them the ARM64 exe
rem still builds but has no local-tunnel support.
set ARCHES=
set NOZIP=0
set RUN=0
set NOUI=0
set NOCLI=0
:parse_args
if "%~1"=="" goto args_done
if /I "%~1"=="x64" (
    set ARCHES=!ARCHES! x64
) else if /I "%~1"=="arm64" (
    set ARCHES=!ARCHES! arm64
) else if /I "%~1"=="all" (
    set ARCHES=!ARCHES! x64 arm64
) else if /I "%~1"=="nozip" (
    set NOZIP=1
) else if /I "%~1"=="run" (
    set RUN=1
) else if /I "%~1"=="noui" (
    set NOUI=1
) else if /I "%~1"=="nogui" (
    set NOUI=1
) else if /I "%~1"=="nocli" (
    set NOCLI=1
) else (
    echo.
    echo  ERROR: unknown argument "%~1".
    echo  Usage: BUILD.bat [x64^|arm64^|all] [nozip] [run] [noui^|nogui^|nocli]
    echo.
    pause & exit /b 1
)
shift
goto parse_args
:args_done
if "%NOUI%%NOCLI%"=="11" (
    echo.
    echo  ERROR: noui and nocli together leave nothing to build.
    echo.
    pause & exit /b 1
)
rem This PC's architecture (what "run" starts). x64 unless the PC is ARM64.
set HOSTARCH=x64
if /I "%PROCESSOR_ARCHITECTURE%"=="ARM64" set HOSTARCH=arm64
if /I "%PROCESSOR_ARCHITEW6432%"=="ARM64" set HOSTARCH=arm64
rem No arch given: "run" is a quick test, so build just this PC's arch; otherwise build both.
if "%ARCHES%"=="" (
    if "%RUN%"=="1" (set ARCHES=%HOSTARCH%) else set ARCHES=x64 arm64
)
rem A partial build never makes a release zip - it would pair a fresh exe with an older one.
set PARTIAL=0
if "%NOUI%"=="1"  set PARTIAL=1
if "%NOCLI%"=="1" set PARTIAL=1
if "%PARTIAL%"=="1" set NOZIP=1
set PACKAGING=release zips
if "%NOZIP%"=="1" set PACKAGING=no zips (test build)
set PARTS=GUI + CLI
if "%NOUI%"=="1"  set PARTS=CLI only
if "%NOCLI%"=="1" set PARTS=GUI only

echo.
echo  --------------------------------------------------
echo  MasselGUARD  v%VERSION%  ^|  %CODENAME%
echo  Harold Masselink  ^|  https://masselink.net
echo  Building arch(es):%ARCHES%  ^|  %PARTS%  ^|  %PACKAGING%
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

echo.
echo  ==========================================
echo   BUILD SUCCESSFUL
echo  ==========================================
echo.
for %%A in (%ARCHES%) do (
    echo   dist\%%A\                     ^(native %%A build^)
    if exist "%DIST%\MasselGUARD-%%A.zip" echo   dist\MasselGUARD-%%A.zip     ^(release asset^)
    if exist "%DIST%\MasselGUARD-%%A.zip.sha256" echo   dist\MasselGUARD-%%A.zip.sha256   ^(release asset, upload with the zip^)
)
echo.
echo   Target machine requires the .NET 10 Desktop Runtime for its architecture:
echo   https://dotnet.microsoft.com/download/dotnet/10.0
echo.

rem ── Optional: start the build for this PC's architecture ────────────────────
if "%RUN%"=="1" (
    set RUNARCH=%HOSTARCH%
    if exist "%DIST%\!RUNARCH!\MasselGUARD.exe" (
        rem With noui the GUI was not rebuilt: this starts the latest one already in dist\<arch>\.
        if "%NOUI%"=="1" echo   noui: starting the latest GUI already in dist\!RUNARCH!\ ^(not rebuilt^)
        echo   Starting dist\!RUNARCH!\MasselGUARD.exe ...
        start "" "%DIST%\!RUNARCH!\MasselGUARD.exe"
        exit /b 0
    )
    echo   NOTE: run skipped - there is no dist\!RUNARCH!\MasselGUARD.exe yet ^(build the GUI once, without noui^).
    echo.
)
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
rem Only the exe(s) being rebuilt are probed (and removed) - a partial build keeps the other one.
if "%NOUI%"=="0" if exist "%OUT%\MasselGUARD.exe" (
    del /f /q "%OUT%\MasselGUARD.exe" >nul 2>&1
    if exist "%OUT%\MasselGUARD.exe" (
        echo   BUILD FAILED -- %ARCH% MasselGUARD.exe is running. Close MasselGUARD ^(including the tray icon^) and retry.
        endlocal & exit /b 1
    )
)
if "%NOCLI%"=="0" if exist "%OUT%\MasselGUARDcli.exe" (
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
rem A full build starts from an empty dist\<arch>\; a partial build (noui / nocli) keeps the
rem folder so the exe that is NOT rebuilt stays in place.
if "%PARTIAL%"=="0" if exist "%OUT%" rmdir /s /q "%OUT%"

rem ── Compile GUI ─────────────────────────────────────────────────────────────
if "%NOUI%"=="1" (
    echo   Skipped MasselGUARD ^(GUI^) - noui
    goto skip_gui
)
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
:skip_gui

rem ── Compile CLI ─────────────────────────────────────────────────────────────
if "%NOCLI%"=="1" (
    echo   Skipped MasselGUARDcli ^(CLI^) - nocli
    goto skip_cli
)
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
:skip_cli

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
rem With "nozip" the stale zip is still removed, so an old build can't be shipped by mistake.
if exist "%DIST%\MasselGUARD-%ARCH%.zip" del /f /q "%DIST%\MasselGUARD-%ARCH%.zip" >nul 2>&1
if exist "%DIST%\MasselGUARD-%ARCH%.zip.sha256" del /f /q "%DIST%\MasselGUARD-%ARCH%.zip.sha256" >nul 2>&1
if "%NOZIP%"=="1" (
    if "%PARTIAL%"=="1" (
        echo   Skipped release zip ^(partial build - a release zip needs both exes from one build^)
    ) else (
        echo   Skipped release zip ^(nozip^)
    )
    echo.
    endlocal & exit /b 0
)
echo   Packaging dist\MasselGUARD-%ARCH%.zip ...
rem Retry the zip: cloud sync (OneDrive) / AV can briefly lock a freshly-copied
rem file in dist\%ARCH%\lang\ right as Compress-Archive reads it. Ride it out.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$src='%OUT%\*'; $dst='%DIST%\MasselGUARD-%ARCH%.zip'; for($i=1;$i -le 8;$i++){ try { Compress-Archive -Path $src -DestinationPath $dst -Force -ErrorAction Stop; break } catch { if($i -eq 8){ throw }; Write-Host ('  zip source locked (attempt ' + $i + '/8) - retrying in 3s...'); Start-Sleep -Seconds 3 } }"
if exist "%DIST%\MasselGUARD-%ARCH%.zip" (
    echo   OK  dist\MasselGUARD-%ARCH%.zip
    rem The in-app updater refuses a release whose zip has no matching .sha256 asset: upload both files.
    rem (.NET SHA256, not Get-FileHash: that cmdlet can be unavailable in locked-down hosts.)
    powershell -NoProfile -ExecutionPolicy Bypass -Command "$z='%DIST%\MasselGUARD-%ARCH%.zip'; $s=[IO.File]::OpenRead($z); try { $h=([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($s)) -replace '-','').ToLower() } finally { $s.Close() }; [IO.File]::WriteAllText($z+'.sha256', $h+'  MasselGUARD-%ARCH%.zip'+[char]10)"
    if exist "%DIST%\MasselGUARD-%ARCH%.zip.sha256" (
        echo   OK  dist\MasselGUARD-%ARCH%.zip.sha256
    ) else (
        echo   BUILD FAILED -- could not create the .sha256 for %ARCH%.
        endlocal & exit /b 1
    )
) else (
    echo   BUILD FAILED -- could not create MasselGUARD-%ARCH%.zip for %ARCH%.
    endlocal & exit /b 1
)

echo.
endlocal & exit /b 0
