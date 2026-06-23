using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MasselGUARD.Models;

namespace MasselGUARD
{
    /// <summary>
    /// Loads and applies themes from  &lt;ExeDir&gt;\theme\&lt;name&gt;\theme.json.
    ///
    /// ── Colour format ────────────────────────────────────────────────────────
    /// All colour values accept:
    ///   "#RRGGBB"        — fully opaque  (e.g. "#1F2328")
    ///   "#AARRGGBB"      — with alpha    (e.g. "#CC1F2328" = 80% opaque)
    ///   "#RGB" / "#ARGB" — shorthand
    ///   Named colours    — "Transparent", "White", "Black", etc.
    ///
    /// ── theme.json colour keys — named by where the colour is used ───────────
    ///   colorWindowBg      main window and dialog background
    ///   colorSurface       title bar, footer, sidebar, button bars
    ///   colorCard          content cards, list backgrounds, input fields
    ///   colorBorder        all borders and dividers
    ///   colorAccent        links, headings, primary active highlight
    ///   colorSuccess       connected status, save/add/finish buttons
    ///   colorDanger        destructive buttons, unavailable / error state
    ///   colorTextPrimary   primary readable text
    ///   colorTextMuted     labels, hints, section headers, secondary info
    ///   colorHighlight     button hover background, selected list row
    ///   colorError         error banner text and border
    ///   colorErrorBg       error banner background
    ///   colorWarning       warning text and border (orphan panel)
    ///   colorWarningBg     warning banner background
    ///   colorListHover     list row hover background
    ///   colorListSelected  list row selected background
    ///
    /// ── Background image ─────────────────────────────────────────────────────
    ///   "backgroundImage"   : "bg.png"        file in the theme folder
    ///   "backgroundStretch" : "stretch"        "stretch" | "center" | "tile" | "topLeft"
    ///   "backgroundOpacity" : 0.18             0.0 – 1.0
    ///
    /// ── App icon ─────────────────────────────────────────────────────────────
    ///   "appIcon" : "icon.png"   replaces the tray icon and the title-bar icon
    ///                            Supported: .ico  .png  .bmp  .jpg  .jpeg
    ///
    /// ── Logo (title bar only) ────────────────────────────────────────────────
    ///   "logo"       : "logo.png"
    ///   "logoWidth"  : 28
    ///   "logoHeight" : 28
    /// </summary>
    public sealed class ThemeManager
    {
        public static ThemeManager Instance { get; } = new ThemeManager();
        private ThemeManager() { }

        public event EventHandler? ThemeChanged;

        public string          CurrentThemeName { get; private set; } = "__system__";
        public ThemeDefinition Current          { get; private set; } = ThemeDefinition.Default;

        // ── Paths ─────────────────────────────────────────────────────────────
        /// <summary>
        /// Shipped / downloaded themes that live next to the exe (the install dir, i.e.
        /// %ProgramFiles%\MasselGUARD\shared_themes\). Read-only in the builder. The only
        /// theme built into the binary is the virtual System (Windows colours) theme.
        /// </summary>
        private static string ThemeRoot =>
            Path.Combine(
                Path.GetDirectoryName(
                    System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                    ?? AppContext.BaseDirectory)
                ?? AppContext.BaseDirectory,
                "shared_themes");

        /// <summary>Public path to the shipped/downloaded shared-themes folder next to the
        /// exe — used by the "Download shared themes" feature to install fetched themes.</summary>
        public static string SharedThemeRoot => ThemeRoot;

        /// <summary>
        /// User-created themes live here so they survive app reinstalls.
        /// %APPDATA%\MasselGUARD\custom_themes\
        /// </summary>
        public static string UserThemeRoot =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MasselGUARD", "custom_themes");

        /// <summary>Pre-3.7 location of user themes (%APPDATA%\MasselGUARD\themes\), kept
        /// only so <see cref="MigrateLegacyUserThemes"/> can move them to the new folder.</summary>
        private static string LegacyUserThemeRoot =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MasselGUARD", "themes");

        /// <summary>
        /// One-time move of user themes from the old <c>themes\</c> folder to
        /// <c>custom_themes\</c>. Moves the whole folder when the new one doesn't exist
        /// yet; otherwise merges per-theme (skipping names that already exist in the new
        /// location) and removes the old folder once empty. Safe to call on every startup.
        /// </summary>
        public static void MigrateLegacyUserThemes()
        {
            try
            {
                var legacy = LegacyUserThemeRoot;
                var target = UserThemeRoot;
                if (!Directory.Exists(legacy)) return;

                if (!Directory.Exists(target))
                {
                    Directory.Move(legacy, target);
                    return;
                }

                foreach (var dir in Directory.GetDirectories(legacy))
                {
                    var dest = Path.Combine(target, Path.GetFileName(dir));
                    if (!Directory.Exists(dest)) Directory.Move(dir, dest);
                }
                // Drop the old folder if nothing is left behind (collisions stay put).
                if (!Directory.EnumerateFileSystemEntries(legacy).Any())
                    Directory.Delete(legacy);
            }
            catch { /* best-effort migration — never block startup over it */ }
        }

