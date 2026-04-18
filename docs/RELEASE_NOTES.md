# MasselGUARD — Release Notes

All notable changes are documented here. The most recent release is at the top.

---

## v2.3.1

### Features

**Colour-blind themes**
Two new built-in themes using the IBM colour-blind safe palette (distinguishable for deuteranopia, protanopia and tritanopia): `colorblind-dark` and `colorblind-light`. Accent colour: #648FFF (blue), success/warning: #FFB000 (gold), danger/error: #DC267F (magenta).

**Language picker redesign**
The language picker now shows a styled `[XX]` code badge (accent colour, Consolas font) next to the language name — replacing flag emoji which render as plain text pairs (e.g. "GB") in WPF on Windows.

### Bug fixes

- **Right panel empty-state background** — when Default Action or Open Network tabs were active, the button row area below the content had a different background shade than the left panel. Fixed by replacing the `Border`/`Grid` wrapper with a direct `Grid` matching the left panel's structure.
- **Right panel top gap** — 8 px `Margin` on the content grid caused a visible gap between the tab strip and content on the right side. Removed.
- **Shield not showing after logo-theme switch** — switching away from a theme with a `logo` to one without left `HasBuiltinIcon = Collapsed` permanently. Fixed: `ApplyLogo` now always sets `HasBuiltinIcon = Visible` in the no-logo path.
- **`AppIcon` scope** — `appIcon` previously also showed an extra image in the title bar. Clarified: `appIcon` affects only the tray icon and `Window.Icon` (taskbar). The title bar shows `logo` or the built-in shield only.
- **Tunnel name colours not updating on theme switch** — `NameColor` and `TypeColor` did not raise `PropertyChanged` on theme change. `RefreshLabels()` now fires these and `StatusColor`, and is called on all tunnel entries from `UpdateThemeToggleIcon()`.
- **`LogLevelInfo` / `LogLevelVerbose` missing translations** — keys were referenced in Settings but never added to any lang file. Added to all 5 languages.
- **Double code badge in language picker** — `_language` in JSON already contains the ISO code; `LangItem.Display` was prepending it again. Now uses `Name` directly.
- **`CharacterSpacing` MC3072** — WinUI-only property removed from WPF XAML.
- **Bottom button row colour mismatch** — `Border` + `Grid` wrapper was removed; replaced with a plain `Grid` matching the left side.

---

## v2.3.0

### Features

**Tunnel groups and category tabs**
Tunnels can be organised into named groups (Work, Personal, Travel, or any custom group). The tunnel list shows a tab strip: All · group tabs · Uncategorized. Uncategorized is always present and not configurable. Groups are managed in Settings → General → Tunnel Groups (add, rename, reorder ↑↓, delete). Deleting a group moves its tunnels to Uncategorized.

**WireGuard profile metadata editing**
WireGuard-linked tunnels (Companion mode) can now be edited: the Edit button opens a dedicated metadata dialog where group and notes can be set. The `.conf` file owned by WireGuard is never touched.

**Tunnel notes**
Each tunnel has a Notes field (shown as a tooltip on the tunnel name in the list). Set via Edit for both local and WireGuard-linked tunnels.

**Theme system — full customisation**
- Four built-in themes: Default Dark, Default Light, Grey Dark (sharp corners), Grey Light (sharp corners)
- Theme folders renamed: `default` → `default-dark`, `light` → `default-light`
- Themes now hot-swap without restart
- Appearance settings moved to their own **Appearance** tab in Settings
- Dark and light theme pickers filtered by `type` field — dark picker only shows dark themes
- Auto-switching based on Windows system theme preference (5-second poll)
- Theme toggle button in title bar cycles Dark → Light → Auto (⚡)

**Custom window chrome**
Themes control title bar height, whether the icon and app name are shown, resize grip visibility, and overall window opacity via `theme.json` keys.

**Custom tray menu colours**
Themes expose `colorTrayBg`, `colorTrayHover`, `colorTrayText`, `colorTrayBorder`, `colorTrayImageMargin`. The `DarkMenuRenderer` reads these from `Application.Resources` at render time — theme hot-swap affects the tray menu immediately.

**Status bar customisation**
Themes can hide the status bar entirely, control its height, and individually show/hide the WiFi and active tunnel labels.

