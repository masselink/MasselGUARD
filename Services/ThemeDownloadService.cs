using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Downloads "shared" themes from a git repository as an HTTPS zip archive — no git
    /// install required — and installs the theme folders it contains into the install's
    /// <c>shared_themes\</c> folder. Same-named themes are overwritten (updated); other
    /// themes already in the folder are left untouched.
    /// </summary>
    public static class ThemeDownloadService
    {
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
