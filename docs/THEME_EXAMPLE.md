# MasselGUARD theme — complete example & instructions

A theme is a folder containing **`theme.json`** plus any image/font files it uses. This file
is a copy-paste starting point with **every field populated**, followed by an explanation of
each one. For the deeper key reference see [`THEME_INFO.md`](THEME_INFO.md); for publishing
themes to the theme repository see [`SHARED_THEMES_REPO_GUIDE.md`](SHARED_THEMES_REPO_GUIDE.md).

## How themes work

MasselGUARD has one theme that's **built in** — **System (Windows colors)**, which follows
the live Windows accent + dark/light mode and is read-only. Every other theme is a folder on
disk and comes from one of two places:

- **Downloaded** — **Settings → Appearance → Download themes…** jumps straight to the
  community theme browser (or go via **Manage themes… → Community themes** inside the Theme
  Manager — both open the same browser). It reads the repository's `index.json`, shows a card
  per theme (preview + tags + search), and installs (or redownloads/overwrites) the ones you
  pick. The default repository is <https://github.com/masselink/MasselGUARD-themes>
  (configurable under **Settings → Advanced → Theme repository**; leave it blank to use the
  default).
- **Made yourself** — the **Theme Manager** (Settings → Appearance → Manage themes…) creates
  and live-previews themes, or you can drop a folder in by hand. Closing the Manager (or the
  browser) returns you to Settings on the Appearance tab.

Each theme ships **both** a `dark` and `light` colour set; MasselGUARD shows the variant that
matches your current mode (Settings → Appearance → Light / Dark / Auto). Downloaded and
self-made themes are treated identically — **all are fully editable and deletable** in the
builder; only System is locked.

## Where themes are stored

All non-System themes — downloaded *and* self-made — live together, one folder each, in:

```
%APPDATA%\MasselGUARD\themes\<theme-id>\theme.json   (+ its images/fonts)
```

