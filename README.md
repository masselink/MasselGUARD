# WireGuard Client and WiFi Switcher

**Version 1.1.2** — by [Harold Masselink](https://github.com/masselink/WGClientWifiSwitcher)

---

<img width="818" height="476" alt="WG Client screenshot" src="https://github.com/user-attachments/assets/5f0f4a2e-7fba-4039-9fc4-6055c1bab6c7" />

---

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
| **Install / Uninstall** | Built-in installer — copies to Program Files, creates Start Menu shortcut, optional auto-start. |
| **Dark theme** | Frameless WPF window, Consolas font, fully themed controls including install dialogs. |
| **Single `.exe`** | Framework-dependent publish — small exe + `lang\` folder. No installer needed to run. |

---

## Requirements

| | |
|---|---|
| OS | Windows 10 or Windows 11 (x64) |
| Runtime | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) — free, ~60 MB, LTS until November 2028 |
| WireGuard | [WireGuard for Windows](https://www.wireguard.com/install/), with tunnels already imported |
| Rights | Administrator — requested automatically via UAC on launch |

---

## Quick start (portable)

1. Install [WireGuard for Windows](https://www.wireguard.com/install/) and import your tunnels
2. Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
3. Run `WGClientWifiSwitcher.exe` directly — no installation required
4. Click **＋ Add Rule** to map a WiFi SSID to a tunnel
5. The app minimises to the system tray — the icon turns green when a tunnel is active

---

## Installation

The app has a built-in installer accessible from the **⬇ Install** button in the status bar.

### Install steps

1. Click **⬇ Install** in the status bar
2. Select a parent folder (defaults to `%ProgramFiles%`) — the app installs into `<folder>\WG Client and WiFi Switcher\`
3. A Start Menu shortcut is created at `Programs\WG Client and WiFi Switcher\WireGuard Client and WiFi Switcher.lnk` with the app icon
4. You are asked whether to **start automatically at Windows login** — if yes, a scheduled task is registered with `RunLevel Highest` so the app launches elevated without a UAC prompt every time
5. You are asked whether to **delete the original files** from the folder you ran the app from
6. The current instance closes and the installed copy launches automatically

### Uninstall steps

Click **⬆ Uninstall** in the status bar. The button only shows when the app detects an installed copy via `config.json` or the registry.

**Step 1 — Confirm**
A confirmation dialog asks whether you want to remove the application. Cancelling at this point leaves everything intact.

**Step 2 — Keep settings?**
You are asked whether to keep your configuration and rules for a possible future reinstall.

| Answer | What happens |
|---|---|
| **Yes** | `%APPDATA%\WGClientWifiSwitcher\config.json` is kept. All rules, the default action, language preference, and WireGuard install path are preserved. |
| **No** | The entire `%APPDATA%\WGClientWifiSwitcher\` folder is deleted, including `config.json` with all rules and settings. |

**Step 3 — Automatic removal**
The following are always removed without asking:

- The scheduled auto-start task (`WGClientWifiSwitcher`) from Task Scheduler
- The Start Menu shortcut folder (`Programs\WG Client and WiFi Switcher\`)
- The registry key `HKLM\SOFTWARE\WGClientWifiSwitcher`
- The `InstalledPath` field in `config.json` (if kept)
- The install folder (`WG Client and WiFi Switcher\` inside Program Files or wherever it was installed)

**Step 4 — Remove current running files? (conditional)**
This question only appears when the app is running from a **different location** than the install folder — for example when you downloaded a new version and ran it from your Downloads folder while the old version was installed elsewhere.

| Answer | What happens |
|---|---|
| **Yes** | The folder the current exe is running from is deleted after the application closes (via a deferred `cmd /c rd` command). |
| **No** | The current running location is left untouched. |

If the app is already running from the install folder this question is skipped — that folder is already handled in step 3.

**Step 5 — Close**
The application logs "Uninstall complete. Closing application." and exits after a brief pause so the message is visible.

### Install state

The installer stores the install location in two places:

- **`%APPDATA%\WGClientWifiSwitcher\config.json`** — `InstalledPath` field (primary)
- **`HKLM\SOFTWARE\WGClientWifiSwitcher`** — `InstallPath` registry value (fallback)

The app checks both on startup to determine whether to show the Install or Uninstall button. If you move the installed exe manually, clear `InstalledPath` from `config.json` and the app will treat itself as not installed.

### Auto-start at login (manual)

If you prefer to register the scheduled task manually:

```powershell
$exe     = "C:\Program Files\WG Client and WiFi Switcher\WGClientWifiSwitcher.exe"
$action  = New-ScheduledTaskAction -Execute $exe
$trigger = New-ScheduledTaskTrigger -AtLogOn
$prin    = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest
Register-ScheduledTask -TaskName "WGClientWifiSwitcher" `
    -Action $action -Trigger $trigger -Principal $prin
```

---

## Configuration

Config file: `%APPDATA%\WGClientWifiSwitcher\config.json`

```json
{
  "Rules": [
    { "Ssid": "HomeNetwork", "Tunnel": "HomeVPN" },
    { "Ssid": "CafeWiFi",    "Tunnel": "" }
  ],
  "DefaultAction": "activate",
  "DefaultTunnel": "HomeVPN",
  "InstallDirectory": "C:\\Program Files\\WireGuard",
  "Language": "en",
  "InstalledPath": "C:\\Program Files\\WG Client and WiFi Switcher"
}
```

| Field | Description |
|---|---|
| `Rules` | List of SSID → tunnel mappings. Empty tunnel = disconnect all. |
| `DefaultAction` | `"none"` / `"disconnect"` / `"activate"` |
| `DefaultTunnel` | Tunnel name used when `DefaultAction` is `"activate"` |
| `InstallDirectory` | WireGuard install dir — auto-detected, rarely needs editing |
| `Language` | Language code matching a file in `lang\` (e.g. `"en"`, `"nl"`) |
| `InstalledPath` | Set by the installer. Remove or set to `null` to reset install state. |

### WireGuard install directory detection order

1. `HKLM\SOFTWARE\WireGuard` → `InstallDirectory`
2. `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WireGuard` → `InstallLocation`
3. `%ProgramFiles%\WireGuard` and `C:\WireGuard`
4. Installed `WireGuardTunnel$*` Windows services (fallback when conf files are not readable)

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

1. Copy `lang\en.json` to e.g. `lang\de.json`
2. Set `"_code": "de"` and `"_language": "Deutsch"`
3. Translate every value
4. The new language appears in the picker automatically — no recompile

```json
{
  "_language": "Deutsch",
  "_code": "de",
  "AppTitle": "WireGuard Client und WiFi Switcher v1.1.2",
  "BtnAddRule": "\uFF0B  Regel hinzufügen"
}
```

---

## Build

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows 10/11 x64

### Steps

Double-click `BUILD.bat`, or run in a terminal:

```
dotnet publish WGClientWifiSwitcher.csproj -c Release -o dist
```

Output:
```
dist\
├── WGClientWifiSwitcher.exe     ← single file (~500 KB)
└── lang\
    ├── en.json
    └── nl.json
```

---

## Activity log

On startup the activity log shows (bottom to top = oldest to newest):

```
HH:MM:SS  Started from: C:\...\WGClientWifiSwitcher.exe
HH:MM:SS  Application started.
HH:MM:SS  Startup WiFi: HomeNetwork — applying rules.
```

App-generated messages are fully translated when you switch language. External messages from WireGuard or the OS are shown as-is.

---

## Project structure

```
WGClientWifiSwitcher/
├── BUILD.bat                   ← Double-click to publish
├── WGClientWifiSwitcher.csproj ← .NET 10, single-file publish
├── app.manifest                ← requireAdministrator UAC elevation
├── App.xaml                    ← Global dark theme styles
├── App.xaml.cs                 ← Startup, tray icon, single-instance, dependency check
├── Lang.cs                     ← Language manager — loads lang/*.json, live switching
├── MainWindow.xaml             ← UI layout (all strings bound to Lang.Instance)
├── MainWindow.xaml.cs          ← Application logic, install/uninstall
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
