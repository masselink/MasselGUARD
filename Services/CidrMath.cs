using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Pure CIDR set-math for split tunneling (design §5). Computes an <b>effective</b>
    /// <c>AllowedIPs</c> list from a base list + a split mode + a set of ranges:
    ///
    /// <list type="bullet">
    ///   <item><c>off</c>     — base returned verbatim.</item>
    ///   <item><c>exclude</c> — base <b>minus</b> the ranges (full tunnel except these). This is
    ///         the classic WireGuard "AllowedIPs calculator": each covering block is split in
    ///         half until it either falls entirely inside or entirely outside an excluded range.</item>
    ///   <item><c>include</c> — only the ranges are tunneled (intersected with the base).</item>
    /// </list>
    ///
    /// IPv4 and IPv6 are computed independently and never mix. The result is emitted v4-first,
    /// each family sorted by network then prefix — a comma-separated string ready to drop into
    /// the <c>[Peer] AllowedIPs =</c> line via <see cref="MasselGUARD.Cli.WireGuardConf.Patch"/>.
    ///
    /// WPF-free and CLI-shared (listed in <c>MasselGUARDcli.csproj</c>). No route-table code:
    /// wireguard-NT programs routes from whatever AllowedIPs we hand it.
    /// </summary>
    public static class CidrMath
    {
        // ── Public entry point ────────────────────────────────────────────────

        /// <summary>
        /// Computes the effective AllowedIPs for a split config. <paramref name="splitMode"/> is
        /// "off" | "exclude" | "include" (case-insensitive; anything unrecognised = "off").
        /// Invalid range/base tokens are skipped (callers validate earlier via
        /// <see cref="MasselGUARD.Cli.WireGuardConf.Validate"/>).
        /// </summary>
        public static string ComputeEffectiveAllowedIPs(
            string baseAllowedIPs, string splitMode, IEnumerable<string> ranges)
        {
            var mode = (splitMode ?? "off").Trim().ToLowerInvariant();
            if (mode == "off") return (baseAllowedIPs ?? "").Trim();

            var baseCidrs  = ParseList(baseAllowedIPs);
            var rangeCidrs = ParseList(string.Join(",", ranges ?? Enumerable.Empty<string>()));

            var v4Base  = baseCidrs .Where(c => c.IsV4).ToList();
            var v6Base  = baseCidrs .Where(c => !c.IsV4).ToList();
            var v4Range = rangeCidrs.Where(c => c.IsV4).ToList();
            var v6Range = rangeCidrs.Where(c => !c.IsV4).ToList();

            List<Cidr> v4Out, v6Out;

            if (mode == "exclude")
            {
                // A tunnel with no AllowedIPs at all is meaningless for exclusion — treat a
                // wholly-empty base as a full tunnel so exclusion has something to carve from.
                if (v4Base.Count == 0 && v6Base.Count == 0)
                {
                    v4Base.Add(Cidr.Parse("0.0.0.0/0")!.Value);
                    v6Base.Add(Cidr.Parse("::/0")!.Value);
                }
                v4Out = Subtract(v4Base, v4Range);
                v6Out = Subtract(v6Base, v6Range);
            }
            else // include
            {
                // Include is authoritative about what to tunnel; intersect with the base so a
                // restrictive server (base narrower than 0/0) still can't be over-routed. An
                // absent base family is treated as full so the user's includes aren't dropped.
                v4Out = Intersect(v4Base.Count > 0 ? v4Base : Full(AddressFamily.InterNetwork),   v4Range);
                v6Out = Intersect(v6Base.Count > 0 ? v6Base : Full(AddressFamily.InterNetworkV6), v6Range);
            }

            var ordered = Sort(v4Out).Concat(Sort(v6Out)).Select(c => c.ToString());
            return string.Join(", ", ordered);
        }

        // ── Family helpers ────────────────────────────────────────────────────

        private static List<Cidr> Full(AddressFamily fam) =>
            new() { Cidr.Parse(fam == AddressFamily.InterNetwork ? "0.0.0.0/0" : "::/0")!.Value };

        private static List<Cidr> ParseList(string? csv)
        {
            var result = new List<Cidr>();
            if (string.IsNullOrWhiteSpace(csv)) return result;
            foreach (var tok in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var c = Cidr.Parse(tok);
                if (c.HasValue) result.Add(c.Value);
            }
            return result;
        }

        private static List<Cidr> Sort(IEnumerable<Cidr> set) =>
            set.Distinct()
               .OrderBy(c => c.Network)
               .ThenBy(c => c.Prefix)
               .ToList();

        // ── Set operations ────────────────────────────────────────────────────

        /// <summary>Subtracts a set of excluded CIDRs from a base set (same family). Returns the
        /// minimal-ish cover of (base − ⋃excludes). Excludes are applied sequentially; because any
        /// two CIDRs are either disjoint or nested, each step stays exact.</summary>
        private static List<Cidr> Subtract(List<Cidr> baseSet, List<Cidr> excludes)
        {
            var current = new List<Cidr>(baseSet);
            foreach (var e in excludes)
            {
                var next = new List<Cidr>();
                foreach (var b in current) next.AddRange(SubtractOne(b, e));
                current = next;
            }
            return current;
        }

        /// <summary>block − exclude, as a list of CIDRs.</summary>
        private static IEnumerable<Cidr> SubtractOne(Cidr block, Cidr exclude)
        {
            if (!block.Overlaps(exclude)) { yield return block; yield break; }
            if (exclude.Contains(block))  yield break;               // fully removed
            // block strictly contains exclude → split into two halves and recurse
            var (lo, hi) = block.Split();
            foreach (var c in SubtractOne(lo, exclude)) yield return c;
            foreach (var c in SubtractOne(hi, exclude)) yield return c;
        }

        /// <summary>includes ∩ base (same family), as a list of CIDRs.</summary>
        private static List<Cidr> Intersect(List<Cidr> baseSet, List<Cidr> includes)
        {
            var result = new List<Cidr>();
            foreach (var i in includes)
                foreach (var b in baseSet)
                {
                    if (b.Contains(i))      result.Add(i);   // include fits inside a base block
                    else if (i.Contains(b)) result.Add(b);   // include covers a base block → clip to base
                    // disjoint → contributes nothing
                }
            return result;
        }

        // ── The CIDR value type ───────────────────────────────────────────────

        /// <summary>An IPv4 or IPv6 CIDR block: a masked network address (as a big-endian
        /// <see cref="BigInteger"/>) plus a prefix length. Two CIDRs of the same family are always
        /// either disjoint or one contains the other — the property the split math relies on.</summary>
        private readonly struct Cidr : IEquatable<Cidr>
        {
            public readonly BigInteger Network;  // masked to Prefix
            public readonly int        Prefix;
            public readonly bool       IsV4;

            private int Bits => IsV4 ? 32 : 128;

            private Cidr(BigInteger network, int prefix, bool isV4)
            {
                IsV4    = isV4;
                Prefix  = prefix;
                Network = network & MaskFor(prefix, isV4 ? 32 : 128);
            }

            /// <summary>Parses "ip", "ip/prefix" (v4 or v6). A bare IP becomes a host route
            /// (/32 or /128). Returns null for anything invalid.</summary>
            public static Cidr? Parse(string token)
            {
                if (string.IsNullOrWhiteSpace(token)) return null;
                token = token.Trim();

                int    prefix;
                string ipPart;
                var slash = token.IndexOf('/');
                if (slash < 0) { ipPart = token; prefix = -1; }
                else
                {
                    ipPart = token[..slash];
                    if (!int.TryParse(token[(slash + 1)..], out prefix)) return null;
                }

                if (!IPAddress.TryParse(ipPart, out var ip)) return null;
                bool isV4 = ip.AddressFamily == AddressFamily.InterNetwork;
                if (!isV4 && ip.AddressFamily != AddressFamily.InterNetworkV6) return null;

                int bits = isV4 ? 32 : 128;
                if (prefix == -1) prefix = bits;                 // bare host
                if (prefix < 0 || prefix > bits) return null;

                return new Cidr(ToBigInteger(ip), prefix, isV4);
            }

            /// <summary>True if this block contains <paramref name="other"/> (equal counts).</summary>
            public bool Contains(Cidr other)
            {
                if (IsV4 != other.IsV4)   return false;
                if (other.Prefix < Prefix) return false;         // other is larger → can't be inside
                return (other.Network & MaskFor(Prefix, Bits)) == Network;
            }

            /// <summary>Two CIDRs overlap iff one contains the other (CIDR blocks never partially overlap).</summary>
            public bool Overlaps(Cidr other) => Contains(other) || other.Contains(this);

            /// <summary>Splits this block into its two halves (prefix + 1).</summary>
            public (Cidr lo, Cidr hi) Split()
            {
                int childPrefix = Prefix + 1;
                var half        = BigInteger.One << (Bits - childPrefix);
                var lo          = new Cidr(Network,        childPrefix, IsV4);
                var hi          = new Cidr(Network + half, childPrefix, IsV4);
                return (lo, hi);
            }

            public override string ToString() => $"{ToIpString()}/{Prefix}";

            private string ToIpString()
            {
                int    nbytes = IsV4 ? 4 : 16;
                byte[] full   = Network.ToByteArray(isUnsigned: true, isBigEndian: true);
                // Left-pad to the fixed width (BigInteger drops leading zero bytes).
                byte[] buf = new byte[nbytes];
                Array.Copy(full, 0, buf, nbytes - full.Length, Math.Min(full.Length, nbytes));
                return new IPAddress(buf).ToString();
            }

            // ── low-level ──
            private static BigInteger ToBigInteger(IPAddress ip)
            {
                byte[] b = ip.GetAddressBytes();  // big-endian
                return new BigInteger(b, isUnsigned: true, isBigEndian: true);
            }

            private static BigInteger MaskFor(int prefix, int bits)
            {
                if (prefix <= 0)    return BigInteger.Zero;
                if (prefix >= bits) return (BigInteger.One << bits) - 1;
                var full     = (BigInteger.One << bits) - 1;
                var hostMask = (BigInteger.One << (bits - prefix)) - 1;
                return full ^ hostMask;
            }

            public bool Equals(Cidr other) =>
                IsV4 == other.IsV4 && Prefix == other.Prefix && Network == other.Network;
            public override bool Equals(object? obj) => obj is Cidr c && Equals(c);
            public override int GetHashCode() => HashCode.Combine(IsV4, Prefix, Network);
        }

        // ── Self-test (design §13 step 1; no test project in the repo) ─────────

        /// <summary>Runs the built-in correctness suite. Returns (passed, failed, failure messages).
        /// Wired to the hidden CLI <c>selftest</c> command.</summary>
        public static (int passed, int failed, List<string> failures) RunSelfTest()
        {
            int passed = 0, failed = 0;
            var failures = new List<string>();

            void ExpectEq(string label, string mode, string @base, string[] ranges, string expected)
            {
                var got = ComputeEffectiveAllowedIPs(@base, mode, ranges);
                if (got == expected) passed++;
                else { failed++; failures.Add($"{label}: expected [{expected}] got [{got}]"); }
            }

            void ExpectExcludeValid(string label, string @base, string[] excludes)
            {
                // Property check for large outputs: (1) no effective block overlaps any exclude;
                // (2) base − effective is fully covered by the excludes (nothing extra was dropped).
                var got     = ComputeEffectiveAllowedIPs(@base, "exclude", excludes);
                var effSet  = ParseList(got);
                var baseSet = ParseList(@base);
                var exSet   = ParseList(string.Join(",", excludes));

                bool overlapsExclude = effSet.Any(e => exSet.Any(x => e.Overlaps(x)));
                var  dropped         = Subtract(baseSet, effSet);                 // base − effective
                // dropped ⊆ ⋃excludes  ⇔  (base − effective) − excludes == ∅. Using set subtraction
                // (not per-block containment) so a dropped block spanning several excludes still passes.
                bool droppedAllExcluded = Subtract(dropped, exSet).Count == 0;

                if (!overlapsExclude && droppedAllExcluded) passed++;
                else
                {
                    failed++;
                    failures.Add($"{label}: overlapsExclude={overlapsExclude} " +
                                 $"droppedAllExcluded={droppedAllExcluded} got=[{got}]");
                }
            }

            // ── off ──
            ExpectEq("off-verbatim", "off", "0.0.0.0/0, ::/0", Array.Empty<string>(), "0.0.0.0/0, ::/0");

            // ── include ──
            ExpectEq("inc-single",   "include", "0.0.0.0/0, ::/0", new[] { "10.0.0.0/24" }, "10.0.0.0/24");
            ExpectEq("inc-sorted",   "include", "0.0.0.0/0, ::/0",
                     new[] { "192.168.1.0/24", "10.0.0.0/8" }, "10.0.0.0/8, 192.168.1.0/24");
            ExpectEq("inc-v6",       "include", "0.0.0.0/0, ::/0", new[] { "fd00::/8" }, "fd00::/8");
            ExpectEq("inc-mixed",    "include", "0.0.0.0/0, ::/0",
                     new[] { "10.0.0.0/8", "fd00::/8" }, "10.0.0.0/8, fd00::/8");
            ExpectEq("inc-bare-host","include", "0.0.0.0/0", new[] { "8.8.8.8" }, "8.8.8.8/32");
            // include clipped by a restrictive base
            ExpectEq("inc-clip",     "include", "10.0.0.0/8", new[] { "10.1.0.0/16", "9.9.9.9/32" }, "10.1.0.0/16");

            // ── exclude: exact known result (0/0 − 10/8) ──
            ExpectEq("exc-10/8", "exclude", "0.0.0.0/0", new[] { "10.0.0.0/8" },
                     "0.0.0.0/5, 8.0.0.0/7, 11.0.0.0/8, 12.0.0.0/6, 16.0.0.0/4, " +
                     "32.0.0.0/3, 64.0.0.0/2, 128.0.0.0/1");

            // ── exclude edge cases ──
            ExpectEq("exc-wrong-family", "exclude", "0.0.0.0/0", new[] { "fd00::/8" }, "0.0.0.0/0");
            ExpectEq("exc-not-in-base",  "exclude", "10.0.0.0/8", new[] { "192.168.0.0/16" }, "10.0.0.0/8");
            ExpectEq("exc-entire-base",  "exclude", "0.0.0.0/0", new[] { "0.0.0.0/0" }, "");
            // v6 must be preserved when only v4 is excluded
            ExpectEq("exc-keeps-v6", "exclude", "0.0.0.0/0, ::/0", new[] { "0.0.0.0/0" }, "::/0");

            // ── exclude: property-verified (large / compound outputs) ──
            ExpectExcludeValid("exc-host",       "0.0.0.0/0", new[] { "203.0.113.7/32" });
            ExpectExcludeValid("exc-two-blocks", "0.0.0.0/0", new[] { "10.0.0.0/8", "192.168.0.0/16" });
            ExpectExcludeValid("exc-adjacent",   "0.0.0.0/0", new[] { "10.0.0.0/8", "11.0.0.0/8" });
            ExpectExcludeValid("exc-nested",     "0.0.0.0/0", new[] { "10.0.0.0/8", "10.1.0.0/16" });
            ExpectExcludeValid("exc-v6-host",    "::/0",      new[] { "2001:db8::1/128" });
            ExpectExcludeValid("exc-dualstack",  "0.0.0.0/0, ::/0", new[] { "10.0.0.0/8", "fd00::/8" });

            return (passed, failed, failures);
        }
    }
}
