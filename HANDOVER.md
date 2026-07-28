# MasselGUARD — Session Handover

**Project:** MasselGUARD — WireGuard tunnel manager for Windows (.NET 10; WinExe GUI + `MasselGUARDcli.exe` console).
**Current version:** **3.8.0 — Handy Hedgehog** (automation + managed-deployment release; user-facing notes in `docs/WHATSNEW.md`).
**Branch:** `dev`
**Last updated:** 2026-07-23

> ⚠️ **Build status: NOT build-verified by the assistant.** Everything below is code-complete but the user runs `BUILD.bat` and manual-tests. Two cross-project traps surfaced this cycle and were fixed — keep them in mind if a build error appears:
> 1. **Models are shared with the CLI and must be WPF-free.** `System.Windows.*` in a `Models/*.cs` breaks the CLI build (hit it with `TunnelRule` using `System.Windows.Visibility`).
> 2. **The CLI lists shared `Services/*.cs` explicitly** in `MasselGUARDcli/MasselGUARDcli.csproj` (it does *not* glob Services; it *does* glob Models). A new shared Service must be added there (hit it with `PresetService.cs` → CS0103 in the CLI only). The GUI globs `**/*.cs`, so it never notices.

Version bumped in `UpdateChecker.cs` (`CurrentVersion` + `_codenames`) and `BUILD.bat` (`VERSION` + `CODENAME`). CLAUDE.md header still says 3.7.1 — update if you want.

---

## What shipped in 3.8.0 (this cycle)

### 1. WiFi rules — new trigger types + on/off + polish
- **`TunnelRule.Kind`** = `"wifi"` | `"schedule"` | `"trusted"` (default wifi; old rules deserialize as wifi). Added `StartTime`/`EndTime`/`Days` (schedule) and `Enabled` (default true) + display helpers (`ScheduleSummary`, `SsidDisplay` with 📶/⏰/🛡 icons, `DisabledIcon` = `"⊘ "`, `RowOpacity`).
- **`RuleEngine`** precedence (`EvaluateWifi`): ManualMode → open-network → **enabled wifi SSID rule** → **enabled trusted rule** (untrusted→connect, trusted→disconnect) → default action. Schedules run on their own timer via `EvaluateSchedules` (called from `MainViewModel.CheckSchedules`, throttled to minute boundaries). Disabled rules skipped everywhere.
- **Trusted networks** driven entirely by the `trusted`-kind rule (its existence + tunnel = enabled). Safe-SSID list lives in `AppConfig.TrustedNetworks`, edited in Settings → WiFi (+ "Add current WiFi network" button).
- **Enable/Disable** via a button below each rules list (label follows selection); disabled rows grey out + `⊘`. Both the main-window panel and Settings list.
- **`RuleDialog`** got a WiFi/Schedule/Trusted **trigger-type selector** + day/time controls + a trusted info line.
- **Fixed:** main-window `WifiRuleRow` now carries the actual `TunnelRule` reference; edit/delete operate on it (was matching by SSID → broke trusted/schedule). Add supports all kinds.

### 2. Managed preset — `.masselguard` policy (headline)
- **One unified `.masselguard`** file = flat full-settings snapshot (`PresetService.PolicyFields`, enums as strings) + optional **`Locked`** object `{ blocks:[…], settings:[…] }` + optional `PolicyName`.
- **Two roles by location:** imported (`ConfigService.Import` → `PresetService.ApplyAll`) = apply all, editable; **any `*.masselguard` next to the exe** (`PresetService.FindPresetFile`) → `ApplyLocked` forces + locks only the `Locked` items, everything else ignored. Not a policy if it locks nothing.
- **Enforcement:** `ConfigService.ApplyPreset()` at end of `Load()` (both paths); `Save()` re-asserts (`_presetObj`). Fails open on a bad file. `IsLocked(field)` / `TunnelsLocked` / `PolicyName` / `HasManagedPreset` drive the UI. Regular `Export`/`Import` also route through `PresetService` (so plain "Export settings" is now a full snapshot).
- **UI lock:** `SettingsWindow.ApplyPresetLocks()` (runs last in `ShowTab`) greys locked controls + 🔒 tooltip + shows the banner (`ManagedBanner`). `MainWindow.ApplyPolicyGating()` + selection handlers gate rule/tunnel Add/Edit/Delete.
- **Export dialog** (`Views/ExportPresetWindow.cs`): grouped **per-setting** checklist (section header selects all its items) + policy name → writes `Locked.settings`. Button lives in **Settings → Advanced → Import/Export** (next to Export/Import).
- **Themes:** a locked theme not installed → `MainWindow.TryInstallPresetThemeAsync` fetches from the shared-themes repo (`ThemeDownloadService.DownloadAsync`), else system colours.
- **Install:** managed install offers to copy the `.masselguard` into the install folder.

