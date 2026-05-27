# MasselGUARD — Changes from v2.9.0 to v3.0.0

---

## Custom appearance system

The Appearance tab was redesigned from a single theme picker into a fully structured appearance control panel.

### System theme mode

A three-pill selector replaces the old "auto theme" toggle:

| Pill | Behaviour |
|---|---|
| **Auto** | Follows Windows dark/light preference (polls every 5 s) |
| **Light** | Always use the light theme |
| **Dark** | Always use the dark theme |

`AppConfig.SystemThemeMode` stores `"auto"` / `"light"` / `"dark"`.

### Custom theme toggle

`UseCustomTheme` (default off) separates the two colour sources:

| State | Source |
|---|---|
| **Off** | Windows 11 system accent and palette (via `ThemeManager.LoadSystem`) |
| **On** | Custom theme files from the `theme/` folder |

When **Custom appearance** is off the theme file pickers collapse automatically.

### Separate dark / light theme pickers

When custom themes are on, two independent pickers appear — one for dark mode, one for light. The active picker (based on current mode) drives the live theme.

`AppConfig.ActiveDarkTheme` and `AppConfig.ActiveLightTheme` stored independently, so switching between light and dark mode no longer resets either selection.

---

## Theme preview button

Theme changes **no longer apply immediately** when you select a different theme or change mode pills. Instead a **▶ Preview** button appears in the Appearance tab.

| State | Button label | Effect |
|---|---|---|
| Idle | `▶  Preview` | Applies the full draft theme (system mode + custom flag + selected file + draft font) to the interface for 10 seconds |
| Counting down | `↩  Xs` (accent colour) | Clicking again reverts immediately |
| Expired | Auto-reverts | Interface returns to committed theme + font |

**What reverts:** the theme that was active when Settings was opened (`_originalTheme`), plus the committed font override.

**Changing any theme setting while preview is running** cancels the preview automatically, so the interface returns to committed state before the next preview can start.

`ThemePreviewBtn` is always visible (not inside `CustomThemePickersPanel`) so it covers both the system-mode selection and the custom theme file pickers.

---

## Font override

A new **Font** section in Settings → Appearance lets the user replace the theme's typeface with any installed system font.

### Font family picker

