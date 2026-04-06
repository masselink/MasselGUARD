# MasselGUARD — Release Notes

---

## v2.2.1

### Bug fixes

- Orphan warning text overflowed the settings card width — replaced horizontal `StackPanel` with a two-column `Grid` so text wraps within the available space
- OpenWifi tunnel selection was cleared on every tunnel list refresh — `RefreshTunnelDropdowns` now sets `_loading = true` before repopulating the `ComboBox` to prevent `SelectionChanged` from overwriting `config.json`
- Wizard mode status panel showed double ✓ / ⚠ prefix — the symbols are now only prepended by code; removed them from the language strings
- Settings window height increased to accommodate wrapped checkbox text
- Tray toast popup duration increased to 6 seconds; font size increased to 15pt; app icon shown in header

---

## v2.2

### New features

**Setup wizard**
A first-run wizard appears when no `config.json` exists. It walks through language selection, operating mode, and automation mode. Re-runnable at any time from the **⊞ Wizard** button at the bottom of the Settings sidebar. All changes are applied and saved only when **Finish** is clicked; **Skip** discards everything.

**Tabbed Settings window**
Settings redesigned as a fixed-height window with a sidebar:
- **General** — language, app mode (with live dependency status), automation mode
- **Advanced** — WireGuard client, log level, installation, tray notifications, orphaned services
- **About** — update checker, version, GitHub link

All settings (mode, manual mode, log level) are deferred and applied together when **Save** is clicked.

**Tray popup notifications**
A small branded toast appears near the system tray when a tunnel connects or disconnects while the main window is hidden. Shows the tunnel name, the reason (WiFi rule, default behaviour, or open network protection), and fades out after 6 seconds. Toggle in **Settings → Advanced**.

**Open network protection**
A new section in the default-action panel. Select a tunnel to activate automatically whenever the device connects to an open (passwordless) WiFi network. This check runs *before* any SSID rule or default action.

**Orphaned service cleanup**
**Settings → Advanced** shows a "Possible Orphaned Tunnel Services" section. `WireGuardTunnel$` services left behind after a crash or force-quit are listed with their status (stale / tunnel still active in kernel) and can be removed individually or all at once. The app also logs a warning at startup if orphans are found.

**Quick Connect in tunnel list**
An active Quick Connect session now appears as a `⚡ <name>` entry at the top of the tunnel list. Clicking **Disconnect** on it works the same as the status-bar button.

**Delete / Unlink / Remove morphing button**
The tunnel toolbar's action button changes label based on selection:
- **Delete** — local tunnel, file present
- **Remove** — local tunnel, file missing (stale entry)
- **Unlink WireGuard profile** — WireGuard-linked tunnel (removes from list, keeps WG conf)
Button is hidden when nothing is selected.

**Language support**
Added 🇩🇪 German, 🇫🇷 French, and 🇪🇸 Spanish. Dutch title corrected to MasselGUARD. Flag emoji in language picker. Language is selectable in the wizard.

**Manual mode layout**
In manual mode the tunnel list fills the entire left column height. The toolbar stays anchored below the list.

### Improvements

- `SafeName()` — tunnel names with spaces, backslashes, or other invalid characters are sanitised for SCM service names and filenames while the display name is preserved
- Version checker uses the GitHub `/tags` API instead of `/releases/latest`. Running ahead of the latest tag shows a witty message.
- Quick Connect supports `.conf.dpapi` files in addition to `.conf`
- Import from file shows `.conf` and `.conf.dpapi` as separate filter entries for reliable Windows Explorer display
- Import from WireGuard renamed to "Link to WireGuard profile"; hidden in Standalone mode
- Startup prompt "update installed version?" has a "Don't ask again" checkbox and a reset toggle in Settings → Advanced
- Service polling uses a 50 ms loop accepting `Running` or `Stopped`, eliminating the false-positive "cannot find file" Event Viewer error caused by wireguard-NT's fast-exit behaviour
- Temp config file deleted immediately after service creation (not only on disconnect)
- Tray toast shows app icon, larger font (15pt), lasts 6 seconds

### Bug fixes

- Settings tabs (Advanced, About) were not switching due to `Tag` being overwritten for highlight styling
- Manual mode tunnel list did not fill full height — buttons floated in old position
- OpenWifi tunnel selection was cleared when tunnel list was refreshed
- Double ✓ / ⚠ symbols in wizard mode status panel
- `_firstRun` field placed in instance scope caused CS0103 in static `LoadConfig`
- `List<>` / `ToList()` compile errors in `SettingsWindow.xaml.cs` (missing usings)
- `FileStream` 7-argument constructor removed in .NET 6+ — replaced with `File.Create` + `SetAccessControl` pattern

---

## v2.1

- Tunnel configs encrypted with DPAPI (`CurrentUser` scope), stored as `.conf.dpapi`
- Atomic temp file creation: `FileSecurity` applied before first byte is written
- Temp file deleted immediately after service starts (not on disconnect)
- `SvcTempDir` in `<ExeDir>\tunnels\temp\` — collocated with encrypted configs
- Tunnel storage in `<ExeDir>\tunnels\` (Program Files when installed, portable dir otherwise)
- Service name sanitisation (`SafeName`) for spaces and backslashes
- Quick Connect button moved to status bar, right-aligned
- WireGuard Log button visible only when a WireGuard-type tunnel is active
- `suppress portable update prompt` config option with checkbox in Settings
- GitHub tags API for version checking

---

## v2.0 (baseline)

- Standalone mode: WireGuard-NT via `tunnel.dll` + `wireguard.dll`, no WireGuard app required
- Companion mode: automates the official WireGuard for Windows application
- Mixed mode: both simultaneously
- WiFi-triggered auto-switching via `WlanRegisterNotification` (no polling)
- WiFi rules: map SSID → tunnel or SSID → disconnect
- Default action panel: none / disconnect all / activate named tunnel
- Quick Connect: open any `.conf` and connect without importing
- System tray with dark context menu and live tunnel status
- Single-instance guard, admin elevation, UAC manifest
- Dutch and English language support
- Dark theme, frameless WPF, Consolas font
