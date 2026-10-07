using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MasselGUARD.Models;

namespace MasselGUARD.Services
{
    /// <summary>Where the list in use came from.</summary>
    public enum DnsListSource
    {
        /// <summary>Nothing downloaded yet and nothing saved.</summary>
        None,
        /// <summary>Only the entries the user added (the list could not be loaded).</summary>
        Saved,
        /// <summary>The downloaded copy from an earlier refresh.</summary>
        Cache,
        /// <summary>Just downloaded.</summary>
        Online,
    }

    public sealed class DnsListResult
    {
        public DnsListData Data { get; init; } = new();
        public DnsListSource Source { get; init; }
        /// <summary>When the cached/online copy was fetched (UTC), null when nothing was downloaded.</summary>
        public DateTime? FetchedUtc { get; init; }
        /// <summary>Why a refresh did not produce a newer list (null = no problem or none attempted).</summary>
        public string? RefreshNote { get; init; }
    }

    /// <summary>
    /// Provides the public DNS server list (see <see cref="DnsServerList"/>) from the list repository on GitHub:
    /// <c>index.json</c> names the provider files, each downloaded from <c>servers/&lt;name&gt;.json</c> on the main branch,
    /// and a copy is kept in <c>%APPDATA%\MasselGUARD\dnslist</c>. The app does not ship a copy of the list.
    /// The refresh is all or nothing (a missing or corrupt file keeps the previous copy, so the list never mixes versions; a file
    /// of a schemaVersion this app does not know is skipped) and conditional (ETag per file: nothing is downloaded or rewritten when
    /// nothing changed). The entries the user added are also saved locally (<c>dns-selected.json</c>), so they stay visible, and can
    /// be unticked again, when the list cannot be downloaded or an entry leaves the online list. Nothing is applied automatically.
    /// GUI-only (HttpClient).
    /// </summary>
    public static class DnsListService
    {
        private static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(24);

        private static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MasselGUARD");
        private static string CacheDir => Path.Combine(Dir, "dnslist");
        private static string MetaPath => Path.Combine(CacheDir, "meta.json");
        private static string IndexPath => Path.Combine(CacheDir, "index.json");
        private static string ProviderPath(string name) => Path.Combine(CacheDir, "servers", name + ".json");
        /// <summary>The user's own copy of the entries they added (outside the cache folder, which a refresh replaces).</summary>
        private static string SavedPath => Path.Combine(Dir, "dns-selected.json");

        private sealed class Meta
        {
            /// <summary>ETag per file ("index.json", "servers/adguard.json").</summary>
            public Dictionary<string, string> ETags { get; set; } = new();
            public DateTime CheckedUtc { get; set; }
            public DateTime FetchedUtc { get; set; }
        }

        // ── The user's saved entries ──────────────────────────────────────────

        public static List<DnsListServer> LoadSaved()
        {
            try
            {
                if (File.Exists(SavedPath) && new FileInfo(SavedPath).Length <= DnsServerList.MaxBytes)
                    return DnsServerList.Parse(File.ReadAllText(SavedPath), out _)?.Servers ?? new List<DnsListServer>();
            }
            catch { /* unreadable: start empty */ }
            return new List<DnsListServer>();
        }

        private static void WriteSaved(List<DnsListServer> servers)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var tmp = SavedPath + ".tmp";
                File.WriteAllText(tmp, DnsServerList.ToFlatJson(servers, DateTime.UtcNow.ToString("yyyy-MM-dd")));
                File.Move(tmp, SavedPath, overwrite: true);
            }
            catch { /* a copy only helps offline */ }
        }

        /// <summary>Remembers the entries the user just added and forgets the ones they removed.</summary>
        public static void SaveSelected(IEnumerable<DnsListServer> added, IEnumerable<string> removedIds)
        {
            var merged = DnsServerList.MergeSaved(LoadSaved(), added, removedIds);
            WriteSaved(merged);
        }

        /// <summary>Drops saved entries that are no longer a profile of the user (removed in Settings or by hand).</summary>
        public static void PruneSaved(IEnumerable<DnsProfile> profiles)
        {
            var saved = LoadSaved();
            var keep = saved.Where(s => DnsServerList.IsAdded(s, profiles)).ToList();
            if (keep.Count != saved.Count) WriteSaved(keep);
        }

        // ── Reading what is stored ────────────────────────────────────────────

        /// <summary>The list to show right now, without any network: the downloaded copy plus the user's saved entries.</summary>
        public static DnsListResult LoadLocal()
        {
            DnsListData? cached = null;
            try { cached = ReadCache(); } catch { /* unreadable cache: treat as none */ }
            var saved = LoadSaved();
            var data = DnsServerList.WithSaved(cached, saved);
            var source = cached != null ? DnsListSource.Cache : saved.Count > 0 ? DnsListSource.Saved : DnsListSource.None;
            return new DnsListResult { Data = data, Source = source, FetchedUtc = cached != null ? ReadMeta()?.FetchedUtc : null };
        }

        private static DnsListData? ReadCache()
        {
            if (!File.Exists(IndexPath)) return null;
            var names = DnsServerList.ParseIndex(File.ReadAllText(IndexPath), out var version, out _);
            if (names == null) return null;
            return DnsServerList.Assemble(names, version, n => File.Exists(ProviderPath(n)) ? File.ReadAllText(ProviderPath(n)) : null, out _);
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
                var data = DnsServerList.Assemble(names, version, n => texts.TryGetValue(n, out var t) ? t : null, out var problem);
                if (data == null) return Note(local, "The downloaded list is not valid (" + problem + ").");

                Store(idxText, texts, names);
                var now = DateTime.UtcNow;
                WriteMeta(new Meta { ETags = etags, CheckedUtc = now, FetchedUtc = now });
                return new DnsListResult { Data = DnsServerList.WithSaved(data, LoadSaved()), Source = DnsListSource.Online, FetchedUtc = now };
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
