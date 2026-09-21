using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Linq;
using MasselGUARD.Models;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ZXing;
using ZXing.Windows.Compatibility;

namespace MasselGUARD.Views
{
    public partial class ImportTunnelDialog : Window
    {
        // Raised when a config is successfully parsed — name + raw config text +
        // source + optional original file path + optional embedded MasselGUARD
        // settings (non-null only when the imported file carried them).
        public event Action<string, string, string, string?, Services.TunnelExportService.TunnelSettings?>? TunnelImported;

        private readonly HashSet<string> _alreadyImported;

        public ImportTunnelDialog(HashSet<string>? alreadyImported = null)
        {
            InitializeComponent();
            _alreadyImported = alreadyImported ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // ── Import from .conf or .conf.dpapi file ────────────────────────────
        private void ImportFromFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = Lang.T("ImportTitle"),
                Filter = "All supported configs (*.conf;*.mgconf;*.conf.dpapi)|*.conf;*.mgconf;*.conf.dpapi"
                       + "|WireGuard config (*.conf)|*.conf"
                       + "|Encrypted config (*.mgconf;*.conf.dpapi)|*.mgconf;*.conf.dpapi"
                       + "|All files (*.*)|*.*",
                FilterIndex = 1,
                Multiselect = false
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                string text;
                string filePath = dlg.FileName;

                if (filePath.EndsWith(Services.TunnelExportService.EncryptedExtension, StringComparison.OrdinalIgnoreCase))
                {
                    // Password-encrypted MasselGUARD export — prompt and decrypt.
                    var bytes = File.ReadAllBytes(filePath);
                    var pw = PasswordPromptWindow.Ask(this,
                        Lang.T("ImportEncryptedTitle"), Lang.T("ImportEncryptedPrompt"));
                    if (pw == null) return; // cancelled
                    try
                    {
                        text = Services.TunnelExportService.Decrypt(bytes, pw);
                    }
                    catch (Exception ex)
                    {
                        ShowStatus(Lang.T("ImportDecryptFailed", ex.Message), isError: true);
                        return;
                    }
                }
                else if (filePath.EndsWith(".conf.dpapi", StringComparison.OrdinalIgnoreCase))
                {
                    var cipherBytes = File.ReadAllBytes(filePath);
                    var plainBytes = ProtectedData.Unprotect(
                        cipherBytes, null, DataProtectionScope.CurrentUser);
                    text = System.Text.Encoding.UTF8.GetString(plainBytes);
                }
                else
                {
                    text = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
                }

                // Split out any embedded MasselGUARD settings before storing.
                var (config, settings) = Services.TunnelExportService.ParseImportText(text);

                var baseName = Path.GetFileNameWithoutExtension(filePath);
                if (baseName.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
                    baseName = Path.GetFileNameWithoutExtension(baseName); // strip .conf from .conf.dpapi

                // Resolve a name collision — overwrite, save under a new name, or cancel.
                // (MainWindow replaces an existing tunnel when the name matches, so
                //  "overwrite" keeps the name and "rename" picks a fresh unique one.)
                if (_alreadyImported.Contains(baseName))
                {
                    switch (AskDuplicate(baseName))
                    {
                        case DupChoice.Cancel:
                            return;
                        case DupChoice.Rename:
                            var newName = AskNewName(baseName);
                            if (string.IsNullOrEmpty(newName)) return;   // cancelled
                            baseName = newName!;
                            break;
                        // DupChoice.Overwrite → keep baseName
                    }
                }

                var storagePath = Services.TunnelService.SaveConfigToFile(baseName, config);
                TunnelImported?.Invoke(baseName, "", "local", storagePath, settings);
                Close();
            }
            catch (Exception ex)
            {
                ShowStatus(Lang.T("ImportFailed", ex.Message), isError: true);
            }
        }

        // ── Import from WireGuard: one or more .conf files, or a WireGuard "export
        //    tunnels to zip" archive. Each config is imported as a local MasselGUARD tunnel. ──
        private void ImportFromWireGuard_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = Lang.T("ImportFromWireGuard"),
                Filter = "WireGuard configs (*.conf;*.zip)|*.conf;*.zip"
                       + "|WireGuard config (*.conf)|*.conf"
                       + "|Exported tunnels (*.zip)|*.zip"
                       + "|All files (*.*)|*.*",
                FilterIndex = 1,
                Multiselect = true,
            };
            if (dlg.ShowDialog() != true) return;

