using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using MasselGUARD.Infrastructure;
using MasselGUARD.Models;
using MasselGUARD.Services;

namespace MasselGUARD.ViewModels
{
    /// <summary>
    /// ViewModel for a single row in the tunnel list.
    /// Exposes Status, ButtonLabel, and Connect/Disconnect commands.
    /// </summary>
    public class TunnelEntryViewModel : ObservableObject
    {
        private readonly TunnelService _tunnels;
        private readonly LogService    _log;
        private readonly ConfigService _config;

        public StoredTunnel StoredTunnel { get; }

        public string Name    => StoredTunnel.Name;
        public string Group   => StoredTunnel.Group;
        public string Notes   => StoredTunnel.Notes;
        public bool   IsLocal => StoredTunnel.Source == "local";

        private bool _isActive;
        public  bool  IsActive
        {
            get => _isActive;
            private set
            {
                if (!SetField(ref _isActive, value)) return;
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusDot));
                OnPropertyChanged(nameof(StatusDotColor));
                OnPropertyChanged(nameof(ButtonLabel));
                OnPropertyChanged(nameof(ButtonEnabled));
                OnPropertyChanged(nameof(ButtonTooltip));
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(NameColor));
                OnPropertyChanged(nameof(TrafficVisibility));
                OnPropertyChanged(nameof(CapRingsVisibility));
                OnPropertyChanged(nameof(CapHighlightVisibility));
                if (value) CapKilled = false;   // the next start clears the kill marker
                ConnectCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
            }
        }

        // ── Transient connection state ────────────────────────────────────────
        private bool _isConnecting;
        /// <summary>True while a connect operation is in progress.</summary>
        public bool IsConnecting
        {
            get => _isConnecting;
            private set
            {
                if (!SetField(ref _isConnecting, value)) return;
                NotifyButtonState();
            }
        }

        private bool _isDisconnecting;
        /// <summary>True while a disconnect operation is in progress.</summary>
        public bool IsDisconnecting
        {
            get => _isDisconnecting;
            private set
            {
                if (!SetField(ref _isDisconnecting, value)) return;
                NotifyButtonState();
            }
        }

        private void NotifyButtonState()
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusDot));
            OnPropertyChanged(nameof(StatusDotColor));
            OnPropertyChanged(nameof(ButtonLabel));
            OnPropertyChanged(nameof(ButtonEnabled));
            OnPropertyChanged(nameof(ButtonTooltip));
            OnPropertyChanged(nameof(StatusColor));
            ConnectCommand.RaiseCanExecuteChanged();
            DisconnectCommand.RaiseCanExecuteChanged();
        }

        // ── Auto-reconnect flag ───────────────────────────────────────────────
        /// <summary>
        /// True when the last disconnect was user-initiated (or rule-triggered).
        /// Used to suppress auto-reconnect when the user deliberately disconnected.
        /// </summary>
        public bool UserDisconnected { get; set; } = false;

        // ── Connection-source tag (for history) ───────────────────────────────
        /// <summary>
        /// Set this before calling <see cref="ConnectCommand"/> to record
        /// how the connection was triggered (e.g. "WiFi Rule: HomeNet → Work VPN").
        /// Consumed and reset to "Manual" inside <see cref="DoConnect"/>.
        /// </summary>
        public string PendingConnectSource { get; set; } = "Manual";

        // ── DNS leak status ───────────────────────────────────────────────────
        private TunnelDll.DnsLeakStatus _dnsStatus = TunnelDll.DnsLeakStatus.Unknown;
        // True when machine-wide DNS-leak prevention (DisableSmartNameResolution) is active,
        // which contains a PotentialLeak — the inline ⚠ icon is then suppressed to match the
        // toast/log warnings, which already stay silent while prevention is enabled.
        private bool _dnsMitigated;

        public string DnsLeakDisplay => _dnsStatus switch
        {
            TunnelDll.DnsLeakStatus.Secure         => "🔒 DNS",
            TunnelDll.DnsLeakStatus.PotentialLeak  => "⚠ DNS",
            TunnelDll.DnsLeakStatus.NotConfigured  => "ⓘ DNS",
            _                                      => "",
        };

        public string DnsLeakTooltip => _dnsStatus switch
        {
            TunnelDll.DnsLeakStatus.Secure        => Lang.T("DnsTipSecure"),
            TunnelDll.DnsLeakStatus.PotentialLeak => Lang.T("DnsTipLeak"),
            TunnelDll.DnsLeakStatus.NotConfigured => Lang.T("DnsTipNotConfigured"),
            _                                     => "",
        };

        public System.Windows.Media.Brush DnsLeakColor => _dnsStatus switch
        {
            TunnelDll.DnsLeakStatus.Secure        => ThemeBrush("Success"),
            TunnelDll.DnsLeakStatus.PotentialLeak => ThemeBrush("WarningColor"),
            TunnelDll.DnsLeakStatus.NotConfigured => ThemeBrush("TextMuted"),
            _                                     => ThemeBrush("TextMuted"),
        };

        public System.Windows.Visibility DnsLeakVisibility =>
            IsActive
            && _dnsStatus != TunnelDll.DnsLeakStatus.Unknown
            && _config.Config.ShowDnsIndicator
            // Hide the leak warning icon when prevention has contained the leak.
            && !(_dnsStatus == TunnelDll.DnsLeakStatus.PotentialLeak && _dnsMitigated)
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;

        public void UpdateDnsStatus(TunnelDll.DnsLeakStatus status, bool mitigated = false)
        {
            _dnsStatus    = status;
            _dnsMitigated = mitigated;
            OnPropertyChanged(nameof(DnsLeakDisplay));
            OnPropertyChanged(nameof(DnsLeakTooltip));
            OnPropertyChanged(nameof(DnsLeakColor));
            OnPropertyChanged(nameof(DnsLeakVisibility));
        }

        // ── Tunnel health ─────────────────────────────────────────────────────
        // Derived from the adapter state + traffic movement observed on each stats
        // poll. (A true WireGuard handshake age would need pipe IPC the app does not
        // yet do; adapter-up + traffic movement is a reliable "is it actually alive".)
        public enum TunnelHealth { Unknown, Healthy, Idle, Down }

        private TunnelHealth _health = TunnelHealth.Unknown;
        private DateTime _lastTrafficMoveUtc = DateTime.MinValue;

        // Health/traffic is shown by colouring the single status dot (StatusDot /
        // StatusDotColor) in front of the status text — there is no separate dot.
        // HealthTooltip is surfaced on that dot.
        // null (not "") when there's nothing to say, so the always-visible dot shows no
        // empty tooltip popup while disconnected / (dis)connecting.
        public string? HealthTooltip => _health switch
        {
            TunnelHealth.Healthy => Lang.T("HealthHealthy"),
            TunnelHealth.Idle    => Lang.T("HealthIdle"),
            TunnelHealth.Down    => Lang.T("HealthDown"),
            _                    => null,
        };

        private void SetHealth(TunnelHealth h)
        {
            if (_health == h) return;
            _health = h;
            OnPropertyChanged(nameof(StatusDot));
            OnPropertyChanged(nameof(StatusDotColor));
            OnPropertyChanged(nameof(HealthTooltip));
        }

        private bool _isAvailable = true;
        public  bool  IsAvailable
        {
            get => _isAvailable;
            set
            {
                if (!SetField(ref _isAvailable, value)) return;
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusDot));
                OnPropertyChanged(nameof(StatusDotColor));
                OnPropertyChanged(nameof(StatusColor));
                ConnectCommand.RaiseCanExecuteChanged();
            }
        }

        private DateTime? _connectedAt;

        /// <summary>UTC time when this tunnel became active. Null when disconnected.</summary>
        public DateTime? ConnectedAt => _connectedAt;

        /// <summary>
        /// Restores a previously known connect time so that after a list rebuild
        /// the uptime counter continues from the original connection rather than resetting.
        /// Only takes effect while the tunnel is already active.
        /// </summary>
        public void RestoreConnectedAt(DateTime? connectedAt)
        {
            if (_isActive && connectedAt.HasValue)
                _connectedAt = connectedAt;
        }

        // The leading dot lives in StatusDot / StatusDotColor (a separate coloured element),
        // so StatusText itself carries no glyph.
        public string StatusText =>
            _isConnecting    ? Lang.T("StatusConnecting")    :
            _isDisconnecting ? Lang.T("StatusDisconnecting") :
            IsActive         ? UptimeDisplay                 :
            IsAvailable      ? Lang.T("StatusDisconnected")  : Lang.T("StatusUnavailable");

        /// <summary>The status dot glyph shown in front of the status text.
        /// Active tunnels show ● (or ▲ when the adapter is down); ◌ while (dis)connecting,
        /// ○ when disconnected, none when unavailable.</summary>
        public string StatusDot =>
            _isConnecting || _isDisconnecting ? "◌" :
            IsActive                          ? (_health == TunnelHealth.Down ? "▲" : "●") :
            IsAvailable                       ? "○" : "";

        /// <summary>Colour of the status dot. When active it reflects tunnel health/traffic
        /// (green healthy · amber idle · red down · accent until the first poll); muted while
        /// (dis)connecting or disconnected; danger when unavailable.</summary>
        public System.Windows.Media.Brush StatusDotColor =>
            _isConnecting || _isDisconnecting ? ThemeBrush("TextMuted") :
            IsActive ? _health switch
            {
                TunnelHealth.Healthy => ThemeBrush("Success"),
                TunnelHealth.Idle    => ThemeBrush("WarningColor"),
                TunnelHealth.Down    => ThemeBrush("Danger"),
                _                    => ThemeBrush("Accent"),
            } :
            IsAvailable ? ThemeBrush("TextMuted") : ThemeBrush("Danger");

        // ── Traffic stats ─────────────────────────────────────────────────────
        private long _rxBytes;
        private long _txBytes;

        /// <summary>Formatted traffic display, e.g. "↑ 1.2 MB  ↓ 3.4 MB".</summary>
        public string TrafficDisplay =>
            IsActive && (_txBytes > 0 || _rxBytes > 0)
                ? $"↑ {FormatBytes(_txBytes)}  ↓ {FormatBytes(_rxBytes)}"
                : "";

        public System.Windows.Visibility TrafficVisibility =>
            IsActive && (_txBytes > 0 || _rxBytes > 0)
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;

        /// <summary>Updates traffic stats from a <see cref="TunnelDll.TunnelStats"/> snapshot.</summary>
        public void UpdateStats(TunnelDll.TunnelStats stats)
        {
            bool moved = stats.RxBytes != _rxBytes || stats.TxBytes != _txBytes;
            _rxBytes = stats.RxBytes;
            _txBytes = stats.TxBytes;
            OnPropertyChanged(nameof(TrafficDisplay));
            OnPropertyChanged(nameof(TrafficVisibility));

            // Derive health from adapter state + traffic movement.
            if (!IsActive)
                SetHealth(TunnelHealth.Unknown);
            else if (!stats.AdapterFound || !stats.AdapterUp)
                SetHealth(TunnelHealth.Down);
            else
            {
                if (moved) _lastTrafficMoveUtc = DateTime.UtcNow;
                bool recent = (DateTime.UtcNow - _lastTrafficMoveUtc).TotalSeconds < 30;
                SetHealth(recent ? TunnelHealth.Healthy : TunnelHealth.Idle);
            }
        }

        // ── Data-usage warnings (day / week / month) ──────────────────────────
        private long _dayBytes, _weekBytes, _monthlyBytes;

        /// <summary>Current session's total bytes (rx+tx) — used for live cap accounting.</summary>
        public long SessionBytes => _rxBytes + _txBytes;

        /// <summary>Configured caps in bytes; 0 = no cap for that period.</summary>
        public long DailyCapBytes   => (long)StoredTunnel.DailyCapMB   * 1_048_576L;
        public long WeeklyCapBytes  => (long)StoredTunnel.WeeklyCapMB  * 1_048_576L;
        public long MonthlyCapBytes => (long)StoredTunnel.MonthlyCapMB * 1_048_576L;

        /// <summary>Period-to-date usage in bytes (history + live session), for cap enforcement.</summary>
        public long DayUsageBytes   => _dayBytes;
        public long WeekUsageBytes  => _weekBytes;
        public long MonthUsageBytes => _monthlyBytes;

        /// <summary>True when any period is configured (so usage is worth showing).</summary>
        private bool AnyCapConfigured =>
            StoredTunnel.DailyCapMB > 0 || StoredTunnel.WeeklyCapMB > 0 || StoredTunnel.MonthlyCapMB > 0;

        // The row's inline usage figure stays month-to-date; the tooltip breaks out
        // whichever caps are configured, and the highlight/colour react to any breach.
        public string MonthlyUsageDisplay =>
            StoredTunnel.MonthlyCapMB > 0
                ? $"▤ {FormatBytes(_monthlyBytes)} / {FormatBytes(MonthlyCapBytes)}"
                : $"▤ {FormatBytes(_monthlyBytes)}";

        public string MonthlyUsageTooltip
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                void Line(string label, long used, long cap, int capMB)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(capMB > 0
                        ? $"{label}: {FormatBytes(used)} of {FormatBytes(cap)} cap{(cap > 0 && used >= cap ? "  ⚠ over" : "")}"
                        : $"{label}: {FormatBytes(used)}");
                }
                Line("Today",      _dayBytes,     DailyCapBytes,   StoredTunnel.DailyCapMB);
                Line("This week",  _weekBytes,    WeeklyCapBytes,  StoredTunnel.WeeklyCapMB);
                Line("This month", _monthlyBytes, MonthlyCapBytes, StoredTunnel.MonthlyCapMB);
                return sb.ToString();
            }
        }

        public System.Windows.Visibility MonthlyUsageVisibility =>
            IsActive && (_monthlyBytes > 0 || AnyCapConfigured)
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;

        /// <summary>Over any configured cap — drives the usage colour and the row highlight.</summary>
        public bool IsOverCap =>
            (DailyCapBytes   > 0 && _dayBytes     >= DailyCapBytes)   ||
            (WeeklyCapBytes  > 0 && _weekBytes    >= WeeklyCapBytes)  ||
            (MonthlyCapBytes > 0 && _monthlyBytes >= MonthlyCapBytes);

        public System.Windows.Media.Brush MonthlyUsageColor =>
            IsOverCap ? ThemeBrush("WarningColor") : ThemeBrush("TextMuted");

        // ── Cap usage rings — shown in the row when connected ──────────────────
        // Three concentric arcs (day inner · week middle · month outer). A ring is
        // drawn only when that period's cap is set; each fills 0→360° as usage → cap.
        // The <see cref="Views.CapRings"/> control turns a ring amber near the limit
        // and red once over it, and the hover tooltip breaks the numbers out.
        private static double Frac(long used, long cap) => cap > 0 ? (double)used / cap : 0.0;

        public double DayCapFraction   => Frac(_dayBytes,     DailyCapBytes);
        public double WeekCapFraction  => Frac(_weekBytes,    WeeklyCapBytes);
        public double MonthCapFraction => Frac(_monthlyBytes, MonthlyCapBytes);

        public bool DayCapSet   => DailyCapBytes   > 0;
        public bool WeekCapSet  => WeeklyCapBytes  > 0;
        public bool MonthCapSet => MonthlyCapBytes > 0;

        // Rings show whenever a cap is configured — even when disconnected, where the
        // CapRings control renders them greyed (Active=false) but at real usage. The
        // per-tunnel HideCapRing flag suppresses them regardless (warn/enforce still run).
        public System.Windows.Visibility CapRingsVisibility =>
            AnyCapConfigured && !StoredTunnel.HideCapRing
                ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        /// <summary>Per-period breakdown for the rings' hover tooltip (set periods only).</summary>
        public string CapRingsTooltip
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                void Line(string label, long used, long cap)
                {
                    if (cap <= 0) return;
                    if (sb.Length > 0) sb.Append('\n');
                    int pct = (int)System.Math.Round(100.0 * used / cap);
                    sb.Append($"{label}: {FormatBytes(used)} / {FormatBytes(cap)} ({pct}%){(used >= cap ? "  ⚠ over" : "")}");
                }
                Line("Day",   _dayBytes,     DailyCapBytes);
                Line("Week",  _weekBytes,    WeeklyCapBytes);
                Line("Month", _monthlyBytes, MonthlyCapBytes);
                return sb.ToString();
            }
        }

        /// <summary>Left accent strip on the tunnel row — amber while over a cap, else invisible.</summary>
        public System.Windows.Media.Brush CapHighlightBrush =>
            IsOverCap ? ThemeBrush("WarningColor") : System.Windows.Media.Brushes.Transparent;

        public System.Windows.Visibility CapHighlightVisibility =>
            IsOverCap && IsActive ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        // ── Cap-kill marker (shown after Connect until the next start) ─────────
        private bool _capKilled;
        /// <summary>True after enforcement disconnected this tunnel at a cap; cleared on the next connect.</summary>
        public bool CapKilled
        {
            get => _capKilled;
            set
            {
                if (SetField(ref _capKilled, value))
                    OnPropertyChanged(nameof(CapKilledVisibility));
            }
        }

        public System.Windows.Visibility CapKilledVisibility =>
            _capKilled ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        public string CapKilledTooltip => Lang.T("CapKilledTooltip");

        /// <summary>Re-raise the localized row strings so a live language switch updates them.</summary>
        public void RefreshLocalized()
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ButtonLabel));
            OnPropertyChanged(nameof(ButtonTooltip));
            OnPropertyChanged(nameof(HealthTooltip));
            OnPropertyChanged(nameof(DnsLeakTooltip));
            OnPropertyChanged(nameof(CapKilledTooltip));
        }

        /// <summary>Called each stats poll with period-to-date totals (history + live session).</summary>
        public void UpdateUsage(long dayBytes, long weekBytes, long monthBytes)
        {
            _dayBytes     = dayBytes;
            _weekBytes    = weekBytes;
            _monthlyBytes = monthBytes;
            OnPropertyChanged(nameof(MonthlyUsageDisplay));
            OnPropertyChanged(nameof(MonthlyUsageTooltip));
            OnPropertyChanged(nameof(MonthlyUsageVisibility));
            OnPropertyChanged(nameof(IsOverCap));
            OnPropertyChanged(nameof(MonthlyUsageColor));
            OnPropertyChanged(nameof(DayCapFraction));
            OnPropertyChanged(nameof(WeekCapFraction));
            OnPropertyChanged(nameof(MonthCapFraction));
            OnPropertyChanged(nameof(DayCapSet));
            OnPropertyChanged(nameof(WeekCapSet));
            OnPropertyChanged(nameof(MonthCapSet));
            OnPropertyChanged(nameof(CapRingsVisibility));
            OnPropertyChanged(nameof(CapRingsTooltip));
            OnPropertyChanged(nameof(CapHighlightBrush));
            OnPropertyChanged(nameof(CapHighlightVisibility));
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)               return $"{bytes} B";
            if (bytes < 1_048_576)          return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1_073_741_824)      return $"{bytes / 1_048_576.0:F1} MB";
            return                                  $"{bytes / 1_073_741_824.0:F2} GB";
        }

        public string UptimeDisplay
        {
            get
            {
                var c = Lang.T("StatusConnected");
                if (!IsActive || _connectedAt == null) return c;
                var elapsed = DateTime.UtcNow - _connectedAt.Value;
                if (elapsed.TotalSeconds < 60)
                    return $"{c}  {(int)elapsed.TotalSeconds}s";
                if (elapsed.TotalMinutes < 60)
                    return $"{c}  {(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s";
                if (elapsed.TotalHours < 24)
                    return $"{c}  {(int)elapsed.TotalHours}h {elapsed.Minutes:D2}m";
                var days = (int)elapsed.TotalDays;
                return $"{c}  {days}d {elapsed.Hours:D2}h {elapsed.Minutes:D2}m";
            }
        }
        public string ButtonLabel =>
            _isConnecting    ? Lang.T("StatusConnecting")    :
            _isDisconnecting ? Lang.T("StatusDisconnecting") :
            IsActive         ? Lang.T("BtnDisconnect")       : Lang.T("BtnConnect");

        public bool ButtonEnabled =>
            !_isConnecting && !_isDisconnecting && (IsAvailable || IsActive);

        public string ButtonTooltip =>
            _isConnecting    ? Lang.T("BtnTipConnecting", Name)    :
            _isDisconnecting ? Lang.T("BtnTipDisconnecting", Name) :
            IsActive         ? Lang.T("BtnTipDisconnect", Name)    : Lang.T("BtnTipConnect", Name);

        public System.Windows.Media.Brush NameColor   => ThemeBrush(IsActive ? "Accent" : "TextPrimary");
        public System.Windows.Media.Brush TypeColor   => ThemeBrush("TextMuted");
        public System.Windows.Media.Brush StatusColor =>
            _isConnecting || _isDisconnecting ? ThemeBrush("TextMuted") :
            IsActive                          ? ThemeBrush("Accent")    :
            IsAvailable                       ? ThemeBrush("TextMuted") : ThemeBrush("Danger");
        public System.Windows.TextDecorationCollection? NameDecoration => null;

        /// <summary>4px colour strip shown before the tunnel name. Transparent when no group colour.</summary>
        public bool IsDefaultTunnel  => _config.Config.DefaultTunnel == StoredTunnel.Name
                                     && _config.Config.DefaultAction == "activate";
        public bool IsOpenProtection => _config.Config.OpenWifiTunnel == StoredTunnel.Name;

        public System.Windows.Visibility DefaultBadgeVis =>
            IsDefaultTunnel  ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        public System.Windows.Visibility OpenBadgeVis =>
            IsOpenProtection ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        public void NotifyBadgesChanged()
        {
            OnPropertyChanged(nameof(IsDefaultTunnel));
            OnPropertyChanged(nameof(IsOpenProtection));
            OnPropertyChanged(nameof(DefaultBadgeVis));
            OnPropertyChanged(nameof(OpenBadgeVis));
        }

        /// <summary>Number of WiFi rules that reference this tunnel (0 = not used in any rule).</summary>
        public int RuleCount =>
            _config.Config.Rules.Count(r => r.Tunnel == StoredTunnel.Name);

        /// <summary>Accent when used in rules, muted when not.</summary>
        public System.Windows.Media.Brush RuleCountColor =>
            RuleCount > 0 ? ThemeBrush("Accent") : ThemeBrush("TextMuted");

        /// <summary>Underline when there are rules to click through to.</summary>
        public System.Windows.TextDecorationCollection? RuleCountDecoration =>
            RuleCount > 0 ? System.Windows.TextDecorations.Underline : null;

        /// <summary>Tooltip explaining the click action.</summary>
        public string? RuleCountTooltip =>
            RuleCount > 0 ? $"Click to highlight {RuleCount} rule(s) for this tunnel" : null;

        public System.Windows.Media.Brush GroupAccentBrush
        {
            get
            {
                var grp = _config.Config.TunnelGroups
                    .FirstOrDefault(g => g.Name == StoredTunnel.Group);
                var col = grp?.Color ?? "";
                if (string.IsNullOrEmpty(col)) return System.Windows.Media.Brushes.Transparent;
                try
                {
                    if (col.StartsWith("#"))
                        return new System.Windows.Media.SolidColorBrush(
                            (System.Windows.Media.Color)
                            System.Windows.Media.ColorConverter.ConvertFromString(col));
                    return (System.Windows.Media.Brush)
                        System.Windows.Application.Current.FindResource(col);
                }
                catch { return System.Windows.Media.Brushes.Transparent; }
            }
        }

        private static System.Windows.Media.Brush ThemeBrush(string key)
        {
            try { return (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource(key); }
            catch { return System.Windows.Media.Brushes.White; }
        }

        public string TypeLabel  => IsLocal ? "Local" : "WireGuard";
        public string TypeColour => IsLocal ? "Accent" : "TextMuted";

        public RelayCommand ConnectCommand    { get; }
        public RelayCommand DisconnectCommand { get; }

        public TunnelEntryViewModel(
            StoredTunnel   stored,
            TunnelService  tunnels,
            LogService     log,
            ConfigService  config)
        {
            StoredTunnel = stored;
            _tunnels     = tunnels;
            _log         = log;
            _config      = config;

            ConnectCommand    = new RelayCommand(DoConnect,
                () => !_isActive && !_isConnecting && !_isDisconnecting && _isAvailable);
            DisconnectCommand = new RelayCommand(DoDisconnect,
                () => _isActive && !_isConnecting && !_isDisconnecting);
        }

        public void RefreshStatus()
        {
            bool active = _tunnels.IsActive(StoredTunnel);
            if (active && !_isActive)
                _connectedAt = DateTime.UtcNow;
            else if (!active)
                _connectedAt = null;

            // Tunnel went inactive externally (e.g. CLI disconnect, service crash).
            // Clear DNS badge and traffic stats so stale indicators don't linger.
            if (!active && _isActive)
            {
                _dnsStatus = TunnelDll.DnsLeakStatus.Unknown;
                OnPropertyChanged(nameof(DnsLeakVisibility));
                OnPropertyChanged(nameof(DnsLeakDisplay));
                OnPropertyChanged(nameof(DnsLeakColor));
                _rxBytes = 0;
                _txBytes = 0;
                OnPropertyChanged(nameof(TrafficDisplay));
                OnPropertyChanged(nameof(TrafficVisibility));
                SetHealth(TunnelHealth.Unknown);
            }

            IsActive = active;
            // Always notify StatusText so uptime counter updates every tick
            if (active) OnPropertyChanged(nameof(StatusText));
        }

        private async void DoConnect() => await ConnectAsync();

        /// <summary>
        /// Awaitable connect — auto-reconnect calls this instead of ConnectCommand so it
        /// can wait for the result rather than reading IsActive mid-connect.
        /// </summary>
        public async Task ConnectAsync()
        {
            UserDisconnected = false;
            string source = PendingConnectSource;
            PendingConnectSource = "Manual"; // consume and reset for next call
            IsConnecting = true;
            try
            {
                // Run on a background thread — companion tunnel connects block for several
                // seconds (CreateService P/Invoke + WaitForStatus polling).
                await Task.Run(() => _tunnels.Connect(StoredTunnel, _config.Config, source));
            }
            finally
            {
                IsConnecting = false;
            }
            RefreshStatus();
        }

        private async void DoDisconnect()
        {
            UserDisconnected = true;
            _dnsStatus = TunnelDll.DnsLeakStatus.Unknown;
            OnPropertyChanged(nameof(DnsLeakVisibility));
            OnPropertyChanged(nameof(DnsLeakDisplay));
            OnPropertyChanged(nameof(DnsLeakColor));
            _rxBytes   = 0;
            _txBytes   = 0;
            IsDisconnecting = true;
            try
            {
                await Task.Run(() => _tunnels.Disconnect(StoredTunnel, _config.Config));
            }
            finally
            {
                IsDisconnecting = false;
            }
            RefreshStatus();
        }
    }
}
