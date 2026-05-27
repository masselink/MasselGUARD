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

        public string          CurrentThemeName { get; private set; } = "default-dark";
        public ThemeDefinition Current          { get; private set; } = ThemeDefinition.Default;

        // ── Paths ─────────────────────────────────────────────────────────────
        private static string ThemeRoot =>
            Path.Combine(
                Path.GetDirectoryName(
                    System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                    ?? AppContext.BaseDirectory)
                ?? AppContext.BaseDirectory,
                "theme");

        public static string ThemeFolder(string name) => Path.Combine(ThemeRoot, name);
        private static string ThemeJson(string name)  => Path.Combine(ThemeFolder(name), "theme.json");

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all available theme folder names that contain a theme.json,
        /// plus the virtual "__system__" entry (Windows accent colours).
        /// </summary>
        public static List<string> AvailableThemes()
        {
            var themes = new List<string> { "__system__" };   // virtual — Windows system colours
            var root   = ThemeRoot;
            if (Directory.Exists(root))
                themes.AddRange(Directory.GetDirectories(root)
                    .Select(d => Path.GetFileName(d)!)
                    .Where(n => File.Exists(Path.Combine(root, n, "theme.json")))
                    .OrderBy(n => n));
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

        /// <summary>Loads a theme by folder name and applies it live to Application.Resources.</summary>
        public void Load(string themeName)
        {
            // ── Virtual theme: use Windows system colours ─────────────────────
            if (themeName is "__system__" or "system")
            {
                LoadSystem(GetSystemIsDark());
                return;
            }

            var jsonPath = ThemeJson(themeName);
            ThemeDefinition def;
            if (File.Exists(jsonPath))
            {
                try
                {
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    def = JsonSerializer.Deserialize<ThemeDefinition>(File.ReadAllText(jsonPath), opts)
                          ?? ThemeDefinition.Default;
                }
                catch { def = ThemeDefinition.Default; }
            }
            else { def = ThemeDefinition.Default; }

            CurrentThemeName = themeName;
            Current          = def;
            Apply(def, ThemeFolder(themeName));
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Apply all theme values to Application.Resources ───────────────────
        private static void Apply(ThemeDefinition d, string folder)
        {
            var res = Application.Current.Resources;

            // ── Resolve colours: system palette as base, theme JSON as overrides ──
            // Any colour left as "" in the theme JSON (or absent from the file) falls
            // back to the live Windows system colour for that semantic slot.  This means
            // every theme automatically inherits the current dark/light palette and only
            // needs to declare the colours it deliberately wants to change.
            bool isDark = !d.Type.Equals("light", StringComparison.OrdinalIgnoreCase);
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

            // Brush resources
            SetBrush(res, "WindowBg",     bg);
            SetBrush(res, "Surface",      surface);
            SetBrush(res, "CardBg",       card);
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
            res["Theme.FontFamily"]          = new FontFamily(d.FontFamily);
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

            var imgPath = Path.Combine(folder, d.BackgroundImage);
            if (!File.Exists(imgPath))
            {
                res["Theme.BackgroundBrush"] = new SolidColorBrush(Colors.Transparent);
                res["Theme.HasBackground"]   = Visibility.Collapsed;
                return;
            }

            try
            {
                var bmp = new BitmapImage(new Uri(imgPath, UriKind.Absolute));

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
            if (!string.IsNullOrWhiteSpace(d.AppIcon))
            {
                var iconPath = Path.Combine(folder, d.AppIcon);
                if (File.Exists(iconPath))
                {
                    try
                    {
                        var bmp = new BitmapImage(new Uri(iconPath, UriKind.Absolute));
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

        private static void ApplyLogo(ResourceDictionary res, ThemeDefinition d, string folder)
        {
            if (!string.IsNullOrWhiteSpace(d.Logo))
            {
                var logoPath = Path.Combine(folder, d.Logo);
                if (File.Exists(logoPath))
                {
                    try
                    {
                        res["Theme.Logo"]           = new BitmapImage(new Uri(logoPath, UriKind.Absolute));
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
            Apply(def, string.Empty);
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Convenience overload — detects dark/light from registry automatically.</summary>
        public void LoadSystem() => LoadSystem(GetSystemIsDark());

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

        public static ThemeDefinition Default => new ThemeDefinition();
    }
}
