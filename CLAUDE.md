# MasselGUARD — Codebase Guide

WireGuard tunnel manager for Windows. Two executables:
- **`MasselGUARD.exe`** — WPF GUI application (`OutputType=WinExe`, no console flash)
- **`MasselGUARDcli.exe`** — Console CLI (`OutputType=Exe`, PowerShell/cmd wait for exit)

.NET 10.

## Build

```bat
BUILD.bat          # requires .NET 10 SDK; output → dist\
```

Produces both `dist\MasselGUARD.exe` and `dist\MasselGUARDcli.exe`.

Current version: **3.6.0 — Dangerous Donkey**
When bumping version, update **both** `UpdateChecker.cs` (`CurrentVersion` + `_codenames`) **and** `BUILD.bat` (`VERSION` + `CODENAME`).

## Key design decisions

- **Two exe split** — `MasselGUARD.exe` is `WinExe` so Windows never allocates a console (no flash). `MasselGUARDcli.exe` is `Exe` so terminals wait for it. Source is shared via `<Compile Include>` links from `MasselGUARDcli/MasselGUARDcli.csproj`.
- **`GenerateAssemblyInfo=false` + `GenerateTargetFrameworkAttribute=false`** on `MasselGUARD.csproj` — suppresses duplicate attributes caused by WPF's wpftmp compile step when `OutputType=WinExe`. Version info is still embedded via the wpftmp project's own generated attributes using `-p:` overrides from BUILD.bat.
- **`requireAdministrator` manifest** — UAC always elevates. Non-admin terminals get an isolated console (new window). `IsIsolatedConsole()` via `GetConsoleProcessList` detects this and pauses before exit.
- **`TearDownAdapter`** — after `EnsureStopped`, calls `WireGuardOpenAdapter` + `WireGuardCloseAdapter` to release any lingering kernel adapter. Needed because the WireGuardTunnelService exits but the adapter can outlive it.
- **`IsRunning()`** — checks `ServiceController.Status == Running` first (primary), then the WireGuard pipe as fallback.
- **`RefreshStatus()` resets DNS badge** — when poll detects tunnel going inactive externally (CLI disconnect), resets `_dnsStatus` and fires PropertyChanged for all DNS properties.
- **`KillSwitchService.Disable()`** — early-returns if tunnel not in `_active` HashSet, preventing spurious `[KillSwitch] Disabled` log entries.
- **Auto-reconnect** — `TunnelService._intentionalDisconnects` (ConcurrentDictionary) is populated at the top of every `Disconnect()` call. `MainViewModel.IsIntentionalDrop()` consumes it via `ConsumeIntentionalDisconnect()` before triggering `AutoReconnectAsync()`. This ensures WiFi-rule, CLI, and user disconnects are never retried. `AutoReconnectAsync` retries up to 3 times with 5 s / 10 s / 15 s backoff. Global mode (`AppConfig.AutoReconnectMode`) + per-tunnel flag (`StoredTunnel.AutoReconnect`) resolved by `TunnelService.ShouldAutoReconnect()` — same pattern as kill switch. Because the poll never sees MasselGUARD's own transitions (`DoDisconnect` refreshes `IsActive` immediately), an intentional mark would otherwise go stale — so a successful `Connect()` removes it again. The WireGuard app's deactivate stops the service *before* deleting its SCM entry, so the crash-vs-deactivate check (`WireGuardServiceExists`) at drop time races the deletion; `AutoReconnectAsync` therefore starts with a 2 s grace check that recognises a clean deactivate before announcing any reconnect countdown, and re-checks after each backoff delay in case the user deactivates mid-loop. Reconnect attempts `await vm.ConnectAsync()` (awaitable extraction of `DoConnect`) — firing `ConnectCommand` and reading `IsActive` immediately reported every attempt as failed.
- **External companion connect/disconnect** — when the 1 s poll sees a companion tunnel transition that MasselGUARD didn't initiate (WireGuard app / CLI), `MainViewModel.RefreshTunnelStatus` calls `TunnelService.RecordExternalConnect` (opens history entry, snapshots byte counters, clears stale intentional mark + `UserDisconnected`) or `RecordExternalDisconnect` (closes the open history entry via `LogDisconnect`, which also writes the `Disconnected: <name>` log line). Without this, externally dropped tunnels left history entries open forever and never appeared in the activity log.
- **`UpdateChecker.UpdateAsync`** — takes an `onShutdown` callback so WPF-specific `Application.Current.Dispatcher.Invoke(ShutdownApp)` stays in the GUI call site, keeping `UpdateChecker.cs` WPF-free.
- **File-only tunnel config storage** — `StoredTunnel.Config` (inline DPAPI blob) is legacy. All new saves write a `.conf.dpapi` file to `%APPDATA%\MasselGUARD\tunnels\` and store only the `Path`. `ConfigService.Load()` migrates old inline entries to files automatically on first run and nulls `Config` so it disappears from `config.json`. The `[JsonIgnore(Condition = WhenWritingNull)]` attribute ensures `Config` is never written once cleared.
- **`TunnelService.SaveConfigToFile`** — DPAPI-encrypts plaintext and writes to `TunnelStorageDir`. Used by GUI add/edit, import dialog, QR import, and CLI import/rawconnect. Returns the file path stored in `StoredTunnel.Path`.
- **`ApplyWifiState`** — central method called from both `OnWifiChanged` (WLAN notification) and `TryUpdateWifi` (startup query). Records SSID history and updates all UI. Ensures the current SSID is captured immediately at startup rather than only on WiFi changes.
- **WiFi history `IsOpen`** — `WifiHistoryEntry.IsOpen` is populated from the `bSecurityEnabled` field of `WLAN_CONNECTION_ATTRIBUTES` (offset +576). `true` = no security (open network). Passed from `WiFiService` → `OnWifiChanged` → `RecordSsidConnect`.

## Project structure

```
MasselGUARD.csproj          # WinExe GUI project
Program.cs                  # Entry point: /service dispatch, then GUI
MasselGUARDcli/
  MasselGUARDcli.csproj     # Exe CLI project (links shared source via <Compile Include>)
  Program.cs                # CLI entry point → CliRunner.Run()
