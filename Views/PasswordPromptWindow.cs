using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MasselGUARD.Views
{
    /// <summary>
    /// Minimal themed single-field password prompt. Returns the entered text via
    /// <see cref="Ask"/>, or <c>null</c> if the user cancels. Used when importing
    /// a password-encrypted (<c>.mgconf</c>) tunnel. Code-only, mirroring the
    /// other inline dialogs.
    /// </summary>
    internal sealed class PasswordPromptWindow : Window
    {
        private readonly PasswordBox _pwBox;
        public string? Result { get; private set; }

        private PasswordPromptWindow(string title, string prompt)
        {
            Brush Res(string key) => (Application.Current.Resources[key] as Brush) ?? Brushes.Gray;
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
            var panel = new StackPanel { Width = 320 };

            var titleTb = new TextBlock
            {
                Text = title, FontFamily = ff, FontSize = 13, FontWeight = FontWeights.Bold,
                Foreground = Res("Accent"), Margin = new Thickness(0, 0, 0, 10),
            };
            titleTb.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
            panel.Children.Add(titleTb);

            panel.Children.Add(new TextBlock
            {
                Text = prompt, FontFamily = ff, FontSize = 11,
                Foreground = Res("TextMuted"), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });

            _pwBox = new PasswordBox
            {
                FontFamily = ff, FontSize = 12,
                Padding    = new Thickness(6, 4, 6, 4),
                Foreground = Res("TextPrimary"),
                Background = Res("CardBg"),
            };
            _pwBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };
            panel.Children.Add(_pwBox);

            var btns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            var btnCancel = new Button
            {
                Content = Lang.T("BtnCancel"),
                Style   = Application.Current.Resources["FlatBtn"] as Style,
                Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0),
            };
            var btnOk = new Button
            {
                Content = Lang.T("BtnOk"),
                Style   = Application.Current.Resources["PrimaryBtn"] as Style,
                Padding = new Thickness(14, 6, 14, 6),
            };
            btnCancel.Click += (_, _) => { DialogResult = false; Close(); };
            btnOk.Click     += (_, _) => Accept();
            btns.Children.Add(btnCancel);
            btns.Children.Add(btnOk);
            panel.Children.Add(btns);

            border.Child = panel;
            Content = border;
            Loaded += (_, _) => _pwBox.Focus();
        }

        private void Accept()
        {
            Result = _pwBox.Password;
            DialogResult = true;
            Close();
        }

        /// <summary>Show the prompt; returns the entered password, or null if cancelled.</summary>
        public static string? Ask(Window? owner, string title, string prompt)
        {
            var dlg = new PasswordPromptWindow(title, prompt) { Owner = owner };
            return dlg.ShowDialog() == true ? dlg.Result : null;
        }
    }
}
