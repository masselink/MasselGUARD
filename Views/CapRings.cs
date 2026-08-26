using System;
using System.Windows;
using System.Windows.Media;

namespace MasselGUARD.Views
{
    /// <summary>
    /// Three concentric "activity ring" arcs showing data-usage against each cap:
    /// innermost = day, middle = week, outermost = month. A ring is drawn only when
    /// that period's cap is set; each arc sweeps 0→360° as usage → cap, turning amber
    /// near the limit and red once over it. Purely presentational (OnRender), theme-aware.
    /// </summary>
    public sealed class CapRings : FrameworkElement
    {
        private const double Stroke = 2.5;   // arc thickness
        private const double Gap    = 1.6;   // space between concentric rings
        private const double WarnAt = 0.85;  // fraction at which a ring turns amber

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
        public static readonly DependencyProperty DaySetProperty =
            DependencyProperty.Register(nameof(DaySet), typeof(bool), typeof(CapRings),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty WeekSetProperty =
            DependencyProperty.Register(nameof(WeekSet), typeof(bool), typeof(CapRings),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty MonthSetProperty =
            DependencyProperty.Register(nameof(MonthSet), typeof(bool), typeof(CapRings),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public double DayFraction   { get => (double)GetValue(DayFractionProperty);   set => SetValue(DayFractionProperty, value); }
        public double WeekFraction  { get => (double)GetValue(WeekFractionProperty);  set => SetValue(WeekFractionProperty, value); }
        public double MonthFraction { get => (double)GetValue(MonthFractionProperty); set => SetValue(MonthFractionProperty, value); }
        public bool   DaySet        { get => (bool)GetValue(DaySetProperty);          set => SetValue(DaySetProperty, value); }
        public bool   WeekSet       { get => (bool)GetValue(WeekSetProperty);         set => SetValue(WeekSetProperty, value); }
        public bool   MonthSet      { get => (bool)GetValue(MonthSetProperty);        set => SetValue(MonthSetProperty, value); }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            // Transparent fill so the whole control is hit-testable (tooltip fires over
            // the gaps between arcs, not only where a stroke was drawn).
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            var  center = new Point(w / 2, h / 2);
            double avail = Math.Min(w, h) / 2 - Stroke / 2 - 1;   // outer arc radius
            if (avail <= Stroke) return;
            double step  = Stroke + Gap;

            var track = TrackColor();

            // Outer → inner so the rings never clip each other: month, week, day.
            DrawRing(dc, center, avail,            MonthSet, MonthFraction, track);
            DrawRing(dc, center, avail - step,     WeekSet,  WeekFraction,  track);
            DrawRing(dc, center, avail - 2 * step, DaySet,   DayFraction,   track);
        }

        private void DrawRing(DrawingContext dc, Point c, double r, bool set, double frac, Color track)
        {
            if (!set || r < Stroke / 2) return;

            // Faint full-circle track behind every configured ring.
            var trackPen = new Pen(new SolidColorBrush(track), Stroke);
            trackPen.Freeze();
            dc.DrawEllipse(null, trackPen, c, r, r);

            double f = frac;
            if (f <= 0) return;

            var pen = new Pen(new SolidColorBrush(ArcColor(f)), Stroke)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap   = PenLineCap.Round,
            };
            pen.Freeze();

            if (f >= 0.9999)   // full / over budget → complete ring
            {
                dc.DrawEllipse(null, pen, c, r, r);
                return;
            }

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

        private static Point PointOnArc(Point c, double r, double angleDeg)
        {
            double a = angleDeg * Math.PI / 180.0;
            return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }

        private Color ArcColor(double f) =>
            f >= 1.0    ? ThemeColor("Danger",      Color.FromRgb(0xE7, 0x4C, 0x3C))
          : f >= WarnAt ? ThemeColor("WarningColor", Color.FromRgb(0xF0, 0xA0, 0x2E))
          :               ThemeColor("Accent",       Color.FromRgb(0x3D, 0x8B, 0xFD));

        private Color TrackColor()
        {
            var b = ThemeColor("TextMuted", Color.FromRgb(0x80, 0x80, 0x80));
            return Color.FromArgb(0x38, b.R, b.G, b.B);   // ~22% alpha
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
    }
}