**Right panel redesign**
- OPTIONS header added above the tab strip (mirrors TUNNELS header on the left)
- Both panels are now equal width (`1*` / `10` / `1*`)
- Tab strip on the right uses the same style as the tunnel category tabs on the left
- Right tab labels pulled from lang files: Activity Log, Rules, Default Action, Open Network
- Rules buttons pinned at the bottom of the right panel, always same height as the left button bar
- Activity Log tab has an **Export Log** button — saves log to a UTF-8 `.txt` file
- Default Action and Open Network tabs have a card background matching the Log and Rules look

**Detailed activity log**
- `SaveConfig` replaced with `SaveConfig(string? changeDescription)` — each save logs exactly what changed: `Saved: Rule added: MasselNET → 1.VPN`, `Saved: Default action: activate`, etc.
- WiFi change logs SSID + security status: `WiFi: HomeNet  🔒 secured` / `⚠ open (no password)`
- Theme changes, language changes, mode changes, and manual mode toggles all logged at Info level
- Log level picker now correctly saves all four values: Normal / Info / Verbose / Debug

**Debug logging**
- Startup summary: OS version, .NET runtime, platform, domain\user
- Before connecting a local tunnel: Interface address, DNS, Endpoint, AllowedIPs, public key prefix, MTU
- Connect and disconnect timing in milliseconds
- WireGuard service name and exe path logged when starting a Companion tunnel
- All debug entries prefixed `[DBG]` and rendered in a distinct muted colour

**Tunnel discovery log suppression**
Discovery is logged once at startup. Subsequent calls to `RefreshTunnelDropdowns` (triggered by saves, imports, tab switches) are silent.

**Language flags**
The language picker now shows country flag emoji before each language name: 🇬🇧 English, 🇳🇱 Dutch, 🇩🇪 German, 🇫🇷 French, 🇪🇸 Spanish. Flags for 13 additional languages pre-mapped for future additions.

