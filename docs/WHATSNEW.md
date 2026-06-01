━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  v3.2.5
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Update available badge
  • A ↑ button in Accent colour appears in the title bar (before the
    settings gear) whenever a newer version is available.
  • Clicking it opens Settings → About directly so you can install
    in one step.
  • The badge appears immediately on startup if a newer version was
    already known from a previous check, and updates after every
    background or manual check.
  • Disappears once the running build is up to date.

Bug fixes
  • Update check frequency (On start / Daily / Weekly / Manual) is now
    correctly saved when pressing Save in Settings → About.
    Previously the selection was staged in the draft but never committed
    to config — it reverted to the previous value after closing Settings.


━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  v3.2.0
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Kill switch
  • Per-tunnel kill switch: blocks all non-tunnel outbound traffic via
    Windows Firewall when a tunnel is active — no traffic leaks if the
    tunnel drops.
  • Global kill switch mode "Always": kill switch is forced on for every
    tunnel; the per-tunnel toggle is hidden in this mode.
  • Firewall rules use the MasselGUARD_KS_ prefix and are removed cleanly
    on exit. Stale rules from a previous crash are cleaned up at startup.
  • Toggle in the tunnel edit dialog; global mode in Settings → Advanced.

Activity log (Extended mode)
  • A grey continuation line appears under each disconnect entry showing
    the session duration and bandwidth (↑ sent / ↓ received).
    Only shown when log level is Extended.

Settings — history table
  • Completely rewritten: removed WPF GridView, replaced with a custom
    header bar and DataTemplate rows.
  • Hover and selection now use the theme ListHover / ListSelected colours
    — no Aero highlight, no 3-D border effect.
  • Hover tooltip shows full connected-at timestamp, duration, and trigger.

Settings — WiFi rules table
  • Rules table in Settings now matches the main window: five columns
    (Name | SSID | Action | Hits | Tunnel), same header bar and row style.

Settings — Import / Export
  • Export and import confirmation dialogs are now fully themed.
  • After a successful import a themed prompt offers to restart immediately.
    Declining shows a warning that some displayed values may not yet reflect
    the imported settings.
  • Version mismatch warnings (file older or newer than current build) now
    show proper translated text in all five languages.

About tab — What's New panel
  • Release notes fetched live from the GitHub repository and
    displayed inline in Settings → About — no browser needed.
  • Offline fallback shows a styled panel with clickable links
    to GitHub and masselink.net.
  • About tab reorganised into named sections: UPDATES,
    MASSELGUARD, WHAT'S NEW.

WiFi rule edit dialog
  • The Edit Rule dialog now shows the rule's hit counter.
  • (Re)set counter button opens a small input dialog — type a
    number to set, type 0 to clear. Cancel makes no change.
  • Counter changes are recorded in the activity log:
      Counter: 42 → 10

Window close
  • X button (or Alt+F4) hides the window to the tray as before.
  • Shift+X performs a clean exit — same as Tray → Exit.


━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  v3.1.0
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Auto-update
  • One-click update: downloads MasselGUARD.zip from GitHub,
    extracts, overwrites installed files, and relaunches — no
    manual steps required.
  • Progress shown inline: Downloading… / Extracting… / Applying…
  • Shift+Check Now force-installs the latest release (for testing).
  • Version comparison now ignores the build timestamp component,
    so a local dev build is never mistaken for an older version.
  • Update status badges use icons to distinguish states:
      ↑  update available
      🚀  running ahead of latest release
      ✓  up to date
      —  never checked

WiFi rule edit dialog
  • The Edit Rule dialog now shows the rule's hit counter.
  • (Re)set counter button opens a small input dialog — type a
    number to set, type 0 to clear. Cancel makes no change.
  • Counter changes are recorded in the activity log:
      Counter: 42 → 10

Window close
  • X button (or Alt+F4) hides the window to the tray as before.
  • Shift+X performs a clean exit — same as Tray → Exit.

