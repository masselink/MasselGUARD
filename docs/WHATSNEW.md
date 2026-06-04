## v3.6.0 — Dangerous Donkey

*Coming soon.*

---

## v3.5.0 — Hypersonic Quokka

### Activity timeline

- A canvas panel above the footer showing tunnel and WiFi activity over the last 24 h, 7 d, or 31 d.
- **Tunnel bar** — one stacked bar for all tunnels; each session is a coloured segment per tunnel.
- **WiFi band** — one row per distinct SSID in the time window, coloured per SSID. Shown only when WiFi capture and Show WiFi are both on.
- Time axis with tick marks and timestamps.
- **Hover tooltip** — at any X position shows everything active at that time:
  - Tunnel: name, connected-since / range, duration, live KB/s when near now.
  - WiFi: SSID, time, duration, 🔒 secured / ⚠ open.
- **`< >` navigation** — step through tunnel sessions; tooltip pins to each midpoint and shows the active WiFi SSID.
- Panel auto-hides when both Show toggles are off.

### Settings — History

- New dedicated tab for controlling what is recorded and displayed.
- **Capture** (independent): Connections → `tunnel_history.json` · WiFi (SSID) → `wifi_history.json`
- **Show** (independent, disabled when capture is off): Tunnel connections · WiFi (SSID)
- Time range pill: Last 24 hours · Last 7 days · Last 31 days

### Tunnel config file storage

