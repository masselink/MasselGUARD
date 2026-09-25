using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MasselGUARD
{
    /// <summary>
    /// Adorner that draws a 2 px horizontal line at a given Y position inside
    /// the tunnel ListView to indicate the drag-drop insertion point.
    /// </summary>
    public class DropLineAdorner : Adorner
    {
        private double _y;
        private readonly Pen _pen;

        public DropLineAdorner(UIElement adornedElement) : base(adornedElement)
        {
            IsHitTestVisible = false;

            // Use Accent brush if available, otherwise a bright blue
            Brush brush;
            try { brush = (Brush)Application.Current.FindResource("Accent"); }
            catch { brush = Brushes.CornflowerBlue; }

            _pen = new Pen(brush, 2) { DashStyle = DashStyles.Solid };
            _pen.Freeze();
        }

        public void SetY(double y)
        {
            _y = y;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = AdornedElement is FrameworkElement fe ? fe.ActualWidth : 200;

            // Small circles at both ends of the line - looks like a professional drop indicator
            double r = 4;
            dc.DrawEllipse(_pen.Brush, null, new Point(r, _y), r, r);
            dc.DrawEllipse(_pen.Brush, null, new Point(width - r, _y), r, r);
            dc.DrawLine(_pen, new Point(r * 2, _y), new Point(width - r * 2, _y));
        }
    }
}
