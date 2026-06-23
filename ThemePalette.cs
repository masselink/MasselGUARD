using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MasselGUARD
{
    /// <summary>
    /// Extracts a theme colour palette from an image (Theme Builder → New from image).
    /// Median-cut quantization over a downscaled copy of the picture. Only ambience
    /// colours (backgrounds, surfaces, accent, text, tray) are derived — status colours
    /// (success/danger/warning) are left empty so they keep their semantic system hues.
    /// </summary>
    internal static class ThemePalette
    {
        /// <summary>
        /// Builds a flat single-variant ThemeDefinition from an image's dominant
        /// colours. The variant matches the image's overall brightness; the opposite
        /// variant is auto-generated at load time or via the builder's button.
        /// </summary>
        public static ThemeDefinition FromImage(string imagePath, out bool isDark)
        {
            var clusters = ExtractClusters(imagePath, maxClusters: 8);
            if (clusters.Count == 0)
                throw new InvalidOperationException("No usable pixels found in the image.");

            double totalW = clusters.Sum(c => c.Weight);
            double avgL   = clusters.Sum(c => c.L * c.Weight) / totalW;
            isDark = avgL < 0.5;

            // Background: darkest (dark variant) / lightest (light variant) cluster
            // among those carrying real weight, so a stray pixel can't win.
            var significant = clusters.Where(c => c.Weight >= totalW * 0.03).ToList();
            if (significant.Count == 0) significant = clusters;
            var bg = isDark
                ? significant.OrderBy(c => c.L).First()
                : significant.OrderByDescending(c => c.L).First();

            // Accent: the most saturated cluster that stands apart from the background
            var accent = clusters
                .Where(c => Math.Abs(c.L - bg.L) > 0.08 || c.S > bg.S + 0.2)
                .OrderByDescending(c => c.S * Math.Sqrt(c.Weight))
                .FirstOrDefault()
                ?? clusters.OrderByDescending(c => c.S).First();

            // Clamp into usable ranges
            double bgL  = isDark ? Math.Clamp(bg.L, 0.04, 0.22) : Math.Clamp(bg.L, 0.82, 0.97);
            double bgS  = Math.Min(bg.S, 0.45);
            double accL = isDark ? Math.Clamp(accent.L, 0.50, 0.72) : Math.Clamp(accent.L, 0.35, 0.55);
            double accS = Math.Max(accent.S, 0.35);

            // Surfaces step away from the background: lighter on dark, darker on light
            double step = isDark ? 1 : -1;
            string Tone(double h, double s, double l) => ToHex(FromHsl(h, s, Math.Clamp(l, 0.02, 0.98)));

            string textPrimary = isDark ? "#F2F2F2" : "#1A1A1A";
            string textMuted   = Tone(bg.H, Math.Min(bgS, 0.15), isDark ? 0.65 : 0.40);
            string surface     = Tone(bg.H, bgS, bgL + 0.04 * step);
            string border      = Tone(bg.H, bgS * 0.8, bgL + 0.16 * step);
            string listHover   = Tone(bg.H, bgS, bgL + 0.09 * step);

            return new ThemeDefinition
            {
                Type = isDark ? "dark" : "light",

                ColorWindowBg     = Tone(bg.H, bgS, bgL),
                ColorSurface      = surface,
                ColorCard         = Tone(bg.H, bgS, bgL + 0.07 * step),
                ColorBorder       = border,
                ColorListHover    = listHover,
                ColorListSelected = Tone(accent.H, accS * 0.5, bgL + 0.13 * step),

                ColorAccent       = Tone(accent.H, accS, accL),
                ColorHighlight    = Tone(accent.H, accS, accL),

                ColorTextPrimary  = textPrimary,
                ColorTextMuted    = textMuted,
                ColorLogTimestamp = textMuted,

                ColorTrayBg     = surface,
                ColorTrayHover  = listHover,
                ColorTrayText   = textPrimary,
                ColorTrayBorder = border,

                // Success/Danger/Error/Warning intentionally empty → semantic
                // system-palette fallbacks.
            };
        }

        // ── Quantization ──────────────────────────────────────────────────────

        private sealed class Cluster
        {
            public double H, S, L;
            public int Weight;
        }

        private static List<Cluster> ExtractClusters(string imagePath, int maxClusters)
        {
            // Decode downscaled — 64 px wide is plenty for dominant colours
            var src = new BitmapImage();
            src.BeginInit();
            src.UriSource        = new Uri(imagePath, UriKind.Absolute);
            src.DecodePixelWidth = 64;
            src.CacheOption      = BitmapCacheOption.OnLoad;
            src.EndInit();

            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);

            var pixels = new List<(byte r, byte g, byte b)>(w * h);
            for (int i = 0; i < px.Length; i += 4)
            {
                if (px[i + 3] < 128) continue;   // skip transparent
                pixels.Add((px[i + 2], px[i + 1], px[i]));
            }
            if (pixels.Count == 0) return new List<Cluster>();

            // Median-cut: repeatedly split the box with the largest channel range
            var boxes = new List<List<(byte r, byte g, byte b)>> { pixels };
            while (boxes.Count < maxClusters)
            {
                List<(byte r, byte g, byte b)>? widest = null;
                int widestRange = 0, widestChannel = 0;
                foreach (var box in boxes)
                {
                    if (box.Count < 2) continue;
                    int rr = box.Max(p => p.r) - box.Min(p => p.r);
                    int gr = box.Max(p => p.g) - box.Min(p => p.g);
                    int br = box.Max(p => p.b) - box.Min(p => p.b);
                    int range = Math.Max(rr, Math.Max(gr, br));
                    if (range > widestRange)
                    {
                        widest = box; widestRange = range;
                        widestChannel = range == rr ? 0 : range == gr ? 1 : 2;
                    }
                }
                if (widest == null || widestRange == 0) break;

                var sorted = widestChannel switch
                {
                    0 => widest.OrderBy(p => p.r).ToList(),
                    1 => widest.OrderBy(p => p.g).ToList(),
                    _ => widest.OrderBy(p => p.b).ToList(),
                };
                int mid = sorted.Count / 2;
                boxes.Remove(widest);
                boxes.Add(sorted.Take(mid).ToList());
                boxes.Add(sorted.Skip(mid).ToList());
            }

            return boxes
                .Where(b => b.Count > 0)
                .Select(b =>
                {
                    double r = b.Average(p => (double)p.r) / 255.0;
                    double g = b.Average(p => (double)p.g) / 255.0;
                    double bl = b.Average(p => (double)p.b) / 255.0;
                    RgbToHsl(r, g, bl, out var hh, out var ss, out var ll);
                    return new Cluster { H = hh, S = ss, L = ll, Weight = b.Count };
                })
                .ToList();
        }

        // ── Colour helpers ────────────────────────────────────────────────────

        private static void RgbToHsl(double r, double g, double b,
            out double h, out double s, out double l)
        {
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2.0;
            if (Math.Abs(max - min) < 0.0001) { h = 0; s = 0; return; }

            double d = max - min;
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if      (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6.0;
            else if (max == g) h = ((b - r) / d + 2) / 6.0;
            else               h = ((r - g) / d + 4) / 6.0;
        }

        private static Color FromHsl(double h, double s, double l)
        {
            static double Hue(double p, double q, double t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                if (t < 1.0 / 6) return p + (q - p) * 6 * t;
                if (t < 1.0 / 2) return q;
                if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
                return p;
            }

            double r, g, b;
            if (s < 0.0001) { r = g = b = l; }
            else
            {
                double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
                double p = 2 * l - q;
                r = Hue(p, q, h + 1.0 / 3);
                g = Hue(p, q, h);
                b = Hue(p, q, h - 1.0 / 3);
            }
            return Color.FromRgb((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
        }

        private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