- Configs are now stored as individual DPAPI-encrypted `.conf.dpapi` files in `%APPDATA%\MasselGUARD\tunnels\`.
- `config.json` stores only the file path — no key material.
- Existing inline-encrypted entries are migrated automatically on first launch.

### CLI — new commands

- `connect --all` — connect all tunnels at once (optionally scoped with `--group <name>`).
- `info <name>` — type, group, uptime, last connected timestamp and trigger source.
- `log [n]` — last *n* activity log entries (default 20). Reads `tunnel_history.json` — no duplication.
- `check-update` — live check against GitHub; exit code 1 when update available.

### CLI — new flags

- `--group <name>` — scope `list` / `connect --all` / `disconnect-all` to one group.
- `--active` — filter `list` to connected tunnels only.
- `--logtype normal|extended` — log detail level.

---

## v3.3.0 — Camouflaged Koala

### Release codenames

- Each version now has a codename shown in the About page, CLI `version` output, and BUILD.bat banner.

### About page — version block

- Version label shows the full product name, version, and codename on one line with build stamp and author below.

### CLI improvements

- `version` output matches the About page format and includes author, website, and cached update status.
- Added `--list` alias for `list` and `--status` alias for `status`.
- `--json` output for `version` now includes an `update_status` field.

### Version and build number separated

- Version (Major.Minor.Patch) is now static in source — BUILD.bat no longer modifies `UpdateChecker.cs`.
- The time-based build stamp (YYMMDDHHMM) is injected at compile time via MSBuild's `InformationalVersion` property.
- In IDE / Debug builds without BUILD.bat the build line is hidden.

### Bug fixes

- DNS badge (🔒 DNS / ⚠ DNS) now disappears immediately when a tunnel is disconnected via CLI or any external trigger.

---

## v3.2.5

### Update available badge

- A ↑ button in Accent colour appears in the title bar whenever a newer version is available.
- Clicking it opens Settings → About directly.
- Badge appears immediately on startup if a newer version was already known; disappears once up to date.

### Bug fixes

- Update check frequency (On start / Daily / Weekly / Manual) is now correctly saved when pressing Save in Settings → About.

---

## v3.2.0

### Kill switch

- **Per-tunnel kill switch** — blocks all non-tunnel outbound traffic via Windows Firewall when a tunnel is active.
- **Global "Always" mode** — forced on for every tunnel; the per-tunnel toggle is hidden.
- Firewall rules use the `MasselGUARD_KS_` prefix and are removed cleanly on exit; stale rules from a crash are cleaned up at startup.
- Toggle in the tunnel edit dialog; global mode in Settings → Advanced.

### Activity log (Extended mode)

- A grey continuation line under each disconnect entry showing session duration and bandwidth (↑ sent / ↓ received).

### Settings — history table

- Completely rewritten with custom header bar and DataTemplate rows.
- Hover and selection use theme `ListHover` / `ListSelected` colours — no Aero highlight.
- Hover tooltip shows full connected-at timestamp, duration, and trigger.

### Settings — WiFi rules table

- Rules table matches the main window: five columns (Name | SSID | Action | Hits | Tunnel).

### Settings — Import / Export

- Export and import confirmation dialogs are fully themed.
- After a successful import a themed prompt offers to restart immediately.
- Version mismatch warnings show proper translated text in all five languages.

### About tab — What's New panel

- Release notes fetched live from GitHub and displayed inline in Settings → About.
- Offline fallback shows a styled panel with clickable links to GitHub and masselink.net.

### WiFi rule edit dialog

- Shows the rule's hit counter.
- **(Re)set counter** button — type a number to set, 0 to clear.
- Counter changes are recorded in the activity log: `Counter: 42 → 10`.

### Window close

- X button (or Alt+F4) hides to tray. **Shift+X** performs a clean exit.

---

## v3.1.0

### Auto-update

- One-click update: downloads `MasselGUARD.zip` from GitHub, extracts, overwrites, and relaunches.
- Progress shown inline: Downloading… / Extracting… / Applying…
- Shift+Check Now force-installs the latest release (for testing).
- Version comparison ignores the build timestamp so a local dev build is never mistaken for an older version.
- Update status badges: ↑ update available · 🚀 running ahead · ✓ up to date · — never checked

### Themed dialogs

- All update-related prompts use the app theme instead of the system MessageBox.

### About tab — What's New panel

- Release notes fetched live from GitHub; offline fallback with clickable links.

---

## v3.0.1

### Bug fixes

- Fixed two error dialogs appearing after applying an update (harmless WPF shutdown artifact).
- Theme preview cancel now correctly reverts to Windows system colours when no custom theme was active.

### Appearance

- System theme label in Settings renamed to "System theme".
- Preview / revert buttons have a fixed width so adjacent controls don't shift.
- Activity log collapse button (») enlarged for easier clicking.

### Startup

- Holding Shift at startup resets both the font override and the custom theme if either caused a problem.

---

## v3.0.0

### Custom appearance system

- Toggle between Windows 11 system colours and custom theme files, independently for dark and light mode.
- System mode pill: Auto / Light / Dark.
- Theme preview: applies for 10 seconds then auto-reverts. No accidental permanent changes.

### Font override

- Pick any installed font from a per-typeface preview dropdown.
- Font size slider (8–18 pt) with 10-second preview.

### Activity log toggle

- ☰ button in the tunnel header opens the log panel; » collapses it.
- Setting persisted across sessions.

### Confirm on close

- Optional confirmation dialog before disconnecting active tunnels on exit.

### Update check frequency

- On start / Daily / Weekly / Manual pill selector in Settings.

### Shift+startup emergency reset

- Holding Shift at launch resets font and/or theme overrides if either is causing a display problem.

---

## v2.9.0

### Architecture

- Full MVVM rewrite — Models / Services / ViewModels / Views.

### Tunnel list improvements

- Drag-to-reorder tunnels.
- Uptime counter in status column.
- ⚡ default action and 🔓 open network protection badges shown inline after the tunnel name.
- Rules column — click to highlight matching WiFi rules.

### WiFi Rules panel

- Added to the main window (left panel, optional).
- Columns: Name | SSID | Action | Hits | Tunnel.
- Hits counter persisted in config, accent colour when > 0.
- Rule Name field with auto-generation from SSID + tunnel.
- Drag-to-reorder rules.

### Defaults button

- Single toolbar button to set both the default action tunnel and open network protection tunnel.

### Custom WPF toast notifications

- Fully themed, slides in from bottom-right, auto-closes.
- Category label (WiFi Rule / Open Network / Default Action).
- Configurable duration (3 / 5 / 10 / 15 / 30 s).

### Double-fire prevention

- WiFi rules fire exactly once per network switch.

---

## v2.5.0

### Settings redesign

- Expanded from 3 tabs to 6: General / Appearance / Default Action / WiFi Rules / Advanced / About.
- Fully deferred save; Cancel reverts everything including live previews.

### Pre/post scripts

- Every tunnel can run a `.bat` or `.ps1` script at four hook points: before/after connect, before/after disconnect.

### Two new built-in themes

- High Contrast Dark and High Contrast Light — suited for low-vision users and high-brightness environments.
- Total built-in themes: six.

### Tray icon badge

- Green counter badge showing the number of active tunnels.

### Import / Export settings

- Export to `.masselguard` file; import with version mismatch warning. Unknown fields are safely ignored.

### Log levels simplified

- Normal (OK + Warn) and Extended (everything). No more log file written to disk.
