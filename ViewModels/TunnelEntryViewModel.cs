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
                ConnectCommand.RaiseCanExecuteChanged();
                DisconnectCommand.RaiseCanExecuteChanged();
            }
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
            _tunnels.Connect(StoredTunnel, _config.Config);
            RefreshStatus();
        }

        private void DoDisconnect()
        {
            _tunnels.Disconnect(StoredTunnel);
            RefreshStatus();
        }
    }
}
