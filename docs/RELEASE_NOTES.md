# MasselGUARD — Release Notes

---

## v2.5.0

### Settings panel — 6 sidebar tabs

The Settings window is now wider (700 px) and has 6 dedicated tabs:

| Tab | Contents |
|---|---|
| General | Language, app mode, tunnel groups |
| Appearance | Theme pickers, auto-switch, background notifications |
| Default Action | WiFi fallback action (none / disconnect / activate + tunnel) |
| WiFi Rules | Disable WiFi rules toggle, SSID rules |
| Advanced | Install, DLLs, WireGuard, orphans, import/export, log level |
| About | Version, update checker |

Rules, Default Action, and Open Network were previously tabs in the main window's right panel. They now live in Settings. The right panel is a clean Activity Log only.

### Save behaviour

Rules changes (add/edit/delete) are deferred — nothing is written to disk until you press the **Save** button on the WiFi Rules page. Default Action and other settings save immediately on change as before.

### Import / Export settings

Export writes a `.masselguard` JSON file containing rules, groups, mode, themes, and automation settings. Tunnel configs and DPAPI material are never exported. Import reads the file back using a version-tolerant `JsonDocument` parser:

- An export warning explains that compatibility with future versions is not guaranteed
- On import, the file's `AppVersion` is compared to the running version — a mismatch warning (Yes/No) is shown for both older-to-newer and newer-to-older imports
- Unknown or future fields are silently ignored, so old exports remain loadable in new versions

### Activity log improvements

- Header now correctly shows "Activity Log" (was showing the raw key name `[RTabLog]` after a cleanup pass removed the key from the lang files)
- `RichTextBox.Background` set to `Transparent` so the parent `Border`'s corner radius renders correctly at all themes
- `SaveConfig(desc)` always logs at `Ok` level in Normal mode. `SaveConfig()` with no description logs at Debug (Extended only) — covers background saves without spamming Normal mode
- Double-save on Default Action tab open fixed: `DefaultTunnelBox` population now wrapped in `_loading` guard so `SelectionChanged` does not fire and trigger a spurious save when the tab opens

### Dialog buttons

Both the WiFi rule and tunnel dialogs now use **Cancel / OK** instead of Cancel / Save, with consistent vertical padding on the button row.

### Info blocks

Each settings section now has a styled info block (Surface background, border, rounded) with a plain-text explanation above the interactive content.

### Wizard updated

The setup wizard grows from 5 to 6 steps:

| Step | Content |
|---|---|
| 0 | Welcome |
| 1 | Language (with [XX] code badge picker matching Settings) |
| 2 | Operating mode |
| 3 | Disable WiFi rules toggle |
| 4 | Rules & Automation overview (WiFi Rules, Default Action explained) |
| 5 | Done + About card (version, update check) |

Language picker fixed: was showing "NL Nederlands" due to `DisplayMemberPath` bypassing the `DataTemplate` and falling back to `ToString()` which includes the code prefix. Now uses the same `DataTemplate` as Settings with the accent-coloured badge.

### Single-instance mutex fix

After installing MasselGUARD to a new location and relaunching, the app would show "already running" even after the previous instance had fully closed. Root cause: the named mutex (`Global\MasselGUARD_SingleInstance`) was orphaned — the OS holds it briefly after process exit. Fix: when mutex acquisition fails, the app now checks whether an actual MasselGUARD process is running (by process name, excluding self). If no real process is found, it retries up to 4 × 500 ms before giving up. Only shows the "already running" dialog if both the mutex is held AND a real process exists.

### Code cleanup

- `WriteDebug()` no-op calls and method removed from `TunnelDll.cs`
- Dead right-panel tab infrastructure removed from `MainWindow.xaml.cs` (`ShowRightTab`, `RightTab_Click`, `_activeRightTab`, `ApplyManualMode` stale calls)
- 10 dead lang keys removed from all 5 language files
- `MainWindow._loading` field removed (handlers moved to `SettingsWindow` which has its own `_loading`)
- Telemetry opt-out (`DOTNET_CLI_TELEMETRY_OPTOUT=1`, `DOTNET_NOLOGO=1`) added to `BUILD.bat` and `tunnelbuild\tunnelbuild.bat`
- Excessive blank lines normalised

### Bug fixes

- `[RTabLog]` shown as literal text in activity log header — key was deleted during cleanup but still referenced in XAML. Replaced with `ActivityLogHeader`.
- Double "Settings saved" log entry — `DefaultTunnelBox_SelectionChanged` set `ActionActivate.IsChecked = true` which fired `DefaultAction_Changed` → second save. Fixed with `_loading` guard around the `IsChecked` assignment.
- Log area corner radius not respected — `RichTextBox` was drawing its own opaque background over the parent `Border`'s rounded corners. Fixed by `Background="Transparent"`.
- XAML `MC3000` errors from orphaned LAN controls left after removal passes. Resolved.
- `SettingsWindow.xaml` `MC3000` double comment `--` in XAML comment. Fixed.
- Wizard language picker showing `[XX] Name` — fixed by applying `DataTemplate` and removing `DisplayMemberPath`.

---

## v2.3.1

- Language badge picker redesign: `[XX]` code badges replace flag emoji (WPF cannot render emoji flags)
- Shield visibility fix after theme switch
- Tunnel name colours not updating on theme change — `RefreshLabels()` now fires `NameColor`, `TypeColor`, `StatusColor`
- Various XAML and build error fixes

---

## v2.3.0

- Tunnel groups and category tabs
- WireGuard profile metadata editing (group, notes) without touching `.conf` files
- Tunnel notes with tooltip display
- Theme system: 4 built-in themes, hot-swap, auto dark/light switching
- Detailed activity log with Extended verbosity
- BUILD.bat improvements

---

## v2.2.1

- Tray toast duration and font size increased
- Various Settings window sizing fixes

---

## v2.2

- Setup wizard (first-run and re-runnable)
- Tabbed Settings window
- Tray popup notifications with reason context
- Open network protection
- Orphaned service cleanup
- Quick Connect
- German, French, Spanish language support

---

## v2.1

- DPAPI encryption for tunnel configs
- Atomic temp file creation with FileSecurity

---

## v2.0

Initial release. Standalone, Companion and Mixed modes. WiFi auto-switching via `WlanRegisterNotification`. WiFi rules, default action, Quick Connect, system tray, single-instance guard, English and Dutch.
