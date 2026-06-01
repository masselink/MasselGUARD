using System;
using System.Linq;
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
                OnPropertyChanged(nameof(ButtonLabel));
                OnPropertyChanged(nameof(ButtonEnabled));
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(NameColor));
                OnPropertyChanged(nameof(TrafficVisibility));
                ConnectCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
            }
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

        public string DnsLeakDisplay => _dnsStatus switch
        {
            TunnelDll.DnsLeakStatus.Secure         => "🔒 DNS",
            TunnelDll.DnsLeakStatus.PotentialLeak  => "⚠ DNS",
            TunnelDll.DnsLeakStatus.NotConfigured  => "ⓘ DNS",
            _                                      => "",
        };

        public string DnsLeakTooltip => _dnsStatus switch
        {
            TunnelDll.DnsLeakStatus.Secure        => "DNS routed through tunnel — protected",
            TunnelDll.DnsLeakStatus.PotentialLeak => "Other adapters have DNS servers — possible DNS leak",
            TunnelDll.DnsLeakStatus.NotConfigured => "No DNS configured in tunnel — queries bypass VPN",
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
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;

        public void UpdateDnsStatus(TunnelDll.DnsLeakStatus status)
        {
            _dnsStatus = status;
            OnPropertyChanged(nameof(DnsLeakDisplay));
            OnPropertyChanged(nameof(DnsLeakTooltip));
            OnPropertyChanged(nameof(DnsLeakColor));
            OnPropertyChanged(nameof(DnsLeakVisibility));
        }

        private bool _isAvailable = true;
        public  bool  IsAvailable
        {
            get => _isAvailable;
            set
            {
                if (!SetField(ref _isAvailable, value)) return;
                OnPropertyChanged(nameof(StatusText));
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

        public string StatusText    => IsActive
            ? $"● {UptimeDisplay}"
            : IsAvailable ? "○ Disconnected" : "Unavailable";

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
            _rxBytes = stats.RxBytes;
            _txBytes = stats.TxBytes;
            OnPropertyChanged(nameof(TrafficDisplay));
            OnPropertyChanged(nameof(TrafficVisibility));
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
                if (!IsActive || _connectedAt == null) return "Connected";
                var elapsed = DateTime.UtcNow - _connectedAt.Value;
                if (elapsed.TotalSeconds < 60)
                    return $"Connected  {(int)elapsed.TotalSeconds}s";
                if (elapsed.TotalMinutes < 60)
                    return $"Connected  {(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s";
                if (elapsed.TotalHours < 24)
                    return $"Connected  {(int)elapsed.TotalHours}h {elapsed.Minutes:D2}m";
                var days = (int)elapsed.TotalDays;
                return $"Connected  {days}d {elapsed.Hours:D2}h {elapsed.Minutes:D2}m";
            }
        }
        public string ButtonLabel   => IsActive ? "Disconnect" : "Connect";
        public bool   ButtonEnabled => IsAvailable || IsActive;
        public string ButtonTooltip => IsActive ? $"Disconnect {Name}" : $"Connect {Name}";

        public System.Windows.Media.Brush NameColor   => ThemeBrush(IsActive ? "Accent" : "TextPrimary");
        public System.Windows.Media.Brush TypeColor   => ThemeBrush("TextMuted");
        public System.Windows.Media.Brush StatusColor => ThemeBrush(IsActive ? "Accent" : IsAvailable ? "TextMuted" : "Danger");
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
                () => !_isActive && _isAvailable);
            DisconnectCommand = new RelayCommand(DoDisconnect,
                () => _isActive);
        }

        public void RefreshStatus()
        {
            bool active = _tunnels.IsActive(StoredTunnel);
            if (active && !_isActive)
                _connectedAt = DateTime.UtcNow;
            else if (!active)
                _connectedAt = null;
            IsActive = active;
            // Always notify StatusText so uptime counter updates every tick
            if (active) OnPropertyChanged(nameof(StatusText));
        }

        private void DoConnect()
        {
            UserDisconnected = false;
            string source = PendingConnectSource;
            PendingConnectSource = "Manual"; // consume and reset for next call
            _tunnels.Connect(StoredTunnel, _config.Config, source);
            RefreshStatus();
        }

        private void DoDisconnect()
        {
            UserDisconnected = true;
            _dnsStatus = TunnelDll.DnsLeakStatus.Unknown;
            OnPropertyChanged(nameof(DnsLeakVisibility));
            OnPropertyChanged(nameof(DnsLeakDisplay));
            OnPropertyChanged(nameof(DnsLeakColor));
            _rxBytes   = 0;
            _txBytes   = 0;
            _tunnels.Disconnect(StoredTunnel);
            RefreshStatus();
        }
    }
}
