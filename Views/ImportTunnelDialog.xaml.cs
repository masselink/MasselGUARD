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
        // Raised when a config is successfully parsed — name + raw config text + source + optional original file path
        public event Action<string, string, string, string?>? TunnelImported;

        private readonly HashSet<string> _alreadyImported;

        public ImportTunnelDialog(HashSet<string>? alreadyImported = null,
            AppMode mode = AppMode.Mixed)
        {
            InitializeComponent();
            _alreadyImported = alreadyImported ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // "Link to WireGuard profile" is only meaningful in Companion or Mixed mode
            bool showWg = mode != AppMode.Standalone;
            bool wgInstalled = MainWindow.FindWireGuardExe() != null;
            if (ImportFromWireGuardBtn != null)
            {
                ImportFromWireGuardBtn.Visibility = showWg
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
                if (showWg && !wgInstalled)
                {
                    ImportFromWireGuardBtn.IsEnabled = false;
                    ImportFromWireGuardBtn.ToolTip   = Lang.T("TunnelWireGuardNotInstalled");
                }
            }
        }

        // ── Import from .conf or .conf.dpapi file ────────────────────────────
        private void ImportFromFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title  = Lang.T("ImportTitle"),
                Filter = "WireGuard config (*.conf)|*.conf|Encrypted config (*.conf.dpapi)|*.conf.dpapi|All files (*.*)|*.*",
                FilterIndex = 1,
                Multiselect = false
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                string text;
                string filePath = dlg.FileName;

                if (filePath.EndsWith(".conf.dpapi", StringComparison.OrdinalIgnoreCase))
                {
                    var cipherBytes = File.ReadAllBytes(filePath);
                    var plainBytes = ProtectedData.Unprotect(
                        cipherBytes, null, DataProtectionScope.CurrentUser);
                    text = System.Text.Encoding.UTF8.GetString(plainBytes);

                    var name = Path.GetFileNameWithoutExtension(
                        Path.GetFileNameWithoutExtension(filePath));
                    var storagePath = Services.TunnelService.SaveConfigToFile(name, text);
                    TunnelImported?.Invoke(name, "", "local", storagePath);
                }
                else
                {
                    text = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
                    var name = Path.GetFileNameWithoutExtension(filePath);
                    var storagePath = Services.TunnelService.SaveConfigToFile(name, text);
                    TunnelImported?.Invoke(name, "", "local", storagePath);
                }
                Close();
            }
            catch (Exception ex)
            {
                ShowStatus(Lang.T("ImportFailed", ex.Message), isError: true);
            }
        }

        // ── Import from WireGuard client config directory ─────────────────────
        private void ImportFromWireGuard_Click(object sender, RoutedEventArgs e)
        {
            var configs = FindWireGuardConfigs();
            if (configs.Count == 0)
            {
                ShowStatus(Lang.T("ImportWireGuardNone"), isError: false);
                return;
            }

            // Collect already-imported names from the field passed by MainWindow
            var picker = new WireGuardPickerDialog(configs, _alreadyImported) { Owner = this };
            if (picker.ShowDialog() != true || picker.SelectedTunnels.Count == 0) return;

            foreach (var (name, _, path) in picker.SelectedTunnels)
                TunnelImported?.Invoke(name, "", "wireguard", path);

            Close();
        }

        private static List<(string name, string path)> FindWireGuardConfigs()
        {
            var results = new List<(string, string)>();
            var candidates = new List<string>();

            // Registry-detected install dir
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WireGuard");
                if (key?.GetValue("InstallDirectory") is string dir)
                    candidates.Add(Path.Combine(dir, "Data", "Configurations"));
            }
            catch { }

            // Common fallbacks
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WireGuard", "Data", "Configurations"));
            candidates.Add(@"C:\WireGuard\Data\Configurations");

            foreach (var dir in candidates.Distinct())
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir)
                    .Where(f => f.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".conf.dpapi", StringComparison.OrdinalIgnoreCase)))
                {
                    var name = Path.GetFileNameWithoutExtension(f);
                    if (name.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
                        name = Path.GetFileNameWithoutExtension(name);
                    results.Add((name, f));
                }
            }
            return results;
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
                // The QR content is the raw WireGuard config text.
                var name = "QR-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var storagePath = Services.TunnelService.SaveConfigToFile(name, picker.DecodedText!);
                TunnelImported?.Invoke(name, "", "local", storagePath);
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
            // Plain system styling — never themed (this is a theme-agnostic capture tool).
            foreach (var ty in new[] { typeof(Button), typeof(TextBlock), typeof(Thumb),
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

    // ── Multi-select tunnel picker for WireGuard import ──────────────────────
    public class WireGuardPickerDialog : Window
    {
        public List<(string name, string config, string path)> SelectedTunnels { get; } = new();

        private readonly List<(string name, string path)> _configs;
        private readonly HashSet<string>                   _alreadyImported;
        private readonly List<System.Windows.Controls.CheckBox> _checkBoxes = new();

        public WireGuardPickerDialog(List<(string name, string path)> configs,
                                      HashSet<string> alreadyImported)
        {
            _configs         = configs;
            _alreadyImported = alreadyImported;

            Title              = Lang.T("ImportWireGuardTitle");
            Width              = 420;
            SizeToContent      = SizeToContent.Height;
            WindowStyle        = WindowStyle.None;
            AllowsTransparency = true;
            Background         = System.Windows.Media.Brushes.Transparent;
            ResizeMode         = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var Br = (string key) =>
                (System.Windows.Media.SolidColorBrush)
                System.Windows.Application.Current.Resources[key];

            var root = new System.Windows.Controls.Border
            {
                Background      = Br("WindowBg"),
                BorderBrush     = Br("Accent"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6)
            };

            var grid = new System.Windows.Controls.Grid();
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(44) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(52) });

            // Title bar
            var titleBar = new System.Windows.Controls.Border
            {
                Background   = Br("Surface"),
                CornerRadius = new CornerRadius(6, 6, 0, 0)
            };
            var titleText = new System.Windows.Controls.TextBlock
            {
                Text      = Lang.T("ImportWireGuardTitle"),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize   = 12, FontWeight = FontWeights.Bold,
                Foreground = Br("Accent"), Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            titleBar.Child = titleText;
            titleBar.MouseLeftButtonDown += (_, ev) =>
            { if (ev.LeftButton == MouseButtonState.Pressed) DragMove(); };
            System.Windows.Controls.Grid.SetRow(titleBar, 0);
            grid.Children.Add(titleBar);

            // Content
            var content = new System.Windows.Controls.StackPanel
            {
                Margin = new Thickness(16, 12, 16, 12)
            };

            content.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text      = Lang.T("ImportWireGuardPrompt"),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize  = 10, Foreground = Br("TextMuted"),
                Margin    = new Thickness(0, 0, 0, 10)
            });

            // Scrollable checkbox list
            var scroll = new System.Windows.Controls.ScrollViewer
            {
                MaxHeight              = 240,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
            };
            var itemStack = new System.Windows.Controls.StackPanel();

            foreach (var (name, _) in configs)
            {
                bool imported = alreadyImported.Contains(name);

                var row = new System.Windows.Controls.Grid();
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
                row.Margin = new Thickness(0, 2, 0, 2);

                var cb = new System.Windows.Controls.CheckBox
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin            = new Thickness(0, 0, 8, 0),
                    IsChecked         = false
                };
                _checkBoxes.Add(cb);
                System.Windows.Controls.Grid.SetColumn(cb, 0);

                var nameBlock = new System.Windows.Controls.TextBlock
                {
                    Text       = name,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize   = 11,
                    Foreground = imported ? Br("TextMuted") : Br("TextPrimary"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                System.Windows.Controls.Grid.SetColumn(nameBlock, 1);

                if (imported)
                {
                    var badge = new System.Windows.Controls.TextBlock
                    {
                        Text       = "✓ imported",
                        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        FontSize   = 9,
                        Foreground = Br("TextMuted"),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin     = new Thickness(8, 0, 0, 0)
                    };
                    System.Windows.Controls.Grid.SetColumn(badge, 2);
                    row.Children.Add(badge);
                }

                row.Children.Add(cb);
                row.Children.Add(nameBlock);
                itemStack.Children.Add(row);
            }

            scroll.Content = itemStack;
            content.Children.Add(scroll);

            // Select all / Deselect all links
            var linkPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin      = new Thickness(0, 8, 0, 0)
            };

            var selectAll = new System.Windows.Controls.Button
            {
                Content         = Lang.T("BtnSelectAll"),
                Style           = (Style)System.Windows.Application.Current.Resources["FlatBtn"],
                FontSize        = 10, Padding = new Thickness(8, 3, 8, 3),
                Margin          = new Thickness(0, 0, 6, 0)
            };
            selectAll.Click += (_, _) => { foreach (var c in _checkBoxes) c.IsChecked = true; };

            var deselectAll = new System.Windows.Controls.Button
            {
                Content  = Lang.T("BtnDeselectAll"),
                Style    = (Style)System.Windows.Application.Current.Resources["FlatBtn"],
                FontSize = 10, Padding = new Thickness(8, 3, 8, 3)
            };
            deselectAll.Click += (_, _) => { foreach (var c in _checkBoxes) c.IsChecked = false; };

            linkPanel.Children.Add(selectAll);
            linkPanel.Children.Add(deselectAll);
            content.Children.Add(linkPanel);

            System.Windows.Controls.Grid.SetRow(content, 1);
            grid.Children.Add(content);

            // Button bar
            var btnBar = new System.Windows.Controls.Border
            {
                Background   = Br("Surface"),
                CornerRadius = new CornerRadius(0, 0, 6, 6)
            };
            var btnStack = new System.Windows.Controls.StackPanel
            {
                Orientation         = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Center,
                Margin              = new Thickness(0, 0, 16, 0)
            };

            var cancelBtn = new System.Windows.Controls.Button
            {
                Content = Lang.T("BtnCancel"),
                Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0),
                Style   = (Style)System.Windows.Application.Current.Resources["FlatBtn"]
            };
            cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };

            var okBtn = new System.Windows.Controls.Button
            {
                Content = Lang.T("BtnImportSelected"),
                Padding = new Thickness(16, 6, 16, 6),
                Style   = (Style)System.Windows.Application.Current.Resources["PrimaryBtn"]
            };
            okBtn.Click += (_, _) =>
            {
                for (int i = 0; i < _configs.Count; i++)
                {
                    if (_checkBoxes[i].IsChecked != true) continue;
                    var (name, path) = _configs[i];
                    // Store path only — no config content for WireGuard references
                    SelectedTunnels.Add((name, "", path));
                }
                if (SelectedTunnels.Count == 0) return; // nothing checked
                DialogResult = true;
                Close();
            };

            btnStack.Children.Add(cancelBtn);
            btnStack.Children.Add(okBtn);
            btnBar.Child = btnStack;
            System.Windows.Controls.Grid.SetRow(btnBar, 2);
            grid.Children.Add(btnBar);

            root.Child  = grid;
            Content     = root;
        }
    }
}
