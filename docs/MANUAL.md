# MasselGUARD — User Manual

**Version 2.5.0**

---

## Contents

1. [Introduction](#1-introduction)
2. [Installation](#2-installation)
3. [First run — Setup wizard](#3-first-run--setup-wizard)
4. [The main window](#4-the-main-window)
5. [Managing tunnels](#5-managing-tunnels)
6. [Connecting and disconnecting](#6-connecting-and-disconnecting)
7. [WiFi Rules](#7-wifi-rules)
8. [Default Action](#8-default-action)
9. [Open Network Protection](#9-open-network-protection)
10. [Settings — General](#10-settings--general)
11. [Settings — Appearance](#11-settings--appearance)
12. [Settings — Advanced](#12-settings--advanced)
13. [Pre/post scripts](#13-prepost-scripts)
14. [Quick Connect](#14-quick-connect)
15. [Import / Export settings](#15-import--export-settings)
16. [The activity log](#16-the-activity-log)
17. [System tray](#17-system-tray)
18. [Themes](#18-themes)
19. [Multiple languages](#19-multiple-languages)
20. [Frequently asked questions](#20-frequently-asked-questions)

---

## 1. Introduction

MasselGUARD is a WireGuard automation tool for Windows. It monitors your WiFi connection and activates the right WireGuard tunnel automatically based on rules you define.

**What it can do:**
- Automatically start a tunnel when you connect to a specific WiFi network
- Protect you on open (passwordless) hotspots by forcing a tunnel before anything else connects
- Run scripts before or after a tunnel connects or disconnects
- Work entirely without the WireGuard app (Standalone mode) or alongside it (Companion mode)

**What it does not do:**
- Create WireGuard server configurations — you need a WireGuard server or VPN provider
- Work without Administrator rights — it creates and manages Windows services

---

## 2. Installation

**Requirements:** Windows 10 or 11 (64-bit), [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0), Administrator account.

For **Standalone mode**: `tunnel.dll` and `wireguard.dll` must be next to `MasselGUARD.exe` (included in the release zip).

For **Companion mode**: [WireGuard for Windows](https://wireguard.com/install) must be installed.

### First-time setup

1. Extract the zip to any folder
2. Double-click `MasselGUARD.exe` and accept the UAC prompt
3. The setup wizard opens automatically

### Installing to Program Files

1. Open **Settings → Advanced**
2. Click **Install** and choose a folder (default: `C:\Program Files\MasselGUARD`)
3. Optionally enable **Start with Windows**

> **After installing** the app will relaunch from the new location. If it says "already running" immediately after closing — wait 2–3 seconds and try again. This happens if the previous process hasn't fully released the single-instance lock yet.

---

## 3. First run — Setup wizard

The wizard runs automatically the first time MasselGUARD starts. Re-run it at any time from **Settings → General → Run Wizard**.

**Step 1 — Language** — Choose your language. The interface updates immediately.

**Step 2 — Operating mode**

| Mode | When to choose |
|---|---|
| **Standalone** | You want MasselGUARD to manage everything. No WireGuard app needed. |
| **Companion** | You already use WireGuard for Windows and want MasselGUARD to automate it. |
| **Mixed** | Both: some tunnels managed by MasselGUARD, others by WireGuard for Windows. |

**Step 3 — Automation** — Choose automatic (react to network changes) or manual (connect and disconnect yourself).

**Step 4 — Rules overview** — Explains WiFi Rules, Default Action, and where to configure them. No settings to change here — just an orientation.

**Step 5 — Done** — Shows the current version and a Check for updates button.

---

## 4. The main window

### Left panel — Tunnels

Shows all your tunnels with live status and a Connect/Disconnect button each. The tab strip at the top filters by group: All · your named groups · Uncategorized.

Bottom buttons: **+ Add**, **Edit**, **Import**, **Delete/Unlink/Remove**.

### Right panel — Activity Log

Timestamped log of everything MasselGUARD does. **Export Log** at the bottom saves to `.txt`.

### Status bar

Current WiFi network (or "No WiFi"), active tunnel name, app mode, and Administrator status.

### Title bar

**⚙** opens Settings. **🌙/☀/⚡** cycles dark → light → auto theme. **×** minimises to the system tray — it does not close the application.

---

## 5. Managing tunnels

### Adding a tunnel — Standalone

1. Click **+ Add**
2. Give the tunnel a name
3. Fill in the WireGuard config fields, or paste raw config on the **Raw** tab
4. Optionally assign a **Group** and add **Notes**
5. Click **Save**

### Adding a tunnel — Companion

1. Click **Import → Link to WireGuard profile**
2. Select an existing WireGuard profile

### Editing a tunnel

Select it and click **Edit**. Local tunnels open a full editor with Fields, Raw, and Scripts tabs. WireGuard-linked tunnels open a metadata dialog for group, notes, and scripts.

### Organising into groups

Create groups in **Settings → General → Tunnel Groups**, then assign tunnels via the Group dropdown in the Edit dialog.

### Deleting / removing

The action button label changes by context: **Delete** removes the tunnel and its encrypted config, **Remove** removes the record only, **Unlink** removes a WireGuard profile link without deleting the WireGuard profile.

---

## 6. Connecting and disconnecting

Click **Connect** or **Disconnect** next to any tunnel. When automation is enabled MasselGUARD does this automatically on network changes.

---

## 7. WiFi Rules

Go to **Settings → WiFi Rules**.

### Disable WiFi rules

The toggle at the top of the WiFi Rules page disables all WiFi-based automation. When on, the rules list and default action are greyed out.

### Adding a rule

1. Click **+ Add Rule**
2. Enter the **SSID** (WiFi network name) — case-sensitive, must match exactly
3. Choose a tunnel, or leave empty to disconnect all tunnels on that network
4. Click **OK**
5. Click **Save** to write the changes to disk

> Rules are deferred — Add/Edit/Delete only update memory. You must press **Save** to make them permanent.

### Example rules

| SSID | Tunnel | Effect |
|---|---|---|
| `HomeWifi` | `home-vpn` | Activates `home-vpn` at home |
| `OfficeWifi` | `office-vpn` | Activates `office-vpn` at the office |
| `CafeGuest` | *(empty)* | Disconnects all tunnels |

---

## 8. Default Action

Go to **Settings → Default Action**.

Determines what happens when you connect to a WiFi network that has no matching rule.

| Option | Effect |
|---|---|
| **Do nothing** | MasselGUARD ignores the unmatched network |
| **Disconnect all** | Disconnects any active tunnel |
| **Activate tunnel** | Connects the selected tunnel |

Default Action saves immediately when you change the setting — no Save button needed.

### Open Network Protection

At the bottom of the Default Action page. Select a tunnel to activate whenever you connect to an open (passwordless) WiFi network — this fires **before** any SSID rule is evaluated. Set to **— none —** to disable.

---

## 10. Settings — General

**Language** — Changes the interface language immediately.

**App mode** — Switch between Standalone, Companion, and Mixed.

**Tunnel groups** — Add, rename, reorder (↑↓), and delete groups. Deleting a group moves its tunnels to Uncategorized.

**Run wizard** — Re-runs the setup wizard.

---

## 11. Settings — Appearance

**Dark / Light theme** — Independent pickers for dark and light mode.

**Auto theme** — Follows the Windows system dark/light preference (polls every 5 seconds).

**Background notifications** — When enabled, a popup appears near the tray whenever MasselGUARD auto-switches a tunnel, showing the tunnel name and reason.

---

## 12. Settings — Advanced

**Installation** — Install to Program Files with optional Start with Windows. Uninstall removes all app files (your tunnel configs in `%APPDATA%\MasselGUARD\` are not deleted).

**DLL status** — Shows whether `tunnel.dll` and `wireguard.dll` are present. Required for Standalone and Mixed modes.

**WireGuard client** — Link to the official WireGuard app. Used in Companion and Mixed modes.

**Orphaned tunnel services** — Lists `WireGuardTunnel$` services left behind after a crash. Remove them individually or all at once.

**Import / Export** — See [§15](#15-import--export-settings).

**Log level** — Normal (OK + Warn only) or Extended (everything, including diagnostic `[DBG]` entries).

---

## 13. Pre/post scripts

Scripts run at four points around a tunnel connection. Set them on the **Scripts** tab when editing a local tunnel, or at the bottom of the metadata dialog for WireGuard-linked tunnels.

| Hook | When |
|---|---|
| Before connect | Immediately before the tunnel starts |
| After connect | After the tunnel is confirmed active |
| Before disconnect | Immediately before the tunnel stops |
| After disconnect | After the tunnel has stopped |

Click **Browse…** to select a `.bat` or `.ps1` file, or click **Embed** to write the script content inline (local tunnels only).

Scripts run as the current user. Exit code and output are logged under Extended log level.

---

## 14. Quick Connect

Connects a tunnel from a `.conf` file without permanently importing it.

1. Click **Quick Connect** (status bar or tray menu)
2. Select a `.conf` or `.conf.dpapi` file
3. The tunnel connects and appears as **⚡ filename** at the top of the list

The config is never saved. The tunnel disappears from the list after disconnecting.

---

## 15. Import / Export settings

Both buttons are in **Settings → Advanced**.

### Export

Saves your settings to a `.masselguard` file. A warning is shown before exporting, explaining:
- Tunnel configurations are not included
- Compatibility with future versions of MasselGUARD is not guaranteed

The export includes: WiFi rules, tunnel groups, default action, open network tunnel, disable WiFi rules, app mode, language, themes, log level, and popup setting.

### Import

1. Click **Import settings** and select a `.masselguard` or `.json` file
2. If the file was exported from a different version of MasselGUARD, a warning shows the version mismatch and asks whether to continue
3. On confirmation, all compatible settings are applied and saved immediately

Rules and tunnel groups replace your current settings entirely. Unknown fields from newer exports are silently ignored.

---

## 16. The activity log

The right panel of the main window. Each line starts with a timestamp (`HH:mm:ss`). Detail lines show with a `↳` prefix.

**Colours in Normal mode:** green = success, orange/red = warning.

**Extended mode adds:** blue/accent = network changes and mode changes, muted = `[DBG]` diagnostic details (connect timing, tunnel config, WiFi detection).

Change verbosity in **Settings → Advanced → Log level**.

Click **Export Log** to save the current log as a `.txt` file.

---

## 17. System tray

Click the shield icon to show or hide the main window. Right-click for the tray menu: tunnel list, Quick Connect, Settings, Exit.

**×** in the main window minimises to tray — it does not stop the application or any active tunnels. Use **Exit** from the tray menu to close completely.

The tray icon shows a green badge with the count of active tunnels.

---

## 18. Themes

Six built-in themes: Default Dark, Default Light, Grey Dark, Grey Light, High Contrast Dark, High Contrast Light.

Switch with the **🌙/☀/⚡** button in the title bar, or choose specific themes in **Settings → Appearance**.

Custom themes: add a folder to the `theme\` directory next to `MasselGUARD.exe` containing a `theme.json` file. See `theme\THEME_INFO.md` for the full property reference. Themes hot-swap instantly.

---

## 19. Multiple languages

Supported: English, Dutch, German, French, Spanish. Change in **Settings → General**.

To add a language: copy `lang\en.json` to `lang\<code>.json`, translate the values, add `"_code"` and `"_language"` at the top, and restart.

---

## 20. Frequently asked questions

**"Already running" after installing to a new location.**
The previous process's single-instance lock can take a moment to release after the exe is moved. Wait 2–3 seconds and try again. MasselGUARD will retry up to 2 seconds automatically before deciding a real second instance is running.

**My WiFi rule is not firing.**
Check that the SSID in the rule exactly matches your network name — it is case-sensitive. Enable Extended logging to see what SSID MasselGUARD is detecting. Also check that Disable WiFi rules is off in Settings → WiFi Rules.

**Tunnel connects but immediately shows as disconnected.**
The wireguard-NT service process exits after loading the kernel driver — this is normal behaviour. Windows Event Viewer may show a false termination error; ignore it.

**I cannot import a `.conf` file.**
The file must be a valid WireGuard config with at minimum an `[Interface]` section containing `PrivateKey` and `Address`.

**My pre/post script is not running.**
Check the file path has no typos and no unquoted spaces. Enable Extended logging — script execution, output, and exit codes are logged as `[Script]` entries.

**The application crashes on startup.**
Most likely the .NET 10 Desktop Runtime is not installed. Download it from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0).

**Can I transfer settings to another computer?**
Use **Settings → Advanced → Export settings** to save a `.masselguard` file, then **Import settings** on the other machine. Tunnel configs cannot be transferred — they are DPAPI-encrypted and tied to your Windows account. Re-import the `.conf` files on the new machine.

**Can I run MasselGUARD without Administrator rights?**
No. Creating and managing Windows services (how WireGuard tunnels work) requires Administrator privileges.
