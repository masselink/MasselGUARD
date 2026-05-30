using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MasselGUARD.Views
{
    public partial class RuleDialog : Window
    {
        public string ResultName   { get; private set; } = "";
        public string ResultSsid   { get; private set; } = "";
        public string ResultTunnel { get; private set; } = "";
        /// <summary>
        /// New counter value to persist. -1 = no change; 0 = cleared; any positive = new value.
        /// </summary>
        public int ResultNewCounterValue { get; private set; } = -1;

        private readonly string? _currentSsid;
        private bool _nameManuallyEdited = false;
        private int  _displayCount;

        public RuleDialog(string? currentSsid,
                          string existingName   = "",
                          string existingSsid   = "",
                          string existingTunnel = "",
                          int    executionCount = -1,   // -1 = add mode (counter hidden)
                          List<string>? tunnels = null)
        {
            InitializeComponent();
            _currentSsid  = currentSsid;
            _displayCount = executionCount;

            // Populate tunnel dropdown
            TunnelBox.Items.Clear();
            TunnelBox.Items.Add("");   // blank = disconnect
            if (tunnels != null)
                foreach (var t in tunnels) TunnelBox.Items.Add(t);

            if (!string.IsNullOrEmpty(existingSsid))
            {
                DialogTitle.Text    = Lang.T("RuleDialogEditTitle");
                SsidBox.Text        = existingSsid;
                _nameManuallyEdited = !string.IsNullOrEmpty(existingName);
                NameBox.Text        = existingName;
            }

            TunnelBox.Text = existingTunnel;

            // Show trigger counter only in edit mode
            if (executionCount >= 0)
            {
                CounterRow.Visibility = Visibility.Visible;
                UpdateCountLabel();
            }

            NameBox.Focus();
        }

        private void UpdateCountLabel()
        {
            if (TriggerCountLabel == null) return;
            TriggerCountLabel.Text = _displayCount == 1
                ? Lang.T("RuleDialogTriggerCountSingular")
                : Lang.T("RuleDialogTriggerCount", _displayCount);
        }

        private void SetCounter_Click(object sender, RoutedEventArgs e)
        {
            // Build a small themed input dialog inline (same pattern as ShowThemedYesNo)
            int? result = ShowCounterInputDialog(_displayCount);
            if (result == null) return;   // cancelled

            _displayCount        = result.Value;
            ResultNewCounterValue = result.Value;
            UpdateCountLabel();
        }

        /// <summary>
        /// Shows a themed modal input dialog for editing the counter value.
        /// Returns the entered value, or null if cancelled.
        /// </summary>
        private int? ShowCounterInputDialog(int currentValue)
        {
            Brush Res(string key) =>
                (Application.Current.Resources[key] as Brush)
                ?? Brushes.Gray;

            var ff = Application.Current.Resources["Theme.FontFamily"] as FontFamily
                     ?? new FontFamily("Segoe UI");

            int? result = null;

            var win = new Window
            {
                WindowStyle           = WindowStyle.None,
                AllowsTransparency    = true,
                Background            = Brushes.Transparent,
                Width                 = 320,
                SizeToContent         = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = this,
                ResizeMode            = ResizeMode.NoResize,
            };

            var border = new Border
            {
                Background      = Res("WindowBg"),
                BorderBrush     = Res("BorderColor"),
                BorderThickness = new Thickness(1),
                CornerRadius    = Application.Current.Resources["Theme.CornerRadius"] is CornerRadius cr ? cr : new CornerRadius(6),
                Padding         = new Thickness(20),
            };

            var panel = new StackPanel();

            // Title
            panel.Children.Add(new TextBlock
            {
                Text       = Lang.T("RuleDialogSetCounterTitle"),
                FontFamily = ff, FontSize = 12, FontWeight = FontWeights.Bold,
                Foreground = Res("TextPrimary"),
                Margin     = new Thickness(0, 0, 0, 8),
            });

            // Hint
            panel.Children.Add(new TextBlock
            {
                Text         = Lang.T("RuleDialogSetCounterHint"),
                FontFamily   = ff, FontSize = 10,
                Foreground   = Res("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 12),
            });

            // TextBox — pre-filled with current count, digits only
            var input = new TextBox
            {
                Text              = currentValue.ToString(),
                FontFamily        = ff, FontSize = 13,
                MaxLength         = 7,
                SelectionStart    = 0,
                SelectionLength   = currentValue.ToString().Length,
                Margin            = new Thickness(0, 0, 0, 16),
            };
            input.PreviewTextInput += (_, te) =>
                te.Handled = !System.Linq.Enumerable.All(te.Text, char.IsDigit);
            panel.Children.Add(input);

            // Buttons row
            var btns = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };

            var btnCancel = new Button
            {
                Content = Lang.T("BtnCancel"),
                Style   = (Style)Application.Current.Resources["FlatBtn"],
                Padding = new Thickness(14, 6, 14, 6),
                Margin  = new Thickness(0, 0, 8, 0),
            };
            var btnApply = new Button
            {
                Content = Lang.T("BtnOk"),
                Style   = (Style)Application.Current.Resources["PrimaryBtn"],
                Padding = new Thickness(14, 6, 14, 6),
            };

            void Apply()
            {
                var text = input.Text.Trim();
                if (!int.TryParse(text, out var val) || val < 0) return;
                result = val;
                win.Close();
            }

            btnCancel.Click   += (_, _) => win.Close();
            btnApply.Click    += (_, _) => Apply();
            input.KeyDown     += (_, ke) => { if (ke.Key == Key.Return) Apply(); };

            btns.Children.Add(btnCancel);
            btns.Children.Add(btnApply);
            panel.Children.Add(btns);

            border.Child = panel;
            win.Content  = border;

            // Select all text when the dialog opens
            win.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };

            win.ShowDialog();
            return result;
        }

        /// <summary>Auto-generate name from SSID and tunnel unless user has typed one.</summary>
        private void AutoGenerateName()
        {
            if (_nameManuallyEdited) return;
            var ssid   = SsidBox.Text.Trim();
            var tunnel = TunnelBox.Text.Trim();
            string generated;
            if (string.IsNullOrEmpty(ssid))
                generated = "";
            else if (string.IsNullOrEmpty(tunnel))
                generated = $"{ssid} → disconnect";
            else
                generated = $"{ssid} → {tunnel}";

            NameBox.TextChanged -= NameBox_TextChanged;
            NameBox.Text = generated;
            NameBox.TextChanged += NameBox_TextChanged;
        }

        private void NameBox_TextChanged(object sender,
            System.Windows.Controls.TextChangedEventArgs e)
            => _nameManuallyEdited = !string.IsNullOrEmpty(NameBox.Text);

        private void SsidBox_TextChanged(object sender,
            System.Windows.Controls.TextChangedEventArgs e)
            => AutoGenerateName();

        private void TunnelBox_Changed(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
            => AutoGenerateName();

        private void UseCurrent_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentSsid))
                SsidBox.Text = _currentSsid;
            else
                MessageBox.Show(
                    Lang.T("RuleDialogNoWifi"),
                    Lang.T("RuleDialogNoWifiTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var ssid = SsidBox.Text.Trim();
            if (string.IsNullOrEmpty(ssid))
            {
                MessageBox.Show(
                    Lang.T("RuleDialogSsidRequired"),
                    Lang.T("RuleDialogValidationTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                SsidBox.Focus();
                return;
            }
            ResultSsid   = ssid;
            ResultTunnel = TunnelBox.Text.Trim();
            var name = NameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
                name = string.IsNullOrEmpty(ResultTunnel)
                    ? $"{ssid} → disconnect"
                    : $"{ssid} → {ResultTunnel}";
            ResultName   = name;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private void Title_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }
    }
}
