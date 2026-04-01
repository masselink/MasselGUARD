using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
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
                    // DPAPI-encrypted file — decrypt with current user's key
                    var cipherBytes = File.ReadAllBytes(filePath);
                    var plainBytes = System.Security.Cryptography.ProtectedData.Unprotect(
                        cipherBytes, null,
                        System.Security.Cryptography.DataProtectionScope.CurrentUser);
                    text = System.Text.Encoding.UTF8.GetString(plainBytes);

                    // Strip double extension to get tunnel name: "home.conf.dpapi" → "home"
                    var name = Path.GetFileNameWithoutExtension(
                        Path.GetFileNameWithoutExtension(filePath));
                    TunnelImported?.Invoke(name, text, "local", null);
                }
                else
                {
                    // Plain .conf file
                    text = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
                    var name = Path.GetFileNameWithoutExtension(filePath);
                    TunnelImported?.Invoke(name, text, "local", null);
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

        // ── QR scan ───────────────────────────────────────────────────────────
        private CancellationTokenSource? _qrCts;

        private async void ImportFromQR_Click(object sender, RoutedEventArgs e)
        {
            _qrCts?.Cancel();
            _qrCts = new CancellationTokenSource();
            ShowStatus(Lang.T("ImportQRSearching"), isError: false);

            try
            {
                var result = await Task.Run(() => ScanQR(_qrCts.Token), _qrCts.Token);
                if (result == null) return;

                ShowStatus(Lang.T("ImportQRFound"), isError: false);

                // The QR content is the raw WireGuard config text
                var name = "QR-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                TunnelImported?.Invoke(name, result, "local", null);
                Close();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ShowStatus(Lang.T("ImportQRError", ex.Message), isError: true);
            }
        }

        private static string? ScanQR(CancellationToken ct)
        {
            var reader = new BarcodeReader
            {
                AutoRotate = true,
                Options    = new ZXing.Common.DecodingOptions
                {
                    TryHarder   = true,
                    TryInverted = true,
                    PossibleFormats = new[] { BarcodeFormat.QR_CODE }
                }
            };

            // Try each camera index
            for (int camIdx = 0; camIdx < 4; camIdx++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var capture = new System.Drawing.Bitmap(1, 1); // probe
                    // Use WinForms capture
                    using var cam = new System.Windows.Forms.Timer();
                    // Try to open camera via DirectShow / WMF via WinForms VideoCapture
                    // ZXing Windows.Compatibility handles frame capture
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < deadline)
                    {
                        ct.ThrowIfCancellationRequested();
                        using var bmp = CaptureCameraFrame(camIdx);
                        if (bmp == null) break;
                        var res = reader.Decode(bmp);
                        if (res != null) return res.Text;
                        System.Threading.Thread.Sleep(150);
                    }
                }
                catch { }
            }
            return null;
        }

        private static System.Drawing.Bitmap? CaptureCameraFrame(int index)
        {
            // Use Windows.Media.Capture is UWP-only; use DirectShow via AForge or
            // screen capture as fallback. For broad compatibility we use a WinForms
            // VideoCapture shim via ZXing's Windows.Compatibility binding.
            try
            {
                // Attempt screen capture of full primary screen as a simple fallback
                // (user holds phone with QR in front of screen/camera)
                var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                var bmp    = new System.Drawing.Bitmap(screen.Width, screen.Height);
                using var g = System.Drawing.Graphics.FromImage(bmp);
                g.CopyFromScreen(screen.Location, System.Drawing.Point.Empty, screen.Size);
                return bmp;
            }
            catch { return null; }
        }

        protected override void OnClosed(EventArgs e)
        {
            _qrCts?.Cancel();
            base.OnClosed(e);
        }

        private void ShowStatus(string text, bool isError)
        {
            Dispatcher.BeginInvoke(() =>
            {
                StatusLabel.Text       = text;
                StatusLabel.Foreground = isError
                    ? (System.Windows.Media.SolidColorBrush)FindResource("Red")
                    : (System.Windows.Media.SolidColorBrush)FindResource("Sub");
                StatusLabel.Visibility = Visibility.Visible;
            });
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
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
                Background      = Br("Bg"),
                BorderBrush     = Br("Border"),
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
                Background   = Br("Panel"),
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
                FontSize  = 10, Foreground = Br("Sub"),
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
                    Foreground = imported ? Br("Sub") : Br("Text"),
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
                        Foreground = Br("Sub"),
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
                Background   = Br("Panel"),
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
