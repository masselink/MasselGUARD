using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MasselGUARD.Models;

namespace MasselGUARD.Views
{
    /// <summary>
    /// Themed modal editor for a <see cref="DnsProfile"/>, built in code (same pattern as
    /// RuleDialog's counter dialog) so it needs no new XAML/window chrome. Returns the edited
    /// profile, or null if cancelled. Editing preserves the profile's <see cref="DnsProfile.Id"/>.
    /// </summary>
    public static class DnsProfileEditor
    {
        public static DnsProfile? Show(Window owner, DnsProfile? existing)
        {
            Brush Res(string key) => (Application.Current.Resources[key] as Brush) ?? Brushes.Gray;
            var ff = Application.Current.Resources["Theme.FontFamily"] as FontFamily ?? new FontFamily("Segoe UI");

            var model = existing?.Clone() ?? new DnsProfile();
            DnsProfile? result = null;

            var win = new Window
            {
                WindowStyle           = WindowStyle.None,
                AllowsTransparency    = true,
                Background            = Brushes.Transparent,
                Width                 = 380,
                SizeToContent         = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = owner,
                ResizeMode            = ResizeMode.NoResize,
            };

            var border = new Border
            {
                Background      = Res("WindowBg"),
                BorderBrush     = Res("Accent"),
                BorderThickness = new Thickness(1),
                CornerRadius    = Application.Current.Resources["Theme.CornerRadius"] is CornerRadius cr ? cr : new CornerRadius(6),
                Padding         = new Thickness(20),
            };
            var panel = new StackPanel();

            // Title (draggable)
            var title = new TextBlock
            {
                Text       = Lang.T(existing == null ? "DnsProfileDialogAddTitle" : "DnsProfileDialogEditTitle"),
                FontFamily = ff, FontSize = 13, FontWeight = FontWeights.Bold,
                Foreground = Res("TextPrimary"), Margin = new Thickness(0, 0, 0, 12),
            };
            title.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) win.DragMove(); };
            panel.Children.Add(title);

            TextBox Field(string labelKey, string value, bool wide = true)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = Lang.T(labelKey), FontFamily = ff, FontSize = 10,
                    Foreground = Res("TextMuted"), Margin = new Thickness(0, 0, 0, 2),
                });
                var tb = new TextBox
                {
                    Text = value, FontFamily = ff, FontSize = 12,
                    Margin = new Thickness(0, 0, 0, 10),
                };
                panel.Children.Add(tb);
                return tb;
            }

            var nameBox = Field("DnsProfileName", model.Name);

            // v4 / v6 primary+secondary in two columns
            Grid TwoCol(string lk1, string v1, out TextBox b1, string lk2, string v2, out TextBox b2)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 10) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                StackPanel Col(string lk, string v, out TextBox box)
                {
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = Lang.T(lk), FontFamily = ff, FontSize = 10, Foreground = Res("TextMuted"), Margin = new Thickness(0, 0, 0, 2) });
                    box = new TextBox { Text = v, FontFamily = ff, FontSize = 12 };
                    sp.Children.Add(box);
                    return sp;
                }
                var c1 = Col(lk1, v1, out b1); Grid.SetColumn(c1, 0); g.Children.Add(c1);
                var c2 = Col(lk2, v2, out b2); Grid.SetColumn(c2, 2); g.Children.Add(c2);
                return g;
            }

            panel.Children.Add(TwoCol("DnsProfileV4Primary", model.V4Primary, out var v4p,
                                      "DnsProfileV4Secondary", model.V4Secondary, out var v4s));
            panel.Children.Add(TwoCol("DnsProfileV6Primary", model.V6Primary, out var v6p,
                                      "DnsProfileV6Secondary", model.V6Secondary, out var v6s));

            // Encryption combo
            panel.Children.Add(new TextBlock { Text = Lang.T("DnsProfileEncryption"), FontFamily = ff, FontSize = 10, Foreground = Res("TextMuted"), Margin = new Thickness(0, 0, 0, 2) });
            var encBox = new ComboBox { FontFamily = ff, FontSize = 12, Margin = new Thickness(0, 0, 0, 10) };
            encBox.Items.Add(new ComboBoxItem { Content = Lang.T("DnsProfileEncryptionPlain"), Tag = "plain" });
            encBox.Items.Add(new ComboBoxItem { Content = Lang.T("DnsProfileEncryptionDoh"),   Tag = "doh" });
            encBox.Items.Add(new ComboBoxItem { Content = Lang.T("DnsProfileEncryptionAuto"),  Tag = "auto" });
            encBox.SelectedIndex = model.Encryption switch { "doh" => 1, "auto" => 2, _ => 0 };
            panel.Children.Add(encBox);

            // DoH template + require-encryption (shown only when encrypting)
            var dohLabel = new TextBlock { Text = Lang.T("DnsProfileDohTemplate"), FontFamily = ff, FontSize = 10, Foreground = Res("TextMuted"), Margin = new Thickness(0, 0, 0, 2) };
            var dohBox   = new TextBox { Text = model.DohTemplate, FontFamily = ff, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
            var reqChk   = new CheckBox { Content = Lang.T("DnsProfileRequireEncryption"), FontFamily = ff, FontSize = 11, Foreground = Res("TextPrimary"), IsChecked = model.RequireEncryption, Margin = new Thickness(0, 0, 0, 8) };
            var dohNote  = new TextBlock { Text = Lang.T("DnsProfileDohNote"), FontFamily = ff, FontSize = 9, Foreground = Res("TextMuted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(dohLabel); panel.Children.Add(dohBox); panel.Children.Add(reqChk); panel.Children.Add(dohNote);

            void SyncEnc()
            {
                bool enc = (encBox.SelectedItem as ComboBoxItem)?.Tag as string != "plain";
                dohLabel.Visibility = dohBox.Visibility = reqChk.Visibility = dohNote.Visibility =
                    enc ? Visibility.Visible : Visibility.Collapsed;
            }
            encBox.SelectionChanged += (_, _) => SyncEnc();
            SyncEnc();

            // Buttons
            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = Lang.T("BtnCancel"), Style = (Style)Application.Current.Resources["FlatBtn"], Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
            var save   = new Button { Content = Lang.T("BtnSave"),   Style = (Style)Application.Current.Resources["PrimaryBtn"], Padding = new Thickness(14, 6, 14, 6) };

            void Save()
            {
                var name = nameBox.Text.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    MessageBox.Show(Lang.T("DnsProfileNameRequired"), Lang.T("RuleDialogValidationTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                bool anyServer = new[] { v4p.Text, v4s.Text, v6p.Text, v6s.Text }
                    .Any(s => !string.IsNullOrWhiteSpace(s));
                if (!anyServer)
                {
                    MessageBox.Show(Lang.T("DnsProfileServerRequired"), Lang.T("RuleDialogValidationTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                model.Name              = name;
                model.V4Primary         = v4p.Text.Trim();
                model.V4Secondary       = v4s.Text.Trim();
                model.V6Primary         = v6p.Text.Trim();
                model.V6Secondary       = v6s.Text.Trim();
                model.Encryption        = (encBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "plain";
                model.DohTemplate       = dohBox.Text.Trim();
                model.RequireEncryption = reqChk.IsChecked == true;
                result = model;
                win.Close();
            }

            cancel.Click += (_, _) => win.Close();
            save.Click   += (_, _) => Save();
            btns.Children.Add(cancel); btns.Children.Add(save);
            panel.Children.Add(btns);

            border.Child = panel;
            win.Content  = border;
            win.Loaded  += (_, _) => nameBox.Focus();
            win.ShowDialog();
            return result;
        }
    }
}
