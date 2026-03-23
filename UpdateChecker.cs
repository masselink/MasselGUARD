using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MasselGUARD
{
    /// <summary>
    /// Checks GitHub Releases for a newer version and can auto-update by downloading
    /// MasselGUARD.zip, extracting it next to the running exe, and relaunching.
    ///
    /// GitHub API endpoint:
    ///   GET https://api.github.com/repos/masselink/MasselGUARD/releases/latest
    ///
    /// The release must contain an asset named MasselGUARD.zip.
    /// The tag name is used as the version string (e.g. "v2.0.1").
    /// </summary>
    public static class UpdateChecker
    {
        private const string ApiUrl     = "https://api.github.com/repos/masselink/MasselGUARD/releases/latest";
        private const string CurrentVersion = "2.0";   // keep in sync with AppTitle

        // ── Public: silent background check (called on startup) ──────────────
        public static async Task CheckAsync(AppConfig cfg, Action saveConfig,
                                             Dispatcher dispatcher)
        {
            try
            {
                var latest = await FetchLatestReleaseAsync();
                if (latest == null) return;

                cfg.LastUpdateCheck    = DateTime.UtcNow;
                cfg.LatestKnownVersion = latest.TagName;
                saveConfig();
            }
            catch { /* silent */ }
        }

        // ── Public: manual check triggered from Settings ──────────────────────
        public static async Task<ReleaseInfo?> CheckNowAsync(AppConfig cfg, Action saveConfig)
        {
            var latest = await FetchLatestReleaseAsync();
            cfg.LastUpdateCheck    = DateTime.UtcNow;
            cfg.LatestKnownVersion = latest?.TagName;
            saveConfig();
            return latest;
        }

        // ── Public: download + extract + relaunch ────────────────────────────
        public static async Task UpdateAsync(ReleaseInfo release,
            IProgress<string> progress, AppConfig cfg, Action saveConfig)
        {
            if (release.ZipUrl == null)
                throw new InvalidOperationException("No MasselGUARD.zip asset in release.");

            var currentExe = Environment.ProcessPath
                ?? AppContext.BaseDirectory;
            var currentDir = Path.GetDirectoryName(currentExe)!;
            var tempZip    = Path.Combine(Path.GetTempPath(),
                $"MasselGUARD_update_{release.TagName}.zip");
            var tempDir    = Path.Combine(Path.GetTempPath(),
                $"MasselGUARD_update_{release.TagName}");

            progress.Report(Lang.T("UpdateDownloading", release.TagName));

            // Download
            using (var http = MakeClient())
            using (var resp = await http.GetAsync(release.ZipUrl,
                       HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync();
                await using var file   = File.Create(tempZip);
                await stream.CopyToAsync(file);
            }

            progress.Report(Lang.T("UpdateExtracting"));

            // Extract to temp dir
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            ZipFile.ExtractToDirectory(tempZip, tempDir);
            File.Delete(tempZip);

            progress.Report(Lang.T("UpdateApplying"));

            // Find the exe inside the extracted zip (may be in a subfolder)
            var newExe = FindFile(tempDir, "MasselGUARD.exe");
            if (newExe == null)
                throw new FileNotFoundException("MasselGUARD.exe not found in update zip.");

            var extractedRoot = Path.GetDirectoryName(newExe)!;

            // Schedule: cmd waits for current process to exit, copies files, relaunches
            var batch = Path.Combine(Path.GetTempPath(), "wgclient_update.bat");
            await File.WriteAllTextAsync(batch,
                BuildUpdateBatch(extractedRoot, currentDir, currentExe));

            // Launch the batch detached, then exit
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "cmd.exe",
                Arguments       = $"/c \"{batch}\"",
                CreateNoWindow  = true,
                UseShellExecute = false
            });

            // Shutdown this instance — the batch will relaunch the new one
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ((App)System.Windows.Application.Current).ShutdownApp();
            });
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        public static bool IsNewerVersion(string? latestTag)
        {
            if (string.IsNullOrEmpty(latestTag)) return false;
            var latest  = ParseVersion(latestTag.TrimStart('v', 'V'));
            var current = ParseVersion(CurrentVersion);
            return latest > current;
        }

        private static Version ParseVersion(string s)
        {
            if (Version.TryParse(s, out var v)) return v;
            return new Version(0, 0);
        }

        public static async Task<ReleaseInfo?> FetchLatestReleaseAsync()
        {
            using var http = MakeClient();
            var json = await http.GetStringAsync(ApiUrl);
            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (tag == null) return null;

            string? zipUrl = null;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (string.Equals(name, "MasselGUARD.zip",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        zipUrl = asset.TryGetProperty("browser_download_url", out var u)
                            ? u.GetString() : null;
                        break;
                    }
                }

            return new ReleaseInfo(tag, zipUrl);
        }

        private static HttpClient MakeClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "MasselGUARD");
            client.Timeout = TimeSpan.FromSeconds(30);
            return client;
        }

        private static string? FindFile(string dir, string filename)
        {
            foreach (var f in Directory.EnumerateFiles(dir, filename,
                         SearchOption.AllDirectories))
                return f;
            return null;
        }

        private static string BuildUpdateBatch(string sourceDir, string destDir, string exePath)
        {
            // Waits for the process to exit (~3s), copies all files, relaunches
            return $@"@echo off
timeout /t 3 /nobreak >nul
robocopy ""{sourceDir}"" ""{destDir}"" /E /IS /IT /IM /NJH /NJS /NP >nul
if exist ""{sourceDir}\lang"" robocopy ""{sourceDir}\lang"" ""{destDir}\lang"" /E /IS /IT /IM /NJH /NJS /NP >nul
start """" ""{exePath}""
del ""%~f0""
";
        }
    }

    public record ReleaseInfo(string TagName, string? ZipUrl);
}
