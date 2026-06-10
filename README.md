# MasselGUARD

**Automated WireGuard tunnel management for Windows**

<img width="1794" height="981" alt="MasselGUARD 3 6 0" src="https://github.com/user-attachments/assets/8018d213-1e91-4503-846e-8eebbf85a94e" />

MasselGUARD sits in the system tray and watches your WiFi connection. When you join a known network it activates the right WireGuard tunnel automatically. When you leave, or land on an unknown network, a configurable fallback fires. It also works as a clean manual WireGuard front-end.

> **User manual** → [`docs/MANUAL.md`](docs/MANUAL.md)
> **CLI manual** → [`docs/CLIManual.md`](docs/CLIManual.md)
> **Technical reference** → [`docs/MasselGUARD.md`](docs/MasselGUARD.md)
> **Release notes (all versions)** → [`docs/WHATSNEW.md`](docs/WHATSNEW.md)

---

## Operating modes

| Mode | When to use |
|---|---|
| **Standalone** | MasselGUARD manages tunnels via `tunnel.dll` + `wireguard.dll`. No WireGuard app needed. |
| **Companion** | Automates the official WireGuard for Windows app. |
| **Mixed** | Both at once — local tunnels and WireGuard profiles side by side. |

---

## Features

### Automation
- **WiFi rules** — map any SSID to any tunnel (or disconnect). Each rule has a **Name**, **SSID**, **Hits counter**, and target tunnel
- WiFi Rules panel: drag-to-reorder, hits counter, click-to-highlight matching rules in tunnel list
- Rules column in tunnel list updates immediately on add/edit/delete
- **Default action** — do nothing / disconnect / activate a fallback when no rule matches
- **Open network protection** — force a tunnel on passwordless WiFi before any rule fires
- **Defaults button** in toolbar — set/clear both roles from a single popup centred on the window
- Rules fire exactly once per network switch (double-fire prevention)

### Auto-reconnect
- Detects unexpected tunnel drops (sleep/wake, kernel crash, network blip) and reconnects automatically
- **3 retry attempts** with 5 s / 10 s / 15 s backoff; gives up cleanly after the third failure
- Intentional disconnects (user, WiFi rule, CLI) are never retried — and a clean deactivate via the WireGuard app is recognised and respected
- Global mode in Settings → Tunnels: **Off** / **Per tunnel** / **Always** (default)
- Per-tunnel toggle in Edit Tunnel dialog; hidden when global mode is Off, greyed when Always

### WireGuard app awareness (companion tunnels)
- Connects and disconnects done in the WireGuard for Windows app are detected, logged, and recorded in history with source *WireGuard app*

### Kill switch
- **Per-tunnel kill switch** — blocks all non-tunnel outbound traffic via Windows Firewall when a tunnel is active
- **Global "Always" mode** — forces the kill switch on for every tunnel without a per-tunnel toggle
- Firewall rules (`MasselGUARD_KS_*`) are removed on clean exit; stale rules from a crash are cleaned up at startup

### Tunnel management
- Live tunnel list — Connect/Disconnect per entry, real-time uptime
- **⚡ / 🔓 badges** inline after tunnel name for default action and open protection
- **Rules column** — count of WiFi rules per tunnel; click to highlight them
- **Tunnel Groups** — colour-coded tabs, drag tunnels between groups by dropping on tab buttons, hide/show, default group, hide empty groups
- **Drag-to-reorder** tunnels and WiFi rules
- Quick Connect — connect any `.conf` from disk without importing
- Pre/post scripts at four hook points per tunnel
- **Pre-flight config validation** — key format, CIDR syntax, MTU, ports, endpoint checked before connect; per-tunnel and global skip option for unusual configs

### History & activity timeline
- **Connection history** — records every tunnel connect/disconnect with timestamp, duration, and traffic (`tunnel_history.json`)
- **WiFi history** — records SSID connect/disconnect with timestamps and open/secured status (`wifi_history.json`)
- **Activity timeline** — canvas above the footer showing tunnel sessions and WiFi rows over the last 24 h / 7 d / 31 d
- **Hover tooltip** — at any X position shows all tunnels connected and the WiFi SSID active at that time; includes duration, traffic, and 🔒/⚠ security tag
- **`< >` navigation** — cycles through tunnel sessions; tooltip shows WiFi active at each session's midpoint
- **Settings — History**: Capture and Show toggles for connections and WiFi are independent; the timeline panel auto-hides when both Show toggles are off

### Interface
- Two-panel layout: tunnel list (+ optional WiFi Rules panel) left | Activity Log right
- **Activity log toggle** — `☰` button in tunnel header opens log; `»` button in log header collapses it; persisted across sessions
- Activity Log: **Time** | **Event** column header; entry count badge; Export Log
- Footer bar: mode (green when installed) | ⚡ default + 🔓 open protection | Administrator
- System tray: two-state shield icon (green filled / grey outline), themed menu with GDI+ icons
- **Custom WPF toast notifications** — fully themed, slides in from bottom-right, shows rule name and category; no system balloon
- **Confirm on close** — optional confirmation dialog before disconnecting active tunnels on exit

