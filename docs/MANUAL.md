# MasselGUARD - User Manual

**Version 4.6.0 - Wired Weasel**

---

## Contents

1. [Introduction](#1-introduction)
2. [Installation and run modes](#2-installation-and-run-modes)
3. [First run: the setup wizard](#3-first-run-the-setup-wizard)
4. [The main window](#4-the-main-window)
5. [Managing tunnels](#5-managing-tunnels)
6. [Connecting and disconnecting](#6-connecting-and-disconnecting)
7. [Default action, open network protection and connect on start](#7-default-action-open-network-protection-and-connect-on-start)
8. [Automation rules](#8-automation-rules)
9. [DNS automation](#9-dns-automation)
10. [Data usage](#10-data-usage)
11. [Settings overview](#11-settings-overview)
12. [Settings: General](#12-settings-general)
13. [Settings: Startup](#13-settings-startup)
14. [Settings: Appearance](#14-settings-appearance)
15. [Settings: Notifications](#15-settings-notifications)
16. [Settings: Diagnostics](#16-settings-diagnostics)
17. [Settings: About](#17-settings-about)
18. [Settings: WireGuard](#18-settings-wireguard)
19. [Settings: DNS](#19-settings-dns)
20. [Settings: Automation](#20-settings-automation)
21. [Settings: Activity log](#21-settings-activity-log)
22. [Settings: History](#22-settings-history)
23. [Import / export settings and managed presets](#23-import--export-settings-and-managed-presets)
24. [Pre/post scripts](#24-prepost-scripts)
25. [Quick Connect](#25-quick-connect)
26. [The activity log](#26-the-activity-log)
27. [History charts](#27-history-charts)
28. [System tray](#28-system-tray)
29. [Kill switch](#29-kill-switch)
30. [Auto-reconnect](#30-auto-reconnect)
31. [Themes](#31-themes)
32. [Font override](#32-font-override)
33. [Languages](#33-languages)
34. [Keyboard and window behaviour](#34-keyboard-and-window-behaviour)
35. [Frequently asked questions](#35-frequently-asked-questions)
36. [Command-line interface (CLI)](#36-command-line-interface-cli) - see also [`CLIManual.md`](CLIManual.md) for the full reference
37. [Background service](#37-background-service)

---

## 1. Introduction

MasselGUARD is a WireGuard client and network-automation tool for Windows. It runs your WireGuard tunnels on its own bundled WireGuard engine, and can switch tunnels and DNS resolvers automatically based on the network you join, a schedule, or whether the network is on your trusted list. It works just as well as a plain manual WireGuard front-end.

MasselGUARD is built from **features** you can turn on or off independently (Settings → General → Features):

| Feature | What it does |
|---|---|
| **WireGuard** | Tunnel management: connect/disconnect, groups, kill switch, auto-reconnect, split tunneling, data usage |
| **DNS** | DNS profiles and per-network DNS resolvers (plain or encrypted DoH), with or without a tunnel |
| **Automation** | Rules that switch tunnels and/or DNS when the network changes or on a schedule |
| **Activity log** | A live event log (optionally kept across restarts) |
| **History** | Records connections, Wi-Fi networks and DNS over time and draws the history charts |

Any combination works, including running with WireGuard or DNS (or both) turned off.

---

## 2. Installation and run modes

**Requirements:** Windows 10 or 11 - **x64 or ARM64** (download the matching build: `MasselGUARD-x64.zip` for Intel/AMD PCs, `MasselGUARD-arm64.zip` for Windows-on-ARM devices), the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) for that architecture, and Administrator rights. On Windows-on-ARM the ARM64 build is required for tunnels; if unsure which you have, Settings → About shows the running architecture.

> Don't run MasselGUARD from a OneDrive (cloud-synced) folder: the WireGuard service runs as LocalSystem and can't read files there, so tunnels fail with "Element not found". Use a normal local folder or install it.

### Run modes

| Mode | Meaning |
|---|---|
| **Standalone** | Running as a portable exe; no installed version detected |
| **Managed (portable)** | An installed version exists; this is a separate copy |
| **Managed** | Running from the installed location - shown **green** in the footer |

### Installing

1. **Settings → Startup → Installation → Install** (or the install choice in the setup wizard)
2. Choose a parent folder
3. Optionally enable **Start with Windows** (with the [background service](#37-background-service) a per-user startup entry, otherwise a Scheduled Task; either way no UAC prompt on later launches)
4. MasselGUARD relaunches from the installed location

The same place shows the current run mode and offers **Uninstall** when running the installed copy. The installer also offers to install the [background service](#37-background-service) (no UAC prompts when you start MasselGUARD).

### Managed (portable) - version prompt

When a portable copy runs next to an installed one and the versions **differ** (including build numbers), a prompt offers to overwrite the installed copy. Tick **Don't ask to update the installed version at startup** (Settings → Startup) to stop the prompt.

---

## 3. First run: the setup wizard

The wizard runs on first launch and when you start a newer version than the one that last ran it. Re-run it any time with **Run Setup Wizard** at the bottom of the Settings sidebar. Every choice in the wizard also exists in Settings.

| Step | Content |
|---|---|
| **0 - Welcome** | Interface language, and **Import previous settings** from a `.masselguard` file |
| **1 - Features** | Pick what you'll use: **WireGuard VPN**, **DNS automation**, **Automation**, **Activity log** (with Normal / Enhanced detail) and **History** (with which charts to show: Timeline, Data usage, DNS). Same switches as Settings → General → Features |
| **2 - Appearance** | Colour scheme (Dark / Light / Follow Windows), theme picker, **Download more themes…**, and whether to show the title-bar Charts and Activity log buttons |
| **3 - Startup** | Install to Program Files or run portable, Start with Windows, Start minimized to tray, Confirm disconnect on exit |
| **4 - Automation** | How rules work, **Disable automation - connect manually**, **Show the Automation panel**, and the title-bar Automation button (with "hiding also disables automation") |
| **5 - WireGuard behaviour** | Auto-reconnect (Off / Per tunnel / Always), Kill switch (Per tunnel / Always), DNS leak indicator, title-bar tunnels button. **Skipped** when WireGuard is off |
| **6 - DNS behaviour** | Enable DNS automation and the title-bar DNS button. **Skipped** when DNS is off |
| **7 - Notifications** | Background tunnel notifications + duration, and what History records (tunnels, Wi-Fi, DNS) |
| **8 - Done** | A summary of every choice |

---

## 4. The main window

### Title bar

From left to right after the logo and name: five **section buttons** that show or hide the main-window sections - **WireGuard** (shield), **DNS** (globe), **Automation** (robot), **Charts** (bar chart) and **Activity log** (menu lines) - then **Settings** (gear), minimize, maximize and **close to tray**. A section button is accent-coloured while its section is shown. Each button can be hidden, and each of WireGuard / DNS / Automation can be set to *hide only* (the feature keeps running) or *hide and disable* (Settings → General → Features). Themes can replace the section icons.

Below the title bar: **WiFi:** with the current network name (click it to see the Automation rules for that network) and the **⚡ Quick Connect** button.

### Layout

WireGuard and DNS sit side by side on top; Automation and the Activity log below; the History charts under those. Whichever sections you show fill the window the same way every time, and a lone section takes the full width. The WireGuard and Automation lists always show at least four entries; the DNS profiles and the log beside them scroll instead of stretching the window. When every section is hidden, the window says so and points you to the title-bar buttons.

### WireGuard panel

Title **WIREGUARD** with the total tunnel count, then the **group tabs**.

Columns: **WireGuard tunnel** | **Status** | **Rules** | **Action**

- **Colour strip** - the tunnel's group colour
- **Badges** after the name - `⚡` default action, `🔓` open network protection, `🚀` connect on start
- **Status** - a status dot and uptime for active tunnels, followed by the tunnel's **usage bars** (day / week / month) or **rings** - see [§10](#10-data-usage)
- **Rules** - how many Automation rules use this tunnel; click to highlight them (hide the column in Settings → Automation)
- **Action** - Connect / Disconnect (a 🛑 marker appears when a data cap disconnected the tunnel, until you next start it)

**Toolbar:** ＋ Add | ✎ Edit | ⬇ Import | **Behaviour** | **Export** | Delete

**Right-click a tunnel** for: set/clear default action tunnel, set/clear open network protection, set/clear connect on start.

### DNS panel

Title **DNS PROFILES**. Columns: **DNS profile name** | **Type** | **Rules** | **Action**. The Action button applies a profile manually (it stays until you undo it); **Revert to default** hands DNS back to the tunnel or the system. **Rules** counts the Automation rules that use the profile (click to highlight them).

**Toolbar:** Revert to default | Add… | Edit… | Remove | **More…** (Browse DNS servers, Import, Export). Drag rows to reorder.

**Use a profile for a moment.** Right-click a profile and choose **For 10 seconds / 1 minute / 5 minutes / 15 minutes**: the profile applies right away and MasselGUARD goes back to automatic DNS by itself when the time is up (for example, Google DNS for a minute to get past a DNS ad-block). The footer shows a countdown (`Google · 0:42`); click it to stop at once. **Shift+Enter** on the selected profile uses the default bypass length (*Settings > DNS > Default bypass length*, 60 seconds unless you change it; a grey "x.xx minutes" next to the box shows lengths above a minute). A tray message confirms when a bypass starts, stops or ends. The same lengths are in the tray icon's right-click menu (see below). Browsers with their own encrypted DNS (Secure DNS in Chrome and Edge, DoH in Firefox) ignore the Windows resolver, so turn that setting off in the browser for this to work there.

**Bypass profile.** Right-click a profile and choose **Mark as bypass profile** (or pick it in *Settings > DNS > Bypass profile*). It gets a `⏱` mark, and the footer shows **DNS Bypass: <name>**. Click that footer item, press the **bypass shortcut** (**Ctrl+Alt+D** by default), or right-click the tray icon and choose **Bypass: <name>** (just above DNS) to switch to it for the default bypass length (*Settings > DNS > Default bypass length*, 5 to 3600 seconds, 60 by default); press the shortcut or click the item again while it runs to stop it. Switch the whole feature off with *Settings > DNS > Timed DNS override*.

**Changing the shortcut.** *Settings > DNS > Bypass shortcut*: click the box and press the keys you want (Ctrl, Alt or Win plus one key; a letter, a digit, F1 to F24 or a navigation key). **Clear** removes the shortcut. It is stored in `config.json` as `BypassShortcut` (for example `"Ctrl+Alt+F9"`; an empty text means no shortcut, and an unusable value falls back to `Ctrl+Alt+D` with a line in the activity log). By default it only works while the MasselGUARD window has focus; turn on *Work in every app* (`BypassShortcutGlobal`) to register it system-wide, so it also works in other programs and while MasselGUARD is hidden in the tray. If another program already uses that combination, the system-wide registration fails (logged) and it still works with the window focused.

**From Windows Explorer.** Turn on *Settings > DNS > Windows right-click menu* and right-click the desktop, the empty space of a folder, or a folder: **DNS bypass (MasselGUARD)** offers the same lengths and **Stop now**, with no UAC prompt and no terminal window. It works through the running MasselGUARD window or, when no window is running, through the [background service](#37-background-service); with neither it shows a short message. On Windows 11 the entry is under **Show more options** (Shift+F10). The command-line equivalent is `MasselGUARDcli dns bypass [seconds|stop|toggle]` (see the [CLI manual](CLIManual.md)).

### Automation panel

Title **AUTOMATION**. Columns: **Name** | **Network (SSID)** | **Action** | **Hits** | **WireGuard tunnel** | **DNS**.

**Toolbar:** + Add | Edit | Delete | Disable/Enable. Drag rows to change the evaluation order. See [§8](#8-automation-rules).

### Activity log

Header **ACTIVITY LOG** with the entry count, columns **Time** | **Event**, and **Clear Log**, **Export Log** and **Expand** (opens the log in its own resizable window that stays live). See [§26](#26-the-activity-log).

### History panel

**History:** toggles for **WireGuard timeline**, **Data usage** and **DNS** (any combination), the time range (**24 h / 7 d / 31 d**), the combined live traffic of all active tunnels, and ◀ ▶ session navigation. See [§27](#27-history-charts).

### Footer

Left: run mode (green when Managed). Right: the primary network (Wi-Fi name, or the wired network), the DNS servers in use, a running timed DNS override with its countdown, the **DNS Bypass** item, and the `⚡` default and `🔓` open-network tunnels.

---

## 5. Managing tunnels

### Adding, editing and importing

- **＋ Add** opens the tunnel editor with an empty config (**Generate** creates a key pair).
- **⬇ Import** offers **Import from file** (`.conf`, `.conf.dpapi`, or an encrypted `.mgconf` export - you're asked for its password), **From WireGuard (.conf / .zip)…** (one or more configs, or a WireGuard "export to zip" archive) and **Scan QR code**. If a tunnel with the same name exists you can overwrite it, import under a new name, or cancel.
- **✎ Edit** (or double-click) opens the editor for the selected tunnel.

Tunnel configs are stored DPAPI-encrypted in `%APPDATA%\MasselGUARD\tunnels\`.

### The tunnel editor

Name and **Group** at the top, then four tabs:

| Tab | Content |
|---|---|
| **Fields** | Interface (private key, address, DNS, listen port, MTU), Peer (public key, preshared key, endpoint, allowed IPs, keepalive) and **Scripts** (see [§24](#24-prepost-scripts)) |
| **Split** | Split tunneling (below) |
| **Options** | **DATA USAGE** - per-period usage reference, caps and Kill at cap (see [§10](#10-data-usage)) |
| **Raw config** | The full config as text, including MasselGUARD's own settings as `# MasselGUARD-…` comment lines. Edits here are applied back to the form on save |

The footer has toggles for **⚡ Default action**, **🔓 Open network protection**, **🔒 Kill switch** and **🔄 Auto-reconnect**. The last two show *(controlled globally)* when their mode is **Always** (Settings → WireGuard); the auto-reconnect toggle is hidden when that mode is **Off**.

### Tunnel groups

Create and manage groups in **Settings → WireGuard → Tunnel groups**: name, colour, hide/show the group tab, set a startup default, reorder and delete. **Drag a tunnel onto a group tab** to move it into that group.

### Drag to reorder

Drag tunnel rows to reorder them within the current group. A drop line shows exactly where the tunnel will land.

### Exporting a tunnel

Select a tunnel and use **Export** to hand it to another device or keep a backup. Three formats:

- **Plain file (`.conf`)** - a standard WireGuard config; import it into any WireGuard client, phone, or router.
- **Encrypted file (`.mgconf`)** - password-protected with AES-256-GCM. Unlike the at-rest storage it's **portable** - open it on any machine with the password. There's no recovery if the password is lost.
- **QR code** - scan straight into the WireGuard mobile app (also on the tunnel's right-click **Show QR code**).

**Include MasselGUARD settings** (file formats only) bundles the tunnel's extras - group, notes, scripts, kill switch, auto-reconnect, data caps and split-tunnel settings - as readable `# MasselGUARD-…` comment lines that other WireGuard clients ignore, so the `.conf` stays universally importable. MasselGUARD restores them on import (the CLI too: `MasselGUARDcli import file.mgconf --password <pw>`).

> The exported config contains the tunnel's **private key** - keep plain and QR exports private; use the encrypted format to share safely. Export is disabled when a managed policy locks *Tunnels*.

### Split tunneling

By default a tunnel is a **full tunnel** - all your traffic goes through it. The editor's **Split** tab lets you route only *some* traffic through it, by destination IP range:

- **Off** - route everything through the tunnel (the default).
- **Exclude these ranges** - a full tunnel *except* the ranges you list. Use this to keep specific destinations on your normal connection - a local printer or NAS (`192.168.1.0/24`), or a service you don't want tunnelled.
- **Only these ranges** - only the listed ranges go through the tunnel; everything else uses your normal connection.

Enter ranges **one per line**, in CIDR notation (`10.0.0.0/8`, `192.168.1.0/24`) or as a single address (`10.0.0.5`, treated as a `/32`). **IPv4 and IPv6** are both supported. An invalid entry is flagged when you save.

The **Effective AllowedIPs (preview)** underneath shows *Base* (the `AllowedIPs` from the Fields tab) and *Effective* (what the tunnel will actually route), updating as you type.

Notes:
- With a **kill switch** active, *Exclude* ranges are still allowed out over your normal connection, so excluded traffic keeps working while the rest is protected.
- Split settings **travel with an export** and are shown by `MasselGUARDcli info <name>`.
- *Per-app* split (by application instead of IP range) is not available yet.

---

## 6. Connecting and disconnecting

Click **Connect** / **Disconnect** on a tunnel's row, or use the tray menu. Automation does this for you on network changes.

Active tunnels show elapsed uptime: `< 1 min` → `Xs`, `< 1 h` → `Xm YYs`, `< 1 day` → `Xh YYm`, `≥ 1 day` → `Xd YYh YYm`.

Connecting a tunnel that has reached a **Kill at cap** limit asks first (see [§10](#10-data-usage)).

---

## 7. Default action, open network protection and connect on start

### Default action

What happens when you join a network that no rule matches: **Do nothing**, **Disconnect all tunnels**, or **Activate** a tunnel. The chosen tunnel shows `⚡` in the list and in the footer.

> **Default action vs. Trusted networks.** Both are catch-alls, but a *Trusted-networks* rule is evaluated **before** the default action and reacts to whether you're on a trusted SSID. Each trusted rule covers one direction and only acts on that side - the default action still fills the side it doesn't cover. See **§8 → Rule evaluation order**.

### Open network protection

Activates a tunnel automatically on **passwordless** Wi-Fi, before any rule. The chosen tunnel shows `🔓`.

### Connect on start

Connects one tunnel automatically when MasselGUARD starts, after the first rule evaluation (so a rule or the default action doesn't undo it). The tunnel shows `🚀`. Combine it with **Start minimized** (Settings → Startup) for a silent connect-and-hide start.

### Where to set them

- The **Behaviour** button under the tunnel list - saves immediately
- **Right-click** a tunnel - saves immediately
- **Settings → Automation** (default action, open network protection) - saves on Settings Save
- The tunnel editor's footer toggles (default action, open network protection) - saves with the tunnel

---

## 8. Automation rules

Rules live in the **Automation** panel on the main window. Every add / edit / delete / enable saves immediately. Automation needs the **Automation** feature on and is paused while **Manual mode** is active.

### Create a rule from the network you are on

Right-click the **network name in the footer**, or click the **From network** button next to **+ Add** in the Automation panel. For each connected network (Wi-Fi and cable) the menu offers **Create rule from this network…**, which opens the rule dialog already filled in with the one condition that identifies it best: the **SSID** on Wi-Fi; on a cable the **DNS suffix**, else the **gateway MAC**, else the **subnet**. A line in the dialog says what was added, and **+ Add condition** combines it with more. You only choose the tunnel and/or DNS profile and press OK.

- If a rule **already matches** the network, the menu offers **Edit rule "X" (position N)…** instead, plus **Create another rule anyway…**.
- After saving, a note appears when an **earlier** rule also matches that network and so wins (first match wins, top-down); drag the new rule above it to change that.
- In **Simple Wi-Fi mode** only Wi-Fi networks with a name get a suggestion; wired networks are shown with a short explanation.
- The menu is not offered when a managed policy locks the rules.

### Rule dialog fields

| Field | Description |
|---|---|
| **Trigger type** | **WiFi network**, **Schedule**, or **Trusted networks (SSIDs)** - see below |
| **Name** | Display name - generated from the trigger and tunnel as you type, until you edit it yourself |
| **WiFi Network (SSID)** | *(WiFi type)* Network name, case-sensitive. **Use Current** fills in the network you're on |
| **Active days / Start / End** | *(Schedule type)* Days of the week and an `HH:mm` window |
| **Activate this rule** | *(Trusted type)* When NOT on a trusted network, or when on one |
| **WireGuard Tunnel** | The tunnel to activate. Leave blank to **disconnect** all tunnels |
| **DNS profile** | Optionally apply a DNS profile too. Leave the tunnel blank for a **DNS-only** rule |
| **Times triggered** | The hit counter, with **(Re)set counter** (type `0` to clear) |

### Trigger types

- **WiFi network** - fires when you join that exact named network.
- **Schedule** - fires during a day-of-week + time window (e.g. Work 09:00-18:00, Mon-Fri). Checked on a one-minute timer, independent of Wi-Fi changes; overnight windows such as 22:00-06:00 work.
- **Trusted networks** - reacts to your trusted-SSID list (Settings → Automation), in **one of two directions**:
    - **When NOT on a trusted network** - e.g. a **full tunnel** on public Wi-Fi.
    - **When on a trusted network** - e.g. a **split tunnel** at home or work.

    A trusted rule acts **only on its own side**; the other side falls through to your other rules and the default action. Add two trusted rules to cover both directions.

### Rule evaluation order

On every Wi-Fi change the engine walks these steps and **stops at the first match**:

1. **Manual mode** - if automation is off, nothing happens.
2. **Open network protection** - on a passwordless network with a tunnel assigned, that tunnel is activated.
3. **WiFi rules** - an enabled rule whose SSID equals the current network (list order; first match wins).
4. **Trusted-network rules** - each enabled trusted rule in list order; it matches only when the current network is on **its** side of the list.
5. **Default action** - the fallback.

Schedule rules run on their own timer. When Wi-Fi drops **entirely**, only *Default action = Disconnect* applies. DNS follows the same order on its own axis (see [§9](#9-dns-automation)), so a rule's tunnel and its DNS profile are independent.

#### Default action vs. Trusted networks

| | **Default action** | **Trusted-network rule** |
|---|---|---|
| Decides based on | Nothing - same result for every unmatched network | Whether the SSID is on your trusted list (and the rule's direction) |
| Fires on | Every unmatched network | Only its side (on-list *or* not-on-list) |
| Runs when Wi-Fi drops completely | Yes | No (no SSID to classify) |

A common setup: one *not-on-list → full tunnel* rule ("VPN everywhere except home and office"); add an *on-list → split tunnel* rule to bring a different tunnel up at home or work.

### Enable / disable, hits and order

- **Disable** a rule to keep it without it firing; it greys out with a `⊘` marker. **Enable** turns it back on.
- **Hits** counts how often a rule has fired (kept across restarts). Reset or set it from the rule dialog; the change is logged, e.g. `Counter: 42 → 10`.
- **Drag** rows to change the evaluation order; the drop line shows where the rule lands.

---

## 9. DNS automation

DNS automation applies a **DNS resolver per network - even with no tunnel active** ("on any open Wi-Fi, use encrypted Cloudflare", "on the office SSID, use the internal resolver"). While a tunnel is connected, its own DNS takes over; when it drops, your DNS choice is re-applied. Your original DNS is saved before the first change and restored when MasselGUARD closes (and recovered automatically after a crash).

### DNS profiles

A profile is a named set of resolvers: IPv4/IPv6 servers and an **Encryption** mode:

- *Plain* - standard unencrypted DNS.
- *DNS-over-HTTPS (DoH)* / *Automatic* - encrypted DNS. **DoH needs Windows 11.** Well-known resolvers need no template; for a custom resolver enter its **DoH template URL**. Tick **Require encryption** to fail closed (never fall back to plaintext).

**Browse DNS servers** opens a searchable list of public resolvers: tick the ones you want, read what each blocks and logs (details pane, with links to the provider's website and privacy policy) and press **Add selected**. **Test speed** sends a few DNS queries from this PC to the servers in the current list (tick **Only ticked servers** to test just those) (it resolves the host name in the **Test name** box, masselink.net by default; change it to test with a name you care about) and shows each server's response time, with **Fastest first** to sort by it (a DoH-only server is timed over DoH, which includes the encrypted handshake, so it looks slower). Servers already in your list start ticked: untick one to remove its profile. A coloured bar at the left of each row shows what **Apply (+N new, -M removed)** will do (grey = stays, accent = new, red = will be removed), and removing asks for confirmation first; a resolver that needs a personal value (the NextDNS configuration id) shows an empty text box on its row, labelled with the field's name (CONFIG_ID): fill it in before you Apply (it is part of the server address, so only letters, digits, - and _ are allowed). The list is kept on GitHub (repository setting under **Settings > Diagnostics > DNS server list**), refreshed at most once a day (only changed files are downloaded, and a list with a broken file is not used; invalid entries are skipped), and the servers you add are saved on your PC as well (no copy of the list ships with the app, so the first use needs an internet connection). Profiles can be imported and exported from the DNS panel's **More…** menu.

### Which DNS applies

- **Default DNS** (Settings → DNS) - any network without a more specific rule. *- none -* leaves the network's DNS alone; *Automatic (DHCP)* forces the network-provided DNS.
- **Open-network DNS** - applied on passwordless Wi-Fi.
- **A rule's DNS profile** - on that rule's network or schedule.
- **Manually applied** - the Action button in the DNS panel applies a profile until you click **Revert to default**; a manual profile also wins over a tunnel's DNS.

When a DNS profile is active, a small badge is drawn over the tray icon (themes can restyle it).

### DNS leak protection

On a split-tunnel connection Windows can send DNS queries out other adapters. **Settings → DNS → DNS leak protection** can turn off *Smart multi-homed name resolution* and *Parallel A / AAAA queries* (global Windows settings; reconnect or reboot to apply), and choose how to be alerted to a possible leak (status icon, log warning, tray notification).

The CLI command `MasselGUARDcli dns status` shows the configuration and each interface's live resolvers.

---

## 10. Data usage

### Usage bars and rings

Every tunnel row shows its usage for **today**, **this week** and **this month**, as slim **bars** or compact **rings** (Settings → WireGuard → Display → *Data-cap usage indicator*), also while disconnected. Each period measures against one of two references, set per period in the tunnel editor → **Options** → **DATA USAGE**:

- **Use history** (the default) - the tunnel's **typical usage**: its average over the last 365 days (or over all of its history when that is shorter), scaled to a day, a week or a month. This is informational only: the bar is drawn in the accent colour, with no warnings and no *Kill at cap*. While ticked, the MB box is greyed and shows that typical value. A tunnel with no history yet shows an empty track.
- **Your own cap** - untick *Use history* and type a limit in MB (0 = none). The box starts from the typical value, so a cap based on your real usage is one click. The bar turns amber near the limit and red once over.

Hover a bar or ring to see, per period, whether it compares against **your cap** or **history** (with the typical value and how many days it's based on). Tick **Hide usage** to hide one period.

### Warnings and Kill at cap

When usage passes one of your caps you get a one-time log entry and tray notification per period, and the row is highlighted. Tick **Kill at cap** to enforce it: the tunnel is disconnected when the limit is reached (sticky *Ignore & reconnect* notification, 🛑 row marker). Connecting over the limit asks first - a Yes/No prompt when you click Connect, or an interactive Connect / Cancel notification when a rule tries. Editing the caps re-arms enforcement.

Usage comes from the connection history, so keep **Settings → History → Capture → Tunnel connections** on. Figures are estimates - MasselGUARD isn't responsible for inaccurate reporting or for tunnels being disconnected (or not) as a result.

### Data usage chart

The History panel's **Data usage** layer charts per-tunnel usage over the selected range, with a red marker where a cap was reached. See [§27](#27-history-charts).

---

## 11. Settings overview

The sidebar has two groups:

- **Application:** General · Startup · Appearance · Notifications · Diagnostics · About
- **Feature settings:** WireGuard · DNS · Automation · Activity log · History

A feature tab is dimmed when its feature is turned off; WireGuard, DNS and Automation then show an **Enable** shortcut instead of their settings. Most changes apply when you click **Save** (Cancel or Esc discards them). **Run Setup Wizard** sits at the bottom of the sidebar.

---

## 12. Settings: General

### Language

Interface language - applies immediately. Hold **Shift** while starting MasselGUARD to reset it to English (see [§35](#35-frequently-asked-questions)).

### Features

One card per feature - **WireGuard**, **DNS**, **Automation**, **Activity log**, **History** - each with:

- **Enable feature** - turning a feature off stops its work (e.g. no log lines are written, no history is recorded).
- **Title-bar button** - **Show / hide** (the button only hides the section) or **Enable / disable** (the button also turns the feature off).
- **Show button** - hide the title-bar button altogether.

### Import / Export

**Export settings**, **Import settings** and **Export settings as preset…** - see [§23](#23-import--export-settings-and-managed-presets).

---

## 13. Settings: Startup

- **Start with Windows** - with the MasselGUARD service installed, a per-user startup entry (no administrator rights needed; MasselGUARD starts unelevated and the service does the privileged work). Without the service, a Scheduled Task at `RunLevel=Highest`, so MasselGUARD starts elevated without a UAC prompt
- **Start minimized** - launch straight to the tray
- **Free memory while hidden in the tray** - when the window is closed to the tray, MasselGUARD compacts its memory and returns the unused part to Windows (default on). The first redraw after reopening can be slightly slower
- **Confirm disconnect on exit** - ask before disconnecting active tunnels when exiting (default on)
- **Installation** - run mode, **Install** / **Uninstall**, and *Don't ask to update the installed version at startup*
- **Background service** - **Install service** / **Remove service**. The service runs the privileged part (tunnels, DNS, kill switch); MasselGUARD uses it automatically when it is running, otherwise it elevates itself as before. Installing asks for administrator approval once, and installs MasselGUARD to a local folder first when needed (the service cannot run from OneDrive or a network path). The installer and the setup wizard offer the same choice. Terminal: `MasselGUARDcli service install | uninstall | status`.

---

## 14. Settings: Appearance

### System theme

**Light**, **Dark** or **Follow Windows**.

### Theme

A single theme picker; every theme has a dark and a light variant and the system-theme choice picks one. **System (Windows colors)** uses the Windows accent palette. **Manage themes…** opens the Theme Manager and **Download themes…** the community theme browser (see [§31](#31-themes)). **▶ Dark** / **▶ Light** preview the selected theme for 10 seconds.

### Font

**Override font** replaces the theme's typeface - see [§32](#32-font-override).

### Data-cap usage indicator

**Bars** (default) or **Rings** on each tunnel row.

---

## 15. Settings: Notifications

- **Background tunnel notifications** - a tray notification when a tunnel switches in the background (rule, default action, open network protection)
- **Notification duration** - how long notifications stay up

Notifications raised together (e.g. Wi-Fi and DNS at start-up) are combined into one. The app name shown comes from the theme.

---

## 16. Settings: Diagnostics

- **Diagnostics / Tester** - live tests with a detailed log: **WireGuard client** (connects an available tunnel, confirms the handshake, disconnects), **DNS profiles** (applies a throwaway profile, reads it back, restores) and **Command line (CLI)** (runs the bundled CLI and its self-test). **Copy log** / **Clear Log**.
- **System diagnostics…** - a read-only overview of environment, connected network adapters (MTU, metric), active tunnels, DNS in use, and an optional *Check public IP* button. **Copy all** copies it as an English plain-text report for support. Also in the tray menu.
- **Theme repository** - the GitHub repository the theme browser downloads from (blank or **Default** = the official one).
- **Orphaned tunnel services** - leftover `WireGuardTunnel$` services from a crash or improper shutdown; **Scan**, then remove one or **Remove All**.

---

## 17. Settings: About

A header card shows the theme's logo, **MasselGUARD**, the version and codename (e.g. `v4.6.0 · Wired Weasel`), the build stamp, the last update check and an update **status pill**:

- `↑` update available - **Update** installs it (download → extract → replace → relaunch)
- `🚀` running ahead of the latest release (a development build)
- `✓` up to date
- `-` never checked

**Check** runs a check now. **Check for updates:** On startup / Daily / Weekly / Manual. Installed community themes are checked at the same time; a banner links to any with updates.

Buttons: **GitHub**, **Website**, **Report an issue** (opens a new GitHub issue with your version details filled in) and **Copy version info** (version, build, architecture, Windows and .NET versions, language and theme - handy for bug reports).

Below: **Credits & license**, and **What's New** - the release notes, fetched live from `docs/WHATSNEW.md` on GitHub (with links to the website and repository if that fails).

---

## 18. Settings: WireGuard

- **Tunnel groups** - add, rename, colour, hide/show, set a startup default, reorder, delete
- **Auto-reconnect** - Off / Per tunnel / Always (default Always) - see [§30](#30-auto-reconnect)
- **Kill switch** - Per tunnel (default) / Always - see [§29](#29-kill-switch)
- **Config validation** - checks configs (keys, addresses, CIDRs, MTU, ports, endpoint) before connecting; **Skip config validation globally** is a last resort for unusual configs
- **Display** - *Always hide tunnel count*, *Hide empty tunnel groups*

---

## 19. Settings: DNS

- **DNS profiles** (first on the page) - Add…, **Browse DNS servers**, Edit…, Remove
- **Enable DNS automation** - the master switch (off by default; nothing changes until you turn it on)
- **Default DNS**, **Open-network DNS**, **Bypass profile** (the resolver for the quick bypass, see [DNS panel](#dns-panel)), **Address families** (IPv4, IPv6 or both)
- **Windows right-click menu** - adds the DNS bypass to Windows Explorer's right-click menu (off by default)
- **Timed DNS override** - the right-click lengths, the tray bypass entry and the keyboard shortcuts (on by default; the override always ends by itself)
- **Bypass shortcut** (default Ctrl+Alt+D) with the option to work in every app, and **Default bypass length** (5 to 3600 seconds, 60 by default)
- **DNS leak protection** (last) - Smart multi-homed name resolution, Parallel A / AAAA queries, and the possible-leak alerts (status icon, activity-log warning, tray notification)

See [§9](#9-dns-automation).

---

## 20. Settings: Automation

The rules themselves are on the main window; this page holds the settings around them:

- **Default Action** - Do nothing / Disconnect all tunnels / Activate tunnel
- **Open Network Protection** - the tunnel for passwordless Wi-Fi
- **Trusted networks (SSIDs)** - one SSID per line; **Add current WiFi network** appends the one you're on. Used by *Trusted networks* rules
- **Keep automation running when MasselGUARD is closed** - needs the [background service](#37-background-service). While no window is open the service keeps applying your rules (tunnel and DNS); closing MasselGUARD then leaves tunnels and the kill switch in place. Off by default.
- **Display** - the switch that shows the Automation panel on the main window, and *Show Rules column*, which removes the Rules column from both the tunnel list and the DNS profile list

---

## 21. Settings: Activity log

- **Log level** - **Normal** or **Enhanced** (extra detail: connect timing, config fields, script output, per-session traffic, settings changes)
- **Clear log on start** - on (default) starts each session with an empty log; off keeps the log in `%APPDATA%\MasselGUARD\masselguard.log` across restarts
- **Max log size (KB)** - older lines are dropped once the file is larger (0 = unlimited; default 512)

---

## 22. Settings: History

### Capture

| Toggle | Records |
|---|---|
| **Tunnel connections** | Tunnel uptime and traffic (`tunnel_history.json`) - also the source for data usage |
| **WiFi (SSID)** | Wi-Fi networks and whether they were open (`wifi_history.json`) |
| **DNS (profiles)** | Which DNS profile or resolver was active |

### Show

Whether the **Tunnel connections**, **WiFi (SSID)** and **DNS** layers are drawn in the charts. A Show toggle is disabled while its Capture toggle is off, and keeps its value for when capture comes back on.

### Activity chart

**Time range:** Last 24 hours · Last 7 days · Last 31 days (also switchable in the chart itself).

**Refresh rate:** the chart is rebuilt from scratch on every refresh, so longer intervals use less CPU and memory. Two settings, in seconds: **while nothing is connected** (5 to 60, default 10) and **while a tunnel is connected** (1 to 10, default 2). Values outside the limits are corrected. Resizing the window or changing the range or theme always redraws at once.

### Connection history

A list of past connections (tunnel, duration, how it was started); hover a row for period and traffic. **Clear history** removes it.

---

## 23. Import / export settings and managed presets

### Export and import

**Settings → General → Import / Export.**

- **Export settings** saves every setting to a `.masselguard` file (JSON). Tunnel configs are **not** included - export tunnels individually (see [§5](#5-managing-tunnels)).
- **Import settings** applies a `.masselguard` file and saves it. A file from a different version asks first. Afterwards you're offered a restart so everything takes effect. Also available in the setup wizard's first step.

### Managed preset (locked settings)

A **managed preset** lets you ship a copy in which settings are pre-set and **locked** - for a company rollout, a family laptop, a kiosk, or any "configure once, hand it out" scenario.

**One file, two roles.** A `.masselguard` file always holds a full snapshot of the settings, plus an optional **`Locked`** list. What it does depends on where it is:
- **Imported** (wizard or Settings → General → Import) → *all* its settings apply and stay **editable**; `Locked` is ignored.
- **Placed next to `MasselGUARD.exe`** → only the settings listed under `Locked` are **forced and locked**; everything else in the file is ignored.

**How the lock works.** At start-up the app looks for any `*.masselguard` next to the exe. If one has a `Locked` list, those settings are forced and re-applied on every save, so editing `config.json` by hand can't override them. Delete the file and everything unlocks on the next launch. `MasselGUARDcli.exe` in the same folder obeys the same policy.

> **Soft lock, not tamper-proof.** Anyone who can write to the app folder can edit or delete the file. It's meant for managed distributions, not as a security boundary against a hostile local user.

**What a locked setting looks like.** A banner at the top of Settings - *"🔒 Some settings are locked by the &lt;policy name&gt; policy."* - and every locked control is greyed out. Rule Add / Edit / Delete are disabled when Automation is locked, and tunnel export when Tunnels is locked.

**Creating one.** Configure the app, then **Export settings as preset…**. Enter a **policy name** (shown in the banner) and tick the settings to lock - grouped by section; a section header ticks all its items. Tunnel definitions are never included. Drop the file next to the exe in your distribution.

**Themes.** If a locked theme isn't installed, the app downloads it from the theme repository on first launch, falling back to system colours.

**Installing.** When you install (Settings → Startup) with a `.masselguard` next to the exe, you're offered to copy it into the install folder so the installed copy stays locked.

**Format** (human-readable JSON; `PolicyName` and `Locked` are the policy bits):
```json
{
  "PolicyName": "Family Safe",
  "Locked": { "blocks": ["killSwitch", "updates"], "settings": ["Language"] },

  "Language": "en",
  "KillSwitchMode": "always",
  "UpdateCheckFrequency": "never",
  "Rules": [ … ]
}
```
Imported, everything applies; as a preset only `KillSwitchMode`, `UpdateCheckFrequency` (from the two blocks) and `Language` are forced and locked.

---

## 24. Pre/post scripts

Four hook points per tunnel - **Before connect**, **After connect**, **Before disconnect**, **After disconnect** - in the tunnel editor's **Fields** tab. Point to a `.bat` or `.ps1` file (**Browse…**) or **Embed** a script inline. Scripts run as the current user; exit code 0 means success. Output is logged at the Enhanced log level.

---

## 25. Quick Connect

**⚡ Quick Connect** (below the title bar) connects a `.conf` or `.conf.dpapi` file without importing it. It appears as `⚡ filename` at the top of the tunnel list and disappears after you disconnect.

---

## 26. The activity log

Two columns, **Time** | **Event**. Long and multi-line entries wrap under the Event column. The timestamp colour comes from the theme (falling back to its muted text colour when too faint).

- **Clear Log** clears the whole log, including the log file when it is kept across restarts.
- **Export Log** saves it as `.txt`.
- **Expand** opens it in a separate resizable window that stays live.

At the **Enhanced** level, each disconnect is followed by a continuation line with the session's duration and traffic:
```
↳ 2h 14m 07s  ·  ↑ 142 MB  ↓ 1.2 GB
```
After saving Settings, the changed fields are listed.

Log lines are always in English, so they're easy to share for support. Show or hide the log with its title-bar button; turn it off completely in Settings → General → Features.

---

## 27. History charts

The History panel draws up to three layers, toggled with **WireGuard timeline**, **Data usage** and **DNS**, over the last 24 h, 7 d or 31 d.

### WireGuard timeline

| Row | Content |
|---|---|
| Tunnel bar | Coloured segments per tunnel; stacked when tunnels overlap |
| Wi-Fi band | All networks on one row, a colour per SSID (when Wi-Fi is captured and shown) |
| Time axis | Tick marks with times |

### Data usage

A per-tunnel line chart with dots (hourly for 24 h, daily for 7/31 d), with a red ring where a tunnel's usage over the range first reached its cap.

### DNS

Which DNS profile or resolver was active over time, with colour profile pills.

### Hover and navigation

Hover anywhere for a tooltip of what was active at that moment: tunnel name, time range, duration, traffic (or live speed near "now"), the Wi-Fi network (🔒 secured / ⚠ open) and the DNS in use. The **◀ ▶** buttons step through tunnel sessions in the range and pin a tooltip to each. The legend under the chart lists the tunnels, networks and DNS profiles; click an entry to hide or show that series. The header shows the combined live traffic of all active tunnels (hover for a per-tunnel breakdown).

The panel is shown when History is on and at least one layer has something to draw. Charts and dates follow the interface language.

---

## 28. System tray

The tray icon shows whether a tunnel is active (themes can supply their own icons), with a small badge when a DNS profile is applied.

**Tray menu:** Show Window · **Tunnels** (connect/disconnect per tunnel, Disconnect All) · **DNS Profiles** · **System diagnostics…** · Exit

Double-click the icon to show the main window. The window's ✕ closes to the tray (tunnels keep running); hold **Shift** while closing, or use **Exit**, to quit.

---

## 29. Kill switch

The kill switch blocks all outbound traffic except through the active WireGuard tunnel, so nothing leaks over your regular connection if the tunnel drops.

### How it works

MasselGUARD adds `MasselGUARD_KS_`-prefixed Windows Firewall rules:
- Sets the default outbound policy to **Block** on all profiles (Domain, Private, Public)
- Allows the WireGuard tunnel adapter and the tunnel's endpoint (plus any split-tunnel *Exclude* ranges)
- Removes the rules and restores **Allow** when the tunnel disconnects or the app exits

### Modes

Settings → WireGuard → **Kill switch**:

| Mode | Behaviour |
|---|---|
| **Per tunnel** (default) | Turn it on per tunnel with the 🔒 toggle in the tunnel editor |
| **Always** | Every tunnel uses it; the per-tunnel toggle shows *(controlled globally)* |

### Crash recovery

At start-up MasselGUARD removes any leftover `MasselGUARD_KS_*` rules and resets the outbound policy to Allow, recovering from a crash that prevented normal cleanup.

---

## 30. Auto-reconnect

When a tunnel drops unexpectedly, MasselGUARD reconnects it - up to 3 attempts with increasing backoff (5 s, 10 s, 15 s).

### What counts as unexpected

These are **never** retried:
- You clicking Disconnect
- An Automation rule or the default action disconnecting the tunnel
- CLI `disconnect` or `disconnect-all`
- A data cap with **Kill at cap** disconnecting it

These **are** retried: the WireGuard adapter or service failing, or the machine waking from sleep with the tunnel gone.

### Modes

Settings → WireGuard → **Auto-reconnect**:

| Mode | Behaviour |
|---|---|
| **Off** | No tunnel reconnects automatically; the per-tunnel toggle is hidden |
| **Per tunnel** | Turn it on per tunnel with the 🔄 toggle in the tunnel editor |
| **Always** (default) | Every tunnel reconnects; the toggle shows *(controlled globally)* |

### Activity log entries

```
[AutoReconnect] 'WorkVPN' dropped - reconnecting in 5s (attempt 1/3)…
[AutoReconnect] 'WorkVPN' reconnected successfully.
```

```
[AutoReconnect] 'WorkVPN' dropped - reconnecting in 5s (attempt 1/3)…
[AutoReconnect] 'WorkVPN' - attempt 1 failed.
[AutoReconnect] 'WorkVPN' dropped - reconnecting in 10s (attempt 2/3)…
[AutoReconnect] 'WorkVPN' - giving up after 3 attempts.
```

---

## 31. Themes

### Built-in theme

Only **System (Windows colors)** is built in - it uses the live Windows accent palette and is the default. Every other theme is downloaded or self-made. Each theme has a dark and a light variant; Settings → Appearance → System theme decides which one is shown.

### Theme files

All themes live in `%APPDATA%\MasselGUARD\themes\<theme-id>\theme.json`, per user and surviving app updates. A theme can set colours, fonts (including a separate header font and bundled font files), corner radius, images (logo, app icon, background, tray icons, per variant), the DNS tray badge, and its own **section icons** for the title-bar buttons, panel headers and Settings sidebar.

For the full `theme.json` reference, a template and the community catalogue, see the [MasselGUARD-themes](https://github.com/masselink/MasselGUARD-themes) repository.

### Theme Manager

**Settings → Appearance → Manage themes…** - every theme except System can be edited and deleted:

- **+ Add theme** - from the current theme, from a light/dark image pair, as a copy of another theme, from **Community themes**, or **Import…** a `.zip`.
- **Editor** - Identity, Colors (light and dark side by side, with copy arrows and a "copy inverted colour" mode), Typography (body font, optional header font, base size), Transparency, Window (title bar and status bar layout), Assets (per-variant images), **Section icons** (path data on a 24×24 grid, with a live preview) and the DNS badge.
- Edits **apply live** (the **● LIVE** indicator; click it to pause). Closing without saving reverts; **Undo/Redo** (Ctrl+Z / Ctrl+Y) step through the session's edits.
- Right-click a theme for **Apply / Duplicate / Export / Delete**.
- **Hold Shift** to fall back to plain Windows colours if a draft makes the Manager unreadable.

### Community themes

**Settings → Appearance → Download themes…** opens a searchable, tag-filterable gallery with dark/light previews (click one to zoom).

- **Install** downloads a theme; an installed theme shows **Reinstall**, or **Update** when the repository has a newer version (confirms first, since it overwrites local edits).
- Installed community themes are checked for updates with the app-update check; a badge in Settings → Appearance and a banner in Settings → About link to them.
- The repository is set in **Settings → Diagnostics → Theme repository**.

### Live preview

**▶ Dark** / **▶ Light** in Settings → Appearance show a variant for 10 seconds. Cancel Settings to go back to the last saved theme.

---

## 32. Font override

Enable **Override font** in Settings → Appearance → Font to replace the theme typeface with any installed font.

- The font family list shows each font in its own typeface; leave it blank for the Windows UI font
- The size slider sets the base size (8-18 pt)
- **▶ Preview** applies the font to the whole interface for 10 seconds
- Changes apply on Save; turn **Override font** off to return to the theme's font

---

## 33. Languages

English, Dutch, German, French, Spanish, Japanese, Italian, Portuguese (Brazil), Russian, Polish, Turkish and Chinese (Simplified). Change it in Settings → General; the picker shows a flag for each. Dates and day names in the charts, History and the rule dialog follow the interface language. The activity log, CLI and the System diagnostics "Copy all" report stay in English.

Add a language: copy `lang\en.json`, translate it, set the `_code`, `_language` and `_flag` keys, and add a matching 20×15 `<flag>.png` to `lang\flags\`.

---

## 34. Keyboard and window behaviour

| Action | Result |
|---|---|
| Double-click the title bar | Maximize / restore |
| Drag a maximized window by its title bar | Restores it under the cursor |
| **Esc** in Settings or a dialog | Closes it, the same as its ✕ / Cancel (unsaved-change prompts still apply) |
| ✕ on the main window | Close to tray |
| **Shift** + ✕ (or Alt+F4) | Exit completely |
| Hold **Shift** while starting | Emergency reset of language, theme and font (see FAQ) |

---

## 35. Frequently asked questions

**How do I exit completely instead of minimizing to the tray?**
Hold **Shift** while clicking ✕ (or pressing Alt+F4), or use Tray → Exit.

**The font, theme or language I chose makes the UI unreadable - how do I recover?**
Hold **Shift** while launching MasselGUARD. Before any window opens it resets the font override, switches to the System theme (Follow Windows) and sets the language to English, then tells you what was reset.

**Can I run without a UAC prompt?**
Yes. The best way is the [background service](#37-background-service) (Settings → Startup → Background service → Install service): MasselGUARD then starts unelevated, with no UAC prompt at all. Without the service, install MasselGUARD and enable **Start with Windows**: launches then go through an elevated Scheduled Task.

**Can I reorder rules, tunnels or DNS profiles?**
Yes - drag the rows. For rules the order is the evaluation order.

**Can I move a tunnel to another group?**
Drag it onto the group's tab.

**How do I hide a section, or turn a feature off?**
Use its title-bar button, or Settings → General → Features. Each button can hide only or also disable the feature.

**What does the Hits column show?**
How many times a rule has fired, kept across restarts. Change it with **(Re)set counter** in the rule dialog.

**How does auto-reconnect work?**
After an unexpected drop it retries after 5 s, 10 s and 15 s, then gives up. Every step is logged. Intentional disconnects are never retried.

**The Auto-reconnect or Kill switch toggle is greyed out in the tunnel editor.**
It shows *(controlled globally)* when that mode is **Always** in Settings → WireGuard. The auto-reconnect toggle is hidden when its mode is **Off**.

**My internet stopped working after MasselGUARD crashed.**
The kill switch firewall rules weren't cleaned up. Start MasselGUARD again - it removes them at start-up. Or open Windows Defender Firewall, delete the rules starting with `MasselGUARD_KS_`, and set the default outbound action back to Allow.

**How do the usage bars / rings work, and can I cap or cut off data usage?**
See [§10](#10-data-usage). In short: each period compares against the tunnel's typical usage (**Use history**, informational) or a cap you set; **Kill at cap** disconnects at the cap.

**Where do I see bandwidth per session?**
At the **Enhanced** log level each disconnect is followed by a line with the session's duration and traffic. The Data usage chart shows it over time.

**A tunnel won't start - "Tunnel did not come up… Element not found".**
Most often MasselGUARD is running from a **OneDrive (cloud-synced) folder**: the tunnel service runs as LocalSystem, which can't read files there. Move MasselGUARD to a normal local folder (e.g. `C:\MasselGUARD` or Program Files). The log says so when it detects this. Other causes: the WireGuard driver blocked by antivirus or Secure Boot - check **Event Viewer → System**.

**DNS isn't changing on my network.**
Check that the DNS feature and **Enable DNS automation** (Settings → DNS) are both on, and that no tunnel is connected (a tunnel's own DNS takes over unless you apply a profile manually). Encrypted DoH needs Windows 11. `MasselGUARDcli dns status` shows what is applied.

**The What's New panel shows "Could not load release notes".**
It's fetched live from GitHub; check your connection. The links in the panel go to the repository and website.

**How do I report a bug?**
Settings → About → **Report an issue** opens a GitHub issue with your version details filled in; **Copy version info** copies them for anywhere else. The System diagnostics report (Settings → Diagnostics) is useful for network problems.

---

## 36. Command-line interface (CLI)

`MasselGUARDcli.exe` (next to `MasselGUARD.exe`) scripts the same tunnels as the GUI; changes show up in the GUI within about a second. Full reference: [`CLIManual.md`](CLIManual.md).

### Requirements

Run as Administrator. From a non-elevated terminal the CLI tells you it needs elevation (use an administrator terminal, or `sudo`). `help`, `version`, `dns status`, `dns bypass` and `network status` work without elevation.

### Commands

| Command | Description |
|---|---|
| `list` | List all tunnels and their status (`--group`, `--active`) |
| `status` | Active tunnel count and names |
| `connect <name>` / `--default` / `--all` | Connect a tunnel, the default tunnel, or all (`--group`) |
| `disconnect <name>` / `disconnect-all` | Disconnect one or all (`--group`) |
| `info <name>` | Details for one tunnel (type, group, uptime, source, split) |
| `dns status` | DNS-automation configuration and live resolvers (read-only) |
| `log [n]` | Recent connections (default 20) |
| `tunnel-history [n]` | Connection history with source and traffic |
| `wifi-history [n]` | Wi-Fi history with duration and security |
| `import <file>` | Import a `.conf`, `.mgconf` (`--password`) or `.conf.dpapi` |
| `delete <name>` | Remove a tunnel (`--force` disconnects first) |
| `rawconnect` | Connect a tunnel built from inline parameters |
| `check-update` | Check GitHub for a newer version |
| `version` / `help` | Version and build / command reference |

Global flags: `--json`, `--quiet` / `-q`, `--group <name>`, `--active`, `--logtype normal|extended`.

Exit codes: `0` success · `1` error · `2` already in the desired state.

### Scripting example

```powershell
# Connect silently, act on exit code
MasselGUARDcli connect "Work VPN" --quiet
switch ($LASTEXITCODE) {
    0 { Write-Host "Connected." }
    2 { Write-Host "Already connected." }
    1 { Write-Host "Failed." }
}
```

---

## 37. Background service

MasselGUARD can do its privileged work (connecting tunnels, changing DNS, the kill switch) in a **Windows service** named *MasselGUARD Service*, instead of inside the app. The service is optional: without it MasselGUARD works as before and elevates itself.

**What you get**
- MasselGUARD **starts without a UAC prompt** (also at Windows start with *Start with Windows*).
- Your **network rules can keep running when MasselGUARD is closed** (Settings → Automation → *Keep automation running when MasselGUARD is closed*, off by default).
- A **timed DNS override** ("use Google for 5 minutes") keeps running if you close the window, and ends on time.
- The **Explorer right-click DNS bypass** and `MasselGUARDcli dns bypass` work when MasselGUARD is not running.
- Tunnel configs are kept by the service in a machine-encrypted store that only administrators and the system can read; the app sends a config only when it changed.

**Install**
1. Install MasselGUARD to **Program Files** first (Settings → Startup → Installation → Install). The service refuses to run from a folder that standard users can write to (for example a folder you created in Explorer) and cannot run from OneDrive or a network path.
2. Settings → Startup → **Background service → Install service**. Windows asks for administrator approval once. On a portable copy the button installs MasselGUARD first and sets the service up as part of that. The installer and the setup wizard offer the same choice, and `MasselGUARDcli service install` does it from a terminal.
3. Restart MasselGUARD when asked. The activity log (Debug level) says *Back-end: MasselGUARD service*.

**Remove** with the same button (*Remove service*), or `MasselGUARDcli service uninstall`. Uninstalling MasselGUARD removes the service too. MasselGUARD then runs in direct mode again after a restart.

**What changes when it runs**
- Actions that really need administrator rights while the app is unelevated (installing, updating an installed copy, orphaned-service removal, the DNS-leak policy) offer to restart MasselGUARD as administrator; repeat the action there.
- *Start with Windows* becomes a per-user startup entry (no approval needed); MasselGUARD waits a few seconds for the service at logon.
- With *Keep automation running* on, the service takes over a few seconds after the last window closes (or at once when you exit) and hands back when a window opens. It applies tunnel and DNS rules only; it records no history, shows no notifications, applies no data caps or schedule rules and does not run your pre/post connect scripts.

**Who may use it** - Administrators, and the users an administrator lists: the user who installed it, plus `MasselGUARDcli service allow --user DOMAIN\name` (`deny` removes, `users` lists). Stored tunnels and the keep-running automation belong to the user who created them; another user cannot overwrite them.

**Updates** - the in-app updater checks the download against the release's `.sha256` file and refuses an update without it; it also stops and restarts the service around the file copy.

**Troubleshooting**
- *Service will not start / install refused with a folder message*: reinstall MasselGUARD to Program Files and register the service from there.
- *Is it running?* Settings → Startup shows the state; `MasselGUARDcli service status`; the service log is `%ProgramData%\MasselGUARD\logs\service.log`.
- *The app says the service does not answer*: start it (`sc start MasselGUARDsvc` as administrator) or restart MasselGUARD to fall back to direct mode.

---

## License

MasselGUARD is free, open-source software released under the **MIT License** - you may use, copy, modify, and redistribute it. The full text is in the `LICENSE` file in the project root.

MasselGUARD bundles the WireGuard native libraries, which remain under their own licenses; see `THIRD-PARTY-NOTICES.md`. **WireGuard** is a registered trademark of Jason A. Donenfeld - MasselGUARD is an independent project, not affiliated with or endorsed by WireGuard LLC.
