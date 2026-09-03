using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MasselGUARD.Views
{
    /// <summary>
    /// Up to three equal-size "activity ring" arcs showing data-usage against each cap,
    /// laid out left → right: day · week · month. A ring is drawn only when that period's
    /// cap is set (the set ones pack together, always the same diameter regardless of how
    /// many are shown). Each arc sweeps 0→360° as usage → cap, turning amber near the limit
    /// and red once over it, with a muted localized centre glyph (d/w/m, respecting the UI
    /// language) so the periods stay identifiable even when some rings are absent.
    /// Purely presentational (OnRender), theme-aware.
    /// </summary>
    public sealed class CapRings : FrameworkElement
    {
        private const double Stroke  = 2.5;   // arc thickness
        private const double RingGap  = 3.0;  // horizontal space between rings
        private const double WarnAt  = 0.85;  // fraction at which a ring turns amber

        // Usage fractions (used / cap); may exceed 1.0 when over budget.
        public static readonly DependencyProperty DayFractionProperty =
            DependencyProperty.Register(nameof(DayFraction), typeof(double), typeof(CapRings),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty WeekFractionProperty =
            DependencyProperty.Register(nameof(WeekFraction), typeof(double), typeof(CapRings),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty MonthFractionProperty =
            DependencyProperty.Register(nameof(MonthFraction), typeof(double), typeof(CapRings),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        // Whether each period's cap is configured (only set rings are drawn).
        // AffectsMeasure so the control re-sizes (via MeasureOverride) when the number of
        // configured rings changes, not just re-renders.
        public static readonly DependencyProperty DaySetProperty =
            DependencyProperty.Register(nameof(DaySet), typeof(bool), typeof(CapRings),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty WeekSetProperty =
            DependencyProperty.Register(nameof(WeekSet), typeof(bool), typeof(CapRings),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty MonthSetProperty =
            DependencyProperty.Register(nameof(MonthSet), typeof(bool), typeof(CapRings),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        // When false (tunnel disconnected) the arcs render greyed, still at real usage.
        public static readonly DependencyProperty ActiveProperty =
            DependencyProperty.Register(nameof(Active), typeof(bool), typeof(CapRings),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        public double DayFraction   { get => (double)GetValue(DayFractionProperty);   set => SetValue(DayFractionProperty, value); }
        public double WeekFraction  { get => (double)GetValue(WeekFractionProperty);  set => SetValue(WeekFractionProperty, value); }
        public double MonthFraction { get => (double)GetValue(MonthFractionProperty); set => SetValue(MonthFractionProperty, value); }
        public bool   DaySet        { get => (bool)GetValue(DaySetProperty);          set => SetValue(DaySetProperty, value); }
        public bool   WeekSet       { get => (bool)GetValue(WeekSetProperty);         set => SetValue(WeekSetProperty, value); }
        public bool   MonthSet      { get => (bool)GetValue(MonthSetProperty);        set => SetValue(MonthSetProperty, value); }
        public bool   Active        { get => (bool)GetValue(ActiveProperty);          set => SetValue(ActiveProperty, value); }

        // Claim exactly the width the set rings need at the current font-derived diameter,
        // so the control sizes to the tunnel line rather than a hard-coded box.
        protected override Size MeasureOverride(Size availableSize)
        {
            int n = (DaySet ? 1 : 0) + (WeekSet ? 1 : 0) + (MonthSet ? 1 : 0);
            if (n == 0) return new Size(0, 0);
            double dia = RingDiameter();
            return new Size(n * dia + (n - 1) * RingGap, dia);
        }

        // Diameter derived from the row's font size (larger theme font ⇒ larger rings),
        // clamped to a sensible range.
        private double RingDiameter()
        {
            double fs = ThemeDouble("Theme.FontSize", 13.0);
            return Math.Max(14.0, Math.Min(30.0, fs * 1.55));
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            // Transparent fill so the whole control is hit-testable (tooltip fires over
            // the gaps between rings, not only where a stroke was drawn).
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            // Configured periods in fixed order: day, week, month.
            var periods = new (bool Set, double Frac, string Glyph)[]
            {
                (DaySet,   DayFraction,   Lang.T("CapRingDay")),
                (WeekSet,  WeekFraction,  Lang.T("CapRingWeek")),
                (MonthSet, MonthFraction, Lang.T("CapRingMonth")),
            };

            int n = 0;
            foreach (var p in periods) if (p.Set) n++;
            if (n == 0) return;

            // Diameter tracks the row's font size (scales with the theme), then clamped
            // so it never exceeds the control's own box.
            double dia = RingDiameter();
            dia = Math.Min(dia, h - 1);
            dia = Math.Min(dia, (w - (n - 1) * RingGap) / n);
            if (dia <= Stroke) return;
            double r = dia / 2 - Stroke / 2 - 0.5;
            if (r < Stroke / 2) return;

            var track = TrackColor();

            // Centre the drawn rings as a group.
            double contentW = n * dia + (n - 1) * RingGap;
            double x = (w - contentW) / 2;
            double cy = h / 2;

            foreach (var p in periods)
            {
                if (!p.Set) continue;
                double cx = x + dia / 2;
                DrawRing(dc, new Point(cx, cy), r, p.Frac, track, p.Glyph);
                x += dia + RingGap;
            }
        }

        private void DrawRing(DrawingContext dc, Point c, double r, double frac, Color track, string glyph)
        {
            // Faint full-circle track behind every configured ring.
            var trackPen = new Pen(new SolidColorBrush(track), Stroke);
            trackPen.Freeze();
            dc.DrawEllipse(null, trackPen, c, r, r);

            // Progress arc (skipped at zero usage — the track + glyph still show).
            double f = frac;
            if (f > 0)
            {
                var pen = new Pen(new SolidColorBrush(Active ? ArcColor(f) : InactiveArcColor()), Stroke)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap   = PenLineCap.Round,
                };
                pen.Freeze();

                if (f >= 0.9999)   // full / over budget → complete ring
                {
                    dc.DrawEllipse(null, pen, c, r, r);
                }
                else
                {
                    double sweep = 360.0 * f;                 // clockwise from 12 o'clock
                    Point start = PointOnArc(c, r, -90);
                    Point end   = PointOnArc(c, r, -90 + sweep);

                    var geo = new StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        ctx.BeginFigure(start, false, false);
                        ctx.ArcTo(end, new Size(r, r), 0, sweep > 180,
                                  SweepDirection.Clockwise, true, false);
                    }
                    geo.Freeze();
                    dc.DrawGeometry(null, pen, geo);
                }
            }

            // Muted localized centre glyph (d / w / m).
            DrawGlyph(dc, c, r, glyph);
        }

        private void DrawGlyph(DrawingContext dc, Point c, double r, string glyph)
        {
            if (string.IsNullOrEmpty(glyph)) return;

            var family = (TryFindResource("Theme.FontFamily") as FontFamily)
                         ?? new FontFamily("Segoe UI");
            var brush = new SolidColorBrush(GlyphColor());
            brush.Freeze();

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var ft = new FormattedText(
                glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                r * 1.15, brush, dpi);

            // Centre on the glyph's actual ink bounds — FormattedText.Height/Width include
            // ascent/descent leading and side bearings, so centring by those sits the
            // letter slightly low and off; the ink bounds give a true optical centre.
            var geo = ft.BuildGeometry(new Point(0, 0));
            Rect b = geo.Bounds;
            if (b.IsEmpty)
            {
                dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
                return;
            }
            dc.DrawText(ft, new Point(
                c.X - b.Left - b.Width  / 2,
                c.Y - b.Top  - b.Height / 2));
        }

        private static Point PointOnArc(Point c, double r, double angleDeg)
        {
            double a = angleDeg * Math.PI / 180.0;
            return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }

        private Color ArcColor(double f) =>
            f >= 1.0    ? ThemeColor("Danger",      Color.FromRgb(0xE7, 0x4C, 0x3C))
          : f >= WarnAt ? ThemeColor("WarningColor", Color.FromRgb(0xF0, 0xA0, 0x2E))
          :               ThemeColor("Accent",       Color.FromRgb(0x3D, 0x8B, 0xFD));

        // Disconnected: a single muted grey arc (still at real usage), clearly "inactive"
        // but stronger than the faint track so the fill level stays readable.
        private Color InactiveArcColor()
        {
            var b = ThemeColor("TextMuted", Color.FromRgb(0x80, 0x80, 0x80));
            return Color.FromArgb(0xB0, b.R, b.G, b.B);
        }

        private Color TrackColor()
        {
            var b = ThemeColor("TextMuted", Color.FromRgb(0x80, 0x80, 0x80));
            return Color.FromArgb(0x38, b.R, b.G, b.B);   // ~22% alpha
        }

        private Color GlyphColor()
        {
            var b = ThemeColor("TextMuted", Color.FromRgb(0x80, 0x80, 0x80));
            return Color.FromArgb(0xC0, b.R, b.G, b.B);   // ~75% alpha — legible but quiet
        }

        private Color ThemeColor(string key, Color fallback)
        {
            try
            {
                if (TryFindResource(key) is SolidColorBrush b) return b.Color;
                if (Application.Current?.TryFindResource(key) is SolidColorBrush ab) return ab.Color;
            }
            catch { /* fall through */ }
            return fallback;
        }

        private double ThemeDouble(string key, double fallback)
        {
            try
            {
                if (TryFindResource(key) is double d) return d;
                if (Application.Current?.TryFindResource(key) is double ad) return ad;
            }
            catch { /* fall through */ }
            return fallback;
        }
    }
}
