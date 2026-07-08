using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MasselGUARD.Services
{
    /// <summary>One theme entry from the repo's <c>index.json</c> manifest.</summary>
    public sealed class ThemeManifestEntry
    {
        public string Id          { get; set; } = "";
        public string Name        { get; set; } = "";
        public string Description { get; set; } = "";
        public string Author      { get; set; } = "";
        public List<string> Tags  { get; set; } = new();
        public string Path        { get; set; } = "";   // theme folder relative to repo root
        public List<string> Files { get; set; } = new(); // files relative to Path
        public string PreviewDark  { get; set; } = "";   // repo-root-relative image path
        public string PreviewLight { get; set; } = "";
    }

    /// <summary>Parsed manifest plus the raw base URL its files resolve against.</summary>
    public sealed class ThemeManifest
    {
        public List<ThemeManifestEntry> Themes { get; set; } = new();
        public string RawBase { get; set; } = "";   // e.g. https://raw.githubusercontent.com/owner/repo/main/
    }

    /// <summary>
    /// Fetches "shared" themes from a git repository. The preferred path reads the repo's
    /// <c>index.json</c> manifest and downloads each theme's files individually over raw
    /// HTTPS (no zip) — used by the Theme Browser. A legacy whole-repo zip path remains for
    /// arbitrary URLs the user types. Installs land in <c>%APPDATA%\MasselGUARD\shared-themes\</c>;
    /// same-named themes are overwritten, others are left untouched.
    /// </summary>
    public static class ThemeDownloadService
    {
        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        private sealed class ManifestFile { public List<ThemeManifestEntry> Themes { get; set; } = new(); }

        private static HttpClient NewClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MasselGUARD");
            return http;
        }

        /// <summary>Raw base URL for a GitHub repo + branch, e.g.
        /// https://github.com/owner/repo → https://raw.githubusercontent.com/owner/repo/{branch}/.</summary>
        private static string RawBase(string repoUrl, string branch)
        {
            var u = (repoUrl ?? "").Trim().TrimEnd('/');
            if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) u = u[..^4];
            var m = Regex.Match(u, @"^https?://github\.com/([^/]+)/([^/]+)$", RegexOptions.IgnoreCase);
            if (!m.Success)
                throw new Exception("Theme browsing needs a GitHub repository URL like https://github.com/owner/repo.");
            return $"https://raw.githubusercontent.com/{m.Groups[1].Value}/{m.Groups[2].Value}/{branch}/";
        }

        /// <summary>Downloads and parses the repo's index.json (tries the main, then master branch).</summary>
        public static async Task<ThemeManifest> FetchManifestAsync(string repoUrl)
        {
            using var http = NewClient();
            Exception? last = null;
            foreach (var branch in new[] { "main", "master" })
            {
                try
                {
                    var rawBase = RawBase(repoUrl, branch);
                    var json    = await http.GetStringAsync(rawBase + "index.json");
                    var parsed  = JsonSerializer.Deserialize<ManifestFile>(json, JsonOpts);
                    return new ThemeManifest { Themes = parsed?.Themes ?? new(), RawBase = rawBase };
                }
                catch (Exception ex) { last = ex; }
            }
            throw new Exception($"Could not load the theme list (index.json). {last?.Message}");
        }

        /// <summary>Fetches a repo-root-relative file (e.g. a preview image) as bytes; null on failure.</summary>
        public static async Task<byte[]?> FetchRawAsync(string rawBase, string repoRelativePath)
        {
            if (string.IsNullOrWhiteSpace(repoRelativePath)) return null;
            try
            {
                using var http = NewClient();
                return await http.GetByteArrayAsync(rawBase + repoRelativePath.TrimStart('/').Replace('\\', '/'));
            }
            catch { return null; }
        }

        /// <summary>
        /// Installs a single manifest theme into <paramref name="targetDir"/> by downloading
        /// each listed file over raw HTTPS. Path components are validated so a malicious
        /// manifest cannot escape <c>shared-themes/&lt;id&gt;/</c>. Returns the file count.
        /// </summary>
        public static async Task<int> InstallThemeAsync(string rawBase, ThemeManifestEntry entry, string targetDir)
        {
            var id = SafeName(entry.Id) ?? throw new Exception("The theme has an invalid id.");
            var dest     = System.IO.Path.Combine(targetDir, id);
            var destRoot = System.IO.Path.GetFullPath(dest) + System.IO.Path.DirectorySeparatorChar;
            Directory.CreateDirectory(dest);

            using var http = NewClient();
            int n = 0;
            foreach (var file in entry.Files)
            {
                var rel = SafeRelative(file);
                if (rel == null) continue;

                var url = $"{rawBase}{entry.Path.Trim('/').Replace('\\', '/')}/{rel}";
                byte[] data;
                try { data = await http.GetByteArrayAsync(url); }
                catch { continue; }

                var outPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(dest, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                if (!outPath.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase)) continue;

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath)!);
                await File.WriteAllBytesAsync(outPath, data);
                n++;
            }
            if (n == 0) throw new Exception("Nothing was installed (the theme listed no valid files).");
            return n;
        }

        /// <summary>A safe single folder name (no separators, no traversal); null if unsafe.</summary>
        private static string? SafeName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (s is "." or "..") return null;
            if (s.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) return null;
            return s;
        }

        /// <summary>A safe relative path (forward slashes, no rooted/UNC, no "." or ".." segment); null if unsafe.</summary>
        private static string? SafeRelative(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Replace('\\', '/').TrimStart('/');
            if (System.IO.Path.IsPathRooted(s)) return null;
            foreach (var seg in s.Split('/'))
                if (seg is "" or "." or "..") return null;
            return s;
        }


        /// <summary>Outcome of a download: the folder names that were installed/updated.</summary>
        public sealed class Result
        {
            public List<string> Names { get; } = new();
            public int Count => Names.Count;
        }

        /// <summary>
        /// Fetches <paramref name="repoUrl"/> as a zip archive and installs every folder
        /// that contains a <c>theme.json</c> into <paramref name="targetDir"/>.
        /// </summary>
        public static async Task<Result> DownloadAsync(string repoUrl, string targetDir)
        {
            if (string.IsNullOrWhiteSpace(repoUrl))
                throw new ArgumentException("No theme repository URL is configured.");

            var data = await FetchArchiveAsync(repoUrl.Trim());

            var tmp = Path.Combine(Path.GetTempPath(), "MasselGUARD_themes_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            var result = new Result();
            try
            {
                var zipPath = Path.Combine(tmp, "archive.zip");
                await File.WriteAllBytesAsync(zipPath, data);

                var extract = Path.Combine(tmp, "x");
                ZipFile.ExtractToDirectory(zipPath, extract);

                Directory.CreateDirectory(targetDir);

                // Any directory that directly holds a theme.json is a theme folder —
                // wherever it sits in the archive (repos usually nest under repo-branch/).
                foreach (var json in Directory.EnumerateFiles(extract, "theme.json", SearchOption.AllDirectories))
                {
                    var src  = Path.GetDirectoryName(json)!;
                    var name = Path.GetFileName(src);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    CopyDir(src, Path.Combine(targetDir, name));   // overwrite same-named, keep the rest
                    if (!result.Names.Contains(name)) result.Names.Add(name);
                }
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { /* temp cleanup is best-effort */ }
            }

            if (result.Count == 0)
                throw new Exception("The archive contained no themes (no folder with a theme.json was found).");

            return result;
        }

        private static async Task<byte[]> FetchArchiveAsync(string url)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MasselGUARD");

            Exception? last = null;
            foreach (var candidate in ArchiveUrls(url))
            {
                try { return await http.GetByteArrayAsync(candidate); }
                catch (Exception ex) { last = ex; }
            }
            throw new Exception($"Could not download the theme archive. {last?.Message}");
        }

        /// <summary>
        /// Candidate zip URLs for a repo URL: a direct <c>.zip</c> is used as-is; a GitHub/
        /// GitLab-style repo URL becomes the default-branch archive (tries main, then master).
        /// </summary>
        private static IEnumerable<string> ArchiveUrls(string url)
        {
            if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                yield return url;
                yield break;
            }

            var u = url.TrimEnd('/');
            if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) u = u[..^4];

            yield return $"{u}/archive/refs/heads/main.zip";
            yield return $"{u}/archive/refs/heads/master.zip";
        }

        private static void CopyDir(string src, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
            foreach (var d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
        }
    }
}
