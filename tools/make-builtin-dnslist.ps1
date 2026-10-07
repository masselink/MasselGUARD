<#
.SYNOPSIS
  Builds the DNS server list that is built into MasselGUARD (Resources\dns-servers.builtin.json) from the
  MasselGUARD-dnslist repository (index.json + servers\*.json).

.DESCRIPTION
  The repository holds one file per provider; the app also ships a flat snapshot of the same list for offline use.
  Run this before a release (and after list changes you want in the snapshot), then build. The app's selftest
  (MasselGUARDcli selftest) checks that every entry of the snapshot is valid.

.PARAMETER Repo
  Path of the MasselGUARD-dnslist checkout (default: ..\MasselGUARD-dnslist next to this repository).
#>
param([string]$Repo = (Join-Path $PSScriptRoot '..\..\MasselGUARD-dnslist'))

$ErrorActionPreference = 'Stop'
$Repo = (Resolve-Path $Repo).Path
$index = Get-Content -Raw -Encoding UTF8 (Join-Path $Repo 'index.json') | ConvertFrom-Json
if ($index.schemaVersion -ne 1) { throw "index.json: unsupported schemaVersion $($index.schemaVersion)" }

$servers = New-Object System.Collections.Generic.List[object]
$ids = @{}
foreach ($name in $index.providers) {
    if ($name -notmatch '^[a-z0-9][a-z0-9-]{0,40}$') { throw "index.json: bad provider name '$name'" }
    $file = Join-Path $Repo "servers\$name.json"
    if (-not (Test-Path $file)) { throw "index.json lists '$name' but servers\$name.json does not exist" }
    $p = Get-Content -Raw -Encoding UTF8 $file | ConvertFrom-Json
    if ($p.schemaVersion -ne 1) { throw "$name.json: unsupported schemaVersion" }
    foreach ($s in $p.servers) {
        if ($ids.ContainsKey($s.id)) { throw "duplicate id '$($s.id)' (in $name.json)" }
        $ids[$s.id] = $true
        $servers.Add([ordered]@{
            id            = $s.id
            name          = $s.name
            provider      = $p.provider
            website       = $p.website
            privacyPolicy = $p.privacyPolicy
            country       = $p.country
            description   = [string]$s.description
            blocks        = @($s.blocks | Where-Object { $_ })
            logging       = $s.logging
            v4            = @($s.v4 | Where-Object { $_ })
            v6            = @($s.v6 | Where-Object { $_ })
            doh           = $s.doh
            encryptedOnly = [bool]$s.encryptedOnly
            parameters    = @($s.parameters | Where-Object { $_ } | ForEach-Object { if ($_ -is [string]) { $_ } else { $_.token } })
        })
    }
}

$out = [ordered]@{ schemaVersion = 1; generated = [string]$index.version; count = $servers.Count; servers = $servers }
$target = Join-Path $PSScriptRoot '..\Resources\dns-servers.builtin.json'
$json = $out | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText([IO.Path]::GetFullPath($target), $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote $([IO.Path]::GetFullPath($target)): $($servers.Count) servers from $($index.providers.Count) provider files."
