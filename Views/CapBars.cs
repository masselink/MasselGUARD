using System;
using System.Windows;
using System.Windows.Media;

namespace MasselGUARD.Views
{
    /// <summary>
    /// Thin horizontal progress-bar variant of <see cref="CapRings"/> — up to three slim stacked
    /// bars (day · week · month), each shown only when its cap is set, filling 0→100% as usage → cap
    /// (accent, amber ≥85%, red ≥100%). No inline text: the day/week/month breakdown and exact
    /// figures live in the hover tooltip (<c>CapRingsTooltip</c>). Disconnected tunnels render greyed
    /// (still at real usage). Same dependency-property interface as <see cref="CapRings"/>, so the two
    /// are interchangeable in the row template. Purely presentational (OnRender), theme-aware.
    /// </summary>
    public sealed class CapBars : FrameworkElement
    {
        private const double BarWidth  = 120.0;
        private const double BarHeight = 6.0;
        private const double BarGap    = 3.0;
        private const double Radius    = 3.0;
        private const double WarnAt    = 0.85;

        public static readonly DependencyProperty DayFractionProperty =
            DependencyProperty.Register(nameof(DayFraction), typeof(double), typeof(CapBars),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty WeekFractionProperty =
            DependencyProperty.Register(nameof(WeekFraction), typeof(double), typeof(CapBars),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty MonthFractionProperty =
            DependencyProperty.Register(nameof(MonthFraction), typeof(double), typeof(CapBars),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty DaySetProperty =
            DependencyProperty.Register(nameof(DaySet), typeof(bool), typeof(CapBars),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty WeekSetProperty =
            DependencyProperty.Register(nameof(WeekSet), typeof(bool), typeof(CapBars),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty MonthSetProperty =
            DependencyProperty.Register(nameof(MonthSet), typeof(bool), typeof(CapBars),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ActiveProperty =
            DependencyProperty.Register(nameof(Active), typeof(bool), typeof(CapBars),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        public double DayFraction   { get => (double)GetValue(DayFractionProperty);   set => SetValue(DayFractionProperty, value); }
        public double WeekFraction  { get => (double)GetValue(WeekFractionProperty);  set => SetValue(WeekFractionProperty, value); }
        public double MonthFraction { get => (double)GetValue(MonthFractionProperty); set => SetValue(MonthFractionProperty, value); }
        public bool   DaySet        { get => (bool)GetValue(DaySetProperty);          set => SetValue(DaySetProperty, value); }
        public bool   WeekSet       { get => (bool)GetValue(WeekSetProperty);         set => SetValue(WeekSetProperty, value); }
        public bool   MonthSet      { get => (bool)GetValue(MonthSetProperty);        set => SetValue(MonthSetProperty, value); }
        public bool   Active        { get => (bool)GetValue(ActiveProperty);          set => SetValue(ActiveProperty, value); }

        protected override Size MeasureOverride(Size availableSize)
        {
            int n = (DaySet ? 1 : 0) + (WeekSet ? 1 : 0) + (MonthSet ? 1 : 0);
            if (n == 0) return new Size(0, 0);
            return new Size(BarWidth, n * BarHeight + (n - 1) * BarGap);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;

            // Transparent fill so the whole control is hit-testable (tooltip over the gaps too).
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            var periods = new (bool Set, double Frac)[]
            {
                (DaySet,   DayFraction),
                (WeekSet,  WeekFraction),
                (MonthSet, MonthFraction),
            };

            int n = 0;
            foreach (var p in periods) if (p.Set) n++;
            if (n == 0) return;

            double bw    = Math.Min(BarWidth, w);
            double total = n * BarHeight + (n - 1) * BarGap;
            double y     = (h - total) / 2;                 // vertically centre the stack
            var trackBrush = new SolidColorBrush(TrackColor()); trackBrush.Freeze();

            foreach (var p in periods)
            {
                if (!p.Set) continue;
                DrawBar(dc, new Rect(0, y, bw, BarHeight), p.Frac, trackBrush);
                y += BarHeight + BarGap;
            }
        }

        private void DrawBar(DrawingContext dc, Rect bar, double frac, Brush trackBrush)
        {
            var trackGeo = new RectangleGeometry(bar, Radius, Radius);
            trackGeo.Freeze();
            dc.DrawGeometry(trackBrush, null, trackGeo);

            double f = Math.Max(0.0, Math.Min(1.0, frac));
            if (f <= 0) return;

            double fillW = Math.Max(bar.Height, bar.Width * f);   // keep the rounded cap visible
            fillW = Math.Min(fillW, bar.Width);
            var fillGeo = new RectangleGeometry(new Rect(bar.X, bar.Y, fillW, bar.Height), Radius, Radius);
            fillGeo.Freeze();

            var fill = new SolidColorBrush(Active ? StateColor(frac) : InactiveColor());
            fill.Freeze();
            dc.PushClip(trackGeo);
            dc.DrawGeometry(fill, null, fillGeo);
            dc.Pop();
        }

        private Color StateColor(double f) =>
            f >= 1.0    ? ThemeColor("Danger",       Color.FromRgb(0xE7, 0x4C, 0x3C))
          : f >= WarnAt ? ThemeColor("WarningColor", Color.FromRgb(0xF0, 0xA0, 0x2E))
          :               ThemeColor("Accent",       Color.FromRgb(0x3D, 0x8B, 0xFD));

        private Color InactiveColor()
        {
            var b = ThemeColor("TextMuted", Color.FromRgb(0x80, 0x80, 0x80));
            return Color.FromArgb(0xB0, b.R, b.G, b.B);
        }

        private Color TrackColor()
        {
            var b = ThemeColor("TextMuted", Color.FromRgb(0x80, 0x80, 0x80));
            return Color.FromArgb(0x2E, b.R, b.G, b.B);
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
