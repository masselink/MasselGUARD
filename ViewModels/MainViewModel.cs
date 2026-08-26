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
            // Re-localize each tunnel row's status/button text on a live language switch.
            Lang.Instance.LanguageChanged += (_, _) =>
            {
                foreach (var t in TunnelList) t.RefreshLocalized();
            };
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
        /// Edge-triggered data-usage warnings for the day / week / month caps. Each
        /// fires a one-time tray toast + log line the first time the tunnel's
        /// period-to-date total crosses that period's configured cap, and re-arms at
        /// the next period boundary (the dedupe key encodes the period). Warnings
        /// only — nothing is disconnected.
        /// </summary>
        private void MaybeWarnDataCaps(TunnelEntryViewModel t, long dayBytes, long weekBytes, long monthBytes, DateTime now)
        {
            // Skip the warning for periods that enforce (Kill): the kill toast supersedes
            // it, so we don't flash a warn toast a beat before the disconnect toast.
            if (!t.StoredTunnel.DailyCapKill)
                WarnCap(t, t.StoredTunnel.DailyCapMB,   dayBytes,
                        $"{t.Name}|D|{now:yyyy-MM-dd}", "Daily", "today");
            if (!t.StoredTunnel.WeeklyCapKill)
                WarnCap(t, t.StoredTunnel.WeeklyCapMB,  weekBytes,
                        $"{t.Name}|W|{System.Globalization.ISOWeek.GetYear(now)}W{System.Globalization.ISOWeek.GetWeekOfYear(now):00}", "Weekly", "this week");
            if (!t.StoredTunnel.MonthlyCapKill)
                WarnCap(t, t.StoredTunnel.MonthlyCapMB, monthBytes,
                        $"{t.Name}|M|{now:yyyy-MM}", "Monthly", "this month");
        }

        private void WarnCap(TunnelEntryViewModel t, int capMB, long usedBytes,
                             string dedupeKey, string label, string when)
        {
            if (capMB <= 0) return;                                  // period cap off
            if (usedBytes < (long)capMB * 1_048_576L) return;        // under cap
            if (!_dataCapWarned.Add(dedupeKey)) return;              // already warned this period

            _log.Warn($"{label} data cap reached for {t.Name}: {capMB} MB used {when}.");
            if (_config.Config.ShowTrayPopupOnSwitch)
                (Application.Current as App)?.ShowTrayNotification(
                    new Views.ToastNotification
                    {
                        Category   = "Data cap reached",
                        Primary    = $"{t.Name}: {label.ToLowerInvariant()} data cap reached",
                        Secondary  = $"{capMB} MB used {when}.",
                        StripColor = "Warning",
                        DurationMs = _config.Config.NotificationDurationSeconds * 1000,
                    });
        }

        // ── Cap enforcement (kill at cap) ─────────────────────────────────────
        /// <summary>Periods (name|letter|instance) the user chose to keep using past the cap.</summary>
        private readonly HashSet<string> _capOverridden = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Periods already auto-disconnected this instance (avoids a double kill/toast).</summary>
        private readonly HashSet<string> _capKilled = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>An over-budget, kill-enabled period that isn't currently overridden.</summary>
        public sealed record CapKillInfo(string Letter, string PeriodWord, long UsedBytes, long CapBytes, string OverrideKey);

        private (long day, long week, long month) PeriodUsage(TunnelEntryViewModel vm, DateTime now)
        {
            var dayStart   = now.Date;
            var weekStart  = dayStart.AddDays(-(((int)dayStart.DayOfWeek + 6) % 7));
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            long live = vm.IsActive ? vm.SessionBytes : 0;
            var (drx, dtx) = _history.GetUsageInRange(vm.Name, dayStart,   dayStart.AddDays(1));
            var (wrx, wtx) = _history.GetUsageInRange(vm.Name, weekStart,  weekStart.AddDays(7));
            var (mrx, mtx) = _history.GetUsageInRange(vm.Name, monthStart, monthStart.AddMonths(1));
            return (drx + dtx + live, wrx + wtx + live, mrx + mtx + live);
        }

        /// <summary>
        /// Returns the first period whose Kill is on and cap is reached (and not
        /// already overridden this period), or null. Used to gate connects and to
        /// auto-disconnect a running tunnel that crosses the line.
        /// </summary>
        public CapKillInfo? CapKillState(TunnelEntryViewModel vm)  => CapKillCore(vm, respectOverride: true);
        /// <summary>Over-budget kill period ignoring the "ignore this period" override (for diagnostics).</summary>
        private CapKillInfo? CapOverBudget(TunnelEntryViewModel vm) => CapKillCore(vm, respectOverride: false);

        private CapKillInfo? CapKillCore(TunnelEntryViewModel vm, bool respectOverride)
        {
            var st  = vm.StoredTunnel;
            if (!st.DailyCapKill && !st.WeeklyCapKill && !st.MonthlyCapKill) return null;

            var now = DateTime.UtcNow;
            var (day, week, month) = PeriodUsage(vm, now);

            CapKillInfo? Check(bool kill, int capMB, long used, string letter, string word, string instance)
            {
                if (!kill || capMB <= 0) return null;
                long cap = (long)capMB * 1_048_576L;
                if (used < cap) return null;
                string key = $"{vm.Name}|{letter}|{instance}";
                return (respectOverride && _capOverridden.Contains(key)) ? null
                    : new CapKillInfo(letter, word, used, cap, key);
            }

            return Check(st.DailyCapKill,   st.DailyCapMB,   day,   "d", "daily",   $"{now:yyyy-MM-dd}")
                ?? Check(st.WeeklyCapKill,  st.WeeklyCapMB,  week,  "w", "weekly",  $"{System.Globalization.ISOWeek.GetYear(now)}W{System.Globalization.ISOWeek.GetWeekOfYear(now):00}")
                ?? Check(st.MonthlyCapKill, st.MonthlyCapMB, month, "m", "monthly", $"{now:yyyy-MM}");
        }

        /// <summary>Forget any "ignore this period" override and kill marker for a tunnel — called
        /// when its caps are edited, so reconfiguring re-arms enforcement.</summary>
        public void ResetCapEnforcement(string name)
        {
            string prefix = name + "|";
            _capOverridden.RemoveWhere(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            _capKilled.RemoveWhere(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            _capIgnoredLogged.RemoveWhere(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
        private readonly HashSet<string> _capIgnoredLogged = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Mark every currently-over kill period for this tunnel as overridden (ignore the limit).</summary>
        public void OverrideCap(TunnelEntryViewModel vm)
        {
            var now = DateTime.UtcNow;
            var (day, week, month) = PeriodUsage(vm, now);
            var st = vm.StoredTunnel;
            void Maybe(bool kill, int capMB, long used, string letter, string instance)
            {
                if (kill && capMB > 0 && used >= (long)capMB * 1_048_576L)
                    _capOverridden.Add($"{vm.Name}|{letter}|{instance}");
            }
            Maybe(st.DailyCapKill,   st.DailyCapMB,   day,   "d", $"{now:yyyy-MM-dd}");
            Maybe(st.WeeklyCapKill,  st.WeeklyCapMB,  week,  "w", $"{System.Globalization.ISOWeek.GetYear(now)}W{System.Globalization.ISOWeek.GetWeekOfYear(now):00}");
            Maybe(st.MonthlyCapKill, st.MonthlyCapMB, month, "m", $"{now:yyyy-MM}");
        }

        /// <summary>Disconnect an active tunnel that has crossed a kill-enabled cap.</summary>
        private void MaybeKillOnCap(TunnelEntryViewModel t)
        {
            if (!t.IsActive) return;
            var kill = CapKillState(t);
            if (kill == null)
            {
                // Over a kill cap but kept up (the user chose to ignore it this period) — note it
                // once so an "over budget yet connected" tunnel is never a silent mystery.
                var over = CapOverBudget(t);
                if (over != null && _capIgnoredLogged.Add(over.OverrideKey))
                    _log.Info($"{t.Name}: over the {over.PeriodWord} cap but kept connected — limit ignored for this period.");
                return;
            }

            // Enforce the disconnect on every poll while over budget. A WireGuard-for-Windows
            // companion tunnel can be re-activated externally after we drop it, so a one-shot
            // kill would let it drift back up; re-issuing the disconnect keeps it down. The
            // warning + sticky toast still fire only once per period (below).
            t.DisconnectCommand.Execute(null);   // marks intentional → no auto-reconnect
            t.RefreshStatus();                   // reflect the drop in the row immediately
            t.CapKilled = true;                  // row shows a stop marker until the next start

            if (!_capKilled.Add(kill.OverrideKey)) return;   // already announced this period

            _log.Warn($"Data cap reached — disconnecting {t.Name}: {kill.PeriodWord} " +
                      $"{FmtBytes(kill.UsedBytes)} / {FmtBytes(kill.CapBytes)}.");

            // Sticky, interactive confirmation — offer to ignore the cap and reconnect.
            (Application.Current as App)?.ShowTrayNotification(new Views.ToastNotification
            {
                Category     = "Data cap reached",
                Primary      = $"{t.Name}: disconnected — {kill.PeriodWord} cap reached",
                Secondary    = $"{FmtBytes(kill.UsedBytes)} of {FmtBytes(kill.CapBytes)} used this period.",
                StripColor   = "Danger",
                Interactive  = true,
                ConfirmLabel = "Ignore & reconnect",
                CancelLabel  = "Dismiss",
                OnConfirm    = () =>
                {
                    OverrideCap(t);
                    t.PendingConnectSource = "Reconnected (cap ignored)";
                    t.ConnectCommand.Execute(null);
                    _log.Info($"Reconnected {t.Name} — {kill.PeriodWord} cap ignored for this period.");
                },
            });
        }

        /// <summary>Interactive toast for an automatic connect blocked by a reached cap.</summary>
        public void ShowCapConnectToast(TunnelEntryViewModel vm, CapKillInfo kill, Action onConnect)
        {
            _log.Info($"Auto-connect held: {vm.Name} is over the {kill.PeriodWord} cap " +
                      $"({FmtBytes(kill.UsedBytes)} / {FmtBytes(kill.CapBytes)}).");
            (Application.Current as App)?.ShowTrayNotification(new Views.ToastNotification
            {
                Category     = "Data cap reached",
                Primary      = $"{vm.Name}: over the {kill.PeriodWord} cap",
                Secondary    = $"{FmtBytes(kill.UsedBytes)} of {FmtBytes(kill.CapBytes)} used this period. Connect anyway?",
                StripColor   = "Danger",
                Interactive  = true,
                ConfirmLabel = "Connect",
                CancelLabel  = "Cancel",
                OnConfirm    = () => { OverrideCap(vm); onConnect(); },
                OnCancel     = () => _log.Info($"Auto-connect cancelled for {vm.Name} (over {kill.PeriodWord} cap)."),
            });
        }

        internal static string FmtBytes(long b)
        {
            if (b < 1_048_576)     return $"{b / 1024.0:F0} KB";
            if (b < 1_073_741_824) return $"{b / 1_048_576.0:F1} MB";
            return $"{b / 1_073_741_824.0:F2} GB";
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

                    // Data usage per period = closed-session history for the calendar
                    // day / week / month + the current live session.
                    var now        = DateTime.UtcNow;
                    var dayStart   = now.Date;                                              // UTC midnight
                    var weekStart  = dayStart.AddDays(-(((int)dayStart.DayOfWeek + 6) % 7)); // Monday
                    var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
                    long live = t.SessionBytes;
                    var (drx, dtx) = _history.GetUsageInRange(t.Name, dayStart,   dayStart.AddDays(1));
                    var (wrx, wtx) = _history.GetUsageInRange(t.Name, weekStart,  weekStart.AddDays(7));
                    var (mrx, mtx) = _history.GetUsageInRange(t.Name, monthStart, monthStart.AddMonths(1));
                    long dayTotal   = drx + dtx + live;
                    long weekTotal  = wrx + wtx + live;
                    long monthTotal = mrx + mtx + live;
                    t.UpdateUsage(dayTotal, weekTotal, monthTotal);
                    MaybeWarnDataCaps(t, dayTotal, weekTotal, monthTotal, now);
                    MaybeKillOnCap(t);   // disconnect if a kill-enabled cap is reached

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
                        // Don't auto-reconnect a tunnel that's over a kill-enabled cap.
                        if (CapKillState(vm) != null)
                        {
                            abort = true;
                            _log.Info($"[AutoReconnect] '{vm.Name}' is over its data cap — not reconnecting.");
                            return;
                        }
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

            // If a kill-enabled cap is already reached, hold the auto-connect and ask
            // via an interactive toast (Connect / Cancel; defaults to Cancel if ignored).
            var capKill = CapKillState(target);
            if (capKill != null && !target.IsActive)
            {
                ShowCapConnectToast(target, capKill, () => ActivateTarget(target, result.Reason));
                return;
            }

            ActivateTarget(target, result.Reason);
        }

        /// <summary>Stop other tunnels, connect <paramref name="target"/>, and toast the switch.</summary>
        private void ActivateTarget(TunnelEntryViewModel target, string reason)
        {
            foreach (var t in TunnelList.Where(t => t.IsActive && t != target))
                t.DisconnectCommand.Execute(null);

            if (!target.IsActive)
            {
                target.PendingConnectSource = reason;   // tag source for history
                target.ConnectCommand.Execute(null);
            }

            if (_config.Config.ShowTrayPopupOnSwitch)
            {
                int ms = _config.Config.NotificationDurationSeconds * 1000;
                bool isRule    = reason.StartsWith("Rule:");
                bool isOpen    = reason.StartsWith("Open network");
                bool isDefault = reason.StartsWith("Default");
                string category = isRule    ? "WiFi Rule Matched"
                                : isOpen    ? "Open Network Protection"
                                : isDefault ? "Default Action"
                                :             "Automation";
                string stripKey = isOpen    ? "Success"
                                : isDefault ? "Warning"
                                :             "Accent";
                (Application.Current as App)?.ShowTrayNotification(
                    new Views.ToastNotification
                    {
                        Category   = category,
                        Primary    = target.Name,
                        Secondary  = reason,
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
