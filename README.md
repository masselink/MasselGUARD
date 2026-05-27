# MasselGUARD

**Automated WireGuard tunnel management for Windows**

MasselGUARD sits in the system tray and watches your WiFi connection. When you join a known network it activates the right WireGuard tunnel automatically. When you leave, or land on an unknown network, a configurable fallback fires. It also works as a clean manual WireGuard front-end.

> **User manual** → [`docs/MANUAL.md`](docs/MANUAL.md)
> **Technical reference** → [`docs/MasselGUARD.md`](docs/MasselGUARD.md)
> **Change history v2.9→v3.0** → [`docs/CHANGES_v290_to_v300.md`](docs/CHANGES_v290_to_v300.md)
> **Change history v2.5→v2.9** → [`docs/CHANGES_v250_to_v290.md`](docs/CHANGES_v250_to_v290.md)
> **Change history v2.3→v2.5** → [`docs/CHANGES_v231_to_v250.md`](docs/CHANGES_v231_to_v250.md)

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

### Tunnel management
- Live tunnel list — Connect/Disconnect per entry, real-time uptime
- **⚡ / 🔓 badges** inline after tunnel name for default action and open protection
- **Rules column** — count of WiFi rules per tunnel; click to highlight them
- **Tunnel Groups** — colour-coded tabs, drag tunnels between groups by dropping on tab buttons, hide/show, default group, hide empty groups
- **Drag-to-reorder** tunnels and WiFi rules
- Quick Connect — connect any `.conf` from disk without importing
- Pre/post scripts at four hook points per tunnel

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

## Build

```bat
BUILD.bat
```

Generates build number (`3.0.0.YYMMDDHHMM`), injects into `UpdateChecker.cs`, compiles, copies output to `dist\`.

Banner:
```
  ----------------------------------------
    MasselGUARD  v3.0.0.2605271152
    Harold Masselink  |  Claude.ai
  ----------------------------------------
```

---

## Security

Tunnel configs encrypted with Windows DPAPI (`CurrentUser` scope). Plaintext temp file during connection is locked to `SYSTEM + Administrators + owner` from byte 0, deleted within ~200 ms.
