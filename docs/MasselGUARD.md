# MasselGUARD — How it works

Technical reference for v2.5.0. For end-user instructions see [`MANUAL.md`](MANUAL.md).

---

## Contents

1. [Operating modes](#1-operating-modes)
2. [Startup sequence](#2-startup-sequence)
3. [WiFi monitoring](#3-wifi-monitoring)
4. [Rule evaluation](#4-rule-evaluation)
5. [Connecting a tunnel — Standalone](#5-connecting-a-tunnel--standalone)
6. [Connecting a tunnel — Companion](#6-connecting-a-tunnel--companion)
7. [Disconnecting a tunnel](#7-disconnecting-a-tunnel)
8. [Pre/post scripts](#8-prepost-scripts)
9. [Tunnel groups and categories](#9-tunnel-groups-and-categories)
10. [Quick Connect](#10-quick-connect)
11. [Open network protection](#11-open-network-protection)
12. [Configuration and storage](#12-configuration-and-storage)
13. [Security model](#13-security-model)
14. [Theme system](#14-theme-system)
15. [Logging](#15-logging)
16. [Settings panel](#16-settings-panel)
17. [Import / Export settings](#17-import--export-settings)
18. [Build and deployment](#18-build-and-deployment)
19. [Troubleshooting](#19-troubleshooting)

---

## 1. Operating modes

MasselGUARD runs in one of three modes, selected in the setup wizard or Settings → General.

**Standalone** — MasselGUARD owns the tunnel lifecycle entirely. No WireGuard application is required. Tunnel configs are created, encrypted, and stored inside the app. Connectivity is provided by `tunnel.dll` + `wireguard.dll` (wireguard-NT) placed next to the executable.

**Companion** — MasselGUARD automates the official WireGuard for Windows application. It does not store or modify tunnel configs — it only starts and stops the `WireGuardTunnel$<n>` Windows services that WireGuard creates. You link existing WireGuard profiles from the Import dialog.

**Mixed** — Both modes active simultaneously. Local (Standalone) tunnels and linked WireGuard profiles coexist in the same tunnel list and can all be automated.

---

## 2. Startup sequence

```
Program.Main()
  └─ Mutex check (single instance guard — see §2a)
  └─ UAC elevation check
  └─ Application.Run(MainWindow)
        │
        ▼
MainWindow.Loaded
  ├─ LoadConfig()              %APPDATA%\MasselGUARD\config.json
  ├─ ApplyManualMode()
  ├─ ApplyLocalTunnelMode()
  ├─ SetupTimer()              1-second status poll
  ├─ _startupComplete = true
  ├─ RegisterWifiEvents()      WlanRegisterNotification
  ├─ UpdateThemeToggleIcon()
  ├─ SyncAutoTheme()
  └─ (optional) ShowWizard()   if no config.json existed
```

### 2a — Single-instance guard

A named mutex (`Global\MasselGUARD_SingleInstance`) prevents multiple instances. If the mutex is already held:

1. Check whether a real MasselGUARD process (by process name, excluding self) is running
2. If no real process found (orphaned mutex — e.g. after install to new location), retry up to 4 × 500 ms
3. Only show the "already running" dialog if mutex is held AND a real process exists
4. If the mutex is acquired after retry, continue normally

This prevents the false-positive "already running" message when relaunching after installation.

---

## 3. WiFi monitoring

MasselGUARD uses `wlanapi.dll` directly rather than WMI or process spawning.

```
WlanRegisterNotification()
  └─ callback fires on ACM codes:
       9  = connected
       10 = disconnected
       21 = roaming

OnWifiChanged()
  ├─ GetCurrentSsid()    WlanQueryInterface(WLAN_INTF_OPCODE_CURRENT_CONNECTION)
  ├─ Update status bar
  ├─ Log WiFi: <SSID> (secured / open)
  └─ ApplyRules(ssid)    (skipped in disable WiFi rules)
```

`GetCurrentSsid()` reads `WLAN_CONNECTION_ATTRIBUTES` directly from memory:

| Offset | Field | Used for |
|---|---|---|
| 520 | `uSSIDLength` | SSID byte length |
| 524 | `ucSSID[32]` | SSID bytes (UTF-8) |
| 580 | `bSecurityEnabled` | 0 = open network |

A 1-second `DispatcherTimer` also calls `UpdateStatusDisplay()` to keep the active tunnel label and tray icon in sync.

---

## 4. Rule evaluation

`ApplyRules(ssid)` runs every time the WiFi network changes:

```
1. Open network protection
   └─ Is bSecurityEnabled = 0?
   └─ Is OpenWifiTunnel configured?
   └─ Yes → SwitchTo(OpenWifiTunnel)  STOP

2. SSID rules
   └─ Does any rule match the SSID exactly (case-insensitive)?
   └─ Yes, tunnel set   → SwitchTo(rule.Tunnel)   STOP
   └─ Yes, tunnel empty → DisconnectAll()          STOP

3. Default action
   └─ "none"       → do nothing
   └─ "disconnect" → DisconnectAll()
   └─ "activate"   → SwitchTo(DefaultTunnel)
```

`SwitchTo(target)` stops any active tunnel that is not `target`, then starts `target`. If `target` is already running it logs `AlreadyActive` and returns.

---

## 5. Connecting a tunnel — Standalone

```
StartTunnel(name)
  ├─ RunTunnelScript(PreConnectScript)
  ├─ ValidateDlls()
  ├─ DpapiDecrypt(confPath) → plaintext WireGuard config
  ├─ WriteSecure(SvcConfPath)
  │   ├─ File.Create()                    empty — inherits parent ACL
  │   ├─ SetAccessControl(fileSec)        SYSTEM + Admins + user only
  │   └─ StreamWriter.Write(plaintext)
  ├─ TunnelDll.Connect(name, svcConf)
  │   └─ Creates WireGuardTunnel$<n> SCM service → wireguard-NT
  ├─ Delete SvcConfPath immediately (~200 ms lifetime)
  └─ RunTunnelScript(PostConnectScript)
```

---

## 6. Connecting a tunnel — Companion

```
StartTunnel(name) — Companion path
  ├─ RunTunnelScript(PreConnectScript)
  ├─ EnsureManagerRunning()
  ├─ ServiceController(SvcName(name)).Start()
  ├─ WaitForStatus(Running, 15 s)
  └─ RunTunnelScript(PostConnectScript)
```

---

## 7. Disconnecting a tunnel

```
StopTunnel(name)
  ├─ RunTunnelScript(PreDisconnectScript)
  ├─ [local]  TunnelDll.Disconnect(name)  → sc.Stop() + sc.Delete()
  │   or
  │   [WG]    ServiceController.Stop() + WaitForStatus(Stopped, 15 s)
  └─ RunTunnelScript(PostDisconnectScript)
```

---

## 8. Pre/post scripts

Each tunnel can run a `.bat` or `.ps1` at four hook points.

| Hook | When |
|---|---|
| `PreConnectScript` | Before the tunnel service starts |
| `PostConnectScript` | After the tunnel is confirmed running |
| `PreDisconnectScript` | Before the tunnel service is stopped |
| `PostDisconnectScript` | After the tunnel has stopped |

Script values take two forms:
- **Path** — `C:\scripts\vpn-up.ps1` — file called at runtime
- **Embedded** — `@embed:<content>` — written to temp file, executed, deleted

`.ps1` → `powershell.exe -ExecutionPolicy Bypass -File`. `.bat` → `cmd.exe /c`. Exit code and output logged. Non-zero exit is a warning but does not abort the operation.

---

## 9. Tunnel groups and categories

Each tunnel can be assigned to a named group. Groups are managed in Settings → General. The tunnel list shows: All · group tabs · Uncategorized. `RebuildTunnelGroups()` builds the tab strip; selection is preserved by name across rebuilds.

---

## 10. Quick Connect

```
QuickConnect_Click()
  ├─ OpenFileDialog (*.conf, *.conf.dpapi)
  ├─ ReadAllBytes + DpapiDecrypt if needed
  ├─ Store in _quickConnectConfig (in-memory only)
  ├─ StartTunnel via local path
  └─ Show "⚡ filename" at top of tunnel list
```

Config is never written to `%APPDATA%\MasselGUARD\tunnels\`.

---

## 11. Open network protection

Detects open (passwordless) WiFi by reading `WLAN_SECURITY_ATTRIBUTES.bSecurityEnabled` at offset 580. A value of `0` means no security. Activates the configured protection tunnel **before** any SSID rule or default action. Configure in Settings → WiFi Rules → Open Network Protection.

---

## 12. Configuration and storage

### config.json — `%APPDATA%\MasselGUARD\config.json`

```json
{
  "Rules":         [ { "Ssid": "HomeWifi", "Tunnel": "home" } ],
  "TunnelGroups":  [ { "Name": "Work", "IsExpanded": true } ],
  "DefaultAction": "activate",
  "DefaultTunnel": "home",
  "OpenWifiTunnel": "home",
  "Mode":          "Standalone",
  "ManualMode":    false,
  "Language":      "en",
  "ActiveTheme":   "default-dark",
  "AutoTheme":     false,
  "LogLevelSetting": "normal",
  "ShowTrayPopupOnSwitch": true
}
```

### Tunnel configs

| Path | Format |
|---|---|
| `<ExeDir>\tunnels\<n>.conf.dpapi` | DPAPI-encrypted WireGuard config |
| `<ExeDir>\tunnels\temp\<n>.conf` | Plaintext copy for service process (~200 ms lifetime) |

---

## 13. Security model

### DPAPI encryption

`.conf.dpapi` files encrypted with `DataProtectionScope.CurrentUser`. Decryption key derived from Windows login credentials — MasselGUARD never stores or handles keys.

### Atomic temp file

```csharp
File.Create(path).Dispose();                    // 1. create empty — inherits parent ACL
new FileInfo(path).SetAccessControl(fileSec);   // 2. restrictive ACL before first byte
using var sw = new StreamWriter(                // 3. write under correct ACL
    new FileStream(path, FileMode.Open, ...));
```

ACL: `SYSTEM + Administrators + owning user` only. Deleted within ~200 ms.

---

## 14. Theme system

Themes live in `theme/<folder>/theme.json`. See `theme/THEME_INFO.md` for the full key reference.

| Folder | Type | Style |
|---|---|---|
| `default-dark` | dark | Rounded (6 px) |
| `default-light` | light | Rounded (6 px) |
| `grey-dark` | dark | Sharp (0 px) |
| `grey-light` | light | Sharp (0 px) |
| `highcontrast-dark` | dark | Near-sharp (2 px), WCAG AAA |
| `highcontrast-light` | light | Near-sharp (2 px), WCAG AAA |

`ThemeManager.Instance.Load(folder)` applies all values into `Application.Current.Resources`. Every `{DynamicResource}` binding updates immediately. Auto-switching polls `HKCU\...\Themes\Personalize\AppsUseLightTheme` every 5 seconds.

---

## 15. Logging

| Level | Shown in Normal | Shown in Extended |
|---|---|---|
| `Ok` (green) | ✓ | ✓ |
| `Warn` (orange) | ✓ | ✓ |
| `Info` (accent) | — | ✓ |
| `Debug` (muted) | — | ✓ |

**Normal** — OK and Warn only. `SaveConfig(desc)` logs at Ok level — always visible.

**Extended** — Everything including Info (network changes, mode changes, language changes) and Debug (`[DBG]` connect timing, tunnel config fields).

Continuation lines (detail sub-entries) render with a `↳` prefix in the timestamp colour. Export: **Export Log** button → UTF-8 `.txt`.

---

## 16. Settings panel

| Tab | Key settings |
|---|---|
| **General** | Language, app mode, disable WiFi rules, tunnel groups |
| **Appearance** | Dark/light theme, auto system theme, background notifications |
| **Default Action** | WiFi fallback (none / disconnect / activate + tunnel), open network protection |
| **WiFi Rules** | Disable WiFi rules toggle, SSID→tunnel rules (deferred save) |
| **Advanced** | Install/uninstall, DLL status, WireGuard client, orphaned service cleanup, import/export, log level |
| **About** | Version, update check |

**Deferred save in WiFi Rules** — Add/Edit/Delete only update memory. A **Save** button writes to disk and logs the change.

---

## 17. Import / Export settings

**Export** (Settings → Advanced → Export settings):
- Shows a warning that tunnel configs are excluded and future-version compatibility is not guaranteed
- Writes a `*.masselguard` JSON file containing: Rules, TunnelGroups, DefaultAction, DefaultTunnel, OpenWifiTunnel, ManualMode, Mode, Language, themes, log level, popup toggle
- Field `AppVersion` stores the exporting app version

**Import** (Settings → Advanced → Import settings):
- Reads `*.masselguard` or `*.json`
- Compares `AppVersion` to running version — shows a Yes/No warning for any mismatch (both older→newer and newer→older)
- Uses `JsonDocument` for field-by-field parsing — unknown/future fields are silently ignored
- Rules and TunnelGroups replace existing lists entirely; all other fields merge

---

## 18. Build and deployment

### BUILD.bat

```bat
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
dotnet publish → dist\
copy theme\ → dist\theme\
copy wireguard-deps\*.dll → dist\
```

### tunnelbuild\tunnelbuild.bat

Builds `tunnel.dll` from source (requires Go 1.21+ and gcc/MinGW). Downloads `wireguard.dll` from download.wireguard.com/wireguard-nt/. Output to `tunnelbuild\wireguard-deps\`.

### Runtime requirements

| | |
|---|---|
| OS | Windows 10 / 11 x64 |
| Runtime | .NET 10 Desktop Runtime |
| Elevation | Administrator |
| Standalone / Mixed | `tunnel.dll` + `wireguard.dll` next to exe |
| Companion / Mixed | WireGuard for Windows installed |

---

## 19. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| "Already running" after reinstall | Orphaned mutex from previous install path | Wait 2–3 s and relaunch; the retry logic acquires the mutex after the OS releases it |
| Tunnel connects but immediately shows disconnected | wireguard-NT service exits after loading kernel driver — SCM logs false positive | Ignore; check tunnel list status |
| WiFi rule not firing | SSID case mismatch, or disable WiFi rules on | Enable Extended logging; compare detected SSID to rule |
| Edit button stays disabled | Row must be selected first | Click the tunnel name in the list |
| Pre/post script not running | Path missing or spaces without quotes | Use Browse; check Extended log for `[Script]` entries |
| Theme not in picker | `theme.json` missing `type` field | Ensure `type` is `"dark"` or `"light"` |
| Import warning on same-version file | AppVersion includes `-beta` suffix in old export | Proceed — fields are compatible |
