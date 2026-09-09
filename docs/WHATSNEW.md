## v4.0.0 — Forking Fox

The headline is **split tunneling** — decide, per tunnel, which traffic goes *through* the tunnel and which bypasses it.

### Split tunneling (route / IP-based)

Every local tunnel's editor now has a **Split** tab with three modes:

- **Off** — route everything through the tunnel (the classic full tunnel; unchanged default).
- **Exclude these ranges** — a full tunnel *except* the destination IP ranges you list (e.g. keep your printer, LAN or a streaming service on the normal connection).
- **Only these ranges** — the opposite: only the listed ranges go through the tunnel, everything else uses your normal connection (a classic split VPN to specific subnets/services).

List the ranges one per line in **CIDR notation** (`192.168.1.0/24`) or as a single address (`10.0.0.5`); **IPv4 and IPv6** are both supported. Under the hood MasselGUARD computes the tunnel's effective routes for you (the standard WireGuard *AllowedIPs* math) — no manual route tables.

It plays correctly with the **kill switch**: in *Exclude* mode the excluded ranges are still allowed out over your normal connection instead of being blocked. Your split settings also **travel with an exported tunnel** (`.conf` / `.mgconf`) and show up in `MasselGUARDcli info`.

> **Coming later:** *per-app* split tunneling (choose by application rather than IP range) — planned for a later 4.x update.

### Also in this release

