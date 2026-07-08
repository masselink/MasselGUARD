# MasselGUARD — Session Handover

**Project:** MasselGUARD — WireGuard tunnel manager for Windows
**Current version:** 3.7.0 — Chromatic Chameleon (dev cycle, theming-focused release)
**Last updated:** 2026-06-15
**Branch:** `dev` — 3.6.0 work committed (6 commits) and pushed; version bump commit `7d6d40a` may still need pushing. Lots of Theme Builder work + the SDK-10.0.301 build fix are implemented but **uncommitted**. **All recent changes are build-verified (0/0) but NOT GUI-tested — user runs BUILD.bat and manual-tests.**

---

## ✅ RESOLVED (2026-06-23) — theme-management UX → Option 1 (Theme Manager hub)

User chose **Option 1**. Implemented (build 0/0, not GUI-tested):
- **Theme Builder → "Theme Manager"** (window Title + title-bar text).
- Settings → Appearance theme section: the dropdown now sits next to a single **"Manage
  themes…"** button (`ManageThemesBtn` → `OpenThemeBuilder_Click`). Removed the separate
  "Browse themes…" button (+ its `BrowseThemes_Click` handler) and the whole **Theme Builder
  launcher card**. The dropdown still switches/activates the live theme.
- Manager left panel: added a **Download…** button (`DownloadThemes_Click` → opens
  `ThemeBrowserWindow` with the configured repo URL or the default), beside **Import…**;
  **+ New theme** kept full-width above them.
- Manager footer: added an **Apply** button (`ApplyBtn`/`Apply_Click`) that makes the selected
  theme the active app theme — resolves unsaved edits first (save/discard), sets
  `config.ActiveTheme`, `ApplyThemeFromConfig()`, refreshes the ● marker. Enabled for any loaded
  theme incl. System.
- Net Settings change: **3 theme buttons → 1** ("Manage themes…"); delete/edit/duplicate/
  download/apply all live in the manager. (Optional one-click Delete on the dropdown row was
  offered but NOT added — manager covers delete.)

---

## ⏭ NEXT SESSION — pending Theme Builder work (agreed 2026-06-15)

**✅ IMPLEMENTED 2026-06-22 (code only — build-verified 0/0, NOT yet GUI-tested).** See the
2026-06-22 progress-log row for the as-built notes and the one design reconciliation.
The original agreed design is preserved below for reference.

User approved the design below; implement it next. All in `Views/ThemeBuilderWindow.xaml.cs`
unless noted. (This session ended here due to context limit — investigation only.)

### A. Theme me the two remaining native pop-ups
`NewThemeDialog` (~line 1753) AND `InputDialog` (~line 1656, used by **Duplicate** + rename)
still use native light styling (`InputDialog.UseSystemStyles` + `SystemColors.WindowBrush`
+ `WindowStyle.ToolWindow`) — they clash with the dark builder. Convert BOTH to the System
palette + custom chrome, **exactly like `ColorPickerDialog`/`ThemedMessageDialog`**:
`ThemeManager.ApplySystemTo(Resources)`, `WindowStyle=None`, `AllowsTransparency`, outer
`Border` (`WindowBg` bg + `Accent` border + `Theme.CornerRadius`), a `Surface` title bar
(title in `Accent` + ✕ + `DragMove`), controls coloured from `TextPrimary`/`TextMuted`/
`CardBg`/`BorderColor`, buttons via `SuccessBtn`/`FlatBtn`. Drop `DialogResult` (use a result
property like the others — it throws on AllowsTransparency windows). **Suggest** extracting the
shared chrome into one helper/base to avoid a 3rd copy. NewThemeDialog has radios/checkbox/
textboxes → colour each explicitly (ApplySystemTo only sets brushes, not implicit styles).

### B. Copy / copy-inverted system  — chosen: **global Invert mode toggle**
Keep the 2 per-row arrows (`MakeArrow`/`Arrow_Click`, ~546): `←` Dark→Light, `→` Light→Dark.
Add a **global "Invert" toggle** next to the Dark/Light pill. When ON, ALL copy actions
(per-row arrows AND the bulk buttons) do a **copy-inverted** instead of raw copy; when OFF, raw.
Because the active mode is hidden state, **change the arrow glyphs/colour when Invert is ON**
(e.g. swap `←`/`→` for a marked/double variant, or tint accent) so it's obvious.
Per-colour invert: reuse `ThemeManager.AutoInvertVariant` on a one-key throwaway
`ThemeDefinition` (set just that key, invert, read it back) — preserves its background-vs-
foreground-aware clamping so a single inverted copy matches the bulk result.

### C. Bulk buttons + "empty slots" toggle
Keep both bulk buttons (`CopyVariant_Click` ~400 / `GenVariant_Click` ~415) but relabel to
**"Copy other → this"** and (driven by the Invert toggle, or keep) **"Copy inverted → this"**.
ADD a toggle **"Overwrite only empty slots"**: ON = copy/invert only populates slots that are
currently empty (skip ones with a value); OFF = overwrite all. Applies to the **bulk** copy/
invert (per-row arrows are explicit single actions — likely ignore the toggle there, or honour
it = no-op when target non-empty; decide during impl). Hold its state in a builder field
(session-only; no AppConfig needed).

Key context: colours live in `_lightVals`/`_darkVals` dicts; boxes in `_lightBoxes`/`_darkBoxes`
(Tag=`(key,dark)`); `ActiveVals`/`OtherVals` are pill-relative; `PopulateColorBoxes()` refreshes
boxes+swatches from the dicts; `OnEditorChanged()` after any change (marks dirty + debounced
live apply + undo snapshot). `ColorFields` is the (key,label) list.

---

## 3.7.0 goal — Theme Builder

A **Theme Builder button in Settings → Appearance** that opens a builder where the user
can create a whole new theme and **see it applied live while changing it**.

**Approach: design first.** The design below was agreed on 2026-06-10. Implement in
phases; tick the checkboxes and add a dated progress-log entry each session.

---

## Current state (verified 2026-06-10)

`Views/ThemeBuilderWindow.xaml(.cs)` (~1,400 lines) already exists and is **fully
functional but unreachable** — nothing instantiates it. What it has:

- Theme list: built-ins (read-only) + user themes from `%APPDATA%\MasselGUARD\custom_themes\`
- **New** (seeds from the currently active theme via `ThemeManager.Instance.Current`),
  **Duplicate** (works on built-ins too), **Delete**
- 21 colour rows (hex TextBox + clickable swatch → WinForms `ColorDialog`)
- Structural fields: name/creator/description, font family + size, corner radius,
  opacity, title/status bar heights, background image (+ stretch/opacity), logo, app icon
- Manual **Preview** toggle (apply draft ↔ revert)
- **Save** → writes `theme.json` to the user theme folder, sets `cfg.ActiveTheme`,
  applies, saves config

### Gaps (why it isn't shippable yet)

1. **No entry point** — not reachable from any UI.
2. **No live apply** — preview is a manual toggle; the 3.7.0 ask is apply-while-editing.
3. **Legacy file format** — `CollectDraft`/`WriteThemeJson` read and write a *flat*
   `ThemeDefinition` (root-level colours, `Type` field). The 3.6.0 unified format wants
   structural settings at root + colours in `dark`/`light` sections. Saved themes work
   (legacy load path exists) but only carry one variant.
4. **WinForms ColorDialog** — functional but ugly and unthemed.
5. **`MessageBox.Show`** everywhere — rest of the app uses themed dialogs
   (`_main.ShowThemedInfo` / `ShowThemedYesNo`).
6. **English-only strings** — no `Lang` keys.
7. **Settings interplay undefined** — Settings uses a deferred draft config; the builder
   writes `ActiveTheme` directly. Opening both at once can cause a stale Settings draft
   to overwrite the builder's save.

---

## Design (agreed 2026-06-10)

### 1. Entry point
- New card in **Settings → Appearance**, directly under the Theme picker card:
  description text + **"Open Theme Builder"** button.
- Clicking it **closes the Settings window** and opens the builder owned by MainWindow.
  Rationale: the main window must be visible for live theming, and closing Settings
  sidesteps the stale-draft conflict (gap 7) entirely.
- When the builder closes, nothing reopens automatically; the user returns to Settings
  manually. The Appearance theme picker repopulates on tab entry (`PopulateThemePicker`
  is already called in `ShowTab`), so the new theme appears without extra wiring.

### 2. Live apply while editing (the core feature)
- Every edit (valid hex colour, slider move, font change, toggle) applies the draft to
  the running app immediately via `ThemeManager.Instance.ApplyPreview(draft, folder)`.
- **Debounce ~150–200 ms** (DispatcherTimer) so slider drags and hex typing don't apply
  per keystroke; invalid hex values are skipped until parseable.
- The manual Preview button is removed; a small **"● LIVE"** indicator shows that edits
  are being applied.
- **Close without saving → revert** to the committed theme via
  `_main.ApplyThemeFromConfig()` (reads committed config — same pattern the Settings
  preview uses). Prompt "Keep unsaved changes?" only if dirty: Save / Discard.
- Selecting a different theme in the builder list while dirty prompts the same way.

### 3. Dual-variant editing (unified 3.6.0 format)
- A **Dark / Light segmented pill** sits above the colour rows. It selects which
  variant's colours are shown and edited. Structural fields stay shared (root level).
- While the builder is open, the app shows the variant being edited — switching the
  pill restyles the whole app to that variant (very tangible feedback).
- Two helper buttons next to the pill:
  - **"Copy from Dark/Light"** — copies the opposite variant's colours into this one.
  - **"Auto-generate"** — fills this variant from the opposite via the existing
    `ThemeManager.AutoInvertVariant` (HSL inversion) so both variants always ship.
- **Save** writes the unified format: root = structural, `dark`/`light` = colour
  sections. A variant never edited and never generated is simply omitted (the app
  auto-generates at load time anyway).
- **Loading a legacy flat theme** (root colours): treat root colours as the variant
  matching its old `Type` field (default dark), leave the other side empty. Saving
  rewrites it in unified format — that *is* the migration.
- On open, the builder starts on the variant matching the current system mode.

### 4. Save semantics
- **Save = save + activate.** The user has been looking at the theme live; activating
  on save matches expectation (current behaviour already does this — keep it).
- Built-ins stay read-only; Duplicate remains the path to editing them.
- After save the builder stays open for further tweaking (status text "Saved ✓"
  instead of a MessageBox).

### 5. New-theme seeding (decided 2026-06-10)
"New theme" offers two starting points:
- **From current theme** (default) — copy the active resolved theme; tweak what you see.
- **From image** — pick a PNG/JPG; extract a palette and map it to theme slots:
  - Downscale to ~64×64, median-cut quantization (hand-rolled, no package) → dominant
    colour clusters.
  - Darkest dominant cluster → `ColorWindowBg`; lightened steps → `ColorSurface`,
    `ColorCard`; most saturated cluster → `ColorAccent` + `ColorHighlight`; borders
    derived from bg; text colours computed by contrast against the chosen bg.
  - **Status colours stay semantic** (success/danger/warning keep standard hues) —
    only ambience colours come from the image.
  - Build the variant matching the image's overall brightness; auto-generate the other
    via `AutoInvertVariant`.
  - Optional checkbox **"use image as window background"** (builder already supports
    bg image + opacity/stretch) so a photo theme is complete in one step.

### 6. Tray menu (decided 2026-06-10)
- Real tray menu restyles **on Save only** (it renders from committed resources +
  GDI+ icons; live-refreshing it is risky).
- The builder shows a **live mock preview of the tray menu** next to the `ColorTray*`
  fields: a rendered facsimile (TrayBg/TrayBorder panel, a few TrayText rows, one row
  in TrayHover state) that updates with every draft change.

### 7. Polish (lower priority, separate phase)
- Replace WinForms `ColorDialog` with a small themed WPF colour popup (hue strip +
  saturation/value square + hex box + a row of the theme's current colours).
- Group the 21 colour rows into collapsible sections: Core (bg/surface/card/border/
  accent/text) · Status (success/danger/error/warning/highlight) · Lists & log · Tray.
- Replace `MessageBox.Show` with themed dialogs.
- `Lang` keys for all builder strings (en/nl/de/fr/es).
- Optional: WCAG contrast hint per text-on-background pair (the High Contrast theme
  pedigree makes this on-brand).

---

## Implementation plan & progress

### Phase 0 — Design ✅ 2026-06-10
- [x] Inventory existing ThemeBuilderWindow; identify gaps
- [x] Design agreed (this document)
- [x] Open decisions resolved: seeding = from current theme OR from image (palette
      extraction); tray = apply on save + live mock preview in the builder

### Phase 1 — Entry point ✅ 2026-06-10
- [x] "Theme Builder" card + button in Settings → Appearance (under the theme preview card)
- [x] Click handler `OpenThemeBuilder_Click`: closes SettingsWindow (OnClosing reverts any
      running preview → builder starts from committed theme), opens
      `ThemeBuilderWindow(_main) { Owner = _main }` non-modal via `Show()`
- [x] Lang keys `ThemeBuilderTitle` / `ThemeBuilderDesc` / `ThemeBuilderOpenBtn` (5 languages)
- [x] Verified: `PopulateThemePicker` clears + repopulates on every Appearance visit and
      selects from a fresh `_draft` clone, so builder-created themes appear automatically

### Phase 2 — Live apply ✅ 2026-06-10
- [x] Debounced auto-apply (180 ms DispatcherTimer): all change handlers route through
      `OnEditorChanged()` → `ApplyDraftLive()` → `ThemeManager.Instance.ApplyPreview`.
      Asset browse/clear flows in automatically (path boxes fire `Field_Changed`)
- [x] Manual Preview button removed; `● LIVE` indicator (accent, with tooltip) shown for
      editable themes; `StatusLabel` shows "Saved ✓" instead of a MessageBox
- [x] Dirty tracking (`_dirty`); revert via `_main.ApplyThemeFromConfig()` (internal)
- [x] Save-or-discard prompt (`ResolveDirtyDraft` → `ShowThemedYesNo`) on close
      (`OnClosing`) and on switching themes in the list while dirty
- Save refactored to `TrySaveDraft()`; still = save + activate, builder stays open

### Phase 3 — Dual-variant editing ✅ 2026-06-10 (needs user build + manual test)
- [x] Dark/Light pill in the Colors card; colour boxes bind to `_darkVals`/`_lightVals`
      via `ActiveVals` (synced in `ColorBox_TextChanged`, shown by `PopulateColorBoxes`)
- [x] App restyles to the edited variant on pill switch (`Variant_Changed` →
      `ApplyDraftLive`; `CollectDraft.Type` = edited variant, which drives `ApplyPreview`).
      Pill stays enabled for read-only built-ins (view both variants); helpers locked
- [x] "Copy from Dark/Light" (label follows variant) + "Auto-generate" buttons —
      auto-generate reuses `ThemeManager.AutoInvertVariant` (widened private → internal)
- [x] Save writes the unified format via `WriteUnifiedThemeJson` (root = structural via
      JsonNode with colour/type keys stripped; dark/light sections with non-empty values
      only; empty section omitted). Legacy `WriteThemeJson` kept for New/Duplicate seeds
- [x] Legacy flat themes load into their `Type` variant (`LoadTheme` split logic);
      saving migrates them to the unified format
- [x] `Type` radios removed from editor; Duplicate now seeds from on-disk metadata so
      dual-variant copies keep both colour sections (unsaved edits not in the copy)
- After save the builder re-applies the edited variant (committed load uses system mode)
- Variant on open: system mode, but prefers a variant that actually has colours

### Phase 4 — New-theme seeding ✅ 2026-06-10 (needs user build + manual test)
- [x] `NewThemeDialog` (code-built, styled like InputDialog): folder name + radio
      "Current theme" / "An image" + image browse row + background checkbox
- [x] `ThemePalette.cs` (new, root, GUI-only — CLI csproj uses explicit includes):
      decode at 64 px → median-cut to 8 clusters → slot mapping per design §5
      (darkest/lightest weighted cluster → bg, lightness steps → surface/card/border/
      hover, most saturated → accent+highlight, text by contrast, tray from surfaces,
      status colours left empty → semantic system fallbacks)
- [x] Variant matches image brightness (weighted avg L); opposite side auto-generates
      at load time, or explicitly via the Phase 3 "Auto-generate" button
- [x] "Use image as window background" → image copied into the theme folder as bg.*,
      stretch=fill, opacity=0.18
- Bonus: "from current theme" now clones via JSON round-trip — the old code mutated
  `ThemeManager.Instance.Current.Name` (live definition) when seeding

### Phase 5 — Tray mock preview ✅ 2026-06-15 (needs user build + manual test)
- [x] Live tray-menu facsimile beneath the ColorTray* rows (`BuildTrayPreview` /
      `UpdateTrayPreview` in ThemeBuilderWindow.xaml.cs). Renders a TrayBorder/TrayBg
      panel with four rows (one in the TrayHover state), restyled immediately on every
      colour edit and on Dark/Light pill switch — using the SAME semantic fallbacks the
      real tray uses (tray bg→Surface, hover→Border, text→TextPrimary, border→Border).
- [x] Confirmed (by code path) the real tray menu picks up new colours after Save:
      `TrySaveDraft` → `ThemeManager.Instance.Load` → `ApplyTheme` fires `ThemeChanged`
      → `App.OnThemeChanged` → `ApplyTrayMenuTheme()` + icon redraw. Live `ApplyPreview`
      deliberately does NOT fire `ThemeChanged`, so the real menu only restyles on Save
      (matches the design — live restyle of the GDI+ menu is risky).

### Phase 6 — Polish
- [ ] Themed WPF colour picker popup (replace WinForms ColorDialog)
- [ ] Collapsible colour groups (Core / Status / Lists & log / Tray)
- [ ] Themed dialogs instead of MessageBox
- [ ] Localize builder strings (en/nl/de/fr/es)
- [ ] Optional: contrast hints

### Phase 7 — Docs & release
- [ ] MANUAL.md: Theme Builder section; remove "coming soon" from README
- [ ] THEME_INFO.md: note builder as the easy path; keep manual format reference
- [ ] WHATSNEW.md 3.7.0 section: Theme Builder entry
- [ ] Full BUILD.bat run + manual test pass (create / edit / dual-variant / image seed / activate / delete)

---

### Feedback round (2026-06-10) — all implemented, user build pending
- [x] **Live-preview/logo bug**: `new BitmapImage(uri)` kept files locked (OnDemand) and
      served stale bitmaps for re-used names like logo.png (WPF URI cache). New
      `ThemeManager.LoadImageUncached` (OnLoad + IgnoreImageCache + Freeze) used for
      logo, app icon, background image, and the builder's logo preview
- [x] **Panel opacity**: `ThemeDefinition.PanelOpacity` (0.2–1.0, default 1) — Surface +
      Card brushes get the alpha multiplied in, so the bg image shows through panels.
      Builder slider "Panel opacity" in the Window section
- [x] **Per-variant logo & background**: checkbox "Different logo & background per
      Dark/Light variant" — assets then follow the Dark/Light pill, stored in the
      variant sections (logo-dark.png / bg-light.jpg naming), `MergeVariant` prefers
      variant assets with root fallback; one shared set remains the default
- [x] **Zip export/import**: Export… button (footer) zips the theme folder
      (theme.json + images); Import… button (next to + New theme) extracts a zip into
      the user themes folder after validating it contains theme.json
- [x] **System theme in builder**: listed at the top of BUILT-IN (read-only view of the
      live Windows palette via `BuildSystemTheme`), and **Duplicate now works on it** —
      seeds a dual-variant copy of the current system colours. Duplicate also copies
      image assets now (previously only theme.json)
- [x] **Builder pop-ups always system-styled**: a broken draft theme applied live could
      make themed dialogs unreadable. InputDialog + NewThemeDialog now use native
      window chrome (ToolWindow), SystemColors, and shadow the app's implicit themed
      control styles with WPF defaults (`InputDialog.UseSystemStyles`); the
      save/discard prompts use the native MessageBox instead of ShowThemedYesNo

### Feedback round 3 (2026-06-10) — all implemented, user build pending
- [x] **Builder fully non-themable**: `ThemeManager.ApplySystemTo(ResourceDictionary)`
      fills the builder window's LOCAL resources with the plain Windows system palette
      (Apply() gained a target-dictionary overload). Local resources shadow the app
      resources, so live theme edits restyle the main window but never the builder.
      Dialogs/MessageBoxes were already system-styled
- [x] **Slider naming**: "Opacity" → "App transparency" (whole window, pre-existing
      behaviour); "Panel opacity" → "Panel transparency" with tooltip naming the
      sections (tunnel list, WiFi rules, activity log incl. headers/title/footer =
      Surface + CardBg brushes, which the alpha already targets)
- [x] **Font dropdown**: FontFamilyBox is now an editable ComboBox listing all
      installed families, each entry rendered in its own typeface
- [x] **Font embedding**: Export copies the theme's font files (regular+bold/italic,
      from the HKLM/HKCU font registry) into the theme folder before zipping —
      universal Windows fonts skipped. `ResolveFontFamily` loads folder *.ttf/*.otf
      as WPF **private fonts** (no installation needed on the importing machine),
      falling back to the installed font of the same name
- [x] **Selected tunnel-group tab themable**: tab buttons used FindResource (frozen
      brush snapshots); resource-based colours now use SetResourceReference so tabs
      follow theme switches and live builder edits
- [x] **Tray icons connected/disconnected, dark+light (all 4 variations)**:
      `ThemeDefinition.TrayIconConnected/TrayIconDisconnected` (+ per-variant via
      dark/light sections, resolved by MergeVariant), loaded into
      `Theme.TrayIconConnected/Disconnected` resources; `App.GetTrayIcon` prefers
      state icon → AppIcon → built-in shield. Builder: two new asset rows, included
      in the per-variant images checkbox (files tray-connected[-dark|-light].*)

## Progress log

| Date | Session work |
|---|---|
| 2026-06-23 | **Small theme fixes.** (a) Settings, Theme Manager and Theme Browser now bind `Opacity="{DynamicResource Theme.WindowOpacity}"` (like MainWindow) so they respect a theme's window transparency (e.g. Glass 0.85) — panel `panelOpacity` already worked via the Surface/CardBg brushes; small pop-ups left opaque for readability. (b) The Theme Manager now **opens on the active theme** (`SelectActiveTheme` in `Loaded` selects it in the list → LoadTheme) instead of the empty state; falls back to System if the active theme is missing. Build 0/0. |
| 2026-06-23 | **Theme Manager UX overhaul.** (1) **Buttons** restyled to the app-standard `FlatBtn` sizing (dropped the ad-hoc FontSize 10 / `Padding "X,0"` that made them look off). (2) **Add flow:** the scattered New/Download/Import buttons → one **"+ Add theme"** opening a new **`AddThemeDialog`** with a *Create* section (Name + **"Based on"** dropdown: Current theme / Two images / *Copy of ‹theme›* — the latter clones that theme's def **and copies its image/font assets** via new `CreateAndEditTheme(copyAssetsFrom:)`) plus **Download community themes…** and **Import .zip…** buttons (dispatched via `AddThemeMode`). Replaced `NewThemeDialog`. (3) **Per-theme actions** (Apply/Duplicate/Export/Delete) moved off the footer/sidebar into a **right-click context menu** on the theme list (`ThemeItem_RightClick` builds it, gated by kind); removed `DuplicateBtn`/`ExportBtn`/`ApplyBtn`/`DeleteThemeBtn` (+ their state code). Footer is now just Undo/Redo · LIVE · Cancel · Save. (4) **Hold-Shift preview:** `OnPreviewKeyDown/Up` apply/clear `ThemeManager.ApplySystemTo(Resources)` (re-added) to peek the window in Windows colours while editing (guarded to not fire while typing in a TextBox; `OnDeactivated` clears a stuck peek). Sidebar hints added: "Right-click a theme…" + "Hold Shift for Windows colours." Build 0/0; not GUI-tested. |
| 2026-06-23 | **Manager + dialogs now follow the active theme; lang cleanup; Shift-reset confirmed.** (1) Reverted the "pin to System (Windows colors)" decision: removed `ThemeManager.ApplySystemTo` from `ThemeBuilderWindow` ctor and the `ThemedDialog` base ctor (and deleted the now-unused `ApplySystemTo` method). The Theme Manager and all its pop-ups (`InputDialog`/`NewThemeDialog`/`ColorPickerDialog`/`ThemedMessageDialog`) now resolve colours from the **active theme** via `FindResource` → app resources, like every other window — so editing a theme live restyles them too. (Eyedropper/QR overlays stay non-themed frozen-screen windows by design.) (2) Removed the now-unused `ThemeBuilderTitle`/`ThemeBuilderDesc`/`ThemeBuilderOpenBtn` lang keys from all 6 locales (still 424-key parity). (3) Confirmed the **Shift-at-startup emergency reset** (`App.OnStartup`) reverts `ActiveTheme→"__system__"` + `SystemThemeMode→"auto"` + clears font override and loads System — the escape hatch if a live-edited theme makes the UI unreadable. Build 0/0. |
| 2026-06-23 | **All themes unified into one `%APPDATA%\MasselGUARD\themes\` folder.** Reverted the custom/shared split: `ThemeManager.{ThemeRoot, SharedThemeRoot, UserThemeRoot}` all point at `themes\`. Added `ConsolidateThemeFolders()` (startup) merging old `custom_themes\`/`shared-themes\`/`shared_themes\` → `themes\` (keep-existing on collision); replaced the two old migration methods. Added `ThemeNames()` (all themes) + `ThemeExists()`; removed `SharedThemeNames`/`IsSharedTheme`. **Shared/custom distinction dropped** — every theme in `themes\` is now editable+deletable; only System is read-only (`_readOnly = IsBuiltinTheme`). Builder list collapsed to **Built-in (System)** + **THEMES** (removed the SHARED `ListBox`). Theme Browser installs to `themes\`; its "Installed ✓" state + builder New/Duplicate/Import already block name collisions (the requested "check on create", now spanning the unified folder). Docs updated (CLAUDE.md, MasselGUARD.md, MANUAL.md, THEME_INFO.md, THEME_EXAMPLE.md, WHATSNEW.md, README.md, repo guide app-side path). Gallery repo keeps its `shared-themes/` folder layout (index.json `path`). Build 0/0. |
| 2026-06-23 | **App bundles no themes; `shared_themes` → `shared-themes`; 3 themes moved to gallery.** Removed blueongrey/highcontrast/glass from the app (deleted `shared_themes/`); they're now download-only from the MasselGUARD-themes repo. `ThemeManager.ThemeRoot` → `%APPDATA%\MasselGUARD\shared-themes` (hyphen); dropped `BundledThemeRoot`/`SeedBundledSharedThemes`; added `MigrateSharedThemesFolder` (old underscore appdata → hyphen), called at startup instead of seeding. BUILD.bat no longer copies a themes folder. Moved `THEME_INFO.md` + `THEME_EXAMPLE.md` to `docs/`. Updated all code comments + docs (CLAUDE.md, MANUAL.md, MasselGUARD.md, SHARED_THEMES_REPO_GUIDE.md uses `shared-themes/` folder). **Gallery handoff:** `gallery-staging/` (untracked) mirrors the MasselGUARD-themes repo — `index.json` (tags + previewDark/Light + `path: shared-themes/<id>`) + `shared-themes/<id>/theme.json` ×3 + README. **Copy its contents into the MasselGUARD-themes repo, then delete `gallery-staging/`** (don't commit it to the app repo; add real preview-*.png screenshots in the gallery). Build 0/0. |
| 2026-06-23 | **WiFi-rules-panel hide now reclaims its space.** `WifiRulesPanelRow` had `MinHeight="152"` in XAML; `RefreshWifiRulesPanel` set its `Height=0` when hidden but the MinHeight kept a 152 px empty gap below the tunnel buttons. Now also zeroes `MinHeight` when hidden (restores 152 when shown), so the tunnel list grows to fill the freed space. The timeline already collapses cleanly (its `InfoSectionRow` is `Auto`), so hiding it lets `MainContentGrid` (`*`) expand and the tunnel list fill there too. Build 0/0. |
| 2026-06-23 | **Shipped themes finalised + theme example file; WCAG claim dropped.** All three shipped themes (`blueongrey`/`highcontrast`/`glass`) confirmed fully populated (21 colour keys × 2 variants + all root fields) with `creator` = "Harold Masselink". Removed the "WCAG AAA" claim from the High Contrast theme description and from the docs (CLAUDE.md, shared_themes/THEME_INFO.md). New **`shared_themes/THEME_EXAMPLE.md`** — a complete copy-paste `theme.json` template (every field populated) + per-field guide, linking THEME_INFO.md and the repo guide. JSON validated for all four. No code change. |
| 2026-06-23 | **Shared themes → %APPDATA%; Browse moved to theme picker; config to Advanced.** `ThemeManager.ThemeRoot`/`SharedThemeRoot` now `%APPDATA%\MasselGUARD\shared_themes` (per-user — no elevation, survives updates). Bundled defaults still ship in `<exedir>\shared_themes\` (`BundledThemeRoot`) and are copied into the appdata folder at startup by `SeedBundledSharedThemes()` (overwrite same-named; downloads preserved), called in `App.OnStartup` after `MigrateLegacyUserThemes`. UI: **Browse shared themes…** button added to the Appearance theme-picker card (`BrowseThemes_Click`); the repo-URL config (URL box + **Default**) moved to **Advanced**; URL **empty → falls back to `AppConfig.DefaultSharedThemesRepoUrl`** in the browse handler. Removed the old Appearance shared-theme card + `DownloadThemes_Click`/`ShowDownloadStatus`. Docs updated (CLAUDE.md, MasselGUARD.md, MANUAL.md, THEME_INFO.md, SHARED_THEMES_REPO_GUIDE.md). Build 0/0. |
| 2026-06-23 | **Timeline colours now derive from the theme.** Replaced the fixed `_chartPalette`/`_wifiPalette` arrays with `TimelinePaletteColor(index, wifi)` — hues fan out from the theme **Accent** by the golden angle (137.5°), so any number of tunnels/SSIDs get distinct colours that match the active theme with zero manual picking. WiFi band is phase-shifted 180°; saturation/brightness adapt to dark vs light (read from `WindowBg` luminance). Stable per-name slots via `_chartColorIndex`/`_wifiColorIndex` (only-grow) keep each tunnel/SSID's colour consistent across renders; `_wifiColors` is now re-derived each render. Added `RefreshInfoSection()` to the `ThemeChanged` handler so the chart recolours live on theme switch. Build 0/0. |
| 2026-06-23 | **Theme Browser (manifest-driven shared-theme download).** Settings → Appearance repo URL now defaults to `https://github.com/masselink/MasselGUARD-themes` (`AppConfig.DefaultSharedThemesRepoUrl`) with a **Default** button (`ResetThemesRepo_Click`); the **Browse…** button opens a new wide **`Views/ThemeBrowserWindow`** (themed, app palette) — cards with `previewDark`/`previewLight` images, a Dark/Light header toggle, text search + tag chips (AND filter), and per-theme Install. `ThemeDownloadService` gained a non-zip manifest path: `ThemeManifestEntry`/`ThemeManifest`, `FetchManifestAsync` (reads `index.json`, raw base from the GitHub URL, main→master), `FetchRawAsync` (preview bytes), `InstallThemeAsync` (per-file raw download into `shared_themes/<id>/` with `SafeName`/`SafeRelative` traversal guards). Legacy zip `DownloadAsync` kept as fallback. Repo contract documented in **docs/SHARED_THEMES_REPO_GUIDE.md** (tags + previewDark/Light + browser behaviour). Build 0/0; not GUI-tested. |
| 2026-06-23 | **i18n-ja PR integrated + JP flag + security hardening.** Merged the Japanese i18n PR into dev (lang key union, ja.json backfilled to 427-key parity). Added missing **`lang/flags/jp.png`** (20×15 Hinomaru; ja.json declares `_flag:"jp"`, loaded by `UiHelpers` from `lang/flags/{_flag}.png`). **Security:** added `ThemeManager.ResolveThemeAsset(folder, fileName)` — rejects rooted/UNC paths and `..` traversal and confirms the resolved path stays inside the theme folder; routed background/app-icon/tray-icon/logo loading through it so an untrusted (downloaded) theme.json can't point the app at files outside its folder. Build 0/0. |
| 2026-06-10 | 3.6.0 finished & committed (settings reorg, external tracking, auto-reconnect, flags, docs). 3.7.0 cycle started (Chromatic Chameleon). Theme Builder design written and agreed, incl. image-based seeding and tray mock preview (this file). |
| 2026-06-10 | **Phase 1 done** — Theme Builder launcher card in Appearance, Settings closes on launch, lang keys ×5, picker-refresh verified. Build verified. |
| 2026-06-10 | **Phase 2 done** — live apply with 180 ms debounce, LIVE indicator + Saved ✓ status, dirty tracking with save/discard prompts on close and theme-switch, revert via ApplyThemeFromConfig. |
| 2026-06-10 | **Phase 3 done (code)** — dual-variant editing: Dark/Light pill, copy/auto-generate, unified-format save with legacy migration on load+save, Type radios removed. |
| 2026-06-10 | **Phase 4 done (code)** — New-theme dialog with seeding from current theme or image; ThemePalette.cs median-cut extraction; bg-image option; clone fix for current-theme seeding. **Phases 1–4 not yet build-verified — user runs BUILD.bat.** Next: Phase 5 (tray mock preview). |
| 2026-06-10 | **Build crash fixed** — user's build crashed at startup (`FileNotFoundException: MasselGUARD 3.7.0.0`): stale `obj\` intermediates from before the 3.6→3.7 bump left the exe with mismatched assembly identity vs WPF resource pack-URIs. Cleared obj/bin; BUILD.bat now cleans intermediates before every publish; gotcha documented in CLAUDE.md. |
| 2026-06-10 | **Phases 1–4 build-verified** — clean rebuild succeeded (the ~393 warnings are pre-existing CA1416 Windows-API noise, harmless for this Windows-only app; ThemePalette.cs warning-free). Manual test pass of the builder still pending. Next: Phase 5 (tray mock preview). |
| 2026-06-10 | **False orphan warning fixed** — `GetOrphanedServices` flagged any non-Running `WireGuardTunnel$` service; the startup orphan check (background dispatcher priority) raced an auto-connect and saw the brand-new service in its brief Stopped window before Start(). Now skips `StartPending` services and any tunnel whose VM is IsConnecting/IsActive/IsDisconnecting. |
| 2026-06-10 | **Builder open-crash fixed** — first-ever open of ThemeBuilderWindow threw NullReferenceException in FontSize_Changed: slider `Minimum` coercion fires ValueChanged during InitializeComponent, before the label elements exist (latent bug from the original unwired builder code). All six slider handlers + Variant_Changed now guard with `if (!IsInitialized) return;` — UpdateSliderLabels() sets the labels after the editor is populated. |
| 2026-06-10 | **Startup crash + CA1416 warnings fixed at the root** — `GenerateAssemblyInfo=false` (old wpftmp workaround) was the cause of BOTH: clean builds produced a 0.0.0.0 assembly while WPF resource pack-URIs demanded 3.7.0.0 (startup `FileNotFoundException` for its own assembly; earlier builds only worked via stale obj\ intermediates), and the missing SDK `SupportedOSPlatform` attribute produced ~400 CA1416 warnings. Removed the flag (wpftmp duplicate problem no longer occurs on current SDK), removed the interim `AssemblyAttributes.cs`, added `IncludeSourceRevisionInInformationalVersion=false` (keeps `+git-sha` out of the About build stamp). **Verified: clean `dotnet build -c Release` = 0 warnings, 0 errors, AssemblyVersion 3.7.0.0.** |
| 2026-06-15 | **Phase 5 done (code)** — live tray-menu mock preview in the Theme Builder (see Phase 5 above). |
| 2026-06-15 | **Build broken by SDK 10.0.301 — interim fix (SUPERSEDED, see next row).** Switched `GenerateAssemblyInfo` OFF + a `WriteAssemblyInfo` target writing `MgAssemblyVersionInfo.cs`. This was a misdiagnosis: the CS0579 was never the WPF two-pass — it was the GUI globbing the CLI sub-project's generated AssemblyInfo (next row). The "generation OFF works" appearance came from cleans that also wiped `MasselGUARDcli\obj\`. |
| 2026-06-15 | **Build CS0579 root-caused & fixed cleanly.** The GUI `.csproj`'s default `**/*.cs` glob reached into `MasselGUARDcli\obj\` and compiled `MasselGUARDcli.AssemblyInfo.cs` into the GUI, whose attributes collided with the GUI's own → CS0579 *duplicate attribute*. Intermittent because it only triggers once the CLI's `obj\` is populated. **Fix: exclude `MasselGUARDcli\**` (Compile/Content/EmbeddedResource/None) from the GUI globs, next to the existing `deps\**` excludes.** Reverted ALL the AssemblyInfo gymnastics — `GenerateAssemblyInfo` is back at the SDK default (ON); no custom target, no committed/generated version file. **Verified: clean publish with `MasselGUARDcli\obj` populated = 0 warnings / 0 errors; managed AssemblyVersion 3.7.0.0; AssemblyInformationalVersion 3.7.0.<build> (stamp works); Company=MasselGUARD.** CLAUDE.md corrected. |
| 2026-06-15 | **QR import: drag-a-box screen scanner.** Replaced the old "Scan QR code" handler (which looped a full-primary-screen capture) with `QrScreenCaptureWindow` — a dimmed, full-screen, topmost overlay (all monitors) with a movable + corner-resizable translucent box and a Scan/Cancel toolbar that follows it. On Scan the overlay + owner dialog hide, the box's screen region is grabbed (DPI-correct via `PointToScreen` + GDI `CopyFromScreen`) and decoded with ZXing (`BarcodeReader`, QR only); the payload (raw WireGuard config) flows into the existing `SaveConfigToFile` → `TunnelImported` path. "No QR found" hint lets the user re-aim without closing. Removed the dead `ScanQR`/`CaptureCameraFrame`/`_qrCts`. New lang keys `BtnScan`/`QrScanInstruction`/`QrScanNotFound` ×5. Non-themed (system-styled overlay). |
| 2026-06-15 | **Theme Builder: 4 changes.** (1) **"Sans Serif Collection" bug** — that Win11 variable-font *collection* isn't a renderable family; WPF mis-measures it so the tunnel list collapsed to one row. Now filtered from the font dropdown (`IsUnusableFontFamily`). (2) **Colours side-by-side** — each row is now `name | Light hex+swatch | ← → | Dark hex+swatch` (header row + the Dark/Light pill kept; pill still drives the live preview). `←` copies Dark→Light, `→` Light→Dark. Rewrote `BuildColorRows`/`AddColorCell`/`Arrow_Click`; split `_colorBoxes`/`_colorSwatches` into `_lightBoxes`/`_darkBoxes`/`_lightSwatches`/`_darkSwatches` (Tag = `(key,dark)`); `ColorBox_TextChanged`/`UpdateSwatch`/`Swatch_Click`/`PopulateColorBoxes`/`CollectDraft`/`SetEditorReadOnly` updated; removed the separate 💧 dropper buttons. (3) **Picker ↔ eyedropper** — `ColorPickerDialog`: moving the cursor off the popup opens the screen eyedropper (`FetchFromScreen`); the grabbed pixel becomes the picker's colour (re-arms on MouseEnter). (4) **Theme from images = two pictures** — `NewThemeDialog` now takes a light-mode and a dark-mode image; `NewTheme_Click` seeds `seed.Light`/`seed.Dark` from each via `ThemePalette.FromImage`; `CreateAndEditTheme`+`CopyVariantBackground` write per-variant `bg-light.*`/`bg-dark.*` when "use as background" is checked. |
| 2026-06-15 | **Builder confirmations/errors themed.** The Delete-theme confirmation (and the builder's other native `MessageBox` dialogs — save/discard prompt, copy/generate-variant info, palette/export/import errors, "already exists", "built-in can't save", "name empty") rendered as always-light native dialogs clashing with the dark System-themed builder. Added `ThemedMessageDialog` (System palette via `ApplySystemTo` + builder chrome; `Confirm`/`Info` statics; result via property, no DialogResult) and routed all ThemeBuilderWindow message boxes through it. `NewThemeDialog`'s own validation boxes left native (that window is its own light/system dialog). |
| 2026-06-15 | **Colour picker matches System theme.** `ColorPickerDialog` previously used `SystemColors.WindowBrush` (always classic light, even in Windows dark mode) → it rendered white against the dark builder. Reworked to style itself via `ThemeManager.ApplySystemTo(Resources)` (same System (Windows colors) palette the builder uses) with custom chrome mirroring the builder window: `WindowStyle=None` + `AllowsTransparency`, `WindowBg`/`Accent` outer border + `Theme.CornerRadius`, a `Surface` title bar ("Pick a colour" in Accent + ✕ + DragMove), hex box in `CardBg`/`TextPrimary`/`BorderColor`, and OK/Cancel using the `SuccessBtn`/`FlatBtn` styles. Still independent of the live draft (uses the system palette, not the draft), so it stays readable. |
| 2026-06-15 | **Three picker fixes.** (1) **Custom colour picker** — replaced the WinForms `ColorDialog` (`Swatch_Click`) with `ColorPickerDialog`: a system-styled HSV picker (saturation/value square + hue strip + live preview + editable hex + RGB readout), non-themed via `InputDialog.UseSystemStyles`. (2) **Eyedropper "not working"** — root cause was `Background=Brushes.Transparent` on an `AllowsTransparency` window = **click-through at the OS level** (no mouse events). Rewrote `EyedropperWindow` to freeze the virtual desktop into a bitmap shown full-screen (opaque → receives mouse) and sample pixels from that frozen bitmap (pixel-exact, no tint). (3) **QR Cancel crash** (`InvalidOperationException: DialogResult can be set only after … shown as dialog`) — the Scan path toggled `Visibility` which reset the dialog's shown-as-dialog state, so Cancel's `DialogResult=false` threw. Dropped `DialogResult` from both `EyedropperWindow` and `QrScreenCaptureWindow` (results via `Picked`/`DecodedText` + `Close()`), and the QR capture now hides via `Opacity` not `Visibility`. Added `using Path = System.IO.Path;` to ThemeBuilderWindow (Shapes import made `Path` ambiguous). |
| 2026-06-15 | **Theme Builder: screen eyedropper.** Each colour row now has a 💧 button (next to the hex box + swatch) that opens `EyedropperWindow` — a full-screen, fully-transparent topmost overlay across all monitors. Hover shows the pixel colour under the cursor (GDI `GetCursorPos`+`GetDC`+`GetPixel`) in a floating swatch+hex readout; left-click captures it into that slot (writes hex → live apply + swatch), right-click/Escape cancels. Transparent overlay = no tint on sampled pixels; inherently non-themed. Existing manual paths kept: type hex, or click the swatch for the WinForms `ColorDialog`. |
| 2026-06-15 | **Theme Builder: Cancel + Undo/Redo.** Footer **Cancel** button (enabled when dirty) discards unsaved edits — `LoadTheme(_editingName)` reloads from disk + `_main.ApplyThemeFromConfig()` reverts the live app. **Undo/Redo** (↶ ↷ footer buttons + Ctrl+Z/Ctrl+Y, the latter suppressed while a TextBox is focused so its own text-undo still works): full editor snapshots (`CollectDraft()` structural + `_darkVals`/`_lightVals` + asset fields + `_editingDark`/`_perVariantAssets`) captured once per debounced edit-settle in the apply timer; `RestoreSnapshot` repopulates via `PopulateEditor` under `_loading` and re-applies live. Stacks reset on `LoadTheme`; Cancel disabled after Save. Builder pop-ups confirmed already non-themeable (native MessageBox/file/color dialogs + `InputDialog.UseSystemStyles`; window pinned via `ApplySystemTo`) — no change needed. |
| 2026-06-15 | **DNS-leak alerts — three channels.** `MainViewModel.MaybeWarnDnsLeak` edge-triggers (once per leak episode via `_dnsLeakWarned`, re-armed on recovery/disconnect) when an active tunnel's `CheckDnsLeak` first hits `PotentialLeak`, ONLY while unmitigated (`DnsLeakService.IsSmartNameResolutionDisabled()` false). Two independent config bools `AppConfig.DnsLeakWarnLog`/`DnsLeakWarnToast` (default on) drive the activity-log line and the tray toast (StripColor=Warning). The third channel is the always-on inline status icon `ShowDnsIndicator` (badge next to bandwidth — `DnsLeakDisplay`). All three toggles consolidated into Settings → Advanced → DNS leak protection ("Possible-leak alerts" group); the old `ShowDnsIndicator` toggle was removed from the Tunnels tab. Disable = all three off. Lang keys ×5 (`SettingsDnsLeakAlertIcon/Log/Toast` + reworded Warn label/desc). Replaces the earlier single `WarnOnDnsLeak` bool. |
| 2026-06-22 | **Theme Builder NEXT-SESSION items A/B/C done (code).** (A) **Native pop-ups themed** — `InputDialog` + `NewThemeDialog` converted from native (`SystemColors`/`ToolWindow`/`UseSystemStyles`) to the System palette + builder chrome. Extracted the shared chrome into a new `ThemedDialog` base (ApplySystemTo + borderless `AllowsTransparency` + `WindowBg`/`Accent` border + `Surface` title bar with ✕/DragMove, via `SetThemedContent`); **retrofitted `ColorPickerDialog` + `ThemedMessageDialog` onto it too** so there's ONE chrome copy. Both dialogs drop `DialogResult` for a `Confirmed` property (callers updated); `NewThemeDialog` validation now routes through `ThemedMessageDialog.Info`; removed `InputDialog.UseSystemStyles`. (B) **Global Invert toggle** — `InvertCopyCheck` next to the Dark/Light pill drives the per-row arrows: raw `← →` when off, accent-bold `⇇ ⇉` when on (`RefreshArrowGlyphs`), inverting each copy via `InvertColorValue` (one-key throwaway `ThemeDefinition` → `AutoInvertVariant`, preserving bg/fg clamping). (C) **Bulk buttons relabeled** to "Copy other → this" (raw) / "Copy inverted → this" (was Auto-generate); new `EmptySlotsOnlyCheck` ("Overwrite only empty slots") makes both bulk buttons fill blanks-only via `BulkApplyToActive`. **Reconciliation:** B said the toggle drives the bulk buttons too, but C's two dedicated raw/inverted bulk buttons make that redundant, so the toggle governs the **per-row arrows only** (C's "or keep" path); per-row arrows ignore the empty-slots toggle (explicit single actions). Removed obsolete `UpdateVariantButtons` (button label is now fixed/variant-agnostic). Both new toggles are session-only fields, disabled for read-only built-ins. **Build: clean `dotnet build -c Release` = 0/0.** Strings still English-only (Phase 6 localization). |
| 2026-06-22 | **Install theme folder renamed `theme\` → `shared_themes\`.** Three-tier model formalised: **embedded** (only the virtual `__system__` Windows-colours theme, in code) · **shared** (`<exedir>\shared_themes\`, %ProgramFiles%, shipped/downloaded, read-only) · **custom** (`%APPDATA%\MasselGUARD\custom_themes\`, per-user, editable). `git mv theme shared_themes` (grey/highcontrast/THEME_INFO.md moved); `ThemeManager.ThemeRoot` → `shared_themes`; BUILD.bat copy step + summary updated; docs updated (CLAUDE.md, docs/MasselGUARD.md, docs/MANUAL.md, shared_themes/THEME_INFO.md). Build 0/0. |
| 2026-06-22 | **Shared themes set read-only (but still deletable/copyable).** Refinement of the row below: split the builder's single read-only flag into `_readOnly` (System + Shared → editor locked, no save/asset/undo) and `_canDelete` (Shared + Custom → delete allowed; System not). Crucially **decoupled preview from editability**: `ApplyDraftLive` no longer early-returns on read-only and `Variant_Changed` calls it unconditionally, so the **Dark/Light pill previews live for shared/System themes** while their fields stay locked (edit handlers like `OnEditorChanged` remain `_readOnly`-gated, so the only path that fires it for read-only is the pill). SHARED list items now show the 🔒 lock; banner/save messages reworded ("System and shared themes are read-only"). Shared delete uses `EditingThemeDir` (→ `shared_themes\`). Docs updated. Build 0/0. |
| 2026-06-22 | **Three-tier theme model in the builder + Dark/Light preview fix.** Redefined `ThemeManager.IsBuiltinTheme` to be **System-only** (the one embedded, read-only theme); renamed `BuiltinThemeNames()` → `SharedThemeNames()`; added `IsSharedTheme()`. **This fixes the Dark/Light pill preview** that "stopped working": shared themes (glass/blueongrey/highcontrast) were treated as read-only built-ins, so `Variant_Changed`'s live-apply (`if (!_isBuiltin …)`) was gated off — now `_isBuiltin` is false for them, so the pill restyles the app live again. Builder list now has **three groups**: BUILT-IN (System, locked), SHARED (`shared_themes\`, editable/deletable/copyable, no lock), CUSTOM (`custom_themes\`). New `SharedList` ListBox + `ThemeList_SelectionChanged` clears the other two lists. Edit/save/delete/asset paths route through new `EditingThemeDir => ThemeManager.ThemeFolder(_editingName)` so shared-theme edits write back to `shared_themes\` in place (elevated app can); New/Duplicate still target `custom_themes\`. Read-only banner + save message reworded to "System theme". Docs updated (CLAUDE.md, docs/MasselGUARD.md). Build 0/0. |
| 2026-06-22 | **Shipped themes filled out + new Glass theme.** `git mv shared_themes/grey → shared_themes/blueongrey`; `ConfigService.Load` remaps `ActiveTheme "grey" → "blueongrey"` (one-time, saves) so existing selections survive. Both default themes now have ALL elements populated in both variants (added the four `colorTray*` + `colorTrayImageMargin`, root `panelOpacity`/`type`). **High Contrast retuned for WCAG AAA** — every text/background pair ≥ 7:1 (fixed the low-contrast log-timestamp greys #666→#B0B0B0 / #767676→#5A5A5A, danger→#FF8080 dark / #B00000 light, muted dark→#D0D0D0). New **`glass`** theme ("Glass"): `windowOpacity 0.95` + `panelOpacity 0.55` + `cornerRadius 10`, cool blue-grey translucent palette, full dark+light variants. Docs updated (CLAUDE.md, docs/MasselGUARD.md). All three theme.json validated; build 0/0. |
| 2026-06-22 | **Download shared themes from a git repo.** New `Services/ThemeDownloadService.cs` (GUI-only): fetches a repo as an **HTTPS zip archive** (no git needed) — accepts a direct `.zip` URL or a GitHub/GitLab repo URL (tries `/archive/refs/heads/main.zip` then `master`), extracts, and installs every folder containing a `theme.json` into `ThemeManager.SharedThemeRoot` (`<exedir>\shared_themes\`), overwriting same-named themes and leaving the rest (user may delete the folder; gets wiped on app update — accepted). Config: `AppConfig.SharedThemesRepoUrl` (configurable). UI: a "Shared theme repository" card in Settings → Appearance (URL box + Download button + status line); `DownloadThemes_Click` persists the URL immediately (`_main.ConfigSvc.Save()`), downloads async, then `PopulateThemePicker()` to surface results. App's `requireAdministrator` elevation lets it write to Program Files. **Strings are English-only (localization = Phase 6).** Build 0/0. |
| 2026-06-22 | **Default themes discovered from disk, not hardcoded.** "High Contrast" + "Blue on grey" already ship as files in `<exedir>\theme\` (BUILD.bat copies `theme\` → `dist\theme\`); they were never embedded in the binary. But `ThemeManager` *hardcoded* their folder names in `BuiltinThemeNames` HashSet. Replaced it with `BuiltinThemeNames()` (enumerates `<exedir>\theme\*\theme.json`) and rewrote `IsBuiltinTheme` to test for a `theme.json` under `ThemeRoot`. Builder's `PopulateThemeList` now lists every shipped theme folder. Net: the app embeds NO theme names — only the virtual `__system__` (Windows colours) theme is built in code; adding/removing a default theme is just a folder under `theme\`. Docs updated (docs/MasselGUARD.md). Build 0/0. |
| 2026-06-22 | **User-theme folder renamed `themes\` → `custom_themes\`.** `ThemeManager.UserThemeRoot` now points to `%APPDATA%\MasselGUARD\custom_themes\`; added `ThemeManager.MigrateLegacyUserThemes()` (moves the whole old folder when the new one is absent, else merges per-theme skipping name collisions, then deletes the old folder if empty — best-effort, wrapped in try/catch) called once at startup in `App.OnStartup` right after `bootCfg.Load()`, before any theme load. All theme paths already route through `UserThemeRoot`, so no other code changed. Docs updated: CLAUDE.md, README.md, theme/THEME_INFO.md, docs/MANUAL.md, docs/MasselGUARD.md, docs/WHATSNEW.md. Build 0/0. |
| 2026-06-22 | **Tray colours honoured by copy / invert.** Tray slots (`ColorTray*`) are usually left blank and resolve via semantic fallback (TrayBg→Surface, TrayHover→Border, TrayText→TextPrimary, TrayBorder→Border), so copy-all / arrows / invert skipped them and the tray menu appeared unaffected. Added `TrayFallback` map + `SourceColor(src,key)` (own value, else tray fallback from the same variant); the per-row arrows and `CopyAllVariant` now source through it, so copy/invert populate the tray menu even when its own slots are empty (writes explicit values into the target variant). Build 0/0. |
| 2026-06-22 | **Theme Builder bulk-copy rework (user follow-up).** Per request the two active-relative bulk buttons collapsed into the toggle model: removed both "Copy other → this" and "Copy inverted → this" (and `CopyVariant_Click`/`GenVariant_Click`/`BulkApplyToActive`/unused `OtherVals`). The global **Invert** toggle now governs raw-vs-inverted for ALL copy actions — the per-row `← →` arrows AND the new bulk buttons. Added two absolute directional bulk buttons: **"Copy all → Light"** (`CopyAllToLight_Click`, Dark→Light) and **"Copy all → Dark"** (`CopyAllToDark_Click`, Light→Dark) via shared `CopyAllVariant(source, target)`, both honouring Invert (per-key `InvertColorValue`/`AutoInvertVariant` clamping) + "Overwrite only empty slots". `SetEditorReadOnly` updated to the new button names. **Build 0/0.** |
| 2026-06-15 | **Rule-count column fix.** Removing/adding/editing a WiFi rule didn't update the per-tunnel "rule count" column. `TunnelsListView.ItemsSource` is a `.ToList()` snapshot from `ApplyGroupFilter` (via `RebuildTunnelGroups`), not the live `_vm.TunnelList`; the rule handlers called `RebuildTunnelList()` (new VMs) but not `RebuildTunnelGroups()` (refresh snapshot). Added `MainWindow.OnRulesChanged()` (Save + RefreshWifiRulesPanel + RebuildTunnelList + RebuildTunnelGroups) and routed all rule mutations through it (`WifiRuleAdd/Edit/Delete_Click` + the public `*RulePublic` methods). |

---

## Key files

| File | Role |
|---|---|
| `Views/ThemeBuilderWindow.xaml(.cs)` | The builder (exists, unwired, legacy-format) |
| `ThemeManager.cs` | `Load(name, isDark)`, `ApplyPreview`, `MergeVariant`, `AutoInvertVariant`, `UserThemeRoot`, `SharedThemeNames`, `IsBuiltinTheme` (System only), `IsSharedTheme`, `ThemeFolder` |
| `Views/SettingsWindow.xaml(.cs)` | Appearance tab (`PageAppearance`), `PopulateThemePicker`, `ShowTab` |
| `Models/AppConfig.cs` | `ActiveTheme` (default `__system__`), `SystemThemeMode` |
| `theme/THEME_INFO.md` | Unified theme format reference |

## Build

```bat
BUILD.bat          # .NET 10 SDK → dist\  (also: dotnet build MasselGUARD.csproj -c Release for a quick check)
```
