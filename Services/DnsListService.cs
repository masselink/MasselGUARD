using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>Where the list in use came from.</summary>
    public enum DnsListSource { BuiltIn, Cache, Online }

    public sealed class DnsListResult
    {
        public DnsListData Data { get; init; } = new();
        public DnsListSource Source { get; init; }
        /// <summary>When the cached/online copy was fetched (UTC), null for the built-in snapshot.</summary>
        public DateTime? FetchedUtc { get; init; }
        /// <summary>Why a refresh did not produce a newer list (null = no problem or none attempted).</summary>
        public string? RefreshNote { get; init; }
    }

    /// <summary>
    /// Provides the public DNS server list (see <see cref="DnsServerList"/>): the built-in snapshot that ships with the app,
    /// a downloaded copy in <c>%APPDATA%\MasselGUARD\dnslist</c>, and the refresh from the main branch of the list repository
    /// on GitHub: <c>index.json</c> names the provider files, each downloaded from <c>servers/&lt;name&gt;.json</c>.
    /// The refresh is all or nothing (one failed or invalid file keeps the previous copy, so the list never mixes versions)
    /// and conditional (ETag per file: nothing is downloaded or rewritten when nothing changed). Nothing is applied
    /// automatically: the picker only shows entries. GUI-only (HttpClient + embedded resource).
    /// </summary>
    public static class DnsListService
    {
        private const string ResourceName = "dns-servers.builtin.json";
        private static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(24);

        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MasselGUARD");
        private static string CacheDir => Path.Combine(Dir, "dnslist");
        private static string MetaPath => Path.Combine(CacheDir, "meta.json");
        private static string IndexPath => Path.Combine(CacheDir, "index.json");
        private static string ProviderPath(string name) => Path.Combine(CacheDir, "servers", name + ".json");

        private sealed class Meta
        {
            /// <summary>ETag per file ("index.json", "servers/adguard.json").</summary>
            public Dictionary<string, string> ETags { get; set; } = new();
            public DateTime CheckedUtc { get; set; }
            public DateTime FetchedUtc { get; set; }
        }

        /// <summary>The list that ships inside the exe (always valid; the flat format, made by tools/make-builtin-dnslist.ps1).</summary>
        public static DnsListData LoadBuiltIn()
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (s == null) return new DnsListData();
            using var r = new StreamReader(s);
            return DnsServerList.Parse(r.ReadToEnd(), out _) ?? new DnsListData();
        }

        /// <summary>The list to show right now, without any network: the downloaded copy when it parses, else the built-in one.</summary>
        public static DnsListResult LoadLocal()
        {
            try
            {
                var cached = ReadCache();
                if (cached != null)
                    return new DnsListResult { Data = cached, Source = DnsListSource.Cache, FetchedUtc = ReadMeta()?.FetchedUtc };
            }
            catch { /* unreadable cache: use the built-in list */ }
            return new DnsListResult { Data = LoadBuiltIn(), Source = DnsListSource.BuiltIn };
        }

        private static DnsListData? ReadCache()
        {
            if (!File.Exists(IndexPath)) return null;
            var names = DnsServerList.ParseIndex(File.ReadAllText(IndexPath), out var version, out _);
            if (names == null) return null;
            return Assemble(names, version, n => File.Exists(ProviderPath(n)) ? File.ReadAllText(ProviderPath(n)) : null, out _);
        }

        /// <summary>Parses the provider files named by the index and joins them; null when a file is missing or invalid
        /// (the cache must be complete) or nothing is left.</summary>
        private static DnsListData? Assemble(List<string> names, string version, Func<string, string?> text, out string? problem)
        {
            problem = null;
            var parts = new List<List<DnsListServer>>();
            int skipped = 0;
            foreach (var n in names)
            {
                var t = text(n);
                int sk = 0;
                string? perr = null;
                var list = t == null ? null : DnsServerList.ParseProvider(t, out sk, out perr);
                if (list == null) { problem = $"{n}.json: {perr ?? "missing"}"; return null; }
                skipped += sk;
                parts.Add(list);
            }
            var data = DnsServerList.Combine(parts, skipped, version);
            if (data.Servers.Count == 0) { problem = "no valid servers"; return null; }
            return data;
        }

        /// <summary>True when the last check is more than a day ago (or never happened).</summary>
        public static bool RefreshDue() => ReadMeta() is not { } m || DateTime.UtcNow - m.CheckedUtc > RefreshEvery;

        private sealed record Fetched(bool NotModified, string? Text, string ETag);

        private static async Task<Fetched> FetchAsync(HttpClient http, string url, string? etag, int maxBytes, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(etag)) req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotModified) return new Fetched(true, null, etag ?? "");
            if (!resp.IsSuccessStatusCode) throw new InvalidDataException($"HTTP {(int)resp.StatusCode} for {url.Substring(url.LastIndexOf('/') + 1)}");
            if (resp.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("file too large");
            var data = await resp.Content.ReadAsByteArrayAsync(ct);
            if (data.Length > maxBytes) throw new InvalidDataException("file too large");
            return new Fetched(false, Encoding.UTF8.GetString(data), resp.Headers.ETag?.ToString() ?? "");
        }

        /// <summary>Downloads the index and the provider files that changed. Returns the new list, or the local one with a
        /// <see cref="DnsListResult.RefreshNote"/> when it could not be refreshed (offline, an invalid file, ...).</summary>
        public static async Task<DnsListResult> RefreshAsync(string? repoUrl, CancellationToken ct = default)
        {
            var local = LoadLocal();
            var baseUrl = DnsServerList.RawBase(string.IsNullOrWhiteSpace(repoUrl) ? AppConfig.DefaultDnsListRepoUrl : repoUrl);
            if (baseUrl == null) return Note(local, "The list repository setting is not a GitHub repository URL.");

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("MasselGUARD");
                bool haveCache = local.Source == DnsListSource.Cache;
                var meta = haveCache ? ReadMeta() ?? new Meta() : new Meta();
                string? Et(string key) => haveCache && meta.ETags.TryGetValue(key, out var e) ? e : null;

                // 1. the index
                var idx = await FetchAsync(http, baseUrl + "index.json", Et("index.json"), DnsServerList.MaxIndexBytes, ct);
                var idxText = idx.NotModified ? File.ReadAllText(IndexPath) : idx.Text!;
                var names = DnsServerList.ParseIndex(idxText, out var version, out var ierr);
                if (names == null) return Note(local, "The list index is not valid (" + ierr + ").");

                // 2. every provider file (4 at a time); a 304 reuses the cached file
                var texts = new Dictionary<string, string>();
                var etags = new Dictionary<string, string> { ["index.json"] = idx.ETag };
                bool anyChange = !idx.NotModified;
                using var gate = new SemaphoreSlim(4);
                var results = await Task.WhenAll(names.Select(async n =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        var key = "servers/" + n + ".json";
                        var cachedFile = File.Exists(ProviderPath(n)) ? ProviderPath(n) : null;
                        var f = await FetchAsync(http, baseUrl + key, cachedFile != null ? Et(key) : null, DnsServerList.MaxProviderBytes, ct);
                        return (n, key, f, cachedText: f.NotModified && cachedFile != null ? File.ReadAllText(cachedFile) : null);
                    }
                    finally { gate.Release(); }
                }));
                foreach (var (n, key, f, cachedText) in results)
                {
                    if (f.NotModified && cachedText == null) return Note(local, $"{n}.json could not be refreshed.");
                    texts[n] = f.NotModified ? cachedText! : f.Text!;
                    etags[key] = f.ETag;
                    if (!f.NotModified) anyChange = true;
                }
                if (haveCache && !anyChange && names.SequenceEqual(ReadCachedNames()))
                {
                    WriteMeta(new Meta { ETags = meta.ETags, CheckedUtc = DateTime.UtcNow, FetchedUtc = meta.FetchedUtc });
                    return local;   // nothing changed
                }

                // 3. all or nothing: every file must be valid before anything is stored
                var data = Assemble(names, version, n => texts.TryGetValue(n, out var t) ? t : null, out var problem);
                if (data == null) return Note(local, "The downloaded list is not valid (" + problem + ").");

                Store(idxText, texts, names);
                var now = DateTime.UtcNow;
                WriteMeta(new Meta { ETags = etags, CheckedUtc = now, FetchedUtc = now });
                return new DnsListResult { Data = data, Source = DnsListSource.Online, FetchedUtc = now };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return Note(local, "The list could not be refreshed: " + ex.Message);
            }
        }

        private static List<string> ReadCachedNames()
        {
            try { return DnsServerList.ParseIndex(File.ReadAllText(IndexPath), out _, out _) ?? new(); }
            catch { return new(); }
        }

        /// <summary>Writes the new cache next to the old one and swaps the folders, so a crash leaves a complete copy.</summary>
        private static void Store(string indexText, Dictionary<string, string> texts, List<string> names)
        {
            var fresh = CacheDir + ".new";
            if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
            Directory.CreateDirectory(Path.Combine(fresh, "servers"));
            File.WriteAllText(Path.Combine(fresh, "index.json"), indexText);
            foreach (var n in names) File.WriteAllText(Path.Combine(fresh, "servers", n + ".json"), texts[n]);
            if (Directory.Exists(CacheDir)) Directory.Delete(CacheDir, true);
            Directory.Move(fresh, CacheDir);
        }

        private static DnsListResult Note(DnsListResult l, string note) =>
            new() { Data = l.Data, Source = l.Source, FetchedUtc = l.FetchedUtc, RefreshNote = note };

        private static Meta? ReadMeta()
        {
            try { return File.Exists(MetaPath) ? JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaPath)) : null; }
            catch { return null; }
        }

        private static void WriteMeta(Meta m)
        {
            try { Directory.CreateDirectory(CacheDir); File.WriteAllText(MetaPath, JsonSerializer.Serialize(m)); }
            catch { /* the meta file only saves requests */ }
        }
    }
}
