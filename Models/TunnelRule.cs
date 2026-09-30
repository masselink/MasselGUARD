using System.Collections.Generic;
using System.Linq;
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
        private string _dnsProfileId  = "";

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

        /// <summary>Row dimming for the rules list - full when enabled, faded when off.</summary>
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
            set { SetField(ref _ssid, value); OnPropertyChanged(nameof(SsidDisplay)); OnPropertyChanged(nameof(RuleName)); }
        }

        // ── Network match (docs/NetworkIdentity-Design.md, section 3.1) ────────────────────────
        // A network rule (Kind "network", or "wifi" for rules saved before wired support) matches the
        // current network by one identity field. For MatchBy "ssid" the value is Ssid (kept for
        // compatibility, so existing rules and the dialog keep working untouched); for the other match
        // types it is MatchValue.

        private string _matchBy    = NetworkMatchBy.Ssid;
        private string _matchValue = "";

        /// <summary>"ssid" | "dnssuffix" | "gatewaymac" | "subnet" (<see cref="NetworkMatchBy"/>).</summary>
        public string MatchBy
        {
            get => _matchBy;
            set { SetField(ref _matchBy, value); OnPropertyChanged(nameof(SsidDisplay)); OnPropertyChanged(nameof(RuleName)); }
        }

        /// <summary>Value for the non-SSID match types (suffix, MAC, CIDR). Unused for MatchBy "ssid".</summary>
        public string MatchValue
        {
            get => _matchValue;
            set { SetField(ref _matchValue, value); OnPropertyChanged(nameof(SsidDisplay)); OnPropertyChanged(nameof(RuleName)); }
        }

        /// <summary>True for a network-triggered rule: the current "network" kind and the legacy "wifi" kind.</summary>
        [JsonIgnore]
        public bool IsNetworkKind => _kind == "network" || _kind == "wifi";

        /// <summary>The match type actually in force (an empty/unknown stored value means SSID).</summary>
        [JsonIgnore]
        public string EffectiveMatchBy => NetworkMatchBy.IsKnown(_matchBy) ? _matchBy : NetworkMatchBy.Ssid;

        /// <summary>The value the engine compares: <see cref="Ssid"/> for SSID rules, else <see cref="MatchValue"/>.</summary>
        [JsonIgnore]
        public string EffectiveMatchValue => EffectiveMatchBy == NetworkMatchBy.Ssid ? _ssid : _matchValue;

        /// <summary>English label for the match type ("SSID", "DNS suffix", "gateway MAC", "subnet").</summary>
        public static string MatchByLabel(string matchBy) => RuleCondition.ByLabel(matchBy);

        // ── Conditions (AND, with NOT) ─────────────────────────────────────────────────────────
        // A network rule can hold several conditions; ALL must hold on the same network. A rule saved
        // before conditions existed has none and is read as ONE condition built from MatchBy/Ssid/MatchValue,
        // so old configs keep working untouched. OR = add another row to the rules table.

        private List<RuleCondition>? _conditions;

        /// <summary>The rule's conditions, or null/empty for a rule that only has the legacy single match.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<RuleCondition>? Conditions
        {
            get => _conditions;
            set { SetField(ref _conditions, value); OnPropertyChanged(nameof(SsidDisplay)); OnPropertyChanged(nameof(RuleName)); OnPropertyChanged(nameof(ConditionsPlain)); }
        }

        /// <summary>The conditions in force: <see cref="Conditions"/> when set, else the legacy single match
        /// (none when that has no value).</summary>
        [JsonIgnore]
        public IReadOnlyList<RuleCondition> EffectiveConditions
        {
            get
            {
                if (_conditions is { Count: > 0 }) return _conditions;
                var v = EffectiveMatchValue;
                return string.IsNullOrWhiteSpace(v)
                    ? System.Array.Empty<RuleCondition>()
                    : new[] { new RuleCondition { By = EffectiveMatchBy, Value = v } };
            }
        }

        /// <summary>Replaces the conditions. A single positive condition is also mirrored into the legacy
        /// fields (<see cref="Ssid"/>/<see cref="MatchBy"/>/<see cref="MatchValue"/>) so older code and builds
        /// that only know the single match still see it; anything richer clears them.</summary>
        public void SetConditions(IEnumerable<RuleCondition> conditions)
        {
            var list = conditions.Select(c => c.Clone()).ToList();
            if (list.Count == 1 && !list[0].Not)
            {
                _conditions = null;                                   // the legacy single match carries it
                MatchBy = list[0].By;
                if (list[0].By == NetworkMatchBy.Ssid) { Ssid = list[0].Value; MatchValue = ""; }
                else                                   { Ssid = "";            MatchValue = list[0].Value; }
                OnPropertyChanged(nameof(Conditions));
            }
            else
            {
                MatchBy = NetworkMatchBy.Ssid; Ssid = ""; MatchValue = "";
                Conditions = list.Count == 0 ? null : list;
            }
            OnPropertyChanged(nameof(SsidDisplay));
            OnPropertyChanged(nameof(RuleName));
            OnPropertyChanged(nameof(ConditionsPlain));
        }

        /// <summary>English one-line form of the conditions, e.g. "SSID Home AND NOT subnet 10.0.0.0/8".</summary>
        [JsonIgnore]
        public string ConditionsPlain => string.Join(" AND ", EffectiveConditions.Select(c => c.ToPlain()));

        /// <summary>List-cell form: 📶 for SSIDs, 🔎 for the others, "NOT " for negated, joined by AND.</summary>
        private string ConditionsDisplay()
        {
            var conds = EffectiveConditions;
            if (conds.Count == 0) return "-";
            return string.Join("  AND  ", conds.Select(c =>
            {
                string not = c.Not ? "NOT " : "";
                return c.By == NetworkMatchBy.Ssid
                    ? $"📶 {not}{c.Value}"                          // 📶 matches the footer's current-SSID icon
                    : $"🔎 {not}{RuleCondition.ByLabel(c.By)}: {c.Value}";
            }));
        }

        public string Tunnel
        {
            get => _tunnel;
            set { SetField(ref _tunnel, value); OnPropertyChanged(nameof(TunnelDisplay)); }
        }

        /// <summary>
        /// Optional DNS profile id this rule applies, in parallel with its tunnel action
        /// (see <c>docs/DnsAutomation-Design.md</c>, Model C). "" = no DNS change;
        /// <see cref="DnsProfile.AutomaticId"/> = revert to DHCP; any other id = apply that
        /// profile. A rule with a DnsProfileId and an empty <see cref="Tunnel"/> is a
        /// DNS-only rule (no tunnel action).
        /// </summary>
        public string DnsProfileId
        {
            get => _dnsProfileId;
            set => SetField(ref _dnsProfileId, value);
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
        ///   "untrusted" - activate when the current SSID is NOT on the trusted
        ///                 list (protect on public networks; typically a full tunnel).
        ///   "trusted"   - activate when the current SSID IS on the list (bring a
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
          : ConditionsDisplay();

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
                // Name from the condition values: "Home", or "Home + 10.20.0.0/16" with several conditions.
                var shown  = string.Join(" + ", EffectiveConditions.Select(c => (c.Not ? "NOT " : "") + c.Value));
                var ssid   = string.IsNullOrEmpty(shown)   ? "\u2014"          : shown;
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
