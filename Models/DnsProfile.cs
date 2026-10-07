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
        private string _listId = "";

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

        /// <summary>Id of the DNS server list entry this profile was created from ("" = made by hand). Only used to show "already added".</summary>
        public string ListId { get => _listId; set => SetField(ref _listId, value ?? ""); }

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
            ListId            = _listId,
        };
    }
}
