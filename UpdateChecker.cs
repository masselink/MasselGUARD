using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using MasselGUARD.Models;

namespace MasselGUARD
{
    /// <summary>
    /// Checks GitHub Releases for a newer version and can auto-update by downloading
    /// MasselGUARD.zip, extracting it next to the running exe, and relaunching.
    ///
    /// GitHub API endpoint:
    ///   GET https://api.github.com/repos/masselink/MasselGUARD/releases/latest
    ///
    /// The release must contain an architecture-specific asset — "MasselGUARD-x64.zip"
    /// or "MasselGUARD-arm64.zip" — matching the running process. Older releases that
    /// ship a single "MasselGUARD.zip" are still accepted as a fallback (x64).
    /// The tag name is used as the version string (e.g. "v2.0.1").
    /// </summary>
    public static class UpdateChecker
    {
        /// <summary>
        /// Architecture moniker for the running process ("x64" / "arm64" / "x86").
        /// Used to pick the matching release asset and shown on the About/version pages.
        /// </summary>
        public static string ArchMoniker => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64   => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86   => "x86",
            var other          => other.ToString().ToLowerInvariant(),
        };

        /// <summary>
        /// Candidate release-asset names for this process, most-specific first:
        /// the arch-specific zip, then the legacy single-arch "MasselGUARD.zip" (x64).
        /// </summary>
        private static string[] AssetCandidates()
        {
            var arch = ArchMoniker;
            // Legacy MasselGUARD.zip predates multi-arch and only ever contained x64.
            return arch == "x64"
                ? new[] { $"MasselGUARD-{arch}.zip", "MasselGUARD.zip" }
                : new[] { $"MasselGUARD-{arch}.zip" };
        }
        private const string TagsApiUrl     = "https://api.github.com/repos/masselink/MasselGUARD/tags";
        private const string ReleasesApiUrl = "https://api.github.com/repos/masselink/MasselGUARD/releases";
        // Major.Minor.Patch only — static, never modified by build.
        // The build timestamp is injected at compile time via -p:InformationalVersion
        // and read at runtime from the assembly attribute (see BuildStamp below).
        private const string CurrentVersion = "3.8.0";