Cli/
  CliRunner.cs              # All CLI commands
  CliOutput.cs              # Console output helpers (Info/Ok/Error/PrintJson)
  WireGuardConf.cs          # WireGuard .conf parser/builder
Models/
  AppConfig.cs              # Main config model (serialised to config.json)
  StoredTunnel.cs           # Tunnel definition; Config field is legacy (null after migration)
  ConnectionHistoryEntry.cs
  WifiHistoryEntry.cs       # Ssid, ConnectedAt, DisconnectedAt, IsOpen
Services/
  TunnelService.cs          # Connect/Disconnect orchestration; SaveConfigToFile; TunnelStorageDir
  TunnelDll.cs              # P/Invoke to tunnel.dll + wireguard.dll  (root, not Services/)
  ConfigService.cs          # Load/Save config.json; MigrateInlineConfigsToFiles on load
  HistoryService.cs         # tunnel_history.json + wifi_history.json (with legacy migration)
  KillSwitchService.cs
  LogService.cs
  ScriptService.cs
  WiFiService.cs            # WLAN API via P/Invoke; fires SsidChanged(ssid, isOpen)
ViewModels/
  MainViewModel.cs
  TunnelEntryViewModel.cs   # RefreshStatus() drives 1-second poll updates
Views/
  SettingsWindow.xaml(.cs)
  MainWindow.xaml(.cs)      # ApplyWifiState; ApplyInfoSectionMode; timeline hover/nav
