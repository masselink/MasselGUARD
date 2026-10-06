using System;
using System.Collections.Generic;
using System.Linq;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>
    /// What a DNS bypass request means when NO MasselGUARD window is running (docs/ServiceBackend-Design.md,
    /// step 3): the CLI reads the user's own config, works out the bypass profile and the interfaces, and asks
    /// the service to apply and hold it. Pure so the rules (and the error texts) are testable. The window,
    /// when it runs, still answers the request itself through <see cref="CommandPipe"/> (it owns the UI state).
    /// WPF-free and CLI-shared.
    /// </summary>
    public sealed record BypassPlan(bool Ok, string Error, DnsProfile? Profile, IReadOnlyList<Guid> Interfaces, string Families, int Seconds)
    {
        public const int DefaultSeconds = 60;

        public static BypassPlan Build(AppConfig cfg, IEnumerable<Guid> adapters, int seconds)
        {
            if (!cfg.EnableDns) return Fail("the DNS feature is switched off.");
            if (!cfg.DnsTempOverrideEnabled) return Fail("the timed DNS override is switched off in Settings > DNS.");
            var p = cfg.DnsProfiles.FirstOrDefault(x => x.Id == cfg.BypassDnsProfileId);
            if (p == null) return Fail("no bypass profile is marked (right-click a DNS profile > Mark as bypass profile).");
            var list = adapters.Where(g => g != Guid.Empty).Distinct().Take(DnsHoldKeeper.MaxGuids).ToList();
            if (list.Count == 0) return Fail("no connected network adapter to switch DNS on.");
            int secs = seconds > 0 ? Math.Clamp(seconds, CommandPipe.MinSeconds, CommandPipe.MaxSeconds) : DefaultSeconds;
            var fam = cfg.DnsAddressFamilies is "v4" or "v6" ? cfg.DnsAddressFamilies : "both";
            return new BypassPlan(true, "", p, list, fam, secs);
        }

        private static BypassPlan Fail(string why) => new(false, why, null, new List<Guid>(), "both", 0);

        public static (int pass, int fail, List<string> failures) RunSelfTest()
        {
            int pass = 0; var fails = new List<string>();
            void Check(bool ok, string what) { if (ok) pass++; else fails.Add(what); }
            var g1 = Guid.NewGuid(); var g2 = Guid.NewGuid();
            var prof = new DnsProfile { Id = "google", Name = "Google", V4Primary = "8.8.8.8" };
            AppConfig Cfg() => new() { EnableDns = true, DnsTempOverrideEnabled = true, BypassDnsProfileId = "google", DnsProfiles = new List<DnsProfile> { prof } };

            var ok = Build(Cfg(), new[] { g1, g2, g1 }, 300);
            Check(ok.Ok && ok.Profile == prof && ok.Interfaces.Count == 2 && ok.Seconds == 300, "plan: ok, duplicate interfaces collapsed");
            Check(Build(Cfg(), new[] { g1 }, 0).Seconds == BypassPlan.DefaultSeconds, "plan: no length = default");
            Check(Build(Cfg(), new[] { g1 }, 1).Seconds == CommandPipe.MinSeconds, "plan: length clamped up");
            Check(Build(Cfg(), new[] { g1 }, 999999).Seconds == CommandPipe.MaxSeconds, "plan: length clamped down");

            var c = Cfg(); c.EnableDns = false;
            Check(!Build(c, new[] { g1 }, 60).Ok && Build(c, new[] { g1 }, 60).Error.Contains("switched off"), "plan: DNS feature off");
            c = Cfg(); c.DnsTempOverrideEnabled = false;
            Check(!Build(c, new[] { g1 }, 60).Ok, "plan: timed override off");
            c = Cfg(); c.BypassDnsProfileId = "";
            Check(!Build(c, new[] { g1 }, 60).Ok && Build(c, new[] { g1 }, 60).Error.Contains("bypass profile"), "plan: no bypass profile marked");
            c = Cfg(); c.BypassDnsProfileId = "deleted";
            Check(!Build(c, new[] { g1 }, 60).Ok, "plan: marked profile no longer exists");
            Check(!Build(Cfg(), new Guid[0], 60).Ok && !Build(Cfg(), new[] { Guid.Empty }, 60).Ok, "plan: no usable adapter");
            c = Cfg(); c.DnsAddressFamilies = "v4";
            Check(Build(c, new[] { g1 }, 60).Families == "v4", "plan: v4 families kept");
            c = Cfg(); c.DnsAddressFamilies = "garbage";
            Check(Build(c, new[] { g1 }, 60).Families == "both", "plan: unknown families -> both");
            Check(Build(Cfg(), Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()), 60).Interfaces.Count == DnsHoldKeeper.MaxGuids, "plan: interface count capped");
            return (pass, fails.Count, fails);
        }
    }
}
