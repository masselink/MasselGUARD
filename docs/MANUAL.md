# MasselGUARD — User Manual

**Version 3.6.0 — Dangerous Donkey**

---

## Contents

1. [Introduction](#1-introduction)
2. [Installation and run modes](#2-installation-and-run-modes)
3. [First run — Setup wizard](#3-first-run--setup-wizard)
4. [The main window](#4-the-main-window)
5. [Managing tunnels](#5-managing-tunnels)
6. [Connecting and disconnecting](#6-connecting-and-disconnecting)
7. [Default action and open network protection](#7-default-action-and-open-network-protection)
8. [WiFi Rules](#8-wifi-rules)
9. [Settings — General](#9-settings--general)
10. [Settings — Tunnels](#10-settings--tunnels)
11. [Settings — WiFi](#11-settings--wifi)
12. [Settings — Appearance](#12-settings--appearance)
13. [Settings — History](#13-settings--history)
14. [Settings — Advanced](#14-settings--advanced)
15. [Settings — About](#15-settings--about)
16. [Pre/post scripts](#16-prepost-scripts)
17. [Quick Connect](#17-quick-connect)
18. [Import / Export settings](#18-import--export-settings)
19. [The activity log](#19-the-activity-log)
20. [Activity timeline](#20-activity-timeline)
21. [System tray](#21-system-tray)
22. [Kill switch](#22-kill-switch)
23. [Auto-reconnect](#23-auto-reconnect)
24. [Themes](#24-themes)
25. [Font override](#25-font-override)
26. [Multiple languages](#26-multiple-languages)
27. [Frequently asked questions](#27-frequently-asked-questions)
28. [Command-line interface (CLI)](#28-command-line-interface-cli) — see also [`CLIManual.md`](CLIManual.md) for the full reference

---

## 1. Introduction

MasselGUARD is a WireGuard automation tool for Windows. It monitors your WiFi connection and activates the right WireGuard tunnel automatically based on rules you define. It also works as a manual WireGuard front-end when automation is not wanted.

---

## 2. Installation and run modes

**Requirements:** Windows 10 or 11 (64-bit), [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0), Administrator rights.

### Run modes

| Mode | Meaning |
|---|---|
| **Standalone** | Running as a portable exe; no installed version detected |
| **Managed (Portable)** | An installed version exists; this is a separate copy |
| **Managed** | Running from the installed location — shown **green** in footer |

### Installing

1. Settings → Advanced → Installation → **Install**
2. Choose a parent folder
3. Optionally enable **Start with Windows** (Scheduled Task, no UAC on relaunch)
4. MasselGUARD relaunches from the installed location

### Managed Portable — version prompt

When running as Managed Portable and the version **differs** from the installed copy (including build number differences), a themed prompt offers to overwrite. The message adapts: "is newer than" or "differs from" depending on direction.

---

## 3. First run — Setup wizard

Runs on first launch and when starting a newer version than the last wizard run.

**Step 0 — Welcome:** Upgrade banner (on version change). Install-choice card (first-run Standalone). Import settings card.

**Step 1 — Language & Appearance:** Language picker (with country flags) and theme selector. Changes apply immediately as a preview.

**Step 2 — Operating mode:** Standalone / Companion / Mixed.

**Step 3 — Startup:** How MasselGUARD is installed, whether it starts automatically with Windows, and confirm disconnect on exit.

**Step 4 — WiFi:** Explains how WiFi rules, the default action, and open network protection work together (rules themselves are created after the wizard, in Settings → WiFi). Disable WiFi rules toggle. Show WiFi rules panel toggle.

**Step 5 — Behavior:** Auto-reconnect mode (Off / Per tunnel / Always), DNS leak indicator, history capture (connections / WiFi), tray notifications.

**Step 6 — Done:** Summary of every chosen setting, version label, and Check for updates.

---

## 4. The main window

### Tunnel list (left panel)

Columns: **Tunnel** | **Type** | **Status** | **Rules** | **Action**

- **Colour strip** — 4 px strip per row showing the tunnel's group colour
- **Badges** — `⚡` (default action) and `🔓` (open network protection) after the tunnel name
- **Status** — uptime for active tunnels: `● Connected  2h 34m`
- **Rules** — count of WiFi rules referencing this tunnel; click to highlight matching rules in the WiFi Rules panel. Rebuilds immediately on rule add/edit/delete
- **Action** — Connect / Disconnect, centred

**Toolbar buttons:** + Add | Edit | Import | **Defaults** | Delete

### Defaults button

Opens a themed popup centred on the main window with:
- **⚡ Default action tunnel** — dropdown + "— clear —"
- **🔓 Open network protection** — dropdown + "— clear —"

Saves immediately on clicking Save. Badges and footer update in place.

### WiFi Rules panel (optional, left panel)

Columns: **Name** (widest) | **SSID** | **Action** | **Hits** | **Tunnel**

- **Hits** — how many times each rule has triggered (persisted, accent colour when > 0)
- Rows are **draggable** to reorder
- Highlighted rows (from clicking a tunnel's rule count): 2 px Accent left border + tinted background
- Add / Edit / Delete buttons; Delete uses themed confirmation dialog
- Collapses when hidden or Manual Mode active

### Activity Log (right panel)

Column header: **Time** | **Event**. Entry count badge. Export Log button.

The log panel can be shown or hidden:
- **`☰` button** — appears on the right side of the tunnel header only when the log is collapsed; click to show
- **`»` button** — appears on the right side of the activity log header; click to collapse
- Toggle state persists across sessions (see Settings → Appearance → Interface)

### Footer bar

Left: run mode (green when Managed) | Centre: ⚡ default tunnel + 🔓 open protection | Right: Administrator status

---

## 5. Managing tunnels

### Tunnel groups

Manage in **Settings → Tunnels**. Each group row has:
- 👁 Hide/show the group tab
- ⭐ Set as startup default
- Name field (editable inline)
- Colour picker (hex or theme key)
- ↑ ↓ ✕ reorder and delete

**Drag tunnels into groups:** Drag any tunnel row and drop it onto a group tab button to reassign it immediately.

**Toggles:** Always hide tunnel count | Hide empty groups

### Drag-to-reorder tunnels

Drag tunnel rows to reorder within the current group. A 2 px Accent drop-line shows the insertion point.

---

## 6. Connecting and disconnecting

Click Connect / Disconnect per tunnel. Automation does this automatically on network changes.

Active tunnels show elapsed uptime: `< 1 min` → `Xs`, `< 1 h` → `Xm YYs`, `< 1 day` → `Xh YYm`, `≥ 1 day` → `Xd YYh YYm`.

---

## 7. Default action and open network protection

### Default action

What happens when connecting to WiFi with no matching rule. Options: Do nothing / Disconnect all / Activate a tunnel. The assigned tunnel shows `⚡` in the list and `⚡ TunnelName` in the footer.

### Open network protection

Activates automatically on **passwordless** WiFi before any SSID rule. The assigned tunnel shows `🔓` in the list and `🔓 TunnelName` in the footer.

### Setting them

- **Defaults button** in the tunnel toolbar (popup centred on window) — immediate save
- **Settings → WiFi** — saves on Settings Save
- **Edit tunnel dialog** footer bar toggles — saves on dialog Save

---

## 8. WiFi Rules

### Rule dialog fields

| Field | Description |
|---|---|
| **Name** | Display name — auto-generates from SSID + tunnel as you type. Stops auto-generating once manually edited. |
| **SSID** | Network name — case-sensitive. "Use Current" fills from active WiFi. |
| **Tunnel** | Leave empty to disconnect all tunnels on this network. |

### Hits counter

The **Hits** column shows how many times each rule has triggered. Persisted in config — survives restarts. Shown in Accent colour when > 0, muted when 0.

**Editing the counter** — open the Edit Rule dialog for any existing rule. Below the form fields (separated by a divider) the current hit count is displayed. Click **(Re)set counter** to open a small input dialog:

- Type any positive integer to set a specific value.
- Type `0` to clear the counter.
- Cancel closes without making a change.

The change is written to config when you save the rule dialog. The activity log records the old and new values: `Counter: 42 → 10`.

### Drag to reorder

Drag rows in the WiFi Rules panel on the main window to change evaluation order. Rules evaluate top to bottom; first match wins.

### Tunnel list updates

Adding, editing, or deleting a rule immediately refreshes both the WiFi Rules panel **and** the Rules column count in the tunnel list.

---

## 9. Settings — General

### Language

Language picker — changes take effect immediately. Five languages: English, Dutch, German, French, Spanish.

### App mode

- **Standalone** — MasselGUARD manages tunnels directly (`tunnel.dll`)
- **Companion** — Automates the WireGuard for Windows app
- **Mixed** — Both simultaneously

### Startup & exit

- **Start with Windows** — registers a Scheduled Task at `RunLevel=Highest`, so MasselGUARD starts elevated without a UAC prompt
- **Confirm disconnect on exit** — when active tunnels are running, ask before disconnecting them on exit. When off, tunnels are disconnected silently (default: on)

All changes deferred until Save.

---

## 10. Settings — Tunnels

All tunnel-wide configuration in one place: groups, connection behaviour, and display options.

### Tunnel groups

- Group list — add/edit/reorder/delete groups, set colour and visibility
- Add group: type name + click + Add

### Auto-reconnect mode

| Setting | Behaviour |
|---|---|
| **Off** (default) | Auto-reconnect is disabled globally |
| **Per tunnel** | Each tunnel has its own toggle in the Edit Tunnel dialog |
| **Always** | Every tunnel reconnects on unexpected drop; per-tunnel toggle is not shown |

### Kill switch mode

| Setting | Behaviour |
|---|---|
| **Per tunnel** (default) | Kill switch can be enabled per tunnel in the tunnel edit dialog |
| **Always** | Kill switch is forced on for every tunnel; per-tunnel toggle is not shown |

### Config validation

Pre-flight validation checks WireGuard config files for errors (invalid IPs, missing keys, bad CIDRs) before connecting. The **Skip config validation globally** toggle disables it as a last resort — invalid configs will still fail, just with a less clear error.

### Display

- **Always hide tunnel count** — removes the `n` number from all group tab buttons
- **Hide empty groups** — suppresses tabs with no tunnels in the current filter
- **Show DNS leak indicator** — shows or hides the DNS status badge on active tunnels

Changes deferred until Save.

---

## 11. Settings — WiFi

Everything that controls what happens when your network changes, in evaluation order: rules first, then the default action when no rule matches, plus open network protection.

**Layout (top to bottom):**
1. Rules list — Add / Edit / Delete buttons
2. **Disable WiFi rules** toggle — pauses all automation
3. **Default action** picker: None / Disconnect / Activate tunnel — applied when no rule matches the current SSID. Same as the Defaults button popup but deferred to Settings Save
4. **Open network protection** tunnel picker — activated when connecting to an unsecured (open) WiFi network, before any SSID rule or default action
5. Display — **Hide WiFi rules on main window** and **Show Rules column** in tunnel list toggles

Rules changes save via the main Save button.

---

## 12. Settings — Appearance

### System theme mode

A pill strip sets whether dark or light mode is used:

| Pill | Behaviour |
|---|---|
| **Auto** | Follows the Windows dark/light preference automatically |
| **Light** | Always use the light theme |
| **Dark** | Always use the dark theme |

### Theme

A **single theme picker** selects the theme. Every theme contains both a dark and a light colour variant — the System mode pill decides which variant is shown. **System (Windows colors)** is a first-class entry in the picker and uses the Windows 11 accent palette instead of a theme file.

### Theme preview

Theme selections are **not applied immediately**. Click **▶ Dark** or **▶ Light** to apply that colour variant of the selected theme to the interface for 10 seconds, then it reverts automatically. Click again (shown as `↩ Xs`) to revert early. Changing any theme setting while a preview is active cancels the preview.

### Font override

Enable **Override font** to replace the theme's typeface with any installed system font.

- **Font family** — editable ComboBox; each font name renders in its own typeface. Leave blank to use the Windows system UI font.
- **Preview label** — sample text shown in the current preview state
- **Font size slider** — 8–18 pt

### Font preview

The **▶ Preview** button next to the size slider applies the draft font to the whole interface for 10 seconds. The preview label updates when Preview is clicked — not on every picker change. Changing the font family or size while a preview is running cancels it and reverts to the committed font.

### Notifications

- **Background notifications** toggle — show WPF toast when a tunnel auto-switches
- **Notification duration** — 3 / 5 / 10 / 15 / 30 seconds

### Toast notification format

```
╔══════════════════════════════════════════╗
║ 🛡 MasselGUARD  ·  WiFi Rule Matched  ✕ ║
╟──────────────────────────────────────────╢
║  1.MasselinkVPN-Split-AG                 ║
║  Rule: MasselNET → activate              ║
╚══════════════════════════════════════════╝
```

- App name from `Theme.AppName` — custom themes override it
- Strip colour: Accent (rule), Success/green (open network), Warning (default action)
- Slides in from bottom-right; auto-dismisses after configured duration

### Interface

- **Show activity log** — shows or hides the activity log panel on the right side of the main window. Changes take immediate effect. Saved state persists across restarts.

---

## 13. Settings — History

Controls what connection and WiFi data is recorded and displayed in the activity timeline.

### Capture

| Toggle | Effect |
|---|---|
| **Connections** | Record tunnel connect/disconnect events to `tunnel_history.json` |
| **WiFi (SSID)** | Record WiFi network connect/disconnect events to `wifi_history.json`, including security type |

### Show

| Toggle | Effect | Enabled when |
|---|---|---|
| **Connections** | Draw tunnel session bars in the timeline chart | Capture Connections is on |
| **WiFi (SSID)** | Draw WiFi SSID rows in the timeline chart | Capture WiFi is on |

The timeline panel **auto-hides** when both Show toggles are effectively off (either disabled by their capture toggle, or manually switched off). Turning off Capture does not force the Show toggle off — it only disables it, preserving your preference for when Capture is re-enabled.

### Activity chart — Time range

Pill selector: **Last 24 hours** · **Last 7 days** · **Last 31 days**

---

## 14. Settings — Advanced

App maintenance and diagnostics only — tunnel behaviour settings (auto-reconnect, kill switch, config validation) live on the **Tunnels** tab; startup settings live on **General**.

**Order:**
1. Import / Export settings
2. Log level (Normal / Extended)
3. Installation — run mode, Install/Uninstall button
4. WireGuard client — open the WireGuard for Windows app
5. Orphaned services — scan and clean up

### Extended log on Save

When extended logging is active, only **changed** fields are logged after Save:
```
[DBG] [Settings] Mode                       Standalone  →  Companion
[DBG] [Settings] Rule added:   MasselNET    → disconnect
[DBG] [Settings] Group added:  Work
```

---

## 15. Settings — About

### Version and update

The version block shows:
```
MasselGUARD v3.6.0  |  Dangerous Donkey
build  2606040000
```
Version codenames are assigned per Major.Minor.Patch release. The build stamp is hidden in IDE/debug builds.

- **Last checked** — timestamp of the most recent update check
- **Status badge** — coloured pill:
  - `↑` update available → Download button appears
  - `🚀` running ahead of latest release (dev build)
  - `✓` up to date
  - `—` never checked
- **Frequency** — On start / Daily / Weekly / Manual pill selector
- **Check now** — runs an immediate check; if an update is found the Download button appears and a themed prompt offers to install now
- **Download** button — only visible after a manual Check now in the current session; starts the in-app pipeline (download → extract → copy → relaunch)

### What's New panel

A scrollable panel showing release notes for all versions, fetched live from `docs/WHATSNEW.md` in the GitHub repository and rendered as formatted Markdown (headings, tables, bold). Updated every time the About tab is opened (once per Settings session). If the network is unavailable, a fallback panel is shown with clickable links to `github.com/masselink/MasselGUARD` and `masselink.net`.

---

## 16. Pre/post scripts

Four hook points per tunnel: Before connect / After connect / Before disconnect / After disconnect. `.bat` or `.ps1` files. Logged in Extended mode.

---

## 17. Quick Connect

Connect a `.conf` or `.conf.dpapi` file without importing. Appears as `⚡ filename` at top of tunnel list. Disappears after disconnecting.

---

## 18. Import / Export settings

**Export** — saves to `.masselguard` (JSON). Tunnel configs not included. A themed confirmation dialog warns that configs are excluded before writing.

**Import** — replaces settings and saves immediately to disk.
- Version mismatch (file older **or** newer than the running build) shows a themed Yes/No warning before proceeding.
- On success a themed prompt offers to **restart now** so all imported settings take effect immediately.
  - **Yes** — launches a new MasselGUARD process and exits the current one.
  - **No** — settings are already saved; a notice warns that some displayed values may not yet match the imported data until the next restart.
- Available in Settings → Advanced and on wizard Step 0.

---

## 19. The activity log

Column header: **Time** | **Event** — consistent with Tunnels and WiFi Rules panels.

Extended mode adds:
- `[DBG]` debug entries (connect timing, tunnel config fields)
- A grey **continuation line** beneath each disconnect entry showing session duration and bandwidth:
  ```
  ↳ 2h 14m 07s  ·  ↑ 142 MB  ↓ 1.2 GB
  ```
- Settings change details after Save

Entry count badge in header. Export Log saves to `.txt`.

The panel can be collapsed via the `»` button in its header, or reopened via the `☰` button that appears in the tunnel list header when the log is hidden.

---

## 20. Activity timeline

The activity timeline is a canvas shown above the footer when at least one history layer is active. It covers the last 24 h, 7 d, or 31 d (set in Settings → History → Activity chart).

### Canvas layout

| Row | Height | Content |
|---|---|---|
| Tunnel bar | 16 px | Coloured segments per tunnel; stacked when multiple tunnels overlap |
| WiFi band | 16 px × SSID count | One row per distinct SSID seen in the time window |
| Time axis | 20 px | Tick marks with timestamps |

### Hover tooltip

Move the mouse anywhere over the canvas. A vertical crosshair follows the cursor and a tooltip shows everything active at that point in time:

- **Tunnel rows** — coloured dot, tunnel name, connected-since / time range, duration, traffic (↑/↓). If the cursor is near the right edge (live data), shows live KB/s instead.
- **WiFi row** — 📶 SSID name, connection time or range, duration, 🔒 secured / ⚠ open network tag.

The tooltip shows on all Y positions (tunnel bar, WiFi rows, gap). If nothing is active at the hovered time, only the crosshair is drawn.

### `< >` navigation

The `<` and `>` buttons step through tunnel sessions within the current time window. Each click pins a tooltip to that session's midpoint showing the full session detail plus the WiFi SSID active at that time.

### Settings — History (effect on timeline)

| Capture Connections | Capture WiFi | Show Connections | Show WiFi | Panel |
|---|---|---|---|---|
| ✓ | ✓ | ✓ | ✓ | Tunnel bars + WiFi rows |
| ✓ | ✓ | ✓ | ✗ | Tunnel bars only |
| ✓ | ✓ | ✗ | ✓ | WiFi rows only |
| ✓ | ✗ | ✓ | — | Tunnel bars only |
| ✗ | ✓ | — | ✓ | WiFi rows only |
| any | any | ✗ | ✗ | Panel hidden |

---

## 21. System tray

**Icon states:**
- Filled green shield — one or more tunnels active
- Outline grey shield — no active tunnels

**Tray menu:**
- 🪟 Show Window
- 🛡 Tunnels (submenu) — shield is green when active
- ⬛→ Exit

Right-click → menu. Double-click → show main window. × in main window → minimise to tray (tunnels keep running).

---

## 22. Kill switch

The kill switch blocks all outbound internet traffic except through the active WireGuard tunnel. If the tunnel drops, traffic is blocked rather than leaking over the regular network interface.

### How it works

MasselGUARD adds `MasselGUARD_KS_` prefixed rules to Windows Firewall:
- Sets the default outbound policy to **Block** on all profiles (Domain, Private, Public)
- Adds an explicit **Allow** rule for the WireGuard tunnel adapter and the remote endpoint IP/port
- Removes all rules and restores **Allow** default on tunnel disconnect or app exit

### Per-tunnel kill switch

In the **Edit Tunnel** dialog, a **Kill Switch** toggle enables the feature for that tunnel only. The toggle is only visible when the global mode is **Off**.

### Global "Always" mode

Set in **Settings → Tunnels → Kill switch mode = Always**. Every tunnel automatically uses the kill switch; the per-tunnel toggle is not shown.

### Crash recovery

At startup, `KillSwitchService.CleanupStaleRules()` removes any leftover `MasselGUARD_KS_*` firewall rules and resets the default outbound policy to Allow — recovering from a previous crash that prevented normal cleanup.

---

## 23. Auto-reconnect

MasselGUARD detects when a tunnel drops unexpectedly and reconnects it automatically — up to 3 attempts with increasing backoff (5 s, 10 s, 15 s).

### What counts as unexpected

An unexpected drop is any tunnel failure **not** triggered deliberately. These are **never** retried:
- User clicking Disconnect
- A WiFi rule disconnecting the tunnel
- CLI `disconnect` or `disconnect-all`
- Deactivating the tunnel in the WireGuard for Windows app — MasselGUARD recognises a clean deactivate and logs `was deactivated via the WireGuard app — not reconnecting`

These **are** retried:
- WireGuard kernel adapter crash
- Machine waking from sleep with the VPN adapter gone
- WireGuard for Windows service crashing unexpectedly

### Global mode

Set in **Settings → Tunnels → Auto-reconnect**:

| Mode | Behaviour |
|---|---|
| **Off** | Disabled globally — no tunnels reconnect automatically |
| **Per tunnel** | Each tunnel has its own toggle in the Edit Tunnel dialog |
| **Always** (default) | Every tunnel reconnects; the per-tunnel toggle is not shown |

### Per-tunnel toggle

When global mode is **Per tunnel**, a **🔄 Auto-reconnect** toggle appears in the Edit Tunnel dialog footer (next to Kill switch). Enable it for tunnels you want to reconnect automatically.

When global mode is **Always**, the toggle shows greyed out with *(controlled globally)*.
When global mode is **Off**, the toggle is hidden entirely.

### Activity log entries

```
[AutoReconnect] 'WorkVPN' dropped — reconnecting in 5s (attempt 1/3)…
[AutoReconnect] 'WorkVPN' reconnected successfully.
```

```
[AutoReconnect] 'WorkVPN' dropped — reconnecting in 5s (attempt 1/3)…
[AutoReconnect] 'WorkVPN' — attempt 1 failed.
[AutoReconnect] 'WorkVPN' dropped — reconnecting in 10s (attempt 2/3)…
[AutoReconnect] 'WorkVPN' — giving up after 3 attempts.
```

---

## 24. Themes

### Built-in themes

Two built-in themes — **Grey** and **High Contrast** — plus **System (Windows colors)**, which uses the Windows 11 accent palette and is the default. Each theme contains both a dark and a light colour variant; the Appearance System mode (Light / Dark / Auto) decides which one is shown.

### Custom theme files

Drop a `<folder>/theme.json` into the `theme\` folder next to the exe, or into `%APPDATA%\MasselGUARD\themes\` (survives app updates). The root level holds structural settings (font, corner radius, chrome); colours live in `"dark"` and `"light"` sections. Either section may be omitted — the missing variant is auto-generated at load time by HSL lightness inversion.

Custom themes can override `AppName` to change the name shown in toast notifications.

### Live preview

Use the **▶ Dark** / **▶ Light** buttons in Settings → Appearance to see a colour variant for 10 seconds before committing. Cancel Settings to revert to the last saved theme.

See `theme/THEME_INFO.md` for the full key reference.

---

## 25. Font override

Enable **Override font** in Settings → Appearance → Font to replace the theme typeface with any installed system font.

- The font family dropdown lists all installed fonts; each entry renders in its own typeface
- The size slider sets the base font size (8–18 pt); 0 uses the theme default (~11 pt)
- Click **▶ Preview** to see the font applied to the whole interface for 10 seconds
- Changes are not committed until Settings is saved

To return to the theme's own font: toggle **Override font** off.

---

## 26. Multiple languages

English, Dutch, German, French, Spanish. Change in Settings → General — the picker shows a country flag next to each language. Add a language: copy `lang\en.json`, translate, set the `_code`, `_language`, and `_flag` keys, and drop a matching 20×15 `<flag>.png` into `lang\flags\`.

---

## 27. Frequently asked questions

**Rules fire twice when switching networks.**
Fixed — debounce re-fire guard and `ApplyWifiState` duplicate guard prevent double execution.

**Groups tab in Settings does nothing when clicked.**
Fixed — the Tunnels tab shows the group management controls.

**My tunnel group picker is empty when editing a tunnel.**
Fixed — the dialog now receives the group list directly from the live config.

**Settings Save shows too many changed fields.**
Fixed — `_draft` now correctly snapshots the live config on Settings open.

**Rules column doesn't update after adding a rule.**
Fixed — `_vm.RebuildTunnelList()` now called after every rule add/edit/delete.

**Can I reorder WiFi rules?**
Yes — drag rows in the WiFi Rules panel on the main window.

**Where is the WiFi rules Save button?**
Removed. All settings (including rules) save when the main Settings Save button is pressed.

**Can I drag a tunnel into a different group?**
Yes — drag the tunnel row and drop it onto the target group tab.

**Can I run without a UAC prompt?**
Yes — enable Start with Windows in Settings → General after installing. Subsequent launches relaunch via the Scheduled Task automatically.

**What does the Hits column show?**
How many times each WiFi rule has triggered since it was created. Persisted across restarts. You can view and edit it by opening the Edit Rule dialog — the counter appears below the form fields with a **(Re)set counter** button.

**How do I exit completely instead of minimizing to tray?**
Hold **Shift** while clicking the window's X button (or pressing Alt+F4). This performs a clean exit — same as Tray → Exit. Without Shift, the window hides to the tray and the app keeps running.

**Can I hide the activity log?**
Yes — click `»` in the log header, or use Settings → Appearance → Interface → Show activity log. The `☰` button reappears in the tunnel header to bring it back.

**Theme changes apply immediately and I can't cancel — how do I preview safely?**
Use the **▶ Preview** button in Settings → Appearance. It applies the theme for 10 seconds then automatically reverts. Cancel Settings to undo all uncommitted changes.

**Can I use a different font than the theme's?**
Yes — enable Override font in Settings → Appearance → Font, pick a family and size, and click **▶ Preview** to try it for 10 seconds before saving.

**The font or theme I chose makes the UI unreadable — how do I recover?**
Hold **Shift** while launching MasselGUARD. Before any window opens, the app detects the key and resets both the font override (back to system UI font) and the custom theme (back to Windows system colours, auto mode). A confirmation dialog lists exactly what was cleared. Normal startup continues afterwards with the reset settings.

**The What's New panel shows a "Could not load" message.**
The panel fetches release notes live from GitHub. Check your internet connection. You can also visit `github.com/masselink/MasselGUARD` or `masselink.net` directly — both links in the error panel are clickable.

**How does auto-reconnect work?**
When MasselGUARD detects that a tunnel dropped unexpectedly, it waits 5 seconds then tries to reconnect. If that fails it retries after 10 s, then 15 s. It gives up after 3 failed attempts. Every step is logged in the activity log.

**Auto-reconnect fired when I intentionally disconnected.**
This should not happen — MasselGUARD marks all intentional disconnects (user click, WiFi rule, CLI) before stopping the tunnel, and the reconnect logic checks that mark. If you see it happening please report the steps that triggered it.

**The Auto-reconnect toggle is greyed out / missing in the Edit Tunnel dialog.**
If it is greyed out with *(controlled globally)*, Settings → Tunnels → Auto-reconnect is set to **Always**. If it is missing entirely, the mode is **Off** — enable Per tunnel or Always first.

**How does the kill switch work?**
When enabled, MasselGUARD sets the Windows Firewall default outbound policy to Block and adds explicit Allow rules for the WireGuard tunnel adapter and endpoint. If the tunnel drops, traffic is blocked rather than routing over your regular internet connection.

**My internet stopped working after MasselGUARD crashed.**
The kill switch firewall rules were not cleaned up. Restart MasselGUARD — it removes stale `MasselGUARD_KS_*` rules and restores the default outbound policy to Allow at startup. Alternatively, open Windows Defender Firewall, remove any rules starting with `MasselGUARD_KS_`, and set the default outbound action back to Allow.

**The kill switch toggle is greyed out in the tunnel edit dialog.**
Settings → Tunnels → Kill switch mode is set to **Always**, which forces the kill switch on for all tunnels. Set it to **Off** to re-enable the per-tunnel toggle.

**Where do I see bandwidth usage?**
In the activity log (Extended mode). After each disconnect a grey continuation line shows the session duration and bandwidth: `↳ 2h 14m  ·  ↑ 142 MB  ↓ 1.2 GB`. Switch to Extended in Settings → Advanced → Log level.

**The import settings dialog showed raw placeholder text instead of a warning.**
Fixed in v3.3.0 — `SettingsImportVersionWarning` and `SettingsImportVersionNewer` are now present in all five language files.

---

## 28. Command-line interface (CLI)

MasselGUARD includes a full CLI for scripting and automation. The GUI and CLI share the same WireGuard kernel driver — any change made via CLI is reflected in the GUI within ~1 second.

### Requirements

Must run as Administrator. When invoked from a non-elevated terminal, Windows will prompt for elevation via UAC and open a new console window. To avoid the prompt, install MasselGUARD and enable **Start with Windows** (Settings → General) — the Scheduled Task runs at `RunLevel=Highest` so no UAC dialog appears.

**Tip:** run PowerShell or cmd.exe as Administrator for inline output without a separate window.

### Commands

| Command | Aliases | Description |
|---|---|---|
| `list` | `--list` | List all tunnels and their status |
| `status` | `--status` | Show active tunnel count and names |
| `connect <name>` | — | Connect a tunnel by name |
| `connect --default` | — | Connect the configured default tunnel |
| `connect --all` | — | Connect all tunnels |
| `disconnect <name>` | — | Disconnect a tunnel by name |
| `disconnect-all` | — | Disconnect all active tunnels |
| `info <name>` | — | Detailed status for one tunnel (type, group, uptime, source) |
| `log [n]` | — | Last *n* activity log entries (default 20) |
| `tunnel-history [n]` | — | Connection history with source and traffic (default 20) |
| `wifi-history [n]` | — | WiFi SSID history with duration and security (default 20) |
| `check-update` | `--check-update` | Live update check against GitHub |
| `version` | `--version`, `-v` | Show version, build, author and update status |
| `help` | `--help`, `-h` | Show this command reference |

### Flags

| Flag | Description |
|---|---|
| `--json` | Machine-readable JSON output |
| `--quiet`, `-q` | No output — exit code only (for scripting) |
| `--group <name>` | Scope `list` / `connect --all` / `disconnect-all` to one group |
| `--active` | Filter `list` to connected tunnels only |
| `--logtype normal\|extended` | Log detail level for `log` command (default: `normal`) |

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Success |
| `1` | Error (tunnel not found, connect failed, not elevated, etc.) |
| `2` | Already in desired state (already connected / already disconnected) |

### About `log [n]`

`log` reads from `%APPDATA%\MasselGUARD\tunnel_history.json` — the **same file** the GUI's Settings → History tab reads. There is no separate CLI log and no duplication.

The GUI's activity log panel (right side of the main window) is **in-memory only** and is not accessible from the CLI. `log` shows connection history only.

```
MasselGUARD log 5
  Tunnel                  When               Duration
  ──────────────────────  ─────────────────  ──────────
  1.MasselinkVPN-Split    today 09:31        active
  2.MasselinkVPN-Full     yesterday 14:05    42m 10s
  1.MasselinkVPN-Split    yesterday 08:12    6h 41m
```

With `--logtype extended`, a **Source** column is added showing what triggered the connection (e.g. `Rule: HomeNet → Work VPN`, `Manual`, `Auto-reconnect`):

```
MasselGUARD log 3 --logtype extended
  Tunnel                  When               Duration    Source
  ──────────────────────  ─────────────────  ──────────  ──────────────────────────
  1.MasselinkVPN-Split    today 09:31        active      Rule: HomeNet → Work VPN
  2.MasselinkVPN-Full     yesterday 14:05    42m 10s     Manual
```

### About `info <name>`

```
MasselGUARD info "1.MasselinkVPN-Split-AG"

  Name:    1.MasselinkVPN-Split-AG
  Type:    Local (tunnel.dll)
  Group:   Work
  Status:  ● Connected  1h 23m
  Source:  Rule: HomeNet → Work VPN  (today 09:31)
```

### Version output

```
MasselGUARD v3.6.0  |  Dangerous Donkey
build:   2606040000
Harold Masselink  |  https://masselink.net
Update:  up to date
```

The update status is read from the cached result of the last update check. Run the GUI and use **Settings → About → Check now** to refresh it.

### JSON output

Add `--json` to any command for machine-readable output:

```powershell
MasselGUARD version --json
```
```json
{
  "version": "3.3.0",
  "codename": "Camouflaged Koala",
  "build": "2506011430",
  "update_status": "up to date"
}
```

```powershell
MasselGUARD connect "Work VPN" --json
```
```json
{ "result": "connected", "message": "Tunnel 'Work VPN' connected." }
```

### Scripting example

```powershell
# Connect silently, act on exit code
MasselGUARD connect "Work VPN" --quiet
switch ($LASTEXITCODE) {
    0 { Write-Host "Connected." }
    2 { Write-Host "Already connected." }
    1 { Write-Host "Failed." }
}
```