- `IsEditable="True"` ComboBox populated with all system fonts
- Each item renders in its own typeface via an explicit `DataTemplate` + `FontPickerItem` class (binds `FontFamily` directly on the `TextBlock` — bypasses WPF's unreliable property inheritance through an editable ComboBox)
- `TextSearch.TextPath="DisplayName"` enables keyboard type-to-jump
- `ToString()` override returns `DisplayName` so the selected item text box shows the font name correctly
- First item: `(System UI font)` — clears the override

### Font size slider

Slider range 8–18 pt, snap-to-tick, paired value label (`12 pt`).

### In-settings preview label

A sample line — `MasselGUARD — 1.MasselinkVPN-Split-AG — Connected 3m 55s` — renders in the currently previewed font. The label **only updates when Preview is clicked**, not on every picker change, reflecting the actual interface state.

### `AppConfig` fields

```csharp
public bool   FontOverrideEnabled { get; set; } = false;
public string FontOverrideFamily  { get; set; } = "";
public double FontOverrideSize    { get; set; } = 0.0;
```

---

## Font preview button

A **▶ Preview** button sits to the left of the font size slider (`[▶ Preview]  min────●────max`).

| State | Behaviour |
|---|---|
| Idle | `▶  Preview` (muted) — click to start |
| Counting down | `↩  Xs` (accent) — 10-second countdown; clicking reverts |
| Expired | Auto-reverts to committed font |

**On click:**
1. `ApplyFontPreview()` — updates the in-settings preview label with the draft font
2. `ThemeManager.ApplyFontOverride(enabled, family, size)` — applies to whole interface

**On revert (cancel, timeout, or picker change):**
- Whole-interface font restored to committed config
- Preview label reset to committed config values (not draft)

**Picker changes while preview is active** (`FontFamilyPicker_Changed`, `FontSizeSlider_Changed`) cancel the preview immediately. The preview label no longer updates when the pickers change — only when Preview is clicked.

---

## Activity log panel toggle

The activity log panel on the right of the main window can now be hidden and restored without leaving the main window.

### Main window controls

| Control | Where | When visible |
|---|---|---|
| `☰` (Log open button) | Right side of tunnel header, after group tabs | Only when log is **collapsed** |
| `»` (Collapse button) | Right side of activity log header | Always (when log panel is visible) |

Both controls share a single `LogToggle_Click` handler that calls `SetLogPanelVisible(!_logPanelVisible)`.

### Column layout when collapsed

| Column | Visible | Hidden |
|---|---|---|
| 0 — Tunnel+WiFi list | `1.5*` | `1.5*` (unchanged) |
| 1 — Gap | `10` | `0` |
| 2 — Log panel | `*` | `0` |

`LogPanelGrid.Visibility` set to `Collapsed` — no space taken. `LogOpenBtn.Visibility` toggled inversely.

### Settings toggle (General → Interface)

A new **Interface** section appears in Settings → General below App Mode:

- **Show activity log** — `ToggleSwitch` labelled "Display the activity log panel on the right side of the main window."
- Changes take immediate effect on the main window via `_main.SetLogPanelVisible()`
- Cancelling Settings reverts the panel to the committed state
- Saved value persists via `AppConfig.ShowActivityLog` (default `true`)

### `AppConfig` field

```csharp
public bool ShowActivityLog { get; set; } = true;
```

Applied in `MainWindow.OnLoaded` after `RebuildLog()`:
```csharp
if (!ConfigSvc.Config.ShowActivityLog)
    SetLogPanelVisible(false);
```

---

## Confirm on close

A new **Confirm disconnect on exit** toggle in Settings → Advanced (`AppConfig.ConfirmOnClose`, default `true`).

When enabled: if tunnels are active when the window is closed, a themed Yes/No dialog asks before disconnecting.
When disabled: tunnels are disconnected silently on exit.

---

## Update check frequency

Settings → About now shows a **frequency pill strip** directly below the "Version" card:

| Pill | Value | Behaviour |
|---|---|---|
| On start | `"onstart"` | Checks once on every app launch |
| Daily | `"daily"` | Checks once per day |
| Weekly | `"weekly"` | Checks once per week (default) |
| Manual | `"manual"` | Never checks automatically |

`AppConfig.UpdateCheckFrequency` (default `"weekly"`).

---

## Window chrome refinements

The title bar minimize / maximize / close buttons were resized for visual balance:

| Button | Character | FontSize |
|---|---|---|
| Minimize | `─` | 14 |
| Maximize | `□` | 13 |
| Close | `✕` | 11 |

Settings button reverted to icon-only `⚙` (no label).

---

## Settings cancel — full revert

`OnClosing` now always stops **both** preview timers (font and theme) before any revert logic runs, preventing stale timer ticks after the window closes:

```csharp
_fontPreviewTimer?.Stop();   _fontPreviewTimer  = null;  _fontPreviewActive  = false;
_themePreviewTimer?.Stop();  _themePreviewTimer = null;  _themePreviewActive = false;
```

When cancelled (not saved):
- Theme reverted to `_originalTheme`
- Font reverted to committed config
- Activity log visibility reverted to committed `ShowActivityLog`

---

## `AppConfig` additions summary

| Field | Default | Purpose |
|---|---|---|
| `ShowActivityLog` | `true` | Activity log panel visible on start |
| `FontOverrideEnabled` | `false` | Apply custom font override |
| `FontOverrideFamily` | `""` | Font family name (empty = system UI font) |
| `FontOverrideSize` | `0.0` | Font size in pt (0 = theme default, ~11 pt) |
| `UseCustomTheme` | `false` | Use theme files instead of Windows system colors |
| `SystemThemeMode` | `"auto"` | `"auto"` / `"light"` / `"dark"` |
| `ActiveDarkTheme` | `"default-dark"` | Theme file for dark mode |
| `ActiveLightTheme` | `"default-light"` | Theme file for light mode |
| `ConfirmOnClose` | `true` | Ask before disconnecting on exit |
| `UpdateCheckFrequency` | `"weekly"` | Auto-update check cadence |
| `LastRunVersion` | `null` | Detects upgrades for wizard re-run |
| `SuppressPortableUpdatePrompt` | `false` | Suppress portable-vs-installed prompt |
