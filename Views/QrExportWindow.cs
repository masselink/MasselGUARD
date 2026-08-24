using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MasselGUARD.Views
{
    /// <summary>
    /// Displays a QR code encoding a tunnel's WireGuard configuration so it can be
    /// scanned into the WireGuard mobile app. Code-only (no XAML) — mirrors the
    /// inline-window pattern used elsewhere. The QR is drawn on a white background
    /// with black modules regardless of theme so it always scans.
    /// </summary>
    internal sealed class QrExportWindow : Window
    {
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

        private readonly System.Drawing.Bitmap _bitmap;
        private readonly string _tunnelName;

        public QrExportWindow(string tunnelName, string configText)
        {
            _tunnelName = tunnelName;
            _bitmap     = Encode(configText);

            Brush Res(string key) =>
                (Application.Current.Resources[key] as Brush) ?? Brushes.Gray;
            var ff = Application.Current.Resources["Theme.FontFamily"] as FontFamily
                     ?? new FontFamily("Segoe UI");

            WindowStyle           = WindowStyle.None;
            AllowsTransparency    = true;
            Background            = Brushes.Transparent;
            SizeToContent         = SizeToContent.WidthAndHeight;
            ResizeMode            = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var border = new Border
            {
                Background      = Res("WindowBg"),
                BorderBrush     = Res("Accent"),
                BorderThickness = new Thickness(1),
                CornerRadius    = Application.Current.Resources["Theme.CornerRadius"] is CornerRadius cr ? cr : new CornerRadius(6),
                Padding         = new Thickness(20),
            };
            var panel = new StackPanel { Width = 340 };

            // Title (draggable)
            var title = new TextBlock
            {
                Text       = Lang.T("QrExportTitle", tunnelName),
                FontFamily = ff, FontSize = 12, FontWeight = FontWeights.Bold,
                Foreground = Res("Accent"),
                Margin     = new Thickness(0, 0, 0, 12),
            };
            title.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
            panel.Children.Add(title);

            // QR image on a white card so dark themes don't invert it
            var qrCard = new Border
            {
                Background           = Brushes.White,
                CornerRadius         = new CornerRadius(4),
                Padding              = new Thickness(10),
                HorizontalAlignment  = HorizontalAlignment.Center,
                Child = new Image
                {
                    Source  = ToBitmapSource(_bitmap),
                    Width   = 280,
                    Height  = 280,
                    Stretch = Stretch.Uniform,
                },
            };
            panel.Children.Add(qrCard);

            panel.Children.Add(new TextBlock
            {
                Text         = Lang.T("QrExportHint"),
                FontFamily   = ff, FontSize = 10,
                Foreground   = Res("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 12, 0, 2),
            });
            panel.Children.Add(new TextBlock
            {
                Text         = Lang.T("QrExportWarning"),
                FontFamily   = ff, FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground   = Res("WarningColor"),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 14),
            });

            // Buttons
            var btns = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var btnSave = new Button
            {
                Content = Lang.T("QrExportSave"),
                Style   = Application.Current.Resources["FlatBtn"] as Style,
                Padding = new Thickness(14, 6, 14, 6),
                Margin  = new Thickness(0, 0, 8, 0),
            };
            var btnClose = new Button
            {
                Content = Lang.T("BtnClose"),
                Style   = Application.Current.Resources["PrimaryBtn"] as Style,
                Padding = new Thickness(14, 6, 14, 6),
            };
            btnSave.Click  += (_, _) => SavePng();
            btnClose.Click += (_, _) => Close();
            btns.Children.Add(btnSave);
            btns.Children.Add(btnClose);
            panel.Children.Add(btns);

            border.Child = panel;
            Content = border;

            Closed += (_, _) => { try { _bitmap.Dispose(); } catch { } };
        }

        private static System.Drawing.Bitmap Encode(string text)
        {
            var writer = new ZXing.Windows.Compatibility.BarcodeWriter
            {
                Format  = ZXing.BarcodeFormat.QR_CODE,
                Options = new ZXing.Common.EncodingOptions
                {
                    Width  = 560,
                    Height = 560,
                    Margin = 1,
                },
            };
            return writer.Write(text);
        }

        private void SavePng()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title    = Lang.T("QrExportSave"),
                Filter   = "PNG image (*.png)|*.png",
                FileName = $"{_tunnelName}-qr.png",
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                _bitmap.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Lang.T("QrExportTitle", _tunnelName),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
        {
            IntPtr hBitmap = bmp.GetHbitmap();
            try
            {
                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
            finally { DeleteObject(hBitmap); }
        }
    }
}