- **Cap-killed marker is now red.** When *Kill at cap* disconnects a tunnel, the 🛑 marker by its Connect button is properly coloured.
- **Usage indicator: bars or rings.** Each tunnel's data-cap usage now shows on its row as slim horizontal **bars** (day / week / month, colour-coded, with the exact breakdown on hover) — the new default. Prefer the original compact **rings**? Switch it in Settings → Tunnels → Display.
- **Hide a usage ring per period.** Each period (daily / weekly / monthly) in the tunnel editor's DATA-USAGE section now has its own **Hide ring** checkbox, so you can show just the monthly ring, for example. The current-usage figures are also column-aligned now.
- **`MasselGUARDcli help` and `version` no longer need Administrator.** Informational commands run in any terminal; only commands that actually touch the tunnel driver require elevation.
- **Fixed: connecting a local tunnel from the CLI.** `MasselGUARDcli connect <name>` failed for local tunnels because the tunnel service was launched with the wrong host executable and exited immediately; it now starts correctly. Thanks to **Sven Grewe ([@qoreQyaS](https://github.com/qoreQyaS))** for the fix ([#47](https://github.com/masselink/MasselGUARD/pull/47)).

---

## v3.9.5 — Selective Serval

This cycle is about **rules, portability, and languages**.

### Trusted-network rules now work in both directions

A *Trusted networks* rule used to do one fixed thing: connect on untrusted networks, disconnect on trusted ones. Now each trusted rule picks a **direction**:

- **When NOT on a trusted network** → activate its tunnel (protect on public WiFi — typically a **full tunnel**).
- **When on a trusted network** → activate its tunnel (bring something up only on known networks — e.g. a **split tunnel** at home or work).

A rule now acts **only on its own side**; the other side falls through to your other rules and the **Default action** (so the Default always still applies where a rule doesn't). Want both behaviours? Add two trusted rules against the one shared trusted-SSID list — one for on-list, one for off-list. Leaving a rule's tunnel blank disconnects on its side instead of connecting.

---

### Data-usage warnings (daily / weekly / monthly)

The per-tunnel data cap is now a proper **DATA-USAGE WARNINGS** section (in the tunnel editor's *Options*) with **daily, weekly, and monthly** thresholds — set any of them in MB, `0` = off.

When a tunnel's usage for a period crosses its threshold you get a **one-time notification**: a log entry, a tray toast, and the tunnel's row is **highlighted** (a subtle amber tint, with the usage figure turning amber and a tooltip breaking down today / this week / this month). Each warning re-arms at the next period boundary.

By default these are **warnings only** — nothing is disconnected. If you want a hard stop, tick **Kill at cap** (see *Enforce a cap* below) and MasselGUARD disconnects the tunnel when the period's limit is reached. Usage is measured from the connection history you already record (calendar day / week / month, UTC).

**Enforce a cap (disconnect at the limit).** Each period now has a **Kill at cap** checkbox next to its threshold. With it on, the tunnel is **disconnected** the moment its usage crosses that cap — with a log line, a **sticky toast** offering **Ignore & reconnect**, and a **🛑 marker** next to its Connect button (until you next start it) — and it won't auto-reconnect while over budget. The tunnel editor's usage section also shows each period's **current usage** (e.g. `· 320 MB used`) next to its threshold. Trying to (re)connect over the limit asks first:
- **Manually** (window open) → a *"Connect anyway and ignore the limit?"* confirmation.
- **Automatically** (a WiFi rule) → an interactive toast with the usage details and **Connect / Cancel** buttons; if you don't answer it defaults to **Cancel** (respecting the limit).

Choosing *Connect anyway* ignores that cap until the period rolls over, so you're not nagged every second. Enforcement is per-period and opt-in — leave it off for warning-only behaviour.

**See it on a chart.** The bottom info panel now has a **Timeline ⇄ Data usage** switch. In *Data usage*, it draws a **line chart (with dots) of data per tunnel** over the selected range — the same **24h / 7d / 31d** toggle you already use, with hourly points for 24h and daily points for 7d / 31d. The window grows a little in this view so the tunnel list keeps its size. The range doubles as the cap period, so where a tunnel's usage reaches its **daily / weekly / monthly** cap a **red limit-marker ring** appears on that point of its line — and you can read the rest straight off the chart: the line **drops to zero** if the cap disconnected it, or **carries on** if the limit was overruled. Hovering a point breaks down that hour/day per tunnel. The legend is just the tunnel name and its colour; click an entry to show/hide that tunnel.

---

### Export a tunnel

Select a tunnel and use the new **Export** button in the toolbar (next to *Defaults*) to hand the config to another device or keep a backup. Three formats:

- **Plain file (`.conf`)** — a standard WireGuard config. Import it into any WireGuard client, phone or router.
- **Encrypted file (`.mgconf`)** — password-protected with **AES-256-GCM** (key derived from your passphrase). Unlike the at-rest storage format, it's **portable**: open it on any machine with the password. There's no recovery if the password is lost.
- **QR code** — scan straight into the WireGuard mobile app (this is the existing QR export, now reachable from the toolbar too).

**Include MasselGUARD settings** (file formats only) bundles the tunnel's extras — group, scripts, kill switch, auto-reconnect, data cap and notes — alongside the config. They ride along as **readable `# MasselGUARD-…` comment lines** that other WireGuard clients ignore, so the `.conf` stays universally importable, while MasselGUARD restores them on import. (Multi-line values like embedded scripts are base64-wrapped so the file stays valid.) QR codes are standard-only and can't carry the extras.

The tunnel editor's **Raw config** tab now shows those same `# MasselGUARD-…` lines below the config, and you can edit them there — Raw is a full editable view of both the WireGuard config and the MasselGUARD settings.

Importing understands all of it: `.mgconf` files prompt for the password, and any bundled settings are restored automatically. The CLI keeps pace — `MasselGUARDcli import file.mgconf --password <pw>`.

> The exported config contains the tunnel's **private key**. Keep plain and QR exports private; use the encrypted format to share safely. A managed policy that locks *Tunnels* now also disables Export.

---

### Twelve interface languages

MasselGUARD now ships in **twelve languages**. Six are new this release — **Italian, Portuguese (Brazil), Russian, Polish, Turkish, and Chinese (Simplified)** — joining English, Dutch, German, French, Spanish, and Japanese. Each gets its own flag in the picker (Settings → General → Interface language, and the setup wizard).

Alongside the new languages, this release clears a large **translation backlog**: hundreds of strings that had quietly stayed English in the European languages are now translated, and UI that was previously hardcoded is fully localized too — the tunnel **Connect / Disconnect** status and buttons, the entire **Theme Manager** (the theme editor and the community theme browser, plus the *Manage themes* / *Download themes* buttons), the *View preset* section, and the DNS/health tooltips. Switching language updates the whole window live.

**Escape hatch:** hold **Shift** while starting MasselGUARD to reset the interface language to the default (English) — handy if you land in a language you can't read. (The same Shift-at-startup reset already covers a bad font or theme, and there's a reminder of it under the language selector.)

---

### Polish

- **A crisper settings icon.** The main-window gear is now a sharp vector icon instead of a faint text glyph — it stays crisp at any scale and picks up the accent colour on hover like the rest of the toolbar chrome.
- **Usage rings on the tunnel row.** The status line dropped the live ↑↓ traffic figure (it added noise). In its place, a connected tunnel shows a compact set of **concentric usage rings** — **day** (innermost), **week** (middle), **month** (outermost). A ring appears automatically for each cap you've set (the old *Show in row* toggle is gone), fills **0 → 360°** as usage approaches the cap, turns **amber** near the limit and **red** once over it. Hover for the exact per-period breakdown, shown immediately.
- **Smoother tunnel import.** The import file picker now defaults to an **All supported configs** filter, so `.conf`, `.mgconf`, and `.conf.dpapi` files all show at once — no more switching the filter to see encrypted files. And if the imported name already exists, you're asked to **Overwrite**, **Save as new name**, or **Cancel** instead of silently creating a second tunnel with the same name.
- **No more duplicate WiFi rules list.** The rules list was showing in *two* places — the main window and Settings → WiFi. Settings now keeps only the automation settings (default action, open-network protection, trusted networks, manual mode, and the main-window rules panel/column toggles); manage the rules themselves on the main window.
- **Tidier tunnel editor.** The behaviour switches (Default action, Open network protection, Kill switch, Auto-reconnect) and the data-usage warnings moved out of the cramped footer into a dedicated **Options** area — a new tab for local tunnels, an *Options* section for companion tunnels. Scripts are no longer a separate tab either: they're now a **Scripts** section at the bottom of **Fields**, below Peer. Both footers are now just *Cancel / Save*.
- **Fully themed dialogs.** Checkboxes now match the theme (accent checkmark) instead of default Windows chrome — so the whole *Export tunnel* pop-over is themed, and its *Include MasselGUARD settings* box correctly greys out for QR exports (which can't carry settings).
- **Nicer on/off switches.** The toggle switches (tunnel settings, Settings, wizard) got a refresh: a crisp white thumb with a soft shadow that stays clear in the off state, a smooth slide, and a properly dimmed look when a switch is read-only (e.g. *Auto-reconnect — controlled globally*).
- **Themed controls everywhere.** The day-of-week toggles in the Schedule rule editor and the *Scan / Cancel* buttons on the QR-capture overlay now follow the active theme (selected days fill with the accent colour) instead of showing default Windows chrome.

---

## v3.9.0 — Adaptive Armadillo

The headline is **native ARM64 support**. MasselGUARD now ships as two native builds — **x64** and **ARM64** — so it runs at full speed on Windows-on-ARM devices (Snapdragon-based Copilot+ PCs, recent Surface models) instead of under x64 emulation. Alongside it: a fix for auto-generated rule names, and a fresh batch of community themes.

---

### Native ARM64 build

Windows on ARM can *emulate* x64 apps, but a VPN can't lean on that: the **wireguard-NT kernel driver is native and cannot be emulated**, so an emulated x64 build can't bring up local (standalone) tunnels on an ARM machine. The fix is a genuine ARM64 build.

- **Two downloads now** — `MasselGUARD-x64.zip` and `MasselGUARD-arm64.zip`. Grab the one that matches your PC: on a normal Intel/AMD machine that's **x64**; on a Snapdragon / Copilot+ / Windows-on-ARM device it's **arm64**.
- **Local tunnels run natively on ARM64** — the ARM64 build carries ARM64 `tunnel.dll` + `wireguard.dll` and drives the native kernel driver directly. (Companion tunnels — automating the WireGuard for Windows app — already worked under emulation and still do.)
- **Auto-update picks the right one** — the updater downloads the build matching your processor automatically; there's nothing to re-select at update time. Older single-arch releases still resolve to the x64 build.
- **One-click switch if you're on the wrong build** — run the x64 build on an ARM64 PC and MasselGUARD offers, right at startup, to **download the native ARM64 build from GitHub and switch to it automatically** (or "Later", or "Don't remind me"). No cryptic driver failure, no manual download. A wrong-architecture DLL is likewise caught up front rather than crashing the connect.
- **Your architecture is shown** in Settings → About and in the CLI `version` output (`arch: arm64`).

> **Which do I need?** If you're not sure, you're almost certainly on **x64**. ARM64 is only for Windows-on-ARM devices.

---

### Fixes

- **Auto-generated rule names** — creating a WiFi rule by typing the SSID *first* and then choosing a tunnel produced the name "SSID → disconnect" even though a tunnel was selected (an editable-dropdown timing quirk that read the tunnel box a beat too early). The name now reflects the tunnel you actually picked.

---

### Themes

Eight new themes are available in the **Community theme browser** (Settings → Appearance → **Download themes…**) — the first batch to ship **background images** and theme-matched fonts:

- **Aurora Borealis**, **Nebula**, **Ocean Depth**, **Alpine Fog** — atmospheric, calm backdrops
- **Synthwave Sunset** (retro Orbitron display font), **Circuit** (JetBrains Mono), **Carbon Fiber**, **Topographic** — textured, technical looks

Each ships tuned dark *and* light variants, with panel opacity set so text stays readable over the artwork.

---

## v3.8.0 — Protective Pangolin  ·  *(unreleased)*

> *Never shipped as a standalone release — these changes are folded into 3.9.0.*

This release is about **automation** and **managed deployment**. WiFi rules gain new trigger types and an on/off switch, tunnels show live health and data usage, a tunnel config can be handed to a phone as a QR code, and — the headline — a `.masselguard` **policy file** lets you ship a build with locked settings for a company, family, or kiosk.

---

### Automation — new rule types, and an on/off switch

- **Trusted-network auto-protect** — a new rule type that connects a chosen tunnel on *any* WiFi network that isn't in your **trusted list**, and disconnects on a trusted one. Manage the trusted SSIDs in **Settings → WiFi** (one per line, or **"Add current WiFi network"**). It's a smarter default action: "VPN everywhere except my home/office".
- **Scheduled rules** — a rule can now fire on a **day + time window** (e.g. Work 09:00–18:00, Mon–Fri) instead of a network. Overnight windows (22:00–06:00) are supported. Checked on a one-minute timer.
- **Enable / disable a rule** — select a rule and click **Disable** (or **Enable**) below the list. A disabled rule stays in place but is skipped — its row greys out with a `⊘` marker. The tidy way to switch a rule off without deleting it.
- The rules list now shows a **kind icon** in front of each rule — 📶 for a WiFi network, ⏰ for a schedule, 🛡 for trusted-network protection.
- **Fixed** — editing or deleting a trusted/schedule rule from the main-window WiFi panel didn't work (it matched rules by SSID, which those kinds don't have). The panel now operates on the exact rule, and can create every rule type.

---

### Managed preset — ship a locked configuration

A **`.masselguard`** file is a full snapshot of the app's settings that plays two roles depending on where it is:

- **Imported** (Setup wizard, or Advanced → Import) → all settings apply and stay **editable**.
- **Placed next to `MasselGUARD.exe`** → only the settings you marked as **Locked** are **forced and locked** — greyed out with a 🔒 and a *"managed by &lt;policy&gt;"* banner at the top of Settings. The forced values are re-applied on every save, so a hand-edited `config.json` can't override them. `MasselGUARDcli.exe` obeys the same file.

- **Create one** via **Advanced → Import / Export → "Export settings as preset…"**: enter a policy name and tick which settings to lock — grouped by section, with a section header that selects all its items, so you can lock a whole section *or* single settings (e.g. lock the tray notification but not its duration). Tunnel definitions are never included.
- If a locked policy sets a **theme that isn't installed**, the app downloads it from the shared-themes repo on first launch (falling back to system colours if that fails).
- The **managed install** offers to copy the `.masselguard` into the install folder so the installed copy stays locked.

> Soft lock — meant for managed distributions where users don't tamper with the build, not a security boundary against a hostile local user.

---

### Live tunnel health

Each active tunnel now shows a small **health dot** next to its status — green ● when the adapter is up and passing traffic, amber ● when up but idle, red ▲ if the adapter is down while the tunnel is marked active.

### Data usage & monthly caps

- Each tunnel shows this **month's data usage** (aggregated from the connection history you already record).
- Set an optional **monthly data cap (MB)** per tunnel in the tunnel dialog; crossing it raises a one-time warning (log + toast) that re-arms next month.

### QR export

Right-click a local tunnel → **"Show QR code"** to display a scannable QR of its configuration — scan it with the WireGuard mobile app to move the tunnel to a phone. Includes a Save-PNG option and a private-key warning.

---

### Fixes & smaller changes

- **Fixed** — the inline DNS-leak icon stayed visible even when DNS-leak *prevention* was enabled (which contains the leak). It's now hidden in that state, matching the toast/log warnings, which already stayed silent.
- **Config validation** — the "Skip config validation" toggle is back on **Settings → Tunnels**, off by default (validation active). Picking any View preset re-asserts validation on. The per-tunnel skip was removed — the bypass now lives in exactly one place.
- All new interface text is translated across **English, Dutch, German, French, Spanish, and Japanese**.

---

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
- The Theme Manager footer gained a **Close** button next to Save, matching the title-bar ✕ (prompts to save if there are unsaved changes).

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
