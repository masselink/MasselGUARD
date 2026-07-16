## v3.7.1 — Chromatic Chameleon

- **Settings → About** now surfaces theme updates too, not just app updates — "Check for update" also checks installed themes, and a click-through banner appears here (in addition to the existing Appearance-tab badge) pointing you at Community themes when one is available.
- **Fixed** — the Theme Manager's right-click menu (Apply / Duplicate / Export / Delete) could pop up shifted away from the theme it was opened for, overlapping the main window. It's now anchored directly under the clicked theme.
- **Fixed** — deleting a theme that ships its own font file (e.g. UniFi's bundled Lato) could fail with a "file in use" error, because selecting the theme to right-click it had already loaded that font live. The app now releases it first.
- **Fixed** — updating/reinstalling a theme from Community themes while it was the active theme could later crash with an unhandled `FileNotFoundException` from WPF's text renderer, for the same reason (a stale font reference to files that had just been overwritten). The app now reloads the theme fresh immediately after the update completes.
- **Fixed** — a theme's custom app icon (`appIcon`) showed correctly in the system tray and momentarily in flyouts, but the taskbar button kept showing the default icon after startup, and even after fixing that, could appear tiny or go blank. Root causes: (1) the taskbar button's icon is cached at registration and needs an explicit refresh; (2) a real multi-resolution `.ico` was being collapsed through a single-frame re-encode, so the taskbar's bigger icon slot got a stretched, undersized bitmap; (3) the native icon handles handed to Windows could be garbage-collected out from under it once nothing else referenced them. All three are fixed — the taskbar button now updates reliably with a correctly-sized icon.
- **Fixed** — Settings → About showed the "Running ahead of / Update available" pill beside the version title, truncating it for longer codenames. It now sits on its own row, right-aligned, between the build stamp and "Last checked" lines.
- Tray icon tooltip and the window's actual title (what Alt-Tab, the taskbar hover tooltip, and Task Manager read — separate from the custom in-window title bar) now both lead with the theme's app name consistently — "*&lt;app name&gt;* — WireGuard VPN Client" when idle, "*&lt;app name&gt;* — &lt;tunnel&gt; active" when connected — and stay in sync immediately on a theme switch instead of only updating on the next tunnel status change.
- **Install** (from a portable copy) now copies only what's actually needed to run — the two exes, `lang\`, and the tunnel DLLs — instead of everything found next to the portable exe. Debug symbols (`.pdb`), the first-run `install-dotnet.bat` helper, and legacy cruft like an old exe-relative `theme\` folder from before themes moved to `%APPDATA%` no longer get carried into new installs. Existing installs are left as-is — this only changes what a fresh install/update copies going forward.
- **Fixed** — the Theme Manager's right-click menu (Apply / Duplicate / Export / Delete) rendered with the plain default Windows look instead of the app's theme. Added a themed style for it (dark surface, themed border, hover highlight), and — since a code-created `ContextMenu` doesn't reliably pick up an implicit style automatically — assigned it explicitly too.
- **Fixed** — picking a different theme in Settings → Appearance while in Light or Dark mode could preview the *wrong* colour variant (resolved from the raw Windows dark/light setting instead of the mode you'd actually picked in Settings) until you toggled the mode pill or hit Save. The picker now respects the mode you chose, same as the Dark/Light preview buttons already did.
- **Fixed** — several labels across the Theme Manager (color-row names, "Light"/"Dark" headers, "Tray menu"/"Tray menu preview" section labels, the sidebar theme list, the ● LIVE indicator) and a couple on the main window (the hidden-tunnel-count badge) were colored once, when first built, and never updated again — so they could go unreadable after previewing a different theme, since the app replaces each color resource with a new object on every theme change rather than updating it in place. All now stay live.

---

## v3.7.0 — Chromatic Chameleon

Theming is the headline of this release: the old Theme Builder is now a full **Theme Manager** with a community theme browser, every image asset (not just colours) is dark/light aware, and individual colours can carry their own transparency. Alongside it, the first-run wizard gained a "Choose your view" step so new users aren't dropped into a full-featured UI they didn't ask for.

---

### Theme Manager (formerly Theme Builder)

- **Renamed and reorganized.** The old scattered New / Duplicate / Export / Delete buttons moved to a right-click context menu on the theme list; the footer is now just Undo/Redo · Live indicator · Cancel · Save.
- **"+ Add theme" dialog** — one entry point for Create (from the current theme, from two images, or as a copy of any existing theme), Import from a `.zip`, and browsing Community themes.
- **Community themes** — a dedicated **"Download themes…"** shortcut in Settings → Appearance jumps straight to the browser (or reach it via Manage themes… → Community themes). Cards show name, author, description, tags, and a search box + tag-filter chips.
  - **Click a preview to zoom it** — enlarges to a full-size overlay; a magnifying-glass hint appears on hover. Closable via the ✕, the backdrop, or Escape. The Dark/Light preview toggle keeps working while zoomed, and the zoomed image stays in sync with it.
  - **Reinstall / overwrite** — previously-installed themes can now be redownloaded to pick up upstream changes, with a confirmation that local edits will be lost.
  - **Update detection** — the repo tags each theme with a version timestamp; an installed theme whose repo copy has since changed shows an **"Update"** button (instead of plain "Reinstall") the next time you open the browser. Installed themes are also checked automatically alongside the regular app-update check, surfacing a small badge in Settings → Appearance when one or more have updates waiting.
  - **Hold Shift** to fall back to plain Windows colours if a theme's preview (or the Manager's own live-edited state) becomes unreadable.
- **Unified theme storage** — all themes, downloaded or hand-made, now live together in one `%APPDATA%\MasselGUARD\themes\` folder. Older split `custom_themes\` / `shared-themes\` folders are merged in automatically on first launch.
- Closing the Theme Manager (or the community browser) now returns focus to **Settings → Appearance** instead of leaving you at the main window.

---

### Every image asset is now dark/light sensitive

Logo, app icon, background image, and both tray icons (connected/disconnected) each get **two independent pickers** — Light and Dark — instead of one shared image. A legacy theme with only a single shared image still works: it seeds both sides on load, and saving migrates it to the dual format automatically. `logoWidth`/`logoHeight`, background stretch, and background opacity stay shared, since those are layout numbers rather than images.

- **App icon now drives the taskbar/Alt-Tab icon**, not just the tray — previously only the tray icon updated when a theme changed; the taskbar button now follows it too, falling back to the compiled default icon when a theme doesn't set one.

---

### Per-colour transparency

Three colours that are meant to be translucent overlays — **List row hover**, **Tray menu hover**, and **Highlight** — get their own transparency sliders in a new **Transparency** section (split out from the old combined "Window" section, which now covers header + status-bar layout only). Dragging a slider rewrites that colour's alpha byte (`#AARRGGBB`) through the same live-preview pipeline as everything else, so it previews instantly and survives Undo/Redo.

---

### Typography — header font + a font size that actually reaches the app

- **New "Header font" field** in the Theme Manager's Typography section — a separate typeface for the title bar app name and section/column headers, independent of the body font. Leave it blank to keep using the body font (the default for every existing theme).
- **The Font size override now actually affects the whole app.** Previously it only reached the tunnel list and WiFi-rule rows; every button, column header, footer label, and dialog elsewhere in the app hardcoded its own literal size and ignored the setting. All of these now scale off the theme's font size (or the override, when one is set) through four consistent tiers — Tiny / Small / body / Header. Window-chrome glyphs (minimize/maximize/close, the settings gear, scroll chevrons) intentionally stay fixed-size, since they're icons rather than text.

---

### Setup wizard — "Choose your view"

A new step lets you pick how much of the interface you want on first run, instead of discovering the History/Tunnels/WiFi visibility toggles later:

| Preset | Shows |
|---|---|
| **Simple** | Tunnels + WiFi rules (panel and column). No timeline, no activity log. |
| **Manual** | Tunnels + activity log. No timeline, no WiFi rules — and turns off WiFi automation, since rules don't apply without it. |
| **Expert** | Everything. |
| **Custom** | Advances to its own dedicated step with the same four toggles shown individually and full-size, instead of a hard-to-notice panel you had to scroll to. |

- The **WiFi Automation** step is now skipped entirely (in both Next and Back) once WiFi rules are disabled — nothing left to configure there.
- The Custom-details step is likewise only reachable by explicitly picking "Custom" — every other preset skips straight past it.
- Every value stays freely editable afterward, either individually in Settings or by re-applying a preset from the same **Simple / Manual / Expert** buttons now available at the top of **Settings → General**.
- Step 1 (Language & Appearance) gained a full **theme picker** — every installed theme, including anything just downloaded — plus its own "Download more themes…" shortcut, so a theme you grab mid-wizard is immediately selectable.
- Fixed the wizard's footer buttons (Skip/Back/Next) rendering visibly different from the rest of the app — they were stretched to fill the footer bar with no vertical padding; now sized and centred like every other button.

---

### Settings — appearance quality-of-life

- **Dark/Light/Follow-system now applies immediately** in Settings → Appearance instead of waiting for Save. If you close the window with it still unsaved, a prompt asks whether to keep the change or discard it.
- The **timeline range** (24h / 7d / 31d) and the individual History-tab visibility toggles now persist immediately when changed, instead of only on the next full Save.
- The Theme Manager's global **Invert** checkbox moved next to the "Copy all → Dark/Light" buttons it modifies (was oddly placed next to the Dark/Light pill) and is now labelled **"Copy inverted colour."**

---

### Fixes

- **Double resize grip** — the main window showed two overlapping resize-grip glyphs in the corner; removed the redundant custom one (WPF already draws its own for this window style) and retired the now-dead "Resize grip" theme toggle.
- Timeline segment colours are derived from the active theme's Accent colour (golden-angle hue rotation), so any number of tunnels/SSIDs get distinct, theme-matching colours automatically, and they recolour live on theme switch.
- The WiFi-rules panel now reclaims its space immediately when hidden, instead of leaving an empty gap.

---

### Language

- **Japanese** added — six languages total (English, Dutch, German, French, Spanish, Japanese), including its own flag in the picker.

---

### Security

- Theme asset paths (background image, app icon, tray icons, logo) in `theme.json` are now constrained to the theme's own folder — a downloaded/untrusted theme can no longer point the app at files elsewhere on disk via a rooted path or `..` traversal.
- `FlushDns` now launches `ipconfig.exe` by its full `System32` path instead of a bare command name, closing off a PATH-hijack route for the elevated process.

---

## v3.6.0 — Dangerous Donkey

### Settings reorganized — 7 tabs

The settings window has been restructured around clear domains: *General → Tunnels → WiFi → Appearance → History → Advanced → About*.

| Tab | Now contains |
|---|---|
| **General** | Language, app mode, Start with Windows, confirm disconnect on exit |
| **Tunnels** *(was Tunnel Groups)* | Group management, auto-reconnect mode, kill switch mode, config validation, display options (tunnel count, empty groups, DNS indicator) |
| **WiFi** *(merges WiFi Rules + Default Action)* | SSID rules, disable-rules toggle, default action, open network protection, rules display options |
| **Appearance** | Theme, font, notifications, show activity log |
| **Advanced** | Maintenance only: import/export, log level, installation, WireGuard client, orphaned services |

The WiFi tab reads top-to-bottom in the same order the app evaluates a network change: rules first, then the default action when no rule matches, plus open network protection.

---

### Companion tunnels — WireGuard app awareness

Tunnels connected or disconnected outside MasselGUARD (WireGuard for Windows app, CLI) are now fully tracked:

- **Activity log** — external transitions are logged: `Connected: <name> via WireGuard app` / `Disconnected: <name> via WireGuard app`.
- **History & timeline** — externally started sessions appear in the connection history and activity timeline (source: *WireGuard app*); externally dropped sessions are closed properly instead of staying open forever.
- **Deactivating a tunnel in the WireGuard app is recognised as deliberate** — MasselGUARD logs it and skips auto-reconnect instead of fighting the WireGuard app over the tunnel. A genuine service crash still reconnects.

---

### Unified dual-variant theme system

The theme architecture has been completely redesigned. Instead of maintaining separate dark and light theme files, every theme is now a **single file** that contains both colour variants.

#### New theme format

```json
{
  "name": "My Theme",
  "fontFamily": "Segoe UI",
  "cornerRadius": 6,
  "dark": {
    "colorWindowBg": "#0E1117",
    "colorAccent":   "#58A6FF"
  },
  "light": {
    "colorWindowBg": "#F6F8FA",
    "colorAccent":   "#0969DA"
  }
}
```

- Root level holds **structural settings only** — font, corner radius, window chrome, status bar, background image, logo.
- `"dark"` and `"light"` sections hold the colour fields for each mode.
- Either section can be omitted. The app **auto-generates** the missing side at load time using HSL lightness inversion — nothing is written to disk.
- Any colour field omitted from a section falls back to the Windows system palette for that slot.

#### What this means for users

- Theme selection is now a **single picker** — choose one theme, the app applies the dark or light colours based on Settings → Appearance → System mode (Light / Dark / Auto).
- Custom themes survive a mode switch automatically.
- Built-in themes: **Grey** and **High Contrast** — plus **System (Windows colors)**, which uses the Windows accent palette and is the default.
- Custom themes live in `%APPDATA%\MasselGUARD\themes\` so they survive app updates.

#### Colour auto-generation

When only one variant is defined, the other is derived at load time:

| Neutral colours (saturation < 15 %) | Straight invert — `L → 1 − L` in HSL |
|---|---|
| Chromatic colours (accents, status, danger) | Invert L then clamp to 0.30 – 0.75 |
| Background fields | Invert L then clamp to 0.06 – 0.94 |

---

### Settings — Appearance tab redesigned

- **Single theme picker** replaces the separate dark/light ComboBoxes and the "Use custom theme" toggle.
- **System mode pill** (Light / Dark / Auto) stays, now controls which colour variant of the selected theme is shown.
- **System (Windows colors)** is included in the picker as a first-class option — no separate toggle needed.
- **▶ Dark / ▶ Light preview buttons** — try either colour variant of the selected theme for 10 seconds before saving.

---

### Tunnel connect — reliability improvements

#### Pre-flight config validation

Config files are now validated **before** the WireGuard service is created. Catches common mistakes that would cause a silent exit:

| Field | Check |
|---|---|
| `PrivateKey` / `PublicKey` / `PresharedKey` | 44-character base64 (32-byte key) |
| `Address` / `AllowedIPs` | Valid CIDRs; IPv6 group count with fix suggestion |
| `DNS` | Valid IP addresses |
| `MTU` | 576 – 9000 |
| `ListenPort` / `PersistentKeepalive` | 1 – 65535 |
| `Endpoint` | `host:port` format |
| Required fields | PrivateKey, Address, PublicKey, Endpoint |

Example fix suggestion: `fd00:dead:beef:4/64` → *"4 of 8 groups. Did you mean: `fd00:dead:beef::4/64`?"*

#### Skip-validation options

- **Per-tunnel** — "⚠ Skip config validation" toggle in the tunnel Edit and Add dialogs.
- **Global override** — Settings → Tunnels → Config validation section.

#### False-positive connect detection fixed

Previously, a failed tunnel start and a successful one both left the service in `Stopped` state, making them indistinguishable. After detecting `Stopped`, the app now waits 300 ms and probes the WireGuard management pipe (`\\.\pipe\WireGuard\<name>`) and the network adapter. If neither is found the failure is logged clearly with a pointer to the Windows Event Log.

#### Orphaned service cleanup

When a tunnel fails to start, the `WireGuardTunnel$<name>` SCM entry is now always cleaned up before throwing, preventing orphaned service entries.

#### Config source priority fixed

`stored.Path` (the `.conf.dpapi` file) is now checked before the legacy inline `stored.Config` blob. Prevents a stale inline blob from being used when a valid file exists alongside it. Priority order:

1. `stored.Path` → `.conf.dpapi` file (DPAPI decrypt)
2. `stored.Path` → plain `.conf` file
3. `stored.Config` → legacy inline DPAPI blob
4. `stored.Config` → raw plaintext (very old builds)

#### BOM handling

- Temp conf files are written without BOM (`UTF-8 NoBOM`).
- `StripBom()` helper is applied to all decrypted content before writing.
- Prevents tunnel.dll (Go) from failing to parse a config that starts with a UTF-8 BOM byte sequence.

#### Diagnostic conf log

After writing the temp conf, a debug line is logged:
```
[DBG] Conf written: 456 bytes, BOM=False, first line=[Interface]
```

---

### Auto-reconnect

- Tunnels that drop unexpectedly (sleep/wake, network blip, service crash) are automatically reconnected.
- **3 retry attempts** with increasing backoff: 5 s, 10 s, 15 s. Gives up cleanly after the third failure.
- Only fires on *unexpected* drops — intentional disconnects (user click, WiFi rule, CLI) and clean deactivations via the WireGuard app are never retried.
- **Global mode** in Settings → Tunnels:
  - **Off** — disabled globally.
  - **Per tunnel** — each tunnel has its own toggle in the Edit dialog.
  - **Always** — every tunnel reconnects regardless of the per-tunnel toggle (default).
- When mode is **Off**, the per-tunnel toggle is hidden in the Edit dialog.
- When mode is **Always**, the toggle shows as disabled with *(controlled globally)*.
- Activity log entries: dropped → reconnecting (attempt N/3) → reconnected ✓ or giving up.
- Each attempt waits for the connect to actually finish before reporting success or failure.

---

### Setup wizard expanded

The wizard now covers the most important behaviour settings, so a fresh install is fully configured in one pass:

- Auto-reconnect mode (Off / Per tunnel / Always)
- DNS leak indicator and tray notifications
- Start with Windows and confirm disconnect on exit
- History capture (connections / WiFi)
- The WiFi step **explains how rules work** — WiFi rules, default action, and open network protection, with a typical-use example — since rules themselves are created after the wizard, once tunnels exist
- A **summary page** at the end shows every chosen setting before finishing.

---

### Language picker — country flags

The language selector (Settings → General and the wizard) now shows a country flag next to each language name. Flags are 20×15 PNG files in `lang\flags\`, referenced by a `_flag` key in each language file — include one when adding your own language.

---

### What's New — rendered Markdown

The What's New panel on the About tab now renders the release notes as formatted Markdown (headings, tables, bold) instead of plain text. Notes are fetched live from GitHub; when offline, a fallback panel links to the project pages.

---

### Updated dependencies

- `tunnel.dll` rebuilt from wireguard-windows source (v1.1).
- `wireguard.dll` updated to wireguard-NT v1.1.
- DLL validation at connect time checks file size to distinguish wireguard-NT from the WireGuard-for-Windows stub.
- `install-dotnet.bat` helper included — checks for the .NET 10 Desktop Runtime and offers to install it when missing.

---

### Directory ACL hardening

- `%APPDATA%\MasselGUARD\` is now restricted to the current user only on first write.
- Removes the default Administrators read-access inherited from `%APPDATA%`.
- Applies retroactively to existing installations on the first Settings save after updating.
- Inheritable — `tunnels\` subfolder and all history/config files inside are covered automatically.

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