        // Release codenames — one entry per public version, keyed by Major.Minor.Patch.
        // Update both here AND in BUILD.bat (set CODENAME=...) when bumping the version.
        private static readonly System.Collections.Generic.Dictionary<string, string> _codenames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                { "3.3.0", "Camouflaged Koala" },
                { "3.5.0", "Hypersonic Quokka"  },
                { "3.6.0", "Dangerous Donkey"   },
                { "3.7.0", "Chromatic Chameleon" },
                { "3.7.1", "Chromatic Chameleon" },
                { "3.8.0", "Protective Pangolin" },
            };

        // ── Public: silent background check (called on startup) ──────────────
        public static async Task CheckAsync(AppConfig cfg, Action saveConfig)
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
            IProgress<string> progress, AppConfig cfg, Action saveConfig,
            Action? onShutdown = null)
        {
            if (release.ZipUrl == null)
                throw new InvalidOperationException(
                    $"No MasselGUARD-{ArchMoniker}.zip asset in release {release.TagName}.");

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

            // Shutdown this instance — the batch will relaunch the new one.
            // GUI callers supply onShutdown to run the WPF dispatcher shutdown.
            onShutdown?.Invoke();
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        // Returns true when the latest published tag is newer than the running build.
        public static bool IsNewerVersion(string? latestTag)
        {
            if (string.IsNullOrEmpty(latestTag)) return false;
            var latest  = ParseVersion(latestTag.TrimStart('v', 'V'));
            var current = ParseVersion(CurrentVersion);
            return latest > current;
        }

        // Returns true when the running build is AHEAD of the latest published tag.
        public static bool IsAheadOfLatest(string? latestTag)
        {
            if (string.IsNullOrEmpty(latestTag)) return false;
            var latest  = ParseVersion(latestTag.TrimStart('v', 'V'));
            var current = ParseVersion(CurrentVersion);
            return current > latest;
        }

        /// <summary>Major.Minor.Patch — use for version comparisons and display.</summary>
        public static string CurrentVersionString => CurrentVersion;

        /// <summary>
        /// Optional codename for the current version, e.g. "Camouflaged Koala".
        /// Returns an empty string for versions without a codename.
        /// </summary>
        public static string Codename =>
            _codenames.TryGetValue(CurrentVersion, out var name) ? name : "";

        /// <summary>
        /// Version + codename for display: "3.3.0 — Camouflaged Koala".
        /// Falls back to just the version string when no codename is set.
        /// </summary>
        public static string VersionWithCodename
        {
            get
            {
                var cn = Codename;
                return string.IsNullOrEmpty(cn) ? CurrentVersionString : $"{CurrentVersionString} — {cn}";
            }
        }

        /// <summary>
        /// Build timestamp (YYMMDDHHMM) read at runtime from the assembly's
        /// InformationalVersion attribute, which BUILD.bat sets via
        /// -p:InformationalVersion=3.3.0.YYMMDDHHMM.
        /// Returns an empty string in IDE / Debug builds where no stamp was injected.
        /// </summary>
        public static string BuildStamp
        {
            get
            {
                var info = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion;
                if (string.IsNullOrEmpty(info)) return "";
                // Strip any +git-hash suffix the SDK may append, then take part[3].
                var parts = info.Split('+')[0].Split('.');
                return parts.Length >= 4 ? parts[3] : "";
            }
        }

        /// <summary>Full display string: "3.3.0.2606011200" — or just "3.3.0" in IDE builds.</summary>
        public static string FullVersionString
        {
            get
            {
                var stamp = BuildStamp;
                return string.IsNullOrEmpty(stamp) ? CurrentVersion : $"{CurrentVersion}.{stamp}";
            }
        }

        private static Version ParseVersion(string s)
        {
            // Compare only Major.Minor.Patch — the 4th component is a build timestamp
            // (yyMMddHHmm) that exceeds int.MaxValue from ~2022 onward, causing
            // Version.TryParse to silently fail and fall back to (0,0), which makes
            // any GitHub tag appear newer than the locally running build.
            var parts = s.Split('.');
            if (parts.Length >= 3
                && int.TryParse(parts[0], out var major)
                && int.TryParse(parts[1], out var minor)
                && int.TryParse(parts[2], out var patch))
                return new Version(major, minor, patch);
            if (parts.Length >= 2
                && int.TryParse(parts[0], out major)
                && int.TryParse(parts[1], out minor))
                return new Version(major, minor);
            return new Version(0, 0);
        }

        // Fetch latest tag from GitHub tags API, then find its release asset.
        public static async Task<ReleaseInfo?> FetchLatestReleaseAsync()
        {
            using var http = MakeClient();

            // Step 1: get the latest tag name from the tags list
            var tagsJson = await http.GetStringAsync(TagsApiUrl);
            using var tagsDoc = JsonDocument.Parse(tagsJson);
            string? latestTag = null;
            Version latestVer  = new Version(0, 0);
            foreach (var tagEl in tagsDoc.RootElement.EnumerateArray())
            {
                var name = tagEl.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name == null) continue;
                var ver = ParseVersion(name.TrimStart('v', 'V'));
                if (ver > latestVer) { latestVer = ver; latestTag = name; }
            }
            if (latestTag == null) return null;

            // Step 2: find the GitHub release for this tag and pick the asset that
            // matches this process's architecture. Collect all assets first, then
            // choose by priority (arch-specific zip, then legacy MasselGUARD.zip).
            string? zipUrl = null;
            try
            {
                var relJson = await http.GetStringAsync(
                    ReleasesApiUrl + "/tags/" + latestTag);
                using var relDoc = JsonDocument.Parse(relJson);

                var byName = new System.Collections.Generic.Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                if (relDoc.RootElement.TryGetProperty("assets", out var assets))
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var aname = asset.TryGetProperty("name", out var an)
                            ? an.GetString() : null;
                        var url = asset.TryGetProperty("browser_download_url", out var u)
                            ? u.GetString() : null;
                        if (!string.IsNullOrEmpty(aname) && !string.IsNullOrEmpty(url))
                            byName[aname] = url;
                    }

                foreach (var candidate in AssetCandidates())
                    if (byName.TryGetValue(candidate, out var url)) { zipUrl = url; break; }
            }
            catch { /* tag exists but has no release — that is fine */ }

            return new ReleaseInfo(latestTag, zipUrl);
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
