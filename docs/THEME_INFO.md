# MasselGUARD — Theme Reference

Each theme lives in its own subfolder under `theme\` and contains a single `theme.json` file. One file covers both dark and light mode — the app picks the right colour set based on the current system mode setting.

The default theme is **System (Windows colors)**, which reads the active Windows accent colour and generates a matching palette automatically. No theme file is needed for this mode.

---

## Shared themes (downloadable)

The app bundles no themes. These are published in the
[MasselGUARD-themes](https://github.com/masselink/MasselGUARD-themes) repo and installed via
**Settings → Appearance → Browse shared themes…** into `%APPDATA%\MasselGUARD\themes\`.

| Folder | Display name | Corner radius | Notes |
|---|---|---|---|
| `blueongrey\` | Blue on grey | 0 px (sharp) | Flat grey with blue accent, utilitarian |
| `highcontrast\` | High Contrast | 2 px | Black/white with strong, vivid accents for low-vision use |
| `glass\` | Glass | 10 px | Translucent window + see-through panels |

---

## Virtual themes

These are not backed by a folder — they are built into the app and always available.

| Name | Description |
|---|---|
| **System (Windows colors)** | Reads Windows accent colour and system dark/light mode. Updates live when you change Windows settings. This is the default. |

---

## Custom themes

Place a folder in `%APPDATA%\MasselGUARD\themes\` containing a `theme.json`. The app picks it up immediately — no restart needed. Select it in **Settings → Appearance**.

> All non-System themes — yours and downloaded ones — share this `themes\` folder, and all are
> editable in the builder. Older `custom_themes\` / `shared-themes\` folders are merged into it
> automatically on first launch. Only the System theme is read-only.

---

## Theme file format

A theme file has two sections:

- **Root level** — structural settings shared between dark and light (font, corner radius, window chrome), plus legacy shared-fallback slots for image assets.
- **`"dark"`** — colour fields for dark mode, plus that variant's own image assets (logo, app icon, background image, tray icons).
- **`"light"`** — colour fields for light mode, plus that variant's own image assets.

Either the `dark` or the `light` section (or both) can be omitted. When one is missing the app **auto-generates** the missing side at load time using HSL lightness inversion. Nothing is written to disk.

### Minimal valid theme (dark only — light is auto-generated)

```json
{
  "name": "My Dark Theme",
  "dark": {
    "colorWindowBg": "#1a1a2e",
    "colorAccent":   "#7c3aed"
  }
}
```

### Full dual-variant example

```json
{
  "name": "My Theme",
  "creator": "Your Name",
  "description": "A short description shown in the theme list.",
  "fontFamily": "Segoe UI",
  "fontSize": 12,
  "cornerRadius": 6,

  "dark": {
    "colorWindowBg":    "#0E1117",
    "colorSurface":     "#161B22",
    "colorCard":        "#1C2128",
    "colorBorder":      "#21262D",
    "colorAccent":      "#58A6FF",
    "colorSuccess":     "#3FB950",
    "colorDanger":      "#F78166",
    "colorTextPrimary": "#E6EDF3",
    "colorTextMuted":   "#8B949E"
  },

  "light": {
    "colorWindowBg":    "#F6F8FA",
    "colorSurface":     "#FFFFFF",
    "colorCard":        "#EAEEF2",
    "colorBorder":      "#D0D7DE",
    "colorAccent":      "#0969DA",
    "colorSuccess":     "#1A7F37",
    "colorDanger":      "#CF222E",
    "colorTextPrimary": "#1F2328",
    "colorTextMuted":   "#636C76"
  }
}
```

---

## Theme fallback

If a theme folder or file cannot be found at startup, the app falls back to **System (Windows colors)** using the current system dark/light mode. No error is shown.

---

## Key reference

### Identity & metadata

| Key | Type | Description |
|---|---|---|
| `name` | string | Display name shown in the theme picker |
| `creator` | string | Your name or handle |
| `description` | string | Short description. Max ~150 chars |
| `appName` | string | Text in the title bar and tray tooltip. Lets you white-label the app |

### Typography *(root level — applies to both variants)*

| Key | Type | Default | Description |
|---|---|---|---|
| `fontFamily` | string | `"Segoe UI"` | Any font installed on the system — e.g. `"Consolas"`, `"Inter"` |
| `fontSize` | number | `12` | Base font size in points. Recommended 11–14 |

### Shape *(root level)*

| Key | Type | Default | Description |
|---|---|---|---|
| `cornerRadius` | int | `6` | Radius for all window and card corners in px. `0` = sharp |

### Colour palette *(inside `"dark"` or `"light"` section)*

All colour values accept `#RRGGBB` (opaque), `#AARRGGBB` (with transparency), or named colours such as `"Transparent"`.  
Omitting a field falls back to the Windows system palette colour for that slot.

