using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace MasselGUARD.Views
{
    public partial class TrayToast : Window
    {
        public TrayToast(string message)
        {
            InitializeComponent();
            MessageLabel.Text = message;
            PositionNearTray();
            Opacity = 0;
            Loaded += (_, _) => FadeIn();
        }

        // Position the toast in the bottom-right corner above the taskbar.
        private void PositionNearTray()
        {
            // Use WPF screen area — works on all DPI settings
            var area = SystemParameters.WorkArea;
            Left = area.Right  - Width  - 12;
            Top  = area.Bottom - 80;  // will be adjusted after height is known
            SizeChanged += (_, _) =>
                Top = area.Bottom - ActualHeight - 12;
        }

        private void FadeIn()
        {
            var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200));
            BeginAnimation(OpacityProperty, anim);
        }

        public void FadeAndClose()
        {
            var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            anim.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, anim);
        }
    }
}