UpdateChecker.cs            # GitHub release check + auto-update (WPF-free)
BUILD.bat
```

## CLI commands

| Command | Notes |
|---|---|
| `list` / `--list` | Supports `--group`, `--active` |
| `status` / `--status` | Active tunnel count |
| `connect <name>` | |
| `connect --default` | Uses `cfg.DefaultTunnel` |
| `connect --all` | Supports `--group` |
| `disconnect <name>` | |
| `disconnect-all` | Supports `--group`; exits 2 when nothing active |
| `info <name>` | Reads HistoryService for uptime/source |
| `log [n]` | Reads `tunnel_history.json`; `--logtype extended` adds source column |
| `check-update` | Async GitHub call; exits 1 when update available |
| `version` / `--version` / `-v` | Shows codename, build stamp, update status |
| `help` / `--help` / `-h` | |

Global flags: `--json`, `--quiet`/`-q`, `--group <name>`, `--active`, `--logtype normal|extended`

Exit codes: `0` success · `1` error · `2` already in desired state

## Data files

| File | Location |
|---|---|
| Config | `%APPDATA%\MasselGUARD\config.json` |
| Tunnel history | `%APPDATA%\MasselGUARD\tunnel_history.json` (migrated from `history.json`) |
| WiFi history | `%APPDATA%\MasselGUARD\wifi_history.json` (migrated from `ssid_history.json`) |
| Tunnel configs | `%APPDATA%\MasselGUARD\tunnels\*.conf.dpapi` (DPAPI encrypted, one file per tunnel) |
| Themes | `<exedir>\theme\<folder>\theme.json` (built-in: grey, highcontrast) + `%APPDATA%\MasselGUARD\themes\` (user) |
| Languages | `<exedir>\lang\*.json` |

## Tunnel sources

- `"local"` — managed by `TunnelDll` (wireguard-NT via `tunnel.dll`)
- anything else — WireGuard for Windows companion (`WireGuardTunnel$<name>` service)

## History & WiFi recording

### Tunnel history (`tunnel_history.json`)
`ConnectionHistoryEntry` fields: `TunnelName`, `ConnectedAt` (UTC), `DisconnectedAt` (UTC, null = still active), `Source`, `SessionRxBytes`, `SessionTxBytes`.

### WiFi history (`wifi_history.json`)
`WifiHistoryEntry` fields: `Ssid`, `ConnectedAt` (UTC), `DisconnectedAt` (UTC, null = still connected), `IsOpen` (true = no encryption / open network).

Recording flow:
- **Startup**: `TryUpdateWifi()` → `ApplyWifiState()` → `RecordSsidConnect(ssid, isOpen)`. The current SSID is captured immediately.
- **SSID change**: `WiFiService.SsidChanged` → `OnWifiChanged` → `ApplyWifiState()` → records new SSID / closes old entry.
- **Disconnect**: `ApplyWifiState(null, false)` → `RecordSsidDisconnect()` closes the open entry.
- **App shutdown**: `App.xaml.cs` calls `RecordSsidDisconnect()` directly to close any open entry.

## Activity timeline

The info panel above the footer renders a canvas with:
- **Tunnel bar** (top, 16 px) — one stacked bar for all tunnels; segments coloured per tunnel.
- **WiFi band** (below tunnel bar, 16 px single row) — all SSIDs combined on one row, each segment coloured per SSID. Rendered only when `ShowWifiInChart && StoreWifiHistory`.
- **Time axis** (bottom, 20 px).

### Hover tooltip (Y-hit tested)
Mouse Y determines primary content:

| Mouse Y | Primary content | Secondary content |
|---|---|---|
| Tunnel bar area | Tunnel name, connected since / duration, live KB/s if near-now | WiFi SSID + time below separator |
| WiFi row area | SSID name (bold), connection time / duration, 🔒 secured or ⚠ open network | Active tunnels below separator |
| Gap / axis | — | — |

Tooltip shows whenever there is content at the hovered X position — including when only WiFi is active and no tunnel is connected.

### `< >` navigation buttons
Cycle through tunnel sessions in the time window. The tooltip for each session shows:
- Tunnel name, time range, duration, traffic (primary)
- WiFi SSID active at that session's midpoint (below separator, if recorded)

### Settings — History page

| Toggle | Config field | Effect |
|---|---|---|
| Capture → Connections | `StoreConnectionHistory` | Writes `tunnel_history.json` |
| Capture → WiFi (SSID) | `StoreWifiHistory` | Writes `wifi_history.json` |
| Show → Connections | `ShowTimeline` | Draw tunnel bars in chart; disabled when Capture Connections is off |
| Show → WiFi (SSID) | `ShowWifiInChart` | Draw WiFi rows in chart; disabled when Capture WiFi is off |

**Panel visibility** is derived in `ApplyInfoSectionMode()`:
```
visible = (ShowTimeline && StoreConnectionHistory)
       || (ShowWifiInChart && StoreWifiHistory)
```
The panel auto-hides when both show-conditions are false. The show-toggles are independent — turning off tunnel capture does not force the WiFi show-toggle off, and vice versa.