### 3. Live tunnel health
`TunnelEntryViewModel` derives `TunnelHealth` (Healthy/Idle/Down) from adapter up-state + traffic movement in `UpdateStats`; shown as ●/▲ next to status in `MainWindow.xaml`. (Honest note in code: a real WireGuard handshake age would need pipe IPC we don't do.)

### 4. Data usage & caps
`HistoryService.GetMonthlyUsage` aggregates the Rx/Tx already recorded. `StoredTunnel.MonthlyCapMB` (edited in both tunnel dialogs). `MainViewModel` poll pushes month-to-date to the row (`MonthlyUsageDisplay`) and fires a once-per-month cap warning (`MaybeWarnDataCap`).

### 5. QR export
Right-click a **local** tunnel → "Show QR code" → `Views/QrExportWindow.cs` (ZXing `BarcodeWriter`, Save-PNG, private-key warning). Context menu added to the tunnel row template.

### 6. Fixes
- **DNS-leak inline icon** now hidden when prevention is active (`_dnsMitigated` computed once per poll, passed to both the icon and the toast). Matches the toast/log suppression.
- **Config validation** — "Skip config validation" toggle restored on Settings → Tunnels (off by default = validating); View presets re-assert it on. Per-tunnel skip removed (`AppConfig.SkipTunnelValidation` is the only bypass).

### Localization
All new UI strings translated across **en/nl/de/fr/es/ja** (**465 keys each**, JSON-validated). Files are edited via targeted regex (no BOM, Japanese kept as real characters, key order preserved) — see the pattern used this session if you add keys.

---

## Key files touched
- `Models/`: `TunnelRule.cs` (kinds/schedule/enabled), `StoredTunnel.cs` (MonthlyCapMB), `AppConfig.cs` (TrustedNetworks; SkipTunnelValidation kept), `Preset.cs` (**now an empty placeholder — safe to delete from the project**).
- `Services/`: `RuleEngine.cs`, `ConfigService.cs`, `PresetService.cs` (**new; in CLI csproj**), `HistoryService.cs`, `TunnelService.cs` (validation).
- `ViewModels/`: `MainViewModel.cs` (scheduler, cap warn, HistoryService injected), `TunnelEntryViewModel.cs` (health, usage, dns-mitigated).
- `Views/`: `SettingsWindow.xaml(.cs)` (lots — rules, trusted, validation, preset, ApplyPresetLocks, banner), `MainWindow.xaml(.cs)` (rules panel, health dot, usage, QR menu, policy gating, theme install, install copy), `RuleDialog.xaml(.cs)`, `TunnelConfigDialog`/`TunnelMetadataDialog` (cap), `QrExportWindow.cs` (new), `ExportPresetWindow.cs` (new).
- `MasselGUARDcli/MasselGUARDcli.csproj` (added `PresetService.cs`).
- `lang/*.json` (6), `docs/MANUAL.md`, `docs/WHATSNEW.md`, `README.md`, `CLAUDE.md`, `UpdateChecker.cs`, `BUILD.bat`.

---

## ⏭ Follow-ups / known gaps (none blocking)
1. **`Models/Preset.cs`** is an empty comment file (couldn't delete via tooling) — remove it from the project when you next touch the csproj.
2. **Unused lang keys** left behind: `SettingsPresetSection`, `SettingsPresetInfo` (section removed), `PresetExportNeedBlock` (reused as the "pick ≥1" message — actually still used). Prune the first two if you like.
3. **`ApplyPresetLocks` control map** covers the common Settings controls incl. notifications/display. A locked setting with **no** mapped control is still *forced* (enforced) but won't visually grey — a couple of niche `PolicyFields` (`DefaultGroup`, `HideEmptyGroups`) aren't in the export dialog either. Add mappings/dialog rows if you want them surfaced.
4. **Theme auto-install** uses `DownloadAsync` (fetches the whole repo). Could be narrowed to a single theme via the manifest APIs (`FetchManifestAsync` + `InstallThemeAsync`) if the bulk download is undesirable.
5. **`TunnelsLocked`** only triggers if a *hand-authored* preset lists `Tunnels` in `Locked` — the export never offers tunnels, so the tunnel Add/Edit/Delete gating is effectively dormant by default. Intentional (tunnels are per-site).
6. **CLAUDE.md version header** still reads 3.7.1.

---

## Working agreements (from this project)
- **User runs `BUILD.bat`** and manual-tests; the assistant does **not** auto-build (per `memory/no-auto-builds.md`).
- When bumping version, update **both** `UpdateChecker.cs` and `BUILD.bat` (see CLAUDE.md).
- Keep all six `lang/*.json` complete (translate new keys, don't leave English placeholders) — the user asked for this explicitly.
- New shared `Services/*.cs` → add to the CLI csproj; keep `Models/*.cs` WPF-free.