This is per-user, needs no admin rights, and survives app updates and reinstalls. The app
bundles no themes — the folder starts empty until you download or create one. (Older
`custom_themes\` / `shared-themes\` folders from earlier builds are merged in automatically on
first launch.)

To **share** a theme, copy its folder into the
[MasselGUARD-themes](https://github.com/masselink/MasselGUARD-themes) repository (or zip it via
the builder's Export) — see [`SHARED_THEMES_REPO_GUIDE.md`](SHARED_THEMES_REPO_GUIDE.md).

> JSON has no comments, so the template below is plain JSON — read the field guide under it.
> Keep all asset references (`logo`, `backgroundImage`, `appIcon`, tray icons, fonts) as
> **plain relative filenames in the same folder** — rooted/UNC paths and `..` are rejected.

## `theme.json` — full template

```json
{
  "name": "My Theme",
  "creator": "Your Name",
  "description": "One-line description shown in the picker and theme browser.",
  "appName": "MasselGUARD",
  "type": "dark",

  "fontFamily": "Segoe UI",
  "fontSize": 12,
  "cornerRadius": 6,

  "titleBarHeight": 48,
  "showTitleBarIcon": true,
  "showTitleBarAppName": true,
  "windowOpacity": 1.0,
  "panelOpacity": 1.0,

  "showStatusBar": true,
  "statusBarHeight": 38,
  "showStatusWifi": true,
  "showStatusTunnel": true,

  "backgroundImage": "",
  "backgroundStretch": "stretch",
  "backgroundOpacity": 1.0,
  "appIcon": "",
  "trayIconConnected": "",
  "trayIconDisconnected": "",
  "logo": "",
  "logoWidth": 28,
  "logoHeight": 28,

  "variables": {},

  "dark": {
    "colorWindowBg":        "#1A1A1A",
    "colorSurface":         "#242424",
    "colorCard":            "#2E2E2E",
    "colorBorder":          "#3C3C3C",
    "colorAccent":          "#6CA0DC",
    "colorSuccess":         "#4CAF6F",
    "colorDanger":          "#D9604A",
    "colorTextPrimary":     "#E0E0E0",
    "colorTextMuted":       "#888888",
    "colorHighlight":       "#802A4A7A",
    "colorError":           "#D9604A",
    "colorErrorBg":         "#3B1C18",
    "colorWarning":         "#C09020",
    "colorWarningBg":       "#3A2A08",
    "colorListHover":       "#A0303030",
    "colorListSelected":    "#1E3355",
    "colorLogTimestamp":    "#777777",
    "colorTrayBg":          "#242424",
    "colorTrayHover":       "#A0303030",
    "colorTrayText":        "#E0E0E0",
    "colorTrayBorder":      "#3C3C3C",
    "colorTrayImageMargin": "#242424",
    "logo":                 "logo-dark.png",
    "appIcon":              "icon-dark.ico",
    "backgroundImage":      "bg-dark.jpg",
    "trayIconConnected":    "tray-connected-dark.ico",
    "trayIconDisconnected": "tray-disconnected-dark.ico"
  },

  "light": {
    "colorWindowBg":        "#F0F0F0",
    "colorSurface":         "#E4E4E4",
    "colorCard":            "#D8D8D8",
    "colorBorder":          "#BBBBBB",
    "colorAccent":          "#2060B0",
    "colorSuccess":         "#2E7D42",
    "colorDanger":          "#B03030",
    "colorTextPrimary":     "#1C1C1C",
    "colorTextMuted":       "#606060",
    "colorHighlight":       "#80C8DEFF",
    "colorError":           "#B03030",
    "colorErrorBg":         "#FADDDD",
    "colorWarning":         "#7A5500",
    "colorWarningBg":       "#FFF0C0",
    "colorListHover":       "#A0DCDCDC",
    "colorListSelected":    "#BFCFE8",
    "colorLogTimestamp":    "#707070",
    "colorTrayBg":          "#E4E4E4",
    "colorTrayHover":       "#A0DCDCDC",
    "colorTrayText":        "#1C1C1C",
    "colorTrayBorder":      "#BBBBBB",
    "colorTrayImageMargin": "#E4E4E4",
    "logo":                 "logo-light.png",
    "appIcon":              "icon-light.ico",
    "backgroundImage":      "bg-light.jpg",
    "trayIconConnected":    "tray-connected-light.ico",
    "trayIconDisconnected": "tray-disconnected-light.ico"
  }
}
```

## Root fields (shared by both variants)

| Field | Type | Meaning |
|---|---|---|
| `name` | string | Display name in the picker / browser. |
| `creator` | string | Author credit. |
| `description` | string | One-line summary. |
| `appName` | string | Name shown in the title bar and toast notifications (usually `MasselGUARD`). |
| `type` | `"dark"` / `"light"` | Default variant for a legacy single-variant theme; with both `dark`+`light` present it just hints the starting variant. |
| `fontFamily` | string | Font **family name** (e.g. `Segoe UI`). If you ship a `.ttf`/`.otf` in the folder, use its family name and the app loads it as a private font (no install needed). |
| `fontSize` | number | Base point size (8–18). |
| `cornerRadius` | number | Corner rounding in px (0 = sharp). |
| `titleBarHeight` | number | Title-bar height (min 32). |
| `showTitleBarIcon` / `showTitleBarAppName` | bool | Title-bar element visibility. |
| `windowOpacity` | number | Whole-window opacity, 0.1–1.0 (the desktop shows through below 1). |
| `panelOpacity` | number | Panel (Surface/Card) opacity, 0.2–1.0 — lets the window background show through panels (used by the Glass theme). |
| `showStatusBar` / `statusBarHeight` / `showStatusWifi` / `showStatusTunnel` | bool / number | Footer status-bar settings. |
| `backgroundImage` | filename | Legacy shared fallback for the background image — leave empty and use the per-variant `backgroundImage` below instead. |
| `backgroundStretch` | `stretch` / `fill` / `center` / `tile` / `topleft` | How the background image is laid out (shared by both variants — it's layout, not colour). |
| `backgroundOpacity` | number | Background-image opacity 0–1 (shared by both variants). |
| `appIcon` | filename | Legacy shared fallback for the taskbar/tray icon (`.ico`/`.png`) — leave empty and use the per-variant `appIcon` below instead. |
| `trayIconConnected` / `trayIconDisconnected` | filename | Legacy shared fallback for state-specific tray icons — use the per-variant versions below instead. |
| `logo` | filename | Legacy shared fallback for the title-bar logo — use the per-variant `logo` below instead. |
| `logoWidth` / `logoHeight` | number | Logo size in px (shared by both variants). |
| `variables` | object | Optional `{ "key": "value" }` custom resources (`Var.key`). Leave `{}` if unused. |

## Colour keys (set the same set in both `dark` and `light`)

`#RRGGBB` or `#AARRGGBB` (alpha — the leading byte). All 21 keys: `colorWindowBg`,
`colorSurface`, `colorCard`, `colorBorder`, `colorAccent`, `colorSuccess`, `colorDanger`,
`colorTextPrimary`, `colorTextMuted`, `colorHighlight`, `colorError`, `colorErrorBg`,
`colorWarning`, `colorWarningBg`, `colorListHover`, `colorListSelected`, `colorLogTimestamp`,
`colorTrayBg`, `colorTrayHover`, `colorTrayText`, `colorTrayBorder`, `colorTrayImageMargin`.

- The timeline derives its tunnel/SSID segment colours from `colorAccent` automatically — you
  do not set those.
- Either variant may be omitted entirely; the app auto-generates the missing one by HSL
  lightness inversion. **Shipping both is recommended** for predictable results.
- Tray colours fall back to main colours when blank (`colorTrayBg`→`colorSurface`,
  `colorTrayHover`→`colorBorder`, `colorTrayText`→`colorTextPrimary`,
  `colorTrayBorder`→`colorBorder`), but populating them explicitly is clearest.
- `colorListHover`, `colorTrayHover` and `colorHighlight` are the three the builder gives
  dedicated transparency sliders (Transparency section) — the template above ships them with
  an explicit alpha byte (e.g. `#A0303030`) as an example; a plain `#RRGGBB` is just as valid
  (opaque).

## Per-variant assets

Every image asset — `logo`, `appIcon`, `backgroundImage`, `trayIconConnected`,
`trayIconDisconnected` — is **dark/light sensitive by default**: the Theme Builder shows two
independent pickers per asset (Light and Dark) and always writes both into the `dark` / `light`
sections, as in the template above. A variant's own value wins; the root-level field of the
same name is only a **legacy fallback** for hand-written themes that ship a single shared
image (or for a variant that never got its own picture) — set the root field, or one variant,
and leave the rest empty to fall back to it. `backgroundStretch`, `backgroundOpacity` and the
logo's `logoWidth`/`logoHeight` stay shared — they're layout numbers, not images.

## Try it

Drop your folder into `%APPDATA%\MasselGUARD\themes\<my-theme>\` (editable) and pick it
in **Settings → Appearance**, or build it visually with the **Theme Builder** (which writes
this exact format). The `blueongrey`, `highcontrast` and `glass` themes in the
[MasselGUARD-themes](https://github.com/masselink/MasselGUARD-themes) repo are complete, real
examples to copy from.