**BUILD.bat improvements**
- Step 3 now copies the `theme\` folder into `dist\` after every successful compile
- DLL menu extended to four options: `[1]` Use provided DLLs (new — recommended), `[2]` Build from source, `[3]` Download from GitHub, `[4]` Skip
- `tunnel.dll` and `wireguard.dll` included in the project folder next to `BUILD.bat` so Option 1 works out of the box

**Documentation**
- `docs/MasselGUARD.md` — full internal documentation (startup sequence, WiFi monitoring, rule evaluation, connect/disconnect flows, security model, theme system, logging, troubleshooting)
- `docs/changelog.md` — this file
- `README.md` — streamlined to a feature overview; detail deferred to docs

### Bug fixes

- **Selection lag / missed clicks** — `RefreshTunnelDropdowns` replaced the entire `ListView.ItemsSource` on every call, which WPF processed as a full reset and silently cleared selection. Fixed: selected tunnel name is saved before rebuild, restored by name-match after `ApplyActiveTab` sets the new source.
- **Edit button disabled after save** — consequence of the above; now resolved.
- **`RulesPanel` / `DefaultActionPanel` CS0103** — `ApplyManualMode` referenced old XAML names from the pre-tab era; rewritten to toggle `RTabBtnRules` and `RTabBtnDefault` visibility.
- **`RightTab_Click` CS1061** — handler was declared in XAML but never implemented in code-behind; added with full `ShowRightTab` state machine.
- **Duplicate `SaveConfigPublic`** — two definitions introduced in the same session; duplicate removed.
- **Log level picker saving** — only saved `"normal"` or `"debug"`; now correctly saves all four values.
- **Border two-children MC3089** — bottom button bar Border had two StackPanel children; wrapped in a Grid.
- **Tray toast background** — message body Border had no background, making it transparent; `Background="{DynamicResource Surface}"` added.
- **Appearance tab not opening** — `TabBtn_Click` switch was missing the `"TabBtnAppearance"` case; always routed to General.
- **Folder subtext removed** — theme picker ComboBox ItemTemplate no longer shows the folder name, only the display name.
- **`GetConfigStatic` CS1061** — `App.xaml.cs` resolved `MainWindow` as the `Application.MainWindow` property (type `Window`) instead of the class; qualified as `MasselGUARD.MainWindow.GetConfigStatic()`.

---

## v2.2.1

### Bug fixes

- Orphan warning text overflowed the settings card — replaced horizontal StackPanel with a two-column Grid
- OpenWifi tunnel selection cleared on every tunnel list refresh — `_loading = true` guard added around ComboBox repopulation
- Double ✓ / ⚠ prefix in wizard mode status panel — symbols removed from lang strings (added only in code)
- Settings window height increased for wrapped checkbox text
- Tray toast duration increased to 6 seconds; font size increased to 15 pt; app icon shown in header

---

## v2.2

### Features

**Setup wizard**
First-run wizard covers language, operating mode, and automation mode. Re-runnable from the **⊞ Wizard** button at the bottom of the Settings sidebar. Changes applied on Finish; Skip discards everything.

**Tabbed Settings window**
Sidebar with General / Advanced / About tabs. All settings deferred to Save button.

**Tray popup notifications**
Branded toast near the tray when a tunnel switches while the window is hidden. Shows tunnel name, reason, and fades after 6 seconds. Toggle in Settings → Advanced.

**Open network protection**
Separate section in Default Action. Selected tunnel activates on any open (passwordless) WiFi before rules are evaluated.

**Orphaned service cleanup**
Settings → Advanced shows `WireGuardTunnel$` SCM entries left behind after a crash. List, inspect, and remove them individually or all at once. Startup warning logged if orphans found.

**Quick Connect in tunnel list**
Active Quick Connect session appears as `⚡ <n>` at the top of the tunnel list and can be disconnected from there.

**Delete / Unlink / Remove morphing button**
Tunnel action button label changes based on selection: Delete (local, file present), Remove (local, file missing), Unlink WireGuard profile (Companion-linked tunnel). Hidden when nothing is selected.

**Language support**
Added 🇩🇪 German, 🇫🇷 French, 🇪🇸 Spanish. Flag emoji in language picker.

### Improvements

- `SafeName()` sanitises tunnel names for SCM and filesystem use
- Version checker uses GitHub `/tags` API; running ahead shows a witty message
- Quick Connect supports `.conf.dpapi` files
- Import from file shows `.conf` and `.conf.dpapi` as separate filter entries
- "Link to WireGuard profile" replaces "Import from WireGuard"; hidden in Standalone mode
- "Don't ask again" checkbox on the portable update prompt; reset toggle in Settings → Advanced
- Service polling accepts Running or Stopped (50 ms loop) — eliminates false-positive Event Viewer error from wireguard-NT's fast-exit behaviour
- Temp config deleted immediately after service creation
- Tray toast: app icon, 15 pt font, 6-second display

### Bug fixes

- Settings Advanced/About tabs not switching (Tag overwritten by highlight styling)
- Manual mode tunnel list not filling full height
- OpenWifi selection cleared on tunnel list refresh
- Double ✓ / ⚠ in wizard status panel
- `_firstRun` in wrong scope (CS0103 in static method)
- Missing `List<>` / `ToList()` usings in SettingsWindow
- `FileStream` 7-argument constructor removed in .NET 6+ — replaced with `File.Create` + `SetAccessControl`

---

## v2.1

- DPAPI encryption for tunnel configs (`CurrentUser` scope), stored as `.conf.dpapi`
- Atomic temp file creation: `FileSecurity` applied before first byte is written
- Temp file deleted immediately after service starts
- `SvcTempDir` in `<ExeDir>\tunnels\temp\`
- Tunnel storage in `<ExeDir>\tunnels\`
- `SafeName()` service name sanitisation
- Quick Connect button moved to status bar (right-aligned)
- WireGuard Log button visible only when a WireGuard tunnel is active
- `SuppressPortableUpdatePrompt` config option
- GitHub tags API for version checking

---

## v2.0

Initial release.

- Standalone mode: WireGuard-NT via `tunnel.dll` + `wireguard.dll`
- Companion mode: automates official WireGuard for Windows
- Mixed mode: both simultaneously
- WiFi auto-switching via `WlanRegisterNotification` (no polling)
- WiFi rules: SSID → tunnel or SSID → disconnect
- Default action: none / disconnect all / activate named tunnel
- Quick Connect: open any `.conf` and connect without importing
- System tray: dark context menu, coloured dot when connected
- Single-instance guard, admin elevation, UAC manifest
- 🇬🇧 English and 🇳🇱 Dutch
- Dark theme, frameless WPF window
