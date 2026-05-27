using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MasselGUARD.Infrastructure;
using MasselGUARD.Models;
using MasselGUARD.Services;

namespace MasselGUARD.ViewModels
{
    /// <summary>
    /// ViewModel for MainWindow.
    /// Coordinates: tunnel list, WiFi state, rule evaluation, logging.
    /// The View binds to properties and commands here — zero logic in code-behind.
    /// </summary>
    public class MainViewModel : ObservableObject, IDisposable
    {
        // ── Services ──────────────────────────────────────────────────────────
        private readonly ConfigService  _config;
        private readonly TunnelService  _tunnels;
        private readonly LogService     _log;
        private readonly WiFiService    _wifi;
        private readonly RuleEngine     _rules;
        private readonly DispatcherTimer _timer;

        // ── Observable state ──────────────────────────────────────────────────
        public ObservableCollection<TunnelEntryViewModel> TunnelList { get; } = new();
        public ObservableCollection<LogEntryViewModel>    LogEntries { get; } = new();

        private string? _currentSsid;
        public  string  CurrentSsidDisplay =>
            string.IsNullOrEmpty(_currentSsid) ? "No WiFi" : _currentSsid;

        private string _activeTunnelName = "Not connected";
        public  string  ActiveTunnelName
        {
            get => _activeTunnelName;
            private set => SetField(ref _activeTunnelName, value);
        }

        private TunnelEntryViewModel? _selectedTunnel;
        public  TunnelEntryViewModel? SelectedTunnel
        {
            get => _selectedTunnel;
            set
            {
                SetField(ref _selectedTunnel, value);
                EditTunnelCommand.RaiseCanExecuteChanged();
                DeleteTunnelCommand.RaiseCanExecuteChanged();
            }
        }

        // ── Commands ──────────────────────────────────────────────────────────
        public RelayCommand AddTunnelCommand    { get; }
        public RelayCommand EditTunnelCommand   { get; }
        public RelayCommand DeleteTunnelCommand { get; }
        public RelayCommand QuickConnectCommand { get; }
        public RelayCommand OpenSettingsCommand { get; }
        public RelayCommand ExportLogCommand    { get; }

        // ── Constructor ───────────────────────────────────────────────────────
        public MainViewModel(
            ConfigService  config,
            TunnelService  tunnels,
            LogService     log,
            WiFiService    wifi,
            RuleEngine     rules)
        {
            _config  = config;
            _tunnels = tunnels;
            _log     = log;
            _wifi    = wifi;
            _rules   = rules;

            AddTunnelCommand    = new RelayCommand(DoAddTunnel);
            EditTunnelCommand   = new RelayCommand(DoEditTunnel,
                () => SelectedTunnel != null);
            DeleteTunnelCommand = new RelayCommand(DoDeleteTunnel,
                () => SelectedTunnel != null);
            QuickConnectCommand = new RelayCommand(DoQuickConnect);
            OpenSettingsCommand = new RelayCommand(DoOpenSettings);
            ExportLogCommand    = new RelayCommand(DoExportLog);

            // Status poll
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => { RefreshTunnelStatus(); StatusTick?.Invoke(); };
            _timer.Start();

            // WiFi events are handled by MainWindow which calls InitialWifiCheck()
            // for both startup queries and live SsidChanged events.
            _log.EntryAdded   += OnLogEntry;
            // Initial state
            RebuildTunnelList();
            RefreshTunnelStatus();
        }

        // ── Tunnel list ───────────────────────────────────────────────────────

        private System.Threading.Timer? _disconnectDebounce;