| Key | Description |
|---|---|
| `colorWindowBg` | Main window and dialog background |
| `colorSurface` | Title bar, footer, sidebar, button bars |
| `colorCard` | Content cards, list backgrounds, input fields |
| `colorBorder` | All borders and dividers |
| `colorAccent` | Links, headings, active/selected highlights. Also used for window border |
| `colorSuccess` | Connected tunnel status, Save / Add / Finish buttons |
| `colorDanger` | Destructive action buttons, unavailable tunnel text, close button |
| `colorTextPrimary` | Primary readable text |
| `colorTextMuted` | Labels, hints, section headers, secondary info |
| `colorHighlight` | Button hover background and selected list row fill |
| `colorError` | Error banner text and border colour |
| `colorErrorBg` | Error banner background fill |
| `colorWarning` | Warning panel text and border |
| `colorWarningBg` | Warning panel background |
| `colorListHover` | List row background on mouse hover |
| `colorListSelected` | List row background when selected / active |
| `colorLogTimestamp` | Timestamp text in the activity log. Defaults to `colorBorder` if omitted |

`colorListHover`, `colorTrayHover` and `colorHighlight` are the three keys the Theme Builder
gives dedicated transparency sliders (**Transparency** section) — they're the fields most
often used with an alpha byte (e.g. `#A0303030`) since they overlay content rather than
filling an opaque area. A plain `#RRGGBB` works too (fully opaque).

### Tray context menu *(inside `"dark"` or `"light"` section)*

Leave any of these empty to inherit the corresponding semantic colour.

| Key | Empty inherits | Description |
|---|---|---|
| `colorTrayBg` | `colorSurface` | Menu background |
| `colorTrayHover` | `colorBorder` | Item hover / selected background |
| `colorTrayText` | `colorTextPrimary` | Item text colour |
| `colorTrayBorder` | `colorBorder` | Menu border and separator line |
| `colorTrayImageMargin` | `colorWindowBg` | Left image-margin column background |

### Window chrome *(root level)*

| Key | Type | Default | Description |
|---|---|---|---|
| `titleBarHeight` | int | `48` | Title bar row height in px. Minimum 32 |
| `showTitleBarIcon` | bool | `true` | Show / hide the logo or shield icon group |
| `showTitleBarAppName` | bool | `true` | Show / hide the application name text |
| `showResizeGrip` | bool | `true` | Show / hide the bottom-right resize handle |
| `windowOpacity` | number | `1.0` | Overall window opacity. `1.0` = fully opaque, `0.1` = nearly transparent |

### Status bar *(root level)*

| Key | Type | Default | Description |
|---|---|---|---|
| `showStatusBar` | bool | `true` | Show / hide the entire status bar |
| `statusBarHeight` | int | `38` | Status bar row height in px. Minimum 24 |
| `showStatusWifi` | bool | `true` | Show / hide the WiFi network label |
| `showStatusTunnel` | bool | `true` | Show / hide the active tunnel label |

### Background image *(per-variant, inside `"dark"` / `"light"` — root is a legacy fallback)*

