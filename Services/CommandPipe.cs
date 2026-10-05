using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace MasselGUARD.Services
{
    /// <summary>
    /// A tiny local command channel between a NON-elevated process (the CLI started from the Windows
    /// right-click menu) and the running, elevated MasselGUARD window. It exists because the window needs
    /// administrator rights and a right-click verb must not raise a UAC prompt on every click.
    /// <para>Only one command family is accepted: the DNS bypass (<c>bypass [seconds|stop|toggle]</c>), which
    /// can do nothing more than switch to the user's own, already configured bypass profile for a short time.
    /// The pipe is open to the current user only (plus SYSTEM and Administrators).</para>
    /// WPF-free and CLI-shared; the self-test covers the command parsing.
    /// </summary>
    public static class CommandPipe
    {
        public const string PipeName = "MasselGUARD.Command";
        public const int MinSeconds = 5, MaxSeconds = 3600;

        /// <summary>A parsed bypass request. <see cref="Action"/> is "start", "stop", "toggle" or "invalid";
        /// <see cref="Seconds"/> is 0 for "use the last chosen length".</summary>
        public readonly record struct BypassRequest(string Action, int Seconds);

        /// <summary>Parses <c>bypass</c>, <c>bypass 60</c>, <c>bypass stop</c>, <c>bypass toggle</c>.</summary>
        public static BypassRequest ParseBypass(string? line)
        {
            var parts = (line ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !parts[0].Equals("bypass", StringComparison.OrdinalIgnoreCase))
                return new BypassRequest("invalid", 0);
            if (parts.Length == 1) return new BypassRequest("toggle", 0);
            if (parts.Length > 2) return new BypassRequest("invalid", 0);
            var arg = parts[1].ToLowerInvariant();
            if (arg == "stop")   return new BypassRequest("stop", 0);
            if (arg == "toggle") return new BypassRequest("toggle", 0);
            if (int.TryParse(arg, out int s))
                return new BypassRequest("start", Math.Clamp(s, MinSeconds, MaxSeconds));
            return new BypassRequest("invalid", 0);
        }

        /// <summary>Sends one command line to the running window. Returns its one-line reply
        /// ("ok: ..." or "error: ..."), or null when no MasselGUARD window is listening.</summary>
        public static string? Send(string line, int timeoutMs = 1500, string pipeName = PipeName)
        {
            try
            {
                using var c = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
                c.Connect(timeoutMs);
                // leaveOpen: disposing a StreamWriter flushes, and flushing after the reader closed the pipe throws.
                var enc = new System.Text.UTF8Encoding(false);
                var w = new StreamWriter(c, enc, 256, leaveOpen: true) { AutoFlush = true };
                w.WriteLine(line);
                var r = new StreamReader(c, enc, false, 256, leaveOpen: true);
                return r.ReadLine();
            }
            catch (Exception ex) { LastSendError = ex.GetType().Name + ": " + ex.Message; return null; }
        }

        /// <summary>Why the last <see cref="Send"/> returned null (for diagnostics).</summary>
        public static string LastSendError { get; private set; } = "";

        /// <summary>The listening side, owned by the running window. <paramref name="handler"/> gets the command
        /// line and returns the one-line reply; it is called on a pool thread (the caller marshals to the UI).</summary>
        public sealed class Server : IDisposable
        {
            private readonly Func<string, string> _handler;
            private readonly CancellationTokenSource _cts = new();
            private readonly string _pipeName;

            public Server(Func<string, string> handler, string pipeName = PipeName)
            { _handler = handler; _pipeName = pipeName; }

            public void Start() => Task.Run(Loop);

            private async Task Loop()
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        var security = new PipeSecurity();
                        var me = WindowsIdentity.GetCurrent().User;
                        if (me != null)
                            security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                        security.AddAccessRule(new PipeAccessRule(
                            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                        security.AddAccessRule(new PipeAccessRule(
                            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));

                        using var pipe = NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, 4,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                        await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);

                        var enc = new System.Text.UTF8Encoding(false);
                        var reader = new StreamReader(pipe, enc, false, 256, leaveOpen: true);
                        var line = await reader.ReadLineAsync().ConfigureAwait(false);
                        string reply;
                        try { reply = _handler(line ?? ""); }
                        catch (Exception ex) { reply = "error: " + ex.Message; }
                        var writer = new StreamWriter(pipe, enc, 256, leaveOpen: true) { AutoFlush = true };
                        await writer.WriteLineAsync(reply).ConfigureAwait(false);
                        try { pipe.WaitForPipeDrain(); } catch { }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { try { await Task.Delay(500, _cts.Token).ConfigureAwait(false); } catch { break; } }
                }
            }

            public void Dispose() => _cts.Cancel();
        }

        // ── Self-test (run via `MasselGUARDcli selftest`) ─────────────────────
        public static (int passed, int failed, List<string> failures) RunSelfTest()
        {
            int pass = 0; var fails = new List<string>();
            void Check(string name, bool ok) { if (ok) pass++; else fails.Add(name); }

            Check("plain-toggles",   ParseBypass("bypass") is { Action: "toggle", Seconds: 0 });
            Check("seconds",         ParseBypass("bypass 60") is { Action: "start", Seconds: 60 });
            Check("ten-seconds",     ParseBypass("bypass 10") is { Action: "start", Seconds: 10 });
            Check("stop",            ParseBypass("bypass stop") is { Action: "stop" });
            Check("toggle-word",     ParseBypass("bypass toggle") is { Action: "toggle" });
            Check("case-insensitive", ParseBypass("BYPASS Stop") is { Action: "stop" });
            Check("extra-spaces",    ParseBypass("  bypass   300  ") is { Action: "start", Seconds: 300 });
            Check("clamp-low",       ParseBypass("bypass 1") is { Action: "start", Seconds: MinSeconds });
            Check("clamp-high",      ParseBypass("bypass 999999") is { Action: "start", Seconds: MaxSeconds });
            Check("negative-clamped", ParseBypass("bypass -5") is { Action: "start", Seconds: MinSeconds });
            Check("garbage-arg",     ParseBypass("bypass banana").Action == "invalid");
            Check("too-many-args",   ParseBypass("bypass 10 20").Action == "invalid");
            Check("other-command",   ParseBypass("shutdown").Action == "invalid");
            Check("empty",           ParseBypass("").Action == "invalid" && ParseBypass(null).Action == "invalid");

            // Round trip over a real pipe (a private name, so a running MasselGUARD window is never touched):
            // proves the ACL-protected server starts, the same user can connect, and the reply comes back.
            try
            {
                var testPipe = "MasselGUARD.SelfTest." + Guid.NewGuid().ToString("N");
                string? seen = null;
                using var srv = new Server(l => { seen = l; return "ok: test"; }, testPipe);
                srv.Start();
                var reply = Send("bypass 30", 3000, testPipe);
                Check("pipe-roundtrip-reply", reply == "ok: test");
                if (reply != "ok: test") fails.Add($"  (reply was '{reply}', {LastSendError})");
                Check("pipe-roundtrip-request", seen == "bypass 30");
                Check("pipe-no-listener", Send("bypass", 300, testPipe + ".none") == null);
            }
            catch (Exception ex) { fails.Add("pipe-roundtrip threw: " + ex.Message); }
            return (pass, fails.Count, fails);
        }
    }
}