        /// <summary>Apply WiFi state from a known SSID (e.g. from a SsidChanged event).</summary>
        public void ApplyWifiState(string? ssid, bool isOpen)
        {
            if (ssid == null && _currentSsid == null) return;

            if (ssid == null)
            {
                // Debounce disconnects: wait 1.5 s before treating as real disconnect.
                // If a new connect fires within that window, the timer is cancelled.
                _disconnectDebounce?.Dispose();
                _disconnectDebounce = new System.Threading.Timer(_ =>
                {
                    var (live, liveOpen) = _wifi.QueryCurrentSsid();
                    if (!string.IsNullOrEmpty(live))
                    {
                        // Got a SSID — transient disconnect (VPN blip or network switch).
                        // Only apply if we haven't already handled this SSID via a connect event.
                        if (live != _currentSsid)
                            Application.Current?.Dispatcher.Invoke(
                                () => ApplyWifiState(live, liveOpen));
                        return;
                    }

                    // Genuinely disconnected
                    _currentSsid = null;
                    OnPropertyChanged(nameof(CurrentSsidDisplay));
                    _log.Info("WiFi disconnected");
                    var result = _rules.EvaluateWifiDisconnected(_config.Config);
                    Application.Current?.Dispatcher.Invoke(() => ApplyRuleResult(result));
                }, null, 2000, System.Threading.Timeout.Infinite);
                return;
            }

            // Cancel any pending disconnect debounce
            _disconnectDebounce?.Dispose();
            _disconnectDebounce = null;

            // Already on this SSID — swallow the duplicate
            if (ssid == _currentSsid) return;

            _currentSsid = ssid;
            OnPropertyChanged(nameof(CurrentSsidDisplay));
            _log.Info($"WiFi: {ssid}{(isOpen ? " (open)" : "")}");
            var r = _rules.EvaluateWifi(_config.Config, ssid, isOpen);
            ApplyRuleResult(r);
        }

        /// <summary>Query current SSID and apply state — used on startup only.</summary>
        public void InitialWifiCheck()
        {
            var (ssid, isOpen) = _wifi.QueryCurrentSsid();
            ApplyWifiState(ssid, isOpen);
        }

        public System.Windows.Visibility RulesColumnVisibility =>
            _config.Config.ShowTunnelRulesColumn && !_config.Config.ManualMode
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;

        public void NotifyRulesColumnChanged() =>
            OnPropertyChanged(nameof(RulesColumnVisibility));

        public void RebuildTunnelList()
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                // Snapshot connect-times before destroying the existing VMs so that
                // tunnels which were already active keep their uptime counter running.
                var connectedAt = TunnelList
                    .Where(t => t.IsActive && t.ConnectedAt.HasValue)
                    .ToDictionary(t => t.Name, t => t.ConnectedAt!.Value,
                                  StringComparer.OrdinalIgnoreCase);

                TunnelList.Clear();
                foreach (var s in _config.Config.Tunnels
                    .Where(t => IsSourceAllowed(t.Source)))
                {
                    var vm = new TunnelEntryViewModel(s, _tunnels, _log, _config);
                    TunnelList.Add(vm);
                }

