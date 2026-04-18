# MasselGUARD — How it works

This document explains the internal operation of MasselGUARD in detail. For a feature overview and quick-start, see the [README](../README.md).

---

## Table of contents

1. [Operating modes](#1-operating-modes)
2. [Startup sequence](#2-startup-sequence)
3. [WiFi monitoring](#3-wifi-monitoring)
4. [Rule evaluation](#4-rule-evaluation)
5. [Connecting a tunnel — Standalone](#5-connecting-a-tunnel--standalone)
6. [Connecting a tunnel — Companion](#6-connecting-a-tunnel--companion)
7. [Disconnecting a tunnel](#7-disconnecting-a-tunnel)
8. [Tunnel groups and categories](#8-tunnel-groups-and-categories)
9. [Quick Connect](#9-quick-connect)
10. [Open network protection](#10-open-network-protection)
11. [Configuration and storage](#11-configuration-and-storage)
12. [Security model](#12-security-model)
13. [Theme system](#13-theme-system)
14. [Logging](#14-logging)
15. [Build and deployment](#15-build-and-deployment)
16. [Troubleshooting](#16-troubleshooting)

---

## 1. Operating modes

MasselGUARD runs in one of three modes, selected in the setup wizard or Settings → General.

### Standalone

MasselGUARD owns the tunnel lifecycle entirely. No WireGuard application is required. Tunnel configs are created, encrypted, and stored inside the app. Connectivity is provided by `tunnel.dll` + `wireguard.dll` (wireguard-NT) placed next to the executable.

### Companion

MasselGUARD automates the official WireGuard for Windows application. It does not store or modify any tunnel configs — it only starts and stops the `WireGuardTunnel$<name>` Windows services that WireGuard creates. You link existing WireGuard profiles from the Import dialog.

### Mixed

Both modes active simultaneously. Local (Standalone) tunnels and linked WireGuard profiles coexist in the same tunnel list and can all be automated.

---

## 2. Startup sequence

```
Program.Main()
  └─ Mutex check (single instance — second launch brings first to front)
  └─ UAC elevation check (exits with prompt if not Administrator)
  └─ Application.Run(MainWindow)
        │
        ▼
MainWindow.Loaded
  ├─ ShowRightTab("Log")          initialise right panel
  ├─ Log(AppStarted)
  ├─ LoadConfig()                  read %APPDATA%\MasselGUARD\config.json
  ├─ ApplyManualMode()             hide Rules/Default tabs if manual
  ├─ ApplyLocalTunnelMode()        show/hide Add/Edit based on mode
  ├─ SetupTimer()                  1-second status poll
  ├─ _startupComplete = true       suppress discovery log on future refreshes
  ├─ RegisterWifiEvents()          WlanRegisterNotification
  ├─ UpdateThemeToggleIcon()
  ├─ SyncAutoTheme()               apply dark/light based on system if AutoTheme=true
  └─ (optional) ShowWizard()       if no config.json existed
```

---

## 3. WiFi monitoring

MasselGUARD uses the native `wlanapi.dll` rather than WMI or process spawning, so it reacts in real time without polling.

```
WlanRegisterNotification()
  └─ callback fires on ACM codes:
       9  = connected
       10 = disconnected
       21 = network roaming

OnWifiChanged()
  ├─ GetCurrentSsid()    WlanQueryInterface(WLAN_INTF_OPCODE_CURRENT_CONNECTION)
  ├─ Update status bar
  ├─ Log WiFi: <SSID> (🔒 secured / ⚠ open)
  └─ ApplyRules(ssid)    (skipped in manual mode)
```

`GetCurrentSsid()` reads `WLAN_CONNECTION_ATTRIBUTES` directly from memory:

| Offset | Field | Used for |
|---|---|---|
| 520 | `uSSIDLength` | SSID byte length |
| 524 | `ucSSID[32]` | SSID bytes (UTF-8) |
| 580 | `bSecurityEnabled` | 0 = open network |

A 1-second `DispatcherTimer` also calls `UpdateStatusDisplay()` to keep the active tunnel label and tray icon in sync between WiFi events.

---

## 4. Rule evaluation

`ApplyRules(ssid)` runs every time the WiFi network changes (and never in manual mode). The priority order is fixed:

```
1. Open network protection
   └─ Is the network open (no password)?
   └─ Is OpenWifiTunnel configured?
   └─ Yes → SwitchTo(OpenWifiTunnel)  STOP — rules not evaluated

2. SSID rules
   └─ Does any rule match the current SSID exactly?
   └─ Yes, tunnel set   → SwitchTo(rule.Tunnel)   STOP
   └─ Yes, tunnel empty → DisconnectAll()          STOP

3. Default action
   └─ "none"       → do nothing
   └─ "disconnect" → DisconnectAll()
   └─ "activate"   → SwitchTo(DefaultTunnel)
```

`SwitchTo(target)` first stops any active tunnel that is not `target`, then starts `target`. If `target` is already running it logs `LogAlreadyActive` and returns immediately.

---

## 5. Connecting a tunnel — Standalone

Standalone tunnels use the `tunnel.dll` / `wireguard.dll` embeddable service model. The full flow:

```
StartTunnel(name)
  ├─ ValidateDlls()         check tunnel.dll and wireguard.dll exist + right size
  ├─ ConfPath(name)         %AppData%\MasselGUARD\tunnels\<name>.conf.dpapi
  │   └─ (migration: if missing, recover from stored.Path or inline config)
  │
  ├─ DpapiDecrypt(confPath) → plaintext WireGuard config
  ├─ LogTunnelDebugInfo()   [Debug] logs Address, DNS, Endpoint, AllowedIPs, PublicKey
  ├─ WriteSecure(SvcConfPath)
  │   ├─ File.Create()                  empty file, inherits parent ACL
  │   ├─ SetAccessControl(fileSec)      lock to SYSTEM + Admins + user
  │   └─ StreamWriter.Write(plaintext)  write config bytes
  │
  ├─ TunnelDll.Connect(name, svcConf, logCallback)
  │   ├─ EnsureStopped()      remove any stale WireGuardTunnel$<name> SCM entry
  │   ├─ CreateService()      WireGuardTunnel$<name>, runs MasselGUARD.exe /service <conf>
  │   ├─ ChangeServiceConfig2(SERVICE_SID_TYPE_UNRESTRICTED)
  │   └─ sc.Start()
  │         │
  │         ▼  (SCM spawns child)
  │   MasselGUARD.exe /service <conf>   ← LocalSystem
  │     ├─ SetDllDirectory(exeDir)
  │     ├─ SetCurrentDirectory(exeDir)
  │     └─ WireGuardTunnelService(conf) ← tunnel.dll
  │           └─ installs wireguard-NT kernel driver
  │           └─ brings tunnel up in kernel space
  │           └─ process exits (~50–100 ms)
  │
  └─ Delete SvcConfPath immediately after Connect returns
```

The plaintext temp file lives for under 200 ms. The kernel driver keeps the tunnel active after the service process exits — `StopTunnel` tells the driver to tear it down.

---

## 6. Connecting a tunnel — Companion

WireGuard Companion tunnels use the existing `WireGuardTunnel$<name>` service that WireGuard for Windows created when you imported the profile.

```
StartTunnel(name) — Companion path
  ├─ EnsureManagerRunning()   start WireGuardManager if stopped
  ├─ ServiceController(SvcName(name)).Start()
  ├─ WaitForStatus(Running, 15 s)
  │
  └─ Fallback if ServiceController fails:
       └─ FindConfPath(name)   search WireGuard's profile directories
       └─ wireguard.exe /installtunnelservice "<conf>"
       └─ Sleep(1500 ms)
```

---

## 7. Disconnecting a tunnel

### Standalone disconnect

```
StopTunnel(name) — local path
  ├─ TunnelDll.Disconnect(name)
  │   └─ sc.Stop()  + sc.Delete()  (WireGuardTunnel$<name>)
  │   └─ wireguard-NT driver tears down kernel tunnel
  └─ Delete SvcConfPath (safety net — should already be gone)
```

### Companion disconnect

```
StopTunnel(name) — WireGuard path
  ├─ ServiceController(SvcName(name)).Stop()
  └─ WaitForStatus(Stopped, 15 s)
```

`DisconnectAll()` iterates `GetActiveTunnelNames()` and calls `StopTunnel` for each.

---

## 8. Tunnel groups and categories

Each tunnel can be assigned to a named group (Work, Personal, Travel, or any custom group). Groups are managed in **Settings → General → Tunnel Groups**.

The tunnel list shows a tab strip at the top:

| Tab | Shows |
|---|---|
| All | Every tunnel regardless of group |
| Work / Personal / Travel / … | Only tunnels in that group |
| Uncategorized | Tunnels not assigned to any group |

Tabs are built by `RebuildTunnelGroups()`. It buckets `_tunnels` by group, builds tab buttons, and sets `TunnelsListView.ItemsSource` to the currently active tab's slice. Selection is preserved by name across rebuilds so that Edit and Delete work on the first click after any save operation.

For **WireGuard-linked tunnels** (Companion mode), the Edit button opens a metadata-only dialog (`TunnelMetadataDialog`) where group and notes can be edited. The `.conf` file itself is owned by WireGuard and is not modified.

---

## 9. Quick Connect

Quick Connect opens any `.conf` or `.conf.dpapi` from disk and connects immediately, without permanently importing the tunnel.

```
QuickConnect_Click()
  ├─ OpenFileDialog (*.conf, *.conf.dpapi)
  ├─ ReadAllBytes + DpapiDecrypt if needed
  ├─ Store in _quickConnectConfig (in-memory only)
  ├─ StartTunnel via the local path (writes temp file, starts service)
  └─ SyncQuickConnectEntry()
       └─ Insert "⚡ <filename>" at top of _tunnels
       └─ Visible in tunnel list; Disconnect button tears it down
```

The config is never written to `%APPDATA%\MasselGUARD\tunnels\`. It exists only in memory and the short-lived temp file.

---

## 10. Open network protection

MasselGUARD detects open (passwordless) WiFi by reading `WLAN_SECURITY_ATTRIBUTES.bSecurityEnabled` at offset 580 inside `WLAN_CONNECTION_ATTRIBUTES`. A value of `0` means no security — no WPA/WPA2/WPA3 negotiation.

When this condition is detected, the configured protection tunnel is activated **before** any SSID rule or default action is evaluated. This guarantees that traffic is never sent unencrypted on an open hotspot, even if there is no SSID rule for that network.

Configure the tunnel under **Options → Open Network** in the main window, or set it to "— none —" to disable.

---

## 11. Configuration and storage

### config.json

Stored at `%APPDATA%\MasselGUARD\config.json`. Written atomically on every save.

```json
{
  "Rules": [
    { "Ssid": "HomeWifi", "Tunnel": "home" },
    { "Ssid": "CafeWifi", "Tunnel": "" }
  ],
  "Tunnels": [
    {
      "Name":   "home",
      "Source": "local",
      "Path":   "C:\\...\\tunnels\\home.conf.dpapi",
      "Group":  "Personal",
      "Notes":  "Home router tunnel"
    }
  ],
  "TunnelGroups": [
    { "Name": "Work",     "IsExpanded": true, "Color": "" },
    { "Name": "Personal", "IsExpanded": true, "Color": "" },
    { "Name": "Travel",   "IsExpanded": true, "Color": "" }
  ],
  "DefaultAction":         "activate",
  "DefaultTunnel":         "home",
  "OpenWifiTunnel":        "home",
  "Mode":                  "Standalone",
  "ManualMode":            false,
  "Language":              "en",
  "ActiveTheme":           "default-dark",
  "ActiveDarkTheme":       "default-dark",
  "ActiveLightTheme":      "default-light",
  "AutoTheme":             false,
  "ShowTrayPopupOnSwitch": true,
  "LogLevelSetting":       "normal"
}
```

### Tunnel configs

| Path | Format | Scope |
|---|---|---|
| `<ExeDir>\tunnels\<name>.conf.dpapi` | DPAPI-encrypted WireGuard config | CurrentUser only |
| `<ExeDir>\tunnels\temp\<name>.conf` | Plaintext copy for service process | Deleted within ~200 ms |

### Directory layout

```
<ExeDir>\
├── MasselGUARD.exe
├── tunnel.dll                  (Standalone / Mixed)
├── wireguard.dll               (Standalone / Mixed)
├── lang\
│   ├── en.json
│   ├── nl.json
│   ├── de.json
│   ├── fr.json
│   └── es.json
├── theme\
│   ├── default-dark\
│   │   └── theme.json
│   ├── default-light\
│   │   └── theme.json
│   ├── grey-dark\
│   │   └── theme.json
│   └── grey-light\
│       └── theme.json
└── tunnels\
    ├── home.conf.dpapi
    └── temp\                   (always empty when idle)
```

---

## 12. Security model

### DPAPI encryption

Tunnel configs are stored as `.conf.dpapi` files encrypted with Windows DPAPI using `DataProtectionScope.CurrentUser`. The decryption key is derived from the user's Windows login credentials by the OS kernel — MasselGUARD never stores or handles keys.

Consequences:
- A file encrypted on one machine cannot be decrypted on another
- A file encrypted by one Windows user cannot be decrypted by another user on the same machine
- Domain re-join or profile migration can invalidate the key (migration recovery path exists)

### Atomic temp file

When a Standalone tunnel connects, a plaintext copy is written to `tunnels\temp\` for the `LocalSystem` service child process. The write is atomic with respect to ACL:

```csharp
File.Create(path).Dispose();                     // 1. create empty — inherits parent ACL
new FileInfo(path).SetAccessControl(fileSec);    // 2. apply restrictive ACL before first byte
using var sw = new StreamWriter(new FileStream(  // 3. write plaintext under correct ACL
    path, FileMode.Open, FileAccess.Write,
    FileShare.None, 4096, FileOptions.WriteThrough));
```

There is no moment when the file exists with loose permissions. ACL: `SYSTEM + Administrators + owning user` only. The file is deleted immediately after `TunnelDll.Connect` returns — typically within 200 ms.

### Service name sanitisation

`SafeName(tunnel)` replaces spaces, backslashes, and all `Path.GetInvalidFileNameChars()` with underscores before the tunnel name is used as an SCM service name or filename. The original display name is preserved in `config.json` and the UI.

### ACL summary

| Location | ACL |
|---|---|
| `tunnels\` | SYSTEM + Administrators: Full Control; Authenticated Users: list/traverse (not inherited to files) |
| `tunnels\<n>.conf.dpapi` | SYSTEM + Administrators + owning user: Full Control; inheritance blocked |
| `tunnels\temp\` | SYSTEM + Administrators + current user: Full Control; inheritance blocked |
| `tunnels\temp\<n>.conf` | Same as temp dir; deleted within ~200 ms |

---

## 13. Theme system

Themes live in `theme/<folder>/theme.json`. MasselGUARD ships with four:

| Folder | Type | Corner radius |
|---|---|---|
| `default-dark` | dark | 6 px |
| `default-light` | light | 6 px |
| `grey-dark` | dark | 0 px (sharp) |
| `grey-light` | light | 0 px (sharp) |
| `colorblind-dark` | dark | 6 px |
| `colorblind-light` | light | 6 px |

### theme.json keys

| Key | Type | Effect |
|---|---|---|
| `name` | string | Display name in the theme picker |
| `type` | `"dark"` / `"light"` | Used for auto system-theme switching |
| `cornerRadius` | int | All windows and cards |
| `windowOpacity` | 0.0–1.0 | Whole-window transparency |
| `titleBarHeight` | int | Title bar row height in px |
| `showTitleBarIcon` | bool | Show/hide the logo/shield group |
| `showTitleBarAppName` | bool | Show/hide the app name text |
| `showResizeGrip` | bool | Show/hide the bottom-right resize handle |
| `showStatusBar` | bool | Show/hide the status bar entirely |
| `statusBarHeight` | int | Status bar row height in px |
| `showStatusWifi` | bool | Show/hide the WiFi label |
| `showStatusTunnel` | bool | Show/hide the active tunnel label |
| `colorTrayBg` | hex | Tray menu background (empty = inherit `colorSurface`) |
| `colorTrayHover` | hex | Tray item hover (empty = inherit `colorBorder`) |
| `colorTrayText` | hex | Tray item text (empty = inherit `colorTextPrimary`) |
| `backgroundImage` | filename | Image file in the theme folder |
| `appIcon` | filename | Custom tray + title bar icon |
| `logo` | filename | Custom logo replacing the built-in shield |
| `variables` | object | Free-form `Var.<key>` dynamic resources |

### Hot-swap

`ThemeManager.Instance.Load(folder)` applies all theme values directly into `Application.Current.Resources`. Every `{DynamicResource ...}` binding in the app updates immediately — no restart needed.

### Auto-switching

When `AutoTheme = true`, a 5-second polling timer reads `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme` and loads `ActiveDarkTheme` or `ActiveLightTheme` accordingly when the system theme changes.

### Adding a custom theme

1. Create `theme/<myfolder>/theme.json` next to the exe
2. Set at minimum: `name`, `type`, and the colour keys you want to override
3. Restart or toggle any theme in Settings → Appearance — the new theme appears in the picker immediately

---

## 14. Logging

### Log levels

| Level | Setting | What appears |
|---|---|---|
| Normal | `"normal"` | ✅ OK messages + ⚠ warnings only |
| Info | `"info"` | + WiFi changes, triggers, mode changes, settings |
| Verbose | `"verbose"` | + all non-debug messages |
| Debug | `"debug"` | Everything including `[DBG]` entries |

Change in **Settings → Advanced → Log level**.

### Info-level events (logged at ≥ Info)

- WiFi network change: SSID + security status (🔒 / ⚠ open)
- Auto-switch trigger reason (rule match / default action / open network)
- App mode change, manual mode toggle
- Language change, theme change
- Log level change

### Debug-level events (logged at Debug only, prefixed `[DBG]`)

| Event | Detail |
|---|---|
| Startup | OS version, .NET runtime, platform, domain\user |
| Before connect | Interface address, DNS, endpoint, AllowedIPs, public key prefix, MTU |
| Service conf path | Full temp file path |
| WireGuard service | Service name + exe path |
| Connect timing | `Connected in N ms` |
| Disconnect timing | `Disconnected in N ms` / `Service stopped in N ms` |
| Conflicting tunnel | Name of tunnel stopped before switching |

### Export

Click **Export Log** (visible when Activity Log tab is active) to save the current log to a UTF-8 `.txt` file.

---

## 15. Build and deployment

### Build script

```bat
BUILD.bat
```

Steps:
1. Verify .NET 10 SDK
2. `dotnet publish` → `dist\`
3. Copy `theme\` folder → `dist\theme\`
4. DLL choice:
   - `[1]` Copy provided `tunnel.dll` + `wireguard.dll` from project root (recommended)
   - `[2]` Build from source (requires Go + gcc)
   - `[3]` Download from GitHub
   - `[4]` Skip — add manually

### Runtime requirements

| | |
|---|---|
| OS | Windows 10 / 11 x64 |
| Runtime | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Elevation | Administrator (UAC prompt on launch) |
| Standalone / Mixed | `tunnel.dll` + `wireguard.dll` next to exe |
| Companion / Mixed | [WireGuard for Windows](https://wireguard.com/install) installed |

---

## 16. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| "Cannot find file" in Event Viewer | Wrong `wireguard.dll` (WireGuard-app version, not wireguard-NT) | Use the ~1.3 MB wireguard-NT DLL from [download.wireguard.com/wireguard-nt](https://download.wireguard.com/wireguard-nt/) |
| Event Viewer termination error even when tunnel works | wireguard-NT service process exits immediately after kernel driver is up — SCM logs a false positive | Ignore; check tunnel list status instead |
| Config file not found on connect | Tunnel created with an older version; migration runs on first connect attempt | If migration fails, delete and re-add the tunnel |
| Quick Connect fails | DLLs missing or invalid `.conf` syntax | Check DLL status in Settings → Advanced; validate the `.conf` file |
| Orphaned services at startup warning | App was killed while a tunnel was active | Settings → Advanced → Possible Orphaned Tunnel Services |
| Selection lost after editing | Should be fixed in v2.3.0 — selection is restored by name after every rebuild | Update to latest build |
| Edit button stays disabled | Click the tunnel row directly in the list (not the Connect/Disconnect button) | Row must be selected first |
| Theme not appearing in picker | `theme.json` missing or malformed | Ensure `type` field is `"dark"` or `"light"` and JSON is valid |