| Key | Type | Default | Description |
|---|---|---|---|
| `backgroundImage` | string | `""` | Filename of an image in **this theme folder** — e.g. `"bg-dark.jpg"`. Leave empty for none |
| `backgroundStretch` | string | `"stretch"` | `"stretch"` (fill window) · `"center"` · `"tile"` · `"topLeft"`. Root level — shared by both variants, it's layout, not an image |
| `backgroundOpacity` | number | `1.0` | `0.0` (invisible) to `1.0` (fully opaque). Root level — shared by both variants |

The Theme Builder shows separate Light and Dark background-image pickers and writes each into
its own `"dark"` / `"light"` section, matching the colour split. The root-level `backgroundImage`
field still works as a shared fallback for hand-written themes that ship one image for both
modes — set it (and leave both variants empty) to use it everywhere, or set a variant's own
`backgroundImage` to override just that mode.

### Custom icon, logo and tray icons *(per-variant, inside `"dark"` / `"light"` — root is a legacy fallback)*

| Key | Type | Description |
|---|---|---|
| `appIcon` | string | Filename of a custom tray + title bar icon. Supports `.ico`, `.png`, `.bmp`, `.jpg`. Leave empty for the built-in shield |
| `logo` | string | Filename of a custom logo shown in the title bar. Leave empty for the default |
| `logoWidth` | int | Logo display width in px. Default `28`. Root level — shared by both variants |
| `logoHeight` | int | Logo display height in px. Default `28`. Root level — shared by both variants |
| `trayIconConnected` | string | Filename of a custom tray icon shown while a tunnel is connected. Leave empty for the built-in icon |
| `trayIconDisconnected` | string | Filename of a custom tray icon shown while disconnected. Leave empty for the built-in icon |

Like the background image, `appIcon`, `logo`, `trayIconConnected` and `trayIconDisconnected` are
each **dark/light sensitive by default** — the Theme Builder gives every one of them two
independent pickers (Light and Dark) and writes both into the `"dark"` / `"light"` sections. The
root-level field of the same name is only a legacy shared fallback (used when a variant's own
value is empty), for hand-written themes that ship a single image for both modes.

### Advanced

| Key | Type | Description |
|---|---|---|
| `variables` | object | Free-form key/value string pairs surfaced as `Var.<key>` WPF dynamic resources for advanced XAML customisation |

---

## Auto colour generation

When a theme defines only one variant, the other is generated automatically at load time. Nothing is written to disk.

| Colour type | Strategy |
|---|---|
| Neutral (saturation < 15 %) | Full lightness invert: `L → 1 − L` in HSL |
| Background fields | Lightness inverted, clamped to 0.06 – 0.94 (avoids pure black/white) |
| Chromatic (accents, status, danger) | Lightness inverted, clamped to 0.30 – 0.75 (stays readable on new background) |

The result is a reasonable starting point — hand-tuning the generated side gives the best results, but auto-generation is good enough for most cases.

---

## Tips

**Layering in dark mode** — Start from `colorWindowBg` (darkest) and work upward: `colorSurface` slightly lighter, `colorCard` slightly lighter still. Keep `colorBorder` subtle — just enough to separate panels.

**Layering in light mode** — Reverse the layering: `colorWindowBg` is the lightest, `colorCard` is slightly darker. Use a very light tint of your accent for `colorListSelected` and `colorHighlight`.

**Sharp corners** — Set `cornerRadius` to `0`. See the Blue on grey theme for an example.

**Window border** — The `colorAccent` value is used for the border of all pop-up dialogs and windows, giving them a coloured outline that matches your accent.

**Testing** — Switch themes in Settings → Appearance while the app is running. The `theme.json` is re-read on every switch, so you can edit and re-apply without restarting.

**Background images** — Lower `backgroundOpacity` significantly (e.g. `0.08`–`0.15`) so the image adds texture without making text hard to read. Place the file in the same folder as `theme.json`.

**Single-variant workflow** — Define only the `"dark"` section. Switch the app to light mode to see the auto-generated result. If it looks good, ship the file as-is. If it needs tweaks, copy the generated colours into a `"light"` section and adjust.
