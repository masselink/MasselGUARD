# MasselGUARD tunnel-build dependency check / installer.
#   check-deps.ps1            -> validate and report (exit 0 = ready, 1 = missing required)
#   check-deps.ps1 -Install   -> additionally try to install missing required tools (winget)
#
# The tunnel.dll build is hermetic: wireguard-windows' build.bat downloads its OWN Go and
# llvm-mingw C toolchain into .deps, so NO system Go or C compiler is required. The real
# prerequisites are just git (to clone), curl + tar (used by build.bat and this script), and
# network access to download.wireguard.com and github.com.
param([switch]$Install)

$ErrorActionPreference = 'Continue'
$missing = @()

function Test-Cmd([string]$name) { Get-Command $name -ErrorAction SilentlyContinue | Select-Object -First 1 }
function FirstLine([string]$s) { ($s -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1) }

# TCP-connect reachability (avoids TLS quirks; just proves the host:port is reachable).
function Test-Tcp([string]$Host_, [int]$Port) {
    try {
        $c = New-Object System.Net.Sockets.TcpClient
        $iar = $c.BeginConnect($Host_, $Port, $null, $null)
        $ok = $iar.AsyncWaitHandle.WaitOne(6000, $false)
        if ($ok -and $c.Connected) { $c.EndConnect($iar); $c.Close(); return $true }
        $c.Close(); return $false
    } catch { return $false }
}

function Show([string]$state, [string]$name, [string]$detail) {
    Write-Host ("  [{0,-4}] {1,-10} {2}" -f $state, $name, $detail)
}

Write-Host ''
Write-Host ' =================================================='
Write-Host '   MasselGUARD tunnel-build dependency check'
Write-Host ' =================================================='
Write-Host ''
Write-Host ' Required tools:'

# --- git (required: clone wireguard-windows + wireguard-nt via our script) ---
$git = Test-Cmd git
if ($git) { Show 'OK' 'git' ("{0}   {1}" -f (FirstLine (& git --version 2>&1)), $git.Source) }
else      { Show 'MISS' 'git' 'not found -- REQUIRED to clone sources'; $missing += 'git' }

# --- curl (required: build.bat downloads its toolchain with curl) ---
$curl = Test-Cmd curl.exe
if ($curl) { Show 'OK' 'curl' ("{0}" -f $curl.Source) }
else       { Show 'MISS' 'curl' 'not found -- bundled with Windows 10 1803+/11'; $missing += 'curl' }

# --- tar (required: extract .zip archives) ---
$tar = Test-Cmd tar.exe
if ($tar) { Show 'OK' 'tar' ("{0}" -f $tar.Source) }
else      { Show 'MISS' 'tar' 'not found -- bundled with Windows 10 1803+/11'; $missing += 'tar' }

Write-Host ''
Write-Host ' Network (the build downloads its toolchain from these):'
$net = @(
    @{ h='download.wireguard.com'; req=$true;  note='Go, llvm-mingw, make, imagemagick, wireguard-nt' }
    @{ h='github.com';             req=$true;  note='wireguard-tools mirror' }
    @{ h='codeload.github.com';    req=$true;  note='GitHub archive host' }
    @{ h='git.zx2c4.com';          req=$false; note='snapshot unreliable here; GitHub mirror used instead' }
)
function ShowNet([string]$state, [string]$host_, [string]$detail) {
    Write-Host ("  [{0,-4}] {1,-23} {2}" -f $state, $host_, $detail)
}
foreach ($n in $net) {
    $ok = Test-Tcp $n.h 443
    if ($ok)          { ShowNet 'OK'   $n.h $n.note }
    elseif ($n.req)   { ShowNet 'MISS' $n.h ("UNREACHABLE:443 -- " + $n.note); $missing += ("net:" + $n.h) }
    else              { ShowNet 'note' $n.h ("unreachable (ok) -- " + $n.note) }
}

Write-Host ''
Write-Host ' Not required (build.bat self-provisions these into .deps):'
Write-Host '   Go compiler and the llvm-mingw C toolchain -- no system install needed.'

# --- Optional install of missing required tools ---
if ($Install -and ($missing | Where-Object { $_ -notlike 'net:*' })) {
    Write-Host ''
    Write-Host ' --------------------------------------------------'
    Write-Host '   Installing missing tools...'
    Write-Host ' --------------------------------------------------'
    $winget = Test-Cmd winget
    foreach ($dep in ($missing | Where-Object { $_ -notlike 'net:*' })) {
        switch ($dep) {
            'git' {
                if ($winget) {
                    Write-Host '   Installing Git (winget Git.Git) -- accept the UAC prompt...'
                    & winget install --id Git.Git -e --source winget --accept-package-agreements --accept-source-agreements
                } else {
                    Write-Host '   winget not available. Install Git manually: https://git-scm.com/download/win'
                }
            }
            default {
                Write-Host "   '$dep' is a built-in Windows tool (System32). If missing, update Windows"
                Write-Host '   (10 1803+ / 11) -- it cannot be installed via winget.'
            }
        }
    }
    Write-Host ''
    Write-Host '   Re-run "tunnelbuild.bat check" to confirm, then open a NEW terminal so a'
    Write-Host '   freshly installed git is on PATH.'
}

Write-Host ''
Write-Host ' =================================================='
if ($missing.Count -eq 0) {
    Write-Host '   RESULT: READY -- you can build x64 + arm64.'
    Write-Host ' =================================================='
    exit 0
} else {
    $toolMiss = $missing | Where-Object { $_ -notlike 'net:*' }
    Write-Host ("   RESULT: MISSING -> {0}" -f ($missing -join ', '))
    if ($toolMiss -and -not $Install) {
        Write-Host '   Run "tunnelbuild.bat check install" to install missing tools.'
    }
    Write-Host ' =================================================='
    exit 1
}
