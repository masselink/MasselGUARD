# MasselGUARD

**Automated WireGuard tunnel management for Windows**

MasselGUARD sits in the system tray and watches your WiFi connection. When you connect to a known network it activates the right WireGuard tunnel automatically. When you leave, or land on an unknown network, a configurable default action fires — disconnect all tunnels, switch to a fallback, or do nothing. It also works as a pure manual client when automation is not wanted.

> **How it works** → [`docs/MasselGUARD.md`](docs/MasselGUARD.md)  
> **Release history** → [`docs/RELEASE_NOTES.md`](docs/RELEASE_NOTES.md)  
> **User manual** → [`docs/MANUAL.md`](docs/MANUAL.md)

---

## The idea

Most WireGuard users end up with the same problem: they have several tunnel profiles for different contexts — a home router, an office gateway, a travel VPN — and they keep forgetting to switch. MasselGUARD solves this by tying tunnel selection to the WiFi network you are on. You configure the rules once; from then on the right tunnel is active automatically.

For users who do not want automation, MasselGUARD is also a clean WireGuard front-end: a compact tunnel list, one-click connect and disconnect, and a tray icon that shows at a glance whether anything is active — including a badge counter showing the number of active tunnels.

---

## Operating modes

**Standalone** — No WireGuard app required. MasselGUARD manages tunnels entirely using `tunnel.dll` and `wireguard.dll` (wireguard-NT). Tunnel configs are created and edited inside the app, encrypted with DPAPI, and stored locally.

**Companion** — Works alongside the official WireGuard for Windows application. MasselGUARD automates connecting and disconnecting existing WireGuard profiles based on network rules without touching any config files.

**Mixed** — Both modes active simultaneously — manage standalone local tunnels and automate WireGuard profiles side by side.

---

## Features

### Automation
| | |
|---|---|
| **WiFi rules** | Map any SSID to any tunnel. Leave the tunnel field empty to disconnect on that network. |
| **Default action** | Do nothing / disconnect all / activate a named fallback when no rule matches. |
| **Open network protection** | Automatically activates a chosen tunnel on open (passwordless) WiFi — before rules are evaluated. |
| **Disable WiFi rules** | Disable all network-based automation; control tunnels by hand. |

### Tunnel management
| | |
|---|---|
| **Tunnel groups** | Organise tunnels into named categories (Work, Personal, Travel, …). Switch between them via tabs. Uncategorized always available. |
| **Pre/post scripts** | Run a `.bat` or `.ps1` before/after connect and before/after disconnect. Per tunnel. Can be stored as a file path or embedded inline. |
| **Quick Connect** | Open any `.conf` or `.conf.dpapi` from disk and connect instantly without importing. |
| **Import** | Import a `.conf` file (Standalone) or link an existing WireGuard profile (Companion). |
| **Notes** | Add notes to any tunnel — shown as a tooltip. |

### Interface
| | |
|---|---|
| **Two-panel layout** | Left: tunnel list with category tabs. Right: Activity Log with Export. |
| **System tray** | Badge counter when tunnels are active. Tunnel toggle from tray menu. |
| **Tray toast** | Branded popup near the tray on auto-switch. Toggle in Settings → Appearance. |
| **Activity log** | Timestamped log of all events. Normal and Extended verbosity. Export to `.txt`. |

### Themes
| | |
|---|---|
| **Built-in themes** | Default Dark, Default Light, Grey Dark, Grey Light, High Contrast Dark, High Contrast Light. |
| **Custom themes** | Drop a `theme.json` folder next to the exe. Full colour, corner radius, font, window opacity, custom logo, tray colours, log timestamp colour. |
| **Auto-switching** | Follows Windows dark/light system preference. |
| **Hot-swap** | Themes apply instantly without restart. |

### Settings
| | |
|---|---|
| **Multi-language** | English · Dutch · German · French · Spanish. Add any language by dropping a JSON file in `lang\`. |
| **Settings tabs** | General · Appearance · Default Action · WiFi Rules · Advanced · About |
| **Import / Export** | Export rules, groups, and preferences to a `.masselguard` file. Version-tolerant format with mismatch warning on import. |
| **Setup wizard** | First-run wizard for language, mode, and automation. Re-runnable from Settings. |
| **Install / Uninstall** | Built-in installer: copies to Program Files, Start Menu shortcut, optional auto-start. |
| **Orphan cleanup** | Detect and remove `WireGuardTunnel$` services left behind after a crash. |
| **Update checker** | Compares running version against GitHub tags. |

---

## Requirements

| | |
|---|---|
| OS | Windows 10 or 11 (x64) |
| Runtime | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Elevation | Administrator |
| Standalone / Mixed | `tunnel.dll` + `wireguard.dll` next to the exe (included in release zip) |
| Companion / Mixed | [WireGuard for Windows](https://wireguard.com/install) installed |

---

## Quick start

1. Extract the release zip and run `MasselGUARD.exe` (accept UAC prompt)
2. Complete the setup wizard — choose language, mode, and automation preference
3. Add tunnels (**+ Add** for Standalone, **Import → Link** for Companion)
4. Add WiFi rules under **Settings → WiFi Rules**
5. MasselGUARD handles the rest from the tray

---

## Build

```bat
BUILD.bat
```

Compiles, copies themes, and copies DLLs from `wireguard-deps\` to `dist\`. If DLLs are missing, run `tunnelbuild\tunnelbuild.bat` first (requires Go + gcc).

---

## Security

Tunnel configs are encrypted with Windows DPAPI (`CurrentUser` scope). The plaintext temp file written during connection is locked to `SYSTEM + Administrators + owning user` from the first byte and deleted within ~200 ms. See [`docs/MasselGUARD.md`](docs/MasselGUARD.md#14-security-model).
