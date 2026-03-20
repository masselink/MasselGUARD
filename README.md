# WireGuard Client and WiFi Switcher

**Version 0.8 beta** — by [Harold Masselink](https://github.com/masselink/WGClientWifiSwitcher)

A native Windows companion app for WireGuard that automatically connects and disconnects VPN tunnels based on your current WiFi network (SSID). Also works as a lightweight manual tunnel client — no need to open the WireGuard GUI for day-to-day use. Built with C# / WPF on .NET 8.

<img width="818" height="476" alt="Screenshot 2026-03-14 132437" src="https://github.com/user-attachments/assets/00ac1e75-a3ba-47b1-b106-170d15386268" />

## Features

### Automatic switching
- Reacts **instantly** to WiFi changes using `WlanRegisterNotification` (Windows WLAN API). No polling.
- Rules fire on **startup** — the current network is detected and matched immediately when the app launches.
- Each rule maps a WiFi SSID to a WireGuard tunnel. Leave the tunnel blank to disconnect on that network.
- **Default action** when no rule matches: do nothing, disconnect all, or activate a named fallback tunnel.

### Tunnel panel
- Lists all known tunnels with live connection status (● Connected / ○ Disconnected).
- Connect or disconnect any tunnel with one click — no WireGuard GUI needed.
- Status refreshes every 10 seconds and instantly after any manual change.

### WireGuard integration
- **Open WireGuard GUI** button in the status bar launches the official WireGuard app directly.
- **Show Log** button opens a dark-themed log window showing the last 24 hours of WireGuard log history, then tails new entries live using `wireguard /dumplog /tail`. Closes cleanly and stops the tail process.
- **Auto-detects** the WireGuard install directory from the Windows registry (`HKLM\SOFTWARE\WireGuard`). No manual configuration needed in most cases.
- Finds `.conf` and `.conf.dpapi` (DPAPI-encrypted) config files automatically. Falls back to reading `WireGuardTunnel$*` Windows services if files are not accessible.

### System tray
- Minimises to tray on close. Double-click to restore.
- Right-click the tray icon for a **Tunnels** submenu — each tunnel shows a coloured dot (green = connected) and can be toggled with a single click.
- **Disconnect All** at the top of the submenu stops all active tunnels at once.
- Tray icon turns green when any tunnel is active.

### Startup dependency check
- On launch, verifies that `wireguard.exe` is available before showing the main window.
- If a dependency is missing, displays a styled error screen explaining what is needed with a link to the project GitHub page.

### Activity log
- Colour-coded, timestamped log of every WiFi change, tunnel switch, rule match, and error.
- Newest entries appear at the top — no scrolling needed to see the latest activity.

### UI
- Frameless dark WPF window — Consolas font, draggable, resizable with grip.
- Fully themed: custom scrollbars, ComboBoxes, radio buttons, tooltips, and tray context menu all match the dark palette.
- No installer needed. Single `.exe`, config stored in `%APPDATA%\WGClientWifiSwitcher\config.json`.
- UAC elevation requested automatically on launch via app manifest — no runtime prompt.

---

## Requirements

| | |
|---|---|
| OS | Windows 10 or Windows 11 (x64) |
| Runtime | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) — free, ~50 MB, pre-installed on many machines |
| WireGuard | [WireGuard for Windows](https://www.wireguard.com/install/), tunnels already imported |
| Rights | Administrator (requested automatically via UAC on launch) |

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

### WireGuard install directory

The app auto-detects the WireGuard install directory from the registry on first launch. Config files are read from the `Data\Configurations` subfolder of the install directory. Detection order:

1. `HKLM\SOFTWARE\WireGuard` → `InstallDirectory` (written by the WireGuard installer)
2. `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WireGuard` → `InstallLocation`
3. `%ProgramFiles%\WireGuard` and `C:\WireGuard` as hardcoded fallbacks
4. `WireGuardTunnel$*` Windows services (fallback when config files are not readable)

### Rules

Each rule maps a **WiFi SSID** → **WireGuard tunnel**. Rules are evaluated top-to-bottom; the first match wins. Leave the tunnel field blank to disconnect all tunnels on that network.

The tunnel dropdown is populated from the discovered config files. You can also type a name directly — it must match exactly as shown in the WireGuard app.

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

Run once in an elevated PowerShell to register a scheduled task that launches the app at login with admin rights — no UAC prompt each time:

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
├── BUILD.bat                  ← Double-click to build
├── WGClientWifiSwitcher.csproj
├── app.manifest               ← requireAdministrator UAC elevation
├── App.xaml                   ← Global dark theme styles
├── App.xaml.cs                ← App startup, tray icon, tray menu, dependency check
├── MainWindow.xaml            ← Main UI layout
├── MainWindow.xaml.cs         ← All app logic
└── Views/
    ├── RuleDialog.xaml         ← Add / Edit rule dialog
    └── RuleDialog.xaml.cs
```

---

## License

MIT — see [LICENSE](LICENSE) for details.
