using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>The measured response of one list server.</summary>
    public sealed class DnsSpeed
    {
        /// <summary>Fastest plain (UDP) answer in ms, null when no plain address answered or the server is DoH-only.</summary>
        public int? PlainMs { get; init; }
        /// <summary>DoH answer in ms (includes the TLS handshake, so it is slower than a plain answer by nature).</summary>
        public int? DohMs { get; init; }
        public int Answered { get; init; }
        public int Tried { get; init; }
        /// <summary>The first failure text (REFUSED, timeout, ...), for the tooltip.</summary>
        public string? Problem { get; init; }

        /// <summary>The figure to rank servers by: the plain answer when there is one, else DoH.</summary>
        public int? RankMs => PlainMs ?? DohMs;
        public bool Failed => Answered == 0;
    }

    /// <summary>
    /// Asks a DNS server to resolve a name and times the answer ("nslookup &lt;name&gt; &lt;server&gt;"), over plain DNS (UDP port 53)
    /// and over DoH, so the user can pick the fastest server of the list. The packet building and answer checking are PURE
    /// (selftested); the network calls are used by the DNS server picker only, on a click, with one query per address
    /// and a second one for the figure (the first may have to be resolved recursively by the server).
    /// </summary>
    public static class DnsProbe
    {
        /// <summary>The name that is resolved unless the user picks another one (<c>AppConfig.DnsTestName</c>).</summary>
        public const string DefaultTestName = "masselink.net";

        /// <summary>A plain host name: letters, digits, dots and hyphens, labels of 1-63 characters, at most 253 in all.</summary>
        public static bool ValidName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 253 || name.StartsWith('.') || name.StartsWith('-')) return false;
            foreach (var label in name.TrimEnd('.').Split('.'))
                if (label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-') || label.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
                    return false;
            return true;
        }

        // ── Packets (pure) ────────────────────────────────────────────────────

        public static byte[] BuildQuery(string name, ushort id)
        {
            var ms = new MemoryStream();
            void W16(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
            W16(id); W16(0x0100); W16(1); W16(0); W16(0); W16(0);
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                var b = Encoding.ASCII.GetBytes(label);
                ms.WriteByte((byte)b.Length); ms.Write(b, 0, b.Length);
            }
            ms.WriteByte(0); W16(1); W16(1);   // type A, class IN
            return ms.ToArray();
        }

        /// <summary>Checks id, response bit, rcode and the answer count; throws <see cref="InvalidDataException"/> with a short reason.</summary>
        public static void CheckAnswer(byte[] r, int len, ushort id)
        {
            if (len < 12) throw new InvalidDataException("short answer");
            if (((r[0] << 8) | r[1]) != id) throw new InvalidDataException("answer id mismatch");
            if ((r[2] & 0x80) == 0) throw new InvalidDataException("not a response");
            int rcode = r[3] & 0x0F, an = (r[6] << 8) | r[7];
            if (rcode != 0) throw new InvalidDataException(rcode == 3 ? "NXDOMAIN" : rcode == 5 ? "REFUSED" : "rcode " + rcode);
            if (an == 0) throw new InvalidDataException("no answer records");
        }

        // ── Network ───────────────────────────────────────────────────────────

        /// <summary>Time of one plain query in ms (the better of two). Throws on failure.</summary>
        public static async Task<int> UdpAsync(string server, string name, int timeoutMs, CancellationToken ct)
        {
            var ip = IPAddress.Parse(server);
            int best = int.MaxValue;
            for (int i = 0; i < 2; i++)
            {
                using var udp = new Socket(ip.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                ushort id = (ushort)Random.Shared.Next(0, 65536);
                var q = BuildQuery(name, id);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                var sw = Stopwatch.StartNew();
                try
                {
                    await udp.ConnectAsync(new IPEndPoint(ip, 53), cts.Token);
                    await udp.SendAsync(q, cts.Token);
                    var buf = new byte[1500];
                    int n = await udp.ReceiveAsync(buf, cts.Token);
                    sw.Stop();
                    CheckAnswer(buf, n, id);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("timeout"); }
                best = Math.Min(best, (int)sw.ElapsedMilliseconds);
            }
            return best;
        }

        /// <summary>Time of one DoH query in ms (the better of two, the second reuses the connection). Throws on failure.</summary>
        public static async Task<int> DohAsync(HttpClient http, string url, string name, int timeoutMs, CancellationToken ct)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("not an https URL");
            int best = int.MaxValue;
            for (int i = 0; i < 2; i++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, uri)
                {
                    Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                    Content = new ByteArrayContent(BuildQuery(name, 0)),
                };
                req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                var sw = Stopwatch.StartNew();
                try
                {
                    using var resp = await http.SendAsync(req, cts.Token);
                    if (!resp.IsSuccessStatusCode) throw new InvalidDataException("HTTP " + (int)resp.StatusCode);
                    var body = await resp.Content.ReadAsByteArrayAsync(cts.Token);
                    sw.Stop();
                    CheckAnswer(body, body.Length, 0);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("timeout"); }
                best = Math.Min(best, (int)sw.ElapsedMilliseconds);
            }
            return best;
        }

        /// <summary>Tests every address of a list entry (plain) and its DoH template (not when it needs a personal id,
        /// or when plain DNS works and <paramref name="withDoh"/> is false).</summary>
        public static async Task<DnsSpeed> TestAsync(DnsListServer s, HttpClient http, string name, int timeoutMs, CancellationToken ct, bool withDoh = true)
        {
            int tried = 0, answered = 0;
            int? plain = null, doh = null;
            string? problem = null;

            if (!s.EncryptedOnly)
                foreach (var ip in s.V4.Concat(s.V6))
                {
                    tried++;
                    try { var ms = await UdpAsync(ip, name, timeoutMs, ct); answered++; plain = plain == null ? ms : Math.Min(plain.Value, ms); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { problem ??= ip + ": " + Short(ex); }
                }

            if (s.Doh.Length > 0 && s.Parameters.Count == 0 && (withDoh || s.EncryptedOnly))
            {
                tried++;
                try { doh = await DohAsync(http, s.Doh, name, timeoutMs, ct); answered++; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { problem ??= "DoH: " + Short(ex); }
            }
            return new DnsSpeed { PlainMs = plain, DohMs = doh, Answered = answered, Tried = tried, Problem = problem };
        }

        private static string Short(Exception ex) => ex is TimeoutException or TaskCanceledException ? "timeout"
            : ex is InvalidDataException ? ex.Message : ex.GetBaseException().Message;

        // ── Self-test ─────────────────────────────────────────────────────────

        public static (int pass, int fail, List<string> failures) RunSelfTest()
        {
            int pass = 0; var fails = new List<string>();
            void Check(bool ok, string what) { if (ok) pass++; else fails.Add(what); }
            bool Throws(byte[] r, int len, ushort id, string contains)
            {
                try { CheckAnswer(r, len, id); return false; }
                catch (InvalidDataException ex) { return ex.Message.Contains(contains); }
            }
            byte[] Resp(ushort id, int rcode, int answers) =>
                new byte[] { (byte)(id >> 8), (byte)id, 0x81, (byte)(0x80 | rcode), 0, 1, (byte)(answers >> 8), (byte)answers, 0, 0, 0, 0 };

            var q = BuildQuery("masselink.net", 0x1234);
            // header: id, RD flag, one question
            Check(q[0] == 0x12 && q[1] == 0x34 && q[2] == 0x01 && q[3] == 0x00 && q[4] == 0 && q[5] == 1, "query: header");
            // 9 'masselink' 3 'net' 0, type A, class IN
            Check(q[12] == 9 && Encoding.ASCII.GetString(q, 13, 9) == "masselink" && q[22] == 3 && Encoding.ASCII.GetString(q, 23, 3) == "net" && q[26] == 0, "query: name labels");
            Check(q.Length == 12 + 1 + 9 + 1 + 3 + 1 + 4 && q[^4] == 0 && q[^3] == 1 && q[^2] == 0 && q[^1] == 1, "query: type A class IN");
            Check(BuildQuery("example.com.", 1).Length == BuildQuery("example.com", 1).Length, "query: trailing dot ignored");

            CheckAnswer(Resp(7, 0, 2), 12, 7); pass++;                      // good answer does not throw
            Check(Throws(Resp(7, 0, 2), 12, 8, "id mismatch"), "answer: wrong id refused");
            Check(Throws(Resp(7, 5, 0), 12, 7, "REFUSED"), "answer: REFUSED");
            Check(Throws(Resp(7, 3, 0), 12, 7, "NXDOMAIN"), "answer: NXDOMAIN");
            Check(Throws(Resp(7, 2, 0), 12, 7, "rcode 2"), "answer: other rcode");
            Check(Throws(Resp(7, 0, 0), 12, 7, "no answer"), "answer: no records");
            Check(Throws(new byte[5], 5, 0, "short"), "answer: short packet");
            var notResp = Resp(7, 0, 1); notResp[2] = 0x01;
            Check(Throws(notResp, 12, 7, "not a response"), "answer: a query is not an answer");

            Check(ValidName("masselink.net") && ValidName("example.com.") && ValidName("a-b.example.org") && ValidName("localhost"), "name: plain host names");
            Check(!ValidName("") && !ValidName("  ") && !ValidName(null) && !ValidName("a b.com") && !ValidName("bad_name.com") && !ValidName("a..b") && !ValidName("-x.com") && !ValidName("x-.com")
                  && !ValidName(".com") && !ValidName("a.com/b") && !ValidName("a.com&calc") && !ValidName(new string('a', 64) + ".com") && !ValidName(string.Join(".", Enumerable.Repeat("abcdefgh", 32))), "name: invalid names refused");

            // ranking
            var fast = new DnsSpeed { PlainMs = 12, DohMs = 90, Answered = 3, Tried = 3 };
            var dohOnly = new DnsSpeed { DohMs = 80, Answered = 1, Tried = 1 };
            var dead = new DnsSpeed { Answered = 0, Tried = 2, Problem = "x" };
            Check(fast.RankMs == 12 && dohOnly.RankMs == 80 && dead.RankMs == null && dead.Failed && !fast.Failed, "speed: rank prefers the plain answer");
            return (pass, fails.Count, fails);
        }
    }
}
