using System;
using System.Collections.Generic;

namespace MasselGUARD.Models
{
    /// <summary>
    /// A time-boxed manual choice, such as "use this DNS profile for 1 minute". Pure value type: it only
    /// knows an id and an absolute UTC end time, so a sleep or a clock change cannot stretch or shorten it
    /// by accident. WPF-free and CLI-shared (<see cref="RunSelfTest"/> is part of <c>MasselGUARDcli selftest</c>).
    /// </summary>
    public readonly record struct TempOverride(string? Id, DateTime EndUtc)
    {
        /// <summary>The durations offered in the menus, in seconds.</summary>
        public static readonly int[] PresetSeconds = { 10, 60, 300, 900 };

        public static TempOverride None => default;

        public static TempOverride Start(string id, TimeSpan duration, DateTime nowUtc)
            => new(id, nowUtc + duration);

        public bool IsSet => Id != null;

        /// <summary>True from the start until the end time has been reached.</summary>
        public bool IsActive(DateTime nowUtc) => Id != null && nowUtc < EndUtc;

        /// <summary>True once an override has run out (still recorded, end time passed).</summary>
        public bool HasExpired(DateTime nowUtc) => Id != null && nowUtc >= EndUtc;

        public TimeSpan Remaining(DateTime nowUtc)
            => Id == null || nowUtc >= EndUtc ? TimeSpan.Zero : EndUtc - nowUtc;

        /// <summary>"0:42" for the countdown (rounded up, so the last second still reads 0:01).</summary>
        public string FormatRemaining(DateTime nowUtc)
        {
            int s = (int)Math.Ceiling(Remaining(nowUtc).TotalSeconds);
            return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
        }

        /// <summary>Menu label key for a preset length: 10 s, 1 / 5 / 15 min.</summary>
        public static string LabelKey(int seconds) => seconds switch
        {
            10  => "DnsTemp10s",
            60  => "DnsTemp1m",
            300 => "DnsTemp5m",
            900 => "DnsTemp15m",
            _   => "DnsTemp1m",
        };

        // ── Self-test (run via `MasselGUARDcli selftest`) ─────────────────────
        public static (int passed, int failed, List<string> failures) RunSelfTest()
        {
            int pass = 0; var fails = new List<string>();
            void Check(string name, bool ok) { if (ok) pass++; else fails.Add(name); }

            var t0 = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

            Check("none-inactive",    !None.IsActive(t0) && !None.IsSet && !None.HasExpired(t0));
            Check("none-remaining-0", None.Remaining(t0) == TimeSpan.Zero);

            var o = Start("google", TimeSpan.FromSeconds(60), t0);
            Check("start-active",     o.IsActive(t0) && o.IsSet);
            Check("end-utc",          o.EndUtc == t0.AddSeconds(60));
            Check("active-before-end", o.IsActive(t0.AddSeconds(59.9)));
            Check("expired-at-end",   !o.IsActive(t0.AddSeconds(60)) && o.HasExpired(t0.AddSeconds(60)));
            Check("expired-after",    o.HasExpired(t0.AddMinutes(5)) && o.Remaining(t0.AddMinutes(5)) == TimeSpan.Zero);
            Check("not-expired-early", !o.HasExpired(t0.AddSeconds(30)));
            Check("remaining-30",     o.Remaining(t0.AddSeconds(30)) == TimeSpan.FromSeconds(30));

            Check("fmt-60",           o.FormatRemaining(t0) == "1:00");
            Check("fmt-42",           o.FormatRemaining(t0.AddSeconds(18)) == "0:42");
            Check("fmt-roundup",      o.FormatRemaining(t0.AddSeconds(59.2)) == "0:01");
            Check("fmt-end",          o.FormatRemaining(t0.AddSeconds(60)) == "0:00");
            Check("fmt-hours",        Start("x", TimeSpan.FromMinutes(90), t0).FormatRemaining(t0) == "1:30:00");

            var ten = Start("p", TimeSpan.FromSeconds(10), t0);
            Check("ten-seconds",      ten.IsActive(t0.AddSeconds(9)) && !ten.IsActive(t0.AddSeconds(10)));

            // Replacing an override restarts the clock for the new profile.
            var again = Start("other", TimeSpan.FromSeconds(300), t0.AddSeconds(30));
            Check("replace-restarts", again.Id == "other" && again.EndUtc == t0.AddSeconds(330));

            Check("presets",          PresetSeconds.Length == 4 && PresetSeconds[0] == 10 && PresetSeconds[3] == 900);
            Check("label-keys",       LabelKey(10) == "DnsTemp10s" && LabelKey(900) == "DnsTemp15m" && LabelKey(7) == "DnsTemp1m");

            return (pass, fails.Count, fails);
        }
    }
}
