# MasselGUARD — Theme Reference

This folder contains the built-in themes for MasselGUARD. Each theme lives in its own subfolder with a `theme.json` file. You can add custom themes by creating a new folder here.

---

## Built-in themes

| Folder | Display name | Type | Corner radius |
|---|---|---|---|
| `default-dark/` | Default Dark | dark | 6 px |
| `default-light/` | Default Light | light | 6 px |
| `grey-dark/` | Grey Dark | dark | 0 px (sharp) |
| `grey-light/` | Grey Light | light | 0 px (sharp) |

---

## Creating a custom theme

1. Create a new folder here — e.g. `theme/my-theme/`
2. Add a `theme.json` with at minimum `name` and `type`
3. MasselGUARD picks up the new theme immediately — no restart needed
4. Select it in **Settings → Appearance**

The minimal valid `theme.json`:

```json
{
  "name": "My Theme",
  "type": "dark"
}
```

Any key you omit falls back to the built-in default.

---

## Full key reference

### Identity

| Key | Type | Description |
|---|---|---|
| `name` | string | Display name shown in the theme picker |
| `type` | `"dark"` / `"light"` | Used for auto system-theme switching |
| `creator` | string | Your name or handle — shown in the info panel |
| `description` | string | Short description below the picker. Max ~150 chars |
| `appName` | string | Text in the title bar and tray tooltip. Lets you white-label the app |

### Typography

| Key | Type | Default | Description |
|---|---|---|---|
| `fontFamily` | string | `"Segoe UI"` | Any font installed on the system — e.g. `"Consolas"`, `"Inter"` |
| `fontSize` | number | `12` | Base font size in points. Recommended 11–14 |

### Shape

| Key | Type | Default | Description |
|---|---|---|---|
| `cornerRadius` | int | `6` | Radius for all window and card corners in px. `0` = sharp |

### Colour palette

All colour values accept `#RRGGBB` (opaque), `#AARRGGBB` (with transparency), or named colours like `"Transparent"`.

| Key | Description |
|---|---|
| `colorWindowBg` | Main window and dialog background. The darkest layer |
| `colorSurface` | Title bar, footer, sidebar, button bars — one step lighter than the window background |
| `colorCard` | Content cards, list backgrounds, input fields — slightly lighter than surface |
| `colorBorder` | All borders and dividers. Keep low contrast for a subtle look |
| `colorAccent` | Links, headings, active/selected highlights. Your brand colour |
| `colorSuccess` | Connected tunnel status, Save / Add / Finish buttons |
| `colorDanger` | Destructive action buttons, unavailable tunnel text, close button |
| `colorTextPrimary` | Primary readable text — high contrast against `colorWindowBg` |
| `colorTextMuted` | Labels, hints, section headers, secondary info — lower contrast |
| `colorHighlight` | Button hover background and selected list row fill |
| `colorError` | Error banner text and border colour |
| `colorErrorBg` | Error banner background fill. Use a very dark tint of `colorError` |
| `colorWarning` | Warning panel text and border |
| `colorWarningBg` | Warning panel background. Use a very dark tint of `colorWarning` |
| `colorListHover` | List row background on mouse hover |
| `colorListSelected` | List row background when selected / active |
| `colorLogTimestamp` | Timestamp text colour in the activity log. Defaults to `colorBorder` if omitted |

### Window chrome

| Key | Type | Default | Description |
|---|---|---|---|
| `titleBarHeight` | int | `48` | Title bar row height in px. Minimum 32 |
| `showTitleBarIcon` | bool | `true` | Show / hide the logo or shield icon group |
| `showTitleBarAppName` | bool | `true` | Show / hide the application name text |
| `showResizeGrip` | bool | `true` | Show / hide the bottom-right resize handle |
| `windowOpacity` | number | `1.0` | Overall window opacity. `1.0` = fully opaque, `0.1` = nearly transparent |

### Status bar

| Key | Type | Default | Description |
|---|---|---|---|
| `showStatusBar` | bool | `true` | Show / hide the entire status bar |
| `statusBarHeight` | int | `38` | Status bar row height in px. Minimum 24 |
| `showStatusWifi` | bool | `true` | Show / hide the WiFi network label |
| `showStatusTunnel` | bool | `true` | Show / hide the active tunnel label |

### Tray context menu

These keys control the Windows Forms tray menu. Leave any of them empty to inherit the corresponding semantic colour.

| Key | Empty inherits | Description |
|---|---|---|
| `colorTrayBg` | `colorSurface` | Menu background |
| `colorTrayHover` | `colorBorder` | Item hover / selected background |
| `colorTrayText` | `colorTextPrimary` | Item text colour |
| `colorTrayBorder` | `colorBorder` | Menu border and separator line |
| `colorTrayImageMargin` | `colorWindowBg` | Left image-margin column background |

### Background image

| Key | Type | Default | Description |
|---|---|---|---|
| `backgroundImage` | string | `""` | Filename of an image in **this theme folder** — e.g. `"bg.png"`. Leave empty for none |
| `backgroundStretch` | string | `"stretch"` | `"stretch"` (fill window) · `"center"` · `"tile"` · `"topLeft"` |
| `backgroundOpacity` | number | `1.0` | `0.0` (invisible) to `1.0` (fully opaque) |

### Custom icon and logo

| Key | Type | Description |
|---|---|---|
| `appIcon` | string | Filename of a custom tray + title bar icon. Supports `.ico`, `.png`, `.bmp`, `.jpg`. Leave empty to use the built-in shield |
| `logo` | string | Filename of a custom logo shown in the title bar (replaces the built-in shield). Leave empty for the default |
| `logoWidth` | int | Logo display width in px. Default `28` |
| `logoHeight` | int | Logo display height in px. Default `28` |

### Advanced

| Key | Type | Description |
|---|---|---|
| `variables` | object | Free-form key/value string pairs surfaced as `Var.<key>` WPF dynamic resources for advanced XAML customisation |

---

## Tips

**Dark theme colours** — Start from `colorWindowBg` (darkest) and work upward: `colorSurface` slightly lighter, `colorCard` slightly lighter still. Keep `colorBorder` subtle — just enough to separate panels.

**Light theme colours** — Reverse the layering: `colorWindowBg` is the lightest, `colorCard` is slightly darker. Use a very light tint of your accent colour for `colorListSelected` and `colorHighlight`.

**Sharp corners** — Set `cornerRadius` to `0`. See `grey-dark` and `grey-light` for examples.

**Transparent tray** — If you set a semi-transparent `colorTrayBg`, Windows will composite the menu over whatever is behind it. This works on Windows 11 with DWM compositing enabled.

**Background images** — Lower `backgroundOpacity` significantly (e.g. `0.08`–`0.15`) so the image adds texture without making text hard to read. Place the file in the same folder as `theme.json`.

**Testing** — Switch themes in Settings → Appearance while the app is running. The `theme.json` is re-read on every switch, so you can edit and re-apply without restarting.
