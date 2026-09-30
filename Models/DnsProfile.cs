using System.Collections.Generic;
using System.Text.Json.Serialization;
using MasselGUARD.Infrastructure;

namespace MasselGUARD.Models
{
    /// <summary>
    /// A named DNS resolver that automation rules point at (see
    /// <c>docs/DnsAutomation-Design.md</c>). Applied to the physical adapter's TCP/IP
    /// settings - independent of any tunnel. WPF-free (the CLI globs <c>Models\*.cs</c>);
    /// <see cref="ObservableObject"/> lives in <c>Infrastructure</c>, which the CLI shares.
    /// </summary>
    public class DnsProfile : ObservableObject
    {
        // ── Virtual profile ids (resolved in code, never stored as a DnsProfile) ──
        /// <summary>Revert the interface to DHCP / network-provided DNS.</summary>
        public const string AutomaticId = "__automatic__";
        /// <summary>No DNS action (leave whatever is set).</summary>
        public const string NoneId = "";

        private string _name        = "";
        private string _v4Primary   = "";
        private string _v4Secondary = "";
        private string _v6Primary   = "";
        private string _v6Secondary = "";
        private string _encryption  = "plain";
        private string _dohTemplate = "";
        private bool   _requireEncryption;

        /// <summary>Stable id referenced by rules/config. Assigned once; never the display name.</summary>
        public string Id { get; set; } = System.Guid.NewGuid().ToString("N");

        /// <summary>User-facing name, e.g. "Cloudflare", "Work", "Quad9".</summary>
        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        // ── Plain (Do53) servers - empty string = that slot is unset ──────────────
        public string V4Primary   { get => _v4Primary;   set => SetField(ref _v4Primary,   value); }
        public string V4Secondary { get => _v4Secondary; set => SetField(ref _v4Secondary, value); }
        public string V6Primary   { get => _v6Primary;   set => SetField(ref _v6Primary,   value); }
        public string V6Secondary { get => _v6Secondary; set => SetField(ref _v6Secondary, value); }

        /// <summary>"plain" (Do53 only) | "doh" (DNS-over-HTTPS) | "auto" (DoH when the
        /// OS/servers support it, else plain).</summary>
        public string Encryption
        {
            get => _encryption;
            set { SetField(ref _encryption, value); OnPropertyChanged(nameof(EncryptionDisplay)); }
        }

        /// <summary>DoH URI template, e.g. "https://cloudflare-dns.com/dns-query".
        /// Used when <see cref="Encryption"/> != "plain".</summary>
        public string DohTemplate
        {
            get => _dohTemplate;
            set => SetField(ref _dohTemplate, value);
        }

        /// <summary>Fail-closed: when true and DoH can't be established, do NOT fall back to
        /// plain - surface an error instead. Default false (best-effort, may downgrade).</summary>
        public bool RequireEncryption
        {
            get => _requireEncryption;
            set => SetField(ref _requireEncryption, value);
        }

