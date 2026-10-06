using System;
using System.Collections.Generic;
using System.Linq;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Phase 2 of the service back-end (docs/ServiceBackend-Design.md): a timed DNS override ("use Google for
    /// 5 minutes") that the SERVICE enforces, so it ends on time and is not undone when the UI window closes.
    /// The UI still shows the countdown and still ends the override itself while it runs; the keeper is the
    /// backstop (it restores the held interfaces a few seconds after the end time unless the UI cancelled it
    /// first) and the memory that lets a restarted UI pick the override up again.
    /// Pure: time and the restore action are passed in. WPF-free and CLI-shared.
    /// </summary>
    public sealed class DnsHoldKeeper
    {
        /// <summary>Grace after the end time, so the UI's own end-of-override (which re-applies automation)
        /// normally wins and the backstop only acts when the UI is gone.</summary>
        public static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);
        public const int MaxSeconds = 3600, MaxGuids = 16;

        private readonly object _lock = new();
        private string? _profileId;
        private List<Guid> _guids = new();
        private DateTime _endUtc;

        public readonly record struct Status(bool Active, string ProfileId, int RemainingSeconds);

        public void Register(string profileId, IEnumerable<Guid> guids, int seconds, DateTime nowUtc)
        {
            lock (_lock)
            {
                _profileId = profileId;
                _guids = guids.Distinct().Take(MaxGuids).ToList();
                _endUtc = nowUtc.AddSeconds(Math.Clamp(seconds, 1, MaxSeconds));
            }
        }

        public void Cancel() { lock (_lock) { _profileId = null; _guids = new(); } }

        public Status Get(DateTime nowUtc)
        {
            lock (_lock)
            {
                if (_profileId == null || nowUtc >= _endUtc) return new Status(false, "", 0);
                return new Status(true, _profileId, (int)Math.Ceiling((_endUtc - nowUtc).TotalSeconds));
            }
        }

        /// <summary>The interfaces under a hold that has not run out yet (they must not be restored).</summary>
        public IReadOnlyList<Guid> Held(DateTime nowUtc)
        {
            lock (_lock) return _profileId != null && nowUtc < _endUtc + Grace ? _guids.ToList() : new List<Guid>();
        }

        /// <summary>Call about once a second. Restores the held interfaces once end + grace has passed
        /// and clears the hold. Returns true when it did.</summary>
        public bool Tick(DateTime nowUtc, Action<Guid> restore)
        {
            List<Guid> due;
            lock (_lock)
            {
                if (_profileId == null || nowUtc < _endUtc + Grace) return false;
                due = _guids; _profileId = null; _guids = new();
            }
            foreach (var g in due) { try { restore(g); } catch { } }
            return true;
        }

        public static (int pass, int fail, List<string> failures) RunSelfTest()
        {
            int pass = 0; var fails = new List<string>();
            void Check(bool ok, string what) { if (ok) pass++; else fails.Add(what); }
            var t0 = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
            var g1 = Guid.NewGuid(); var g2 = Guid.NewGuid();

            var k = new DnsHoldKeeper();
            Check(!k.Get(t0).Active && k.Held(t0).Count == 0, "keeper: empty at start");

            k.Register("google", new[] { g1, g2, g1 }, 60, t0);
            var s = k.Get(t0.AddSeconds(10));
            Check(s.Active && s.ProfileId == "google" && s.RemainingSeconds == 50, "keeper: status and remaining seconds");
            Check(k.Held(t0.AddSeconds(10)).Count == 2, "keeper: duplicate guids collapsed");
            Check(k.Get(t0.AddSeconds(60)).Active == false, "keeper: inactive at end time");
            Check(k.Held(t0.AddSeconds(61)).Count == 2, "keeper: still held during the grace");
            Check(k.Held(t0.AddSeconds(64)).Count == 0, "keeper: nothing held after the grace");

            var restored = new List<Guid>();
            Check(!k.Tick(t0.AddSeconds(62), restored.Add) && restored.Count == 0, "keeper: tick before end+grace does nothing");
            Check(k.Tick(t0.AddSeconds(63), restored.Add) && restored.Count == 2, "keeper: tick restores the held interfaces");
            Check(!k.Tick(t0.AddSeconds(70), restored.Add) && restored.Count == 2, "keeper: restores only once");

            k.Register("x", new[] { g1 }, 30, t0);
            k.Cancel();
            Check(!k.Get(t0).Active && !k.Tick(t0.AddHours(1), restored.Add), "keeper: cancel drops the hold without restoring");

            k.Register("x", new[] { g1 }, 999999, t0);
            Check(k.Get(t0).RemainingSeconds == MaxSeconds, "keeper: length clamped to the maximum");
            k.Register("x", Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()), 10, t0);
            Check(k.Held(t0).Count == MaxGuids, "keeper: interface count capped");

            k.Register("x", new[] { g1 }, 30, t0);
            k.Register("y", new[] { g2 }, 30, t0);
            Check(k.Get(t0).ProfileId == "y" && k.Held(t0).Single() == g2, "keeper: a new hold replaces the old one");
            return (pass, fails.Count, fails);
        }
    }
}
