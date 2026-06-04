# MasselGUARD

**Automated WireGuard tunnel management for Windows**

<img width="1794" height="981" alt="MasselGUARD 3 5 0" src="https://github.com/user-attachments/assets/8018d213-1e91-4503-846e-8eebbf85a94e" />

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
- **Custom appearance system** — toggle between Windows 11 system colours and custom theme files
- **System mode** — Auto (follows Windows) / Light / Dark pill selector
- **Separate dark/light theme pickers** — independent theme files for each mode
- **Theme preview button** — apply the selected theme for 10 seconds before committing; auto-reverts
- **Font override** — pick any installed font; per-typeface rendering in the dropdown; size slider 8–18 pt
- **Font preview button** — apply the draft font to the whole interface for 10 seconds; auto-reverts
- Six built-in themes: Default Dark/Light, Grey Dark/Light, High Contrast Dark/Light

### Settings
- **Fully deferred save** — all changes staged until Save; Cancel reverts everything including previews
- **Tunnel Groups** dedicated tab in Settings
- Extended log shows only **changed** fields on Save
- Start with Windows toggle (Scheduled Task, no UAC on subsequent launches)
- Notification duration picker (3 / 5 / 10 / 15 / 30 s)
- **Update check frequency** — On start / Daily / Weekly / Manual
- Five languages: English, Dutch, German, French, Spanish

---

## Requirements

| | |
|---|---|
| OS | Windows 10 or 11 (x64) |
| Runtime | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Elevation | Administrator (or Scheduled Task for UAC-free managed launch) |
| Standalone / Mixed | `tunnel.dll` + `wireguard.dll` next to the exe (included in release zip) |
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
MasselGUARD v3.3.0  |  Camouflaged Koala
build:   2506011430
Harold Masselink  |  https://masselink.net
Update:  up to date
```

| Command | Aliases | Description |
|---|---|---|
| `list` | `--list` | All tunnels + connected/idle status |
| `status` | `--status` | Active tunnel count and names |
| `connect <name>` | — | Connect a tunnel by name |
| `connect --default` | — | Connect the configured default tunnel |
| `disconnect <name>` | — | Disconnect a tunnel by name |
| `disconnect-all` | — | Disconnect all active tunnels |
| `version` | `--version`, `-v` | Version, build, author and update status |
| `help` | `--help`, `-h` | Command reference |

Flags available on any command: `--json` (machine-readable output), `--quiet` / `-q` (exit code only).

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
  MasselGUARD  v3.3.0  |  Camouflaged Koala
  Harold Masselink  |  https://masselink.net
  --------------------------------------------------
```

Update `CODENAME` in both `BUILD.bat` and `UpdateChecker.cs` when bumping the version.

---

## Security

Tunnel configs are stored as individual DPAPI-encrypted `.conf.dpapi` files in `%APPDATA%\MasselGUARD\tunnels\` (`CurrentUser` scope). `config.json` never contains key material. Existing inline-encrypted configs are migrated to files automatically on first launch. Plaintext temp file during connection is locked to `SYSTEM + Administrators + owner` from byte 0, deleted within ~200 ms.
