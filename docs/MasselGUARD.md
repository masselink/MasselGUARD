# MasselGUARD — How it works

Technical reference for v3.2.0 (build YYMMDDHHMM). For end-user instructions see [`MANUAL.md`](MANUAL.md).

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
14. [Security model](#14-security-model)
15. [Theme system](#15-theme-system)
16. [Font override system](#16-font-override-system)
17. [Logging](#17-logging)
18. [Settings panel](#18-settings-panel)
19. [Import / Export settings](#19-import--export-settings)
20. [Build and deployment](#20-build-and-deployment)
21. [Troubleshooting](#21-troubleshooting)

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
  ├─ UpdateWifiLabel(ssid)
  └─ _vm.ApplyWifiState(ssid, isOpen)
       ├─ null ssid → 2-second debounce → re-query → disconnect or re-apply
       └─ non-null → log, EvaluateWifi(), ApplyRuleResult()
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
| `<ExeDir>\tunnels\<n>.conf.dpapi` | DPAPI-encrypted WireGuard config |
| `<ExeDir>\tunnels\temp\<n>.conf` | Plaintext copy for service process (~200 ms lifetime) |

---

## 14. Security model

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

## 15. Theme system

Themes live in `theme/<folder>/theme.json`. See `theme/THEME_INFO.md` for the full key reference.

| Folder | Type | Style |
|---|---|---|
| `default-dark` | dark | Rounded (6 px) |
| `default-light` | light | Rounded (6 px) |
| `grey-dark` | dark | Sharp (0 px) |
| `grey-light` | light | Sharp (0 px) |
| `highcontrast-dark` | dark | Near-sharp (2 px), WCAG AAA |
| `highcontrast-light` | light | Near-sharp (2 px), WCAG AAA |

`ThemeManager.Instance.Load(folder)` applies all values into `Application.Current.Resources`. Every `{DynamicResource}` binding updates immediately.

### Custom appearance system

`AppConfig.UseCustomTheme` (bool, default `false`) separates colour sources:

- **Off** → `ThemeManager.Instance.LoadSystem(isDark)` — Windows 11 system accent palette
- **On** → `ThemeManager.Instance.Load(ActiveDarkTheme or ActiveLightTheme)` — custom file

`AppConfig.SystemThemeMode` (`"auto"` / `"light"` / `"dark"`) determines dark/light preference:
- `"auto"` — polls `HKCU\...\Themes\Personalize\AppsUseLightTheme` every 5 seconds
- `"light"` / `"dark"` — fixed regardless of Windows setting

`AppConfig.ActiveDarkTheme` and `AppConfig.ActiveLightTheme` are stored independently so switching modes doesn't reset either picker.

### Theme preview (Settings)

`DarkThemePicker_SelectionChanged`, `LightThemePicker_SelectionChanged`, `SystemMode_Changed`, and `CustomTheme_Changed` **do not apply** the theme live. They only update `_draft`. The `ThemePreviewBtn` applies the full draft state:

```csharp
private void ApplyDraftTheme()
{
    bool isDark = IsDraftDark();
    if (!_draft.UseCustomTheme)
        ThemeManager.Instance.LoadSystem(isDark);
    else
    {
        var target = isDark ? _draft.ActiveDarkTheme : _draft.ActiveLightTheme;
        ThemeManager.Instance.Load(target);
    }
    ThemeManager.ApplyFontOverride(_draft.FontOverrideEnabled, _draft.FontOverrideFamily, _draft.FontOverrideSize);
}
```

A `DispatcherTimer` counts 10 seconds, then `CancelThemePreview()` loads `_originalTheme` and restores committed font.

---

## 16. Font override system

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
| **General** | Language, app mode, show activity log |
| **Tunnel Groups** | Group add/edit/reorder, colours, visibility, hide count, hide empty |
| **Appearance** | System mode, custom theme toggle, dark/light theme pickers, theme preview, font override, font preview, notifications |
| **Default Action** | WiFi fallback (none / disconnect / activate + tunnel), open network protection |
| **WiFi Rules** | Disable WiFi rules toggle, SSID→tunnel rules |
| **Advanced** | Import/export, log level, install/uninstall, start with Windows, confirm on close, WireGuard client, orphaned services |
| **About** | Version, update check frequency, check now |

**Deferred save** — all tabs (including WiFi Rules) commit on the main Save button. Cancel reverts theme, font, and activity log visibility to committed state.

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

`BUILD.bat` generates the build number:
```bat
for /f %%a in ('powershell -NoProfile -Command "Get-Date -Format yyMMddHHmm"') do set BUILD_NUM=%%a
set FULL_VERSION=3.0.0.%BUILD_NUM%
```

Injects into `UpdateChecker.cs` via a temp `.ps1` file:
```bat
echo ... -replace 'CurrentVersion = ".*?"', 'CurrentVersion = "%FULL_VERSION%"' | Set-Content $f > temp.ps1
powershell -File temp.ps1
```

`Version.TryParse` handles 4-part versions natively for comparison.

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
    "TabBtnGroups"        => "Groups",
    "TabBtnAppearance"    => "Appearance",
    "TabBtnDefaultAction" => "DefaultAction",
    "TabBtnRules"         => "Rules",
    "TabBtnAdvanced"      => "Advanced",
    "TabBtnAbout"         => "About",
    _                     => "General",
};
```

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

## 38. Managed Portable version check

`NormaliseVersion(v)` strips leading `v`/`V` and whitespace. Comparison:

```csharp
if (NormaliseVersion(currentVer) == NormaliseVersion(installedVer)) return; // identical, no prompt
bool currentIsNewer = IsVersionNewer(currentVer, installedVer);
string msg = currentIsNewer ? "...is newer than..." : "...differs from...";
```

Triggers on any difference including build number — `2.9.0.2505181430` vs `2.9.0.2505161200` will prompt. `IsVersionNewer` uses 4-part `Version.TryParse` comparison so build timestamps sort correctly.
