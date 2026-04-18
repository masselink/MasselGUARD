# MasselGUARD

**Automated WireGuard tunnel management for Windows**

MasselGUARD sits in the system tray and watches your WiFi. When you connect to a known network it activates the right WireGuard tunnel automatically. When you leave, or land on an unknown network, a configurable default action fires — disconnect all tunnels, switch to a fallback, or do nothing. It also works as a pure manual client when automation is not wanted.

> **Detailed documentation** → [`docs/MasselGUARD.md`](docs/MasselGUARD.md)  
> **Version history** → [`docs/RELEASE_NOTES.md`](docs/RELEASE_NOTES.md)

---

## The idea

Most WireGuard users end up with the same problem: they have several tunnel profiles for different contexts — a home router, an office gateway, a travel VPN — and they keep forgetting to switch. MasselGUARD solves this by tying tunnel selection to the WiFi network you are on. You configure the rules once; from then on the right tunnel is active before your browser even opens.

For users who do not want automation, MasselGUARD is also a clean WireGuard front-end: a compact tunnel list, one-click connect and disconnect, and a tray icon that shows at a glance whether anything is active.

---

## Operating modes

### Standalone
No WireGuard app required. MasselGUARD manages tunnels entirely using `tunnel.dll` and `wireguard.dll` (wireguard-NT). Tunnel configs are created and edited inside the app, encrypted with DPAPI, and stored locally.

### Companion
Works alongside the official WireGuard for Windows application. MasselGUARD automates connecting and disconnecting existing WireGuard profiles based on WiFi rules without touching any config files.

### Mixed
Both modes active simultaneously — manage standalone local tunnels and automate WireGuard profiles side by side.

---

## Features

### Automation
| | |
|---|---|
| **WiFi rules** | Map any SSID to any tunnel. Leave the tunnel field empty to disconnect on that network. |
| **Default action** | Do nothing / disconnect all / activate a named fallback tunnel when no rule matches. |
| **Open network protection** | Automatically activates a chosen tunnel on open (passwordless) WiFi — runs before any rule is evaluated. |
| **Manual mode** | Disable all WiFi-based automation; control tunnels by hand. |

### Tunnel management
| | |
|---|---|
| **Tunnel groups** | Organise tunnels into named categories (Work, Personal, Travel, …). Switch between them via tabs above the tunnel list. Uncategorized always available. |
| **Tunnel list** | Live status, one-click connect/disconnect. Action button morphs: Delete / Remove / Unlink depending on type and availability. |
| **Quick Connect** | Open any `.conf` or `.conf.dpapi` from disk and connect instantly without importing. Appears in the tunnel list; disconnect from there. |
| **Import** | Import a `.conf` file (Standalone) or link an existing WireGuard profile (Companion). |
| **Notes** | Add notes to any tunnel — shown as a tooltip on the tunnel name. |

### Interface
| | |
|---|---|
| **Two-panel layout** | Left: tunnel list with category tabs. Right: Options panel with Activity Log, Rules, Default Action, Open Network tabs. |
| **System tray** | Coloured dot indicator when connected. Tunnel toggle from tray menu. |
| **Tray toast** | Branded popup near the tray on auto-switch, showing the tunnel name and the reason (rule, default, open network). |
| **Activity log** | Timestamped log of all events. Four verbosity levels: Normal, Info, Verbose, Debug. Export to `.txt`. |

### Themes
| | |
|---|---|
| **Built-in themes** | Default Dark, Default Light, Grey Dark (sharp corners), Grey Light (sharp corners), Colour-Blind Dark, Colour-Blind Light. |
| **Custom themes** | Drop a `theme.json` folder next to the exe. Full colour palette, corner radius, font, window opacity, title bar chrome, status bar visibility, custom logo, tray menu colours. |
| **Auto-switching** | Follows Windows dark/light system preference automatically. |
| **Hot-swap** | Themes apply instantly without restart. |

### Settings
| | |
|---|---|
| **Multi-language** | 🇬🇧 English · 🇳🇱 Dutch · 🇩🇪 German · 🇫🇷 French · 🇪🇸 Spanish. Add any language by dropping a JSON file in `lang\`. |
| **Setup wizard** | First-run wizard for language, mode, and automation. Re-runnable from Settings. |
| **Install / Uninstall** | Built-in installer: copies to Program Files, Start Menu shortcut, optional auto-start. |
| **Orphan cleanup** | Detect and remove `WireGuardTunnel$` SCM entries left behind after a crash. |
| **Update checker** | Compares running version against GitHub tags. |

---

## Requirements

| | |
|---|---|
| OS | Windows 10 or 11 (x64) |
| Runtime | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Elevation | Administrator (UAC prompt on launch) |
| Standalone / Mixed | `tunnel.dll` + `wireguard.dll` (wireguard-NT) next to the exe |
| Companion / Mixed | [WireGuard for Windows](https://wireguard.com/install) installed |

---

## Quick start

1. Download and extract the release zip
2. Run `MasselGUARD.exe` (accept UAC prompt)
3. The setup wizard opens — choose your language, mode, and whether to enable automation
4. Add tunnels (Standalone: **+ Add**, Companion: **Import → Link to WireGuard profile**)
5. Add WiFi rules under **Options → Rules**
6. MasselGUARD handles the rest from the tray

---

## Build

```bat
BUILD.bat
```

Compiles the project, copies the theme folder, then prompts for DLLs (use the included ones, build from source, download from GitHub, or skip). See [`docs/MasselGUARD.md`](docs/MasselGUARD.md#15-build-and-deployment) for full build details.

---

## Security

Tunnel configs are encrypted with Windows DPAPI (`CurrentUser` scope) — only the Windows account that created the file can read it. The plaintext copy written for the service process during connection is locked to `SYSTEM + Administrators + owning user` from the first byte and deleted within ~200 ms.

Full details in [`docs/MasselGUARD.md`](docs/MasselGUARD.md#12-security-model).
