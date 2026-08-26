using System.Collections.Generic;
using System.Text.Json.Serialization;
using MasselGUARD.Infrastructure;

namespace MasselGUARD.Models
{
    /// <summary>
    /// An automation rule that maps a trigger to a WireGuard tunnel.
    /// Two kinds: "wifi" (triggered by an SSID) and "schedule" (triggered by
    /// day-of-week + time window). Empty Tunnel means "disconnect all".
    /// </summary>
    public class TunnelRule : ObservableObject
    {
        private string _ssid          = "";
        private string _tunnel        = "";
        private string _networkType   = "wifi";
        private string _name          = "";
        private string _kind          = "wifi";
        private string _startTime     = "09:00";
        private string _endTime       = "17:00";
        private List<int> _days       = new() { 1, 2, 3, 4, 5 }; // Mon–Fri
        private bool   _enabled       = true;
        private string _trustedWhen   = "untrusted";

        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        /// <summary>When false the rule is kept in the list but ignored by the engine.
        /// Lets a rule be temporarily switched off without deleting it.</summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                SetField(ref _enabled, value);
                OnPropertyChanged(nameof(RowOpacity));
                OnPropertyChanged(nameof(DisabledIcon));
            }
        }

        /// <summary>Row dimming for the rules list — full when enabled, faded when off.</summary>
        [JsonIgnore] public double RowOpacity => _enabled ? 1.0 : 0.4;

        /// <summary>Small marker (with trailing space) shown before a disabled rule's name;
        /// empty when enabled. Kept WPF-free so the shared CLI project still compiles.</summary>
        [JsonIgnore] public string DisabledIcon => _enabled ? "" : "⊘ ";

        /// <summary>"wifi" (SSID-triggered) | "schedule" (time-triggered).</summary>
        public string Kind
        {
            get => _kind;
            set { SetField(ref _kind, value); OnPropertyChanged(nameof(SsidDisplay)); OnPropertyChanged(nameof(RuleName)); OnPropertyChanged(nameof(ScheduleSummary)); }
        }

        /// <summary>Schedule start time, "HH:mm" (24-hour). Used when Kind == "schedule".</summary>
        public string StartTime
        {
            get => _startTime;
            set { SetField(ref _startTime, value); OnPropertyChanged(nameof(SsidDisplay)); OnPropertyChanged(nameof(ScheduleSummary)); OnPropertyChanged(nameof(RuleName)); }
        }

        /// <summary>Schedule end time, "HH:mm" (24-hour). An end &lt;= start means an overnight window.</summary>
        public string EndTime
        {
            get => _endTime;
            set { SetField(ref _endTime, value); OnPropertyChanged(nameof(SsidDisplay)); OnPropertyChanged(nameof(ScheduleSummary)); OnPropertyChanged(nameof(RuleName)); }
        }

        /// <summary>Days the schedule is active: 0=Sun … 6=Sat. Empty = every day.</summary>
        public List<int> Days
        {
            get => _days;
            set { SetField(ref _days, value); OnPropertyChanged(nameof(ScheduleSummary)); }
        }

        public string Ssid
        {
            get => _ssid;
            set { SetField(ref _ssid, value); OnPropertyChanged(nameof(SsidDisplay)); }
        }

        public string Tunnel
        {
            get => _tunnel;
            set { SetField(ref _tunnel, value); OnPropertyChanged(nameof(TunnelDisplay)); }
        }

        /// <summary>"wifi" | "ethernet" | "vpn" | "any"</summary>
        public string NetworkType
        {
            get => _networkType;
            set => SetField(ref _networkType, value);
        }

        /// <summary>
        /// For Kind=="trusted", which side of the trusted-network list activates
        /// this rule's tunnel:
        ///   "untrusted" — activate when the current SSID is NOT on the trusted
        ///                 list (protect on public networks; typically a full tunnel).
        ///   "trusted"   — activate when the current SSID IS on the list (bring a
        ///                 tunnel up only on known networks; e.g. a split tunnel).
        /// The rule fires only on its matching side; the other side falls through
        /// to the next rule and finally the Default action. Ignored for other kinds.
        /// </summary>
        public string TrustedWhen
        {
            get => _trustedWhen;
            set { SetField(ref _trustedWhen, value); OnPropertyChanged(nameof(SsidDisplay)); OnPropertyChanged(nameof(RuleName)); }
        }

        /// <summary>True when this trusted rule fires on networks that ARE on the list.</summary>
        [JsonIgnore]
        public bool TrustedWhenOnList =>
            string.Equals(_trustedWhen, "trusted", System.StringComparison.OrdinalIgnoreCase);


        [JsonIgnore]
        public string SsidDisplay =>
            _kind == "schedule" ? $"⏰ {ScheduleSummary}"
          : _kind == "trusted"  ? (TrustedWhenOnList ? "🛡 Trusted networks" : "🛡 Untrusted networks")
          : (string.IsNullOrEmpty(_ssid) ? "—" : $"📶 {_ssid}");   // 📶 matches the footer's current-SSID icon

        [JsonIgnore]
        public string TunnelDisplay =>
            string.IsNullOrEmpty(_tunnel) ? "\u2014 disconnect" : _tunnel;

        /// <summary>Auto-generated display name: user Name if set, else "SSID \u2192 tunnel/disconnect".</summary>
        [JsonIgnore]
        public string RuleName
        {
            get
            {
                if (!string.IsNullOrEmpty(_name)) return _name;
                if (_kind == "schedule")
                    return $"{ScheduleSummary} \u2192 {(string.IsNullOrEmpty(_tunnel) ? "disconnect" : _tunnel)}";
                if (_kind == "trusted")
                {
                    var side = TrustedWhenOnList ? "Trusted network" : "Untrusted network";
                    return $"{side} \u2192 {(string.IsNullOrEmpty(_tunnel) ? "disconnect" : _tunnel)}";
                }
                var ssid   = string.IsNullOrEmpty(_ssid)   ? "\u2014"          : _ssid;
                var target = string.IsNullOrEmpty(_tunnel) ? "disconnect" : _tunnel;
                return $"{ssid} \u2192 {target}";
            }
        }

        /// <summary>Compact "Days HH:mm–HH:mm" summary for schedule rules.</summary>
        [JsonIgnore]
        public string ScheduleSummary
        {
            get
            {
                string[] abbr = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
                string days;
                if (_days == null || _days.Count == 0 || _days.Count == 7)
                    days = "Daily";
                else if (_days.Count == 5 && _days.Contains(1) && _days.Contains(2) &&
                         _days.Contains(3) && _days.Contains(4) && _days.Contains(5))
                    days = "Weekdays";
                else if (_days.Count == 2 && _days.Contains(0) && _days.Contains(6))
                    days = "Weekend";
                else
                    days = string.Join(",", _days.FindAll(d => d >= 0 && d <= 6).ConvertAll(d => abbr[d]));
                return $"{days} {_startTime}–{_endTime}";
            }
        }

        /// <summary>"Connect" when a tunnel is set; "Disconnect" when the rule disconnects all.</summary>
        [JsonIgnore]
        public string ActionLabel => string.IsNullOrEmpty(_tunnel) ? "Disconnect" : "Connect";

        /// <summary>Number of times this rule has been triggered. Persisted in config.</summary>
        public int ExecutionCount { get; set; } = 0;
    }
}
