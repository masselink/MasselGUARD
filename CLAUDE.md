# MasselGUARD — Codebase Guide

WireGuard tunnel manager for Windows. Two executables:
- **`MasselGUARD.exe`** — WPF GUI application (`OutputType=WinExe`, no console flash)
- **`MasselGUARDcli.exe`** — Console CLI (`OutputType=Exe`, PowerShell/cmd wait for exit)

.NET 10.

## Build

```bat
BUILD.bat          # all arches (x64 + arm64); requires .NET 10 SDK
BUILD.bat x64      # single arch
BUILD.bat arm64
```

Each arch publishes natively (framework-dependent single-file) into `dist\<arch>\` (`MasselGUARD.exe` + `MasselGUARDcli.exe`) and is zipped to `dist\MasselGUARD-<arch>.zip` for release. ARM64 is a genuine cross-publish from an x64 host — the SDK produces a native ARM64 apphost (PE machine `0xAA64`). See **x64 / ARM64 architecture support** below.

Current version: **4.0.0 — Forking Fox**
When bumping version, update **both** `UpdateChecker.cs` (`CurrentVersion` + `_codenames`) **and** `BUILD.bat` (`VERSION` + `CODENAME`).

**On every new public release**, also update the **Scoop bucket** (separate repo `masselink/MasselGUARD-scoop`, local checkout `../MasselGUARD-scoop`): bump `version` and refresh **both** SHA256 hashes (`64bit` + `arm64`) in `bucket/masselguard.json` to match the new `MasselGUARD-x64.zip` / `MasselGUARD-arm64.zip` release assets. The manifest's `checkver: "github"` + per-arch `autoupdate` templates let the Excavator CI (`.github/workflows/excavator.yml`) do this automatically once the release is published, but verify it landed (or run `checkver -u` manually). The bucket builds on @qoreQyaS's original manifest (issue #49) and extends it with ARM64, a CLI `PATH` shim (`bin: MasselGUARDcli.exe`), and a GUI shortcut.

## Key design decisions

- **Two exe split** — `MasselGUARD.exe` is `WinExe` so Windows never allocates a console (no flash). `MasselGUARDcli.exe` is `Exe` so terminals wait for it. Source is shared via `<Compile Include>` links from `MasselGUARDcli/MasselGUARDcli.csproj`.
- **AssemblyInfo uses normal SDK generation** (`GenerateAssemblyInfo` left at its default ON). `BUILD.bat`'s `-p:Version`/`-p:AssemblyVersion`/`-p:InformationalVersion=<ver>.<build>` flow into the assembly; `UpdateChecker.BuildStamp` reads the managed `AssemblyInformationalVersionAttribute` at runtime, and WPF's resource pack-URIs match the real `AssemblyVersion`. `IncludeSourceRevisionInInformationalVersion=false` keeps `+<git-sha>` out of the About-page build stamp.
- **The GUI `.csproj` must exclude the `MasselGUARDcli\**` sub-project from its globs** (`Compile`/`Content`/`EmbeddedResource`/`None` Remove), alongside `deps\**`. Both folders sit under the GUI project directory, so the SDK's `**/*.cs` compile glob reaches into them. For `MasselGUARDcli\` this is doubly important: the CLI shares the GUI's source via one-way `<Compile Include>` *links*, so the GUI must never compile the CLI's own files — and the glob would otherwise pull in `MasselGUARDcli\obj\…\MasselGUARDcli.AssemblyInfo.cs`, whose assembly attributes collide with the GUI's SDK-generated ones and fail a clean build with CS0579 *duplicate attribute*. This only surfaces once the CLI has been built and its `obj\` is populated, so it presents as an intermittent/"sometimes builds" failure — if you see CS0579 on `AssemblyCompany`/`AssemblyVersion`/etc., check the exclusion first. (Do **not** work around it by disabling `GenerateAssemblyInfo`; that ships a 0.0.0.0 assembly and WPF then throws `FileNotFoundException` for its own pack-URIs at startup.) BUILD.bat deletes `obj\`/`bin\` before publishing so stale intermediates can never produce a mixed-version exe.
- **x64 / ARM64 architecture support** — the managed code is arch-portable; the only per-arch pieces are the launcher exe and the two native DLLs (`tunnel.dll`, `wireguard.dll`). A Windows process is one architecture and .NET has no fat binary, so each arch is a **separate native build**, not one universal exe. On Windows-on-ARM the x64 build *cannot* drive local tunnels — the wireguard-NT **kernel driver is native and can't be emulated** — so ARM64 users need the ARM64 build (companion tunnels still work emulated). Mechanics: both `.csproj` declare `<RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>` with a conditional default `RuntimeIdentifier` (IDE → win-x64; `BUILD.bat` overrides via `-p:RuntimeIdentifier`). Native DLLs live in `wireguard-deps\<arch>\`; build them with `tunnelbuild\tunnelbuild.bat <arch>` — ARM64 `tunnel.dll` is a Go+CGO cross-build needing an aarch64 toolchain (llvm-mingw's `aarch64-w64-mingw32-clang`); `get-wireguard-dlls.ps1` PE-verifies each produced DLL's machine type. `UpdateChecker` picks the release asset by `RuntimeInformation.ProcessArchitecture` (`MasselGUARD-<arch>.zip`, falling back to legacy `MasselGUARD.zip` for x64). `TunnelDll.ValidateDlls()` gates on architecture first: `ArchSupportError()` catches the emulated-x64-on-ARM64 case, and a PE machine-type check on each DLL catches a wrong-arch DLL before the P/Invoke throws `BadImageFormatException`. A release needs **both** `MasselGUARD-x64.zip` and `MasselGUARD-arm64.zip` uploaded.
- **`requireAdministrator` manifest** — UAC always elevates. Non-admin terminals get an isolated console (new window). `IsIsolatedConsole()` via `GetConsoleProcessList` detects this and pauses before exit.
- **`TearDownAdapter`** — after `EnsureStopped`, calls `WireGuardOpenAdapter` + `WireGuardCloseAdapter` to release any lingering kernel adapter. Needed because the WireGuardTunnelService exits but the adapter can outlive it.
- **`IsRunning()`** — checks `ServiceController.Status == Running` first (primary), then the WireGuard pipe as fallback.
- **`RefreshStatus()` resets DNS badge** — when poll detects tunnel going inactive externally (CLI disconnect), resets `_dnsStatus` and fires PropertyChanged for all DNS properties.
- **`KillSwitchService.Disable()`** — early-returns if tunnel not in `_active` HashSet, preventing spurious `[KillSwitch] Disabled` log entries.
- **Auto-reconnect** — `TunnelService._intentionalDisconnects` (ConcurrentDictionary) is populated at the top of every `Disconnect()` call. `MainViewModel.IsIntentionalDrop()` consumes it via `ConsumeIntentionalDisconnect()` before triggering `AutoReconnectAsync()`. This ensures WiFi-rule, CLI, and user disconnects are never retried. `AutoReconnectAsync` retries up to 3 times with 5 s / 10 s / 15 s backoff. Global mode (`AppConfig.AutoReconnectMode`) + per-tunnel flag (`StoredTunnel.AutoReconnect`) resolved by `TunnelService.ShouldAutoReconnect()` — same pattern as kill switch. Because the poll never sees MasselGUARD's own transitions (`DoDisconnect` refreshes `IsActive` immediately), an intentional mark would otherwise go stale — so a successful `Connect()` removes it again. The WireGuard app's deactivate stops the service *before* deleting its SCM entry, so the crash-vs-deactivate check (`WireGuardServiceExists`) at drop time races the deletion; `AutoReconnectAsync` therefore starts with a 2 s grace check that recognises a clean deactivate before announcing any reconnect countdown, and re-checks after each backoff delay in case the user deactivates mid-loop. Reconnect attempts `await vm.ConnectAsync()` (awaitable extraction of `DoConnect`) — firing `ConnectCommand` and reading `IsActive` immediately reported every attempt as failed.
- **External companion connect/disconnect** — when the 1 s poll sees a companion tunnel transition that MasselGUARD didn't initiate (WireGuard app / CLI), `MainViewModel.RefreshTunnelStatus` calls `TunnelService.RecordExternalConnect` (opens history entry, snapshots byte counters, clears stale intentional mark + `UserDisconnected`) or `RecordExternalDisconnect` (closes the open history entry via `LogDisconnect`, which also writes the `Disconnected: <name>` log line). Without this, externally dropped tunnels left history entries open forever and never appeared in the activity log.
- **`UpdateChecker.UpdateAsync`** — takes an `onShutdown` callback so WPF-specific `Application.Current.Dispatcher.Invoke(ShutdownApp)` stays in the GUI call site, keeping `UpdateChecker.cs` WPF-free.
- **File-only tunnel config storage** — `StoredTunnel.Config` (inline DPAPI blob) is legacy. All new saves write a `.conf.dpapi` file to `%APPDATA%\MasselGUARD\tunnels\` and store only the `Path`. `ConfigService.Load()` migrates old inline entries to files automatically on first run and nulls `Config` so it disappears from `config.json`. The `[JsonIgnore(Condition = WhenWritingNull)]` attribute ensures `Config` is never written once cleared.
- **Managed preset / unified `.masselguard`** — a `.masselguard` file is a **flat full-settings snapshot** (`PresetService.Build` serialises `PolicyFields`; enums as strings) plus an optional **`Locked`** object (`{blocks:[…], settings:[…]}`). One file, two roles by **location + `Locked`**: **imported** (wizard/`ConfigService.Import` → `PresetService.ApplyAll`) every value applies + stays editable, `Locked` ignored; **next to the exe** only the `Locked` sections/settings are forced + locked (`PresetService.ApplyLocked`), everything else ignored. `ConfigService.ApplyPreset()` (end of `Load()`, both paths) scans `PresetService.FindPresetFile()` for **any `*.masselguard`**, calls `ApplyLocked`, and treats it as a policy only if it locked ≥1 field; `Save()` re-asserts (`_presetObj.ApplyLocked`) so a hand-edited `config.json` can't win; fails open on a bad file. Blocks → fields = `PresetService.Blocks`/`BlockFields`; `IsLocked(field)` drives the UI. `TunnelsLocked` only if a hand-authored `Locked` names `Tunnels` (export never offers it). `SettingsWindow.ApplyPresetLocks()` (last in `ShowTab`) greys locked controls + banner; `MainWindow.ApplyPolicyGating()` + selection handlers gate rule Add/Edit/Delete. A locked theme that isn't installed is fetched via `ThemeDownloadService.DownloadAsync` (`MainWindow.TryInstallPresetThemeAsync`), else system colours. Managed install offers to copy the `.masselguard`. `ConfigService.Export`/`Import` both route through `PresetService` (regular "Export settings" = full snapshot, no `Locked`; "Export as preset" adds the ticked sections). **Soft lock** — user-writable; not a security boundary (hard lock = HKLM/ACL'd ProgramData). `PresetService` is WPF-free (CLI-shared) — **new shared `Services/*.cs` must be added to `MasselGUARDcli.csproj`'s explicit `<Compile Include>` list** (CLI globs Models, not Services).
- **`TunnelService.SaveConfigToFile`** — DPAPI-encrypts plaintext and writes to `TunnelStorageDir`. Used by GUI add/edit, import dialog, QR import, and CLI import/rawconnect. Returns the file path stored in `StoredTunnel.Path`.
- **Directional trusted-network rules** — a `trusted`-kind `TunnelRule` carries `TrustedWhen` (`"untrusted"` = activate when the SSID is NOT on `AppConfig.TrustedNetworks`, the classic protect-on-public / full-tunnel case; `"trusted"` = activate only when it IS on the list, e.g. a split tunnel at home/work). `TunnelRule.TrustedWhenOnList` is the parsed bool. `RuleEngine.EvaluateWifi` step 4 loops **all** enabled trusted rules and a rule matches only on **its** side (`TrustedWhenOnList ? isTrusted : !isTrusted`); first match activates its tunnel (empty `Tunnel` = disconnect), the non-matching side falls through to the Default. So two rules on the one shared list cover both directions and the Default fills any gap (it no longer hardcodes disconnect-on-trusted). `RuleDialog` gained a two-radio direction selector in `TrustedPanel` (`ResultTrustedWhen`; `existingTrustedWhen` ctor arg) and no longer requires a tunnel. The field serialises with the rule, so managed-preset export/import (`PresetService` "Rules") carries it for free. (This rule kind never shipped in a public release — 3.7.1 was last — so there was no migration.)
- **Tunnel export (`TunnelExportService`)** — portable export/import of a single tunnel, WPF-free (shared → **listed in `MasselGUARDcli.csproj`**). Two concerns: (1) **settings passthrough** — the per-tunnel MasselGUARD extras (`Group`/`Notes`/`KillSwitch`/`AutoReconnect`/`RetryCount`/`RetryDelaySec`/`DailyCapMB`/`WeeklyCapMB`/`MonthlyCapMB`/scripts) render as **readable `# MasselGUARD-<Key>: <value>` comment lines** (WireGuard ignores them, so the `.conf` stays universally importable); a value with a newline/edge-whitespace (embedded multi-line scripts, notes) is base64-wrapped and its key gains a `!b64` suffix. `BuildSettingsBlock`/`Emit` write them, `ParseImportText`/`ApplyKey` read them back and strip them from the config (it also still accepts the legacy `# MasselGUARD-Settings: <base64-json>` one-liner). The tunnel editor's **Raw** tab is a full editable view: `TabRaw_GotFocus` renders config+settings via `BuildExportText(config, DialogSettings())`, and save-from-Raw calls `ParseImportText` → applies parsed settings back onto the form controls (`ApplySettingsToControls`; absent key ⇒ unchanged, globally-controlled toggles not overridden) and stores only the clean config. (2) **Password encryption** — AES-256-GCM with a PBKDF2-SHA256 key (container: magic `MGENC1` + version + salt + nonce + tag + ciphertext, `.mgconf` extension). Deliberately **not** DPAPI: DPAPI is machine/user-bound and can't be shared; this is portable. GUI: toolbar **Export** button after *Defaults* (`ExportTunnel_Click` → `TunnelExportWindow`, radio plain/encrypted/QR + "include settings" checkbox greyed for QR); QR delegates to the existing `QrExportWindow` (standard-only, no settings). Import side: `ImportTunnelDialog` recognises `.mgconf` (prompts via `PasswordPromptWindow`) and restores bundled settings through an extended `TunnelImported` event (5th arg `TunnelExportService.TunnelSettings?`); CLI `import` gains `--password`/`--group` and the same restore. A global `"always"` kill-switch/auto-reconnect overrides any imported per-tunnel flag. **Policy-gated**: export is disabled when `ConfigSvc.TunnelsLocked` (a locked/kiosk install must not exfiltrate private keys).
- **Data-usage warnings (day/week/month)** — per-tunnel data caps that **warn by default and can optionally enforce** (Kill at cap disconnects — see **Enforcement** below). Per-tunnel `StoredTunnel.DailyCapMB`/`WeeklyCapMB`/`MonthlyCapMB` (0 = off), edited in the tunnel dialogs' *DATA-USAGE WARNINGS* section. The `MainViewModel` stats poll computes each period's usage = `HistoryService.GetUsageInRange` (closed sessions whose `ConnectedAt` is in the UTC calendar day/week[Mon-start]/month) + the live `SessionBytes`, pushes them to the row via `TunnelEntryViewModel.UpdateUsage`, and calls `MaybeWarnDataCaps`→`WarnCap` which fires a one-time log line + tray toast per period (dedupe key encodes the period so it re-arms at the next boundary). `IsOverCap` (over ANY configured cap) drives the amber usage colour and the row highlight (`CapHighlightBrush`/`CapHighlightVisibility` → a subtle tinted `Border` in the tunnel `ListView` item template). **Row usage rings** — the connected row renders three concentric arcs via the `Views.CapRings` FrameworkElement (custom `OnRender`): day (inner) / week (middle) / month (outer), each drawn only when its cap is set and filling 0→360° as usage→cap (amber ≥85%, red ≥100%). VM feeds it `DayCapFraction`/`WeekCapFraction`/`MonthCapFraction` + `DayCapSet`/`WeekCapSet`/`MonthCapSet`, gated by `CapRingsVisibility` (active + any cap set) with `CapRingsTooltip` (immediate, `ToolTipService.InitialShowDelay=0`). The old per-period *Show in row* toggle (`StoredTunnel.*CapShow`) and the `CapRemaining*` "% left" chips were removed. A ring shows whenever its cap is set **unless** its per-period hide flag is on — `StoredTunnel.DailyCapHideRing`/`WeeklyCapHideRing`/`MonthlyCapHideRing` (a "Hide ring" checkbox before "Kill at cap" in each period row; local UI pref, NOT exported); these fold into `DayCapSet`/`WeekCapSet`/`MonthCapSet` so a hidden period simply isn't drawn. **Bars vs rings** — `AppConfig.CapIndicatorStyle` (**"bars" default** | "rings", Settings → Tunnels → Display) picks the row indicator: `Views.CapBars` (slim stacked horizontal progress bars, no inline text — breakdown on hover) or `Views.CapRings` (compact concentric arcs). Same DP interface, drop-in. Both controls sit in the row template gated by `CapRingsVisibility`/`CapBarsVisibility` (style + `AnyRingShown`); bars-style sets a fixed row height via the `ListViewItem` `MinHeight="{Binding RowMinHeight}"` so rows stay uniform. Changing the style rebuilds the list. The three caps round-trip through `TunnelExportService.TunnelSettings`. **Chart view** — the bottom info panel has a `Timeline ⇄ Data usage` switch (`AppConfig.InfoPanelMode` "timeline"/"usage"; `RenderChart()` dispatches to `RenderChartCore` or `RenderUsageChart`). `RenderUsageChart` buckets history bytes over the shared `InfoTimeRangeDays` range (24h→hourly, 7d/31d→daily; bytes attributed to a session's `ConnectedAt` bucket + live session in the current bucket) and draws a per-tunnel **line chart with dots** (scaled to the largest single-tunnel bucket value, not stacked) reusing the timeline palette/legend; the range doubles as the cap period, so a **red limit-marker ring** (`Danger`) is drawn on the bucket where a tunnel's cumulative range usage first reaches its day/week/month cap (the line then drops if killed or continues if overruled). The legend is name + colour swatch only (no usage figure, no amber-over colour). `UsageHover` gives the per-bucket breakdown; the ◀▶ session-nav is hidden in usage mode (`SetNavButtonsVisible`). **Enforcement (kill at cap)** — per-period `StoredTunnel.DailyCapKill`/`WeeklyCapKill`/`MonthlyCapKill` (a "Kill at cap" checkbox next to the period's threshold). `MainViewModel.CapKillState(vm)` returns the first over-budget, kill-enabled, non-overridden period (usage recomputed fresh from history so it works for inactive tunnels too; instance keys `yyyy-MM-dd`/ISO-week/`yyyy-MM`); `CapKillCore(respectOverride)` backs both it and `CapOverBudget` (ignores the override, for diagnostics). The poll's `MaybeKillOnCap` disconnects an active tunnel that crosses the line **every poll** (so a companion tunnel re-activated externally by WireGuard-for-Windows is re-killed), calls `RefreshStatus()` to reflect the drop, and sets `TunnelEntryViewModel.CapKilled` (🛑 row marker, cleared on the next connect); the warn/log + sticky toast are deduped once per period via `_capKilled`. Over a kill-cap but overridden → a one-time info log (`_capIgnoredLogged`). Editing a tunnel's caps calls `MainViewModel.ResetCapEnforcement(name)` (from `OnEditTunnel`) to clear that tunnel's `_capOverridden`/`_capKilled`/`_capIgnoredLogged` so the new limits re-arm. Intentional disconnect → no auto-reconnect; `AutoReconnectAsync` also aborts when `CapKillState != null`. Connecting over a reached cap is gated: **manual** (`TunnelToggle_Click`) shows `ShowThemedYesNo`; **auto** (`ApplyRuleResult`, refactored so the activate body is `ActivateTarget`) shows an **interactive toast** (`ShowCapConnectToast` → `ToastWindow` with `Interactive`/`OnConfirm`/`OnCancel`, no auto-dismiss, 120 s safety timeout = Cancel). Both call `OverrideCap(vm)` on "connect anyway", which adds the period to `_capOverridden` so it isn't re-killed/re-prompted until the period resets. Kill flags are NOT exported (local enforcement prefs). The usage view is taller than the timeline, so `ApplyUsageWindowHeight` grows the window by `UsageExtraHeight` while it's shown (and shrinks back) so the tunnel list keeps its size.
- **`ApplyWifiState`** — central method called from both `OnWifiChanged` (WLAN notification) and `TryUpdateWifi` (startup query). Records SSID history and updates all UI. Ensures the current SSID is captured immediately at startup rather than only on WiFi changes.
- **WiFi history `IsOpen`** — `WifiHistoryEntry.IsOpen` is populated from the `bSecurityEnabled` field of `WLAN_CONNECTION_ATTRIBUTES` (offset +576). `true` = no security (open network). Passed from `WiFiService` → `OnWifiChanged` → `RecordSsidConnect`.
- **Split tunneling (route/IP-based, 4.0.0)** — full design in [`docs/SplitTunneling-Design.md`](docs/SplitTunneling-Design.md); technical summary in `docs/Reference.md` §43. Because MasselGUARD never touches the route table (`tunnel.dll` programs routes from `AllowedIPs`), split is a **pure `AllowedIPs` rewrite**: `StoredTunnel.SplitMode`/`SplitRanges`/`SplitApps` → `Models.SplitConfig` → `RouteBasedBackend.ApplyToConfig` (using `CidrMath.ComputeEffectiveAllowedIPs`) patches the plaintext `.conf` in `TunnelService.Connect` **after decrypt, before validation** (fail-safe: original config on error). `exclude` mode adds kill-switch bypass allow-rules (`KillSwitchService.Enable`'s `bypassRanges`) so excluded traffic still reaches the physical NIC. Editable via the **Split** tab in `TunnelConfigDialog` (local tunnels only); round-trips through `TunnelExportService` (`SplitMode`/`SplitRanges` comment lines) and surfaces in CLI `info`. `SplitApps` is a reserved, inert placeholder for a future WinDivert per-app backend (`ISplitTunnelBackend` is the seam). **`CidrMath.cs` + `SplitTunnelBackend.cs` are shared — listed in `MasselGUARDcli.csproj`.** Verified by `MasselGUARDcli selftest` (CIDR math + conf rewrite + export round-trip).
- **CLI elevation** — `MasselGUARDcli` requires admin, but the informational commands `help`/`version`/`selftest` (+ aliases) bypass the gate in `MasselGUARDcli/Program.cs` (`IsNonElevatedCommand`) so they run in any terminal. Anything touching the driver/service still elevates.

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
  TunnelRule.cs             # Automation rule: Kind = wifi | schedule | trusted; Enabled flag
  SplitConfig.cs            # Split-tunnel DTO (From(StoredTunnel)); WPF-free, CLI-shared via Models glob
  ConnectionHistoryEntry.cs
  WifiHistoryEntry.cs       # Ssid, ConnectedAt, DisconnectedAt, IsOpen
Services/
  TunnelService.cs          # Connect/Disconnect orchestration; SaveConfigToFile; TunnelStorageDir; split rewrite in Connect
  CidrMath.cs               # Effective-AllowedIPs CIDR set math (exclude/include). Add to CLI csproj!
  SplitTunnelBackend.cs     # ISplitTunnelBackend + RouteBasedBackend (conf rewrite + KS bypass). Add to CLI csproj!
  TunnelDll.cs              # P/Invoke to tunnel.dll + wireguard.dll  (root, not Services/)
  ConfigService.cs          # Load/Save config.json; MigrateInlineConfigsToFiles + ApplyPreset on load
  PresetService.cs          # Managed preset: Load/Apply (force+lock) + Build/Save (export). Add to CLI csproj!
  RuleEngine.cs             # WiFi/schedule/trusted rule precedence (EvaluateWifi/EvaluateSchedules); trusted rules are directional (TunnelRule.TrustedWhen)
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
| Themes | **All non-System themes live together** in `%APPDATA%\MasselGUARD\themes\<folder>\theme.json` — both downloaded (Theme Browser, from the shared-themes repo `AppConfig.DefaultSharedThemesRepoUrl` = github.com/masselink/MasselGUARD-themes) and user-made (Theme Builder). No themes are bundled with the app. `ThemeManager.{ThemeRoot, SharedThemeRoot, UserThemeRoot}` all point at this one folder; discovered by `ThemeManager.ThemeNames()`. `ThemeManager.ConsolidateThemeFolders` merges any old `custom_themes\` / `shared-themes\` / `shared_themes\` into `themes\` at startup (keeps existing on name collision). Create/Duplicate/Import block overwriting an existing name (`ThemeManager.ThemeExists`). Only the virtual `__system__` (Windows colours) theme is embedded in code and read-only (`IsBuiltinTheme`); **every theme in `themes\` is editable + deletable**. Builder lists: Built-in (System) + THEMES (all, editable). Theme engine mechanics (discovery, load/resolution, HSL auto-invert): `docs/Reference.md` §16. Theme *format* — full field/manifest reference and copy-paste template — lives entirely in the [MasselGUARD-themes repo](https://github.com/masselink/MasselGUARD-themes)'s `THEME_EXAMPLE.md` (sibling checkout at `../MasselGUARD-themes`); nothing theme-authoring-related is duplicated here. |
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