        /// <summary>
        /// Folder names of the shared themes that live in the install's
        /// <c>shared_themes\</c> folder (shipped + downloaded), discovered from disk. The
        /// app embeds NO theme names — adding/removing a shared theme is just a folder
        /// there. Shared themes are editable/deletable in the builder.
        /// </summary>
        public static List<string> SharedThemeNames()
        {
            var root = ThemeRoot;
            if (!Directory.Exists(root)) return new List<string>();
            return Directory.GetDirectories(root)
                .Select(d => Path.GetFileName(d)!)
                .Where(n => File.Exists(Path.Combine(root, n, "theme.json")))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>True only for the virtual System (Windows colours) theme — the single
        /// theme embedded in code, which is read-only (cannot be edited or deleted). Shared
        /// and custom themes are editable.</summary>
        public static bool IsBuiltinTheme(string name) =>
            name is "__system__" or "system";

        /// <summary>True when a theme lives in the install's <c>shared_themes\</c> folder.</summary>
        public static bool IsSharedTheme(string name) =>
            !string.IsNullOrEmpty(name) &&
            File.Exists(Path.Combine(ThemeRoot, name, "theme.json"));

        /// <summary>
        /// Returns the folder for a theme, checking user themes first so custom themes
        /// can shadow built-ins by name (though this is unlikely in practice).
        /// </summary>
        public static string ThemeFolder(string name)
        {
            var user = Path.Combine(UserThemeRoot, name);
            if (Directory.Exists(user)) return user;
            return Path.Combine(ThemeRoot, name);
        }

        private static string ThemeJson(string name) => Path.Combine(ThemeFolder(name), "theme.json");

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all available theme folder names that contain a theme.json,
        /// plus the virtual "__system__" entry (Windows accent colours).
        /// </summary>
        public static List<string> AvailableThemes()
        {
            var themes = new List<string> { "__system__" };   // virtual — Windows system colours

            // Built-in themes from exe folder
            var root = ThemeRoot;
            if (Directory.Exists(root))
                themes.AddRange(Directory.GetDirectories(root)
                    .Select(d => Path.GetFileName(d)!)
                    .Where(n => File.Exists(Path.Combine(root, n, "theme.json")))
                    .OrderBy(n => n));

            // User-created themes from AppData (skip duplicates)
            var userRoot = UserThemeRoot;
            if (Directory.Exists(userRoot))
                foreach (var dir in Directory.GetDirectories(userRoot).OrderBy(d => d))
                {
                    var name = Path.GetFileName(dir)!;
                    if (!themes.Contains(name) &&
                        File.Exists(Path.Combine(userRoot, name, "theme.json")))
                        themes.Add(name);
                }

            return themes;
        }

        /// <summary>
        /// Reads just the display name from a theme's JSON without fully loading it.
        /// Returns the folder name as fallback if name is missing or file unreadable.
        /// </summary>
        public static string GetThemeDisplayName(string folderName)
        {
            if (folderName is "__system__" or "system")
                return "System (Windows colors)";
            try
            {
                var json = ThemeJson(folderName);
                if (!File.Exists(json)) return folderName;
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var def  = JsonSerializer.Deserialize<ThemeDefinition>(File.ReadAllText(json), opts);
                return string.IsNullOrWhiteSpace(def?.Name) ? folderName : def.Name;
            }
            catch { return folderName; }
        }

        /// <summary>
        /// Loads a theme by folder name and applies it live to Application.Resources.
        /// <paramref name="isDark"/> controls which variant block is merged when the
        /// JSON contains a "dark" or "light" section.
        /// </summary>
        public void Load(string themeName, bool isDark)
        {
            if (themeName is "__system__" or "system") { LoadSystem(isDark); return; }
            var def = ReadTheme(themeName);
            if (def == null) { LoadSystem(isDark); return; }
            ApplyTheme(themeName, def, isDark);
        }

        /// <summary>
        /// Backward-compatible overload: derives dark/light from the theme's own
        /// <c>Type</c> field.  Prefer <see cref="Load(string,bool)"/> when the
        /// caller already knows which mode is active.
        /// </summary>
        public void Load(string themeName)
        {
            if (themeName is "__system__" or "system") { LoadSystem(GetSystemIsDark()); return; }
            var def = ReadTheme(themeName);
            if (def == null) { LoadSystem(GetSystemIsDark()); return; }
            bool isDark = !def.Type.Equals("light", StringComparison.OrdinalIgnoreCase);
            ApplyTheme(themeName, def, isDark);
        }

        // ── Internal helpers ──────────────────────────────────────────────────

        private static ThemeDefinition? ReadTheme(string themeName)
        {
            var jsonPath = ThemeJson(themeName);
            if (File.Exists(jsonPath))
            {
                try
                {
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    return JsonSerializer.Deserialize<ThemeDefinition>(File.ReadAllText(jsonPath), opts);
                }
                catch { }
            }
            return null;
        }

        private void ApplyTheme(string themeName, ThemeDefinition def, bool isDark)
        {
            ThemeDefinition resolved;
            var variant = isDark ? def.Dark : def.Light;

            if (variant != null)
            {
                // Explicit variant defined — use it.
                resolved = MergeVariant(def, variant);
            }
            else
            {
                // No explicit variant for this mode — try the other side and invert it,
                // or fall back to root (backward compat: old single-file themes with
                // colours at root level and no dark/light sections).
                var otherVariant = isDark ? def.Light : def.Dark;
                if (otherVariant != null)
                {
                    var otherResolved = MergeVariant(def, otherVariant);
                    resolved = AutoInvertVariant(otherResolved);
                }
                else
                {
                    resolved = def;   // legacy single-variant file; system palette fills blanks
                }
            }

            CurrentThemeName = themeName;
            Current          = resolved;
            Apply(resolved, ThemeFolder(themeName), isDark);
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Returns a new ThemeDefinition that uses <paramref name="root"/> for all
        /// structural settings (font, chrome, layout) and overlays non-empty color
        /// strings from <paramref name="variant"/>.
        /// </summary>
        private static ThemeDefinition MergeVariant(ThemeDefinition root, ThemeDefinition variant)
        {
            static string O(string v, string b) => !string.IsNullOrEmpty(v) ? v : b;
            return new ThemeDefinition
            {
                // Structural: always from root
                Name              = root.Name,
                AppName           = root.AppName,
                Type              = root.Type,
                Creator           = root.Creator,
                Description       = root.Description,
                FontFamily        = root.FontFamily,
                FontSize          = root.FontSize,
                CornerRadius      = root.CornerRadius,
                // Assets: a variant may carry its own logo / background image
                // (e.g. a light logo for the light variant); root is the shared
                // fallback. Stretch/opacity/size stay shared.
                BackgroundImage   = O(variant.BackgroundImage, root.BackgroundImage),
                BackgroundStretch = root.BackgroundStretch,
                BackgroundOpacity = root.BackgroundOpacity,
                AppIcon           = root.AppIcon,
                Logo              = O(variant.Logo, root.Logo),
                LogoWidth         = root.LogoWidth,
                LogoHeight        = root.LogoHeight,
                TrayIconConnected    = O(variant.TrayIconConnected,    root.TrayIconConnected),
                TrayIconDisconnected = O(variant.TrayIconDisconnected, root.TrayIconDisconnected),
                TitleBarHeight      = root.TitleBarHeight,
                ShowTitleBarIcon    = root.ShowTitleBarIcon,
                ShowTitleBarAppName = root.ShowTitleBarAppName,
                ShowResizeGrip      = root.ShowResizeGrip,
                WindowOpacity       = root.WindowOpacity,
                PanelOpacity        = root.PanelOpacity,
                ShowStatusBar    = root.ShowStatusBar,
                StatusBarHeight  = root.StatusBarHeight,
                ShowStatusWifi   = root.ShowStatusWifi,
                ShowStatusTunnel = root.ShowStatusTunnel,
                Variables        = root.Variables,

                // Colors: variant wins when non-empty, root is fallback
                ColorWindowBg        = O(variant.ColorWindowBg,        root.ColorWindowBg),
                ColorSurface         = O(variant.ColorSurface,         root.ColorSurface),
                ColorCard            = O(variant.ColorCard,            root.ColorCard),
                ColorBorder          = O(variant.ColorBorder,          root.ColorBorder),
                ColorAccent          = O(variant.ColorAccent,          root.ColorAccent),
                ColorSuccess         = O(variant.ColorSuccess,         root.ColorSuccess),
                ColorDanger          = O(variant.ColorDanger,          root.ColorDanger),
                ColorTextPrimary     = O(variant.ColorTextPrimary,     root.ColorTextPrimary),
                ColorTextMuted       = O(variant.ColorTextMuted,       root.ColorTextMuted),
                ColorHighlight       = O(variant.ColorHighlight,       root.ColorHighlight),
                ColorError           = O(variant.ColorError,           root.ColorError),
                ColorErrorBg         = O(variant.ColorErrorBg,         root.ColorErrorBg),
                ColorWarning         = O(variant.ColorWarning,         root.ColorWarning),
                ColorWarningBg       = O(variant.ColorWarningBg,       root.ColorWarningBg),
                ColorListHover       = O(variant.ColorListHover,       root.ColorListHover),
                ColorListSelected    = O(variant.ColorListSelected,    root.ColorListSelected),
                ColorLogTimestamp    = O(variant.ColorLogTimestamp,    root.ColorLogTimestamp),
                ColorTrayBg          = O(variant.ColorTrayBg,          root.ColorTrayBg),
                ColorTrayHover       = O(variant.ColorTrayHover,       root.ColorTrayHover),
                ColorTrayText        = O(variant.ColorTrayText,        root.ColorTrayText),
                ColorTrayBorder      = O(variant.ColorTrayBorder,      root.ColorTrayBorder),
                ColorTrayImageMargin = O(variant.ColorTrayImageMargin, root.ColorTrayImageMargin),
            };
        }

        // ── Auto-invert: generate the opposite variant by flipping HSL lightness ─
        /// <summary>
        /// Returns a copy of <paramref name="src"/> with all colour fields lightness-inverted.
        /// Used when a theme defines only one variant so the opposite mode still looks reasonable.
        /// Strategy:
        ///   • Neutrals (saturation &lt; 15 %): full invert  L → 1−L
        ///   • Chromatic colours (accents, status): invert L but clamp so the colour
        ///     stays usable (dark backgrounds keep bright accents, light backgrounds keep
        ///     saturated but readable tones).
        /// </summary>
        internal static ThemeDefinition AutoInvertVariant(ThemeDefinition src)
        {
            static string Inv(string hex, bool isBackground = false)
            {
                if (string.IsNullOrEmpty(hex)) return hex;
                if (!TryParseColor(hex, out var c)) return hex;
                RgbToHsl(c.R / 255.0, c.G / 255.0, c.B / 255.0, out double h, out double s, out double l);

                double newL;
                if (s < 0.15)
                {
                    // Neutral: straight invert
                    newL = 1.0 - l;
                }
                else
                {
                    // Chromatic: invert but keep contrast against the new background tone
                    newL = 1.0 - l;
                    if (isBackground)
                        newL = Math.Clamp(newL, 0.06, 0.94);
                    else
                        // Accent/status: ensure it's visible on both dark and light BG
                        newL = Math.Clamp(newL, 0.30, 0.75);
                }

                HslToRgb(h, s, newL, out double r2, out double g2, out double b2);
                int ri = (int)Math.Round(r2 * 255);
                int gi = (int)Math.Round(g2 * 255);
                int bi = (int)Math.Round(b2 * 255);

                // Preserve alpha if original had it
                return c.A < 255
                    ? $"#{c.A:X2}{ri:X2}{gi:X2}{bi:X2}"
                    : $"#{ri:X2}{gi:X2}{bi:X2}";
            }

            static string InvBg(string hex) => Inv(hex, isBackground: true);

            return new ThemeDefinition
            {
                // Structural: unchanged
                Name              = src.Name,
                AppName           = src.AppName,
                Type              = src.Type,
                Creator           = src.Creator,
                Description       = src.Description,
                FontFamily        = src.FontFamily,
                FontSize          = src.FontSize,
                CornerRadius      = src.CornerRadius,
                BackgroundImage   = src.BackgroundImage,
                BackgroundStretch = src.BackgroundStretch,
                BackgroundOpacity = src.BackgroundOpacity,
                AppIcon           = src.AppIcon,
                Logo              = src.Logo,
                LogoWidth         = src.LogoWidth,
                LogoHeight        = src.LogoHeight,
                TrayIconConnected    = src.TrayIconConnected,
                TrayIconDisconnected = src.TrayIconDisconnected,
                TitleBarHeight    = src.TitleBarHeight,
                ShowTitleBarIcon    = src.ShowTitleBarIcon,
                ShowTitleBarAppName = src.ShowTitleBarAppName,
                ShowResizeGrip    = src.ShowResizeGrip,
                WindowOpacity     = src.WindowOpacity,
                PanelOpacity      = src.PanelOpacity,
                ShowStatusBar     = src.ShowStatusBar,
                StatusBarHeight   = src.StatusBarHeight,
                ShowStatusWifi    = src.ShowStatusWifi,
                ShowStatusTunnel  = src.ShowStatusTunnel,
                Variables         = src.Variables,

                // Colours: inverted
                ColorWindowBg        = InvBg(src.ColorWindowBg),
                ColorSurface         = InvBg(src.ColorSurface),
                ColorCard            = InvBg(src.ColorCard),
                ColorBorder          = Inv(src.ColorBorder),
                ColorAccent          = Inv(src.ColorAccent),
                ColorSuccess         = Inv(src.ColorSuccess),
                ColorDanger          = Inv(src.ColorDanger),
                ColorTextPrimary     = Inv(src.ColorTextPrimary),
                ColorTextMuted       = Inv(src.ColorTextMuted),
                ColorHighlight       = InvBg(src.ColorHighlight),
                ColorError           = Inv(src.ColorError),
                ColorErrorBg         = InvBg(src.ColorErrorBg),
                ColorWarning         = Inv(src.ColorWarning),
                ColorWarningBg       = InvBg(src.ColorWarningBg),
                ColorListHover       = InvBg(src.ColorListHover),
                ColorListSelected    = InvBg(src.ColorListSelected),
                ColorLogTimestamp    = Inv(src.ColorLogTimestamp),
                ColorTrayBg          = InvBg(src.ColorTrayBg),
                ColorTrayHover       = InvBg(src.ColorTrayHover),
                ColorTrayText        = Inv(src.ColorTrayText),
                ColorTrayBorder      = Inv(src.ColorTrayBorder),
                ColorTrayImageMargin = InvBg(src.ColorTrayImageMargin),
            };
        }

        // ── HSL ↔ RGB helpers ─────────────────────────────────────────────────

        private static bool TryParseColor(string hex, out (int A, int R, int G, int B) result)
        {
            result = (255, 0, 0, 0);
            hex = hex.TrimStart('#').Trim();
            try
            {
                switch (hex.Length)
                {
                    case 3:  // #RGB
                        result = (255,
                            Convert.ToInt32(new string(hex[0], 2), 16),
                            Convert.ToInt32(new string(hex[1], 2), 16),
                            Convert.ToInt32(new string(hex[2], 2), 16));
                        return true;
                    case 4:  // #ARGB
                        result = (Convert.ToInt32(new string(hex[0], 2), 16),
                            Convert.ToInt32(new string(hex[1], 2), 16),
                            Convert.ToInt32(new string(hex[2], 2), 16),
                            Convert.ToInt32(new string(hex[3], 2), 16));
                        return true;
                    case 6:  // #RRGGBB
                        result = (255,
                            Convert.ToInt32(hex[..2], 16),
                            Convert.ToInt32(hex[2..4], 16),
                            Convert.ToInt32(hex[4..6], 16));
                        return true;
                    case 8:  // #AARRGGBB
                        result = (Convert.ToInt32(hex[..2], 16),
                            Convert.ToInt32(hex[2..4], 16),
                            Convert.ToInt32(hex[4..6], 16),
                            Convert.ToInt32(hex[6..8], 16));
                        return true;
                    default: return false;
                }
            }
            catch { return false; }
        }

        private static void RgbToHsl(double r, double g, double b,
            out double h, out double s, out double l)
        {
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;
            l = (max + min) / 2.0;
            if (delta < 1e-10) { h = 0; s = 0; return; }
            s = l > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);
            if      (max == r) h = ((g - b) / delta + (g < b ? 6 : 0)) / 6.0;
            else if (max == g) h = ((b - r) / delta + 2.0) / 6.0;
            else               h = ((r - g) / delta + 4.0) / 6.0;
        }

        private static void HslToRgb(double h, double s, double l,
            out double r, out double g, out double b)
        {
            if (s < 1e-10) { r = g = b = l; return; }
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3);
        }