                // Restore connect-times on tunnels that are still active after rebuild.
                // RefreshStatus sets IsActive first, so RestoreConnectedAt can check it.
                foreach (var vm in TunnelList)
                {
                    vm.RefreshStatus();
                    if (connectedAt.TryGetValue(vm.Name, out var t0))
                        vm.RestoreConnectedAt(t0);
                }
            });
        }

        private bool IsSourceAllowed(string source) => _config.Config.Mode switch
        {
            AppMode.Standalone => source == "local",
            AppMode.Companion  => source != "local",
            _                  => true,
        };

        // ── Status refresh ────────────────────────────────────────────────────

        public void RefreshTunnelStatus()
        {
            foreach (var t in TunnelList)
                t.RefreshStatus();

            var active = TunnelList.FirstOrDefault(t => t.IsActive);
            ActiveTunnelName = active?.Name ?? "Not connected";
        }

        // ── WiFi ──────────────────────────────────────────────────────────────

        private void OnSsidChanged(string? ssid, bool isOpen)
        {
            _currentSsid = ssid;
            OnPropertyChanged(nameof(CurrentSsidDisplay));

            if (!string.IsNullOrEmpty(ssid))
                _log.Info($"WiFi: {ssid}{(isOpen ? " (open)" : "")}");
            else
                _log.Info("WiFi disconnected");

            var result = _rules.EvaluateWifi(_config.Config, ssid, isOpen);
            ApplyRuleResult(result);
        }

        private void ApplyRuleResult(RuleEngine.RuleResult result)
        {
            if (result.Action == RuleEngine.ActionKind.None) return;

            _log.Info($"Automation: {result.Reason}");

            if (result.Action == RuleEngine.ActionKind.Disconnect)
            {
                foreach (var t in TunnelList.Where(t => t.IsActive))
                    t.DisconnectCommand.Execute(null);

                if (_config.Config.ShowTrayPopupOnSwitch)
                {
                    int ms = _config.Config.NotificationDurationSeconds * 1000;
                    // Determine category and strip colour from reason
                    bool isRule    = result.Reason.StartsWith("Rule:");
                    bool isOpen    = result.Reason.StartsWith("Open network");
                    bool isDefault = result.Reason.StartsWith("Default");
                    string category  = isRule    ? "WiFi Rule Matched"
                                     : isOpen    ? "Open Network Protection"
                                     : isDefault ? "Default Action"
                                     :             "Automation";
                    string stripKey  = isOpen    ? "Success"
                                     : isDefault ? "Warning"
                                     :             "Accent";
                    (Application.Current as App)?.ShowTrayNotification(
                        new Views.ToastNotification
                        {
                            Category   = category,
                            Primary    = "Disconnected",
                            Secondary  = result.Reason,
                            StripColor = stripKey,
                            DurationMs = ms,
                        });
                }
                return;
            }

            // Activate
            var target = TunnelList.FirstOrDefault(t =>
                string.Equals(t.Name, result.TunnelName,
                    StringComparison.OrdinalIgnoreCase));

            if (target == null)
            {
                _log.Warn($"Tunnel not found: {result.TunnelName}");
                return;
            }

            // Stop others first
            foreach (var t in TunnelList.Where(t => t.IsActive && t != target))
                t.DisconnectCommand.Execute(null);

            if (!target.IsActive)
                target.ConnectCommand.Execute(null);

            if (_config.Config.ShowTrayPopupOnSwitch)
            {
                int ms = _config.Config.NotificationDurationSeconds * 1000;
                bool isRule    = result.Reason.StartsWith("Rule:");
                bool isOpen    = result.Reason.StartsWith("Open network");
                bool isDefault = result.Reason.StartsWith("Default");
                string category = isRule    ? "WiFi Rule Matched"
                                : isOpen    ? "Open Network Protection"
                                : isDefault ? "Default Action"
                                :             "Automation";
                string stripKey = isOpen    ? "Success"
                                : isDefault ? "Warning"
                                :             "Accent";
                // Extract rule name from reason if available
                string primary = target.Name;
                string secondary = result.Reason;
                (Application.Current as App)?.ShowTrayNotification(
                    new Views.ToastNotification
                    {
                        Category   = category,
                        Primary    = primary,
                        Secondary  = secondary,
                        StripColor = stripKey,
                        DurationMs = ms,
                    });
            }
        }

        // ── Log ───────────────────────────────────────────────────────────────

        private void OnLogEntry(LogEntry entry)
        {
            Application.Current?.Dispatcher.Invoke(() =>
                LogEntries.Add(new LogEntryViewModel(entry)));
        }

        // ── Command implementations ───────────────────────────────────────────

        private void DoAddTunnel()
        {
            // Request raised to View via event / dialog service
            AddTunnelRequested?.Invoke();
        }

        private void DoEditTunnel()
        {
            if (SelectedTunnel == null) return;
            EditTunnelRequested?.Invoke(SelectedTunnel.StoredTunnel);
        }

        private void DoDeleteTunnel()
        {
            if (SelectedTunnel == null) return;
            DeleteTunnelRequested?.Invoke(SelectedTunnel.StoredTunnel);
        }

        private void DoQuickConnect()  => QuickConnectRequested?.Invoke();
        private void DoOpenSettings()  => OpenSettingsRequested?.Invoke();

        private void DoExportLog()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title      = "Export Activity Log",
                Filter     = "Text files (*.txt)|*.txt",
                FileName   = $"MasselGUARD-log-{DateTime.Now:yyyyMMdd}",
                DefaultExt = ".txt",
            };
            if (dlg.ShowDialog() == true)
                _log.ExportToFile(dlg.FileName);
        }

        // ── Events raised to View ─────────────────────────────────────────────
        public event Action?                 AddTunnelRequested;
        public event Action<StoredTunnel>?   EditTunnelRequested;
        public event Action<StoredTunnel>?   DeleteTunnelRequested;
        public event Action?                 QuickConnectRequested;
        public event Action?                 OpenSettingsRequested;
        /// <summary>Fires every second on the UI thread — MainWindow uses this to update status bar labels.</summary>
        public event Action?                 StatusTick;

        // ── IDisposable ───────────────────────────────────────────────────────
        public void Dispose()
        {
            _timer.Stop();
            _disconnectDebounce?.Dispose();
            _log.EntryAdded -= OnLogEntry;
        }
    }
}
