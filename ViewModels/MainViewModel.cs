using System;
using System.Collections.Generic;
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
        private readonly HistoryService _history;
        private readonly DispatcherTimer _timer;

        // ── Observable state ──────────────────────────────────────────────────
        public ObservableCollection<TunnelEntryViewModel> TunnelList { get; } = new();
        public ObservableCollection<LogEntryViewModel>    LogEntries { get; } = new();

        // ── Stats polling / auto-reconnect ────────────────────────────────────
        private int _statusTick = 0;
        private const int StatsPollEveryTicks = 5; // every 5 seconds
        private readonly HashSet<string> _reconnecting =
            new(StringComparer.OrdinalIgnoreCase);

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

        // ── Column widths (pixel, bound by DataTemplate ColumnDefinitions) ─────
        private double _tunCol0W = 160; public double TunCol0W { get => _tunCol0W; set => SetField(ref _tunCol0W, value); }
        private double _tunCol1W = 180; public double TunCol1W { get => _tunCol1W; set => SetField(ref _tunCol1W, value); }
        private double _tunCol2W = 60;  public double TunCol2W { get => _tunCol2W; set => SetField(ref _tunCol2W, value); }
        private double _tunCol3W = 90;  public double TunCol3W { get => _tunCol3W; set => SetField(ref _tunCol3W, value); }
        private double _wifCol0W = 160; public double WifCol0W { get => _wifCol0W; set => SetField(ref _wifCol0W, value); }
        private double _wifCol1W = 120; public double WifCol1W { get => _wifCol1W; set => SetField(ref _wifCol1W, value); }
        private double _wifCol2W = 100; public double WifCol2W { get => _wifCol2W; set => SetField(ref _wifCol2W, value); }
        private double _wifCol3W = 40;  public double WifCol3W { get => _wifCol3W; set => SetField(ref _wifCol3W, value); }
        private double _wifCol4W = 120; public double WifCol4W { get => _wifCol4W; set => SetField(ref _wifCol4W, value); }

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
            RuleEngine     rules,
            HistoryService history)
        {
            _config  = config;
            _tunnels = tunnels;
            _log     = log;
            _wifi    = wifi;
            _rules   = rules;
            _history = history;

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
            _timer.Tick += (_, _) => { RefreshTunnelStatus(); CheckSchedules(); StatusTick?.Invoke(); };
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

                // Snapshot UserDisconnected flags so auto-reconnect suppression survives rebuilds.
                var userDisconnected = TunnelList
                    .Where(t => t.UserDisconnected)
                    .Select(t => t.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                TunnelList.Clear();
                foreach (var s in _config.Config.Tunnels
                    .Where(t => IsSourceAllowed(t.Source)))
                {
                    var vm = new TunnelEntryViewModel(s, _tunnels, _log, _config);
                    TunnelList.Add(vm);
                }

                // Restore connect-times and UserDisconnected flags on tunnels still active
                // after rebuild. RefreshStatus sets IsActive first so RestoreConnectedAt works.
                foreach (var vm in TunnelList)
                {
                    vm.RefreshStatus();
                    if (connectedAt.TryGetValue(vm.Name, out var t0))
                        vm.RestoreConnectedAt(t0);
                    if (userDisconnected.Contains(vm.Name))
                        vm.UserDisconnected = true;
                }
            });
        }

        private bool IsSourceAllowed(string source) => _config.Config.Mode switch
        {
            AppMode.Standalone => source == "local",
            AppMode.Companion  => source != "local",
            _                  => true,
        };

        // ── DNS leak warning ──────────────────────────────────────────────────

        /// <summary>Tunnels currently in a warned PotentialLeak episode (re-armed on recovery/disconnect).</summary>
        private readonly HashSet<string> _dnsLeakWarned = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Keys "tunnel|yyyy-MM" already warned about exceeding the monthly data cap.</summary>
        private readonly HashSet<string> _dataCapWarned = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Edge-triggered "monthly data cap reached" warning. Fires a one-time tray toast +
        /// log line the first time a tunnel's month-to-date total crosses its configured cap.
        /// Re-arms automatically next calendar month (the key includes yyyy-MM).
        /// </summary>
        private void MaybeWarnDataCap(TunnelEntryViewModel t, long monthlyBytes)
        {
            long capMB = t.StoredTunnel.MonthlyCapMB;
            if (capMB <= 0) return;                                 // no cap set
            if (monthlyBytes < capMB * 1_048_576L) return;          // under cap

            var key = $"{t.Name}|{DateTime.UtcNow:yyyy-MM}";
            if (!_dataCapWarned.Add(key)) return;                   // already warned this month

            _log.Warn($"Monthly data cap reached for {t.Name}: " +
                      $"{capMB} MB used this month.");
            if (_config.Config.ShowTrayPopupOnSwitch)
                (Application.Current as App)?.ShowTrayNotification(
                    new Views.ToastNotification
                    {
                        Category   = "Data cap reached",
                        Primary    = $"{t.Name}: monthly data cap reached",
                        Secondary  = $"{capMB} MB used this month.",
                        StripColor = "Warning",
                        DurationMs = _config.Config.NotificationDurationSeconds * 1000,
                    });
        }

        /// <summary>
        /// Edge-triggered "possible DNS leak" warning. Fires a one-time tray toast + log
        /// line when a tunnel's DNS status first becomes PotentialLeak — but only while the
        /// leak is unmitigated (smart name resolution still enabled). It stays silent once
        /// the machine policy contains the leak, on Secure/NotConfigured/Unknown, and after
        /// the first warning until the status recovers (tracked in <see cref="_dnsLeakWarned"/>).
        /// </summary>
        private void MaybeWarnDnsLeak(TunnelEntryViewModel t, TunnelDll.DnsLeakStatus dns, bool mitigated)
        {
            if (dns != TunnelDll.DnsLeakStatus.PotentialLeak)
            {
                _dnsLeakWarned.Remove(t.Name);   // episode over — re-arm for next time
                return;
            }
            bool wantLog   = _config.Config.DnsLeakWarnLog;
            bool wantToast = _config.Config.DnsLeakWarnToast;
            if (!wantLog && !wantToast)          return;   // both warning channels off
            if (_dnsLeakWarned.Contains(t.Name)) return;   // already warned this episode
            if (mitigated) return;                         // prevention active → contained → no message

            _dnsLeakWarned.Add(t.Name);
            if (wantLog)
                _log.Warn($"Possible DNS leak on {t.Name}: another network adapter has DNS servers. " +
                          "Enable DNS leak protection in Settings → Advanced.");
            if (wantToast)
                (Application.Current as App)?.ShowTrayNotification(
                    new Views.ToastNotification
                    {
                        Category   = "DNS leak warning",
                        Primary    = $"Possible DNS leak: {t.Name}",
                        Secondary  = "Enable DNS leak protection in Settings → Advanced.",
                        StripColor = "Warning",
                        DurationMs = _config.Config.NotificationDurationSeconds * 1000,
                    });
        }

        // ── Status refresh ────────────────────────────────────────────────────

        public void RefreshTunnelStatus()
        {
            _statusTick++;
            bool doStatsPoll = (_statusTick % StatsPollEveryTicks == 0);

            foreach (var t in TunnelList)
            {
                bool wasActive = t.IsActive;
                t.RefreshStatus();
                bool nowActive = t.IsActive;

                if (!nowActive) _dnsLeakWarned.Remove(t.Name);   // re-arm the DNS-leak warning

                if (doStatsPoll && nowActive)
                {
                    var stats = TunnelDll.GetTrafficStats(t.Name);
                    t.UpdateStats(stats);
                    var dns = TunnelDll.CheckDnsLeak(t.Name);
                    // Prevention state (machine-wide) — a set leak becomes "contained".
                    bool dnsMitigated = Services.DnsLeakService.IsSmartNameResolutionDisabled();
                    t.UpdateDnsStatus(dns, dnsMitigated);
                    MaybeWarnDnsLeak(t, dns, dnsMitigated);

                    // Monthly data usage = closed-session history for this month + live session.
                    var (mrx, mtx) = _history.GetMonthlyUsage(t.Name, DateTime.UtcNow);
                    long monthTotal = mrx + mtx + t.SessionBytes;
                    t.UpdateMonthlyUsage(monthTotal);
                    MaybeWarnDataCap(t, monthTotal);

                    // Local tunnel: if the kernel adapter is gone but we still think
                    // it's connected (IsRunning checks in-memory HashSet only),
                    // the adapter must have dropped (e.g. after sleep/wake).
                    bool localDrop = t.IsLocal && !stats.AdapterFound
                        && t.ConnectedAt.HasValue
                        && (DateTime.UtcNow - t.ConnectedAt.Value).TotalSeconds > 30;
                    if (localDrop && !IsIntentionalDrop(t)
                        && TunnelService.ShouldAutoReconnect(t.StoredTunnel, _config.Config))
                    {
                        TunnelDll.ForceMarkDisconnected(t.Name);
                        t.RefreshStatus(); // now shows disconnected
                        _ = AutoReconnectAsync(t);
                    }
                }

                // Companion tunnel connected externally (WireGuard client).
                if (!t.IsLocal && !wasActive && nowActive && !t.IsConnecting)
                {
                    string via = _log.IsExtended ? " via WireGuard app" : "";
                    _log.Ok($"Connected: {t.Name}{via}");
                    // The external connect supersedes any earlier user disconnect —
                    // clear the suppression flag so a later external drop is detected.
                    t.UserDisconnected = false;
                    _tunnels.RecordExternalConnect(t.Name, "WireGuard app");
                }

                // Companion tunnel dropped externally (WireGuard client).
                if (!t.IsLocal && wasActive && !nowActive && !IsIntentionalDrop(t))
                {
                    string via = _log.IsExtended ? " via WireGuard app" : "";
                    // Closes the open history entry and logs "Disconnected: <name>".
                    _tunnels.RecordExternalDisconnect(t.Name, via);

                    // Only auto-reconnect when the SCM entry still exists (service crashed).
                    // An absent entry means the WireGuard client deactivated it intentionally.
                    // The deactivate deletes the entry slightly AFTER stopping the service, so
                    // this early check can race the deletion — AutoReconnectAsync re-checks
                    // after its backoff delay and aborts when the entry is gone by then.
                    var svcName = "WireGuardTunnel$" + t.Name;
                    bool entryGone = !TunnelService.WireGuardServiceExists(svcName);
                    if (!entryGone && TunnelService.ShouldAutoReconnect(t.StoredTunnel, _config.Config))
                        _ = AutoReconnectAsync(t);
                }
            }

            var active = TunnelList.FirstOrDefault(t => t.IsActive);
            ActiveTunnelName = active?.Name ?? "Not connected";
        }

        /// <summary>
        /// Returns true when the tunnel drop was caused by MasselGUARD itself
        /// (user click, WiFi rule, CLI) — suppresses auto-reconnect in those cases.
        /// Checks the TunnelService intentional-disconnect registry first, then falls
        /// back to the ViewModel's UserDisconnected flag.
        /// </summary>
        private bool IsIntentionalDrop(TunnelEntryViewModel vm) =>
            _tunnels.ConsumeIntentionalDisconnect(vm.Name) || vm.UserDisconnected;

        private const int AutoReconnectMaxAttempts = 3;

        private async System.Threading.Tasks.Task AutoReconnectAsync(TunnelEntryViewModel vm)
        {
            // Guard against concurrent reconnect attempts for the same tunnel.
            lock (_reconnecting)
            {
                if (_reconnecting.Contains(vm.Name)) return;
                _reconnecting.Add(vm.Name);
            }

            try
            {
                // Grace period before announcing anything: the WireGuard client's
                // deactivate deletes the SCM entry just after stopping the service,
                // so wait for the deletion to settle. A clean deactivate is then
                // recognised up front and skipped silently — no reconnect countdown.
                if (!vm.IsLocal)
                {
                    await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(2));
                    if (vm.IsActive) return; // came back up on its own
                    if (!TunnelService.WireGuardServiceExists("WireGuardTunnel$" + vm.Name))
                    {
                        _log.Info($"[AutoReconnect] '{vm.Name}' was deactivated via the WireGuard app — not reconnecting.");
                        return;
                    }
                }

                for (int attempt = 1; attempt <= AutoReconnectMaxAttempts; attempt++)
                {
                    int delaySec = attempt * 5;   // 5 s, 10 s, 15 s
                    _log.Info($"[AutoReconnect] '{vm.Name}' dropped — reconnecting in {delaySec}s (attempt {attempt}/{AutoReconnectMaxAttempts})…");

                    await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(delaySec));

                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher == null) return;

                    bool abort     = false;
                    bool connected = false;
                    await dispatcher.InvokeAsync(() =>
                    {
                        // Abort if the tunnel came back up on its own, or user disconnected.
                        if (vm.IsActive || IsIntentionalDrop(vm)) { abort = true; connected = vm.IsActive; return; }
                        // Also abort if the setting was switched off while waiting.
                        if (!TunnelService.ShouldAutoReconnect(vm.StoredTunnel, _config.Config)) { abort = true; return; }
                        // Companion: the WireGuard client's deactivate stops the service first
                        // and deletes the SCM entry a moment later, so the entry check at
                        // drop time races the deletion. By now (≥5 s) the deletion is done —
                        // a missing entry means a deliberate deactivate, not a crash.
                        if (!vm.IsLocal && !TunnelService.WireGuardServiceExists("WireGuardTunnel$" + vm.Name))
                        {
                            abort = true;
                            _log.Info($"[AutoReconnect] '{vm.Name}' was deactivated via the WireGuard app — not reconnecting.");
                        }
                    });

                    if (abort)
                    {
                        if (connected)
                            _log.Ok($"[AutoReconnect] '{vm.Name}' reconnected successfully.");
                        return;
                    }

                    // Run the connect on the UI thread and wait for it to actually finish —
                    // firing ConnectCommand and reading IsActive immediately made every
                    // attempt report failure while the connect was still in flight.
                    await await dispatcher.InvokeAsync(() =>
                    {
                        vm.PendingConnectSource = "Auto-reconnect";
                        return vm.ConnectAsync();
                    });
                    connected = vm.IsActive;

                    if (connected)
                    {
                        _log.Ok($"[AutoReconnect] '{vm.Name}' reconnected successfully.");
                        return;
                    }

                    _log.Warn($"[AutoReconnect] '{vm.Name}' — attempt {attempt} failed.");
                }

                _log.Warn($"[AutoReconnect] '{vm.Name}' — giving up after {AutoReconnectMaxAttempts} attempts.");
            }
            finally
            {
                lock (_reconnecting) { _reconnecting.Remove(vm.Name); }
            }
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
            {
                // Tag the connect source so it's recorded correctly in history.
                target.PendingConnectSource = result.Reason;
                target.ConnectCommand.Execute(null);
            }

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

        // ── Schedule (time-based rules) ───────────────────────────────────────

        private string? _scheduleActiveTunnel;
        private int     _lastScheduleMinute = -1;

        /// <summary>
        /// Time-rule scheduler. Invoked every second from the status poll but acts
        /// only when the wall-clock minute changes. Activates a schedule rule's tunnel
        /// when its window opens, and disconnects the schedule-driven tunnel when the
        /// window closes. Explicit WiFi rules and manual actions can still override.
        /// </summary>
        public void CheckSchedules()
        {
            var now = DateTime.Now;
            if (now.Minute == _lastScheduleMinute) return;
            _lastScheduleMinute = now.Minute;

            if (_config.Config.ManualMode) return;

            var r = _rules.EvaluateSchedules(_config.Config, now);

            switch (r.Action)
            {
                case RuleEngine.ActionKind.Activate:
                    if (!string.Equals(_scheduleActiveTunnel, r.TunnelName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _scheduleActiveTunnel = r.TunnelName;
                        ApplyRuleResult(r);
                    }
                    break;

                case RuleEngine.ActionKind.Disconnect:
                    _scheduleActiveTunnel = null;
                    ApplyRuleResult(r);
                    break;

                default: // None — no schedule window currently open
                    if (_scheduleActiveTunnel != null)
                    {
                        _scheduleActiveTunnel = null;
                        ApplyRuleResult(new RuleEngine.RuleResult(
                            RuleEngine.ActionKind.Disconnect, null, "Schedule window ended"));
                    }
                    break;
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
