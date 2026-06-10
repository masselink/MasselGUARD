# MasselGUARD — CLI Manual

**Version 3.6.0 — Dangerous Donkey**

MasselGUARD includes a full command-line interface for scripting, automation, and headless operation. The CLI and the GUI share the same WireGuard kernel driver and the same configuration — any change made via CLI is reflected in the GUI within ~1 second, and vice versa.

---

## Contents

1. [Requirements](#1-requirements)
2. [Usage](#2-usage)
3. [Global flags](#3-global-flags)
4. [Exit codes](#4-exit-codes)
5. [Commands](#5-commands)
   - [list](#list)
   - [status](#status)
   - [connect](#connect)
   - [disconnect](#disconnect)
   - [disconnect-all](#disconnect-all)
   - [info](#info)
   - [log](#log)
   - [tunnel-history](#tunnel-history)
   - [wifi-history](#wifi-history)
   - [import](#import)
   - [delete](#delete)
   - [rawconnect](#rawconnect)
   - [check-update](#check-update)
   - [version](#version)
   - [help](#help)
6. [Scripting examples](#6-scripting-examples)
7. [Security notes](#7-security-notes)

---

## 1. Requirements

- **Administrator privileges** — MasselGUARD requires elevation to manage WireGuard kernel adapters and Windows services.
- **Running from an elevated terminal** (recommended) — run PowerShell or cmd.exe as Administrator. Output appears inline in the same window.
- **Running from a non-elevated terminal** — Windows will prompt for UAC elevation and open a new console window. A "Press any key to close" prompt appears so you can read the output before the window closes.

> **Tip:** Install MasselGUARD and enable **Start with Windows** (Settings → General). The Scheduled Task runs at `RunLevel=Highest`, so subsequent CLI calls from any context will not show a UAC popup.

---

## 2. Usage

```
MasselGUARD <command> [arguments] [options]
```

Commands and flag names are **case-insensitive**.

---

## 3. Global flags

These flags work with any command.

| Flag | Description |
|---|---|
| `--json` | Output in JSON format — suitable for piping to `jq` or other tools |
| `--quiet`, `-q` | Suppress all output — rely on exit code only |
| `--group <name>` | Scope `list`, `connect --all`, and `disconnect-all` to one tunnel group |
| `--active` | Filter `list` to connected tunnels only |
| `--logtype normal\|extended` | Control the detail level of `log` output (default: `normal`) |

---

## 4. Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Error — tunnel not found, connect failed, file not found, etc. |
| `2` | Already in desired state — tunnel was already connected / disconnected |

`check-update` returns `1` when an update is available (useful for scripting: non-zero = action needed).

---

## 5. Commands

---

### list

Lists all configured tunnels and their current status.

```
MasselGUARD list
MasselGUARD --list
```

**Options:**

| Flag | Effect |
|---|---|
| `--group <name>` | Show only tunnels in the named group |
| `--active` | Show only connected tunnels |
| `--json` | JSON array output |

**Examples:**

```powershell
# Show all tunnels
MasselGUARD list

# Show only connected tunnels
MasselGUARD list --active

# Show tunnels in the "Work" group
MasselGUARD list --group Work

# Machine-readable output
MasselGUARD list --json
```

**Plain output:**
```
  Name                       Type        Status       Group
  ─────────────────────────  ──────────  ───────────  ──────
  1.HomeVPN                  Local       ● Connected  Home
  2.WorkVPN-Full             Local       ○ Idle        Work
  3.WorkVPN-Split            Local       ○ Idle        Work
```

**JSON output:**
```json
[
  { "name": "1.HomeVPN",      "status": "connected", "source": "local",     "group": "Home" },
  { "name": "2.WorkVPN-Full", "status": "idle",      "source": "local",     "group": "Work" },
  { "name": "3.WorkVPN-Split","status": "idle",      "source": "wireguard", "group": "Work" }
]
```

---

### status

Shows the count and names of currently active tunnels.

```
MasselGUARD status
MasselGUARD --status
```

**Examples:**

```powershell
MasselGUARD status

MasselGUARD status --json
```

**Plain output:**
```
Active: 2
  • 1.HomeVPN
  • 2.WorkVPN-Full
```

**JSON output:**
```json
{ "active_count": 2, "tunnels": ["1.HomeVPN", "2.WorkVPN-Full"] }
```

---

### connect

Connects a tunnel by name. Optionally patches config fields before connecting without modifying the stored file.

```
MasselGUARD connect <name>
MasselGUARD connect --default
MasselGUARD connect --all
```

**Options:**

| Flag | Effect |
|---|---|
| `--default` | Connect the tunnel configured as the default action tunnel |
| `--all` | Connect every idle tunnel |
| `--group <name>` | Combined with `--all`: restrict to one group |
| `--override-dns <servers>` | Override DNS server(s) for this connection only |
| `--override-endpoint <host:port>` | Override server endpoint for this connection only |
| `--override-address <CIDR>` | Override interface address for this connection only |

**Override behaviour:** The stored config file is never modified. Overrides are applied in memory, re-encrypted, and used only for this connection. The GUI will show the tunnel as connected normally.

**Examples:**

```powershell
# Connect by name
MasselGUARD connect "1.HomeVPN"

# Connect the default tunnel
MasselGUARD connect --default

# Connect all idle tunnels
MasselGUARD connect --all

# Connect all idle tunnels in the Work group
MasselGUARD connect --all --group Work

# Connect but override the DNS server for this session
MasselGUARD connect "1.HomeVPN" --override-dns 9.9.9.9

# Connect to the same tunnel via a different endpoint (failover)
MasselGUARD connect "1.HomeVPN" --override-endpoint backup.example.com:51820

# Connect silently — script checks exit code
MasselGUARD connect "1.HomeVPN" --quiet
```

**JSON result:**
```json
{ "result": "connected", "message": "Tunnel '1.HomeVPN' connected." }
```
```json
{ "result": "already_connected", "message": "Tunnel '1.HomeVPN' is already connected." }
```

---

### disconnect

Disconnects a named tunnel.

```
MasselGUARD disconnect <name>
```

**Examples:**

```powershell
MasselGUARD disconnect "1.HomeVPN"

MasselGUARD disconnect "1.HomeVPN" --quiet
```

---

### disconnect-all

Disconnects all active tunnels. Returns exit code `2` (already in desired state) when nothing is active.

```
MasselGUARD disconnect-all
```

**Options:**

| Flag | Effect |
|---|---|
| `--group <name>` | Disconnect only active tunnels in the named group |

**Examples:**

```powershell
# Disconnect everything
MasselGUARD disconnect-all

# Disconnect only active Work tunnels
MasselGUARD disconnect-all --group Work
```

---

### info

Shows detailed status for a single tunnel, including type, group, live uptime, and the source of the last connection.

```
MasselGUARD info <name>
```

**Examples:**

```powershell
MasselGUARD info "1.HomeVPN"

MasselGUARD info "1.HomeVPN" --json
```

**Plain output (connected):**
```
  Name:    1.HomeVPN
  Type:    Local (tunnel.dll)
  Group:   Home
  Status:  ● Connected  1h 23m
  Source:  Rule: HomeWifi → HomeVPN  (today 09:31)
```

**Plain output (disconnected):**
```
  Name:    2.WorkVPN-Full
  Type:    WireGuard for Windows
  Group:   Work
  Status:  ○ Disconnected
  Last:    yesterday 14:05  —  42m 10s  (Manual)
```

**JSON output:**
```json
{
  "name":           "1.HomeVPN",
  "status":         "connected",
  "type":           "local",
  "group":          "Home",
  "uptime_sec":     4980,
  "last_source":    "Rule: HomeWifi → HomeVPN",
  "last_connected": "2026-06-02T09:31:00+02:00"
}
```

---

### log

Shows recent connection history. Reads from `%APPDATA%\MasselGUARD\tunnel_history.json` — the **same file** that Settings → History shows in the GUI. No duplication; one source of truth.

> **Note:** The GUI's activity log panel (debug entries, timing, script output) is in-memory only and is not accessible from the CLI. `log` shows connection history only.

```
MasselGUARD log
MasselGUARD log <n>
```

**Options:**

| Flag | Effect |
|---|---|
| `<n>` | Number of entries to show (default: 20) |
| `--logtype normal` | Tunnel \| When \| Duration (default) |
| `--logtype extended` | Adds the **Source** column (what triggered the connection) |
| `--json` | Full JSON output including timestamps and source |

**Examples:**

```powershell
# Last 20 entries
MasselGUARD log

# Last 5 entries
MasselGUARD log 5

# With trigger source
MasselGUARD log 10 --logtype extended

# Machine-readable
MasselGUARD log 50 --json
```

**Plain output (normal):**
```
  Tunnel              When               Duration
  ──────────────────  ─────────────────  ──────────
  1.HomeVPN           today 09:31        active
  2.WorkVPN-Full      yesterday 14:05    42m 10s
  1.HomeVPN           yesterday 08:12    6h 41m
```

**Plain output (extended):**
```
  Tunnel              When               Duration    Source
  ──────────────────  ─────────────────  ──────────  ──────────────────────────
  1.HomeVPN           today 09:31        active      Rule: HomeWifi → HomeVPN
  2.WorkVPN-Full      yesterday 14:05    42m 10s     Manual
  1.HomeVPN           yesterday 08:12    6h 41m      Auto-reconnect
```

**JSON output:**
```json
[
  {
    "tunnel":           "1.HomeVPN",
    "connected_at":     "2026-06-02T09:31:00+02:00",
    "disconnected_at":  null,
    "duration_sec":     null,
    "active":           true,
    "source":           null
  },
  {
    "tunnel":           "2.WorkVPN-Full",
    "connected_at":     "2026-06-01T14:05:00+02:00",
    "disconnected_at":  "2026-06-01T14:47:10+02:00",
    "duration_sec":     2530,
    "active":           false,
    "source":           null
  }
]
```

> The `source` field is only populated in JSON when `--logtype extended` is used.

---

### tunnel-history

Shows tunnel connection history. Reads from `%APPDATA%\MasselGUARD\tunnel_history.json`. Always includes the trigger source and — in JSON output — session traffic bytes.

```
MasselGUARD tunnel-history
MasselGUARD tunnel-history <n>
```

**Options:**

| Flag | Effect |
|---|---|
| `<n>` | Number of entries to show (default: 20) |
| `--json` | Full JSON output including source and traffic bytes |

**Examples:**

```powershell
# Last 20 entries
MasselGUARD tunnel-history

# Last 10 entries
MasselGUARD tunnel-history 10

# Machine-readable
MasselGUARD tunnel-history 50 --json
```

**Plain output:**
```
  Tunnel              When               Duration    Source
  ──────────────────  ─────────────────  ──────────  ──────────────────────────
  1.HomeVPN           today 09:31        active      Rule: HomeWifi → HomeVPN
  2.WorkVPN-Full      yesterday 14:05    42m 10s     Manual
  1.HomeVPN           yesterday 08:12    6h 41m      Auto-reconnect
```

**JSON output:**
```json
[
  {
    "tunnel":           "1.HomeVPN",
    "connected_at":     "2026-06-02T09:31:00+02:00",
    "disconnected_at":  null,
    "duration_sec":     null,
    "active":           true,
    "source":           "Rule: HomeWifi → HomeVPN",
    "rx_bytes":         0,
    "tx_bytes":         0
  },
  {
    "tunnel":           "2.WorkVPN-Full",
    "connected_at":     "2026-06-01T14:05:00+02:00",
    "disconnected_at":  "2026-06-01T14:47:10+02:00",
    "duration_sec":     2530,
    "active":           false,
    "source":           "Manual",
    "rx_bytes":         148404224,
    "tx_bytes":         12582912
  }
]
```

---

### wifi-history

Shows WiFi SSID connection history. Reads from `%APPDATA%\MasselGUARD\wifi_history.json`. Requires **Settings → History → Capture → WiFi (SSID)** to be enabled; returns an empty list otherwise.

```
MasselGUARD wifi-history
MasselGUARD wifi-history <n>
```

**Options:**

| Flag | Effect |
|---|---|
| `<n>` | Number of entries to show (default: 20) |
| `--json` | Full JSON output including `open` flag and timestamps |

**Examples:**

```powershell
# Last 20 entries
MasselGUARD wifi-history

# Last 5 entries
MasselGUARD wifi-history 5

# Machine-readable
MasselGUARD wifi-history --json
```

**Plain output:**
```
  SSID              When               Duration    Security
  ────────────────  ─────────────────  ──────────  ────────
  HomeNetwork       today 07:45        active      secured
  CoffeeShop-Free   yesterday 11:20    1h 12m      open
  WorkOffice        yesterday 08:05    9h 02m      secured
```

**JSON output:**
```json
[
  {
    "ssid":             "HomeNetwork",
    "connected_at":     "2026-06-02T07:45:00+02:00",
    "disconnected_at":  null,
    "duration_sec":     null,
    "active":           true,
    "open":             false
  },
  {
    "ssid":             "CoffeeShop-Free",
    "connected_at":     "2026-06-01T11:20:00+02:00",
    "disconnected_at":  "2026-06-01T12:32:00+02:00",
    "duration_sec":     4320,
    "active":           false,
    "open":             true
  }
]
```

---

### import

Imports a WireGuard `.conf` or `.conf.dpapi` file into MasselGUARD as a local tunnel. Duplicate names are rejected.

```
MasselGUARD import <file>
```

**Options:**

| Flag | Effect |
|---|---|
| `--name <display-name>` | Override the tunnel name (default: filename without extension) |
| `--group <name>` | Assign the tunnel to a group |
| `--unsecure` | Store without DPAPI encryption (copies plaintext to the tunnels folder) |

**Default behaviour (secure):** The config is DPAPI-encrypted (`CurrentUser` scope) and written to `%APPDATA%\MasselGUARD\tunnels\<name>.conf.dpapi`. Only the file path is stored in `config.json` — no key material. The original file is not moved or deleted.

**`--unsecure` behaviour:** A copy of the plaintext `.conf` is written to `<exedir>\tunnels\<name>.conf`. A warning is printed. Useful when the config must be readable on disk (e.g. shared admin tools), but not recommended.

**Importing a `.conf.dpapi` file:** The file is decrypted first (requires the same Windows user account that encrypted it). The decrypted content is then re-encrypted under the current user.

**Examples:**

```powershell
# Basic import — name taken from filename
MasselGUARD import "C:\VPN\home.conf"

# Import with custom name and group
MasselGUARD import "C:\VPN\home.conf" --name "Home VPN" --group Personal

# Import an already-encrypted file
MasselGUARD import "C:\Backup\home.conf.dpapi" --name "Home VPN"

# Import without DPAPI (not recommended)
MasselGUARD import "C:\VPN\home.conf" --unsecure

# Import silently and check exit code
MasselGUARD import "C:\VPN\home.conf" --quiet
if ($LASTEXITCODE -eq 0) { Write-Host "Imported." }
```

**JSON result:**
```json
{ "result": "imported", "message": "Tunnel 'home' imported successfully." }
```

---

### delete

Removes a tunnel from the configuration. If the tunnel used a file on disk (path-based), the file is also deleted.

```
MasselGUARD delete <name>
MasselGUARD remove <name>
```

**Options:**

| Flag | Effect |
|---|---|
| `--force` | Disconnect the tunnel first if it is currently active |

**Without `--force`:** exits with error code `1` if the tunnel is active.

**Examples:**

```powershell
# Delete an idle tunnel
MasselGUARD delete "Home VPN"

# Force-disconnect and delete
MasselGUARD delete "Home VPN" --force

# Delete silently
MasselGUARD delete "Home VPN" --force --quiet
```

---

### rawconnect

Builds a WireGuard connection entirely from command-line parameters, without a stored config file. Useful for one-off or automated connections where the config is generated at runtime.

```
MasselGUARD rawconnect --endpoint <host:port> --pubkey <key>
             (--privkey <key> | --privkeyfile <path>)
             [options]
```

**Required flags:**

| Flag | Description |
|---|---|
| `--endpoint <host:port>` | Server endpoint, e.g. `vpn.example.com:51820` |
| `--pubkey <base64>` | Server public key |
| `--privkey <base64>` | Client private key (⚠ visible in process listings — see Security notes) |
| `--privkeyfile <path>` | Client private key read from a file (recommended) |

**Optional flags:**

| Flag | Description |
|---|---|
| `--address <CIDR>` | Interface address, e.g. `10.0.0.2/32` |
| `--dns <servers>` | DNS server(s), e.g. `1.1.1.1` or `1.1.1.1,8.8.8.8` |
| `--psk <base64>` | Pre-shared key (inline) |
| `--pskfile <path>` | Pre-shared key read from a file |
| `--allowed <CIDRs>` | Allowed IPs (default: `0.0.0.0/0, ::/0`) |
| `--name <display>` | Display name for this session (default: `rawconnect-YYMMDDHHMM`) |
| `--group <name>` | Group assignment (only relevant with `--save`) |
| `--save` | Import the tunnel permanently into config before connecting |

**Examples:**

```powershell
# Minimal connection — private key loaded from file
MasselGUARD rawconnect `
  --endpoint vpn.example.com:51820 `
  --pubkey   <server-public-key> `
  --privkeyfile C:\Keys\client.key

# With address and DNS
MasselGUARD rawconnect `
  --endpoint   vpn.example.com:51820 `
  --pubkey     <server-public-key> `
  --privkeyfile C:\Keys\client.key `
  --address    10.0.0.2/32 `
  --dns        10.0.0.1

# Split-tunnel (only route specific IPs through VPN)
MasselGUARD rawconnect `
  --endpoint   vpn.example.com:51820 `
  --pubkey     <server-public-key> `
  --privkeyfile C:\Keys\client.key `
  --allowed    10.0.0.0/8,192.168.1.0/24

# One-off connection with a display name
MasselGUARD rawconnect `
  --endpoint   vpn.example.com:51820 `
  --pubkey     <server-public-key> `
  --privkeyfile C:\Keys\client.key `
  --name       "Conference VPN"

# Connect and also save permanently to config
MasselGUARD rawconnect `
  --endpoint   vpn.example.com:51820 `
  --pubkey     <server-public-key> `
  --privkeyfile C:\Keys\client.key `
  --name       "Home VPN" --group Personal --save
```

---

### check-update

Performs a live update check against the GitHub releases API. Updates the cached status used by `version`.

```
MasselGUARD check-update
MasselGUARD --check-update
```

**Exit codes:**

| Code | Meaning |
|---|---|
| `0` | Up to date (or running ahead of latest) |
| `1` | Update available — or check failed |

> Exit code `1` on update available is intentional for scripting: non-zero means action is needed.

**Examples:**

```powershell
MasselGUARD check-update

MasselGUARD check-update --json
```

**Plain output:**
```
Up to date — v3.6.0 is the latest release.
```
```
Update available: v3.7.0  (current: v3.6.0)
```

**JSON output:**
```json
{
  "result":  "up_to_date",
  "current": "3.6.0",
  "latest":  "3.6.0",
  "message": "Up to date — v3.6.0 is the latest release."
}
```
```json
{
  "result":  "update_available",
  "current": "3.6.0",
  "latest":  "3.7.0",
  "message": "Update available: v3.7.0  (current: v3.6.0)"
}
```

---

### version

Shows the current version, build stamp, author, and cached update status.

```
MasselGUARD version
MasselGUARD --version
MasselGUARD -v
```

**Plain output:**
```
MasselGUARD v3.6.0  |  Dangerous Donkey
build:   2606040000
Harold Masselink  |  https://masselink.net
Update:  up to date
```

**JSON output:**
```json
{
  "version":       "3.6.0",
  "codename":      "Dangerous Donkey",
  "build":         "2606040000",
  "update_status": "up to date"
}
```

> The update status is read from the last cached check. Run `check-update` to refresh it.

---

### help

Prints a compact command reference.

```
MasselGUARD help
MasselGUARD --help
MasselGUARD -h
```

---

## 6. Scripting examples

### Connect on login, disconnect on logoff

```powershell
# connect.ps1  (run as Administrator at login)
MasselGUARD connect "Work VPN" --quiet
exit $LASTEXITCODE
```

```powershell
# disconnect.ps1  (run as Administrator at logoff)
MasselGUARD disconnect-all --quiet
exit 0
```

### Fail-over to backup endpoint

```powershell
MasselGUARD connect "Work VPN" --quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "Primary failed, trying backup..."
    MasselGUARD connect "Work VPN" --override-endpoint backup.example.com:51820 --quiet
}
```

### Nightly batch import of new tunnel configs

```powershell
# Imports any .conf file in a drop folder, skips already-imported ones
$drop = "C:\VPN\Pending"
foreach ($f in Get-ChildItem $drop -Filter "*.conf") {
    $result = MasselGUARD import $f.FullName --group Batch --json | ConvertFrom-Json
    if ($result.result -eq "imported") {
        Write-Host "Imported: $($f.Name)"
        Remove-Item $f.FullName
    } else {
        Write-Host "Skipped:  $($f.Name) — $($result.message)"
    }
}
```

### Alert when an update is available

```powershell
MasselGUARD check-update --quiet
if ($LASTEXITCODE -eq 1) {
    $info = MasselGUARD check-update --json | ConvertFrom-Json
    Send-MailMessage -To admin@example.com `
        -Subject "MasselGUARD update available" `
        -Body $info.message
}
```

### Disconnect all Work tunnels before a meeting

```powershell
# Bound to a keyboard shortcut or a scheduled task
MasselGUARD disconnect-all --group Work --quiet
```

### Audit: show all tunnels that connected in the last 24 hours

```powershell
$cutoff = (Get-Date).AddHours(-24).ToUniversalTime()
MasselGUARD log 100 --logtype extended --json |
    ConvertFrom-Json |
    Where-Object { [datetime]$_.connected_at -gt $cutoff } |
    Select-Object tunnel, connected_at, duration_sec, source |
    Format-Table
```

### Health check — alert if VPN drops

```powershell
# Run every 5 minutes via Scheduled Task
$status = MasselGUARD status --json | ConvertFrom-Json
if ($status.active_count -eq 0) {
    # No active tunnels — reconnect default
    MasselGUARD connect --default --quiet
    if ($LASTEXITCODE -ne 0) {
        # Still failed — log or alert
        Add-Content "C:\Logs\vpn-monitor.log" "$(Get-Date) VPN reconnect failed"
    }
}
```

---

## 7. Security notes

### Private key on the command line

When using `--privkey <key>` in `rawconnect`, the private key is:
- Visible in `Get-Process` / `ps` output while the command runs
- Stored in shell history (`~/.pshistory`, `%APPDATA%\Microsoft\Windows\PowerShell\PSReadLine\ConsoleHost_history.txt`)
- Potentially logged by audit software or EDR solutions

**Always prefer `--privkeyfile <path>` in production.** Store the key file with restrictive ACLs:

```powershell
# Create key file with restricted ACL (current user only)
$keyPath = "C:\Keys\client.key"
Set-Content $keyPath "<private-key>"
$acl = Get-Acl $keyPath
$acl.SetAccessRuleProtection($true, $false)
$rule = New-Object Security.AccessControl.FileSystemAccessRule(
    $env:USERNAME, "Read", "Allow")
$acl.AddAccessRule($rule)
Set-Acl $keyPath $acl
```

### DPAPI scope

Tunnel configs are encrypted with Windows DPAPI at `CurrentUser` scope. This means:

- The config can only be decrypted by the **same Windows user account** on the **same machine**
- Backing up a `.conf.dpapi` file and restoring it on a different machine or user account will fail to decrypt
- To move a config, use `MasselGUARD import` on the original machine (the GUI can also export configs) and re-import on the target

### `--unsecure` import

Storing a config without DPAPI means anyone with Administrator access to the machine can read the private key. Only use `--unsecure` when:
- DPAPI is unsuitable (service accounts, cross-user automation)
- The machine has other physical or software security controls in place

The MasselGUARD CLI will always print a visible warning when `--unsecure` is used.
