using System.Collections.Generic;
using System.Linq;

namespace MasselGUARD.Models
{
    /// <summary>
    /// A tunnel's split-tunneling intent, decoupled from <see cref="StoredTunnel"/> so a
    /// split-tunnel backend (<c>ISplitTunnelBackend</c>) never sees the whole model.
    /// WPF-free and CLI-shared. See <c>docs/SplitTunneling-Design.md</c> §3–4.
    /// </summary>
    public sealed class SplitConfig
    {
        /// <summary>"off" | "exclude" | "include".</summary>
        public string Mode { get; init; } = "off";
        /// <summary>Destination CIDRs/IPs (route-based split — 4.0.0).</summary>
        public IReadOnlyList<string> Ranges { get; init; } = new List<string>();
        /// <summary>App paths (per-app split — reserved for the WinDivert backend, inert in 4.0.0).</summary>
        public IReadOnlyList<string> Apps { get; init; } = new List<string>();

        /// <summary>True when route-based split should actually rewrite AllowedIPs
        /// (a non-"off" mode with at least one range).</summary>
        public bool HasRouteSplit =>
            !string.Equals(Mode, "off", System.StringComparison.OrdinalIgnoreCase) && Ranges.Count > 0;

        /// <summary>True when per-app split is configured (WinDivert territory — never true in 4.0.0 UI).</summary>
        public bool HasAppSplit => Apps.Count > 0;

        /// <summary>Builds a <see cref="SplitConfig"/> from a stored tunnel (null-safe copies).</summary>
        public static SplitConfig From(StoredTunnel t) => new()
        {
            Mode   = string.IsNullOrWhiteSpace(t.SplitMode) ? "off" : t.SplitMode.Trim().ToLowerInvariant(),
            Ranges = (t.SplitRanges ?? new List<string>())
                        .Where(r => !string.IsNullOrWhiteSpace(r))
                        .Select(r => r.Trim()).ToList(),
            Apps   = (t.SplitApps ?? new List<string>())
                        .Where(a => !string.IsNullOrWhiteSpace(a))
                        .Select(a => a.Trim()).ToList(),
        };
    }
}
