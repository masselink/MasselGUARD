namespace MasselGUARD.Models
{
    /// <summary>
    /// One condition of a network rule: "the network's <see cref="By"/> is (or, with
    /// <see cref="Not"/>, is NOT) <see cref="Value"/>". A rule holds a list of them and matches only
    /// when ALL hold on the same network (AND). OR is done with separate rows of the rules table
    /// (first hit wins). WPF-free POCO, serialised with the rule.
    /// </summary>
    public class RuleCondition
    {
        /// <summary>"ssid" | "dnssuffix" | "gatewaymac" | "subnet" (<see cref="NetworkMatchBy"/>).</summary>
        public string By    { get; set; } = NetworkMatchBy.Ssid;

        /// <summary>Canonical value (suffix, MAC, CIDR list, or the SSID).</summary>
        public string Value { get; set; } = "";

        /// <summary>True = the condition is negated ("is not").</summary>
        public bool   Not   { get; set; }

        public RuleCondition Clone() => new() { By = By, Value = Value, Not = Not };

        /// <summary>English label of the match type ("SSID", "DNS suffix", "gateway MAC", "subnet").</summary>
        public static string ByLabel(string by) => by switch
        {
            NetworkMatchBy.DnsSuffix  => "DNS suffix",
            NetworkMatchBy.GatewayMac => "gateway MAC",
            NetworkMatchBy.Subnet     => "subnet",
            NetworkMatchBy.ConnectionType => "connection type",
            NetworkMatchBy.AdapterName    => "adapter name",
            NetworkMatchBy.AdapterDesc    => "adapter description",
            NetworkMatchBy.AdapterMac     => "adapter MAC",
            _                         => "SSID",
        };

        /// <summary>Plain-English form, e.g. "NOT subnet 10.0.0.0/8" or "SSID Home".</summary>
        public string ToPlain() => $"{(Not ? "NOT " : "")}{ByLabel(By)} {Value}";
    }
}
