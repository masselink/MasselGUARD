# WireGuard Client and WiFi Switcher

**Version 1.0** — by [Harold Masselink](https://github.com/masselink/WGClientWifiSwitcher)

A native Windows companion app for WireGuard that automatically switches VPN tunnels based on your current WiFi network. Built with C# / WPF on .NET 10 (LTS) — no Electron, no bloat.

---

<img width="818" height="476" alt="Screenshot" src="https://github.com/user-attachments/assets/5f0f4a2e-7fba-4039-9fc4-6055c1bab6c7" />

## What it does

Define rules that map WiFi networks to WireGuard tunnels. When you connect to a known network the right tunnel activates automatically. When you move to an unknown network a configurable default action kicks in. Everything happens instantly using the Windows WLAN notification API — no polling.

The app also works as a lightweight manual tunnel client. Connect and disconnect tunnels directly from the main window or the system tray without opening the WireGuard GUI.

---

## Features

| | |
|---|---|
| **Auto-switching** | Instant reaction to WiFi changes via `WlanRegisterNotification`. Rules applied on startup too. |
| **Rule manager** | Map any SSID to any tunnel. Leave tunnel blank to disconnect on that network. |
| **Default action** | Do nothing / disconnect all / activate a fallback tunnel when no rule matches. |
| **Tunnel panel** | Live status for every tunnel. Connect or disconnect with one click. |
| **System tray** | Coloured dot per tunnel, click to toggle. Icon turns green when connected. |
| **WireGuard log** | Opens a live-tailing log window (last 24h + streaming via `/dumplog /tail`). |
| **Multi-language** | Ships with English and Dutch. Add any language by dropping a JSON file in `lang\`. |
| **Auto-detection** | Finds WireGuard via registry. Reads `.conf` and `.conf.dpapi` files automatically. |
| **Single instance** | Named mutex prevents duplicate launches. Shows a styled dialog if already running. |
| **Dependency check** | Verifies `wireguard.exe` is present before opening. Styled error screen with GitHub link if not. |
| **Dark theme** | Frameless WPF window, Consolas font, fully themed controls. |
| **No installer** | Single `.exe` + `lang\` folder. Config at `%APPDATA%\WGClientWifiSwitcher\config.json`. |

---

## Requirements

| | |
|---|---|
| OS | Windows 10 or Windows 11 (x64) |
| Runtime | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) — free, ~60 MB, LTS until November 2028 |
| WireGuard | [WireGuard for Windows](https://www.wireguard.com/install/), with tunnels already imported |
| Rights | Administrator — requested automatically via UAC on launch |

---

## Build

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows 10/11 x64

### Steps

Double-click `BUILD.bat`, or run in a terminal:

```
dotnet build WGClientWifiSwitcher.csproj -c Release -o dist
```

Output: `dist\WGClientWifiSwitcher.exe` and `dist\lang\`

---

## Quick start

1. Install [WireGuard for Windows](https://www.wireguard.com/install/) and import your tunnels
2. Build or download the app
3. Run `WGClientWifiSwitcher.exe` — it will auto-detect your WireGuard installation
4. Click **＋ Add Rule** to map a WiFi SSID to a tunnel
5. The app minimises to the system tray — the icon turns green when a tunnel is active

---

## Configuration

Config file: `%APPDATA%\WGClientWifiSwitcher\config.json`

### WireGuard install directory

Auto-detected from the registry in this order:

1. `HKLM\SOFTWARE\WireGuard` → `InstallDirectory`
2. `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WireGuard` → `InstallLocation`
3. `%ProgramFiles%\WireGuard` and `C:\WireGuard`
4. Installed `WireGuardTunnel$*` Windows services (fallback when conf files are not readable)

Config files are read from `<InstallDirectory>\Data\Configurations`.

### Rules

Each rule maps a **WiFi SSID** → **WireGuard tunnel**. Rules are evaluated top-to-bottom; the first match wins. Leave the tunnel field blank to disconnect all tunnels on that network.

### Default action

| Setting | What happens when no rule matches |
|---|---|
| Do nothing | Tunnels stay as-is |
| Disconnect all | All active tunnels are stopped |
| Activate tunnel | A named fallback tunnel is started |

---

## Multi-language support

Language files live in `lang\` next to the executable. The picker in the title bar lets you switch without restarting. The activity log re-renders in the new language live.

**To add a language:**

1. Copy `lang\en.json` to `lang\de.json` (or any ISO code)
2. Set `"_code": "de"` and `"_language": "Deutsch"`
3. Translate every value
4. The new language appears in the picker automatically — no recompile

```json
{
  "_language": "Deutsch",
  "_code": "de",
  "AppTitle": "WireGuard Client und WiFi Switcher v1.0",
  "BtnAddRule": "\uFF0B  Regel hinzufügen"
}
```

---

## Auto-start at login (optional)

The app requires administrator rights. Register a scheduled task to avoid the UAC prompt every time:

```powershell
$exe     = "C:\path\to\dist\WGClientWifiSwitcher.exe"
$action  = New-ScheduledTaskAction -Execute $exe
$trigger = New-ScheduledTaskTrigger -AtLogOn
$prin    = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest
Register-ScheduledTask -TaskName "WGClientWifiSwitcher" `
    -Action $action -Trigger $trigger -Principal $prin
```

---

## Project structure

```
WGClientWifiSwitcher/
├── BUILD.bat                   ← Double-click to build
├── WGClientWifiSwitcher.csproj ← .NET 10, WPF + WinForms
├── app.manifest                ← requireAdministrator UAC elevation
├── App.xaml                    ← Global dark theme styles
├── App.xaml.cs                 ← Startup, tray icon, single-instance, dependency check
├── Lang.cs                     ← Language manager — loads lang/*.json, live switching
├── MainWindow.xaml             ← UI layout (all strings bound to Lang.Instance)
├── MainWindow.xaml.cs          ← Application logic
├── lang/
│   ├── en.json                 ← English
│   └── nl.json                 ← Nederlands
└── Views/
    ├── RuleDialog.xaml         ← Add / Edit rule dialog
    └── RuleDialog.xaml.cs
```

---

## License

MIT — see [LICENSE](LICENSE) for details.
