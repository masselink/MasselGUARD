using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MasselGUARD.Views
{
    /// <summary>
    /// Minimal themed single-line text prompt. Returns the entered text via
    /// <see cref="Ask"/>, or <c>null</c> if the user cancels. Used for the
    /// "save under a new name" step when an imported tunnel name collides.
    /// Code-only, mirroring <see cref="PasswordPromptWindow"/>.
    /// </summary>
    internal sealed class TextPromptWindow : Window
    {
        private readonly TextBox _box;
        public string? Result { get; private set; }

        private TextPromptWindow(string title, string prompt, string initial, string? error)
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
            var panel = new StackPanel { Width = 340 };

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
                Margin = new Thickness(0, 0, 0, 8),
            });

            _box = new TextBox
            {
                Text = initial ?? "", FontFamily = ff, FontSize = 12,
                Padding = new Thickness(6, 4, 6, 4),
                Foreground = Res("TextPrimary"), Background = Res("CardBg"),
            };
            _box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };
            panel.Children.Add(_box);

            if (!string.IsNullOrEmpty(error))
                panel.Children.Add(new TextBlock
                {
                    Text = error, FontFamily = ff, FontSize = 10,
                    Foreground = Res("Danger"), TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 0),
                });

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
            Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
        }

        private void Accept()
        {
            Result = _box.Text;
            DialogResult = true;
            Close();
        }

        /// <summary>Show the prompt; returns the entered text, or null if cancelled.</summary>
        public static string? Ask(Window? owner, string title, string prompt, string initial = "", string? error = null)
        {
            var dlg = new TextPromptWindow(title, prompt, initial, error) { Owner = owner };
            return dlg.ShowDialog() == true ? dlg.Result : null;
        }
    }
}
