# MasselGUARD — How it works

Technical reference for v3.6.0 — Dangerous Donkey. For end-user instructions see [`MANUAL.md`](MANUAL.md).

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
12. [Kill switch](#12-kill-switch)
13. [Configuration and storage](#13-configuration-and-storage)
14. [Activity timeline](#14-activity-timeline)
15. [Security model](#15-security-model)
16. [Theme system](#16-theme-system)
17. [Font override system](#17-font-override-system)
18. [Logging](#18-logging)
19. [Settings panel](#19-settings-panel)
20. [Import / Export settings](#20-import--export-settings)
21. [Build and deployment](#21-build-and-deployment)
22. [Troubleshooting](#22-troubleshooting)
23. [Command-line interface (CLI)](#22-command-line-interface-cli)

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
  │   └─ MigrateInlineConfigsToFiles()  (one-time: inline Config → .conf.dpapi file)
  ├─ HistoryService.Load()     %APPDATA%\MasselGUARD\tunnel_history.json
  ├─ HistoryService.LoadSsid() %APPDATA%\MasselGUARD\wifi_history.json
  ├─ ApplyManualMode()
  ├─ ApplyLocalTunnelMode()
  ├─ SetupTimer()              1-second status poll
  ├─ _startupComplete = true
  ├─ RegisterWifiEvents()      WlanRegisterNotification
  ├─ TryUpdateWifi()           captures current SSID immediately at startup
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
WlanOpenHandle(version=2)
WlanRegisterNotification(WLAN_NOTIFICATION_SOURCE_ACM)
  └─ OnNotification() fires on ACM codes:
       9  = ACM_CONNECTED
       10 = ACM_DISCONNECTED
       20 = ACM_CONNECTION_COMPLETE

FireIfChanged(ssid, isOpen)
  └─ only invokes SsidChanged if ssid != _lastFiredSsid
     (deduplicates ACM_CONNECTED + ACM_CONNECTION_COMPLETE)

MainWindow.OnWifiChanged(ssid, isOpen)       [UI thread via Dispatcher.BeginInvoke]
  └─ ApplyWifiState(ssid, isOpen)            [central handler — also called by TryUpdateWifi]
       ├─ if ssid != _lastRecordedSsid:
       │    StoreWifiHistory? → HistoryService.RecordSsidConnect(ssid, isOpen)
       │                      → HistoryService.RecordSsidDisconnect() (if null)
       ├─ UpdateWifiLabel(ssid)
       └─ _vm.ApplyWifiState(ssid, isOpen)
            ├─ null ssid → 2-second debounce → re-query → disconnect or re-apply
            └─ non-null → log, EvaluateWifi(), ApplyRuleResult()

TryUpdateWifi()    [called at startup and by the retry timer]
  └─ QueryCurrentSsid() → ApplyWifiState()  [records SSID immediately on launch]
```

`ReadCurrentSsidFromApi()` reads `WLAN_CONNECTION_ATTRIBUTES` directly from memory:

| Offset | Field | Used for |
|---|---|---|
| 0 | `isState` (DWORD) | Must equal 1 (connected) before reading SSID |
| 520 | `uSSIDLength` (DWORD) | SSID byte length (clamped to 0–32) |
| 524 | `ucSSID[32]` | SSID bytes (UTF-8) |
| 576 | `bSecurityEnabled` (BOOL) | `false` = open network |

`WLAN_INTERFACE_INFO` stride: 532 bytes per entry.
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

### External transitions (companion tunnels)

The 1-second status poll (`MainViewModel.RefreshTunnelStatus`) compares each tunnel's cached `IsActive` against the live service state. Transitions that MasselGUARD did not initiate (WireGuard app, CLI) are handled explicitly:

- **External connect** → log `Connected: <name> via WireGuard app`, `TunnelService.RecordExternalConnect` opens a history entry (source *WireGuard app*), snapshots byte counters, and clears any stale intentional-disconnect mark and `UserDisconnected` flag.
- **External disconnect** → `TunnelService.RecordExternalDisconnect` closes the open history entry, writes extended-log continuation lines, and logs `Disconnected: <name> via WireGuard app`.

Auto-reconnect distinguishes a *clean deactivate* from a *crash* by checking whether the `WireGuardTunnel$<name>` SCM entry still exists. The WireGuard app stops the service **before** deleting the entry, so the check at drop time races the deletion — `AutoReconnectAsync` therefore re-checks after a 2 s grace delay (and after every backoff delay) and aborts with `was deactivated via the WireGuard app — not reconnecting` when the entry is gone. Reconnect attempts `await vm.ConnectAsync()` so success/failure is judged after the connect actually finishes.

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

Each tunnel can be assigned to a named group. Groups are managed in Settings → Tunnels. The tunnel list shows: All · group tabs · Uncategorized. `RebuildTunnelGroups()` builds the tab strip; selection is preserved by name across rebuilds.

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

Detects open (passwordless) WiFi by reading `WLAN_SECURITY_ATTRIBUTES.bSecurityEnabled` at offset 580. A value of `0` means no security. Activates the configured protection tunnel **before** any SSID rule or default action. Configure in Settings → WiFi → Open network protection.

---

## 12. Kill switch

`KillSwitchService` (late-bound via `HNetCfg.FwPolicy2` / `HNetCfg.FwRule` COM) manages a traffic block rule set in Windows Firewall.

### Enable flow

```
KillSwitchService.Enable(tunnelName, interfaceAlias, endpointIp, endpointPort)
  ├─ lock(_lock) / already active → return
  ├─ _savedDomain/Private/Public = policy.DefaultOutboundAction[prof]
  ├─ policy.DefaultOutboundAction[ALL_PROFILES] = Block
  ├─ Add MasselGUARD_KS_Allow_WG — Allow outbound on interfaceAlias (UDP/TCP)
  ├─ Add MasselGUARD_KS_Allow_Endpoint — Allow UDP to endpointIp:endpointPort
  ├─ Add MasselGUARD_KS_Allow_Loopback — Allow loopback (127.0.0.1/8)
  ├─ Add MasselGUARD_KS_Allow_DHCP — Allow UDP port 67/68 (DHCP)
  └─ _active.Add(tunnelName)
```

### Disable flow

```
KillSwitchService.Disable(tunnelName)
  ├─ _active.Remove(tunnelName)
  └─ if _active.Count == 0:
       ├─ Restore DefaultOutboundAction from _savedDomain/Private/Public
       └─ Remove all MasselGUARD_KS_* rules
```

`DisableAll()` clears `_active` and always restores policy — called on app exit from `App.OnExit`.

### Startup cleanup

`CleanupStaleRules()` called from `MainWindow.Loaded` before anything else:
1. Restore `DefaultOutboundAction` to `Allow` for all three profiles
2. Remove every rule whose `Name` starts with `MasselGUARD_KS_`

### Global mode

`AppConfig.KillSwitchMode` (`"off"` / `"always"`). When `"always"`, `isGlobalAlways = true` is passed to both `TunnelConfigDialog` and `TunnelMetadataDialog` — the per-tunnel toggle is hidden and the effective value is always `true`. The `stored.KillSwitch` field is only written when `!isGlobalAlways`.

### Reference counting

`_active` (`HashSet<string>`) tracks enabled tunnels. Policy is only applied on the **first** `Enable()` and only restored on the **last** `Disable()`. Concurrent tunnels all share the same firewall state.

---

## 13. Configuration and storage

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
| `%APPDATA%\MasselGUARD\tunnels\<n>.conf.dpapi` | DPAPI-encrypted WireGuard config (one file per tunnel) |
| `<ExeDir>\tunnels\temp\<n>.conf` | Plaintext copy for service process (~200 ms lifetime) |

`StoredTunnel.Config` (inline DPAPI blob in `config.json`) is legacy. `ConfigService.MigrateInlineConfigsToFiles()` runs on every load and moves any inline blobs to `.conf.dpapi` files, nulling `Config` so it no longer appears in JSON. `TunnelService.SaveConfigToFile(name, plaintext)` writes new files.

### History files

| File | Contents |
|---|---|
| `%APPDATA%\MasselGUARD\tunnel_history.json` | `ConnectionHistoryEntry[]` — TunnelName, ConnectedAt (UTC), DisconnectedAt (UTC), Source, SessionRxBytes, SessionTxBytes |
| `%APPDATA%\MasselGUARD\wifi_history.json` | `WifiHistoryEntry[]` — Ssid, ConnectedAt (UTC), DisconnectedAt (UTC), IsOpen |

Legacy file names (`history.json`, `ssid_history.json`) are renamed to the new names on first load via `File.Move`.

---

## 14. Activity timeline

### Canvas geometry

Rendered by `RenderChartCore()` into `TimelineCanvas`. Constants: `ChartBarH = 16`, `ChartBarGap = 5`, `ChartSsidGap = 2`.

```
Y 0–16       Tunnel bar (all tunnels stacked, one bar)
Y 16–21      Gap
Y 21–37      WiFi SSID row 0         (rowTop = wifiBandTop + si*(16+2))
Y 37–55      WiFi SSID row 1
...
Y barsH+     Time axis (20 px)
```

`_chartWifiBandTop` and `_chartSsids` are set each render pass so `MouseMove` can Y-hit-test without recomputing layout.

### Hover tooltip (`TimelineCanvas_MouseMove`)

A **unified tooltip** is shown regardless of Y position. At each X (= hovered time):

1. Loop `_chartData` → add a row for every tunnel session active at that time (name, time range, duration, live KB/s if near-now)
2. Call `AddWifiRow()` → append a WiFi row if `wifi_history.json` has an entry covering that time (SSID name, 📶, time range, 🔒/⚠)

Tooltip only shown when at least one tunnel or WiFi entry is found. Crosshair always shown.

`GetSsidAt(DateTime t)` converts `t` to UTC before comparing against UTC-stored entries.

### `< >` navigation

`GetNavEntries()` collects all tunnel sessions in the time window. `ShowNavTooltip()` pins a tooltip to the session's midpoint, then calls `AddWifiRow()` to append the WiFi row for that moment.

### Settings — toggle rules

`ApplyInfoSectionMode()` derives panel visibility:
```
visible = (ShowTimeline && StoreConnectionHistory)
       || (ShowWifiInChart && StoreWifiHistory)
```
`StoreConnectionHistory` off disables `ShowTimeline` toggle; `StoreWifiHistory` off disables `ShowWifiInChart` toggle. Neither forces the other's Show toggle off.

---

## 15. Security model

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

## 16. Theme system

Themes are **unified dual-variant** files: one `theme.json` per theme containing both colour variants. See `theme/THEME_INFO.md` for the full key reference.

| Location | Purpose |
|---|---|
| `<exedir>\theme\<folder>\theme.json` | Bundled built-in themes (`grey`, `highcontrast`) |
| `%APPDATA%\MasselGUARD\themes\<folder>\theme.json` | User themes — survive app updates, checked first |

`ThemeManager.BuiltinThemeNames` = `{ "grey", "highcontrast" }`; the virtual `__system__` theme (Windows accent palette) is also treated as built-in.

### File format

Root level holds **structural settings only** (font, corner radius, chrome, `AppName`); colour fields live in `"dark"` and `"light"` sections:

```json
{
  "name": "My Theme",
  "fontFamily": "Segoe UI",
  "cornerRadius": 6,
  "dark":  { "colorWindowBg": "#0E1117", "colorAccent": "#58A6FF" },
  "light": { "colorWindowBg": "#F6F8FA", "colorAccent": "#0969DA" }
}
```

### Load and resolution

`ThemeManager.Instance.Load(name, isDark)` resolves the variant and applies all values into `Application.Current.Resources` (every `{DynamicResource}` binding updates immediately):

```
variant = isDark ? def.Dark : def.Light
if variant != null      → MergeVariant(root, variant)
else if other != null   → AutoInvertVariant(MergeVariant(root, other))   ← auto-generated
else                    → def                                            ← legacy single-file theme
```

`AutoInvertVariant` derives the missing variant by HSL lightness inversion: neutral colours (S < 15 %) invert fully; backgrounds clamp to L ∈ [0.06, 0.94]; chromatic colours clamp to L ∈ [0.30, 0.75]. Nothing is written to disk — computed on every load. Colour fields missing from both sections fall back to the Windows system palette.

### Theme selection

`AppConfig.ActiveTheme` (default `"__system__"`) stores the single selected theme. `__system__` → `ThemeManager.Instance.LoadSystem(isDark)` (Windows 11 accent palette); anything else → `Load(ActiveTheme, isDark)`.

`AppConfig.SystemThemeMode` (`"auto"` / `"light"` / `"dark"`) determines dark/light preference:
- `"auto"` — polls `HKCU\...\Themes\Personalize\AppsUseLightTheme` every 5 seconds
- `"light"` / `"dark"` — fixed regardless of Windows setting

### Theme preview (Settings)

Picker and mode changes **do not apply** the theme live — they only update `_draft`. The **▶ Dark** / **▶ Light** buttons apply the selected theme's variant for 10 seconds via a `DispatcherTimer`; `CancelThemePreview()` then calls `_main.ApplyThemeFromConfig()`, which re-reads the committed config.

---

## 17. Font override system

`ThemeManager.ApplyFontOverride(bool enabled, string family, double size)` injects font resources into `Application.Current.Resources`:

```csharp
resources["Theme.FontFamily"] = new FontFamily(family);   // replaces theme font
resources["Theme.FontSize"]   = size;
```

All `{DynamicResource Theme.FontFamily}` and `{DynamicResource Theme.FontSize}` bindings update immediately.

### FontPickerItem class

The font family ComboBox uses a `FontPickerItem` data class rather than raw strings to solve WPF's property-inheritance failure through `IsEditable="True"` ComboBoxes:

```csharp
private sealed class FontPickerItem
{
    public string DisplayName { get; }
    public string FontName    { get; }
    public System.Windows.Media.FontFamily FontFamily { get; }
    public override string ToString() => DisplayName;  // drives IsEditable text box
}
```

The `DataTemplate` binds `FontFamily="{Binding FontFamily}"` directly on the `TextBlock` — this bypasses the broken WPF property inheritance chain and renders each item in its own typeface reliably.

`TextSearch.TextPath="DisplayName"` enables keyboard type-to-jump in the dropdown.

### Font preview timer

`FontPreviewBtn` applies the draft font to the whole interface for 10 seconds:

1. `ApplyFontPreview()` — updates the in-settings preview label from `_draft`
2. `ThemeManager.ApplyFontOverride(true, family, size)` — whole-interface apply
3. `DispatcherTimer` counts 10 s → `CancelFontPreview()` restores committed font + resets label

Changing the font picker or size slider while preview is active calls `CancelFontPreview()` immediately. The preview label does **not** update on picker changes — only on Preview click.

---

## 17. Logging

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

## 18. Settings panel

| Tab | Key settings |
|---|---|
| **General** | Language, app mode, start with Windows, confirm on close |
| **Tunnels** | Group add/edit/reorder, colours, visibility; auto-reconnect mode, kill switch mode, config validation; hide count, hide empty, DNS indicator |
| **WiFi** | SSID→tunnel rules, disable WiFi rules toggle, WiFi fallback (none / disconnect / activate + tunnel), open network protection, rules panel visibility |
| **Appearance** | System mode, custom theme toggle, dark/light theme pickers, theme preview, font override, font preview, notifications, show activity log |
| **History** | Capture/show toggles, chart time range, connection history list |
| **Advanced** | Import/export, log level, install/uninstall, WireGuard client, orphaned services |
| **About** | Version, update check frequency, check now |

**Deferred save** — all tabs (including WiFi rules) commit on the main Save button. Cancel reverts theme, font, and activity log visibility to committed state.

---

## 19. Import / Export settings

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

## 20. Build and deployment

### BUILD.bat

```bat
set VERSION=3.3.0
set CODENAME=Camouflaged Koala
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
dotnet publish -p:Version=%VERSION% -p:InformationalVersion=%VERSION%.%BUILD_NUM% → dist\
copy theme\ → dist\theme\
copy wireguard-deps\*.dll → dist\
```

Banner printed during build:
```
  --------------------------------------------------
  MasselGUARD  v3.3.0  |  Camouflaged Koala
  Harold Masselink  |  https://masselink.net
  --------------------------------------------------
```

Update `CODENAME` in both `BUILD.bat` **and** `UpdateChecker.cs` (`_codenames` dictionary) when bumping the version.

### Version vs. build stamp

- **Version** (`Major.Minor.Patch`) — static in source; never modified by BUILD.bat
- **Build stamp** (`YYMMDDHHMM`) — injected as `InformationalVersion` at compile time via `-p:InformationalVersion`; read at runtime from `Assembly.GetEntryAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()`
- Working tree stays clean after a build; no source file is patched

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

## 21. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| "Already running" after reinstall | Orphaned mutex from previous install path | Wait 2–3 s and relaunch; the retry logic acquires the mutex after the OS releases it |
| Tunnel connects but immediately shows disconnected | wireguard-NT service exits after loading kernel driver — SCM logs false positive | Ignore; check tunnel list status |
| WiFi rule not firing | SSID case mismatch, or disable WiFi rules on | Enable Extended logging; compare detected SSID to rule |
| Edit button stays disabled | Row must be selected first | Click the tunnel name in the list |
| Pre/post script not running | Path missing or spaces without quotes | Use Browse; check Extended log for `[Script]` entries |
| Theme not in picker | `theme.json` missing `type` field | Ensure `type` is `"dark"` or `"light"` |
| Import warning on same-version file | AppVersion includes `-beta` suffix in old export | Proceed — fields are compatible |

---

## 22. Run modes and installation

`MainWindow.AppRunModeKind` enum:

| Value | Condition |
|---|---|
| `Standalone` | `GetInstalledPath()` returns null |
| `ManagedPortable` | Installed path found, but current exe directory ≠ installed path |
| `Managed` | Current exe directory == installed path (using `Path.GetFullPath()` comparison) |

`GetInstalledPath()`:
1. Checks `AppConfig.InstalledPath` (stored as a directory); tests `File.Exists(dir + "\MasselGUARD.exe")`
2. Falls back to `HKLM\SOFTWARE\MasselGUARD\InstallPath`; syncs found value back to config

**UAC bypass** (`Program.cs`):
```
if (!IsElevated() && ScheduledTaskExists("MasselGUARD"))
    Process.Start("schtasks.exe", "/run /tn MasselGUARD /i")
    Environment.Exit(0)
```

The `MasselGUARD` Scheduled Task is created at `RunLevel=Highest` during install when the user opts in to "Start with Windows". This is the same pattern used by WireGuard for Windows.

---

## 23. Tray icon rendering

`App.TrayIconHelper.RenderIcon(int S, int activeCount)` produces a 32-bit ARGB bitmap at size S (typically 16 or 32 px) using GDI+ (`System.Drawing`).

**Active state** (`activeCount > 0`):
- Shield filled with `Success` theme colour (resolved from `Application.Current.Resources["Success"]` as `SolidColorBrush`)
- White checkmark chevron (✓ style) inside

**Idle state** (`activeCount == 0`):
- Shield filled with `CardBg` theme colour
- Shield outlined with `BorderColor` theme colour at 1.2 px
- `Accent`-coloured downward chevron inside

The icon is only redrawn when `_lastTrayActiveCount` changes — not on the 1-second `StatusTick`.

---

## 24. WiFi Rules panel (main window)

A read-only summary of `AppConfig.Rules` displayed below the tunnel management buttons in `Grid.Row="3"` (header) and `Grid.Row="4"` (content) of the left column.

Visibility logic in `RefreshWifiRulesPanel()`:
```
visible = AppConfig.ShowWifiRulesOnMainWindow && !AppConfig.ManualMode
```

The right column (Activity Log) uses `Grid.RowSpan="5"` so it always fills the full height of the outer grid regardless of the left panel height.

---

## 25. Tunnel uptime

`TunnelEntryViewModel._connectedAt` (nullable `DateTime`) is set to `DateTime.UtcNow` when `IsActive` transitions `false → true`. It is cleared on disconnect.

`StatusText` formats `DateTime.UtcNow - _connectedAt` as:
- `< 60s` → `Xs`
- `< 1h` → `Xm YYs`
- `< 24h` → `Xh YYm`
- `≥ 24h` → `Xd YYh YYm`

`RefreshStatus()` raises `PropertyChanged(nameof(StatusText))` every tick when active, driven by the 1-second `DispatcherTimer` in `MainViewModel`.

---

## 26. Deferred-save pattern (SettingsWindow)

`SettingsWindow` creates `_draft = _main.ConfigSvc.Config.DeepClone()` on `Loaded`. All handler mutations target `_draft`. `_vm` (SettingsViewModel) is populated from `_draft` and stages additional fields (rules, language, mode, log level, themes).

On **Save** (`SaveBtn_Click`):
1. Snapshot `before = _main.ConfigSvc.Config.DeepClone()`
2. Copy all `_draft` fields to `ConfigSvc.Config` (includes font override, activity log, confirm-on-close, theme fields)
3. Call `_vm.DoSave()` — writes `_vm` fields to config and calls `ConfigSvc.Save()`
4. If extended logging: call `LogChangedSettings(before, ConfigSvc.Config)` — logs only differing fields

On **Cancel / close without Save** (`OnClosing`):
- Always: both preview timers stopped (`_fontPreviewTimer`, `_themePreviewTimer`)
- Theme reverted to `_originalTheme` via `ThemeManager.Instance.Load(_originalTheme)`
- Font reverted to committed config via `ThemeManager.ApplyFontOverride(...)`
- Activity log panel visibility reverted to `_main.ConfigSvc.Config.ShowActivityLog`

`_savedSuccessfully = true` is set in `SaveBtn_Click` before `Close()` — `OnClosing` skips the revert block on a successful save.

---

## 27. WiFi rule name and execution counter

`TunnelRule` model fields added:
```csharp
public string Name           { get; set; } = "";   // display name
public int    ExecutionCount { get; set; } = 0;    // incremented by RuleEngine on match
```

`RuleEngine.EvaluateWifi` increments `match.ExecutionCount++` before returning a result. Config is saved by the caller after rule execution.

`WifiRuleRow` display class auto-generates `RuleName` when `rule.Name` is empty:
```csharp
var autoName = string.IsNullOrEmpty(r.Tunnel)
    ? $"{ssid} → disconnect"
    : $"{ssid} → {r.Tunnel}";
RuleName = string.IsNullOrEmpty(r.Name) ? autoName : r.Name;
```

---

## 28. WiFi rules drag-to-reorder

`WifiRulesListView` has `AllowDrop="True"`. Three handlers:
- `PreviewMouseDown` — captures `WifiRuleRow` and start position
- `PreviewMouseMove` — starts `DragDrop.DoDragDrop` after 4 px movement
- `Drop` — finds source and target by SSID, removes and reinserts in `ConfigSvc.Config.Rules`, saves

---

## 29. Double-fire prevention

Two guards prevent rules firing twice on a network switch:

**Guard 1** — `ApplyWifiState` entry:
```csharp
if (ssid == _currentSsid) return;   // already on this SSID
```

**Guard 2** — Debounce callback:
```csharp
var (live, liveOpen) = _wifi.QueryCurrentSsid();
if (!string.IsNullOrEmpty(live))
{
    if (live != _currentSsid)   // only apply if connect event hasn't already handled it
        ApplyWifiState(live, liveOpen);
    return;
}
```

Sequence on network switch (MasselTHINGS → MasselNET):
1. `ACM_DISCONNECTED` → debounce timer starts (2 s)
2. `ACM_CONNECTED` → `ApplyWifiState("MasselNET")` → `_currentSsid = "MasselNET"` → rule fires
3. Debounce fires 2 s later → re-queries → `live = "MasselNET"` → Guard 2: `live == _currentSsid` → no-op

---

## 30. Build number

`BUILD.bat` generates the build stamp and passes it to MSBuild — no source file is modified:

```bat
for /f %%a in ('powershell -NoProfile -Command "Get-Date -Format yyMMddHHmm"') do set BUILD_NUM=%%a
dotnet publish -p:Version=%VERSION% -p:InformationalVersion=%VERSION%.%BUILD_NUM%
```

`UpdateChecker.BuildStamp` reads it back at runtime:
```csharp
Assembly.GetEntryAssembly()
    ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    ?.InformationalVersion;
// → "3.3.0.2506011430"  (last 10 chars = build stamp)
```

`Version.TryParse` handles 4-part versions for comparison. The version component (`Major.Minor.Patch`) is always static; only the build stamp changes between builds.

---

## 31. Tray menu icons

`DrawMenuIcon(MenuIconKind)` produces a 16×16 GDI+ bitmap using theme colours from `Application.Current.Resources`:

| Kind | Description |
|---|---|
| `ShieldOff` | Shield filled with `BorderColor` |
| `ShieldOn` | Shield filled with `Success` + white checkmark |
| `Window` | Window frame with Accent title bar + 3 dots |
| `Exit` | Door + right arrow in `ErrorColor` |

`UpdateTrayStatus` updates `_tunnelMenuHeader.Image` to `ShieldOn`/`ShieldOff` based on `activeCount`.

---

## 32. Notification duration

`AppConfig.NotificationDurationSeconds` (default 5). Picker in Settings → Appearance: 3 / 5 / 10 / 15 / 30 seconds.

`ShowTrayNotification(title, body, durationMs)` uses `_trayIcon.ShowBalloonTip(durationMs)` with `BalloonTipIcon = ToolTipIcon.Info`. The shield tray icon appears as the notification source in the Windows notification centre automatically.

---

## 33. Defaults button popup

`DefaultsBtn_Click` builds a code-only `Window` (no XAML) with two `ComboBox` pickers — default action tunnel and open network protection — each with a "— clear —" entry. Positioned at:

```csharp
var winPos = PointToScreen(new Point(0, 0));
popup.Left = winPos.X / dpiX + (ActualWidth  - popup.ActualWidth)  / 2;
popup.Top  = winPos.Y / dpiY + (ActualHeight - popup.ActualHeight) / 2;
```

On Save: writes `DefaultAction`, `DefaultTunnel`, `OpenWifiTunnel` to config, then calls `NotifyAllBadges()`, `UpdateStatusBarCentre()`, `RefreshWifiRulesPanel()`, `_vm.RebuildTunnelList()`, `ApplyGroupFilter()`.

---

## 34. Drag tunnels into groups

Each tab button created in `AddTab()` receives:
```csharp
btn.AllowDrop = true;
btn.DragOver  += TunnelTabDragOver;
btn.Drop      += TunnelTabDrop;
```

`TunnelTabDragOver` accepts `"TunnelEntry"` data format (same key used by row drag). `TunnelTabDrop` reads `btn.Tag` for the group name, sets `stored.Group = tag == "Uncategorized" ? "" : tag`, saves, logs, and calls `_vm.RebuildTunnelList()` + `RebuildTunnelGroups()`.

---

## 35. Settings tab routing

`TabBtn_Click` maps button `Name` → page name via a `switch`:

```csharp
string tab = btn.Name switch
{
    "TabBtnTunnels"       => "Tunnels",
    "TabBtnWifi"          => "Wifi",
    "TabBtnAppearance"    => "Appearance",
    "TabBtnAdvanced"      => "Advanced",
    "TabBtnHistory"       => "History",
    "TabBtnAbout"         => "About",
    _                     => "General",
};
```

Legacy tab names are mapped inside `ShowTab` for callers that still pass the pre-3.7 names: `"Groups"` → `"Tunnels"`, `"Rules"`/`"DefaultAction"` → `"Wifi"`.

`Tab` is not used for routing because `SideTabBtn` style triggers on `Tag="Active"` for the highlight — the tag is exclusively for visual state.

`ShowTab(tab)` sets visibility on all `Page*` controls, sets `TabBtn*.Tag = "Active"` for the active button, and calls the appropriate refresh method.

---

## 36. Toast notification model

`ToastNotification` (public record-like class):

| Property | Type | Description |
|---|---|---|
| `Category` | `string` | Header category label |
| `Primary` | `string` | Large primary text (tunnel name) |
| `Secondary` | `string?` | Muted secondary text (rule reason) |
| `StripColor` | `string?` | Resource key (`"Accent"`, `"Success"`, `"Warning"`) or hex |
| `DurationMs` | `int` | Auto-dismiss duration in ms |

`ShowTrayNotification(ToastNotification n)` deduplicates by `Category|Primary|Secondary` key, suppressing identical notifications within 1 second. A `DispatcherTimer` resets the key after 1 second.

The legacy `ShowTrayNotification(string title, string body, int durationMs)` overload maps to a `ToastNotification` with `Category = title`, `Primary = body`.

---

## 37. Rule edit → tunnel list refresh

`WifiRuleAdd_Click`, `WifiRuleEdit_Click`, and `WifiRuleDelete_Click` all call:
1. `RefreshWifiRulesPanel()` — rebuilds the `WifiRuleRow` collection with updated `ExecutionCount` and names
2. `_vm.RebuildTunnelList()` — recomputes `TunnelEntryViewModel.RuleCount` for all tunnels from `ConfigSvc.Config.Rules`

This ensures the Rules column in the tunnel list stays in sync with any rule change.

---

## 22. Command-line interface (CLI)

### Entry point

`Program.Main()` calls `CliRunner.IsCliInvocation(args)` before any WPF initialisation. Returns `true` when `args[0]` is any value other than `/service`. CLI mode runs without WPF — no `App`, no `MainWindow`.

```
Program.Main(args)
  ├─ HandleServiceArgs(args)      /service dispatch — exits if matched
  ├─ CliRunner.IsCliInvocation()  → true when non-/service arg present
  │    └─ CliRunner.Run(args)     runs CLI, returns exit code
  └─ HideConsoleForGuiLaunch()    GUI path — detaches console
```

### Console ownership detection

`OutputType=Exe` (console subsystem) means Windows always allocates a console. For GUI launches, `HideConsoleForGuiLaunch()` hides and frees the console to prevent a black window.

For CLI launches from a **non-elevated terminal**, the `requireAdministrator` manifest causes Windows to create a new isolated console for the elevated process. `IsIsolatedConsole()` detects this:

```csharp
[DllImport("kernel32.dll")]
static extern uint GetConsoleProcessList(uint[] list, uint count);

static bool IsIsolatedConsole()
    => GetConsoleProcessList(new uint[2], 2) <= 1;
```

When isolated (count ≤ 1), `CliRunner.Run()` pauses before exit so the user can read the output, and prints a tip to run as Administrator for inline output.

### Command routing

`CliRunner.Run()` flow:

```
1. Parse global flags: --json, --quiet / -q
2. Early switch: help / --help / -h / -?  → CmdHelp()  (no config needed)
3. ConfigService.Load()
4. cmd switch:
     list / --list           → CmdList(cfg, json)
     status / --status       → CmdStatus(cfg, json)
     connect                 → CmdConnect(args, cfg, json, quiet)
     disconnect              → CmdDisconnect(args, cfg, json, quiet)
     disconnect-all          → CmdDisconnectAll(cfg, json, quiet)
     version / --version/-v  → CmdVersion(cfg, json)
     _                       → UnknownCmd(cmd)
5. Console.Out.Flush()
6. IsIsolatedConsole() pause (if !quiet)
```

### Version command

`CmdVersion` reads update status from `cfg.LatestKnownVersion` (populated by the GUI's background update check):

```csharp
string updateStatus =
    string.IsNullOrEmpty(latestKnown)             ? "unknown — run 'MasselGUARD status' to check" :
    UpdateChecker.IsNewerVersion(latestKnown)      ? $"update available — v{latestKnown}" :
    UpdateChecker.IsAheadOfLatest(latestKnown)     ? $"ahead of latest ({latestKnown})" :
                                                     "up to date";
```

Plain output:
```
MasselGUARD v3.3.0  |  Camouflaged Koala
build:   2506011430
Harold Masselink  |  https://masselink.net
Update:  up to date
```

JSON output adds `update_status` field alongside `version`, `codename`, `build`.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Error (tunnel not found, connect failed, not elevated, etc.) |
| `2` | Already in desired state |

### CliOutput

`Cli/CliOutput.cs` — thin wrapper around `Console.Out` / `Console.Error`:

| Method | Stream | Usage |
|---|---|---|
| `Info(msg)` | stdout | Normal output |
| `Ok(msg)` | stdout | Success messages |
| `Error(msg)` | stderr | Error messages |
| `PrintJson(obj)` | stdout | Serialises `obj` with `System.Text.Json`, indented |

---

## 39. Release codenames

`UpdateChecker._codenames` — static dictionary keyed by `"Major.Minor.Patch"`:

```csharp
private static readonly Dictionary<string, string> _codenames =
    new(StringComparer.OrdinalIgnoreCase)
    {
        { "3.3.0", "Camouflaged Koala" },
    };
```

`UpdateChecker.Codename` returns the name for the current version or `""` if none is assigned. `UpdateChecker.VersionWithCodename` returns `"3.3.0 — Camouflaged Koala"` or just `"3.3.0"`.

Codenames are assigned per `Major.Minor.Patch` release only — not per build. Update the dictionary in `UpdateChecker.cs` **and** `BUILD.bat` when bumping `VERSION`.

---

## 38. Managed Portable version check

`NormaliseVersion(v)` strips leading `v`/`V` and whitespace. Comparison:

```csharp
if (NormaliseVersion(currentVer) == NormaliseVersion(installedVer)) return; // identical, no prompt
bool currentIsNewer = IsVersionNewer(currentVer, installedVer);
string msg = currentIsNewer ? "...is newer than..." : "...differs from...";
```

Triggers on any difference including build number — `2.9.0.2505181430` vs `2.9.0.2505161200` will prompt. `IsVersionNewer` uses 4-part `Version.TryParse` comparison so build timestamps sort correctly.