### Appearance
- **Unified dual-variant theme system** — one theme file contains both dark and light colour sets
- **Single theme picker** in Settings → Appearance — choose a theme once; dark or light colours are applied automatically based on the current mode
- **System mode pill** — Auto (follows Windows) / Light / Dark
- **System (Windows colors)** available as a theme option — uses the live Windows 11 accent palette
- **Auto colour generation** — define only dark or only light colours; the other variant is computed at load time from HSL lightness inversion, never stored
- **Theme preview** — apply the selected theme for 10 seconds before committing; auto-reverts
- **Font override** — pick any installed font; per-typeface rendering in the dropdown; size slider 8–18 pt
- **Theme Builder** *(coming soon)* — create and edit custom dual-variant themes with live preview
- Built-in themes: **Grey** and **High Contrast**, plus **System (Windows colors)**; user themes in `%APPDATA%\MasselGUARD\themes\`

### Settings
- **Organized in 7 tabs** — General / Tunnels / WiFi / Appearance / History / Advanced / About
- **Fully deferred save** — all changes staged until Save; Cancel reverts everything including previews
- **Tunnels tab** — groups, auto-reconnect, kill switch, config validation, and display options in one place
- **WiFi tab** — rules, default action, and open network protection in evaluation order
- Extended log shows only **changed** fields on Save
- Start with Windows toggle (Scheduled Task, no UAC on subsequent launches)
- Notification duration picker (3 / 5 / 10 / 15 / 30 s)
- **Update check frequency** — On start / Daily / Weekly / Manual
- Five languages: English, Dutch, German, French, Spanish — with country flags in the picker

---

## Requirements

| | |
|---|---|
| OS | Windows 10 or 11 (x64) |
| Runtime | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Elevation | Administrator (or Scheduled Task for UAC-free managed launch) |
| Standalone / Mixed | `tunnel.dll` + `wireguard.dll` v1.1 (wireguard-NT) next to the exe (included in release zip) |
| Companion / Mixed | [WireGuard for Windows](https://wireguard.com/install) installed |

---

## Quick start

1. Extract the zip, run `MasselGUARD.exe`
2. Complete the setup wizard — or import a `.masselguard` settings file on Step 0
3. Add tunnels, create WiFi rules, configure defaults
4. MasselGUARD handles the rest from the tray

---

## Command-line interface

MasselGUARD includes a full CLI for scripting and automation. Requires Administrator.

```
MasselGUARD version
```
```
MasselGUARD v3.6.0  |  Dangerous Donkey
build:   2606040000
Harold Masselink  |  https://masselink.net
Update:  up to date
```

| Command | Aliases | Description |
|---|---|---|
| `list` | `--list` | All tunnels + connected/idle status |
| `status` | `--status` | Active tunnel count and names |
| `connect <name>` | — | Connect a tunnel by name |
| `connect --default` | — | Connect the configured default tunnel |
| `connect --all` | — | Connect all tunnels |
| `disconnect <name>` | — | Disconnect a tunnel by name |
| `disconnect-all` | — | Disconnect all active tunnels |
| `info <name>` | — | Detailed status for one tunnel |
| `log [n]` | — | Last *n* activity log entries (default 20) |
| `tunnel-history [n]` | — | Connection history with source and traffic |
| `wifi-history [n]` | — | WiFi SSID history with duration and security |
| `import <file>` | — | Import a `.conf` or `.conf.dpapi` tunnel |
| `delete <name>` | `remove` | Remove a tunnel from config |
| `rawconnect` | — | Connect a tunnel built from inline parameters |
| `check-update` | `--check-update` | Live update check against GitHub |
| `version` | `--version`, `-v` | Version, build, author and update status |
| `help` | `--help`, `-h` | Command reference |

Flags available on any command: `--json` (machine-readable output), `--quiet` / `-q` (exit code only), `--group <name>`, `--active`.

Exit codes: `0` success · `1` error · `2` already in desired state.

---

## Build

```bat
BUILD.bat
```

Requires .NET 10 SDK. Generates a `YYMMDDHHMM` build stamp, compiles with `dotnet publish`, copies output to `dist\`.

Banner:
```
  --------------------------------------------------
  MasselGUARD  v3.6.0  |  Dangerous Donkey
  Harold Masselink  |  https://masselink.net
  --------------------------------------------------
```

Update `CODENAME` in both `BUILD.bat` and `UpdateChecker.cs` when bumping the version.

---

## Security

Tunnel configs are stored as individual DPAPI-encrypted `.conf.dpapi` files in `%APPDATA%\MasselGUARD\tunnels\` (`CurrentUser` scope). `config.json` never contains key material. Existing inline-encrypted configs are migrated to files automatically on first launch. Plaintext temp file during connection is locked to `SYSTEM + Administrators + owner` from byte 0, deleted within ~200 ms.

`%APPDATA%\MasselGUARD\` is restricted to the current user only (removes inherited Administrators read access). Applied on first Settings save after installation.