About tab — What's New panel
  • Release notes are fetched live from the GitHub repository and
    displayed inline in Settings → About — no browser needed.
  • If offline, a styled panel with clickable links to GitHub and
    masselink.net is shown instead.

Themed dialogs
  • All update-related prompts now use the app theme instead of
    the system MessageBox.


━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  v3.0.1
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Bug fixes
  • Fixed two error dialogs appearing after applying an update
    (harmless WPF shutdown artifact — now silently suppressed).
  • Theme preview cancel now correctly reverts to Windows system
    colours when no custom theme was active.

Appearance
  • System theme label in Settings renamed to "System theme" with
    a clearer description.
  • Preview / revert buttons have a fixed width so adjacent
    controls don't shift when the label changes.
  • Activity log collapse button (») enlarged for easier clicking.

Startup
  • Holding Shift at startup resets both the font override and the
    custom theme back to Windows system colours if either was
    causing a problem.


━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  v3.0.0
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Custom appearance system
  • Toggle between Windows 11 system colours and custom theme
    files — independently for dark and light mode.
  • System mode pill: Auto (follows Windows) / Light / Dark.
  • Theme preview: applies the selected theme for 10 seconds then
    auto-reverts. No accidental permanent changes.

Font override
  • Pick any installed font from a per-typeface preview dropdown.
  • Font size slider (8–18 pt).
  • Font preview button — same 10-second preview as themes.

Activity log toggle
  • ☰ button in the tunnel header opens the log panel.
  • » button in the log header collapses it.
  • Setting persisted across sessions.

Confirm on close
  • Optional confirmation dialog before disconnecting active
    tunnels on exit (Settings → Advanced).

Update check frequency
  • On start / Daily / Weekly / Manual pill selector in Settings.

Shift+startup emergency reset
  • Holding Shift at launch resets font and/or theme overrides
    if either is causing a display problem.


━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  v2.9.0
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Architecture
  • Full MVVM rewrite — Models / Services / ViewModels / Views.
    UI is now clean glue over proper service and data layers.

Tunnel list improvements
  • Drag-to-reorder tunnels.
  • Uptime counter in status column.
  • ⚡ default action and 🔓 open network protection badges
    shown inline after the tunnel name.
  • Rules column — click to highlight matching WiFi rules.

WiFi Rules panel
  • Added to the main window (left panel, optional).
  • Columns: Name | SSID | Action | Hits | Tunnel.
  • Hits counter persisted in config, accent colour when > 0.
  • Rule Name field with auto-generation from SSID + tunnel.
  • Drag-to-reorder rules.

Defaults button
  • Single toolbar button opens a popup to set both the default
    action tunnel and open network protection tunnel.

Tray menu
  • GDI+ icons per item. Shield updates green/muted on tunnel
    state change.

Custom WPF toast notifications
  • Fully themed — slides in from bottom-right, auto-closes.
  • Category label (WiFi Rule / Open Network / Default Action).
  • Configurable duration (3 / 5 / 10 / 15 / 30 s).

Double-fire prevention
  • WiFi rules fire exactly once per network switch.


━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
  v2.5.0
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Settings redesign
  • Expanded from 3 tabs to 6 dedicated tabs: General /
    Appearance / Default Action / WiFi Rules / Advanced / About.
  • Fully deferred save — all changes stage until you press Save.
  • Cancel reverts everything including live previews.

Pre/post scripts
  • Every tunnel can run a .bat or .ps1 script at four points:
    before connect, after connect, before disconnect, after
    disconnect. Supports inline embedding or a file path.

Two new built-in themes
  • High Contrast Dark and High Contrast Light — suited for
    low-vision users and high-brightness environments.
  • Total built-in themes: six.

Tray icon badge
  • Green counter badge shows the number of active tunnels.

Import / Export settings
  • Export to .masselguard file; import with version mismatch
    warning. Unknown fields are safely ignored.

Setup wizard updated
  • Six steps, including a language picker and automation
    overview on first run.

Log levels simplified
  • Normal (OK + Warn) and Extended (everything). No more log
    file written to disk.
