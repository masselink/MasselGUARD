using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WGWifiSwitcher
{
    public class TunnelRule : INotifyPropertyChanged
    {
        private string _ssid   = "";
        private string _tunnel = "";

        public string Ssid   { get => _ssid;   set { _ssid   = value; OnProp(); } }
        public string Tunnel { get => _tunnel; set { _tunnel = value; OnProp(); OnProp(nameof(TunnelDisplay)); } }

        [JsonIgnore] public string TunnelDisplay => string.IsNullOrEmpty(_tunnel) ? "\u2014 disconnect" : _tunnel;
        [JsonIgnore] public string StatusText { get; set; } = "\u2014";
        [JsonIgnore] public SolidColorBrush StatusColor =>
            StatusText == "\u25cf active"
                ? new SolidColorBrush(Color.FromRgb(63, 185, 80))
                : new SolidColorBrush(Color.FromRgb(139, 148, 158));

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnProp([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class AppConfig
    {
        public List<TunnelRule> Rules         { get; set; } = new();
        public string           DefaultAction { get; set; } = "none";
        public string           DefaultTunnel { get; set; } = "";
    }

    public partial class MainWindow : Window
    {
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WGWifiSwitcher", "config.json");

        private AppConfig _cfg = new();
        private readonly ObservableCollection<TunnelRule> _rules = new();
        private string? _lastWifi;
        private readonly DispatcherTimer _timer = new();
        private bool _loading = false;

        // WireGuard executable
        private static readonly string WgExe =
            File.Exists(@"C:\Program Files\WireGuard\wireguard.exe")
                ? @"C:\Program Files\WireGuard\wireguard.exe"
                : "wireguard";

        // Search known locations for a tunnel's .conf file
        private static string? FindConfPath(string tunnel)
        {
            var dirs = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),       "WireGuard"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WireGuard"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),          "WireGuard"),
            };
            foreach (var dir in dirs)
            {
                var p = Path.Combine(dir, tunnel + ".conf");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        // ── Constructor ──────────────────────────────────────────────────────

        public MainWindow()
        {
            InitializeComponent();
            RulesListView.ItemsSource = _rules;

            System.Windows.Application.Current.DispatcherUnhandledException += (s, e) =>
            {
                Log("ERROR: " + e.Exception.Message, LogLevel.Warn);
                e.Handled = true;
            };

            Loaded += (_, _) =>
            {
                LoadConfig();
                UpdateAdminLabel();
                SetupTimer();
                Log("Application started.", LogLevel.Info);
            };
        }

        // ── Taskbar icon (force AppWindow style so it appears without WindowStyle) ──

        private const int GWL_EXSTYLE      = -20;
        private const int WS_EX_APPWINDOW  = 0x00040000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

        // ── Wlan native API ───────────────────────────────────────────────────
        [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);
        [DllImport("wlanapi.dll")] static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
        [DllImport("wlanapi.dll")] static extern uint WlanRegisterNotification(IntPtr clientHandle, uint dwNotifSource, bool bIgnoreDuplicate, WlanNotificationCallback funcCallback, IntPtr pCallbackContext, IntPtr pReserved, out uint pdwPrevNotifSource);

        private const uint WLAN_NOTIFICATION_SOURCE_ACM = 0x00000008;
        private const uint WLAN_NOTIFICATION_SOURCE_ALL = 0x0000FFFF;

        [StructLayout(LayoutKind.Sequential)]
        private struct WLAN_NOTIFICATION_DATA
        {
            public uint NotificationSource;
            public uint NotificationCode;
            public Guid InterfaceGuid;
            public uint dwDataSize;
            public IntPtr pData;
        }

        private delegate void WlanNotificationCallback(ref WLAN_NOTIFICATION_DATA data, IntPtr context);

        private IntPtr _wlanHandle = IntPtr.Zero;
        private WlanNotificationCallback? _wlanCallback; // keep reference to prevent GC

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd  = new WindowInteropHelper(this).Handle;

            // Force taskbar button to appear (WindowStyle=None hides it otherwise)
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, (style & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW);

            // Set window icon to match the tray icon
            try
            {
                using var drawingIcon = TrayIconHelper.CreateIcon(false);
                Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    drawingIcon.Handle,
                    System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            }
            catch { }
        }

        // ── Timer ────────────────────────────────────────────────────────────

        private void SetupTimer()
        {
            // Slow timer — only refreshes status display every 10s, does NOT drive WiFi detection
            _timer.Interval = TimeSpan.FromSeconds(10);
            _timer.Tick += (_, _) => UpdateStatusDisplay();
            _timer.Start();

            // Register for instant WiFi change notifications via WlanApi
            RegisterWifiEvents();

            // Set initial state
            var wifi = GetCurrentSsid();
            _lastWifi = wifi;
            UpdateStatusDisplay(wifi);
        }

        // Called by WlanApi callback when connection state changes
        private void OnWifiChanged()
        {
            var wifi = GetCurrentSsid();
            UpdateStatusDisplay(wifi);
            if (wifi == _lastWifi) return;
            _lastWifi = wifi;
            Log("WiFi changed to: " + (wifi ?? "disconnected"), LogLevel.Info);
            ApplyRules(wifi);
        }

        private void UpdateStatusDisplay(string? wifi = null)
        {
            wifi ??= GetCurrentSsid();
            WifiLabel.Text       = wifi ?? "Not connected";
            WifiLabel.Foreground = wifi != null
                ? (SolidColorBrush)FindResource("Text")
                : (SolidColorBrush)FindResource("Sub");

            foreach (var rule in _rules)
                rule.StatusText = (!string.IsNullOrEmpty(rule.Tunnel) && GetTunnelStatus(rule.Tunnel))
                    ? "\u25cf active" : "\u2014";

            var active = GetActiveTunnelNames();
            TunnelLabel.Text       = active.Count > 0 ? string.Join(", ", active) : "\u2014";
            TunnelLabel.Foreground = active.Count > 0
                ? (SolidColorBrush)FindResource("Green")
                : (SolidColorBrush)FindResource("Sub");

            ((App)System.Windows.Application.Current).UpdateTrayStatus(TunnelLabel.Text, active.Count > 0);
        }

        private void RegisterWifiEvents()
        {
            try
            {
                uint result = WlanOpenHandle(2, IntPtr.Zero, out _, out _wlanHandle);
                if (result != 0) { Log("WlanApi unavailable (code " + result + "), using timer fallback.", LogLevel.Warn); _wlanHandle = IntPtr.Zero; return; }

                // Keep delegate alive — GC would collect it otherwise and crash
                _wlanCallback = (ref WLAN_NOTIFICATION_DATA data, IntPtr ctx) =>
                {
                    // ACM codes 9 = connected, 10 = disconnected, 21 = roaming
                    if (data.NotificationSource == WLAN_NOTIFICATION_SOURCE_ACM &&
                        (data.NotificationCode == 9 || data.NotificationCode == 10 || data.NotificationCode == 21))
                    {
                        // Must marshal back to UI thread
                        Dispatcher.BeginInvoke(new Action(OnWifiChanged));
                    }
                };

                WlanRegisterNotification(_wlanHandle, WLAN_NOTIFICATION_SOURCE_ACM, true, _wlanCallback, IntPtr.Zero, IntPtr.Zero, out _);
                Log("WiFi event monitoring active.", LogLevel.Ok);
            }
            catch (Exception ex) { Log("WlanApi error: " + ex.Message + " — using timer fallback.", LogLevel.Warn); }
        }

        // ── Rule logic ───────────────────────────────────────────────────────

        private void ApplyRules(string? ssid)
        {
            TunnelRule? match = null;
            if (ssid != null)
                match = _cfg.Rules.FirstOrDefault(r =>
                    string.Equals(r.Ssid.Trim(), ssid.Trim(), StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                if (string.IsNullOrEmpty(match.Tunnel))
                {
                    Log("Rule matched: " + ssid + " -> disconnect all", LogLevel.Ok);
                    DisconnectAll();
                }
                else
                {
                    Log("Rule matched: " + ssid + " -> activate " + match.Tunnel, LogLevel.Ok);
                    SwitchTo(match.Tunnel);
                }
            }
            else
            {
                switch (_cfg.DefaultAction)
                {
                    case "disconnect":
                        Log("No rule for " + (ssid ?? "disconnected") + " - disconnecting all.", LogLevel.Info);
                        DisconnectAll();
                        break;
                    case "activate" when !string.IsNullOrEmpty(_cfg.DefaultTunnel):
                        Log("No rule for " + (ssid ?? "disconnected") + " - activating default: " + _cfg.DefaultTunnel, LogLevel.Info);
                        SwitchTo(_cfg.DefaultTunnel);
                        break;
                    default:
                        Log("No rule for " + (ssid ?? "disconnected") + " - doing nothing.", LogLevel.Info);
                        break;
                }
            }
        }

        private void DisconnectAll()
        {
            foreach (var name in GetActiveTunnelNames())
                Log("  Stopped " + name + ": " + (StopTunnel(name) ? "OK" : "failed"), LogLevel.Warn);
        }

        private void SwitchTo(string target)
        {
            foreach (var name in GetActiveTunnelNames().Where(n => n != target))
            {
                StopTunnel(name);
                Log("  Stopped " + name, LogLevel.Warn);
            }

            if (GetTunnelStatus(target))
            {
                Log("  " + target + " already active.", LogLevel.Info);
                return;
            }

            bool ok = StartTunnel(target);
            Log("  Started " + target + ": " + (ok ? "OK" : "FAILED"), ok ? LogLevel.Ok : LogLevel.Warn);
            if (!ok && !string.IsNullOrEmpty(LastError)) Log("  sc.exe: " + LastError, LogLevel.Warn);
        }

        // ── WireGuard helpers ────────────────────────────────────────────────

        private static string SvcName(string t) => "WireGuardTunnel$" + t;

        private static bool RunWg(string args)
        {
            try
            {
                var psi = new ProcessStartInfo(WgExe, args)
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using var p = Process.Start(psi)!;
                p.WaitForExit(15000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        private static bool StartTunnel(string tunnel)
        {
            // wireguard.exe /tunnel start does not require the WireGuard GUI to be running.
            // It talks directly to the WireGuard system service (WireGuardManager / the tunnel service).
            string err;
            if (RunWg("/tunnel start " + tunnel, out err))
                return true;

            // Fallback: sc.exe start (works if service is already registered and just stopped)
            bool ok = RunSc("start " + SvcName(tunnel), out err);
            if (!ok) LastError = err;
            return ok;
        }

        private static bool StopTunnel(string tunnel)
        {
            string err;
            if (RunWg("/tunnel stop " + tunnel, out err))
                return true;

            bool ok = RunSc("stop " + SvcName(tunnel), out err);
            if (!ok) LastError = err;
            return ok;
        }

        internal static string LastError = "";

        private static bool RunWg(string args, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo(WgExe, args)
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using var p = Process.Start(psi)!;
                output = p.StandardOutput.ReadToEnd().Trim() + p.StandardError.ReadToEnd().Trim();
                p.WaitForExit(15000);
                return p.ExitCode == 0;
            }
            catch (Exception ex) { output = ex.Message; return false; }
        }

        private static bool RunSc(string args, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo("sc.exe", args)
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using var p = Process.Start(psi)!;
                output = p.StandardOutput.ReadToEnd().Trim() + p.StandardError.ReadToEnd().Trim();
                p.WaitForExit(15000);
                return p.ExitCode == 0 || p.ExitCode == 1056;
            }
            catch (Exception ex) { output = ex.Message; return false; }
        }

        private static bool GetTunnelStatus(string tunnel)
        {
            try
            {
                using var svc = new ServiceController(SvcName(tunnel));
                return svc.Status == ServiceControllerStatus.Running;
            }
            catch { return false; }
        }

        private static List<string> GetActiveTunnelNames()
        {
            try
            {
                return ServiceController.GetServices()
                    .Where(s => s.ServiceName.StartsWith("WireGuardTunnel$", StringComparison.OrdinalIgnoreCase)
                             && s.Status == ServiceControllerStatus.Running)
                    .Select(s => s.ServiceName.Substring("WireGuardTunnel$".Length))
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        // ── WiFi detection ────────────────────────────────────────────────────

        private static string? GetCurrentSsid()
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
                    { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var proc = Process.Start(psi)!;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);
                foreach (var line in output.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith("SSID") && !t.Contains("BSSID"))
                    {
                        var idx = t.IndexOf(':');
                        if (idx >= 0)
                        {
                            var ssid = t.Substring(idx + 1).Trim();
                            if (!string.IsNullOrEmpty(ssid)) return ssid;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        // ── Config ────────────────────────────────────────────────────────────

        private void LoadConfig()
        {
            _loading = true;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    _cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), opts) ?? new AppConfig();
                }
            }
            catch (Exception ex)
            {
                Log("Config load error: " + ex.Message, LogLevel.Warn);
                _cfg = new AppConfig();
            }
            finally
            {
                _rules.Clear();
                foreach (var r in _cfg.Rules) _rules.Add(r);

                ActionNone.IsChecked     = false;
                ActionDiscon.IsChecked   = false;
                ActionActivate.IsChecked = false;
                switch (_cfg.DefaultAction)
                {
                    case "disconnect": ActionDiscon.IsChecked   = true; break;
                    case "activate":   ActionActivate.IsChecked = true; break;
                    default:           ActionNone.IsChecked     = true; break;
                }

                DefaultTunnelBox.Text = _cfg.DefaultTunnel;
                _loading = false;
            }
        }

        private void SaveConfig()
        {
            try
            {
                _cfg.Rules = _rules.ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.WriteAllText(ConfigPath,
                    JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true }));
                Log("Config saved (" + _cfg.Rules.Count + " rule(s))", LogLevel.Ok);
            }
            catch (Exception ex)
            {
                Log("Save error: " + ex.Message, LogLevel.Warn);
                System.Windows.MessageBox.Show("Failed to save:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── UI events ──────────────────────────────────────────────────────────

        private void AddRule_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Views.RuleDialog(GetCurrentSsid()) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _rules.Add(new TunnelRule { Ssid = dlg.ResultSsid, Tunnel = dlg.ResultTunnel });
                SaveConfig();
                Log("Rule added: " + dlg.ResultSsid + " -> " +
                    (string.IsNullOrEmpty(dlg.ResultTunnel) ? "disconnect" : dlg.ResultTunnel), LogLevel.Ok);
            }
        }

        private void EditRule_Click(object sender, RoutedEventArgs e)
        {
            if (RulesListView.SelectedItem is not TunnelRule rule) return;
            var dlg = new Views.RuleDialog(null, rule.Ssid, rule.Tunnel) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                rule.Ssid   = dlg.ResultSsid;
                rule.Tunnel = dlg.ResultTunnel;
                SaveConfig();
                Log("Rule updated: " + dlg.ResultSsid + " -> " +
                    (string.IsNullOrEmpty(dlg.ResultTunnel) ? "disconnect" : dlg.ResultTunnel), LogLevel.Info);
            }
        }

        private void DeleteRule_Click(object sender, RoutedEventArgs e)
        {
            if (RulesListView.SelectedItem is not TunnelRule rule) return;
            if (System.Windows.MessageBox.Show("Delete rule for " + rule.Ssid + "?", "Delete Rule",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _rules.Remove(rule);
                SaveConfig();
                Log("Rule deleted: " + rule.Ssid, LogLevel.Warn);
            }
        }

        private void ActivateRule_Click(object sender, RoutedEventArgs e)
        {
            if (RulesListView.SelectedItem is not TunnelRule rule) return;
            if (string.IsNullOrEmpty(rule.Tunnel))
            {
                Log("Manual: disconnecting all tunnels.", LogLevel.Info);
                DisconnectAll();
            }
            else
            {
                Log("Manual: activating " + rule.Tunnel, LogLevel.Info);
                SwitchTo(rule.Tunnel);
            }
        }

        private void RulesListView_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            bool sel          = RulesListView.SelectedItem != null;
            EditBtn.IsEnabled     = sel;
            DeleteBtn.IsEnabled   = sel;
            ActivateBtn.IsEnabled = sel;
        }

        private void RefreshTunnels_Click(object sender, RoutedEventArgs e)
        {
            var active = GetActiveTunnelNames();
            TunnelLabel.Text = active.Count > 0 ? string.Join(", ", active) : "\u2014";
            Log("Active tunnels: " + (active.Count > 0 ? string.Join(", ", active) : "none"), LogLevel.Info);
        }

        private void DefaultAction_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if      (ActionNone.IsChecked     == true) _cfg.DefaultAction = "none";
            else if (ActionDiscon.IsChecked   == true) _cfg.DefaultAction = "disconnect";
            else                                       _cfg.DefaultAction = "activate";
            SaveConfig();
        }

        private void DefaultTunnelBox_TextChanged(object sender,
            System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_loading) return;
            _cfg.DefaultTunnel = DefaultTunnelBox.Text.Trim();
        }

        private void DefaultTunnelBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _cfg.DefaultTunnel = DefaultTunnelBox.Text.Trim();
            SaveConfig();
        }

        // ── Logging ────────────────────────────────────────────────────────────

        private enum LogLevel { Info, Ok, Warn }
        private static readonly SolidColorBrush LInfo = new(Color.FromRgb(88,  166, 255));
        private static readonly SolidColorBrush LOk   = new(Color.FromRgb(63,  185,  80));
        private static readonly SolidColorBrush LWarn = new(Color.FromRgb(247, 129, 102));
        private static readonly SolidColorBrush LTime = new(Color.FromRgb(48,   54,  61));

        private void Log(string message, LogLevel level)
        {
            try
            {
                var para = new Paragraph { Margin = new Thickness(0) };
                para.Inlines.Add(new Run(DateTime.Now.ToString("HH:mm:ss") + "  ") { Foreground = LTime });
                para.Inlines.Add(new Run(message) { Foreground = level switch { LogLevel.Ok => LOk, LogLevel.Warn => LWarn, _ => LInfo } });
                if (LogDocument.Blocks.Count > 300) LogDocument.Blocks.Remove(LogDocument.Blocks.LastBlock);
                if (LogDocument.Blocks.FirstBlock != null)
                    LogDocument.Blocks.InsertBefore(LogDocument.Blocks.FirstBlock, para);
                else
                    LogDocument.Blocks.Add(para);
            }
            catch { }
        }

        // ── Window chrome ──────────────────────────────────────────────────────

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e) => Hide();
        private void CloseBtn_Click(object sender, RoutedEventArgs e)    => Hide();
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_wlanHandle != IntPtr.Zero) { WlanCloseHandle(_wlanHandle, IntPtr.Zero); _wlanHandle = IntPtr.Zero; }
            base.OnClosed(e);
        }

        private void UpdateAdminLabel()
        {
            bool admin = new System.Security.Principal.WindowsPrincipal(
                System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            AdminLabel.Text       = admin ? "\u25cf ADMIN" : "\u26a0 NOT ADMIN";
            AdminLabel.Foreground = admin ? (SolidColorBrush)FindResource("Green") : (SolidColorBrush)FindResource("Red");
        }
    }
}
