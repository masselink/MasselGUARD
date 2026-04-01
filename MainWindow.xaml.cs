using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.ServiceProcess;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MasselGUARD
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

    // Represents a known tunnel with live status for the manual panel
    public enum TunnelType { Local, WireGuard }

    public class TunnelEntry : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _active      = false;
        private bool _available   = true;

        public string     Name   { get; set; } = "";
        public TunnelType Type   { get; set; } = TunnelType.Local;

        public bool Active
        {
            get => _active;
            set { _active = value; OnProp(); OnProp(nameof(StatusText)); OnProp(nameof(StatusColor)); OnProp(nameof(ButtonLabel)); OnProp(nameof(ButtonEnabled)); }
        }

        public bool IsAvailable
        {
            get => _available;
            set { _available = value; OnProp(); OnProp(nameof(StatusText)); OnProp(nameof(StatusColor)); OnProp(nameof(NameColor)); OnProp(nameof(NameDecoration)); OnProp(nameof(ButtonEnabled)); OnProp(nameof(ButtonTooltip)); }
        }

        public bool WireGuardInstalled { get; set; } = true;

        // ── Display ──────────────────────────────────────────────────────────
        public string StatusText =>
            !_available ? Lang.T("TunnelUnavailable") :
            _active     ? Lang.T("TunnelStatusConnected") :
                          Lang.T("TunnelStatusDisconnected");

        public System.Windows.Media.SolidColorBrush StatusColor =>
            !_available
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(247, 129, 102)) // red/warn
                : _active
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(63, 185, 80))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158));

        public System.Windows.Media.SolidColorBrush NameColor =>
            _available
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(230, 237, 243))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158));

        public System.Windows.TextDecorationCollection? NameDecoration =>
            _available ? null : System.Windows.TextDecorations.Strikethrough;

        public string ButtonLabel   => _active ? Lang.T("TunnelBtnDisconnect") : Lang.T("TunnelBtnConnect");
        public bool   ButtonEnabled => _available && (Type == TunnelType.Local || WireGuardInstalled);
        public string ButtonTooltip =>
            !_available                             ? Lang.T("TunnelUnavailableTooltip") :
            (!WireGuardInstalled && Type != TunnelType.Local) ? Lang.T("TunnelWireGuardNotInstalled") : "";

        public string TypeLabel => Type == TunnelType.Local
            ? Lang.T("TunnelTypeLocal")
            : Lang.T("TunnelTypeWireGuard");

        public System.Windows.Media.SolidColorBrush TypeColor =>
            Type == TunnelType.Local
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(88, 166, 255))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 158));

        public void RefreshLabels()
        {
            OnProp(nameof(StatusText));
            OnProp(nameof(ButtonLabel));
            OnProp(nameof(TypeLabel));
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnProp([System.Runtime.CompilerServices.CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
    }

    // ── App mode ──────────────────────────────────────────────────────────────
    /// <summary>
    /// Controls which tunnel backends are available.
    ///   Standalone  — only tunnel.dll (local DLLs required, no WireGuard app needed)
    ///   Companion   — only the official WireGuard app (via ServiceController / wireguard.exe)
    ///   Mixed       — both backends available simultaneously (default)
    /// </summary>
    public enum AppMode { Standalone, Companion, Mixed }

        public class StoredTunnel
    {
        public string  Name   { get; set; } = "";
        public string  Config { get; set; } = "";  // raw .conf text — used for locally created tunnels
        public string  Source { get; set; } = "local"; // "local" or "wireguard"
        public string? Path   { get; set; } = null;    // original file path — used for wireguard tunnels
    }

    public class AppConfig
    {
        public List<TunnelRule>   Rules              { get; set; } = new();
        public List<StoredTunnel> Tunnels            { get; set; } = new();
        public string             DefaultAction      { get; set; } = "none";
        public string             DefaultTunnel      { get; set; } = "";
        public string             InstallDirectory   { get; set; } = @"C:\Program Files\WireGuard";
        public string             Language           { get; set; } = "en";
        public string?            InstalledPath      { get; set; } = null;
        public DateTime           LastUpdateCheck    { get; set; } = DateTime.MinValue;
        public string?            LatestKnownVersion { get; set; } = null;
        public bool               ManualMode                  { get; set; } = false;
        public string             LogLevelSetting             { get; set; } = "normal";
        // When true the startup prompt "you are running a portable copy while
        // an installed version exists — update it?" is silently skipped.
        public bool               SuppressPortableUpdatePrompt { get; set; } = false;

        // ── App mode (replaces the old EnableLocalTunnels boolean) ───────────
        public AppMode Mode { get; set; } = AppMode.Standalone;

        // Backward-compat shim: old config.json files had "EnableLocalTunnels"
        // Deserialise it and forward to Mode on first load.
        [JsonIgnore] private bool _legacyLocalTunnels = true;
        public bool EnableLocalTunnels
        {
            get => Mode != AppMode.Companion;
            set { _legacyLocalTunnels = value;
                  // Only downgrade Mixed→Companion; don't overwrite an already-set Mode
                  if (!value && Mode == AppMode.Mixed) Mode = AppMode.Companion; }
        }

        [JsonIgnore]
        public string ConfDirectory => string.IsNullOrWhiteSpace(InstallDirectory)
            ? ""
            : Path.Combine(InstallDirectory, @"Data\Configurations");

        [JsonIgnore]
        public string WgExePath => string.IsNullOrWhiteSpace(InstallDirectory)
            ? "wireguard"
            : Path.Combine(InstallDirectory, "wireguard.exe");

        // ── Language persistence helpers ─────────────────────────────────────
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MasselGUARD", "config.json");

        public static void SaveLanguage(string code)
        {
            try
            {
                AppConfig cfg = new();
                if (File.Exists(ConfigPath))
                {
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), opts) ?? new AppConfig();
                }
                cfg.Language = code;
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                File.WriteAllText(ConfigPath,
                    JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public static string LoadLanguage()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return "en";
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var cfg  = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), opts);
                return cfg?.Language ?? "en";
            }
            catch { return "en"; }
        }
    }

    public partial class MainWindow : Window
    {
        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MasselGUARD", "config.json");

        private static AppConfig _cfg = new();
        private readonly ObservableCollection<TunnelRule>  _rules   = new();
        private readonly ObservableCollection<TunnelEntry> _tunnels = new();
        private string? _lastWifi;
        private readonly DispatcherTimer _timer = new();
        private Ringlogger?              _ringlogger;
        private string                   _ringloggerTunnel = "";
        private bool _loading = false;

        // WireGuard executable — derived from configured install directory
        private static string WgExe => _cfg.WgExePath;

        // Called by App before MainWindow is created to validate dependencies
        public static string? FindWireGuardExe()
        {
            // Try registry-detected path first
            var detected = DetectWireGuardInstallDir();
            if (detected != null)
            {
                var path = System.IO.Path.Combine(detected, "wireguard.exe");
                if (File.Exists(path)) return path;
            }
            // Fallback: just check Program Files
            var def = @"C:\Program Files\WireGuard\wireguard.exe";
            return File.Exists(def) ? def : null;
        }

        // Auto-detect WireGuard install directory from the registry, then common paths
        private static string? DetectWireGuardInstallDir()
        {
            // 1. Registry: HKLM\SOFTWARE\WireGuard — InstallDirectory value
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WireGuard");
                if (key?.GetValue("InstallDirectory") is string dir && Directory.Exists(dir))
                    return dir.TrimEnd('\\', '/');
            }
            catch { }

            // 2. Registry: uninstall entry written by the WireGuard NSIS installer
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WireGuard");
                if (key?.GetValue("InstallLocation") is string dir && Directory.Exists(dir))
                    return dir.TrimEnd('\\', '/');
            }
            catch { }

            // 3. Well-known default paths
            foreach (var candidate in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard"),
                @"C:\WireGuard",
            })
            {
                if (File.Exists(Path.Combine(candidate, "wireguard.exe")))
                    return candidate;
            }

            return null;
        }

        // Returns the .conf file path — both local and WireGuard use a stored path reference
        private static string? FindConfPath(string tunnel, out string searched)
        {
            searched = "";

            // Check stored reference (both local and wireguard)
            var stored = _cfg.Tunnels.FirstOrDefault(t =>
                string.Equals(t.Name, tunnel, StringComparison.OrdinalIgnoreCase));
            if (stored != null && !string.IsNullOrEmpty(stored.Path) && File.Exists(stored.Path))
                return stored.Path;

            // Fallback: scan WireGuard install directories
            var dirs = new List<string>();
            if (!string.IsNullOrWhiteSpace(_cfg.ConfDirectory))
                dirs.Add(_cfg.ConfDirectory);
            dirs.AddRange(new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),       "WireGuard"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),  "WireGuard"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WireGuard"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),          "WireGuard"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),          "WireGuard", "Data"),
                @"C:\WireGuard",
            });
            foreach (var dir in dirs)
            {
                var p  = Path.Combine(dir, tunnel + ".conf");
                if (File.Exists(p))  return p;
                var pd = Path.Combine(dir, tunnel + ".conf.dpapi");
                if (File.Exists(pd)) return pd;
            }
            return null;
        }
        private static string? FindConfPath(string tunnel) => FindConfPath(tunnel, out _);
        // Returns tunnel names stored in config.json
        internal static List<string> GetAvailableTunnels() =>
            _cfg.Tunnels
                .Where(t => IsSourceAllowed(t.Source))
                .Select(t => t.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n)
                .ToList();

        /// <summary>Returns true when the given tunnel source is allowed by the current mode.</summary>
        private static bool IsSourceAllowed(string source) => _cfg.Mode switch
        {
            AppMode.Standalone => source == "local",
            AppMode.Companion  => source != "local",
            _                  => true   // Mixed
        };

        private static List<string> GetTunnelsFromFiles()
        {
            var candidates = new List<string>();

            // Configured install dir → derive conf path
            if (!string.IsNullOrWhiteSpace(_cfg.ConfDirectory))
                candidates.Add(_cfg.ConfDirectory);

            // Registry auto-detect
            var detected = DetectWireGuardInstallDir();
            if (detected != null)
            {
                var detectedConf = Path.Combine(detected, "Data", "Configurations");
                if (!candidates.Contains(detectedConf, StringComparer.OrdinalIgnoreCase))
                    candidates.Add(detectedConf);
            }
            candidates.AddRange(new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),       "WireGuard", "Data", "Configurations"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),  "WireGuard", "Data", "Configurations"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WireGuard", "Data", "Configurations"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),       "WireGuard"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),  "WireGuard"),
                @"C:\Program Files\WireGuard\Data\Configurations",
                @"C:\ProgramData\WireGuard",
            });

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    if (!Directory.Exists(candidate)) continue;
                    // WireGuard stores configs as .conf (portable) or .conf.dpapi (DPAPI-encrypted)
                    var found = Directory.GetFiles(candidate)
                        .Where(f => f.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)
                                 || f.EndsWith(".conf.dpapi", StringComparison.OrdinalIgnoreCase))
                        .Select(f => {
                            var name = Path.GetFileName(f);
                            if (name.EndsWith(".conf.dpapi", StringComparison.OrdinalIgnoreCase))
                                name = name.Substring(0, name.Length - ".conf.dpapi".Length);
                            else
                                name = Path.GetFileNameWithoutExtension(name);
                            return name;
                        })
                        .OrderBy(n => n)
                        .ToList();
                    if (found.Count > 0)
                    {
                        // Back-derive the install directory from the conf folder
                        var expectedSuffix = Path.DirectorySeparatorChar + "Data" +
                                             Path.DirectorySeparatorChar + "Configurations";
                        if (candidate.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
                        {
                            var install = candidate.Substring(0, candidate.Length - expectedSuffix.Length);
                            if (!string.Equals(_cfg.InstallDirectory, install, StringComparison.OrdinalIgnoreCase))
                                _cfg.InstallDirectory = install;
                        }
                        return found;
                    }
                }
                catch { }
            }
            return new List<string>();
        }

        private static List<string> GetTunnelsFromServices()
        {
            // WireGuard registers tunnel services as "WireGuardTunnel$<TunnelName>"
            // These are visible in the SCM even without access to the conf files
            try
            {
                return ServiceController.GetServices()
                    .Where(s => s.ServiceName.StartsWith("WireGuardTunnel$", StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.ServiceName.Substring("WireGuardTunnel$".Length))
                    .OrderBy(n => n)
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        // ── Constructor ──────────────────────────────────────────────────────

        public MainWindow()
        {
            InitializeComponent();
            RulesListView.ItemsSource  = _rules;
            TunnelsListView.ItemsSource = _tunnels;

            System.Windows.Application.Current.DispatcherUnhandledException += (s, e) =>
            {
                LogRaw("ERROR: " + e.Exception.Message, LogLevel.Warn);
                e.Handled = true;
            };

            Loaded += (_, _) =>
            {
                // Refresh TunnelEntry labels when language changes
                Lang.Instance.LanguageChanged += (_, _) =>
                    Dispatcher.BeginInvoke(() =>
                    {
                        foreach (var t in _tunnels) t.RefreshLabels();
                        UpdateAdminLabel();
                        UpdateFooterLabel();
                        RebuildLog();
                    });

                UpdateAdminLabel();
                UpdateFooterLabel();
                var startedFrom = Environment.ProcessPath
                    ?? AppContext.BaseDirectory;
                Log("LogStartedFrom", LogLevel.Info, startedFrom);
                Log("LogAppStarted", LogLevel.Info);
                LoadConfig();
                ApplyManualMode();
                ApplyLocalTunnelMode();
                SetupTimer();

                // (Local tunnels use wireguard.exe /installtunnelservice — no DLLs needed)

                // Background update check — once every 7 days
                if ((DateTime.UtcNow - _cfg.LastUpdateCheck).TotalDays >= 7)
                    _ = UpdateChecker.CheckAsync(_cfg, SaveConfig, Dispatcher);

                // If running portable while installed elsewhere, prompt to update
                // (unless the user chose "don't ask again").
                if (IsRunningPortableWhileInstalled() && !_cfg.SuppressPortableUpdatePrompt)
                {
                    var installPath = GetInstalledPath()!;
                    var (result, suppress) = ShowUpdatePrompt(
                        Lang.T("UpdatePromptMessage", installPath),
                        Lang.T("UpdatePromptTitle"));
                    if (suppress)
                    {
                        _cfg.SuppressPortableUpdatePrompt = true;
                        SaveConfig();
                    }
                    if (result == MessageBoxResult.Yes)
                        RunUpdate();
                }
            };
        }

        // ── Taskbar icon (force AppWindow style so it appears without WindowStyle) ──

        private const int GWL_EXSTYLE      = -20;
        private const int WS_EX_APPWINDOW  = 0x00040000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);

        // ── Wlan native API ───────────────────────────────────────────────────
        [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);
        [DllImport("wlanapi.dll")] static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr ppInterfaceList);
        [DllImport("wlanapi.dll")] static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, uint opCode, IntPtr reserved, out uint dataSize, out IntPtr ppData, IntPtr wlanOpcodeValueType);
        [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr pMemory);

        // WLAN_INTERFACE_INFO_LIST header: dwNumberOfItems(4) + dwIndex(4) then items
        // WLAN_INTERFACE_INFO: Guid(16) + strInterfaceDescription(512) + isState(4)
        // WlanQueryInterface opCode 7 = wlan_intf_opcode_current_connection
        // WLAN_CONNECTION_ATTRIBUTES starts with: isState(4) + wlanConnectionMode(4) +
        //   strProfileName(512) + wlanAssociationAttributes which starts with:
        //   dot11Ssid = { uSSIDLength(4) + ucSSID(32) }
        private const uint WLAN_INTF_OPCODE_CURRENT_CONNECTION = 7;
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

            // Set window icon to match the tray icon.
            // Important: do NOT wrap the icon in a using block — CreateBitmapSourceFromHIcon
            // needs the HICON to stay alive until WPF has copied the pixels. Instead,
            // extract the handle, create the BitmapSource, then destroy the handle manually.
            try
            {
                var drawingIcon = TrayIconHelper.CreateIcon(false);
                var hIcon = drawingIcon.Handle;
                Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    hIcon,
                    System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                DestroyIcon(hIcon);
                drawingIcon.Dispose();
            }
            catch { }
        }

        // ── Timer ────────────────────────────────────────────────────────────

        private void SetupTimer()
        {
            // Timer: refreshes status and polls the tunnel.dll ring-log
            _timer.Interval = TimeSpan.FromSeconds(2);
            _timer.Tick += (_, _) => { UpdateStatusDisplay(); PollRinglogger(); };
            _timer.Start();

            // Register for instant WiFi change notifications via WlanApi
            RegisterWifiEvents();

            // Set initial state — detect WiFi and apply rules immediately
            var wifi = GetCurrentSsid();
            _lastWifi = wifi;
            UpdateStatusDisplay(wifi);
            if (wifi != null)
            {
                Log("LogStartupWifi", LogLevel.Info, wifi);
                ApplyRules(wifi);
            }
            else
            {
                Log("LogStartupNoWifi", LogLevel.Info);
                ApplyRules(null);
            }
        }

        private void PollRinglogger()
        {
            // Find any running local tunnel and tail its ring-log from tunnel.dll
            var running = _cfg.Tunnels.FirstOrDefault(t =>
                t.Source == "local" && TunnelDll.IsRunning(t.Name));

            if (running == null)
            {
                if (_ringlogger != null)
                {
                    _ringlogger.Dispose();
                    _ringlogger = null;
                    _ringloggerTunnel = "";
                }
                return;
            }

            if (!string.Equals(running.Name, _ringloggerTunnel,
                StringComparison.OrdinalIgnoreCase))
            {
                _ringlogger?.Dispose();
                var logPath = TunnelDll.GetLogFilePath(running.Name, ConfPath(running.Name));
                _ringlogger = new Ringlogger(logPath);
                _ringloggerTunnel = running.Name;
            }

            foreach (var (_, text) in _ringlogger!.CollectNewLines())
                LogRaw($"[wg] {text}", LogLevel.Debug);
        }


        // Called by WlanApi callback when connection state changes
        private void OnWifiChanged()
        {
            var wifi = GetCurrentSsid();
            UpdateStatusDisplay(wifi);
            if (wifi == _lastWifi) return;
            _lastWifi = wifi;
            Log("LogWifiChanged", LogLevel.Info, wifi ?? Lang.T("LogWifiDisconnected"));
            if (!_cfg.ManualMode)
                ApplyRules(wifi);
        }

        public void UpdateStatusDisplay(string? wifi = null)
        {
            wifi ??= GetCurrentSsid();
            WifiLabel.Text       = wifi ?? Lang.T("StatusNone");
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
            RefreshTunnelEntryStatuses();
            RefreshQuickConnectButton();
            RefreshWireGuardLogBtn();
        }

        private void RegisterWifiEvents()
        {
            try
            {
                uint result = WlanOpenHandle(2, IntPtr.Zero, out _, out _wlanHandle);
                if (result != 0) { Log("LogWlanUnavailable", LogLevel.Warn, result); _wlanHandle = IntPtr.Zero; return; }

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
                Log("LogWlanActive", LogLevel.Ok);
            }
            catch (Exception ex) { Log("LogWlanError", LogLevel.Warn, ex.Message); }
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
                    Log("LogRuleMatchedDisconnect", LogLevel.Ok, ssid ?? "");
                    DisconnectAll();
                }
                else
                {
                    Log("LogRuleMatchedActivate", LogLevel.Ok, ssid ?? "", match.Tunnel);
                    SwitchTo(match.Tunnel);
                }
            }
            else
            {
                switch (_cfg.DefaultAction)
                {
                    case "disconnect":
                        Log("LogNoRuleDisconnect", LogLevel.Info, ssid ?? Lang.T("LogWifiDisconnected"));
                        DisconnectAll();
                        break;
                    case "activate" when !string.IsNullOrEmpty(_cfg.DefaultTunnel):
                        Log("LogNoRuleActivate", LogLevel.Info, ssid ?? Lang.T("LogWifiDisconnected"), _cfg.DefaultTunnel);
                        SwitchTo(_cfg.DefaultTunnel);
                        break;
                    default:
                        Log("LogNoRuleNothing", LogLevel.Info, ssid ?? Lang.T("LogWifiDisconnected"));
                        break;
                }
            }
        }

        private void DisconnectAll()
        {
            foreach (var name in GetActiveTunnelNames())
                Log("LogStoppedTunnel", LogLevel.Warn, name, StopTunnel(name) ? Lang.T("LogStoppedTunnelOk") : Lang.T("LogStoppedTunnelFail"));
        }

        private void SwitchTo(string target)
        {
            // Check availability before attempting connection
            var entry = _tunnels.FirstOrDefault(t =>
                string.Equals(t.Name, target, StringComparison.OrdinalIgnoreCase));
            if (entry != null && !entry.IsAvailable)
            {
                Log("LogTunnelUnavailable", LogLevel.Warn, target);
                Dispatcher.BeginInvoke(() =>
                    ShowErrorBanner(Lang.T("TunnelConnectUnavailable", target)));
                return;
            }

            foreach (var name in GetActiveTunnelNames().Where(n => n != target))
            {
                StopTunnel(name);
                Log("LogStoppedTunnel", LogLevel.Warn, name, Lang.T("LogStoppedTunnelOk"));
            }

            if (GetTunnelStatus(target))
            {
                Log("LogAlreadyActive", LogLevel.Info, target);
                return;
            }

            LastError = "";
            bool ok = StartTunnel(target);
            Log("LogStartedTunnel", ok ? LogLevel.Ok : LogLevel.Warn, target, ok ? Lang.T("LogTunnelOk") : Lang.T("LogTunnelFailed"));
            if (!ok && !string.IsNullOrEmpty(LastError)) LogRaw("  " + LastError, LogLevel.Warn);
        }

        // ── WireGuard helpers ────────────────────────────────────────────────

        private static string SvcName(string t) => "WireGuardTunnel$" + t;

        internal static string LastError = "";

        // Ensure WireGuardManager (the WireGuard system service) is running.
        private static void EnsureManagerRunning()
        {
            try
            {
                using var mgr = new ServiceController("WireGuardManager");
                if (mgr.Status == ServiceControllerStatus.Stopped ||
                    mgr.Status == ServiceControllerStatus.Paused)
                {
                    mgr.Start();
                    mgr.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(8));
                }
            }
            catch { }
        }

        private bool StartTunnel(string tunnel)
        {
            LastError = "";
            var stored = _cfg.Tunnels.FirstOrDefault(t =>
                string.Equals(t.Name, tunnel, StringComparison.OrdinalIgnoreCase));

            // ── Local tunnel — tunnel.dll + wireguard.dll ─────────────────────
            if (stored?.Source == "local")
            {
                // Validate DLLs early for a clear error before any SCM call.
                var dllError = TunnelDll.ValidateDlls();
                if (dllError != null)
                { LastError = dllError; return false; }

                // Read the stored .conf from AppData\tunnels\, write a
                // BOM-free copy to Program Files\MasselGUARD\temp\ for the service.
                // LocalSystem can read Program Files; AppData is user-scoped.
                string confPath = ConfPath(tunnel);
                if (!File.Exists(confPath))
                {
                    // Migration: recover from old stored.Path (.conf.dpapi) or inline config
                    var stored2 = _cfg.Tunnels.FirstOrDefault(t =>
                        string.Equals(t.Name, tunnel, StringComparison.OrdinalIgnoreCase));
                    string? recovered = null;
                    if (!string.IsNullOrEmpty(stored2?.Path) && File.Exists(stored2!.Path))
                    {
                        try
                        {
                            recovered = stored2.Path.EndsWith(".conf.dpapi",
                                StringComparison.OrdinalIgnoreCase)
                                ? DpapiDecrypt(File.ReadAllBytes(stored2.Path))
                                : File.ReadAllText(stored2.Path, System.Text.Encoding.UTF8);
                        }
                        catch { }
                    }
                    if (recovered == null && !string.IsNullOrEmpty(stored2?.Config))
                        recovered = stored2!.Config;

                    if (recovered == null)
                    { LastError = $"Config file not found: {confPath}"; return false; }

                    // Persist to new location so future connects skip migration
                    EnsureTunnelsDirExists();
                    File.WriteAllBytes(confPath, DpapiEncrypt(recovered));
                    if (stored2 != null) { stored2.Path = confPath; stored2.Config = ""; }
                    SaveConfig();
                    LogRaw($"Migrated '{tunnel}' → {confPath} (DPAPI)", LogLevel.Info);
                }

                // Decrypt .conf.dpapi → write BOM-free plaintext to SvcTempDir
                // using atomic FileSecurity creation so the file is locked to
                // SYSTEM + Administrators + current user from the very first byte
                // written — no window where inherited permissions apply.
                EnsureTunnelsDirExists();
                string svcConf   = SvcConfPath(tunnel);
                string plaintext = DpapiDecrypt(File.ReadAllBytes(confPath));
                WriteSecure(svcConf, plaintext);

                bool ok = TunnelDll.Connect(tunnel, svcConf,
                    msg => LogRaw(msg, LogLevel.Debug), out var err);

                // Delete the plaintext temp file immediately — the service child
                // process has already parsed the config into the kernel driver and
                // no longer needs the file on disk.
                try { File.Delete(svcConf); } catch { }

                if (!ok) LastError = err;
                return ok;
            }

            // ── WireGuard GUI tunnel ──────────────────────────────────────────
            EnsureManagerRunning();

            try
            {
                using var svc = new ServiceController(SvcName(tunnel));
                if (svc.Status == ServiceControllerStatus.Running) return true;
                svc.Start();
                svc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                return true;
            }
            catch (Exception ex1)
            {
                LastError = $"ServiceController: {ex1.Message}";
            }

            // Fallback: wireguard.exe /installtunnelservice
            string searched;
            var conf = FindConfPath(tunnel, out searched);
            if (conf != null)
            {
                if (RunProcess(WgExe, $"/installtunnelservice \"{conf}\"", out var err2))
                {
                    System.Threading.Thread.Sleep(1500);
                    return true;
                }
                LastError += $"  wireguard.exe: {err2}";
            }
            else
                LastError += $"  No .conf found. Searched: {searched}";

            return false;
        }

        private bool StopTunnel(string tunnel)
        {
            LastError = "";
            var stored = _cfg.Tunnels.FirstOrDefault(t =>
                string.Equals(t.Name, tunnel, StringComparison.OrdinalIgnoreCase));

            // ── Local tunnel ──────────────────────────────────────────────────
            if (stored?.Source == "local")
            {
                TunnelDll.Disconnect(tunnel, out var err);
                if (!string.IsNullOrEmpty(err)) LastError = err;
                // Temp conf was already deleted right after service creation;
                // try again as a safety net in case an earlier run left one.
                try { var f = SvcConfPath(tunnel); if (File.Exists(f)) File.Delete(f); } catch { }
                return true;
            }

            // ── WireGuard tunnel: use ServiceController ───────────────────────
            try
            {
                using var svc = new ServiceController(SvcName(tunnel));
                if (svc.Status == ServiceControllerStatus.Stopped) return true;
                svc.Stop();
                svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                return true;
            }
            catch (Exception ex)
            {
                LastError = "ServiceController stop: " + ex.Message;
                return false;
            }
        }

        private static bool RunProcess(string exe, string args, out string output)
        {
            output = "";
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using var p = Process.Start(psi)!;
                output = p.StandardOutput.ReadToEnd().Trim() + p.StandardError.ReadToEnd().Trim();
                p.WaitForExit(15000);
                return p.ExitCode == 0;
            }
            catch (Exception ex) { output = ex.Message; return false; }
        }

        private bool GetTunnelStatus(string tunnel)
        {
            var stored = _cfg.Tunnels.FirstOrDefault(t =>
                string.Equals(t.Name, tunnel, StringComparison.OrdinalIgnoreCase));

            if (stored?.Source == "local")
                return TunnelDll.IsRunning(tunnel);

            try
            {
                using var svc = new ServiceController(SvcName(tunnel));
                return svc.Status == ServiceControllerStatus.Running;
            }
            catch { return false; }
        }

        private static List<string> GetActiveTunnelNames()
        {
            var active = new List<string>();

            // Local tunnels: tracked in TunnelDll._connected (service exits immediately
            // after tunnel is up, so SCM status is always Stopped for these).
            foreach (var t in _cfg.Tunnels.Where(t => t.Source == "local"))
                if (TunnelDll.IsRunning(t.Name))
                    active.Add(t.Name);

            // WireGuard companion tunnels: live as long as their service is Running.
            try
            {
                foreach (var s in ServiceController.GetServices()
                    .Where(s => s.ServiceName.StartsWith("WireGuardTunnel$", StringComparison.OrdinalIgnoreCase)
                             && s.Status == ServiceControllerStatus.Running))
                {
                    var name = s.ServiceName.Substring("WireGuardTunnel$".Length);
                    if (!active.Contains(name, StringComparer.OrdinalIgnoreCase))
                        active.Add(name);
                }
            }
            catch { }

            return active;
        }

        // Returns true if a Quick Connect session is currently active.
        private bool QuickConnectIsActive() =>
            _quickConnectTunnelName != null
            && TunnelDll.IsRunning(_quickConnectTunnelName);

        // ── WiFi detection ────────────────────────────────────────────────────

        private static string? GetCurrentSsid()
        {
            // Use WlanQueryInterface directly — no process spawn, no event log noise.
            // Falls back to null (not connected / no adapter) on any error.
            try
            {
                uint result = WlanOpenHandle(2, IntPtr.Zero, out _, out IntPtr tempHandle);
                if (result != 0 || tempHandle == IntPtr.Zero) return null;
                try
                {
                    if (WlanEnumInterfaces(tempHandle, IntPtr.Zero, out IntPtr pList) != 0)
                        return null;
                    try
                    {
                        // pList → WLAN_INTERFACE_INFO_LIST:
                        //   DWORD dwNumberOfItems  (offset 0)
                        //   DWORD dwIndex          (offset 4)
                        //   WLAN_INTERFACE_INFO[]  (offset 8, each 532 bytes)
                        int count = Marshal.ReadInt32(pList, 0);
                        for (int i = 0; i < count; i++)
                        {
                            // Interface GUID is the first 16 bytes of each WLAN_INTERFACE_INFO
                            IntPtr itemPtr = pList + 8 + i * 532;
                            byte[] guidBytes = new byte[16];
                            Marshal.Copy(itemPtr, guidBytes, 0, 16);
                            Guid ifGuid = new Guid(guidBytes);

                            uint result2 = WlanQueryInterface(
                                tempHandle, ref ifGuid,
                                WLAN_INTF_OPCODE_CURRENT_CONNECTION,
                                IntPtr.Zero, out _, out IntPtr pConn,
                                IntPtr.Zero);
                            if (result2 != 0 || pConn == IntPtr.Zero) continue;
                            try
                            {
                                // WLAN_CONNECTION_ATTRIBUTES layout:
                                //   isState                (4)  offset 0
                                //   wlanConnectionMode     (4)  offset 4
                                //   strProfileName         (512) offset 8   (256 wchars)
                                //   WLAN_ASSOCIATION_ATTRIBUTES:
                                //     dot11Ssid:
                                //       uSSIDLength        (4)  offset 520
                                //       ucSSID[32]         (32) offset 524
                                int ssidLen = Marshal.ReadInt32(pConn, 520);
                                if (ssidLen <= 0 || ssidLen > 32) continue;
                                byte[] ssidBytes = new byte[ssidLen];
                                Marshal.Copy(pConn + 524, ssidBytes, 0, ssidLen);
                                string ssid = System.Text.Encoding.UTF8.GetString(ssidBytes);
                                if (!string.IsNullOrEmpty(ssid)) return ssid;
                            }
                            finally { WlanFreeMemory(pConn); }
                        }
                    }
                    finally { WlanFreeMemory(pList); }
                }
                finally { WlanCloseHandle(tempHandle, IntPtr.Zero); }
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
                Log("LogConfigLoadError", LogLevel.Warn, ex.Message);
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

                RefreshTunnelDropdowns();
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
                Log("LogConfigSaved", LogLevel.Ok, _cfg.Rules.Count);
            }
            catch (Exception ex)
            {
                Log("LogConfigSaveError", LogLevel.Warn, ex.Message);
                System.Windows.MessageBox.Show("Failed to save:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── UI events ──────────────────────────────────────────────────────────

        private void AddRule_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Views.RuleDialog(GetCurrentSsid(), tunnels: GetAvailableTunnels()) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _rules.Add(new TunnelRule { Ssid = dlg.ResultSsid, Tunnel = dlg.ResultTunnel });
                SaveConfig();
                UpdateCountBadges();
                Log("LogRuleAdded", LogLevel.Ok, dlg.ResultSsid,
                    string.IsNullOrEmpty(dlg.ResultTunnel) ? Lang.T("TunnelBtnDisconnect") : dlg.ResultTunnel);
            }
        }

        private void EditRule_Click(object sender, RoutedEventArgs e)
        {
            if (RulesListView.SelectedItem is not TunnelRule rule) return;
            var dlg = new Views.RuleDialog(null, rule.Ssid, rule.Tunnel, GetAvailableTunnels()) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                rule.Ssid   = dlg.ResultSsid;
                rule.Tunnel = dlg.ResultTunnel;
                SaveConfig();
                Log("LogRuleUpdated", LogLevel.Info, dlg.ResultSsid,
                    string.IsNullOrEmpty(dlg.ResultTunnel) ? Lang.T("TunnelBtnDisconnect") : dlg.ResultTunnel);
            }
        }

        private void DeleteRule_Click(object sender, RoutedEventArgs e)
        {
            if (RulesListView.SelectedItem is not TunnelRule rule) return;
            if (System.Windows.MessageBox.Show(
                    Lang.T("RuleDialogSsidRequired") + "\n" + rule.Ssid + "?",
                    Lang.T("BtnDeleteRule"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _rules.Remove(rule);
                SaveConfig();
                UpdateCountBadges();
                Log("LogRuleDeleted", LogLevel.Warn, rule.Ssid);
            }
        }

        private void RulesListView_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            bool sel          = RulesListView.SelectedItem != null;
            EditBtn.IsEnabled   = sel;
            DeleteBtn.IsEnabled = sel;
        }

        // ── Tunnel management ──────────────────────────────────────────────────

        /// <summary>
        // ── Tunnel storage ─────────────────────────────────────────────────────
        // Encrypted tunnels  → <ExeDir>\tunnels\<n>.conf.dpapi
        //   DPAPI CurrentUser scope — only the owning user can decrypt.
        //   ACL on each file: SYSTEM + Administrators + owning user only.
        //
        // Temp conf files    → %ProgramFiles%\MasselGUARD\temp\<n>.conf
        //   Plaintext without BOM, written just before the service starts,
        //   deleted on disconnect. ACL: SYSTEM + Administrators + current user.
        //   Located in Program Files so LocalSystem can always read it.
        //
        // Storage : %AppData%\MasselGUARD\tunnels\   (DPAPI CurrentUser encrypted)
        // Temp    : %ProgramFiles%\MasselGUARD\temp\  (plaintext, service-readable, ACL-locked)
        private static readonly string TunnelStorageDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MasselGUARD", "tunnels");

        // Temp dir for the plaintext copy the LocalSystem service process reads.
        // In Program Files so the inherited ACL already grants SYSTEM access;
        // we additionally lock it to SYSTEM + Administrators + current user.
        private static readonly string SvcTempDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "MasselGUARD", "temp");

        public static string TunnelStorageDirPublic => TunnelStorageDir;

        // Returns the DPAPI-encrypted .conf.dpapi path for a stored local tunnel
        private static string ConfPath(string name) =>
            Path.Combine(TunnelStorageDir, SafeName(name) + ".conf.dpapi");

        // Returns the temp plaintext .conf path used by the service
        private static string SvcConfPath(string name) =>
            Path.Combine(SvcTempDir, SafeName(name) + ".conf");

        // Returns true if a .conf.dpapi with this name exists on disk regardless
        // of which user created it — used to enforce global name uniqueness.
        private static bool TunnelFileExists(string name) =>
            File.Exists(ConfPath(name));

        // Returns a filesystem- and SCM-safe version of the tunnel name.
        // Replaces spaces and backslashes with underscores, then strips any
        // remaining characters that are invalid in file names or service names.
        // The original display name is stored separately in config.json.
        private static string SafeName(string name)
        {
            // Spaces and backslashes are explicitly not allowed in Windows service
            // names and cause path issues in the SCM binary path argument.
            var safe = name.Replace(' ', '_').Replace('\\', '_');
            // Strip any other characters invalid in file names
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                safe = safe.Replace(c, '_');
            return safe;
        }

        // ── DPAPI helpers ─────────────────────────────────────────────────────
        // Encrypt with CurrentUser scope — only this user (on this machine) can decrypt.
        private static byte[] DpapiEncrypt(string plainText)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(plainText);
            return System.Security.Cryptography.ProtectedData.Protect(
                bytes, null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
        }

        private static string DpapiDecrypt(byte[] cipherBytes)
        {
            var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
                cipherBytes, null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        // ── ACL helpers ───────────────────────────────────────────────────────

        private static void ApplySecureAcl(string path, bool isDirectory = false)
        {
            try
            {
                var flags = System.Security.AccessControl.InheritanceFlags.None;
                var prop  = System.Security.AccessControl.PropagationFlags.None;
                if (isDirectory)
                {
                    flags = System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                            System.Security.AccessControl.InheritanceFlags.ObjectInherit;
                }

                System.Security.AccessControl.FileSystemSecurity acl = isDirectory
                    ? new System.IO.DirectoryInfo(path).GetAccessControl()
                    : new System.IO.FileInfo(path).GetAccessControl();

                acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

                foreach (System.Security.AccessControl.FileSystemAccessRule r in
                    acl.GetAccessRules(true, true,
                        typeof(System.Security.Principal.SecurityIdentifier)))
                    acl.RemoveAccessRule(r);

                void Add(System.Security.Principal.IdentityReference id) =>
                    acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        id,
                        System.Security.AccessControl.FileSystemRights.FullControl,
                        flags, prop,
                        System.Security.AccessControl.AccessControlType.Allow));

                Add(new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.LocalSystemSid, null));
                Add(new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null));
                Add(System.Security.Principal.WindowsIdentity.GetCurrent().User!);

                if (isDirectory)
                    new System.IO.DirectoryInfo(path).SetAccessControl(
                        (System.Security.AccessControl.DirectorySecurity)acl);
                else
                    new System.IO.FileInfo(path).SetAccessControl(
                        (System.Security.AccessControl.FileSecurity)acl);
            }
            catch { /* best-effort */ }
        }

        // Creates a plaintext file with the correct ACL in one atomic step.
        // Using FileStream + FileSecurity at open-time means the file is
        // protected from the moment it is created — no inherited-ACL window.
        private static void WriteSecure(string path, string text)
        {
            var fileSec = new System.Security.AccessControl.FileSecurity();
            fileSec.SetAccessRuleProtection(isProtected: true,
                preserveInheritance: false);

            void Add(System.Security.Principal.IdentityReference id) =>
                fileSec.AddAccessRule(
                    new System.Security.AccessControl.FileSystemAccessRule(
                        id,
                        System.Security.AccessControl.FileSystemRights.FullControl,
                        System.Security.AccessControl.InheritanceFlags.None,
                        System.Security.AccessControl.PropagationFlags.None,
                        System.Security.AccessControl.AccessControlType.Allow));

            Add(new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.LocalSystemSid, null));
            Add(new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null));
            Add(System.Security.Principal.WindowsIdentity.GetCurrent().User!);

            // The 7-arg FileStream(path,mode,rights,share,buf,opts,security) constructor
            // was removed in .NET 6+. Instead: create the file empty, apply the ACL
            // immediately (before any data is written), then write content.
            // The file exists for a few microseconds with no content — the ACL is set
            // before the first byte of plaintext is written.
            using (var empty = File.Create(path)) { /* creates empty, inherits parent ACL */ }
            new FileInfo(path).SetAccessControl(fileSec);  // lock ACL before writing
            using var sw = new StreamWriter(
                new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            sw.Write(text);
        }

        private static void EnsureTunnelsDirExists()
        {
            // tunnels\ — plain user folder, no special ACL needed.
            if (!Directory.Exists(TunnelStorageDir))
                Directory.CreateDirectory(TunnelStorageDir);

            // temp\ — SYSTEM + Administrators + current user only.
            if (!Directory.Exists(SvcTempDir))
            {
                Directory.CreateDirectory(SvcTempDir);
                ApplySecureAcl(SvcTempDir, isDirectory: true);
            }
        }

        // Tunnels directory ACL:
        //   SYSTEM + Administrators  → FullControl (inherited)
        //   Authenticated Users      → ListDirectory + ReadAttributes (dir only, no inherit)
        // This lets any local user check whether a tunnel name is taken without
        // being able to read the encrypted content of another user's .conf.dpapi.
        private static void ApplyTunnelsDirAcl(string dirPath)
        {
            try
            {
                var acl = new System.IO.DirectoryInfo(dirPath).GetAccessControl();
                acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

                var inheritAll = System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                                 System.Security.AccessControl.InheritanceFlags.ObjectInherit;
                var noInherit  = System.Security.AccessControl.InheritanceFlags.None;
                var noProp     = System.Security.AccessControl.PropagationFlags.None;
                var allow      = System.Security.AccessControl.AccessControlType.Allow;

                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    inheritAll, noProp, allow));

                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    inheritAll, noProp, allow));

                // Authenticated Users: list the directory only — not read file content
                acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
                    System.Security.AccessControl.FileSystemRights.ListDirectory |
                    System.Security.AccessControl.FileSystemRights.ReadAttributes |
                    System.Security.AccessControl.FileSystemRights.Traverse,
                    noInherit, noProp, allow));

                new System.IO.DirectoryInfo(dirPath).SetAccessControl(acl);
            }
            catch { /* best-effort */ }
        }

        // ── Tunnel CRUD ───────────────────────────────────────────────────────

        private void SaveTunnelConfig(string name, string config,
            string source = "local", string? filePath = null)
        {
            if (source == "local")
            {
                EnsureTunnelsDirExists();

                // Global duplicate check: if the file exists and is not in this
                // user's config, it belongs to another user on the same machine.
                var existingEntry = _cfg.Tunnels.FirstOrDefault(t =>
                    string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existingEntry == null && TunnelFileExists(name))
                    throw new InvalidOperationException(
                        Lang.T("TunnelNameTakenGlobally", name));

                filePath = ConfPath(name);              // DPAPI-encrypted .conf.dpapi
                File.WriteAllBytes(filePath, DpapiEncrypt(config));
                config = "";    // don't store plaintext in config.json
            }

            var existing = _cfg.Tunnels.FirstOrDefault(t =>
                string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Config = config;
                existing.Source = source;
                existing.Path   = filePath;
            }
            else
            {
                _cfg.Tunnels.Add(new StoredTunnel
                    { Name = name, Config = config, Source = source, Path = filePath });
            }
            SaveConfig();
        }

        private string? LoadTunnelConfig(string name)
        {
            var stored = _cfg.Tunnels.FirstOrDefault(t =>
                string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (stored == null) return null;

            if (!string.IsNullOrEmpty(stored.Path) && File.Exists(stored.Path))
            {
                if (stored.Path.EndsWith(".conf.dpapi", StringComparison.OrdinalIgnoreCase))
                    return DpapiDecrypt(File.ReadAllBytes(stored.Path));
                return File.ReadAllText(stored.Path, System.Text.Encoding.UTF8);
            }
            // Fallback: plaintext stored inline in config.json
            return string.IsNullOrEmpty(stored.Config) ? null : stored.Config;
        }

        private void DeleteTunnelConfig(string name)
        {
            var stored = _cfg.Tunnels.FirstOrDefault(t =>
                string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (stored?.Source == "local")
            {
                // Delete the stored .conf file
                if (!string.IsNullOrEmpty(stored.Path) && File.Exists(stored.Path))
                    try { File.Delete(stored.Path); } catch { }
                // Also try canonical path in case Path is stale
                var canonical = ConfPath(name);
                if (File.Exists(canonical)) try { File.Delete(canonical); } catch { }
            }
            _cfg.Tunnels.RemoveAll(t =>
                string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            SaveConfig();
        }


        private void TunnelsListView_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var entry    = TunnelsListView.SelectedItem as TunnelEntry;
            bool sel     = entry != null;
            bool isLocal = entry?.Type == TunnelType.Local;
            bool isWg    = entry?.Type == TunnelType.WireGuard;
            bool avail   = entry?.IsAvailable ?? false;

            EditTunnelBtn.IsEnabled = sel && isLocal;

            // The action button morphs based on the selected tunnel type/state:
            //  • WireGuard-linked  → "Unlink"  (removes from list, keeps WG conf)
            //  • Local, unavailable → "Remove" (cleans up the stale entry)
            //  • Local, available   → "Delete" (deletes the encrypted conf file)
            if (!sel)
            {
                DeleteTunnelBtn.IsEnabled = false;
                DeleteTunnelBtn.Content   = Lang.T("BtnDeleteTunnel");
            }
            else if (isWg)
            {
                DeleteTunnelBtn.IsEnabled = true;
                DeleteTunnelBtn.Content   = Lang.T("ImportUnlinkWireGuard");
            }
            else if (!avail)
            {
                DeleteTunnelBtn.IsEnabled = true;
                DeleteTunnelBtn.Content   = Lang.T("BtnRemoveTunnel");
            }
            else
            {
                DeleteTunnelBtn.IsEnabled = true;
                DeleteTunnelBtn.Content   = Lang.T("BtnDeleteTunnel");
            }
        }

        private void AddTunnel_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Views.TunnelConfigDialog { Owner = this };
            if (dlg.ShowDialog() != true) return;
            SaveTunnelConfig(dlg.ResultName!, dlg.ResultConfig!);
            Log("TunnelSavedLog", LogLevel.Ok, dlg.ResultName!);
            RefreshTunnelDropdowns();
        }

        private void EditTunnel_Click(object sender, RoutedEventArgs e)
        {
            if (TunnelsListView.SelectedItem is not TunnelEntry entry) return;
            var config = LoadTunnelConfig(entry.Name);
            var dlg    = new Views.TunnelConfigDialog(entry.Name, config) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            // If name changed, delete old file
            if (!string.Equals(dlg.ResultName, entry.Name, StringComparison.OrdinalIgnoreCase))
                DeleteTunnelConfig(entry.Name);

            SaveTunnelConfig(dlg.ResultName!, dlg.ResultConfig!);
            Log("TunnelSavedLog", LogLevel.Ok, dlg.ResultName!);
            RefreshTunnelDropdowns();
        }

        private void ImportTunnel_Click(object sender, RoutedEventArgs e)
        {
            var alreadyImported = new HashSet<string>(
                _cfg.Tunnels.Select(t => t.Name),
                StringComparer.OrdinalIgnoreCase);
            var dlg = new Views.ImportTunnelDialog(alreadyImported, _cfg.Mode)
                { Owner = this };
            dlg.TunnelImported += (name, config, source, path) =>
                SaveImportedTunnel(name, config, source, path);
            dlg.ShowDialog();
        }

        // Unlink a WireGuard-companion tunnel: removes config.json entry only,
        // does not delete any file since the conf belongs to the WireGuard app.
        public void UnlinkWireGuardTunnel(string name)
        {
            var confirm = ShowDialog(
                Lang.T("ImportUnlinkConfirm", name),
                Lang.T("ImportUnlinkTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            // Remove from config only — do not delete the WireGuard app's file
            _cfg.Tunnels.RemoveAll(t =>
                string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            SaveConfig();
            Log("TunnelDeletedLog", LogLevel.Warn, name);
            RefreshTunnelDropdowns();
        }

        private void SaveImportedTunnel(string name, string config, string source = "local", string? path = null)
        {
            // Sanitise name
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            // Duplicate check: first in this user's own config, then globally on disk.
            bool inMyConfig  = _cfg.Tunnels.Any(t =>
                string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            bool takenByOther = !inMyConfig && TunnelFileExists(name);

            if (takenByOther)
            {
                // Another user on this machine already has a tunnel with this name.
                // Refuse silently with an informational dialog — no overwrite option.
                ShowDialog(
                    Lang.T("TunnelNameTakenGlobally", name),
                    Lang.T("ImportDuplicateTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (inMyConfig)
            {
                var overwrite = ShowDialog(
                    Lang.T("ImportDuplicate", name),
                    Lang.T("ImportDuplicateTitle"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (overwrite != MessageBoxResult.Yes) return;
            }

            try
            {
                SaveTunnelConfig(name, config, source, path);
                Log("TunnelImportedLog", LogLevel.Ok, name);
                Dispatcher.BeginInvoke(RefreshTunnelDropdowns);
            }
            catch (Exception ex)
            {
                Log("ImportFailed", LogLevel.Warn, ex.Message);
            }
        }

        private void DeleteTunnel_Click(object sender, RoutedEventArgs e)
        {
            if (TunnelsListView.SelectedItem is not TunnelEntry entry) return;

            if (entry.Type == TunnelType.WireGuard)
            {
                // WireGuard-linked: unlink (remove from list, keep WG app conf)
                UnlinkWireGuardTunnel(entry.Name);
                return;
            }

            if (!entry.IsAvailable)
            {
                // Unavailable local tunnel: remove stale entry without confirmation
                // (the encrypted file is already gone so there is nothing to lose)
                var confirm = ShowDialog(
                    Lang.T("RemoveTunnelConfirm", entry.Name),
                    Lang.T("RemoveTunnelTitle"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;
                _cfg.Tunnels.RemoveAll(t =>
                    string.Equals(t.Name, entry.Name,
                        StringComparison.OrdinalIgnoreCase));
                SaveConfig();
                Log("TunnelDeletedLog", LogLevel.Warn, entry.Name);
                RefreshTunnelDropdowns();
                return;
            }

            // Local available tunnel: confirm + delete encrypted file
            var del = ShowDialog(
                Lang.T("DeleteTunnelConfirm", entry.Name),
                Lang.T("DeleteTunnelTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (del != MessageBoxResult.Yes) return;
            DeleteTunnelConfig(entry.Name);
            Log("TunnelDeletedLog", LogLevel.Warn, entry.Name);
            RefreshTunnelDropdowns();
        }

        private void AuthorLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("https://github.com/masselink/MasselGUARD")
                      { UseShellExecute = true }); }
            catch { }
        }

        private void DefaultAction_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if      (ActionNone.IsChecked     == true) _cfg.DefaultAction = "none";
            else if (ActionDiscon.IsChecked   == true) _cfg.DefaultAction = "disconnect";
            else                                       _cfg.DefaultAction = "activate";
            SaveConfig();
        }

        private void RefreshTunnelDropdowns()
        {
            var tunnels = GetAvailableTunnels();

            LogRaw(Lang.T("LogTunnelDiscovery", tunnels.Count) +
                (tunnels.Count > 0 ? Lang.T("LogTunnelDiscoveryList", string.Join(", ", tunnels)) : "") +
                Lang.T("LogInstallDir", _cfg.InstallDirectory ?? "none"), LogLevel.Info);

            // Update DefaultTunnelBox ComboBox
            var prev = DefaultTunnelBox.Text;
            DefaultTunnelBox.Items.Clear();
            foreach (var t in tunnels) DefaultTunnelBox.Items.Add(t);
            DefaultTunnelBox.Text = prev;

            // Rebuild tunnel panel list
            var active      = GetActiveTunnelNames();
            var wgInstalled = FindWireGuardExe() != null;
            _tunnels.Clear();
            foreach (var t in tunnels)
            {
                var stored = _cfg.Tunnels.FirstOrDefault(s =>
                    string.Equals(s.Name, t, StringComparison.OrdinalIgnoreCase));
                var isLocal = stored?.Source != "wireguard";
                _tunnels.Add(new TunnelEntry
                {
                    Name               = t,
                    Active             = active.Contains(t),
                    Type               = isLocal ? TunnelType.Local : TunnelType.WireGuard,
                    WireGuardInstalled = wgInstalled
                });
            }

            // Rebuild tray menu
            ((App)System.Windows.Application.Current).RebuildTrayTunnelMenu(tunnels, active);

            // Update count badges
            TunnelCountLabel.Text = tunnels.Count.ToString();
            RuleCountLabel.Text   = _rules.Count.ToString();

            // Check availability of all tunnels
            CheckTunnelAvailability();
            UpdateCountBadges();
        }

        private void UpdateCountBadges()
        {
            TunnelCountLabel.Text = _tunnels.Count.ToString();
            RuleCountLabel.Text   = _rules.Count.ToString();
        }

        // Called from UpdateStatusDisplay to refresh live Active flags without rebuilding
        private void RefreshTunnelEntryStatuses()
        {
            var active      = GetActiveTunnelNames();
            var wgInstalled = FindWireGuardExe() != null;
            foreach (var e in _tunnels)
            {
                e.WireGuardInstalled = wgInstalled;
                e.Active = active.Contains(e.Name);
            }

            // Inject or update the synthetic Quick Connect entry.
            SyncQuickConnectEntry();

            ((App)System.Windows.Application.Current).RebuildTrayTunnelMenu(
                _tunnels.Select(t => t.Name).ToList(), active);
        }

        // Keeps a synthetic TunnelEntry for the active Quick Connect session
        // in sync with _tunnels so it appears in the list and can be disconnected
        // by clicking its toggle button like any other tunnel.
        private void SyncQuickConnectEntry()
        {
            const string QcPrefix = "⚡ ";
            bool qcActive = QuickConnectIsActive();

            // Remove stale QC entry (session ended or name changed)
            var stale = _tunnels.Where(t => t.Name.StartsWith(QcPrefix)).ToList();
            foreach (var s in stale)
                if (!qcActive || !string.Equals(s.Name,
                        QcPrefix + _quickConnectDisplayName,
                        StringComparison.OrdinalIgnoreCase))
                    _tunnels.Remove(s);

            if (!qcActive) return;

            string displayName = QcPrefix + (_quickConnectDisplayName ?? "Quick Connect");
            var existing = _tunnels.FirstOrDefault(t =>
                string.Equals(t.Name, displayName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Active = true;
                return;
            }

            // Insert at top of list so it is immediately visible
            _tunnels.Insert(0, new TunnelEntry
            {
                Name               = displayName,
                Active             = true,
                Type               = TunnelType.Local,
                WireGuardInstalled = true,
                IsAvailable        = true,
            });
        }

        private void DefaultTunnelBox_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (DefaultTunnelBox.SelectedItem is string s)
            {
                _cfg.DefaultTunnel = s;
                SaveConfig();
            }
        }

        private void DefaultTunnelBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _cfg.DefaultTunnel = DefaultTunnelBox.Text.Trim();
            SaveConfig();
        }

        public void ManualStart(string tunnel)
        {
            Log("LogManualConnect", LogLevel.Info, tunnel);
            LastError = "";
            bool ok = StartTunnel(tunnel);
            Log("LogManualConnectResult", ok ? LogLevel.Ok : LogLevel.Warn, tunnel, ok ? Lang.T("TunnelStatusConnected") : Lang.T("LogTunnelFailed"));
            if (!ok && !string.IsNullOrEmpty(LastError)) LogRaw("  " + LastError, LogLevel.Warn);
        }

        public void ManualStop(string tunnel)
        {
            Log("LogManualDisconnect", LogLevel.Info, tunnel);
            bool ok = StopTunnel(tunnel);
            Log("LogManualConnectResult", ok ? LogLevel.Ok : LogLevel.Warn, tunnel, ok ? Lang.T("TunnelStatusDisconnected") : Lang.T("LogTunnelFailed"));
        }

        // ── Tunnel availability ────────────────────────────────────────────────

        /// <summary>
        /// Checks whether each tunnel's config file still exists.
        /// Marks unavailable tunnels visually and logs a warning for newly unavailable ones.
        /// Called on startup and every time the window is shown.
        /// </summary>
        private void CheckTunnelAvailability()
        {
            var wgInstalled = FindWireGuardExe() != null;
            foreach (var entry in _tunnels)
            {
                bool available = IsTunnelConfigAvailable(entry.Name, entry.Type);
                if (!available && entry.IsAvailable)
                    Log("LogTunnelUnavailable", LogLevel.Warn, entry.Name);
                entry.IsAvailable          = available;
                entry.WireGuardInstalled   = wgInstalled;
            }
        }

        private static bool IsTunnelConfigAvailable(string name, TunnelType type)
        {
            var stored = _cfg.Tunnels.FirstOrDefault(t =>
                string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (stored == null) return false;

            if (stored.Source == "local")
                return File.Exists(ConfPath(name));   // .conf.dpapi in tunnels\

            // WireGuard tunnel: original file path must still exist
            return !string.IsNullOrEmpty(stored.Path) && File.Exists(stored.Path);
        }

        private void ShowErrorBanner(string message)
        {
            ErrorBannerText.Text    = message;
            ErrorBanner.Visibility  = System.Windows.Visibility.Visible;
        }

        private void DismissBanner_Click(object sender, RoutedEventArgs e)
        {
            ErrorBanner.Visibility = System.Windows.Visibility.Collapsed;
        }

        private async void TunnelToggle_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as System.Windows.Controls.Button)?.DataContext is not TunnelEntry entry) return;

            // Quick Connect synthetic entry: route to QuickConnect_Click which
            // already handles the active→disconnect toggle internally.
            if (entry.Name.StartsWith("⚡ ", StringComparison.Ordinal))
            {
                QuickConnect_Click(sender, e);
                return;
            }

            // Disable the button while the operation is in progress so the user
            // cannot double-click and to give visual feedback.
            if (sender is System.Windows.Controls.Button btn) btn.IsEnabled = false;
            try
            {
                if (entry.Active)
                {
                    // StopTunnel calls ServiceController and waits — run off UI thread.
                    await System.Threading.Tasks.Task.Run(() => ManualStop(entry.Name));
                }
                else
                {
                    // StartTunnel → TunnelDll.Connect → InstallAndStart blocks up to 30 s.
                    // Running it on a thread-pool thread keeps the UI responsive.
                    await System.Threading.Tasks.Task.Run(() => ManualStart(entry.Name));
                }
            }
            finally
            {
                if (sender is System.Windows.Controls.Button b) b.IsEnabled = true;
                UpdateStatusDisplay();
            }
        }

        // ── Install / Uninstall ────────────────────────────────────────────────

        private static readonly string InstallRegKey =
            @"SOFTWARE\MasselGUARD";
        private static readonly string InstallFolderName =
            "MasselGUARD";

        private static string? GetInstalledPath()
        {
            // Primary: config.json (user-scoped, no registry dependency)
            try
            {
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                if (File.Exists(ConfigPath))
                {
                    var cfg = JsonSerializer.Deserialize<AppConfig>(
                        File.ReadAllText(ConfigPath), opts);
                    if (!string.IsNullOrEmpty(cfg?.InstalledPath))
                        return cfg.InstalledPath;
                }
            }
            catch { }

            // Fallback: registry (written by previous installs)
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(InstallRegKey);
                return key?.GetValue("InstallPath") as string;
            }
            catch { return null; }
        }

        private static bool IsInstalled() =>
            GetInstalledPath() is string p && File.Exists(Path.Combine(p, "MasselGUARD.exe"));

        private void UpdateInstallButton()
        {
            // Notify settings window to refresh its install state if open
            if (_settingsWindow != null && _settingsWindow.IsVisible)
                _settingsWindow.RefreshInstallState();
            UpdateFooterLabel();
        }

        private void UpdateFooterLabel()
        {
            var installedPath = GetInstalledPath();
            var currentDir    = Path.GetDirectoryName(
                Environment.ProcessPath ?? AppContext.BaseDirectory) ?? "";
            bool installed = installedPath != null &&
                             File.Exists(Path.Combine(installedPath, "MasselGUARD.exe"));
            bool runningFromInstall = installed &&
                string.Equals(currentDir, installedPath, StringComparison.OrdinalIgnoreCase);

            if (_cfg.ManualMode)
            {
                FooterLabel.Text       = Lang.T("StatusManualMode");
                FooterLabel.Foreground = (SolidColorBrush)FindResource("Sub");
            }
            else if (installed && runningFromInstall)
            {
                FooterLabel.Text       = Lang.T("StatusManaged");
                FooterLabel.Foreground = (SolidColorBrush)FindResource("Green");
            }
            else if (installed && !runningFromInstall)
            {
                FooterLabel.Text       = Lang.T("StatusManagedPortable");
                FooterLabel.Foreground = (SolidColorBrush)FindResource("Sub");
            }
            else
            {
                FooterLabel.Text       = Lang.T("StatusPortable");
                FooterLabel.Foreground = (SolidColorBrush)FindResource("Sub");
            }
        }

        /// <summary>Shows/hides the rules and default-action panels based on ManualMode.</summary>
        internal void ApplyManualMode()
        {
            bool manual = _cfg.ManualMode;
            RulesPanel.Visibility = manual ? Visibility.Collapsed : Visibility.Visible;
            DefaultActionPanel.Visibility = Visibility.Visible;

            // When manual mode hides the rules panel the spacer rows below the
            // tunnel list are empty. Expand TunnelsListView to consume them.
            System.Windows.Controls.Grid.SetRowSpan(TunnelsListView, manual ? 6 : 1);

            UpdateFooterLabel();
        }

        internal void ApplyLocalTunnelMode()
        {
            bool localAllowed    = _cfg.Mode != AppMode.Companion;
            bool companionAllowed = _cfg.Mode != AppMode.Standalone;

            // Show/hide Add + Edit buttons based on whether local tunnels are allowed
            AddTunnelBtn.Visibility  = localAllowed ? Visibility.Visible : Visibility.Collapsed;
            EditTunnelBtn.Visibility = localAllowed ? Visibility.Visible : Visibility.Collapsed;

            // WireGuard GUI button: Companion + Mixed only
            if (OpenWgBtn != null)
                OpenWgBtn.Visibility = companionAllowed ? Visibility.Visible : Visibility.Collapsed;

            // WireGuard Log button: Companion + Mixed AND only when a WireGuard tunnel is active
            RefreshWireGuardLogBtn();

            // Quick Connect: Standalone + Mixed only
            if (QuickConnectBtn != null)
                QuickConnectBtn.Visibility = localAllowed ? Visibility.Visible : Visibility.Collapsed;

            // Refresh list to show/hide tunnels by source
            RefreshTunnelDropdowns();
        }

        // WireGuard Log button: visible only in Companion/Mixed mode AND when at
        // least one WireGuard-type tunnel (source != "local") is currently active.
        private void RefreshWireGuardLogBtn()
        {
            if (WireGuardLogBtn == null) return;
            bool companionAllowed = _cfg.Mode != AppMode.Standalone;
            bool wgTunnelActive   = false;
            if (companionAllowed)
            {
                try
                {
                    wgTunnelActive = ServiceController.GetServices()
                        .Any(s => s.ServiceName.StartsWith("WireGuardTunnel$",
                                      StringComparison.OrdinalIgnoreCase)
                                  && s.Status == ServiceControllerStatus.Running
                                  && _cfg.Tunnels.Any(t =>
                                      !string.Equals(t.Source, "local",
                                          StringComparison.OrdinalIgnoreCase)
                                      && string.Equals(t.Name,
                                          s.ServiceName.Substring("WireGuardTunnel$".Length),
                                          StringComparison.OrdinalIgnoreCase)));
                }
                catch { }
            }
            WireGuardLogBtn.Visibility = (companionAllowed && wgTunnelActive)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Quick Connect ─────────────────────────────────────────────────────
        // Opens a .conf directly from disk and connects immediately without
        // importing or storing it. Acts as a toggle: click again to disconnect.
        //
        // The plaintext conf is written to tunnels\temp\ (readable by LocalSystem)
        // and deleted as soon as the tunnel disconnects or the app exits.
        private string? _quickConnectConfPath   = null;
        private string? _quickConnectTunnelName = null;
        private string? _quickConnectDisplayName = null;

        public async void QuickConnect_Click(object sender, RoutedEventArgs e)
        {
            // ── Disconnect if a quick-connect session is already active ───────
            if (_quickConnectTunnelName != null && TunnelDll.IsRunning(_quickConnectTunnelName))
            {
                var qName = _quickConnectTunnelName;
                var qConf = _quickConnectConfPath;
                _quickConnectTunnelName  = null;
                _quickConnectConfPath    = null;
                _quickConnectDisplayName = null;
                RefreshQuickConnectButton();

                await System.Threading.Tasks.Task.Run(() =>
                {
                    TunnelDll.Disconnect(qName, out _);
                    try { if (qConf != null && File.Exists(qConf)) File.Delete(qConf); } catch { }
                });
                LogRaw("Quick Connect: disconnected.", LogLevel.Ok);
                UpdateStatusDisplay();
                return;
            }

            // ── Pick a .conf file ─────────────────────────────────────────────
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Quick Connect — open WireGuard config",
                Filter = "WireGuard Config (*.conf;*.conf.dpapi)|*.conf;*.conf.dpapi|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog(this) != true) return;

            string srcPath = dlg.FileName;
            // Strip double extension for .conf.dpapi: "home.conf.dpapi" → "home"
            string name = srcPath.EndsWith(".conf.dpapi", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(srcPath))
                : Path.GetFileNameWithoutExtension(srcPath);

            // Validate DLLs before reading the config
            var dllErr = TunnelDll.ValidateDlls();
            if (dllErr != null)
            { ShowErrorBanner($"Quick Connect: {dllErr}"); return; }

            // Write a BOM-free temp copy to Program Files\MasselGUARD\temp\
            // so LocalSystem can reliably read it regardless of the source location.
            string tunnelKey = "qc_" + name;
            EnsureTunnelsDirExists();
            string qcSvcConf = SvcConfPath(tunnelKey);
            try
            {
                string raw;
                if (srcPath.EndsWith(".conf.dpapi", StringComparison.OrdinalIgnoreCase))
                    raw = DpapiDecrypt(File.ReadAllBytes(srcPath));
                else
                    raw = File.ReadAllText(srcPath, System.Text.Encoding.UTF8);
                // Atomic creation: file locked to SYSTEM + Admins + current user
                // from first byte — no inherited-ACL window.
                WriteSecure(qcSvcConf, raw);
            }
            catch (Exception ex)
            { ShowErrorBanner($"Quick Connect: could not prepare config: {ex.Message}"); return; }

            _quickConnectConfPath    = qcSvcConf;
            _quickConnectTunnelName  = tunnelKey;
            _quickConnectDisplayName = name;
            RefreshQuickConnectButton();

            LogRaw($"Quick Connect: connecting {name}…", LogLevel.Info);

            bool ok = false;
            string err = "";
            await System.Threading.Tasks.Task.Run(() =>
            {
                ok = TunnelDll.Connect(tunnelKey, qcSvcConf,
                    msg => LogRaw(msg, LogLevel.Debug), out err);
            });

            // Delete temp file immediately — service has parsed the config already.
            try { File.Delete(qcSvcConf); } catch { }

            if (ok)
            {
                LogRaw($"Quick Connect: {name} connected ✓", LogLevel.Ok);
            }
            else
            {
                LogRaw($"Quick Connect failed: {err}", LogLevel.Warn);
                _quickConnectConfPath    = null;
                _quickConnectTunnelName  = null;
                _quickConnectDisplayName = null;
                RefreshQuickConnectButton();
            }
            UpdateStatusDisplay();
        }

        // Update the Quick Connect button label based on active state
        private void RefreshQuickConnectButton()
        {
            if (QuickConnectBtn == null) return;
            bool active = _quickConnectTunnelName != null && TunnelDll.IsRunning(_quickConnectTunnelName);
            if (active && _quickConnectDisplayName != null)
            {
                QuickConnectBtn.Content = $"⚡  {_quickConnectDisplayName}  ✕";
                QuickConnectBtn.ToolTip = Lang.T("QuickConnectDisconnect");
            }
            else
            {
                QuickConnectBtn.SetBinding(System.Windows.Controls.ContentControl.ContentProperty,
                    new System.Windows.Data.Binding("[BtnQuickConnect]")
                    {
                        Source = Lang.Instance
                    });
                QuickConnectBtn.ToolTip = Lang.T("TooltipQuickConnect");
            }
        }

        private void CleanupQuickConnect()
        {
            if (_quickConnectTunnelName == null) return;
            var qName = _quickConnectTunnelName;
            _quickConnectTunnelName  = null;
            _quickConnectDisplayName = null;
            var qcPath = _quickConnectConfPath;
            _quickConnectConfPath    = null;
            TunnelDll.Disconnect(qName, out _);
            try { if (qcPath != null && File.Exists(qcPath)) File.Delete(qcPath); } catch { }
        }

        private void RunInstall()
        {
            // Check if already running from an installed location
            var currentExe = Environment.ProcessPath
                ?? AppContext.BaseDirectory;
            var installedPath = GetInstalledPath();
            if (installedPath != null &&
                string.Equals(Path.GetDirectoryName(currentExe), installedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                ShowDialog(Lang.T("InstallRunningFromInstall"), Lang.T("InstallTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Show styled location picker — defaults to Program Files\MasselGUARD
            var defaultParent = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var chosenParent  = ShowInstallLocationDialog(defaultParent);
            if (chosenParent == null) return;

            var installDir = Path.Combine(chosenParent, InstallFolderName);

            try
            {
                Log("InstallProgress", LogLevel.Info);

                // 1. Create install folder and copy all files
                Directory.CreateDirectory(installDir);
                var sourceDir = Path.GetDirectoryName(currentExe)!;
                foreach (var file in Directory.GetFiles(sourceDir))
                    File.Copy(file, Path.Combine(installDir, Path.GetFileName(file)), overwrite: true);

                // Copy lang subfolder
                var sourceLang = Path.Combine(sourceDir, "lang");
                if (Directory.Exists(sourceLang))
                {
                    var destLang = Path.Combine(installDir, "lang");
                    Directory.CreateDirectory(destLang);
                    foreach (var file in Directory.GetFiles(sourceLang))
                        File.Copy(file, Path.Combine(destLang, Path.GetFileName(file)), overwrite: true);
                }

                var installedExe = Path.Combine(installDir, "MasselGUARD.exe");

                // Save icon as .ico file in install dir for the Start Menu shortcut
                var icoPath = Path.Combine(installDir, "MasselGUARD.ico");
                using (var icon = TrayIconHelper.CreateIcon(false))
                using (var fs = File.Create(icoPath))
                    icon.Save(fs);

                // 2. Start Menu shortcut via PowerShell
                var startMenuDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                    InstallFolderName);
                Directory.CreateDirectory(startMenuDir);
                var shortcutPath = Path.Combine(startMenuDir, "MasselGUARD.lnk");
                RunPowerShell($@"
$s = (New-Object -ComObject WScript.Shell).CreateShortcut('{shortcutPath}')
$s.TargetPath      = '{installedExe}'
$s.WorkingDirectory = '{installDir}'
$s.IconLocation    = '{icoPath},0'
$s.Description     = 'MasselGUARD'
$s.Save()");

                // 3. Write registry entry + config.json
                using var key = Registry.LocalMachine.CreateSubKey(InstallRegKey)!;
                key.SetValue("InstallPath", installDir);
                key.SetValue("Version", "1.1");
                _cfg.InstalledPath = installDir;
                SaveConfig();

                Log("InstallDone", LogLevel.Ok);

                // 4. Ask about scheduled task (auto-start)
                var autostart = ShowDialog(
                    Lang.T("InstallAutostart"),
                    Lang.T("InstallAutostartTitle"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (autostart == MessageBoxResult.Yes)
                {
                    var taskResult = RunPowerShell($@"
$exe     = '{installedExe}'
$action  = New-ScheduledTaskAction -Execute $exe
$trigger = New-ScheduledTaskTrigger -AtLogOn
$prin    = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest
Register-ScheduledTask -TaskName 'MasselGUARD' `
    -Action $action -Trigger $trigger -Principal $prin -Force");

                    if (taskResult)
                        Log("InstallScheduledOk", LogLevel.Ok);
                    else
                        Log("InstallScheduledFail", LogLevel.Warn, "PowerShell error");
                }

                // 5. Optionally delete the source folder (unless it IS the install dir)
                var sourceDir2 = Path.GetDirectoryName(
                    Environment.ProcessPath
                    ?? AppContext.BaseDirectory)!;
                var isDifferentDir = !string.Equals(sourceDir2, installDir,
                    StringComparison.OrdinalIgnoreCase);

                if (isDifferentDir)
                {
                    var deleteSource = ShowDialog(
                        Lang.T("InstallDeleteSource", sourceDir2),
                        Lang.T("InstallDeleteSourceTitle"),
                        MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (deleteSource == MessageBoxResult.Yes)
                    {
                        // Use cmd to delete after this process exits (can't delete running exe)
                        Process.Start(new ProcessStartInfo("cmd.exe",
                            $"/c timeout /t 2 /nobreak >nul & rd /s /q \"{sourceDir2}\"")
                        {
                            CreateNoWindow  = true,
                            UseShellExecute = false
                        });
                    }
                }

                // 6. Relaunch from installed location and close this instance
                Log("InstallRestarting", LogLevel.Ok);
                Process.Start(new ProcessStartInfo(installedExe) { UseShellExecute = true });
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    ((App)System.Windows.Application.Current)
                        .ShutdownApp();
                });

                UpdateInstallButton();
            }
            catch (Exception ex)
            {
                Log("InstallFailed", LogLevel.Warn, ex.Message);
                ShowDialog(Lang.T("InstallFailed", ex.Message), Lang.T("InstallTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RunUninstall()
        {
            var installDir = GetInstalledPath();
            if (installDir == null || !Directory.Exists(installDir))
            {
                ShowDialog(Lang.T("NotInstalled"), Lang.T("UninstallTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                UpdateInstallButton();
                return;
            }

            if (ShowDialog(Lang.T("UninstallConfirm"), Lang.T("UninstallTitle"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            // Ask about config files
            var keepConfig = ShowDialog(
                Lang.T("UninstallKeepConfig"),
                Lang.T("UninstallKeepConfigTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

            try
            {
                Log("UninstallProgress", LogLevel.Info);

                // Stop all running tunnel services first so tunnel.dll is released
                StopAllTunnelServices();

                // 1. Remove scheduled task
                var taskRemoved = RunPowerShell(
                    "Unregister-ScheduledTask -TaskName 'MasselGUARD' -Confirm:$false -ErrorAction SilentlyContinue");
                if (taskRemoved) Log("UninstallRemovedTask", LogLevel.Ok);

                // 2. Remove Start Menu shortcut
                var startMenuDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                    InstallFolderName);
                if (Directory.Exists(startMenuDir))
                    Directory.Delete(startMenuDir, recursive: true);

                // 3. Remove registry entry + clear config.json InstalledPath
                Registry.LocalMachine.DeleteSubKey(InstallRegKey, throwOnMissingSubKey: false);
                _cfg.InstalledPath = null;
                SaveConfig();

                // 4. Delete config if user chose to
                if (!keepConfig)
                {
                    // Delete user config from AppData
                    var configDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "MasselGUARD");
                    // Also delete svc temp dir from ProgramData
                    var tunnelDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "MasselGUARD");
                    if (Directory.Exists(configDir))
                        Directory.Delete(configDir, recursive: true);
                    if (Directory.Exists(tunnelDir))
                        try { Directory.Delete(tunnelDir, recursive: true); } catch { }
                }

                // 5. Schedule deletion of install folder via cmd (can't delete running exe)
                var currentExe = Environment.ProcessPath
                    ?? AppContext.BaseDirectory;
                var currentDir = Path.GetDirectoryName(currentExe) ?? "";
                var isRunningFromInstall = string.Equals(
                    currentDir, installDir,
                    StringComparison.OrdinalIgnoreCase);

                if (isRunningFromInstall)
                {
                    // Running from install folder — schedule deletion after this process exits
                    // Use a loop in cmd to wait for the exe to be released before deleting
                    Process.Start(new ProcessStartInfo("cmd.exe",
                        $"/c timeout /t 3 /nobreak >nul & " +
                        $"del /f /q \"{Path.Combine(installDir, "MasselGUARD.exe")}\" >nul 2>&1 & " +
                        $"rd /s /q \"{installDir}\"")
                    {
                        CreateNoWindow  = true,
                        UseShellExecute = false
                    });
                }
                else
                {
                    // Delete the install folder immediately
                    Directory.Delete(installDir, recursive: true);

                    // Only ask about removing the current running location when it differs
                    var removeCurrent = ShowDialog(
                        Lang.T("UninstallRemoveCurrent", currentDir),
                        Lang.T("UninstallRemoveCurrentTitle"),
                        MessageBoxButton.YesNo, MessageBoxImage.Question);

                    if (removeCurrent == MessageBoxResult.Yes)
                    {
                        Process.Start(new ProcessStartInfo("cmd.exe",
                            $"/c timeout /t 2 /nobreak >nul & rd /s /q \"{currentDir}\"")
                        {
                            CreateNoWindow  = true,
                            UseShellExecute = false
                        });
                    }
                }

                Log("UninstallClosing", LogLevel.Ok);
                UpdateInstallButton();

                // Close the application after a brief delay so the log message is visible
                System.Windows.Application.Current.Dispatcher.BeginInvoke(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(1500);
                    ((App)System.Windows.Application.Current).ShutdownApp();
                });
            }
            catch (Exception ex)
            {
                Log("UninstallFailed", LogLevel.Warn, ex.Message);
                ShowDialog(Lang.T("UninstallFailed", ex.Message), Lang.T("UninstallTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Styled install location picker — shows default path, Browse button, install-into preview
        private string? ShowInstallLocationDialog(string defaultParent)
        {
            var Br = (string key) => (SolidColorBrush)FindResource(key);

            string selectedParent = defaultParent;
            string? result        = null;

            var win = new Window
            {
                Title                 = Lang.T("InstallLocationTitle"),
                Width                 = 520,
                SizeToContent         = SizeToContent.Height,
                WindowStyle           = WindowStyle.None,
                AllowsTransparency    = true,
                Background            = System.Windows.Media.Brushes.Transparent,
                ResizeMode            = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = this
            };

            var root = new System.Windows.Controls.Border
            {
                Background      = Br("Bg"),
                BorderBrush     = Br("Border"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6)
            };

            var outer = new System.Windows.Controls.Grid();
            outer.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(44) });
            outer.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
            outer.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(52) });

            // ── Title bar ────────────────────────────────────────────────────
            var titleBar = new System.Windows.Controls.Border
            {
                Background   = Br("Panel"),
                CornerRadius = new CornerRadius(6, 6, 0, 0)
            };
            var titleText = new System.Windows.Controls.TextBlock
            {
                Text              = Lang.T("InstallLocationTitle"),
                FontFamily        = new System.Windows.Media.FontFamily("Consolas"),
                FontSize          = 12, FontWeight = FontWeights.Bold,
                Foreground        = Br("Accent"),
                Margin            = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            titleBar.Child = titleText;
            titleBar.MouseLeftButtonDown += (_, e) =>
            {
                if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) win.DragMove();
            };
            System.Windows.Controls.Grid.SetRow(titleBar, 0);
            outer.Children.Add(titleBar);

            // ── Content ───────────────────────────────────────────────────────
            var content = new System.Windows.Controls.StackPanel
            {
                Margin = new Thickness(20, 16, 20, 16)
            };

            // Note about subfolder
            content.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text         = Lang.T("InstallLocationNote"),
                FontFamily   = new System.Windows.Media.FontFamily("Consolas"),
                FontSize     = 11,
                Foreground   = Br("Sub"),
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 16)
            });

            // "Install into:" label + path preview (updates when Browse is clicked)
            content.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text       = Lang.T("InstallLocationCurrent"),
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize   = 10,
                Foreground = Br("Sub"),
                Margin     = new Thickness(0, 0, 0, 4)
            });

            var pathPreview = new System.Windows.Controls.Border
            {
                Background      = Br("Card"),
                BorderBrush     = Br("Border"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(10, 7, 10, 7),
                Margin          = new Thickness(0, 0, 0, 16)
            };
            var pathText = new System.Windows.Controls.TextBlock
            {
                Text         = Path.Combine(selectedParent, InstallFolderName),
                FontFamily   = new System.Windows.Media.FontFamily("Consolas"),
                FontSize     = 11,
                Foreground   = Br("Text"),
                TextWrapping = System.Windows.TextWrapping.Wrap
            };
            pathPreview.Child = pathText;
            content.Children.Add(pathPreview);

            // Browse button
            var browseBtn = new System.Windows.Controls.Button
            {
                Content             = Lang.T("BtnBrowse"),
                Style               = (Style)FindResource("FlatBtn"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding             = new Thickness(16, 6, 16, 6)
            };
            browseBtn.Click += (_, _) =>
            {
                using var folderDlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description            = Lang.T("InstallSelectFolder"),
                    UseDescriptionForTitle = true,
                    SelectedPath           = selectedParent
                };
                if (folderDlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    selectedParent   = folderDlg.SelectedPath;
                    pathText.Text    = Path.Combine(selectedParent, InstallFolderName);
                }
            };
            content.Children.Add(browseBtn);

            System.Windows.Controls.Grid.SetRow(content, 1);
            outer.Children.Add(content);

            // ── Button bar ────────────────────────────────────────────────────
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
                Style   = (Style)FindResource("FlatBtn"),
                Padding = new Thickness(20, 6, 20, 6),
                Margin  = new Thickness(0, 0, 8, 0)
            };
            cancelBtn.Click += (_, _) => { result = null; win.Close(); };

            var installBtn = new System.Windows.Controls.Button
            {
                Content = Lang.T("BtnInstallHere"),
                Style   = (Style)FindResource("PrimaryBtn"),
                Padding = new Thickness(20, 6, 20, 6)
            };
            installBtn.Click += (_, _) => { result = selectedParent; win.Close(); };

            btnStack.Children.Add(cancelBtn);
            btnStack.Children.Add(installBtn);
            btnBar.Child = btnStack;
            System.Windows.Controls.Grid.SetRow(btnBar, 2);
            outer.Children.Add(btnBar);

            root.Child  = outer;
            win.Content = root;
            win.ShowDialog();
            return result;
        }

        // Styled dark dialog matching app theme — replaces MessageBox.Show
        // Styled YesNo dialog with an optional "Don't ask again" checkbox.
        // Returns (YesNo result, whether the checkbox was ticked).
        private (MessageBoxResult result, bool dontAskAgain) ShowUpdatePrompt(
            string message, string title)
        {
            var Br = (string key) => (SolidColorBrush)FindResource(key);

            var result       = MessageBoxResult.No;
            var dontAsk      = false;
            var win = new Window
            {
                Title                 = title,
                Width                 = 480,
                SizeToContent         = SizeToContent.Height,
                WindowStyle           = WindowStyle.None,
                AllowsTransparency    = true,
                Background            = System.Windows.Media.Brushes.Transparent,
                ResizeMode            = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = this
            };

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
                { Background = Br("Panel"), CornerRadius = new CornerRadius(6, 6, 0, 0) };
            var titleText = new System.Windows.Controls.TextBlock
            {
                Text              = title,
                FontFamily        = new System.Windows.Media.FontFamily("Consolas"),
                FontSize          = 12, FontWeight = FontWeights.Bold,
                Foreground        = Br("Accent"),
                Margin            = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            titleBar.Child = titleText;
            titleBar.MouseLeftButtonDown += (_, e) =>
                { if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) win.DragMove(); };
            System.Windows.Controls.Grid.SetRow(titleBar, 0);
            grid.Children.Add(titleBar);

            // Message + checkbox
            var contentPanel = new System.Windows.Controls.StackPanel
                { Margin = new Thickness(20, 16, 20, 12) };

            var msgPanel = new System.Windows.Controls.StackPanel
                { Orientation = System.Windows.Controls.Orientation.Horizontal };
            var iconBlock = new System.Windows.Controls.TextBlock
            {
                Text              = "?",
                FontFamily        = new System.Windows.Media.FontFamily("Consolas"),
                FontSize          = 20, Foreground = Br("Accent"),
                Margin            = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var msgBlock = new System.Windows.Controls.TextBlock
            {
                Text         = message,
                FontFamily   = new System.Windows.Media.FontFamily("Consolas"),
                FontSize     = 11, Foreground = Br("Text"),
                TextWrapping = System.Windows.TextWrapping.Wrap,
                MaxWidth     = 380
            };
            msgPanel.Children.Add(iconBlock);
            msgPanel.Children.Add(msgBlock);
            contentPanel.Children.Add(msgPanel);

            // "Don't ask again" checkbox
            var chk = new System.Windows.Controls.CheckBox
            {
                Content     = Lang.T("DontAskAgainUpdate"),
                FontFamily  = new System.Windows.Media.FontFamily("Consolas"),
                FontSize    = 10,
                Foreground  = Br("Sub"),
                Margin      = new Thickness(0, 12, 0, 0),
                IsChecked   = false
            };
            contentPanel.Children.Add(chk);

            System.Windows.Controls.Grid.SetRow(contentPanel, 1);
            grid.Children.Add(contentPanel);

            // Button bar
            var btnBar = new System.Windows.Controls.Border
                { Background = Br("Panel"), CornerRadius = new CornerRadius(0, 0, 6, 6) };
            var btnStack = new System.Windows.Controls.StackPanel
            {
                Orientation         = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Center,
                Margin              = new Thickness(0, 0, 16, 0)
            };

            void AddBtn(string label, MessageBoxResult res, bool isPrimary = false)
            {
                var btn = new System.Windows.Controls.Button
                {
                    Content = label,
                    Style   = (Style)FindResource(isPrimary ? "PrimaryBtn" : "FlatBtn"),
                    Padding = new Thickness(20, 6, 20, 6),
                    Margin  = new Thickness(6, 0, 0, 0)
                };
                btn.Click += (_, _) => { result = res; dontAsk = chk.IsChecked == true; win.Close(); };
                btnStack.Children.Add(btn);
            }

            AddBtn(Lang.T("BtnNo"),  MessageBoxResult.No);
            AddBtn(Lang.T("BtnYes"), MessageBoxResult.Yes, isPrimary: true);

            btnBar.Child = btnStack;
            System.Windows.Controls.Grid.SetRow(btnBar, 2);
            grid.Children.Add(btnBar);

            root.Child  = grid;
            win.Content = root;
            win.ShowDialog();
            return (result, dontAsk);
        }

                private MessageBoxResult ShowDialog(string message, string title,
            MessageBoxButton buttons = MessageBoxButton.OK,
            MessageBoxImage icon = MessageBoxImage.None)
        {
            var Br = (string key) => (SolidColorBrush)FindResource(key);

            var iconText = icon switch
            {
                MessageBoxImage.Warning or MessageBoxImage.Error => "⚠",
                MessageBoxImage.Question                          => "?",
                MessageBoxImage.Information                       => "ℹ",
                _                                                 => ""
            };
            var iconColor = icon switch
            {
                MessageBoxImage.Error                             => "Red",
                MessageBoxImage.Warning                           => "Red",
                MessageBoxImage.Question                          => "Accent",
                _                                                 => "Sub"
            };

            var result = MessageBoxResult.None;
            var win = new Window
            {
                Title              = title,
                Width              = 460,
                SizeToContent      = SizeToContent.Height,
                WindowStyle        = WindowStyle.None,
                AllowsTransparency = true,
                Background         = System.Windows.Media.Brushes.Transparent,
                ResizeMode         = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner              = this
            };

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
                Text              = title,
                FontFamily        = new System.Windows.Media.FontFamily("Consolas"),
                FontSize          = 12, FontWeight = FontWeights.Bold,
                Foreground        = Br("Accent"),
                Margin            = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            titleBar.Child = titleText;
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) win.DragMove(); };
            System.Windows.Controls.Grid.SetRow(titleBar, 0);
            grid.Children.Add(titleBar);

            // Message area
            var msgPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin      = new Thickness(20, 16, 20, 16)
            };
            if (!string.IsNullOrEmpty(iconText))
            {
                var iconBlock = new System.Windows.Controls.TextBlock
                {
                    Text              = iconText,
                    FontFamily        = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize          = 20,
                    Foreground        = Br(iconColor),
                    Margin            = new Thickness(0, 0, 14, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                msgPanel.Children.Add(iconBlock);
            }
            var msgBlock = new System.Windows.Controls.TextBlock
            {
                Text              = message,
                FontFamily        = new System.Windows.Media.FontFamily("Consolas"),
                FontSize          = 11,
                Foreground        = Br("Text"),
                TextWrapping      = System.Windows.TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth          = 370
            };
            msgPanel.Children.Add(msgBlock);
            System.Windows.Controls.Grid.SetRow(msgPanel, 1);
            grid.Children.Add(msgPanel);

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

            void AddBtn(string label, MessageBoxResult res, bool isPrimary = false)
            {
                var btn = new System.Windows.Controls.Button
                {
                    Content         = label,
                    Style           = (Style)FindResource(isPrimary ? "PrimaryBtn" : "FlatBtn"),
                    Padding         = new Thickness(20, 6, 20, 6),
                    Margin          = new Thickness(6, 0, 0, 0)
                };
                btn.Click += (_, _) => { result = res; win.Close(); };
                btnStack.Children.Add(btn);
            }

            switch (buttons)
            {
                case MessageBoxButton.YesNo:
                    AddBtn(Lang.T("BtnNo"),  MessageBoxResult.No);
                    AddBtn(Lang.T("BtnYes"), MessageBoxResult.Yes, isPrimary: true);
                    break;
                case MessageBoxButton.OKCancel:
                    AddBtn(Lang.T("BtnCancel"), MessageBoxResult.Cancel);
                    AddBtn(Lang.T("BtnOk"),     MessageBoxResult.OK, isPrimary: true);
                    break;
                default:
                    AddBtn(Lang.T("BtnOk"), MessageBoxResult.OK, isPrimary: true);
                    break;
            }

            btnBar.Child = btnStack;
            System.Windows.Controls.Grid.SetRow(btnBar, 2);
            grid.Children.Add(btnBar);

            root.Child = grid;
            win.Content = root;
            win.ShowDialog();
            return result;
        }

        private static bool RunPowerShell(string script)
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"")
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                };
                using var proc = Process.Start(psi)!;
                proc.WaitForExit(30000);
                return proc.ExitCode == 0;
            }
            catch { return false; }
        }

        private void OpenWireGuardGui_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exe = WgExe;
                if (!File.Exists(exe))
                {
                    Log("LogGuiNotFound", LogLevel.Warn, exe);
                    return;
                }
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                Log("LogOpenedGui", LogLevel.Ok);
            }
            catch (Exception ex) { Log("LogGuiError", LogLevel.Warn, ex.Message); }
        }

        private void ShowWireGuardLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exe = WgExe;
                if (!File.Exists(exe))
                {
                    Log("LogGuiNotFound", LogLevel.Warn, exe);
                    return;
                }

                // Frameless dark window matching app style
                var bgBrush   = (SolidColorBrush)FindResource("Bg");
                var panelBrush = (SolidColorBrush)FindResource("Panel");
                var borderBrush = (SolidColorBrush)FindResource("Border");
                var cardBrush  = (SolidColorBrush)FindResource("Card");
                var textBrush  = (SolidColorBrush)FindResource("Text");
                var accentBrush = (SolidColorBrush)FindResource("Accent");

                var tb = new System.Windows.Controls.TextBox
                {
                    IsReadOnly          = true,
                    FontFamily          = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize            = 11,
                    Background          = cardBrush,
                    Foreground          = textBrush,
                    BorderThickness     = new Thickness(0),
                    VerticalScrollBarVisibility   = System.Windows.Controls.ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                    TextWrapping        = System.Windows.TextWrapping.NoWrap,
                    Padding             = new Thickness(10),
                    Text                = ""
                };

                // Title bar
                var titleText = new TextBlock
                {
                    Text       = Lang.T("LogWindowTitle"),
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize   = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = accentBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin     = new Thickness(14, 0, 0, 0)
                };

                var tailLabel = new TextBlock
                {
                    Text       = Lang.T("LogWindowLive"),
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize   = 10,
                    Foreground = (SolidColorBrush)FindResource("Green"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin     = new Thickness(0, 0, 12, 0)
                };

                var closeBtn = new System.Windows.Controls.Button
                {
                    Content         = "✕",
                    Style           = (Style)FindResource("DangerBtn"),
                    Padding         = new Thickness(10, 4, 10, 4),
                    BorderThickness = new Thickness(0),
                    Background      = Brushes.Transparent,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin          = new Thickness(0, 0, 4, 0)
                };

                var titleBar = new System.Windows.Controls.Grid
                {
                    Background = panelBrush,
                    Height     = 44
                };
                titleBar.Children.Add(titleText);
                var rightStack = new System.Windows.Controls.StackPanel
                {
                    Orientation       = System.Windows.Controls.Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment   = VerticalAlignment.Center
                };
                rightStack.Children.Add(tailLabel);
                rightStack.Children.Add(closeBtn);
                titleBar.Children.Add(rightStack);

                var outerBorder = new System.Windows.Controls.Border
                {
                    BorderBrush     = borderBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(6)
                };

                var root = new System.Windows.Controls.Grid();
                root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                System.Windows.Controls.Grid.SetRow(titleBar, 0);
                System.Windows.Controls.Grid.SetRow(tb, 1);
                root.Children.Add(titleBar);
                root.Children.Add(tb);
                outerBorder.Child = root;

                var win = new Window
                {
                    Title               = Lang.T("LogWindowTitle"),
                    Width               = 920,
                    Height              = 620,
                    Background          = bgBrush,
                    WindowStyle         = WindowStyle.None,
                    AllowsTransparency  = true,
                    ResizeMode          = ResizeMode.CanResizeWithGrip,
                    Owner               = this,
                    Content             = outerBorder
                };

                closeBtn.Click += (_, _) => win.Close();
                titleBar.MouseLeftButtonDown += (_, mev) => { if (mev.LeftButton == System.Windows.Input.MouseButtonState.Pressed) win.DragMove(); };

                // Helper: filter raw dumplog output to lines from the last 24 hours
                string[] FilterRecent(string raw)
                {
                    var cutoff = DateTime.Now.AddDays(-1);
                    return raw.Split('\n').Where(l =>
                    {
                        if (l.Length >= 19 &&
                            DateTime.TryParseExact(l.Substring(0, 19), "yyyy/MM/dd HH:mm:ss",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out var dt))
                            return dt >= cutoff;
                        return l.TrimEnd().Length > 0;
                    }).ToArray();
                }

                // Step 1: load last 24h history with a plain /dumplog (exits immediately)
                try
                {
                    var histPsi = new ProcessStartInfo(exe, "/dumplog")
                    {
                        UseShellExecute        = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow         = true
                    };
                    using var histProc = Process.Start(histPsi)!;
                    var histText = histProc.StandardOutput.ReadToEnd();
                    histProc.WaitForExit(8000);
                    var histLines = FilterRecent(histText);
                    if (histLines.Length > 0)
                    {
                        tb.Text = string.Join("\n", histLines) + "\n";
                        tb.ScrollToEnd();
                    }
                }
                catch { /* if history fails, start fresh */ }

                // Step 2: start /dumplog /tail — streams new lines to stdout as they arrive
                var tailProc = new Process
                {
                    StartInfo = new ProcessStartInfo(exe, "/dumplog /tail")
                    {
                        UseShellExecute        = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError  = true,
                        CreateNoWindow         = true
                    },
                    EnableRaisingEvents = true
                };

                tailProc.OutputDataReceived += (_, args) =>
                {
                    if (string.IsNullOrEmpty(args.Data)) return;
                    var line = args.Data;
                    Dispatcher.BeginInvoke(() => { tb.AppendText(line + "\n"); tb.ScrollToEnd(); });
                };
                tailProc.ErrorDataReceived += (_, args) =>
                {
                    if (string.IsNullOrEmpty(args.Data)) return;
                    var line = args.Data;
                    Dispatcher.BeginInvoke(() => { tb.AppendText(line + "\n"); tb.ScrollToEnd(); });
                };
                tailProc.Exited += (_, _) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        tailLabel.Text       = Lang.T("LogWindowStopped");
                        tailLabel.Foreground = (SolidColorBrush)FindResource("Sub");
                    });
                };

                tailProc.Start();
                tailProc.BeginOutputReadLine();
                tailProc.BeginErrorReadLine();

                win.Closed += (_, _) =>
                {
                    try { if (!tailProc.HasExited) tailProc.Kill(); } catch { }
                    tailProc.Dispose();
                };

                win.Show();
                Log("LogLogOpened", LogLevel.Ok);
            }
            catch (Exception ex) { Log("LogLogError", LogLevel.Warn, ex.Message); }
        }

        // ── Logging ────────────────────────────────────────────────────────────

        private enum LogLevel { Debug, Info, Ok, Warn }
        private static readonly SolidColorBrush LInfo = new(Color.FromRgb(88,  166, 255));
        private static readonly SolidColorBrush LOk   = new(Color.FromRgb(63,  185,  80));
        private static readonly SolidColorBrush LWarn = new(Color.FromRgb(247, 129, 102));
        private static readonly SolidColorBrush LTime = new(Color.FromRgb(48,   54,  61));

        // A log entry is either a translatable key+args pair, or a raw (external) string.
        private record LogEntry(DateTime Time, LogLevel Level, string? Key, object[]? Args, string? Raw);

        private readonly List<LogEntry> _logEntries = new();
        private const int MaxLogEntries = 300;

        // Log a translatable message by key (app-generated messages)
        private void Log(string key, LogLevel level, params object[] args)
        {
            if (level == LogLevel.Debug && _cfg.LogLevelSetting != "debug") return;
            // Marshal to UI thread when called from a background worker (e.g. async connect).
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Log(key, level, args)); return; }
            var entry = new LogEntry(DateTime.Now, level, key, args, null);
            _logEntries.Insert(0, entry);
            if (_logEntries.Count > MaxLogEntries) _logEntries.RemoveAt(_logEntries.Count - 1);
            RenderLogEntry(entry, prepend: true);
        }

        // Log a raw untranslatable string (e.g. OS error messages, WireGuard internals)
        private void LogRaw(string message, LogLevel level)
        {
            if (level == LogLevel.Debug && _cfg.LogLevelSetting != "debug") return;
            // Marshal to UI thread when called from a background worker (e.g. async connect).
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => LogRaw(message, level)); return; }
            var entry = new LogEntry(DateTime.Now, level, null, null, message);
            _logEntries.Insert(0, entry);
            if (_logEntries.Count > MaxLogEntries) _logEntries.RemoveAt(_logEntries.Count - 1);
            RenderLogEntry(entry, prepend: true);
        }

        private void LogDebug(string key, params object[] args) =>
            Log(key, LogLevel.Debug, args);

        private void RenderLogEntry(LogEntry entry, bool prepend)
        {
            try
            {
                var text = entry.Key != null
                    ? (entry.Args?.Length > 0 ? Lang.T(entry.Key, entry.Args) : Lang.T(entry.Key))
                    : (entry.Raw ?? "");

                var para = new Paragraph { Margin = new Thickness(0) };
                para.Inlines.Add(new Run(entry.Time.ToString("HH:mm:ss") + "  ") { Foreground = LTime });
                para.Inlines.Add(new Run(text) { Foreground = entry.Level switch
                    { LogLevel.Ok => LOk, LogLevel.Warn => LWarn, LogLevel.Debug => LTime, _ => LInfo } });

                if (prepend)
                {
                    if (LogDocument.Blocks.FirstBlock != null)
                        LogDocument.Blocks.InsertBefore(LogDocument.Blocks.FirstBlock, para);
                    else
                        LogDocument.Blocks.Add(para);
                }
                else
                {
                    LogDocument.Blocks.Add(para);
                }
            }
            catch { }
        }

        // Rebuild the entire log display in the current language
        private void RebuildLog()
        {
            try
            {
                LogDocument.Blocks.Clear();
                foreach (var entry in _logEntries)
                    RenderLogEntry(entry, prepend: false);
            }
            catch { }
        }

        // ── Window chrome ──────────────────────────────────────────────────────

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }

        private Views.SettingsWindow? _settingsWindow;

        private void SettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsWindow != null && _settingsWindow.IsVisible)
            {
                _settingsWindow.Activate();
                return;
            }
            _settingsWindow = new Views.SettingsWindow(this) { Owner = this };
            _settingsWindow.Show();
        }

        // Public wrappers used by SettingsWindow
        public void LogDebugPublic(string key, params object[] args) => LogDebug(key, args);
        public void OpenWireGuardGui()  => OpenWireGuardGui_Click(this, new RoutedEventArgs());
        public void OpenWireGuardLog()  => ShowWireGuardLog_Click(this, new RoutedEventArgs());
        public void ApplyLocalTunnelModePublic() => ApplyLocalTunnelMode();
        public bool   IsInstalledCheck()        => IsInstalled();
        public string? GetInstalledPathPublic()  => GetInstalledPath();
        public AppConfig GetConfig()             => _cfg;
        public void   SaveConfigPublic()         => SaveConfig();
        public void   SetMode(AppMode mode)
        {
            _cfg.Mode = mode;
            SaveConfig();
            ApplyLocalTunnelMode();
            ApplyManualMode();
        }

        /// <summary>True when running from a different directory than the install path.</summary>
        public bool IsRunningPortableWhileInstalled()
        {
            var installedPath = GetInstalledPath();
            if (installedPath == null) return false;
            var currentDir = Path.GetDirectoryName(
                Environment.ProcessPath ?? AppContext.BaseDirectory) ?? "";
            return !string.Equals(currentDir, installedPath, StringComparison.OrdinalIgnoreCase);
        }

        public void RunInstallPublic()
        {
            if (!IsInstalled())        RunInstall();
            else if (IsRunningPortableWhileInstalled()) RunUpdate();
            else                       RunUninstall();
        }

        /// <summary>
        /// Overwrites the installed version with the currently running portable copy.
        /// Copies all files from the current exe directory into the install directory,
        /// then relaunches from the installed location.
        /// </summary>
        private void RunUpdate()
        {
            var installDir = GetInstalledPath()!;
            var currentExe = Environment.ProcessPath ?? AppContext.BaseDirectory;
            var sourceDir  = Path.GetDirectoryName(currentExe)!;

            try
            {
                Log("InstallProgress", LogLevel.Info);

                // Stop all running WireGuardTunnel$ services — they hold tunnel.dll open
                StopAllTunnelServices();

                // Copy all files from current dir → install dir (overwrite)
                foreach (var file in Directory.GetFiles(sourceDir))
                {
                    var dest = Path.Combine(installDir, Path.GetFileName(file));
                    // Rename running exe instead of copying over it directly
                    if (string.Equals(Path.GetFileName(file), "MasselGUARD.exe",
                        StringComparison.OrdinalIgnoreCase) && File.Exists(dest))
                    {
                        var old = dest + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(dest, old);
                    }
                    File.Copy(file, dest, overwrite: true);
                }

                // Copy lang subfolder
                var sourceLang = Path.Combine(sourceDir, "lang");
                if (Directory.Exists(sourceLang))
                {
                    var destLang = Path.Combine(installDir, "lang");
                    Directory.CreateDirectory(destLang);
                    foreach (var file in Directory.GetFiles(sourceLang))
                        File.Copy(file, Path.Combine(destLang, Path.GetFileName(file)), overwrite: true);
                }

                // Clean up renamed old exe
                var oldExe = Path.Combine(installDir, "MasselGUARD.exe.old");
                if (File.Exists(oldExe))
                    try { File.Delete(oldExe); } catch { }

                Log("UpdateDoneRestart", LogLevel.Ok);

                // Relaunch from installed location
                var installedExe = Path.Combine(installDir, "MasselGUARD.exe");
                Process.Start(new ProcessStartInfo(installedExe) { UseShellExecute = true });
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                    ((App)System.Windows.Application.Current).ShutdownApp());
            }
            catch (Exception ex)
            {
                Log("InstallFailed", LogLevel.Warn, ex.Message);
                ShowDialog(Lang.T("InstallFailed", ex.Message), Lang.T("InstallTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Stops and deletes all WireGuardTunnel$ services so tunnel.dll is released
        /// before install/update tries to overwrite it.
        /// </summary>
        private void StopAllTunnelServices()
        {
            CleanupQuickConnect();
            try
            {
                foreach (var t in _cfg.Tunnels.Where(t => t.Source == "local"))
                {
                    if (TunnelDll.IsRunning(t.Name))
                    {
                        Log("LogStoppedTunnel", LogLevel.Info, t.Name, "");
                        TunnelDll.Disconnect(t.Name, out _);
                        // No temp file to delete — conf lives in tunnels\ directly.
                    }
                }
            }
            catch { }

            // Sweep orphaned WireGuardTunnel$ services not in config
            try
            {
                foreach (var svc in ServiceController.GetServices())
                {
                    if (!svc.ServiceName.StartsWith("WireGuardTunnel$",
                        StringComparison.OrdinalIgnoreCase)) continue;
                    var name = svc.ServiceName.Substring("WireGuardTunnel$".Length);
                    if (_cfg.Tunnels.Any(t => string.Equals(t.Name, name,
                        StringComparison.OrdinalIgnoreCase))) continue;
                    try
                    {
                        var stored = _cfg.Tunnels.FirstOrDefault(t =>
                            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (stored?.Source == "local")
                            TunnelDll.Disconnect(name, out _);
                        else if (svc.Status != ServiceControllerStatus.Stopped)
                            svc.Stop();
                    }
                    catch { }
                }
            }
            catch { }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            // Recheck availability every time the window is brought to the foreground
            if (_tunnels.Count > 0)
                CheckTunnelAvailability();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)    => Hide();
        private void Window_Closing(object sender, CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }

        protected override void OnClosed(EventArgs e)
        {
            CleanupQuickConnect();
            if (_wlanHandle != IntPtr.Zero) { WlanCloseHandle(_wlanHandle, IntPtr.Zero); _wlanHandle = IntPtr.Zero; }
            base.OnClosed(e);
        }

        private void UpdateAdminLabel()
        {
            bool admin = new System.Security.Principal.WindowsPrincipal(
                System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            AdminLabel.Text       = admin ? Lang.T("LogAdminYes") : Lang.T("LogAdminNo");
            AdminLabel.Foreground = admin ? (SolidColorBrush)FindResource("Green") : (SolidColorBrush)FindResource("Red");
        }
    }

    // Simple data class for the language picker ComboBox
    public class LangItem
    {
        public string Code { get; }
        public string Name { get; }
        public LangItem(string code, string name) { Code = code; Name = name; }
        public override string ToString() => Name;
    }
}
