using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

        /// <summary>Returns all available theme folder names that contain a theme.json.</summary>
        public static List<string> AvailableThemes()
        {
            var root = ThemeRoot;
            if (!Directory.Exists(root)) return new List<string>();
            return Directory.GetDirectories(root)
                .Select(d => Path.GetFileName(d)!)
                .Where(n => File.Exists(Path.Combine(root, n, "theme.json")))
                .OrderBy(n => n)
                .ToList();
        }

        /// <summary>
        /// Reads just the display name from a theme's JSON without fully loading it.
        /// Returns the folder name as fallback if name is missing or file unreadable.
        /// </summary>
        public static string GetThemeDisplayName(string folderName)
        {
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

            // C.* Color resources
            SetColor(res, "C.WindowBg",      d.ColorWindowBg);
            SetColor(res, "C.Surface",       d.ColorSurface);
            SetColor(res, "C.CardBg",        d.ColorCard);
            SetColor(res, "C.BorderColor",   d.ColorBorder);
            SetColor(res, "C.Accent",        d.ColorAccent);
            SetColor(res, "C.Success",       d.ColorSuccess);
            SetColor(res, "C.Danger",        d.ColorDanger);
            SetColor(res, "C.TextPrimary",   d.ColorTextPrimary);
            SetColor(res, "C.TextMuted",     d.ColorTextMuted);
            SetColor(res, "C.Highlight",     d.ColorHighlight);

            // Brush resources
            SetBrush(res, "WindowBg",      d.ColorWindowBg);
            SetBrush(res, "Surface",       d.ColorSurface);
            SetBrush(res, "CardBg",        d.ColorCard);
            SetBrush(res, "BorderColor",   d.ColorBorder);
            SetBrush(res, "Accent",        d.ColorAccent);
            SetBrush(res, "Success",       d.ColorSuccess);
            SetBrush(res, "Danger",        d.ColorDanger);
            SetBrush(res, "TextPrimary",   d.ColorTextPrimary);
            SetBrush(res, "TextMuted",     d.ColorTextMuted);
            SetBrush(res, "Highlight",     d.ColorHighlight);
            SetBrush(res, "ErrorColor",    d.ColorError);
            SetBrush(res, "ErrorBg",       d.ColorErrorBg);
            SetBrush(res, "WarningColor",  d.ColorWarning);
            SetBrush(res, "WarningBg",     d.ColorWarningBg);
            SetBrush(res, "ListHover",     d.ColorListHover);
            SetBrush(res, "ListSelected",  d.ColorListSelected);

            // Log timestamp colour — falls back to colorBorder if not set in theme
            var tsHex = !string.IsNullOrWhiteSpace(d.ColorLogTimestamp) ? d.ColorLogTimestamp : d.ColorBorder;
            res["Theme.LogTimestampColor"] = ParseColor(tsHex, Colors.Gray);

            // Typography
            res["Theme.FontFamily"]         = new FontFamily(d.FontFamily);
            res["Theme.FontSize"]           = d.FontSize;
            res["Theme.CornerRadius"]       = new CornerRadius(d.CornerRadius);
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
            res["Theme.ShowStatusBar"]       = d.ShowStatusBar    ? Visibility.Visible : Visibility.Collapsed;
            res["Theme.StatusBarHeight"]     = new GridLength(d.ShowStatusBar ? Math.Max(24, d.StatusBarHeight) : 0);
            res["Theme.ShowStatusWifi"]      = d.ShowStatusWifi   ? Visibility.Visible : Visibility.Collapsed;
            res["Theme.ShowStatusTunnel"]    = d.ShowStatusTunnel ? Visibility.Visible : Visibility.Collapsed;

            // ── Tray menu colours — fall back to semantic colours when empty ──
            string trayBg     = Fallback(d.ColorTrayBg,          d.ColorSurface);
            string trayHover  = Fallback(d.ColorTrayHover,       d.ColorBorder);
            string trayText   = Fallback(d.ColorTrayText,        d.ColorTextPrimary);
            string trayBorder = Fallback(d.ColorTrayBorder,      d.ColorBorder);
            string trayImg    = Fallback(d.ColorTrayImageMargin, d.ColorWindowBg);
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
        // All values accept #RRGGBB (opaque) or #AARRGGBB (with transparency)
        public string ColorWindowBg     { get; set; } = "#0E1117";
        public string ColorSurface      { get; set; } = "#161B22";
        public string ColorCard         { get; set; } = "#1C2128";
        public string ColorBorder       { get; set; } = "#21262D";
        public string ColorAccent       { get; set; } = "#58A6FF";
        public string ColorSuccess      { get; set; } = "#3FB950";
        public string ColorDanger       { get; set; } = "#F78166";
        public string ColorTextPrimary  { get; set; } = "#E6EDF3";
        public string ColorTextMuted    { get; set; } = "#8B949E";
        public string ColorHighlight    { get; set; } = "#1F6FEB";
        public string ColorError        { get; set; } = "#F78166";
        public string ColorErrorBg      { get; set; } = "#3D1A1A";
        public string ColorWarning      { get; set; } = "#D29922";
        public string ColorWarningBg    { get; set; } = "#3D2A0A";
        public string ColorListHover    { get; set; } = "#141A22";
        public string ColorListSelected    { get; set; } = "#0D2748";
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
