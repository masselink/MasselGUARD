using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace MasselGUARD.Views
{
    public partial class TrayToast : Window
    {
        public TrayToast(string message)
        {
            InitializeComponent();
            MessageLabel.Text = message;
            LoadAppIcon();
            PositionNearTray();
            Opacity = 0;
            Loaded += (_, _) => FadeIn();
        }

        private void LoadAppIcon()
        {
            try
            {
                // Load the application .ico as a WPF image source
                var uri = new Uri("pack://application:,,,/MasselGUARD.ico", UriKind.Absolute);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource       = uri;
                bitmap.DecodePixelWidth = 16;
                bitmap.EndInit();
                AppIcon.Source = bitmap;
            }
            catch { /* icon not critical - toast still shows */ }
        }

        private void PositionNearTray()
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 12;
            Top  = area.Bottom - 100;
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
            var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350));
            anim.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, anim);
        }
    }
}
