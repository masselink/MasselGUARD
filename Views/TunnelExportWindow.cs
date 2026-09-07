using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MasselGUARD.Models;
using MasselGUARD.Services;

namespace MasselGUARD.Views
{
    /// <summary>
    /// "Export tunnel" dialog. Offers three formats — a plain <c>.conf</c>, a
    /// password-encrypted <c>.mgconf</c>, or a QR code — with an option to embed
    /// the tunnel's MasselGUARD-specific settings (group, scripts, kill-switch,
    /// auto-reconnect, data cap, notes). QR is standard-only, so it can never
    /// carry the extras; that checkbox greys out when QR is picked.
    ///
    /// Code-only (no XAML), mirroring <see cref="QrExportWindow"/>. The encrypted
    /// path uses <see cref="TunnelExportService"/> (AES-256-GCM / PBKDF2) which is
    /// portable across machines, unlike the DPAPI at-rest storage format.
    /// </summary>
    internal sealed class TunnelExportWindow : Window
    {
        private readonly StoredTunnel _tunnel;
        private readonly string       _config;

        private readonly RadioButton _rbPlain;
        private readonly RadioButton _rbEncrypted;
        private readonly RadioButton _rbQr;
        private readonly CheckBox    _cbSettings;
        private readonly StackPanel  _pwPanel;
        private readonly PasswordBox _pwBox;
        private readonly PasswordBox _pwConfirm;
        private readonly TextBlock   _errorTb;

        public TunnelExportWindow(StoredTunnel tunnel, string config)
        {
            _tunnel = tunnel;
            _config = config;

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
            var panel = new StackPanel { Width = 360 };

            // Title (draggable)
            var title = new TextBlock
            {
                Text       = Lang.T("ExportTunnelTitle", tunnel.Name),
                FontFamily = ff, FontSize = 13, FontWeight = FontWeights.Bold,
                Foreground = Res("Accent"),
                Margin     = new Thickness(0, 0, 0, 14),
            };
            title.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
            panel.Children.Add(title);

            // ── Format section ────────────────────────────────────────────────
            panel.Children.Add(SectionLabel(Lang.T("ExportFormatLabel"), ff, Res("TextMuted")));

            _rbPlain     = FormatRadio(Lang.T("ExportFormatPlain"),     Lang.T("ExportFormatPlainHint"),     ff, Res);
            _rbEncrypted = FormatRadio(Lang.T("ExportFormatEncrypted"), Lang.T("ExportFormatEncryptedHint"), ff, Res);
            _rbQr        = FormatRadio(Lang.T("ExportFormatQr"),        Lang.T("ExportFormatQrHint"),        ff, Res);
            _rbPlain.IsChecked = true;
            _rbPlain.Checked     += (_, _) => OnFormatChanged();
            _rbEncrypted.Checked += (_, _) => OnFormatChanged();
            _rbQr.Checked        += (_, _) => OnFormatChanged();
            panel.Children.Add(_rbPlain);
            panel.Children.Add(_rbEncrypted);
            panel.Children.Add(_rbQr);

            // ── Include-settings checkbox ─────────────────────────────────────
            _cbSettings = new CheckBox
            {
                Content    = Lang.T("ExportIncludeSettings"),
                FontFamily = ff, FontSize = 11,
                Foreground = Res("TextPrimary"),
                Margin     = new Thickness(0, 12, 0, 0),
                IsChecked  = false,
            };
            panel.Children.Add(_cbSettings);
            panel.Children.Add(new TextBlock
            {
                Text         = Lang.T("ExportIncludeSettingsHint"),
                FontFamily   = ff, FontSize = 10,
                Foreground   = Res("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(24, 2, 0, 0),
            });

            // ── Password panel (encrypted only) ───────────────────────────────
            _pwPanel = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
            _pwPanel.Children.Add(SectionLabel(Lang.T("ExportPasswordLabel"), ff, Res("TextMuted")));
            _pwBox     = MakePasswordBox(ff, Res);
            _pwConfirm = MakePasswordBox(ff, Res);
            _pwBox.Margin     = new Thickness(0, 4, 0, 6);
            _pwConfirm.Margin = new Thickness(0, 0, 0, 4);
            _pwPanel.Children.Add(WithPlaceholder(_pwBox,     Lang.T("ExportPasswordPlaceholder"),        ff, Res));
            _pwPanel.Children.Add(WithPlaceholder(_pwConfirm, Lang.T("ExportPasswordConfirmPlaceholder"), ff, Res));
            _pwPanel.Children.Add(new TextBlock
            {
                Text         = Lang.T("ExportPasswordHint"),
                FontFamily   = ff, FontSize = 10,
                Foreground   = Res("TextMuted"),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 2, 0, 0),
            });
            panel.Children.Add(_pwPanel);

            // ── Private-key warning ───────────────────────────────────────────
            panel.Children.Add(new TextBlock
            {
                Text         = Lang.T("ExportPrivateKeyWarning"),
                FontFamily   = ff, FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground   = Res("WarningColor"),
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 14, 0, 0),
            });

            // ── Inline error line ─────────────────────────────────────────────
            _errorTb = new TextBlock
            {
                FontFamily   = ff, FontSize = 10,
                Foreground   = Res("Danger"),
                TextWrapping = TextWrapping.Wrap,
                Visibility   = Visibility.Collapsed,
                Margin       = new Thickness(0, 8, 0, 0),
            };
            panel.Children.Add(_errorTb);

