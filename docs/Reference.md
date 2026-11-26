# MasselGUARD - Technical reference

Developer/technical reference for version 6 - Mending Meerkat. For end-user instructions see [`MANUAL.md`](MANUAL.md).

---

## Contents

1. [Operating modes](#1-operating-modes)
2. [Startup sequence](#2-startup-sequence)
3. [WiFi monitoring](#3-wifi-monitoring)
4. [Rule evaluation](#4-rule-evaluation)
5. [Connecting a tunnel](#5-connecting-a-tunnel)
6. [External tunnel changes](#6-external-tunnel-changes)
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
23. [Run modes and installation](#23-run-modes-and-installation)
24. [Tray icon rendering](#24-tray-icon-rendering)
25. [WiFi Rules panel (main window)](#25-wifi-rules-panel-main-window)
26. [Tunnel uptime](#26-tunnel-uptime)
27. [Deferred-save pattern (SettingsWindow)](#27-deferred-save-pattern-settingswindow)
28. [WiFi rule name and execution counter](#28-wifi-rule-name-and-execution-counter)
29. [WiFi rules drag-to-reorder](#29-wifi-rules-drag-to-reorder)
30. [Double-fire prevention](#30-double-fire-prevention)
31. [Build number](#31-build-number)
32. [Tray menu icons](#32-tray-menu-icons)
33. [Notification duration](#33-notification-duration)
34. [Defaults button popup](#34-defaults-button-popup)
35. [Drag tunnels into groups](#35-drag-tunnels-into-groups)
36. [Settings tab routing](#36-settings-tab-routing)
37. [Toast notification model](#37-toast-notification-model)
38. [Rule edit → tunnel list refresh](#38-rule-edit--tunnel-list-refresh)
39. [Command-line interface (CLI)](#39-command-line-interface-cli)
40. [Release codenames](#40-release-codenames)
41. [Managed Portable version check](#41-managed-portable-version-check)
42. [Licensing & third-party components](#42-licensing--third-party-components)
43. [Split tunneling (4.0.0)](#43-split-tunneling-400)
44. [DNS automation](#44-dns-automation)
45. [Service back-end (5.0)](#45-service-back-end-50)

---

## 1. Operating modes

MasselGUARD manages its own tunnels: you create or import configs in the app, they are stored encrypted, and `tunnel.dll` + `wireguard.dll` (WireGuardNT) next to the executable provide connectivity. No WireGuard application is needed. (The former *Companion* and *Mixed* modes, which drove WireGuard for Windows, have been removed; `ConfigService.DropCompanionTunnels` drops leftover entries when the config is loaded.)

The privileged work (tunnels, DNS, kill switch) runs in one of two **back-ends**, chosen at startup:

| Back-end | When | See |
|---|---|---|
| **Service** | The MasselGUARD service is installed, running and accepts this user; the app can then run unelevated | [section 45](#45-service-back-end-50) |
| **Direct** | Otherwise; the app itself runs elevated | [section 23](#23-run-modes-and-installation) |

---

## 2. Startup sequence

```
Program.Main()
  ├─ Set working directory + DLL search path to the exe folder
  ├─ "/service <conf>"  → tunnel service process (TunnelDll.HandleServiceArgs), then exit
  ├─ "/svc"             → privileged back-end service, then exit
  ├─ "--bypass ..."     → windowless DNS bypass client, then exit
  ├─ Elevation decision (section 23)
  └─ App.Run()
       ▼
App.OnStartup
  ├─ Load language and theme (holding Shift resets font, theme and language)
  ├─ Single-instance check (see 2a)
  ├─ new MainWindow()
  │    ConfigService.Load, LogService, HistoryService (tunnel, network and DNS history),
  │    PrivilegedBackend.Select, TunnelService, WiFiService, RuleEngine, MainViewModel
  ├─ Start the command pipe (DNS bypass requests)
  └─ Show the window (and hide it to the tray with Start minimized)
       ▼
MainWindow.OnLoaded
  ├─ KillSwitch.CleanupStaleRules()
  ├─ Apply theme and layout; close history entries of tunnels that are no longer running
  ├─ WiFiService.Start(); query the current SSID
  ├─ EvaluateNetworkNow(), then StartNetworkWatching()   (a wired-only PC has no Wi-Fi event)
  ├─ After the first Wi-Fi query settles: connect the "connect on start" tunnel
  └─ Update check, version prompts, setup wizard (first run or after an upgrade)
```

### 2a - Single-instance guard

A named mutex (`Global\MasselGUARD_SingleInstance`, usable by elevated and unelevated instances) prevents a second instance. If it is held:

1. Retry for up to 3 s (covers a crashed instance and an update that restarts the app).
2. If it is still held and another MasselGUARD process exists, show the *already running* dialog: close the other copy and continue here, or bring the running copy to the front and exit.

---

## 3. WiFi monitoring

MasselGUARD uses `wlanapi.dll` directly rather than WMI or process spawning.

```
WlanOpenHandle(version=2)
WlanRegisterNotification(ACM source)
  └─ OnNotification() reacts to:
       9  = ACM_CONNECTED          ┐ query the SSID (up to 5 tries, 200 ms apart)
       20 = ACM_CONNECTION_COMPLETE┘
       10 = ACM_DISCONNECTED

FireIfChanged(ssid, isOpen)          only raises SsidChanged when the SSID differs from the last one

MainWindow.OnWifiChanged             [UI thread]
  └─ ApplyWifiState(ssid, isOpen)    also used by the startup query
       ├─ UpdateWifiLabel(ssid)
       └─ MainViewModel.ApplyWifiState
            ├─ no SSID  → 2 s debounce, query again, then EvaluateNetworkNow()
            └─ new SSID → log it, EvaluateNetworkNow()
```

Wi-Fi events only trigger an evaluation. Rules, DNS and the network history work from a **network snapshot** of all connected Wi-Fi and wired adapters (`Services/NetworkMonitor`); `Services/NetworkWatcher` also re-evaluates after Windows network changes, debounced by `AppConfig.NetworkSettleMs`. See [`NetworkIdentity-Design.md`](NetworkIdentity-Design.md).

`ReadCurrentSsidFromApi()` reads `WLAN_CONNECTION_ATTRIBUTES` from memory:

| Offset | Field | Used for |
|---|---|---|
| 0 | `isState` | Must be 1 (connected) |
| 520 | `uSSIDLength` | SSID byte length (clamped to 0-32) |
| 524 | `ucSSID[32]` | SSID bytes (UTF-8) |
| 588 | `bSecurityEnabled` | 0 = open network |
| 596, 600 | auth and cipher algorithm | 802.11 open authentication with no cipher also counts as open |

`WLAN_INTERFACE_INFO` entries are 532 bytes apart.

---

## 4. Rule evaluation

`MainViewModel.ApplyNetworkSnapshot` is the one decision point. The **tunnel** is one decision for the **primary** network only; **DNS** is decided per adapter ([section 44](#44-dns-automation)). The tunnel rules are re-run only when the primary network's identity changes.

`RuleEngine.EvaluateNetwork(cfg, primary)`, first match wins:

```
1. Manual mode on, or the WireGuard feature off      → nothing
2. Open Wi-Fi and OpenWifiTunnel set                 → activate it
3. Enabled network rules whose conditions all hold   → the FIRST one in the rules table:
                                                       activate its tunnel; empty tunnel = disconnect
4. Enabled trusted rules, each on its side of the
   trusted-networks list                             → activate / disconnect
5. Default action                                    → "none" | "disconnect" | "activate" DefaultTunnel
```

DNS-only rules are skipped on the tunnel axis. When the last network disappears, `EvaluateWifiDisconnected` applies the default action only if it is *disconnect*. Schedule rules are evaluated separately by `EvaluateSchedules`.

`MainViewModel.ApplyRuleResult` carries out the result: *activate* disconnects the other active tunnels and connects the target (`ActivateTarget`); a reached data cap with *Kill at cap* asks first.

---

## 5. Connecting a tunnel

```
TunnelService.Connect(stored, cfg, source)
  ├─ RunScript(PreConnectScript)
  └─ ConnectLocal
       ├─ TunnelDll.ValidateDlls()
       ├─ Check the tunnel name (see below)
       ├─ DecryptConfig(stored); apply the split-tunnel rewrite; validate the config
       ├─ Direct mode:  WriteSecure(<exe>\tunnels\temp\<wire>.conf)
       │                  create empty → ACL: SYSTEM, Administrators, user → write
       │                TunnelDll.Connect(name, conf)
       │                delete the temp file
       ├─ Service mode: connect by name from the service's store (section 45)
       │
       │  TunnelDll.Connect
       │   ├─ Refuses to replace a running service stamped for another tunnel name
       │   └─ Creates the WireGuardTunnel$<wire> service, stamps its description, starts it
       └─ Enable the kill switch when configured
  then: record history, RunScript(PostConnectScript)
```

**Tunnel names** (`Models/TunnelName`). The name the user typed is the only one stored. `TunnelName.Wire(name)` (spaces become `_`) is computed whenever Windows or `tunnel.dll` needs it: the staged `.conf` file name (`tunnel.dll` takes the tunnel name from it and accepts only `^[a-zA-Z0-9_=+.-]{1,32}$`), the service name, the adapter, the UAPI pipe and the kill-switch interface. The conversion is never reversed. `IsValid` applies the rule to the wire form (no space at either end); `Clashes` finds names that share a wire form.

**Stopping the right service.** The service description is `MasselGUARD Tunnel: <app name>`, written before the service starts. `TunnelDll.Disconnect` refuses a service stamped for a different name, `IsRunning` is false for it, and `TunnelService.Disconnect` then leaves history and kill switch unchanged. A service without a stamp cannot be told apart. The tunnel editor locks the name while the tunnel is connected.

---

## 6. External tunnel changes

The former companion (WireGuard for Windows) connect path has been removed. The 1-second status poll (`MainViewModel.RefreshTunnelStatus`) still notices when a tunnel stops outside the app's own disconnect (a crash, or another process).

**Auto-reconnect.** A drop is retried only when auto-reconnect applies to the tunnel (`TunnelService.ShouldAutoReconnect`) and MasselGUARD did not stop the tunnel itself (`ConsumeIntentionalDisconnect`). `AutoReconnectAsync` waits a short grace period, then makes up to 3 attempts with 5, 10 and 15 s back-off, and stops if a kill-enabled data cap is reached.

---

## 7. Disconnecting a tunnel

```
TunnelService.Disconnect(stored, cfg)
  ├─ Mark the disconnect as intentional (no auto-reconnect)
  ├─ RunScript(PreDisconnectScript)
  ├─ Read the final traffic counters
  ├─ TunnelDll.Disconnect(name)          directly, or through the service
  │    ├─ Refuses a service stamped for another tunnel name
  │    └─ Stops and deletes the service, then closes the adapter
  ├─ Refused or failed → log the reason; history and kill switch stay as they are
  ├─ Close the history entry, disable the tunnel's kill switch
  └─ RunScript(PostDisconnectScript)
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
- **Path** - `C:\scripts\vpn-up.ps1` - file called at runtime
- **Embedded** - `@embed:<content>` - written to temp file, executed, deleted

`.ps1` → `powershell.exe -ExecutionPolicy Bypass -File`. `.bat` → `cmd.exe /c`. Exit code and output logged. Non-zero exit is a warning but does not abort the operation.

---

## 9. Tunnel groups and categories

Each tunnel can be assigned to a named group. Groups are managed in Settings → WireGuard. The tunnel list shows: All · group tabs · Uncategorized. `RebuildTunnelGroups()` builds the tab strip; selection is preserved by name across rebuilds.

---

## 10. Quick Connect

**Not available in the current app:** the *Quick Connect* button in `MainWindow.xaml` is collapsed and nothing shows it. The code behind it remains:

```
QuickConnect_Click()
  ├─ OpenFileDialog (*.conf, *.conf.dpapi)
  ├─ new StoredTunnel { Name = converted file name, Path = the file }   (not added to the config)
  └─ TunnelService.Connect(...)
```

---

## 11. Open network protection

An open (passwordless) Wi-Fi network activates `OpenWifiTunnel` **before** any rule or the default action (step 2 in [section 4](#4-rule-evaluation)). How "open" is detected is described in [section 3](#3-wifi-monitoring). Configure it in Settings → Automation, in the tunnel editor, from a tunnel's context menu or with the **Defaults** button.

---

## 12. Kill switch

`KillSwitchService` uses Windows Firewall through late-bound COM (`HNetCfg.FwPolicy2` / `HNetCfg.FwRule`). All rule names start with `MasselGUARD_KS_`.

### Enable flow

```
Enable(tunnelName, endpointIp, bypassRanges)
  ├─ Already enabled for this tunnel → return
  ├─ First tunnel: remember each profile's default outbound action, set it to Block,
  │                add MasselGUARD_KS_Allow_Loopback (127.0.0.0/8, ::1/128)
  ├─ Add MasselGUARD_KS_Allow_WG_<name>        outbound on the tunnel adapter (name with spaces as "_")
  ├─ Add MasselGUARD_KS_Allow_EP_<name>        UDP to the server's endpoint address
  └─ Add MasselGUARD_KS_Allow_Split_<name>_<i> for split-tunnel bypass ranges (section 43)
```

### Disable flow

`Disable(tunnelName)` does nothing for a tunnel that never enabled it. Otherwise it removes that tunnel's rules; when it was the last one, the saved outbound actions are restored and the loopback rule is removed. `DisableAll()` does the same for every tunnel. It runs on app exit, except when the service keeps automation running, which leaves tunnels and kill switch in place.

### Startup cleanup

`CleanupStaleRules()` removes leftover `MasselGUARD_KS_` rules. The outbound default is put back only when such rules were found or this run raised it, so a block-outbound policy set for other reasons is not lowered.

### Global mode

`AppConfig.KillSwitchMode`: `"per-tunnel"` (default, uses `StoredTunnel.KillSwitch`) or `"always"`. With *always*, the tunnel editor shows the setting as controlled globally.

---

## 13. Configuration and storage

### config.json - `%APPDATA%\MasselGUARD\config.json`

Serialised from `Models/AppConfig.cs`, which is the full list of settings. A few of them:

| Key | Content |
|---|---|
| `Tunnels`, `TunnelGroups` | Tunnel entries (no key material) and groups |
| `Rules` | Automation rules, in evaluation order |
| `TrustedNetworks` | Trusted list: SSIDs and `suffix:` / `mac:` / `subnet:` entries |
| `DefaultAction`, `DefaultTunnel` | `"none"` / `"disconnect"` / `"activate"` and its tunnel |
| `OpenWifiTunnel`, `ConnectOnStartTunnel` | Tunnel names |
| `ManualMode`, `PrimaryNetworkMode` | Automation off; how the primary network is chosen |
| `KillSwitchMode`, `AutoReconnectMode` | Global modes |
| `DnsProfiles` and other `Dns*` keys | [Section 44](#44-dns-automation) |
| `Language`, `ActiveTheme`, `SystemThemeMode`, `LogLevelSetting` | Interface and logging |

### Tunnel configs

| Path | Format |
|---|---|
| `%APPDATA%\MasselGUARD\tunnels\<name>.conf.dpapi` | DPAPI-encrypted WireGuard config (one file per tunnel) |
| `<ExeDir>\tunnels\temp\<wire>.conf` | Direct mode: short-lived plaintext copy for the tunnel service |
| `%ProgramData%\MasselGUARD\` | Service mode: the service's encrypted store and staging folder (section 45) |

`StoredTunnel.Config` (inline DPAPI blob in `config.json`) is legacy; `ConfigService.Load()` moves such entries to files. `TunnelService.SaveConfigToFile(name, plaintext)` writes new files.

### History files (in `%APPDATA%\MasselGUARD\`)

| File | Contents |
|---|---|
| `tunnel_history.json` | `ConnectionHistoryEntry[]` - TunnelName, ConnectedAt (UTC), DisconnectedAt (UTC), Source, SessionRxBytes, SessionTxBytes |
| `wifi_history.json` | `WifiHistoryEntry[]` for the primary network, Wi-Fi or wired - Ssid (display label), ConnectedAt, DisconnectedAt, IsOpen, Kind, AdapterName, DnsSuffix, GatewayMac |
| `dns_history.json` | Which DNS profile was applied, and when |

The older names `history.json` and `ssid_history.json` are migrated.

---

## 14. Activity timeline

The info panel above the footer has up to three panes, each with its own toggle: **Timeline**, **Data usage** and **DNS**. The range is 24 h, 7 d or 31 d.

### Timeline layout (`RenderChartCore`, into `TimelineCanvas`)

| Band | Content |
|---|---|
| Tunnel bar (`ChartBarH` = 16 px) | One bar for all tunnels, one colour per tunnel |
| Gap (`ChartBarGap` = 5 px) | |
| Network band (16 px) | One row for the primary network history, one colour per network; only when `ShowWifiInChart && StoreWifiHistory` and there is data |
| Time axis | |

Colours are derived from the theme's accent colour.

### Hover and navigation

The tooltip depends on the mouse height: over the tunnel bar it leads with the tunnel session, over the network band with the network, and shows the other below a separator. `GetNavEntries()` lists the tunnel sessions in the range; the ◀ ▶ buttons step through them (`ShowNavTooltip`). They are shown with the Timeline pane.

### Visibility (`ApplyInfoSectionMode`)

```
chartable = (ShowTimeline && StoreConnectionHistory)
         || (ShowWifiInChart && StoreWifiHistory)
         || StoreConnectionHistory
panel visible = chartable && ChartsEnabled && ChartsSectionVisible
```

---

## 15. Security model

### DPAPI encryption

`.conf.dpapi` files encrypted with `DataProtectionScope.CurrentUser`. Decryption key derived from Windows login credentials - MasselGUARD never stores or handles keys.

### Atomic temp file

```csharp
File.Create(path).Dispose();                    // 1. create empty - inherits parent ACL
new FileInfo(path).SetAccessControl(fileSec);   // 2. restrictive ACL before first byte
using var sw = new StreamWriter(                // 3. write under correct ACL
    new FileStream(path, FileMode.Open, ...));
```

ACL: `SYSTEM + Administrators + owning user` only. The file is deleted right after the connect attempt. This is the direct-mode path; with the service, tunnels are connected from the service's own store (section 45 and [`ServiceBackend-Security.md`](ServiceBackend-Security.md)).

---

## 16. Theme system

Themes are **unified dual-variant** files: one `theme.json` per theme containing both colour
variants (plus per-variant assets). The full field reference, JSON schema, and copy-paste
template live in the [MasselGUARD-themes](https://github.com/masselink/MasselGUARD-themes) repo
- this section covers only how the **app itself** discovers, loads, and resolves them.

| Location | Purpose |
|---|---|
| `%APPDATA%\MasselGUARD\themes\<folder>\theme.json` | All non-System themes - downloaded (Theme Browser) and user-made (Theme Builder) - live here per-user; survive app updates. The app bundles none |

`ThemeManager.ThemeNames()` discovers themes **from disk** - every folder under `%APPDATA%\MasselGUARD\themes\` with a `theme.json` (or `<folder>-theme.json`, the naming convention the community repo uses). **The app bundles no themes**; they are either installed by the Theme Browser (from the shared-themes repo) or created by the Theme Builder - both into the same per-user folder, so everything is editable and survives app updates. `ConsolidateThemeFolders` merges any old `custom_themes\`/`shared-themes\`/`shared_themes\` into `themes\` at startup; Create/Duplicate/Import refuse to overwrite an existing name (`ThemeExists`). Only the virtual `__system__` theme (Windows accent palette) is built in code, read-only and non-deletable (`IsBuiltinTheme`); every theme in `themes\` is editable and deletable. The builder lists **Built-in** (System) and **THEMES** (all the rest). The Dark/Light pill previews live for read-only themes too.

### Load and resolution

`ThemeManager.Instance.Load(name, isDark)` resolves the variant and applies all values into `Application.Current.Resources` (every `{DynamicResource}` binding updates immediately):

```
variant = isDark ? def.Dark : def.Light
if variant != null      → MergeVariant(root, variant)
else if other != null   → AutoInvertVariant(MergeVariant(root, other))   ← auto-generated
else                    → def                                            ← legacy single-file theme
```

`AutoInvertVariant` derives the missing variant by HSL lightness inversion: neutral colours (S < 15 %) invert fully; backgrounds clamp to L ∈ [0.06, 0.94]; chromatic colours clamp to L ∈ [0.30, 0.75]. Nothing is written to disk - computed on every load. Colour fields missing from both sections fall back to the Windows system palette.

### Theme selection

`AppConfig.ActiveTheme` (default `"__system__"`) stores the single selected theme. `__system__` → `ThemeManager.Instance.LoadSystem(isDark)` (Windows 11 accent palette); anything else → `Load(ActiveTheme, isDark)`.

`AppConfig.SystemThemeMode` (`"auto"` / `"light"` / `"dark"`) determines dark/light preference:
- `"auto"` - polls `HKCU\...\Themes\Personalize\AppsUseLightTheme` every 5 seconds
- `"light"` / `"dark"` - fixed regardless of Windows setting

### Theme preview (Settings)

Picker and mode changes **do not apply** the theme live - they only update `_draft`. The **▶ Dark** / **▶ Light** buttons apply the selected theme's variant for 10 seconds via a `DispatcherTimer`; `CancelThemePreview()` then calls `_main.ApplyThemeFromConfig()`, which re-reads the committed config.

### Font-size resource tiers

`ThemeManager.Apply()` sets four scaled resources from the theme's base `FontSize` - `Theme.FontSize.Tiny` (base−2), `Theme.FontSize.Small` (base−1), `Theme.FontSize` (base), `Theme.FontSize.Header` (base+2) - bound throughout the chrome so both a theme's own size and the Settings font-size override reach every label, not just the tunnel list. `Theme.HeaderFontFamily` resolves to the theme's `headerFontFamily` when set, else falls back to `Theme.FontFamily`.

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

The `DataTemplate` binds `FontFamily="{Binding FontFamily}"` directly on the `TextBlock` - this bypasses the broken WPF property inheritance chain and renders each item in its own typeface reliably.

`TextSearch.TextPath="DisplayName"` enables keyboard type-to-jump in the dropdown.

### Font preview timer

`FontPreviewBtn` applies the draft font to the whole interface for 10 seconds:

1. `ApplyFontPreview()` - updates the in-settings preview label from `_draft`
2. `ThemeManager.ApplyFontOverride(true, family, size)` - whole-interface apply
3. `DispatcherTimer` counts 10 s → `CancelFontPreview()` restores committed font + resets label

Changing the font picker or size slider while preview is active calls `CancelFontPreview()` immediately. The preview label does **not** update on picker changes - only on Preview click.

---

## 18. Logging

`AppConfig.LogLevelSetting` selects one of three levels (Settings → Activity log):

| Level | Setting | Records |
|---|---|---|
| **Normal** | `normal` | `Ok` and `Warn` |
| **Enhanced** | `extended` | Also `Info`: what happens on tunnels, networks and automation |
| **Debug** | `debug` | Also `Debug`: technical detail, plus a block of system information at startup |

Continuation lines start with `↳`. The log is written to `%APPDATA%\MasselGUARD\masselguard.log`; `ClearLogOnStart` and `MaxLogSizeKB` control how long lines are kept. The main window and the pop-out log window use the same `Views/LogTable` renderer. Log text is always English.

---

## 19. Settings panel

| Group | Tab | Covers |
|---|---|---|
| Application | **General** | Language, feature switches, import / export |
| | **Startup** | Start with Windows, tray behaviour, installation, background service |
| | **Appearance** | Theme, light / dark, font override |
| | **Notifications** | Pop-ups and their duration, network-change notification |
| | **Diagnostics** | System diagnostics, tester, repositories, orphaned tunnel services |
| | **About** | Version, update check, licenses, What's New |
| Feature settings | **WireGuard** | Groups, auto-reconnect, kill switch, display options |
| | **DNS** | DNS automation, profiles, bypass, leak protection |
| | **Automation** | Rules options, trusted networks, default action, open-network protection |
| | **Activity log** | Log level, clearing and size |
| | **History** | What is recorded, chart options, history list |

Settings are edited in a draft and committed on **Save** ([section 27](#27-deferred-save-pattern-settingswindow)).

---

## 20. Import / Export settings

Both directions go through `Services/PresetService`. The format and the managed-preset behaviour are described in the [manual](MANUAL.md) and in `CLAUDE.md` ("Managed preset").

- **Export** (`ConfigService.Export`) writes a `*.masselguard` JSON snapshot of the policy settings (`PresetService.PolicyFields`), including rules and groups, plus `AppVersion`. It never contains tunnel configs or DPAPI data. *Export as preset* can also add locked blocks.
- **Import** (`ConfigService.Import`) applies every value in the file (`PresetService.ApplyAll`) and ignores `Locked`; unknown fields are ignored. The values stay editable.
- A `*.masselguard` file **next to the exe** is a managed policy: only what it lists under `Locked` is forced and locked.

---

## 21. Build and deployment

### BUILD.bat

```bat
BUILD.bat            :: both arches (x64 + arm64)
BUILD.bat x64        :: single arch
BUILD.bat arm64
```

Per arch (`x64`, `arm64`) BUILD.bat cleans `obj\`/`bin\`, cross-publishes GUI + CLI framework-dependent single-file to `dist\<arch>\` with `-r win-<arch> -p:RuntimeIdentifier=win-<arch>`, copies `lang\`, the license files (`LICENSE.txt`, `THIRD-PARTY-NOTICES.md`, `licenses\Apache-2.0.txt`) and the matching `wireguard-deps\<arch>\*.dll`, and zips the folder to `dist\MasselGUARD-<arch>.zip`. Both `.csproj` declare `<RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>` with a conditional default RID (win-x64 for IDE builds). ARM64 cross-publishes from an x64 host - the SDK emits a native ARM64 apphost (PE machine `0xAA64`). A release requires **both** `MasselGUARD-x64.zip` and `MasselGUARD-arm64.zip`.

(No themes are bundled - they're downloaded from the shared-themes repo into `%APPDATA%`.)

Banner printed during build:
```
  --------------------------------------------------
  MasselGUARD  v6  |  Mending Meerkat
  Harold Masselink  |  https://masselink.net
  Building arch(es): x64 arm64  |  GUI + CLI  |  release zips
  --------------------------------------------------
```

Other arguments, in any order: `nozip`, `run`, `noui` / `nogui` and `nocli` (see `CLAUDE.md`, "Build"). Each release zip gets a `.zip.sha256` file next to it.

Update `VERSION`/`CODENAME` in `BUILD.bat` **and** the version in `UpdateChecker.cs` and both `.csproj` files when bumping the version ([section 40](#40-release-codenames)).

### Version vs. build stamp

- **Version** (a single number from 5 on, such as `6`) - static in source; never modified by BUILD.bat
- **Build stamp** (`yyMMddHHmm`) - injected as part of `InformationalVersion` at compile time ([section 31](#31-build-number))
- The working tree stays clean after a build; no source file is patched

### tunnelbuild\tunnelbuild.bat

Builds the per-architecture native DLLs into `wireguard-deps\<arch>\`. The built DLLs are committed to the repository, so this is only needed to update them (`force`); after that, update the versions in `THIRD-PARTY-NOTICES.md`. Run with no argument it **prompts** for x64 / arm64 / both / **check**; or pass `x64` / `arm64` / `all` (optionally `force`), or `check [install]`. It orchestrates `get-wireguard-dlls.ps1`, which **downloads** `wireguard.dll` (wireguard-NT, per-arch, from download.wireguard.com/wireguard-nt/) and **builds** `tunnel.dll` from a fresh `wireguard-windows` clone.

- **Hermetic build** - `wireguard-windows`'s own `build.bat` downloads its *own* Go + llvm-mingw toolchain (x86/amd64/arm64) into `.deps` and sets `GOROOT`/`GOARCH`/`CC` itself, so **no system Go or C compiler is required** - only **git**, **curl** and **tar** (curl + tar ship with Windows 10 1803+/11). It builds all three arches every run; the script keeps the one whose PE machine matches the target.
- **Cache-first** - a DLL is only (re)built when missing or the wrong arch; otherwise "Already cached". **`force`** sets the cached DLL aside as `.forcebak` (auto-restored if the rebuild fails) and rebuilds from scratch.
- **wireguard-tools mirror** - `wireguard-windows`'s build.bat fetches wireguard-tools from `git.zx2c4.com`'s cgit snapshot, which curl-schannel drops on some networks ("server closed abruptly"). `Repair-WgToolsDownload` rewrites that one line to the **GitHub mirror** (`github.com/WireGuard/wireguard-tools/archive/<commit>.zip`) with a dynamically computed SHA256 so build.bat's integrity check still passes.
- **`check [install]`** - `check-deps.ps1` validates git/curl/tar + TCP reach to download.wireguard.com and github.com, reports what's buildable, and (with `install`) winget-installs missing git.
- **Robustness** - hashing uses .NET `SHA256` and extraction uses `tar` (not the `Get-FileHash` / `Expand-Archive` cmdlets, which can be missing in locked-down PowerShell hosts). `get-wireguard-dlls.ps1` PE-verifies each produced DLL's machine type so a wrong-arch build can't slip through.

> The built DLLs (and the whole app) **must not run from a OneDrive/cloud-synced path** - the local-tunnel service runs as LocalSystem and can't read per-user cloud folders; MasselGUARD warns at startup if it detects this.

### Architecture support (x64 / ARM64)

The managed code is architecture-portable; only the launcher exe and the two native DLLs are per-arch. A Windows process is a single architecture and .NET has no fat binary, so each arch is a separate native build. On Windows-on-ARM the wireguard-NT **kernel driver is native and cannot be emulated**, so an emulated x64 build cannot drive local tunnels - ARM64 users need the ARM64 build (companion tunnels still work emulated). `UpdateChecker` selects the release asset by `RuntimeInformation.ProcessArchitecture` (`MasselGUARD-<arch>.zip`, falling back to legacy `MasselGUARD.zip` for x64). `TunnelDll.ValidateDlls()` gates on architecture first - `ArchSupportError()` catches the emulated-x64-on-ARM64 case, and a PE machine-type check on each DLL catches a wrong-arch DLL before the P/Invoke throws `BadImageFormatException`. The running architecture is shown in Settings → About and the CLI `version` output. When the x64 build runs on an ARM64 system, `MainWindow.OfferArm64SwitchAsync()` shows a startup prompt (Download / Later / Don't remind me - the last persists `AppConfig.ArmSwitchDismissed`) that can **switch to the native ARM64 build in one click**: `UpdateChecker.FetchLatestReleaseAsync(forceArch: "arm64")` selects the arm64 asset even though the process is x64, then the normal `UpdateAsync` download/extract/relaunch pipeline installs it and relaunches natively.

### Runtime requirements

| | |
|---|---|
| OS | Windows 10 / 11 - x64 or ARM64 (separate native builds) |
| Runtime | .NET 10 Desktop Runtime (matching architecture) |
| Elevation | Needed to install; at start only without the background service ([section 23](#23-run-modes-and-installation)) |
| Tunnels | Matching-arch `tunnel.dll` + `wireguard.dll` next to the exe |

---

## 22. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| "Cannot start service 'WireGuardTunnel$...'" | The tunnel service process ended before it reached Windows (for example a name WireGuard refuses, in builds before 6) | Check the Windows System event log; see [section 5](#5-connecting-a-tunnel) for the name rules |
| Tunnel fails with "Element not found" | MasselGUARD runs from a cloud-synced folder that LocalSystem cannot read | Move or install it to a normal local folder |
| A rule does not fire | Rule order, a disabled rule, manual mode, Simple Wi-Fi mode, or conditions that do not match | Use **Test rule** / **Advanced test**, `MasselGUARDcli network status`, and the Enhanced log level |
| Edit button stays disabled | No row selected | Click the row first |
| Pre/post script not running | Wrong path | Use Browse; check the log for `[Script]` entries at Enhanced or Debug level |
| Theme not in picker | No readable `theme.json` in its folder | See the themes repository's `THEME_EXAMPLE.md` |

---

## 23. Run modes and installation

`MainWindow.AppRunModeKind`:

| Value | Condition |
|---|---|
| `Standalone` | `GetInstalledPath()` returns null |
| `ManagedPortable` | An installed copy exists, but this exe runs from another folder |
| `Managed` | This exe runs from the installed folder |

`GetInstalledPath()` checks `AppConfig.InstalledPath`, then `HKLM\SOFTWARE\MasselGUARD\InstallPath`.

**Elevation** (`app.manifest` is `asInvoker`; `Program.Main` decides):

```
elevated                                → continue
not elevated, service usable            → continue unelevated (service back-end)
otherwise, scheduled task "MasselGUARD" → run the task (highest privileges, no prompt) and exit
otherwise                               → relaunch with a UAC prompt (declining ends the app)
```

**Start with Windows** is a per-user `HKCU\...\Run` entry when the service is installed (`Services/AutostartRunKey`), otherwise the elevated scheduled task. `MainWindow.ApplyAutostart` keeps only one of the two.

---

## 24. Tray icon rendering

`App.TrayIconHelper.RenderIcon(int S, int activeCount)` produces a 32-bit ARGB bitmap at size S (typically 16 or 32 px) using GDI+ (`System.Drawing`).

**Active state** (`activeCount > 0`):
- Shield filled with `Success` theme colour (resolved from `Application.Current.Resources["Success"]` as `SolidColorBrush`)
- White checkmark chevron (✓ style) inside

**Idle state** (`activeCount == 0`):
- Shield filled with `CardBg` theme colour
- Shield outlined with `BorderColor` theme colour at 1.2 px
- `Accent`-coloured downward chevron inside

The icon is only redrawn when `_lastTrayActiveCount` changes - not on the 1-second `StatusTick`.

---

## 25. WiFi Rules panel (main window)

The Automation panel lists `AppConfig.Rules` with a fixed `#` column for the evaluation order, and has buttons to add (also from the current network), edit, delete, enable or disable, and test rules. `RefreshWifiRulesPanel()` shows it when:

```
AppConfig.ShowWifiRulesOnMainWindow && !AppConfig.ManualMode
```

Clicking a column header sorts the display only; evaluation always follows the stored order. Row heights and the placement of the activity log are handled by `ApplyContentGeometry`.

---

## 26. Tunnel uptime

`TunnelEntryViewModel._connectedAt` (nullable `DateTime`) is set to `DateTime.UtcNow` when `IsActive` transitions `false → true`. It is cleared on disconnect.

`StatusText` formats `DateTime.UtcNow - _connectedAt` as:
- `< 60s` → `Xs`
- `< 1h` → `Xm YYs`
- `< 24h` → `Xh YYm`
- `≥ 24h` → `Xd YYh YYm`

`RefreshStatus()` raises `PropertyChanged(nameof(StatusText))` every tick when active, driven by the 1-second `DispatcherTimer` in `MainViewModel`.

---

## 27. Deferred-save pattern (SettingsWindow)

`SettingsWindow` creates `_draft = _main.ConfigSvc.Config.DeepClone()` when it loads. Handlers change `_draft`; `_vm` (SettingsViewModel) stages further fields.

On **Save**, `SaveBtn_Click` calls `CommitDraft()` and closes. `CommitDraft` marks the window as saved, takes a snapshot of the old config, writes the draft and the view-model fields to the config, saves, applies the side effects, and logs the settings that changed (`LogChangedSettings`).

On **close without saving** (`OnClosing`):
- the theme and font preview timers are stopped
- if the Dark / Light / Follow system choice was changed, the user is asked whether to keep it; *yes* commits the draft like Save
- otherwise the committed theme and font are applied again (`ApplyThemeFromConfig`) and the activity-log visibility is restored

---

## 28. WiFi rule name and execution counter

`TunnelRule.Name` is the optional display name; without one, `RuleName` summarises the rule. `ExecutionCount` is raised by `RuleEngine` when a network or trusted rule decides the tunnel.

**Directional trusted rules.** A `trusted` rule carries `TrustedWhen` (`"untrusted"` or `"trusted"`; `TrustedWhenOnList` is the parsed bool) and fires only on that side of `AppConfig.TrustedNetworks`. The first matching rule activates its tunnel (an empty tunnel disconnects); otherwise evaluation continues with the default action. Two rules can therefore cover both directions with one shared list.

---

## 29. WiFi rules drag-to-reorder

`WifiRulesListView` handles `PreviewMouseDown`, `PreviewMouseMove` (a drag starts after 4 px), `DragOver` / `DragLeave` (drop line) and `Drop`. The drop moves the dragged rule **instance** within `ConfigSvc.Config.Rules` to the position of the drop line, saves and refreshes. Rows are not matched by their text, because several rules can show the same value.

---

## 30. Double-fire prevention

| Guard | Where |
|---|---|
| The same SSID is not reported twice | `WiFiService.FireIfChanged`, and `MainViewModel.ApplyWifiState` (`ssid == _currentSsid`) |
| A disconnect is only acted on after 2 s, and not when the radio has a network again | the disconnect debounce in `ApplyWifiState` |
| An unchanged network snapshot does nothing | `ApplyNetworkSnapshot` (`snap.SameAs(previous)`) |
| Wi-Fi associated but without an address yet is not a disconnect | `ApplyNetworkSnapshot` |
| Same network with changed details and the same decision: no action, no extra rule hit | `ApplyNetworkSnapshot` (evaluates a copy of the config first) |
| No repeated pop-up for the same decision | `ApplyRuleResult` / `ActivateTarget` (`_lastAnnouncedReason`) |

---

## 31. Build number

`BUILD.bat` creates the build stamp (`yyMMddHHmm`) and passes it to MSBuild; no source file is modified:

```bat
for /f %%a in ('powershell -NoProfile -Command "Get-Date -Format yyMMddHHmm"') do set BUILD_NUM=%%a
dotnet publish ... -p:Version=%VERSION% -p:InformationalVersion=%VERSION%.%BUILD_NUM%
```

`UpdateChecker.BuildStamp` reads `AssemblyInformationalVersionAttribute`, and `UpdateChecker.SplitFullVersion` separates version and stamp (`6.<stamp>`, or the older `4.6.0.<stamp>`). Update checks compare versions only (Major.Minor.Patch, a missing part is 0), never the stamp.

---

## 32. Tray menu icons

`App.DrawMenuIcon(MenuIconKind)` draws the tray menu icons with GDI+ in theme colours. The kinds are `ShieldOff`, `ShieldOn`, `Window`, `Exit`, `Dns` and `Diag`. `UpdateTrayStatus` updates the tray text and icons when the number of active tunnels changes.

---

## 33. Notification duration

`AppConfig.NotificationDurationSeconds` (default 5); the picker in Settings → Notifications offers 3, 5, 10, 15 and 30 seconds. Notifications are MasselGUARD's own pop-up windows (`Views/ToastWindow`), not Windows balloon tips; see [section 37](#37-toast-notification-model).

---

## 34. Defaults button popup

`DefaultsBtn_Click` builds a code-only `Window` (no XAML) with three `ComboBox` pickers - default action, open network protection and connect on start - each with a "clear" entry. Positioned at:

```csharp
var winPos = PointToScreen(new Point(0, 0));
popup.Left = winPos.X / dpiX + (ActualWidth  - popup.ActualWidth)  / 2;
popup.Top  = winPos.Y / dpiY + (ActualHeight - popup.ActualHeight) / 2;
```

On Save: writes `DefaultAction`, `DefaultTunnel`, `OpenWifiTunnel` and `ConnectOnStartTunnel` to config, then calls `NotifyAllBadges()`, `UpdateStatusBarCentre()`, `RefreshWifiRulesPanel()`, `_vm.RebuildTunnelList()`, `ApplyGroupFilter()`.

---

## 35. Drag tunnels into groups

Each tab button created in `AddTab()` receives:
```csharp
btn.AllowDrop = true;
btn.DragOver  += TunnelTabDragOver;
btn.Drop      += TunnelTabDrop;
```

`TunnelTabDragOver` accepts `"TunnelEntry"` data format (same key used by row drag). `TunnelTabDrop` reads `btn.Tag` for the group name, sets `stored.Group = tag == "Uncategorized" ? "" : tag`, saves, logs, and calls `_vm.RebuildTunnelList()` + `RebuildTunnelGroups()`.

---

## 36. Settings tab routing

`SettingsWindow.TabBtn_Click` maps the button name to a tab name, and `ShowTab(tab)` shows the matching `Page*` panel, marks the button with `Tag="Active"` (used only for the highlight) and refreshes that page:

| Button | Tab name |
|---|---|
| `TabBtnTunnels` / `TabBtnDns` / `TabBtnWifi` / `TabBtnLog` / `TabBtnHistory` | `Tunnels` / `Dns` / `Wifi` / `Log` / `History` |
| `TabBtnStartup` / `TabBtnAppearance` / `TabBtnNotifications` / `TabBtnDiagnostics` / `TabBtnAbout` | `Startup` / `Appearance` / `Notifications` / `Diagnostics` / `About` |
| any other | `General` |

---

## 37. Toast notification model

`ToastNotification` (public record-like class):

| Property | Type | Description |
|---|---|---|
| `Category` | `string` | Header category label |
| `Primary` | `string` | Large primary text (tunnel name) |
| `Secondary` | `string?` | Muted secondary text (rule reason) |
| `StripColor` | `string?` | Resource key (`"Accent"`, `"Success"`, `"Warning"`) or hex |
| `DurationMs` | `int` | Auto-dismiss duration in ms |

`ShowTrayNotification(ToastNotification n)` deduplicates by `Category|Primary|Secondary` key, suppressing identical notifications within 1 second. A `DispatcherTimer` resets the key after 1 second. **Interactive** toasts, which ask for a decision (for example connecting over a data cap), are never deduplicated, and while one is waiting for an answer other new toasts are dropped. At the Enhanced and Debug log levels each shown message is also written to the log.

The legacy `ShowTrayNotification(string title, string body, int durationMs)` overload maps to a `ToastNotification` with `Category = title`, `Primary = body`.

---

## 38. Rule edit → tunnel list refresh

`WifiRuleAdd_Click`, `WifiRuleEdit_Click`, and `WifiRuleDelete_Click` all call:
1. `RefreshWifiRulesPanel()` - rebuilds the `WifiRuleRow` collection with updated `ExecutionCount` and names
2. `_vm.RebuildTunnelList()` - recomputes `TunnelEntryViewModel.RuleCount` for all tunnels from `ConfigSvc.Config.Rules`

This ensures the Rules column in the tunnel list stays in sync with any rule change.

---

## 39. Command-line interface (CLI)

### Entry point

The CLI is its own executable, `MasselGUARDcli.exe` (console subsystem, `MasselGUARDcli/Program.cs` -> `Cli.CliRunner.Run`), built from the same shared sources as the GUI (`MasselGUARDcli.csproj` links them). `MasselGUARD.exe` is a `WinExe`, so Windows never allocates a console for the GUI and there is nothing to hide.

The CLI manifest is `asInvoker`; `CliProgram.Main` checks elevation itself. The read-only commands `help`, `version`, `selftest`, `dns` (`status`, `bypass`) and `network` run in any terminal (`IsNonElevatedCommand`); every other command needs an elevated terminal and otherwise prints "MasselGUARDcli requires Administrator privileges" and exits with code 1. Before anything else the CLI sets the working directory and the DLL search path to its own folder so `tunnel.dll` finds `wireguard.dll`.

`dns bypass` talks to the running window through `Services/CommandPipe`, and falls back to the MasselGUARD service when no window answers (`Services/BypassClient`; the Explorer right-click entries run the windowless `MasselGUARD.exe --bypass` launcher, which uses the same client); `service ...` manages the service (see section 45).
### Command routing

`CliRunner.Run()`:

```
1. Read global flags: --json, --quiet / -q, --active, --group <name>, --logtype
2. help and selftest run without loading the config
3. ConfigService.Load()
4. Switch on the command: list, status, connect, disconnect, disconnect-all, info, dns, network,
   service, log, tunnel-history, wifi-history / network-history, import, delete / remove,
   rawconnect, check-update, version; anything else is reported as unknown
5. Console.Out.Flush()
```

See [`CLIManual.md`](CLIManual.md) for each command.

### Version command

`CmdVersion` takes the update status from `cfg.LatestKnownVersion` (stored by the last update check): *unknown*, *update available*, *ahead of latest* or *up to date*.

Plain output:
```
MasselGUARDcli v6  |  Mending Meerkat
build:   <yyMMddHHmm>
arch:    arm64
Harold Masselink  |  https://masselink.net
Update:  up to date
```

JSON output has `version`, `codename`, `build`, `arch` and `update_status`. `arch` comes from `UpdateChecker.ArchMoniker` (`x64` / `arm64` / `x86`).

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Error (tunnel not found, connect failed, not elevated, etc.) |
| `2` | Already in desired state |

### CliOutput

`Cli/CliOutput.cs` - thin wrapper around `Console.Out` / `Console.Error`:

| Method | Stream | Usage |
|---|---|---|
| `Info(msg)` | stdout | Normal output |
| `Ok(msg)` | stdout | Success messages |
| `Error(msg)` | stderr | Error messages |
| `PrintJson(obj)` | stdout | Serialises `obj` with `System.Text.Json`, indented |

---

## 40. Release codenames

`UpdateChecker._codenames` maps the version, written exactly as in `CurrentVersion`, to a codename (for example `{ "4.6.0", "Wired Weasel" }`, `{ "5", "Background Badger" }`, `{ "6", "Mending Meerkat" }`). `UpdateChecker.Codename` returns the name for the current version or `""`; `VersionWithCodename` returns for example `"6 - Mending Meerkat"`.

When the version changes, update the dictionary and `CurrentVersion` in `UpdateChecker.cs`, `VERSION` and `CODENAME` in `BUILD.bat`, and the default versions in both `.csproj` files.

---

## 41. Managed Portable version check

In `ManagedPortable` mode the app compares its own full version with the installed exe's `ProductVersion` (else `FileVersion`):

1. Same version (`NormaliseVersion` ignores a `v` prefix and trailing zeros) **and** same build stamp: no prompt.
2. Different versions: `IsVersionNewer` compares them part by part.
3. Same version, different build: the higher stamp is newer.

The prompt says whether this copy is newer or just different, and offers to install it over the installed copy.

---

## 42. Licensing & third-party components

MasselGUARD's own source is **MIT** (`LICENSE`, repo root). Bundled third-party components keep their own licenses, documented in `THIRD-PARTY-NOTICES.md` (repo root):

- `tunnel.dll` - WireGuard embeddable tunnel service (wireguard-windows): **MIT**. The Go runtime and `golang.org/x` libraries compiled into it are **BSD 3-Clause**; the notices file records the source commit and versions of the bundled DLLs.
- `wireguard.dll` - WireGuardNT prebuilt DLL: [**Prebuilt Binaries License**](https://git.zx2c4.com/wireguard-nt/plain/prebuilt-binaries-license.txt) (not open source; the WireGuardNT source is GPLv2). Redistribution needs WireGuard LLC's written consent, **except** insofar as the DLL is distributed alongside other software that uses it only through the API in `wireguard.h` (the "Permitted API"). So ship it unmodified, only together with the app, and call nothing outside that API. The license text governs.
- NuGet packages - Markdig (**BSD 2-Clause**), ZXing.Net and its Windows bindings (**Apache-2.0**), Microsoft `System.*` packages (**MIT**). The Apache-2.0 text is in `licenses/Apache-2.0.txt`; `BUILD.bat` copies it into every build and release zip.
- .NET 10 - framework-dependent (runtime not bundled), © Microsoft, MIT.
- **WinDivert** - reserved for future per-app split tunneling (a later 4.x; **not yet bundled**); dual **LGPLv3 / GPLv3**, intended via the **LGPLv3** dynamic-link path. Compliance checklist in `HANDOVER.md` §3C.

**WireGuard** is a registered trademark of Jason A. Donenfeld; MasselGUARD is an independent project, not affiliated with or endorsed by WireGuard LLC. When adding, removing, or updating a bundled component, update `THIRD-PARTY-NOTICES.md` in the same change.

---

## 43. Split tunneling (4.0.0)

Route/IP-based split tunneling. Full design in [`SplitTunneling-Design.md`](SplitTunneling-Design.md); this is the implementation summary.

**Key fact:** MasselGUARD never programs the routing table - `tunnel.dll` (wireguard-NT) derives all routes from the peer's `AllowedIPs`. Route-based split is therefore a **pure `AllowedIPs` rewrite** on the plaintext `.conf`, with no route-table code and no new kernel surface.

**Data model** (`Models/StoredTunnel.cs`, WPF-free): `SplitMode` (`"off"` | `"exclude"` | `"include"`), `SplitRanges` (`List<string>` of CIDRs/IPs), and a reserved-but-inert `SplitApps` (per-app placeholder for the future WinDivert backend). `Models/SplitConfig.cs` is the backend-facing DTO (`From(StoredTunnel)`, `HasRouteSplit`, `HasAppSplit`).

**Core math** (`Services/CidrMath.cs`): `ComputeEffectiveAllowedIPs(base, mode, ranges)`. `exclude` = base minus the ranges via per-family CIDR set subtraction (binary block splitting - the standard WireGuard AllowedIPs calculator); `include` = the ranges intersected with the base; `off` = base verbatim. IPv4/IPv6 computed independently, output v4-first and sorted. Pure/unit-testable - covered by `MasselGUARDcli selftest`.

**Backend abstraction** (`Services/SplitTunnelBackend.cs`): `ISplitTunnelBackend` with `RouteBasedBackend` (4.0.0) and a future `WinDivertBackend` (per-app). `ApplyToConfig` reads the first `[Peer]`'s `AllowedIPs`, computes the effective set, and patches it back with `Cli.WireGuardConf.Patch`. Per-app hooks (`OnConnected`/`OnDisconnected`) are no-ops here.

**Connect wiring** (`Services/TunnelService.cs`): the rewrite runs **after decrypt, before validation** in `Connect`, so the config that gets validated and written already reflects the split. No-op unless `SplitConfig.HasRouteSplit`; any error fails safe (connects with the original config).

**Kill-switch coupling** (`Services/KillSwitchService.cs`): in `exclude` mode the excluded ranges must reach the physical NIC, but the WFP kill switch blocks all non-tunnel outbound. `Enable(tunnel, endpointIp, bypassRanges)` adds `MasselGUARD_KS_Allow_Split_<tunnel>_<i>` allow-rules for those ranges, tracked by exact name (tunnel names can prefix one another) and removed precisely; `CleanupStaleRules` already prefix-scans all `MasselGUARD_KS_` rules. Fed from `RouteBasedBackend.KillSwitchBypassRanges`.

**UI** (`Views/TunnelConfigDialog`): a **Split** tab (local tunnels only) - mode radios, a ranges box (one CIDR per line, validated on save), and a disabled Apps preview. Threaded through `existingSplit*` ctor args + `ResultSplit*`; `SplitApps` is preserved untouched on edit.

**Persistence & portability:** `SplitMode`/`SplitRanges` round-trip through `TunnelExportService` as `# MasselGUARD-SplitMode:` / `# MasselGUARD-SplitRanges:` comment lines (`SplitApps` excluded - unused). CLI `info` surfaces them (`Split:` line / `split_mode`+`split_ranges` JSON).

## 44. DNS automation

Rule-driven DNS resolver selection that works **independently of tunnels** ("on Wi-Fi X → DNS Y", plain or DoH). Full design in [`DnsAutomation-Design.md`](DnsAutomation-Design.md); this is the implementation summary.

**Model C - a parallel axis.** On a network change two decisions run side by side: the tunnel action for the primary network (`RuleEngine.EvaluateNetwork`) and the DNS action for each adapter. The DNS precedence is a **pure, CLI-shared** function `Services/DnsPolicy.cs` → `Evaluate(cfg, networkIdentity, now)` returning `DnsResult(None | Apply | Automatic, ProfileId, Reason)`: off (manual mode, or DNS automation or the DNS feature off) → open network → network rules (first in the table) → trusted rule → schedule rule → default. A dangling profile id fails safe to `None`. `RuleEngine.EvaluateDns` wraps it. `DnsPolicy.IsWithinSchedule` is the single schedule-window impl (RuleEngine delegates to it); `DnsPolicy.IsDnsOnly` (empty `Tunnel` + a `DnsProfileId`) is used on the tunnel axis to **skip DNS-only rules**, so such a rule never reads as "disconnect". Covered by the `DnsPolicy` suite of `MasselGUARDcli selftest`.

**Data model** (WPF-free, CLI-globbed): `Models/DnsProfile.cs` - Id/Name, v4+v6 primary/secondary, `Encryption` (`plain`|`doh`|`auto`), `DohTemplate`, `RequireEncryption`, `ListId` (the DNS-list entry it came from); sentinels `NoneId`(`""`)/`AutomaticId`(`__automatic__`); (the old `BuiltInPresets()` was replaced by the DNS server list, see CLAUDE.md "DNS server list"). `TunnelRule.DnsProfileId` attaches DNS to any rule. `AppConfig`: `DnsAutomationEnabled`, `DnsProfiles`, `DefaultDnsProfileId`, `OpenWifiDnsProfileId`, `DnsAddressFamilies` (`both`|`v4`|`v6`). All defaults leave existing behaviour unchanged.

**Application** (`Services/DnsService.cs`, GUI-only): per-interface DNS by adapter GUID → alias via `netsh interface ip[v4|v6] set/add dnsservers` (full System32 path, `ArgumentList`-quoted). **DoH:** encrypted profiles register `netsh dns add encryption server=<ip> dohtemplate=<uri> autoupgrade=yes udpfallback=<no|yes>` per server IP, then set the servers (Windows 11 auto-upgrades to DoH). Gated on Win11 22000+ (`OperatingSystem.IsWindowsVersionAtLeast(10,0,22000)`); a `RequireEncryption` profile on an older build is **refused** (fail-closed), otherwise it downgrades to plain. Well-known resolver IPs carry built-in templates. `ipconfig /flushdns` on each change.

**Resolver ownership.** `ApplyNetworkSnapshot` evaluates DNS for each adapter that appeared or changed and `ApplyPendingDns` applies it to that adapter. A profile the user enabled by hand outranks everything, including a connected tunnel's DNS. Otherwise, while any tunnel is active (`_tunnelOwnsDns`, recomputed in `RefreshTunnelStatus`) the tunnel's own DNS supersedes, so the rule's DNS action is **held** and applied again when the tunnel goes down. `None` restores any prior override (an unmatched network gets its own resolver back). An adapter that disappears gets its original DNS restored.

**Lifecycle** (`Services/DnsService` + `%APPDATA%\MasselGUARD\dns_state.json`): the interface's original static/DHCP DNS is snapshotted before the first override (read from the `Tcpip`/`Tcpip6` `NameServer` registry values), and restored on **exit** (`App.OnExit` → `MainViewModel.RestoreDnsOverrides`) and at **startup** after a crash/reboot (`RecoverDnsFromPreviousRun`) - netsh writes persist, so this cleanup is essential.

**UI & CLI:** Settings → **DNS** (master toggle, default/open-network profile pickers, address families, and a profiles manager whose editor is the code-built `Views/DnsProfileEditor.cs`); `RuleDialog` gained an optional DNS-profile picker (`ResultDnsProfileId`). CLI `dns status` (read-only, non-elevated) prints the config + live per-interface resolvers.

---

## 45. Service back-end (5.0)

The privileged operations (tunnels, DNS, kill switch) can run in a LocalSystem Windows service, **MasselGUARDsvc** (`MasselGUARD.exe /svc`), reached through the named pipe `MasselGUARD.Service`; without it the app elevates itself as before ("direct mode"). The full design (components, RPC operations, data folders, startup/elevation flow, headless automation, update checksums, manual test plan) is in [`ServiceBackend-Design.md`](ServiceBackend-Design.md) and the security model and review log in [`ServiceBackend-Security.md`](ServiceBackend-Security.md). User-facing description: [`MANUAL.md`](MANUAL.md) chapter 37; commands: [`CLIManual.md`](CLIManual.md) (`service`, `dns bypass`).

Facts worth knowing when reading the code elsewhere in this reference:
- `app.manifest` is `asInvoker` (it was `requireAdministrator` up to 4.6, which is what direct mode still achieves with a UAC relaunch); `Program.Main` decides between unelevated service mode, the scheduled task and a UAC relaunch.
- Tunnel configs in service mode live in `%ProgramData%\MasselGUARD\tunnels\*.tun` (machine-scope DPAPI), not in `%APPDATA%\...\*.conf.dpapi`; the per-user files remain the source of truth the window pushes from.
- `dns_state.json` is per user in direct mode and under `%ProgramData%\MasselGUARD` for the service.
- The updater checks the download against `MasselGUARD-<arch>.zip.sha256`; `BUILD.bat` writes it.