        private static double HueToRgb(double p, double q, double t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        // ── Apply all theme values to Application.Resources ───────────────────
        private static void Apply(ThemeDefinition d, string folder, bool isDark)
            => Apply(d, folder, isDark, Application.Current.Resources);

        /// <summary>
        /// Fills <paramref name="target"/> with the plain Windows system palette.
        /// Used by the Theme Builder to pin its own window (and dialogs) to System
        /// (Windows colors): local resources shadow the application resources, so
        /// live theme edits never restyle the builder itself.
        /// </summary>
        public static void ApplySystemTo(ResourceDictionary target)
        {
            bool isDark = GetSystemIsDark();
            Apply(BuildSystemTheme(isDark), folder: "", isDark, target);
        }

        private static void Apply(ThemeDefinition d, string folder, bool isDark, ResourceDictionary res)
        {

            // ── Resolve colours: system palette as base, theme JSON as overrides ──
            // Any colour left as "" in the theme JSON (or absent from the file) falls
            // back to the live Windows system colour for that semantic slot.  This means
            // every theme automatically inherits the current dark/light palette and only
            // needs to declare the colours it deliberately wants to change.
            var sys = BuildSystemTheme(isDark);
            // R(): theme value when non-empty, system base when blank.
            string R(string val, string fb) => string.IsNullOrWhiteSpace(val) ? fb : val;

            var bg      = R(d.ColorWindowBg,     sys.ColorWindowBg);
            var surface = R(d.ColorSurface,      sys.ColorSurface);
            var card    = R(d.ColorCard,         sys.ColorCard);
            var border  = R(d.ColorBorder,       sys.ColorBorder);
            var accent  = R(d.ColorAccent,       sys.ColorAccent);
            var success = R(d.ColorSuccess,      sys.ColorSuccess);
            var danger  = R(d.ColorDanger,       sys.ColorDanger);
            var txtPri  = R(d.ColorTextPrimary,  sys.ColorTextPrimary);
            var txtMut  = R(d.ColorTextMuted,    sys.ColorTextMuted);
            var hl      = R(d.ColorHighlight,    sys.ColorHighlight);
            var error   = R(d.ColorError,        sys.ColorError);
            var errorBg = R(d.ColorErrorBg,      sys.ColorErrorBg);
            var warning = R(d.ColorWarning,      sys.ColorWarning);
            var warnBg  = R(d.ColorWarningBg,    sys.ColorWarningBg);
            var lstHov  = R(d.ColorListHover,    sys.ColorListHover);
            var lstSel  = R(d.ColorListSelected, sys.ColorListSelected);

            // C.* Color resources
            SetColor(res, "C.WindowBg",    bg);
            SetColor(res, "C.Surface",     surface);
            SetColor(res, "C.CardBg",      card);
            SetColor(res, "C.BorderColor", border);
            SetColor(res, "C.Accent",      accent);
            SetColor(res, "C.Success",     success);
            SetColor(res, "C.Danger",      danger);
            SetColor(res, "C.TextPrimary", txtPri);
            SetColor(res, "C.TextMuted",   txtMut);
            SetColor(res, "C.Highlight",   hl);

            // Brush resources. Surface/Card honour PanelOpacity so a background
            // image can shine through the panels.
            var panelOp = Math.Clamp(d.PanelOpacity, 0.2, 1.0);
            SetBrush(res, "WindowBg",     bg);
            SetBrush(res, "Surface",      surface, panelOp);
            SetBrush(res, "CardBg",       card,    panelOp);
            SetBrush(res, "BorderColor",  border);
            SetBrush(res, "Accent",       accent);
            SetBrush(res, "Success",      success);
            SetBrush(res, "Danger",       danger);
            SetBrush(res, "TextPrimary",  txtPri);
            SetBrush(res, "TextMuted",    txtMut);
            SetBrush(res, "Highlight",    hl);
            SetBrush(res, "ErrorColor",   error);
            SetBrush(res, "ErrorBg",      errorBg);
            SetBrush(res, "WarningColor", warning);
            SetBrush(res, "WarningBg",    warnBg);
            SetBrush(res, "ListHover",    lstHov);
            SetBrush(res, "ListSelected", lstSel);

            // Log timestamp colour — falls back to resolved border colour if not set
            var tsHex = !string.IsNullOrWhiteSpace(d.ColorLogTimestamp) ? d.ColorLogTimestamp : border;
            res["Theme.LogTimestampColor"] = ParseColor(tsHex, Colors.Gray);

            // Typography
            res["Theme.FontFamily"]          = ResolveFontFamily(d.FontFamily, folder);
            res["Theme.FontSize"]            = d.FontSize;
            res["Theme.FontSize.Small"]      = Math.Max(8.0, d.FontSize - 1.0);
            res["Theme.FontSize.Tiny"]       = Math.Max(7.0, d.FontSize - 2.0);
            res["Theme.CornerRadius"]        = new CornerRadius(d.CornerRadius);
            res["Theme.CornerRadiusTop"]    = new CornerRadius(d.CornerRadius, d.CornerRadius, 0, 0);
            res["Theme.CornerRadiusBottom"] = new CornerRadius(0, 0, d.CornerRadius, d.CornerRadius);

            // App name
            res["Theme.AppName"] = string.IsNullOrWhiteSpace(d.AppName) ? "MasselGUARD" : d.AppName;

            // ── Window chrome ─────────────────────────────────────────────────
            res["Theme.TitleBarHeight"]      = new GridLength(Math.Max(32, d.TitleBarHeight));
            res["Theme.ShowTitleBarIcon"]    = d.ShowTitleBarIcon    ? Visibility.Visible : Visibility.Collapsed;
            res["Theme.ShowTitleBarAppName"] = d.ShowTitleBarAppName ? Visibility.Visible : Visibility.Collapsed;
            res["Theme.ShowResizeGrip"]      = d.ShowResizeGrip      ? Visibility.Visible : Visibility.Collapsed;
            res["Theme.WindowOpacity"]       = Math.Clamp(d.WindowOpacity, 0.1, 1.0);

            // ── Status bar ────────────────────────────────────────────────────
            res["Theme.ShowStatusBar"]   = d.ShowStatusBar    ? Visibility.Visible : Visibility.Collapsed;
            res["Theme.StatusBarHeight"] = new GridLength(d.ShowStatusBar ? Math.Max(24, d.StatusBarHeight) : 0);
            res["Theme.ShowStatusWifi"]  = d.ShowStatusWifi   ? Visibility.Visible : Visibility.Collapsed;
            res["Theme.ShowStatusTunnel"]= d.ShowStatusTunnel ? Visibility.Visible : Visibility.Collapsed;

            // ── Tray menu colours — fall back to resolved semantic colours when empty ──
            string trayBg     = Fallback(d.ColorTrayBg,          surface);
            string trayHover  = Fallback(d.ColorTrayHover,       border);
            string trayText   = Fallback(d.ColorTrayText,        txtPri);
            string trayBorder = Fallback(d.ColorTrayBorder,      border);
            string trayImg    = Fallback(d.ColorTrayImageMargin, bg);
            SetBrush(res, "TrayBg",          trayBg);
            SetBrush(res, "TrayHover",       trayHover);
            SetBrush(res, "TrayText",        trayText);
            SetBrush(res, "TrayBorder",      trayBorder);
            SetBrush(res, "TrayImageMargin", trayImg);
            // Also store as Drawing.Color for WinForms tray menu renderer
            res["Theme.TrayBgColor"]          = ToDrawingColor(trayBg);
            res["Theme.TrayHoverColor"]       = ToDrawingColor(trayHover);
            res["Theme.TrayTextColor"]        = ToDrawingColor(trayText);
            res["Theme.TrayBorderColor"]      = ToDrawingColor(trayBorder);
            res["Theme.TrayImageMarginColor"] = ToDrawingColor(trayImg);

            // Background image
            ApplyBackground(res, d, folder);

            // App icon (tray + title bar)
            ApplyAppIcon(res, d, folder);

            // State-specific tray icons (connected / disconnected)
            ApplyTrayStateIcons(res, d, folder);

            // Logo (title bar only)
            ApplyLogo(res, d, folder);

            // Custom variables
            if (d.Variables != null)
                foreach (var kv in d.Variables)
                    res[$"Var.{kv.Key}"] = kv.Value;
        }

        private static void ApplyBackground(ResourceDictionary res, ThemeDefinition d, string folder)
        {
            if (string.IsNullOrWhiteSpace(d.BackgroundImage))
            {
                res["Theme.BackgroundBrush"] = new SolidColorBrush(Colors.Transparent);
                res["Theme.HasBackground"]   = Visibility.Collapsed;
                return;
            }

            var imgPath = ResolveThemeAsset(folder, d.BackgroundImage);
            if (imgPath == null || !File.Exists(imgPath))
            {
                res["Theme.BackgroundBrush"] = new SolidColorBrush(Colors.Transparent);
                res["Theme.HasBackground"]   = Visibility.Collapsed;
                return;
            }

            try
            {
                var bmp = LoadImageUncached(imgPath);

                // Map backgroundStretch string to WPF Stretch + AlignmentX/Y
                var (stretch, alignX, alignY, tile) = ParseStretchMode(d.BackgroundStretch);

                var brush = new ImageBrush(bmp)
                {
                    Stretch              = stretch,
                    AlignmentX           = alignX,
                    AlignmentY           = alignY,
                    TileMode             = tile,
                    Opacity              = d.BackgroundOpacity,
                    ViewportUnits        = BrushMappingMode.RelativeToBoundingBox,
                    Viewport             = new Rect(0, 0, 1, 1)
                };

                res["Theme.BackgroundBrush"] = brush;
                res["Theme.HasBackground"]   = Visibility.Visible;
            }
            catch
            {
                res["Theme.BackgroundBrush"] = new SolidColorBrush(Colors.Transparent);
                res["Theme.HasBackground"]   = Visibility.Collapsed;
            }
        }

        private static (Stretch stretch, AlignmentX x, AlignmentY y, TileMode tile)
            ParseStretchMode(string? mode) => (mode?.ToLowerInvariant()) switch
        {
            "center"  => (Stretch.None,             AlignmentX.Center, AlignmentY.Center, TileMode.None),
            "topleft" => (Stretch.None,             AlignmentX.Left,   AlignmentY.Top,    TileMode.None),
            "tile"    => (Stretch.None,             AlignmentX.Left,   AlignmentY.Top,    TileMode.Tile),
            _         => (Stretch.UniformToFill,    AlignmentX.Center, AlignmentY.Center, TileMode.None), // "stretch" default
        };

        private static void ApplyAppIcon(ResourceDictionary res, ThemeDefinition d, string folder)
        {
            // appIcon = tray icon + Window.Icon (taskbar) only.
            // The title bar uses 'logo' or the built-in shield — never appIcon.
            var iconPath = ResolveThemeAsset(folder, d.AppIcon);
            if (iconPath != null)
            {
                if (File.Exists(iconPath))
                {
                    try
                    {
                        var bmp = LoadImageUncached(iconPath);
                        res["Theme.AppIcon"]  = bmp;
                        res["Theme.TrayIcon"] = BitmapToWinFormsIcon(bmp);
                        return;
                    }
                    catch { }
                }
            }

            res["Theme.AppIcon"]  = null;
            res["Theme.TrayIcon"] = null;
        }

        /// <summary>
        /// Loads the optional connected/disconnected tray icons. Resolved per
        /// dark/light variant by MergeVariant before this runs, so a theme can ship
        /// all four variations (state × variant). Empty/missing → null, and the
        /// tray falls back to the single AppIcon or the built-in shield.
        /// </summary>
        private static void ApplyTrayStateIcons(ResourceDictionary res, ThemeDefinition d, string folder)
        {
            res["Theme.TrayIconConnected"]    = LoadStateIcon(d.TrayIconConnected);
            res["Theme.TrayIconDisconnected"] = LoadStateIcon(d.TrayIconDisconnected);

            object? LoadStateIcon(string file)
            {
                var path = ResolveThemeAsset(folder, file);
                if (path == null || !File.Exists(path)) return null;
                try { return BitmapToWinFormsIcon(LoadImageUncached(path)); }
                catch { return null; }
            }
        }

        /// <summary>
        /// Resolves the theme font. When the theme folder ships font files
        /// (*.ttf / *.otf — added by the Theme Builder's export), they are used as
        /// WPF private fonts — no installation needed on the client machine — with
        /// the installed system font of the same name as fallback.
        /// </summary>
        private static FontFamily ResolveFontFamily(string name, string folder)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "Segoe UI";
            try
            {
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder) &&
                    Directory.EnumerateFiles(folder).Any(f =>
                        f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)))
                {
                    var baseUri = new Uri(folder.TrimEnd('\\', '/') + "\\", UriKind.Absolute);
                    return new FontFamily(baseUri, $"./#{name}, {name}");
                }
            }
            catch { }
            return new FontFamily(name);
        }

        /// <summary>
        /// Loads an image fully into memory, bypassing WPF's URI cache. Keeps the
        /// file unlocked (so the Theme Builder can overwrite logo.png/bg.png in
        /// place) and guarantees a re-saved file under the same name shows its new
        /// content instead of a stale cached bitmap.
        /// </summary>
        /// <summary>
        /// Resolves a theme asset (logo / background / icon) named in theme.json to an
        /// absolute path, but ONLY if it stays inside the theme's own folder. Rejects
        /// rooted/UNC paths and "../" traversal so a theme — including one downloaded from
        /// a shared-theme repo — can't point the app at files outside its folder. Returns
        /// null when the value is empty or escapes the folder (treated as "no asset").
        /// </summary>
        private static string? ResolveThemeAsset(string folder, string? fileName)
        {
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(fileName)) return null;
            if (Path.IsPathRooted(fileName)) return null;   // must be a relative name
            try
            {
                var root = Path.GetFullPath(folder.TrimEnd('\\', '/')) + Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(Path.Combine(folder, fileName));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
                return full;
            }
            catch { return null; }
        }

        internal static BitmapImage LoadImageUncached(string path)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource     = new Uri(path, UriKind.Absolute);
            bmp.CacheOption   = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        private static void ApplyLogo(ResourceDictionary res, ThemeDefinition d, string folder)
        {
            var logoPath = ResolveThemeAsset(folder, d.Logo);
            if (logoPath != null)
            {
                if (File.Exists(logoPath))
                {
                    try
                    {
                        res["Theme.Logo"]           = LoadImageUncached(logoPath);
                        res["Theme.LogoWidth"]      = (double)d.LogoWidth;
                        res["Theme.LogoHeight"]     = (double)d.LogoHeight;
                        res["Theme.HasLogo"]        = Visibility.Visible;
                        res["Theme.HasBuiltinIcon"] = Visibility.Collapsed;  // logo replaces shield
                        return;
                    }
                    catch { /* fall through */ }
                }
            }

            res["Theme.Logo"]           = null;
            res["Theme.HasLogo"]        = Visibility.Collapsed;
            res["Theme.HasBuiltinIcon"] = Visibility.Visible;   // no logo → show shield
        }

        // ── System theme detection ────────────────────────────────────────────
        /// <summary>
        /// Returns true when Windows is set to dark app mode.
        /// Reads HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme.
        /// Returns true (dark) as the safe fallback if the key is missing.
        /// </summary>
        public static bool GetSystemIsDark()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("AppsUseLightTheme") is int v)
                    return v == 0;  // 0 = dark mode
            }
            catch { }
            return true; // default to dark
        }

        // ── Windows system-color theme ────────────────────────────────────────
        /// <summary>
        /// Returns the accent colour the user chose in Settings → Personalization → Colors.
        ///
        /// Primary source — AccentPalette (REG_BINARY, 32 bytes):
        ///   HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent\AccentPalette
        ///   Layout: 8 × {R, G, B, reserved}, ordered lightest→darkest.
        ///   Entry 3 (bytes 12-15) is the "Regular" shade — the exact chosen colour,
        ///   used by Windows for interactive controls (toggles, checkboxes, etc.).
        ///
        /// Fallback — DWM\AccentColor (ABGR DWORD):
        ///   HKCU\Software\Microsoft\Windows\DWM\AccentColor
        ///   This is the window-chrome shade (title bars) which can differ slightly.
        ///
        /// Returns "#0078D4" (Windows blue) if both keys are absent.
        /// </summary>
        public static string GetSystemAccentColor()
        {
            // AccentColorMenu — the exact colour the user chose in Settings → Personalization → Colors,
            // stored by Windows Explorer as a packed ABGR DWORD.
            try
            {
                using var explorerKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
                if (explorerKey?.GetValue("AccentColorMenu") is int menu)
                {
                    byte r = (byte)( menu         & 0xFF);
                    byte g = (byte)((menu >>  8) & 0xFF);
                    byte b = (byte)((menu >> 16) & 0xFF);
                    return $"#{r:X2}{g:X2}{b:X2}";
                }
                if (explorerKey?.GetValue("AccentPalette") is byte[] palette && palette.Length >= 16)
                    return $"#{palette[12]:X2}{palette[13]:X2}{palette[14]:X2}";
            }
            catch { }

            try
            {
                using var dwmKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\DWM");
                if (dwmKey?.GetValue("AccentColor") is int raw)
                {
                    byte r = (byte)( raw         & 0xFF);
                    byte g = (byte)((raw >>  8) & 0xFF);
                    byte b = (byte)((raw >> 16) & 0xFF);
                    return $"#{r:X2}{g:X2}{b:X2}";
                }
            }
            catch { }

            return "#0078D4"; // Windows blue fallback
        }

        /// <summary>
        /// Builds a complete ThemeDefinition for the current Windows dark or light mode.
        ///
        /// Win32 GetSysColor() (which WPF's SystemColors wraps) does NOT return dark
        /// values for apps that lack a dark-mode manifest entry — it always returns the
        /// traditional light palette.  We therefore use hand-tuned palettes here and
        /// only read the accent colour dynamically from the registry.
        ///
        /// Custom themes (theme.json) override any key they declare; empty keys fall
        /// back to these palette values via Apply()'s R() resolver.
        /// </summary>
        public static ThemeDefinition BuildSystemTheme(bool isDark)
        {
            string accent = GetSystemAccentColor();

            var def = new ThemeDefinition
            {
                Name       = isDark ? "System Dark" : "System Light",
                Type       = isDark ? "dark" : "light",
                // Use Windows' own UI font (Segoe UI on Win10/11, Tahoma on older).
                // Individual theme.json files can override this via "fontFamily".
                FontFamily = SystemFonts.MessageFontFamily?.Source ?? "Segoe UI",
            };

            if (isDark)
            {
                // ── Dark palette ──────────────────────────────────────────────
                def.ColorWindowBg    = "#1C1C1C";   // true dark canvas
                def.ColorSurface     = "#2C2C2C";   // title bar / footer / column headers
                def.ColorCard        = "#252525";   // list panels — between bg and surface
                def.ColorBorder      = "#3D3D3D";   // subtle separation
                def.ColorTextPrimary = "#EEEEEE";   // slightly off-white — easier on the eyes
                def.ColorTextMuted   = "#9E9E9E";   // secondary / label text
                def.ColorAccent      = accent;
                def.ColorSuccess     = "#3FB950";   // GitHub green — legible on dark
                def.ColorDanger      = "#F47067";   // warm red, not over-saturated
                def.ColorHighlight   = BlendHex(accent, "#1C1C1C", 0.25);
                def.ColorError       = "#F47067";
                def.ColorErrorBg     = "#3D1A1A";
                def.ColorWarning     = "#D4A017";
                def.ColorWarningBg   = "#3D2E00";
                def.ColorListHover   = "#2D2D2D";   // 1 step lighter than CardBg
                def.ColorListSelected = BlendHex(accent, "#1C1C1C", 0.40);
            }
            else
            {
                // ── Light palette ─────────────────────────────────────────────
                def.ColorWindowBg    = "#F5F5F5";   // off-white, matches Windows 11 shell
                def.ColorSurface     = "#EBEBEB";   // title bar / footer clearly distinct
                def.ColorCard        = "#FFFFFF";   // pure white panels for list contrast
                def.ColorBorder      = "#D5D5D5";   // visible but unobtrusive on white
                def.ColorTextPrimary = "#1C1C1C";   // near-black, high contrast
                def.ColorTextMuted   = "#646464";   // clearly secondary, never invisible
                def.ColorAccent      = accent;
                def.ColorSuccess     = "#1A7F37";   // dark green — readable on white
                def.ColorDanger      = "#C62828";   // deep red — clear destructive signal
                def.ColorHighlight   = BlendHex(accent, "#FFFFFF", 0.15);
                def.ColorError       = "#C62828";
                def.ColorErrorBg     = "#FDECEA";
                def.ColorWarning     = "#7B5800";
                def.ColorWarningBg   = "#FFF8E6";
                def.ColorListHover   = "#EDEDED";   // 1 step darker than CardBg
                def.ColorListSelected = BlendHex(accent, "#FFFFFF", 0.82);
            }

            return def;
        }

        /// <summary>Converts a WPF Color to a #RRGGBB hex string.</summary>
        private static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        /// <summary>
        /// Applies (or clears) a global font override on top of the active theme.
        /// Must be called AFTER Load() / LoadSystem() so it wins over the theme's own font.
        ///
        /// When <paramref name="enabled"/> is false, does nothing — the theme font set by
        /// Apply() remains in effect.  When enabled with an empty family, the Windows UI
        /// system font (SystemParameters.MessageFontFamily) is used.
        /// </summary>
        public static void ApplyFontOverride(bool enabled, string family, double size = 0.0)
        {
            if (!enabled) return;
            var res = Application.Current?.Resources;
            if (res == null) return;

            // Font family
            string fontName = string.IsNullOrWhiteSpace(family)
                ? (SystemFonts.MessageFontFamily?.Source ?? "Segoe UI")
                : family;
            try { res["Theme.FontFamily"] = new FontFamily(fontName); }
            catch { /* invalid font name — leave the current font in place */ }

            // Font size (only when explicitly set; 0 = keep theme default)
            if (size > 0.0)
            {
                res["Theme.FontSize"]       = size;
                res["Theme.FontSize.Small"] = Math.Max(8.0, size - 1.0);
                res["Theme.FontSize.Tiny"]  = Math.Max(7.0, size - 2.0);
            }
        }

        /// <summary>
        /// Applies a live theme derived from the current Windows system accent colour and
        /// light/dark mode.  Sets CurrentThemeName to "__system__" and fires ThemeChanged.
        /// </summary>
        public void LoadSystem(bool isDark)
        {
            var def = BuildSystemTheme(isDark);
            CurrentThemeName = "__system__";
            Current          = def;
            Apply(def, string.Empty, isDark);
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Convenience overload — detects dark/light from registry automatically.</summary>
        public void LoadSystem() => LoadSystem(GetSystemIsDark());

        /// <summary>
        /// Applies a ThemeDefinition directly as a live preview without changing
        /// CurrentThemeName or firing ThemeChanged.  Call Load() with the previous
        /// name to revert.
        /// </summary>
        public void ApplyPreview(ThemeDefinition def, string folder)
        {
            bool isDark = !def.Type.Equals("light", StringComparison.OrdinalIgnoreCase);
            Apply(def, folder, isDark);
        }

        /// <summary>
        /// Returns the full ThemeDefinition for a folder without applying it,
        /// used to read metadata (name, type, creator, description) for display.
        /// </summary>
        public static ThemeDefinition? GetThemeMetadata(string folderName)
        {
            try
            {
                var json = ThemeJson(folderName);
                if (!File.Exists(json)) return null;
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<ThemeDefinition>(File.ReadAllText(json), opts);
            }
            catch { return null; }
        }

        /// <summary>
        /// Sanitizes a string for safe display: strips HTML/XML tags, control characters,
        /// limits to maxLength, trims whitespace. Safe against injection.
        /// </summary>
        public static string SanitizeInfo(string? input, int maxLength = 150)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            // Strip any < > & characters (HTML/XML tags/entities)
            var sb = new System.Text.StringBuilder();
            foreach (char c in input)
            {
                if (c == '<' || c == '>' || c == '&' || c == '"' || c == '\'' || c == '`')
                    continue;
                if (char.IsControl(c) && c != '\n' && c != '\r')
                    continue;
                sb.Append(c);
            }
            var result = sb.ToString().Trim();
            // Collapse to max 3 lines
            var lines = result.Split('\n');
            if (lines.Length > 3)
                result = string.Join("\n", lines.Take(3));
            // Enforce hard character limit
            if (result.Length > maxLength)
                result = result[..maxLength].TrimEnd() + "…";
            return result;
        }

        /// <summary>Converts a BitmapImage to a multi-size System.Drawing.Icon for the tray.</summary>
        public static System.Drawing.Icon? BitmapToWinFormsIcon(BitmapImage bmp)
        {
            try
            {
                // Encode to PNG in memory, then produce a single-frame .ico
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var ms = new System.IO.MemoryStream();
                encoder.Save(ms);
                var pngBytes = ms.ToArray();

                // Write minimal .ico with one 32x32 PNG frame
                using var ico = new System.IO.MemoryStream();
                using var w   = new System.IO.BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true);
                w.Write((short)0);       // reserved
                w.Write((short)1);       // type: icon
                w.Write((short)1);       // count: 1 frame
                // ICONDIRENTRY
                w.Write((byte)0);        // width  (0 = 256)
                w.Write((byte)0);        // height (0 = 256)
                w.Write((byte)0);        // color count
                w.Write((byte)0);        // reserved
                w.Write((short)1);       // planes
                w.Write((short)32);      // bpp
                w.Write(pngBytes.Length);
                w.Write(6 + 16);         // offset = ICONDIR + 1×ICONDIRENTRY
                w.Write(pngBytes);
                ico.Position = 0;
                return new System.Drawing.Icon(ico);
            }
            catch { return null; }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Linearly blends two hex colour strings.
        /// factor=0 → src unchanged; factor=1 → becomes target colour.
        /// </summary>
        private static string BlendHex(string src, string target, double factor)
        {
            try
            {
                var s = ParseColor(src,    Colors.Gray);
                var t = ParseColor(target, Colors.Black);
                byte r = (byte)(s.R + (t.R - s.R) * factor);
                byte g = (byte)(s.G + (t.G - s.G) * factor);
                byte b = (byte)(s.B + (t.B - s.B) * factor);
                return $"#{r:X2}{g:X2}{b:X2}";
            }
            catch { return src; }
        }

        private static Color ParseColor(string hex, Color fallback)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return fallback; }
        }

        private static void SetColor(ResourceDictionary res, string key, string hex)
        {
            var c = ParseColor(hex, Colors.Transparent);
            if (res.Contains(key)) res[key] = c;
            else res.Add(key, c);
        }

        private static void SetBrush(ResourceDictionary res, string key, string hex)
        {
            res[key] = new SolidColorBrush(ParseColor(hex, Colors.Transparent));
        }

        /// <summary>SetBrush with an opacity multiplied into the colour's alpha channel.</summary>
        private static void SetBrush(ResourceDictionary res, string key, string hex, double opacity)
        {
            var c = ParseColor(hex, Colors.Transparent);
            if (opacity < 1.0)
                c.A = (byte)Math.Round(c.A * Math.Clamp(opacity, 0.0, 1.0));
            res[key] = new SolidColorBrush(c);
        }

        private static string Fallback(string value, string fallback)
            => string.IsNullOrWhiteSpace(value) ? fallback : value;

        private static System.Drawing.Color ToDrawingColor(string hex)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
            }
            catch { return System.Drawing.Color.FromArgb(22, 27, 34); }
        }
    }

    // ── Theme definition ──────────────────────────────────────────────────────
    public class ThemeDefinition
    {
        public string Name     { get; set; } = "Default Dark";
        public string AppName  { get; set; } = "MasselGUARD";

        public string FontFamily   { get; set; } = "Segoe UI";
        public double FontSize     { get; set; } = 12;
        public double CornerRadius { get; set; } = 6;

        // ── Colours — named by where/how each appears in the UI ───────────────
        // All values accept #RRGGBB (opaque) or #AARRGGBB (with transparency).
        // Empty string ("") means "inherit from the Windows system palette" —
        // Apply() fills unset keys from BuildSystemTheme() before applying.
        public string ColorWindowBg     { get; set; } = "";
        public string ColorSurface      { get; set; } = "";
        public string ColorCard         { get; set; } = "";
        public string ColorBorder       { get; set; } = "";
        public string ColorAccent       { get; set; } = "";
        public string ColorSuccess      { get; set; } = "";
        public string ColorDanger       { get; set; } = "";
        public string ColorTextPrimary  { get; set; } = "";
        public string ColorTextMuted    { get; set; } = "";
        public string ColorHighlight    { get; set; } = "";
        public string ColorError        { get; set; } = "";
        public string ColorErrorBg      { get; set; } = "";
        public string ColorWarning      { get; set; } = "";
        public string ColorWarningBg    { get; set; } = "";
        public string ColorListHover    { get; set; } = "";
        public string ColorListSelected { get; set; } = "";
        /// <summary>Timestamp colour in the activity log. Defaults to colorBorder if not set.</summary>
        public string ColorLogTimestamp    { get; set; } = "";

        // ── Background image ──────────────────────────────────────────────────
        public string BackgroundImage   { get; set; } = "";
        /// <summary>"stretch" (default) | "center" | "tile" | "topLeft"</summary>
        public string BackgroundStretch { get; set; } = "stretch";
        public double BackgroundOpacity { get; set; } = 1.0;

        // ── App icon (.ico / .png / .bmp / .jpg) — tray + title bar ──────────
        public string AppIcon           { get; set; } = "";

        // ── Tray icons per connection state (optional; empty → AppIcon/shield) ──
        /// <summary>Tray icon while at least one tunnel is connected. Per-variant via dark/light sections.</summary>
        public string TrayIconConnected    { get; set; } = "";
        /// <summary>Tray icon while no tunnel is connected. Per-variant via dark/light sections.</summary>
        public string TrayIconDisconnected { get; set; } = "";

        // ── Logo (title bar display only) ─────────────────────────────────────
        public string Logo              { get; set; } = "";
        public int    LogoWidth         { get; set; } = 28;
        public int    LogoHeight        { get; set; } = 28;

        public Dictionary<string, string>? Variables { get; set; } = null;

        // ── Metadata ──────────────────────────────────────────────────────────
        public string Type        { get; set; } = "dark";
        public string Creator     { get; set; } = "";
        public string Description { get; set; } = "";

        // ── Window chrome ─────────────────────────────────────────────────────
        /// <summary>Height of the title bar row in pixels. Default 48.</summary>
        public int    TitleBarHeight      { get; set; } = 48;
        /// <summary>Show the logo / shield icon in the title bar.</summary>
        public bool   ShowTitleBarIcon    { get; set; } = true;
        /// <summary>Show the app name text in the title bar.</summary>
        public bool   ShowTitleBarAppName { get; set; } = true;
        /// <summary>Show the resize grip in the bottom-right corner.</summary>
        public bool   ShowResizeGrip      { get; set; } = true;
        /// <summary>Overall window opacity (0.0 fully transparent – 1.0 fully opaque).</summary>
        public double WindowOpacity       { get; set; } = 1.0;

        /// <summary>
        /// Opacity of the Surface and Card panel brushes (0.2–1.0). Below 1.0 the
        /// window background image shows through the panels.
        /// </summary>
        public double PanelOpacity        { get; set; } = 1.0;

        // ── Status bar ────────────────────────────────────────────────────────
        /// <summary>Show the status bar below the title bar.</summary>
        public bool   ShowStatusBar       { get; set; } = true;
        /// <summary>Height of the status bar row in pixels. Default 38.</summary>
        public int    StatusBarHeight     { get; set; } = 38;
        /// <summary>Show the WiFi network name in the status bar.</summary>
        public bool   ShowStatusWifi      { get; set; } = true;
        /// <summary>Show the active tunnel name in the status bar.</summary>
        public bool   ShowStatusTunnel    { get; set; } = true;

        // ── Tray menu colours ─────────────────────────────────────────────────
        /// <summary>Tray context menu background. Defaults to colorSurface.</summary>
        public string ColorTrayBg          { get; set; } = "";
        /// <summary>Tray menu item hover / selected background. Defaults to colorBorder.</summary>
        public string ColorTrayHover       { get; set; } = "";
        /// <summary>Tray menu item text colour. Defaults to colorTextPrimary.</summary>
        public string ColorTrayText        { get; set; } = "";
        /// <summary>Tray menu border and separator colour. Defaults to colorBorder.</summary>
        public string ColorTrayBorder      { get; set; } = "";
        /// <summary>Tray menu left image-margin column colour. Defaults to colorWindowBg.</summary>
        public string ColorTrayImageMargin { get; set; } = "";

        // ── Dual-variant support ──────────────────────────────────────────────
        /// <summary>
        /// Optional dark-mode color overrides.  When present, the app loads these
        /// colors (merged on top of the root values) when dark mode is active.
        /// All structural settings (font, corner radius, window chrome, etc.) are
        /// always taken from the root — only color fields are merged from variants.
        /// </summary>
        public ThemeDefinition? Dark  { get; set; } = null;

        /// <summary>
        /// Optional light-mode color overrides.  Merged on top of root colors when
        /// light mode is active.
        /// </summary>
        public ThemeDefinition? Light { get; set; } = null;

        /// <summary>True when the JSON contains at least one variant section.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsDualVariant => Dark != null || Light != null;

        public static ThemeDefinition Default => WindowsDefault;

        /// <summary>
        /// Hardcoded Windows Default theme — Fluent Design tokens, Segoe UI Variable.
        /// Used when no theme file is found and as the built-in "windows-default" virtual theme.
        /// </summary>
        public static ThemeDefinition WindowsDefault => new ThemeDefinition
        {
            Name         = "Windows Default",
            AppName      = "MasselGUARD",
            FontFamily   = "Segoe UI Variable",
            FontSize     = 12,
            CornerRadius = 8,
            Dark = new ThemeDefinition
            {
                ColorWindowBg     = "#202020",
                ColorSurface      = "#2B2B2B",
                ColorCard         = "#313131",
                ColorBorder       = "#3D3D3D",
                ColorAccent       = "#0078D4",
                ColorSuccess      = "#6CCB5F",
                ColorDanger       = "#FF4343",
                ColorTextPrimary  = "#FFFFFF",
                ColorTextMuted    = "#9D9D9D",
                ColorHighlight    = "#004578",
                ColorError        = "#FF4343",
                ColorErrorBg      = "#3D1A1A",
                ColorWarning      = "#FCE100",
                ColorWarningBg    = "#3D3400",
                ColorListHover    = "#3D3D3D",
                ColorListSelected = "#004578",
                ColorLogTimestamp = "#9D9D9D",
                ColorTrayBg          = "#2B2B2B",
                ColorTrayHover       = "#3D3D3D",
                ColorTrayText        = "#FFFFFF",
                ColorTrayBorder      = "#3D3D3D",
                ColorTrayImageMargin = "#2B2B2B",
            },
            Light = new ThemeDefinition
            {
                ColorWindowBg     = "#F3F3F3",
                ColorSurface      = "#FFFFFF",
                ColorCard         = "#EBEBEB",
                ColorBorder       = "#D4D4D4",
                ColorAccent       = "#0067C0",
                ColorSuccess      = "#107C10",
                ColorDanger       = "#C42B1C",
                ColorTextPrimary  = "#1C1C1C",
                ColorTextMuted    = "#5D5D5D",
                ColorHighlight    = "#CCE4F7",
                ColorError        = "#C42B1C",
                ColorErrorBg      = "#FDE7E9",
                ColorWarning      = "#7A5D1A",
                ColorWarningBg    = "#FFF4CE",
                ColorListHover    = "#E5E5E5",
                ColorListSelected = "#CCE4F7",
                ColorLogTimestamp = "#5D5D5D",
                ColorTrayBg          = "#F3F3F3",
                ColorTrayHover       = "#E5E5E5",
                ColorTrayText        = "#1C1C1C",
                ColorTrayBorder      = "#D4D4D4",
                ColorTrayImageMargin = "#F3F3F3",
            },
        };
    }
}