            int imported = 0;
            try
            {
                foreach (var file in dlg.FileNames)
                {
                    if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        using var zip = System.IO.Compression.ZipFile.OpenRead(file);
                        foreach (var entry in zip.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name)) continue;   // directory entry
                            if (!entry.Name.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)) continue;
                            using var sr = new StreamReader(entry.Open(), System.Text.Encoding.UTF8);
                            if (ImportConfigText(Path.GetFileNameWithoutExtension(entry.Name), sr.ReadToEnd()))
                                imported++;
                        }
                    }
                    else
                    {
                        if (ImportConfigText(Path.GetFileNameWithoutExtension(file),
                                             File.ReadAllText(file, System.Text.Encoding.UTF8)))
                            imported++;
                    }
                }

                if (imported > 0) Close();
                else ShowStatus(Lang.T("ImportWireGuardNone"), isError: false);
            }
            catch (Exception ex)
            {
                ShowStatus(Lang.T("ImportFailed", ex.Message), isError: true);
            }
        }

        /// <summary>Import one WireGuard config text as a local tunnel (with duplicate-name
        /// resolution). Returns true when a tunnel was actually stored.</summary>
        private bool ImportConfigText(string baseName, string text)
        {
            var (config, settings) = Services.TunnelExportService.ParseImportText(text);
            if (string.IsNullOrWhiteSpace(config)) return false;
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "tunnel";

            if (_alreadyImported.Contains(baseName))
            {
                switch (AskDuplicate(baseName))
                {
                    case DupChoice.Cancel: return false;
                    case DupChoice.Rename:
                        var nn = AskNewName(baseName);
                        if (string.IsNullOrEmpty(nn)) return false;
                        baseName = nn!;
                        break;
                    // Overwrite → keep baseName
                }
            }

            var storagePath = Services.TunnelService.SaveConfigToFile(baseName, config);
            _alreadyImported.Add(baseName);
            TunnelImported?.Invoke(baseName, "", "local", storagePath, settings);
            return true;
        }

        // ── QR scan (drag a box over an on-screen QR code) ────────────────────
        private void ImportFromQR_Click(object sender, RoutedEventArgs e)
        {
            var picker = new QrScreenCaptureWindow { Owner = this };
            picker.ShowDialog();
            if (string.IsNullOrWhiteSpace(picker.DecodedText))
                return;

            try
            {
                ShowStatus(Lang.T("ImportQRFound"), isError: false);
                // The QR content is the raw WireGuard config text (settings are
                // parsed out too, in case it was a MasselGUARD-made payload).
                var name = "QR-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var (config, settings) = Services.TunnelExportService.ParseImportText(picker.DecodedText!);
                var storagePath = Services.TunnelService.SaveConfigToFile(name, config);
                TunnelImported?.Invoke(name, "", "local", storagePath, settings);
                Close();
            }
            catch (Exception ex)
            {
                ShowStatus(Lang.T("ImportQRError", ex.Message), isError: true);
            }
        }

        private void ShowStatus(string text, bool isError)
        {
            Dispatcher.BeginInvoke(() =>
            {
                StatusLabel.Text       = text;
                StatusLabel.Foreground = isError
                    ? (System.Windows.Media.SolidColorBrush)FindResource("Danger")
                    : (System.Windows.Media.SolidColorBrush)FindResource("TextMuted");
                StatusLabel.Visibility = Visibility.Visible;
            });
        }

        // ── Duplicate-name resolution ─────────────────────────────────────────
        private enum DupChoice { Overwrite, Rename, Cancel }

        /// <summary>Themed 3-way prompt shown when the imported name already exists.</summary>
        private DupChoice AskDuplicate(string name)
        {
            Brush Res(string k) => (Application.Current.Resources[k] as Brush) ?? Brushes.Gray;
            var ff = Application.Current.Resources["Theme.FontFamily"] as FontFamily ?? new FontFamily("Segoe UI");

            var win = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            };
            var result = DupChoice.Cancel;

            var border = new Border
            {
                Background = Res("WindowBg"), BorderBrush = Res("Accent"), BorderThickness = new Thickness(1),
                CornerRadius = Application.Current.Resources["Theme.CornerRadius"] is CornerRadius cr ? cr : new CornerRadius(6),
                Padding = new Thickness(20),
            };
            var panel = new StackPanel { MaxWidth = 380 };
            panel.Children.Add(new TextBlock
            {
                Text = Lang.T("ImportDuplicateTitle"), FontFamily = ff, FontSize = 13, FontWeight = FontWeights.Bold,
                Foreground = Res("Accent"), Margin = new Thickness(0, 0, 0, 10),
            });
            panel.Children.Add(new TextBlock
            {
                Text = Lang.T("ImportDuplicateMsg", name), FontFamily = ff, FontSize = 11,
                Foreground = Res("TextPrimary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16),
            });

            var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button Mk(string key, string styleKey, Thickness margin) => new()
            {
                Content = Lang.T(key), Style = Application.Current.Resources[styleKey] as Style,
                Padding = new Thickness(12, 6, 12, 6), Margin = margin,
            };
            var over   = Mk("BtnOverwrite", "DangerBtn",  new Thickness(0, 0, 8, 0));
            var rename = Mk("BtnSaveAsNew", "FlatBtn",     new Thickness(0, 0, 8, 0));
            var cancel = Mk("BtnCancel",    "PrimaryBtn",  new Thickness(0));
            over.Click   += (_, _) => { result = DupChoice.Overwrite; win.Close(); };
            rename.Click += (_, _) => { result = DupChoice.Rename;    win.Close(); };
            cancel.Click += (_, _) => { result = DupChoice.Cancel;    win.Close(); };
            btns.Children.Add(over); btns.Children.Add(rename); btns.Children.Add(cancel);
            panel.Children.Add(btns);

            border.Child = panel; win.Content = border;
            win.ShowDialog();
            return result;
        }

        /// <summary>Prompt for a new, unique tunnel name; loops until unique or cancelled.</summary>
        private string? AskNewName(string original)
        {
            string suggestion = SuggestUniqueName(original);
            string? error = null;
            while (true)
            {
                var entered = TextPromptWindow.Ask(this,
                    Lang.T("ImportRenameTitle"), Lang.T("ImportRenamePrompt"), suggestion, error);
                if (entered == null) return null;               // cancelled
                entered = entered.Trim();
                if (entered.Length == 0) { error = Lang.T("ImportNameEmpty"); continue; }
                if (_alreadyImported.Contains(entered))
                {
                    error = Lang.T("ImportNameTaken");
                    suggestion = SuggestUniqueName(entered);
                    continue;
                }
                return entered;
            }
        }

        private string SuggestUniqueName(string name)
        {
            if (!_alreadyImported.Contains(name)) return name;
            for (int i = 2; ; i++)
            {
                var candidate = $"{name} ({i})";
                if (!_alreadyImported.Contains(candidate)) return candidate;
            }
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }
    }

    // ── Screen QR capture: a movable/resizable box + Scan ────────────────────────
    // A full-screen, dimmed topmost overlay across all monitors. The user drags and
    // resizes a translucent box over an on-screen QR code and presses Scan; the overlay
    // hides itself, grabs just that screen region (DPI-correct via PointToScreen +
    // GDI CopyFromScreen) and decodes it with ZXing. Inherently non-themed (raw colours
    // only). On success DecodedText holds the QR payload (the raw WireGuard config text).
    internal sealed class QrScreenCaptureWindow : Window
    {
        public string? DecodedText { get; private set; }

        private static readonly SolidColorBrush Accent = new(Color.FromRgb(0x4F, 0xC3, 0xF7));

        private readonly Canvas    _canvas = new();
        private readonly Border    _box;
        private readonly Border    _toolbar;
        private readonly TextBlock _hint;
        private readonly Thumb[]   _handles = new Thumb[4];

        private bool   _moving;
        private Point  _moveStart;
        private double _boxL, _boxT;

        public QrScreenCaptureWindow()
        {
            // The capture surface itself stays theme-agnostic (raw colours for the box,
            // handles and backdrop). Buttons are deliberately left OUT of this reset so the
            // Scan / Cancel buttons inherit the app's themed FlatBtn instead of raw Windows
            // chrome on the dark toolbar.
            foreach (var ty in new[] { typeof(TextBlock), typeof(Thumb),
                                       typeof(StackPanel), typeof(Border) })
                Resources[ty] = new Style(ty);

            WindowStyle           = WindowStyle.None;
            AllowsTransparency    = true;
            Background            = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0));   // dim backdrop
            Topmost               = true;
            ShowInTaskbar         = false;
            ResizeMode            = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Cursor                = Cursors.Cross;
            Left   = SystemParameters.VirtualScreenLeft;
            Top    = SystemParameters.VirtualScreenTop;
            Width  = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
            Content = _canvas;

            _box = new Border
            {
                BorderBrush     = Accent,
                BorderThickness = new Thickness(2),
                Background      = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
                Cursor          = Cursors.SizeAll,
                Width = 320, Height = 320,
            };
            _box.MouseLeftButtonDown += Box_Down;
            _box.MouseMove           += Box_Move;
            _box.MouseLeftButtonUp   += Box_Up;
            Canvas.SetLeft(_box, Math.Max(0, (SystemParameters.PrimaryScreenWidth  - 320) / 2));
            Canvas.SetTop (_box, Math.Max(0, (SystemParameters.PrimaryScreenHeight - 320) / 2));
            _canvas.Children.Add(_box);

            for (int i = 0; i < 4; i++)
            {
                var t = new Thumb { Width = 14, Height = 14, Template = HandleTemplate(),
                                    Cursor = (i == 0 || i == 3) ? Cursors.SizeNWSE : Cursors.SizeNESW, Tag = i };
                t.DragDelta += Handle_DragDelta;
                _handles[i] = t;
                _canvas.Children.Add(t);
            }

            _hint = new TextBlock
            {
                Text = Lang.T("QrScanInstruction"),
                Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                TextWrapping = TextWrapping.Wrap, MaxWidth = 300,
            };
            var scan   = MakeButton(Lang.T("BtnScan"));
            scan.Click  += async (_, _) => await ScanAsync();
            var cancel = MakeButton(Lang.T("BtnCancel"));
            cancel.Click += (_, _) => Close();   // DecodedText stays null → caller treats as cancel
            var bar = new StackPanel { Orientation = Orientation.Horizontal };
            bar.Children.Add(_hint); bar.Children.Add(scan); bar.Children.Add(cancel);
            _toolbar = new Border
            {
                Background      = new SolidColorBrush(Color.FromArgb(235, 28, 28, 28)),
                BorderBrush     = Brushes.Gray, BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8),
                Child = bar,
            };
            _canvas.Children.Add(_toolbar);

            KeyDown     += (_, ev) => { if (ev.Key == Key.Escape) Close(); };
            Loaded      += (_, _)  => { Focus(); LayoutChrome(); };
            SizeChanged += (_, _)  => LayoutChrome();
        }

        private static Button MakeButton(string text) => new()
        {
            Content = text, Padding = new Thickness(16, 5, 16, 5), MinWidth = 70,
            Margin = new Thickness(6, 0, 0, 0),
        };

        private static ControlTemplate HandleTemplate()
        {
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetValue(Border.BackgroundProperty, Accent);
            b.SetValue(Border.BorderBrushProperty, Brushes.White);
            b.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            return new ControlTemplate(typeof(Thumb)) { VisualTree = b };
        }

        // ── Move (drag the box body) ──
        private void Box_Down(object sender, MouseButtonEventArgs e)
        {
            _moving = true;
            _moveStart = e.GetPosition(_canvas);
            _boxL = Canvas.GetLeft(_box);
            _boxT = Canvas.GetTop(_box);
            _box.CaptureMouse();
            e.Handled = true;
        }

        private void Box_Move(object sender, MouseEventArgs e)
        {
            if (!_moving) return;
            var p = e.GetPosition(_canvas);
            Canvas.SetLeft(_box, _boxL + (p.X - _moveStart.X));
            Canvas.SetTop (_box, _boxT + (p.Y - _moveStart.Y));
            LayoutChrome();
        }

        private void Box_Up(object sender, MouseButtonEventArgs e)
        {
            _moving = false;
            _box.ReleaseMouseCapture();
        }

        // ── Resize (corner handles) ──
        private void Handle_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (sender is not Thumb th || th.Tag is not int idx) return;
            const double min = 40;
            double l = Canvas.GetLeft(_box), t = Canvas.GetTop(_box);
            double right = l + _box.Width, bottom = t + _box.Height;

            switch (idx)
            {
                case 0: l = Math.Min(l + e.HorizontalChange, right - min);  t = Math.Min(t + e.VerticalChange, bottom - min); break; // NW
                case 1: right = Math.Max(l + min, right + e.HorizontalChange); t = Math.Min(t + e.VerticalChange, bottom - min); break; // NE
                case 2: l = Math.Min(l + e.HorizontalChange, right - min);  bottom = Math.Max(t + min, bottom + e.VerticalChange); break; // SW
                case 3: right = Math.Max(l + min, right + e.HorizontalChange); bottom = Math.Max(t + min, bottom + e.VerticalChange); break; // SE
            }
            Canvas.SetLeft(_box, l); Canvas.SetTop(_box, t);
            _box.Width  = Math.Max(min, right - l);
            _box.Height = Math.Max(min, bottom - t);
            LayoutChrome();
        }

        private void LayoutChrome()
        {
            double l = Canvas.GetLeft(_box), t = Canvas.GetTop(_box), w = _box.Width, h = _box.Height;
            PlaceHandle(_handles[0], l,     t);
            PlaceHandle(_handles[1], l + w, t);
            PlaceHandle(_handles[2], l,     t + h);
            PlaceHandle(_handles[3], l + w, t + h);

            _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double tw = _toolbar.DesiredSize.Width, ttoolh = _toolbar.DesiredSize.Height;
            double tx = l + (w - tw) / 2;
            double ty = t + h + 10;
            if (ty + ttoolh > ActualHeight) ty = t - ttoolh - 10;
            tx = Math.Max(0, Math.Min(tx, Math.Max(0, ActualWidth - tw)));
            ty = Math.Max(0, ty);
            Canvas.SetLeft(_toolbar, tx);
            Canvas.SetTop (_toolbar, ty);
        }

        private static void PlaceHandle(Thumb t, double cx, double cy)
        {
            Canvas.SetLeft(t, cx - t.Width  / 2);
            Canvas.SetTop (t, cy - t.Height / 2);
        }

        private async System.Threading.Tasks.Task ScanAsync()
        {
            Point p0, p1;
            try
            {
                p0 = _box.PointToScreen(new Point(0, 0));                         // physical px
                p1 = _box.PointToScreen(new Point(_box.ActualWidth, _box.ActualHeight));
            }
            catch { return; }
            int x = (int)Math.Round(p0.X), y = (int)Math.Round(p0.Y);
            int w = Math.Max(1, (int)Math.Round(p1.X - p0.X));
            int h = Math.Max(1, (int)Math.Round(p1.Y - p0.Y));

            // Hide the overlay (and the owning import dialog) via Opacity — NOT Visibility,
            // which would reset this window's "shown as dialog" state — so neither is
            // captured if it sits over the QR, then grab the region.
            var owner = Owner;
            Opacity = 0;
            if (owner != null) owner.Opacity = 0;
            await System.Threading.Tasks.Task.Delay(120);
            string? text = null;
            try
            {
                using var bmp = new System.Drawing.Bitmap(w, h);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                    g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
                text = Decode(bmp);
            }
            catch { }
            finally
            {
                if (owner != null) owner.Opacity = 1;
                Opacity = 1;
                Focus();
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                DecodedText = text;   // reported via property, not DialogResult
                Close();
            }
            else
            {
                _hint.Text       = Lang.T("QrScanNotFound");
                _hint.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x65));
            }
        }

        private static string? Decode(System.Drawing.Bitmap bmp)
        {
            var reader = new BarcodeReader
            {
                AutoRotate = true,
                Options = new ZXing.Common.DecodingOptions
                {
                    TryHarder       = true,
                    TryInverted     = true,
                    PossibleFormats = new[] { BarcodeFormat.QR_CODE },
                },
            };
            return reader.Decode(bmp)?.Text;
        }
    }
}