        // ── Display helpers (JSON-ignored) ───────────────────────────────────────
        /// <summary>True when this profile requests any form of encrypted DNS.</summary>
        [JsonIgnore]
        public bool IsEncrypted =>
            !string.Equals(_encryption, "plain", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>Short encryption label for the profiles list.</summary>
        [JsonIgnore]
        public string EncryptionDisplay => _encryption switch
        {
            "doh"  => "DoH",
            "auto" => "Auto",
            _       => "Plain",
        };

        /// <summary>Connection type identified from the entered data: the DoH/DoT/… scheme of the
        /// template if one is set, otherwise inferred from <see cref="Encryption"/> ("Plain"/"DoH"/"Auto").</summary>
        [JsonIgnore]
        public string TypeDisplay
        {
            get
            {
                var t = (_dohTemplate ?? "").Trim().ToLowerInvariant();
                if (t.StartsWith("https://")) return "DoH";
                if (t.StartsWith("tls://") || t.StartsWith("dot://")) return "DoT";
                if (t.StartsWith("quic://") || t.StartsWith("doq://")) return "DoQ";
                if (t.StartsWith("sdns://")) return "DNSCrypt";
                return _encryption switch { "doh" => "DoH", "auto" => "Auto", _ => "Plain" };
            }
        }

        /// <summary>The DNS connection to show in a list: the DoH/DoT template URL when set,
        /// otherwise the plain server address(es).</summary>
        [JsonIgnore]
        public string ConnectionDisplay =>
            !string.IsNullOrWhiteSpace(_dohTemplate) ? _dohTemplate.Trim() : ServersDisplay;

        /// <summary>Compact "primary / secondary" summary for the profiles list.</summary>
        [JsonIgnore]
        public string ServersDisplay
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(_v4Primary))   parts.Add(_v4Primary);
                if (!string.IsNullOrWhiteSpace(_v4Secondary)) parts.Add(_v4Secondary);
                if (!string.IsNullOrWhiteSpace(_v6Primary))   parts.Add(_v6Primary);
                if (!string.IsNullOrWhiteSpace(_v6Secondary)) parts.Add(_v6Secondary);
                return parts.Count == 0 ? "-" : string.Join(", ", parts);
            }
        }

        /// <summary>Independent deep copy (for edit-in-dialog then commit/cancel).</summary>
        public DnsProfile Clone() => new()
        {
            Id                = Id,
            Name              = _name,
            V4Primary         = _v4Primary,
            V4Secondary       = _v4Secondary,
            V6Primary         = _v6Primary,
            V6Secondary       = _v6Secondary,
            Encryption        = _encryption,
            DohTemplate       = _dohTemplate,
            RequireEncryption = _requireEncryption,
        };

        // ── Built-in presets (seed for the "Add preset" UI) ──────────────────────
        /// <summary>Well-known public resolvers. Each call returns fresh instances with new
        /// Ids so they can be added to <c>AppConfig.DnsProfiles</c> directly. NextDNS ships as
        /// a template the user completes with their own config id.</summary>
        public static List<DnsProfile> BuiltInPresets() => new()
        {
            new DnsProfile { Name = "Cloudflare", V4Primary = "1.1.1.1", V4Secondary = "1.0.0.1",
                             V6Primary = "2606:4700:4700::1111", V6Secondary = "2606:4700:4700::1001",
                             Encryption = "auto", DohTemplate = "https://cloudflare-dns.com/dns-query" },
            new DnsProfile { Name = "Cloudflare (malware-blocking)", V4Primary = "1.1.1.2", V4Secondary = "1.0.0.2",
                             V6Primary = "2606:4700:4700::1112", V6Secondary = "2606:4700:4700::1002",
                             Encryption = "auto", DohTemplate = "https://security.cloudflare-dns.com/dns-query" },
            new DnsProfile { Name = "Google", V4Primary = "8.8.8.8", V4Secondary = "8.8.4.4",
                             V6Primary = "2001:4860:4860::8888", V6Secondary = "2001:4860:4860::8844",
                             Encryption = "auto", DohTemplate = "https://dns.google/dns-query" },
            new DnsProfile { Name = "Quad9", V4Primary = "9.9.9.9", V4Secondary = "149.112.112.112",
                             V6Primary = "2620:fe::fe", V6Secondary = "2620:fe::9",
                             Encryption = "auto", DohTemplate = "https://dns.quad9.net/dns-query" },
            new DnsProfile { Name = "AdGuard", V4Primary = "94.140.14.14", V4Secondary = "94.140.15.15",
                             V6Primary = "2a10:50c0::ad1:ff", V6Secondary = "2a10:50c0::ad2:ff",
                             Encryption = "auto", DohTemplate = "https://dns.adguard-dns.com/dns-query" },
            new DnsProfile { Name = "OpenDNS", V4Primary = "208.67.222.222", V4Secondary = "208.67.220.220",
                             V6Primary = "2620:119:35::35", V6Secondary = "2620:119:53::53",
                             Encryption = "auto", DohTemplate = "https://doh.opendns.com/dns-query" },
            new DnsProfile { Name = "NextDNS (set your config id)", V4Primary = "", V4Secondary = "",
                             Encryption = "doh", DohTemplate = "https://dns.nextdns.io/YOUR_CONFIG_ID" },
        };
    }
}