            // ── Buttons ───────────────────────────────────────────────────────
            var btns = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin              = new Thickness(0, 16, 0, 0),
            };
            var btnCancel = new Button
            {
                Content = Lang.T("BtnCancel"),
                Style   = Application.Current.Resources["FlatBtn"] as Style,
                Padding = new Thickness(14, 6, 14, 6),
                Margin  = new Thickness(0, 0, 8, 0),
            };
            var btnExport = new Button
            {
                Content = Lang.T("BtnExport"),
                Style   = Application.Current.Resources["PrimaryBtn"] as Style,
                Padding = new Thickness(14, 6, 14, 6),
            };
            btnCancel.Click += (_, _) => Close();
            btnExport.Click += (_, _) => DoExport();
            btns.Children.Add(btnCancel);
            btns.Children.Add(btnExport);
            panel.Children.Add(btns);

            border.Child = panel;
            Content = border;

            OnFormatChanged();
        }

        // ── Format switching ──────────────────────────────────────────────────
        private void OnFormatChanged()
        {
            bool encrypted = _rbEncrypted.IsChecked == true;
            bool qr        = _rbQr.IsChecked == true;

            _pwPanel.Visibility = encrypted ? Visibility.Visible : Visibility.Collapsed;

            // QR is standard-only — it cannot carry MasselGUARD settings.
            _cbSettings.IsEnabled = !qr;
            if (qr) _cbSettings.IsChecked = false;

            _errorTb.Visibility = Visibility.Collapsed;
        }

        // ── Export ────────────────────────────────────────────────────────────
        private void DoExport()
        {
            if (_rbQr.IsChecked == true)
            {
                // QR encodes the plain WireGuard config only; hand off to the
                // existing QR window and close this dialog.
                var qr = new QrExportWindow(_tunnel.Name, _config) { Owner = Owner };
                Close();
                qr.ShowDialog();
                return;
            }

            bool includeSettings = _cbSettings.IsChecked == true;
            var  exportText      = TunnelExportService.BuildExportText(_config, _tunnel, includeSettings);

            if (_rbEncrypted.IsChecked == true)
            {
                var pw = _pwBox.Password;
                if (string.IsNullOrEmpty(pw))
                {
                    ShowError(Lang.T("ExportErrorPasswordEmpty"));
                    return;
                }
                if (pw != _pwConfirm.Password)
                {
                    ShowError(Lang.T("ExportErrorPasswordMismatch"));
                    return;
                }

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title    = Lang.T("ExportTunnelTitle", _tunnel.Name),
                    Filter   = "MasselGUARD encrypted config (*" + TunnelExportService.EncryptedExtension + ")|*" + TunnelExportService.EncryptedExtension,
                    FileName = SafeFileName(_tunnel.Name) + TunnelExportService.EncryptedExtension,
                };
                if (dlg.ShowDialog() != true) return;
                try
                {
                    var bytes = TunnelExportService.Encrypt(exportText, pw);
                    File.WriteAllBytes(dlg.FileName, bytes);
                }
                catch (Exception ex) { ShowError(ex.Message); return; }
            }
            else // plain .conf
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title    = Lang.T("ExportTunnelTitle", _tunnel.Name),
                    Filter   = "WireGuard config (*.conf)|*.conf|All files (*.*)|*.*",
                    FileName = SafeFileName(_tunnel.Name) + ".conf",
                };
                if (dlg.ShowDialog() != true) return;
                try
                {
                    // No BOM — WireGuard's parser rejects one.
                    File.WriteAllText(dlg.FileName, exportText,
                        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                }
                catch (Exception ex) { ShowError(ex.Message); return; }
            }

            Close();
        }

        private void ShowError(string message)
        {
            _errorTb.Text       = message;
            _errorTb.Visibility = Visibility.Visible;
        }

        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars   = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
            return new string(chars);
        }

        // ── Small UI builders ─────────────────────────────────────────────────
        private static TextBlock SectionLabel(string text, FontFamily ff, Brush muted) => new()
        {
            Text       = text,
            FontFamily = ff, FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new Thickness(0, 0, 0, 4),
        };

        private RadioButton FormatRadio(string label, string hint, FontFamily ff, Func<string, Brush> res)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = label, FontFamily = ff, FontSize = 12,
                Foreground = res("TextPrimary"),
            });
            content.Children.Add(new TextBlock
            {
                Text = hint, FontFamily = ff, FontSize = 10,
                Foreground = res("TextMuted"), TextWrapping = TextWrapping.Wrap,
            });
            return new RadioButton
            {
                GroupName = "ExportFormat",
                Content   = content,
                Margin    = new Thickness(0, 4, 0, 0),
            };
        }

        private static PasswordBox MakePasswordBox(FontFamily ff, Func<string, Brush> res) => new()
        {
            FontFamily = ff, FontSize = 12,
            Padding    = new Thickness(6, 4, 6, 4),
            Foreground = res("TextPrimary"),
            Background = res("CardBg"),
        };

        // Wrap a PasswordBox with a placeholder overlay (PasswordBox has no PlaceholderText).
        private static Grid WithPlaceholder(PasswordBox box, string placeholder, FontFamily ff, Func<string, Brush> res)
        {
            var grid = new Grid();
            var ph = new TextBlock
            {
                Text = placeholder, FontFamily = ff, FontSize = 12,
                Foreground = res("TextMuted"),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            box.PasswordChanged += (_, _) =>
                ph.Visibility = box.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            grid.Children.Add(box);
            grid.Children.Add(ph);
            return grid;
        }
    }
}
