using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
    /// the arch-specific zip, extracting it next to the running exe, and relaunching.
    ///
    /// GitHub API endpoint:
    ///   GET https://api.github.com/repos/masselink/MasselGUARD/releases/latest
    ///
    /// The release must contain an architecture-specific asset - "MasselGUARD-x64.zip"
    /// or "MasselGUARD-arm64.zip" - matching the running process. The legacy single-arch
    /// "MasselGUARD.zip" is no longer produced or accepted.
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
        /// Release-asset name for the given architecture: only the arch-specific zip
        /// ("MasselGUARD-x64.zip" / "MasselGUARD-arm64.zip"). The legacy single-arch
        /// "MasselGUARD.zip" is no longer produced or accepted.
        /// </summary>
        private static string[] AssetCandidates(string arch)
            => new[] { $"MasselGUARD-{arch}.zip" };
        private const string TagsApiUrl     = "https://api.github.com/repos/masselink/MasselGUARD/tags";
        private const string ReleasesApiUrl = "https://api.github.com/repos/masselink/MasselGUARD/releases";
        // Major.Minor.Patch only - static, never modified by build.
        // The build timestamp is injected at compile time via -p:InformationalVersion
        // and read at runtime from the assembly attribute (see BuildStamp below).
        private const string CurrentVersion = "5";

        // Release codenames - one entry per public version, keyed by Major.Minor.Patch.
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
                { "3.9.0", "Adaptive Armadillo" },
                { "3.9.5", "Selective Serval" },
                { "4.0.0", "Forking Fox" },
                { "4.1.0", "Layered Lynx" },
                { "4.2.0", "Resolving Raven" },
                { "4.5.0", "Resolving Raven" },
                { "4.6.0", "Wired Weasel" },
                { "5", "Background Badger" },
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

            // The checksum published with the release (MasselGUARD-<arch>.zip.sha256). No checksum = no update:
            // the zip ends up running with administrator/SYSTEM rights, so an unverifiable download is refused.
            if (release.Sha256Url == null)
                throw new InvalidOperationException(
                    $"Release {release.TagName} has no MasselGUARD-{ArchMoniker}.zip.sha256 asset, so the download cannot be verified. " +
                    "The update was not applied; download the release manually from GitHub.");

            progress.Report(Lang.T("UpdateDownloading", release.TagName));

            // Download
            string? expectedHash;
            using (var http = MakeClient())
            {
                expectedHash = ParseChecksum(await http.GetStringAsync(release.Sha256Url));
                if (expectedHash == null)
                    throw new InvalidOperationException($"The checksum file of release {release.TagName} is not valid; the update was not applied.");

                using var resp = await http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync();
                await using var file   = File.Create(tempZip);
                await stream.CopyToAsync(file);
            }

            // Verify before anything is extracted or copied.
            var actualHash = Sha256OfFile(tempZip);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(tempZip); } catch { }
                throw new InvalidOperationException(
                    $"The downloaded update does not match its checksum (expected {expectedHash[..12]}..., got {actualHash[..12]}...). It was discarded and nothing was changed.");
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

            // Shutdown this instance - the batch will relaunch the new one.
            // GUI callers supply onShutdown to run the WPF dispatcher shutdown.
            onShutdown?.Invoke();
        }

        /// <summary>An update replaces the exe: that needs administrator rights when the MasselGUARD service
        /// is installed (it holds the exe, so it is stopped and restarted around the copy) or when the install
        /// folder is not writable by this user (Program Files).</summary>
        public static bool NeedsElevation()
        {
            if (Services.ServiceInstaller.IsElevated()) return false;
            if (Services.ServiceInstaller.IsInstalled()) return true;
            try
            {
                var dir = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory)!;
                var probe = Path.Combine(dir, ".mg-write-test-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "x"); File.Delete(probe);
                return false;
            }
            catch { return true; }
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

        /// <summary>Major.Minor.Patch - use for version comparisons and display.</summary>
        public static string CurrentVersionString => CurrentVersion;

        /// <summary>
        /// Optional codename for the current version, e.g. "Camouflaged Koala".
        /// Returns an empty string for versions without a codename.
        /// </summary>
        public static string Codename =>
            _codenames.TryGetValue(CurrentVersion, out var name) ? name : "";

        /// <summary>
        /// Version + codename for display: "3.3.0 - Camouflaged Koala".
        /// Falls back to just the version string when no codename is set.
        /// </summary>
        public static string VersionWithCodename
        {
            get
            {
                var cn = Codename;
                return string.IsNullOrEmpty(cn) ? CurrentVersionString : $"{CurrentVersionString} - {cn}";
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
                return SplitFullVersion(info).build;
            }
        }

        /// <summary>Splits a full version string into its base and its build stamp (YYMMDDHHMM). Handles every form the build
        /// produced: <c>4.6.0.2610071200</c>, <c>5.2610071200</c> (a single-number release), a padded <c>4.2.0.0</c> (no stamp), a bare
        /// <c>5</c> and a trailing <c>+git-hash</c>.</summary>
        public static (string ver, string build) SplitFullVersion(string? full)
        {
            if (string.IsNullOrWhiteSpace(full)) return ("0.0.0", "");
            var parts = full.Trim().TrimStart('v', 'V').Split('+')[0].Split('.');
            var last = parts[^1];
            if (parts.Length >= 2 && last.Length == 10 && last.All(char.IsDigit))
                return (string.Join('.', parts.Take(parts.Length - 1)), last);
            string ver = string.Join('.', parts.Take(3));
            string build = parts.Length >= 4 ? parts[3] : "";
            if (build == "0") build = "";
            return (ver, build);
        }

        /// <summary>Full display string: "3.3.0.2606011200" - or just "3.3.0" in IDE builds.</summary>
        public static string FullVersionString
        {
            get
            {
                var stamp = BuildStamp;
                return string.IsNullOrEmpty(stamp) ? CurrentVersion : $"{CurrentVersion}.{stamp}";
            }
        }

        /// <summary>Reads a version or release tag (without the leading v): <c>5</c>, <c>5.0</c> and <c>5.0.0</c> are the same version,
        /// <c>4.6.0</c> and <c>4.6.0-rc</c> too (the first numeric parts count). From 5 on releases are a single number (5, 6, 7...);
        /// the two- and three-part forms stay readable for the 4.x tags and for the first 5 tag (<c>v5.0</c>, which copies of 4.x need:
        /// their parser wants at least two parts). Anything else is 0.0.0. Always returns Major.Minor.Patch (a missing part is 0) so that
        /// <c>5</c> and <c>5.0.0</c> compare equal.</summary>
        private static Version ParseVersion(string s)
        {
            // Only Major.Minor.Patch count - the 4th component is a build timestamp (yyMMddHHmm) that exceeds
            // int.MaxValue from ~2022 onward, which made Version.TryParse fail and every tag look newer than the running build.
            var parts = s.Split('.');
            if (parts.Length >= 3
                && int.TryParse(parts[0], out var major)
                && int.TryParse(parts[1], out var minor)
                && int.TryParse(parts[2], out var patch))
                return new Version(major, minor, patch);
            if (parts.Length >= 2
                && int.TryParse(parts[0], out major)
                && int.TryParse(parts[1], out minor))
                return new Version(major, minor, 0);
            if (parts.Length == 1 && int.TryParse(parts[0], out major) && major >= 0)
                return new Version(major, 0, 0);
            return new Version(0, 0, 0);
        }

        /// <summary>The zip for <paramref name="arch"/> and its <c>.sha256</c> asset from a GitHub release object (the JSON of
        /// <c>/releases/latest</c> or <c>/releases/tags/&lt;tag&gt;</c>). Either may be null when the release does not have it.</summary>
        public static (string? zipUrl, string? sha256Url) PickAssets(JsonElement release, string arch)
        {
            var byName = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var asset in assets.EnumerateArray())
                {
                    var aname = asset.TryGetProperty("name", out var an) ? an.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (!string.IsNullOrEmpty(aname) && !string.IsNullOrEmpty(url)) byName[aname] = url;
                }
            foreach (var candidate in AssetCandidates(arch))
                if (byName.TryGetValue(candidate, out var zip))
                    return (zip, byName.TryGetValue(candidate + ".sha256", out var sha) ? sha : null);
            return (null, null);
        }

        /// <summary>The tag with the highest version number (numeric: <c>v10</c> beats <c>v9</c>, <c>v5.0</c> equals <c>v5</c>);
        /// tags that are not versions are ignored. Null when there is none.</summary>
        public static string? PickLatestTag(System.Collections.Generic.IEnumerable<string> tags)
        {
            string? best = null;
            Version bestVer = new Version(0, 0, 0);
            foreach (var name in tags)
            {
                var ver = ParseVersion(name.TrimStart('v', 'V'));
                if (ver > bestVer) { bestVer = ver; best = name; }
            }
            return best;
        }

        // Fetch latest tag from GitHub tags API, then find its release asset.
        // forceArch overrides the process architecture when selecting the asset - used by the
        // "switch to ARM64" flow, where the running process is emulated x64 but we want the
        // arm64 build. Pass null (default) to select for the current process architecture.
        public static async Task<ReleaseInfo?> FetchLatestReleaseAsync(string? forceArch = null)
        {
            using var http = MakeClient();
            var wantArch = forceArch ?? ArchMoniker;

            // Step 0: GitHub's own "latest release" (the newest published release that is not a draft or a pre-release): one request that
            // returns the tag and the files, and a tag without a release is never offered. Falls back to the tag list below when there is
            // no such release yet, the network fails, or the rate limit is hit.
            try
            {
                var latestJson = await http.GetStringAsync(ReleasesApiUrl + "/latest");
                using var latestDoc = JsonDocument.Parse(latestJson);
                var root = latestDoc.RootElement;
                var tag = root.TryGetProperty("tag_name", out var tn) ? tn.GetString() : null;
                bool isDraft = root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;
                bool isPre   = root.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True;
                if (!string.IsNullOrEmpty(tag) && !isDraft && !isPre && ParseVersion(tag.TrimStart('v', 'V')) > new Version(0, 0, 0))
                {
                    var (zip, sha) = PickAssets(root, wantArch);
                    return new ReleaseInfo(tag, zip, sha);
                }
            }
            catch { /* fall back to the tag list */ }

            // Step 1: the latest TAG, always: read every page of the tags list (the API returns the tags in an order that is not
            // numeric - v10 sorts before v9 - and 30 per page by default) and take the highest version number.
            var tagNames = new System.Collections.Generic.List<string>();
            for (int page = 1; page <= 10; page++)
            {
                var tagsJson = await http.GetStringAsync($"{TagsApiUrl}?per_page=100&page={page}");
                using var tagsDoc = JsonDocument.Parse(tagsJson);
                int count = 0;
                foreach (var tagEl in tagsDoc.RootElement.EnumerateArray())
                {
                    count++;
                    if (tagEl.TryGetProperty("name", out var n) && n.GetString() is { } name) tagNames.Add(name);
                }
                if (count < 100) break;
            }
            var latestTag = PickLatestTag(tagNames);
            if (latestTag == null) return null;

            // Step 2: find the GitHub release for this tag and pick the arch-specific
            // asset that matches this process's architecture.
            string? zipUrl = null;
            string? sha256Url = null;
            try
            {
                var relJson = await http.GetStringAsync(ReleasesApiUrl + "/tags/" + latestTag);
                using var relDoc = JsonDocument.Parse(relJson);
                (zipUrl, sha256Url) = PickAssets(relDoc.RootElement, wantArch);
            }            catch { /* tag exists but has no release - that is fine */ }

            return new ReleaseInfo(latestTag, zipUrl, sha256Url);
        }

        // ── Download verification ─────────────────────────────────────────────────

        /// <summary>The hash from a <c>.sha256</c> file: the first word of the first non-empty line (the format of
        /// <c>sha256sum</c> and of BUILD.bat: <c>&lt;hash&gt;  &lt;file name&gt;</c>), or just a bare hash. Null when it is not 64 hex digits.</summary>
        public static string? ParseChecksum(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            var word = line?.Split(new[] { ' ', '\t', '*' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (word == null || word.Length != 64 || !word.All(Uri.IsHexDigit)) return null;
            return word.ToLowerInvariant();
        }

        public static string Sha256OfFile(string path)
        {
            using var s = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(s)).ToLowerInvariant();
        }

        public static (int pass, int fail, System.Collections.Generic.List<string> failures) RunSelfTest()
        {
            int pass = 0; var fails = new System.Collections.Generic.List<string>();
            void Check(bool ok, string what) { if (ok) pass++; else fails.Add(what); }
            const string abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";   // SHA-256 of "abc"

            const string relJson = """{"tag_name":"v5.0","assets":[{"name":"MasselGUARD-x64.zip","browser_download_url":"https://x/x64.zip"},{"name":"MasselGUARD-x64.zip.sha256","browser_download_url":"https://x/x64.sha"},{"name":"MasselGUARD-arm64.zip","browser_download_url":"https://x/arm.zip"}]}""";
            using (var rel = JsonDocument.Parse(relJson))
            {
                Check(PickAssets(rel.RootElement, "x64") == ("https://x/x64.zip", "https://x/x64.sha"), "release assets: the zip and its checksum for this architecture");
                Check(PickAssets(rel.RootElement, "arm64") == ("https://x/arm.zip", null), "release assets: a zip without a checksum has none");
                Check(PickAssets(rel.RootElement, "riscv") == (null, null), "release assets: no zip for another architecture");
            }
            using (var empty = JsonDocument.Parse("""{"tag_name":"v5.0"}""")) Check(PickAssets(empty.RootElement, "x64") == (null, null), "release assets: a release without assets");
            Check(PickLatestTag(new[] { "v4.6.0", "v10", "v9", "v5.0" }) == "v10", "latest tag: numeric order (v10 beats v9)");
            Check(PickLatestTag(new[] { "v5.0", "v4.6.0", "latest", "nightly" }) == "v5.0" && PickLatestTag(new[] { "latest", "x" }) == null && PickLatestTag(System.Array.Empty<string>()) == null, "latest tag: ignores names that are not versions");
            Check(PickLatestTag(new[] { "v5", "v5.0.1", "v5.0" }) == "v5.0.1", "latest tag: patch beats the plain number");
            Check(SplitFullVersion("4.6.0.2610071200") == ("4.6.0", "2610071200") && SplitFullVersion("5.2610071200") == ("5", "2610071200"), "build stamp: after 3 parts and after a single number");
            Check(SplitFullVersion("5") == ("5", "") && SplitFullVersion("4.2.0.0") == ("4.2.0", "") && SplitFullVersion("4.6.0") == ("4.6.0", "") && SplitFullVersion("5.0") == ("5.0", ""), "build stamp: none when there is none");
            Check(SplitFullVersion("v5.2610071200+abc123") == ("5", "2610071200") && SplitFullVersion("") == ("0.0.0", "") && SplitFullVersion(null) == ("0.0.0", ""), "build stamp: v prefix, git hash, empty");
            // versions: a single number from 5 on, the old forms stay readable, and they compare as the same version
            Check(ParseVersion("5") == new Version(5, 0, 0) && ParseVersion("5.0") == new Version(5, 0, 0) && ParseVersion("5.0.0") == new Version(5, 0, 0), "version: 5, 5.0 and 5.0.0 are the same");
            Check(ParseVersion("4.6.0") == new Version(4, 6, 0) && ParseVersion("4.6.0-rc") == new Version(4, 6, 0) && ParseVersion("4.6") == new Version(4, 6, 0), "version: the 4.x forms");
            Check(ParseVersion("10") > ParseVersion("9") && ParseVersion("6") > ParseVersion("5.0") && ParseVersion("5") > ParseVersion("4.6.9"), "version: single numbers compare numerically");
            Check(ParseVersion("latest") == new Version(0, 0, 0) && ParseVersion("") == new Version(0, 0, 0) && ParseVersion("5-beta") == new Version(0, 0, 0) && ParseVersion("-1") == new Version(0, 0, 0), "version: garbage is 0.0.0");
            var cur = ParseVersion(CurrentVersion);
            Check(IsNewerVersion("v" + (cur.Major + 1)) && IsNewerVersion("V" + (cur.Major + 1) + ".0") && IsNewerVersion("v" + (cur.Major + 1) + ".0.0"), "version: the next number is newer, in any form");
            Check(!IsNewerVersion("v" + cur.Major + "." + cur.Minor + "." + cur.Build) && !IsNewerVersion("v1") && !IsNewerVersion("latest") && !IsNewerVersion(null), "version: the same, older and garbage tags are not newer");
            Check(IsAheadOfLatest("v1") && !IsAheadOfLatest("v" + (cur.Major + 1)), "version: ahead of an older tag only");

            Check(ParseChecksum(abc + "  MasselGUARD-x64.zip\n") == abc, "checksum: sha256sum format");
            Check(ParseChecksum(abc.ToUpperInvariant() + " *MasselGUARD-x64.zip") == abc, "checksum: upper case and binary marker");
            Check(ParseChecksum(abc) == abc && ParseChecksum("\r\n\r\n" + abc + "\r\n") == abc, "checksum: bare hash, blank lines, CRLF");
            Check(ParseChecksum("") == null && ParseChecksum(null) == null && ParseChecksum("not a hash") == null, "checksum: empty and garbage rejected");
            Check(ParseChecksum(abc[..63]) == null && ParseChecksum(abc + "0") == null, "checksum: wrong length rejected");
            Check(ParseChecksum("g" + abc[1..]) == null, "checksum: non-hex rejected");

            var f = Path.Combine(Path.GetTempPath(), "mg-sha-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(f, "abc");
                Check(Sha256OfFile(f) == abc, "checksum: file hash matches the known value");
                File.WriteAllText(f, "abd");
                Check(Sha256OfFile(f) != abc, "checksum: a changed file no longer matches");
            }
            catch (Exception ex) { fails.Add("checksum threw: " + ex.Message); }
            finally { try { File.Delete(f); } catch { } }
            return (pass, fails.Count, fails);
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
sc stop MasselGUARDsvc >nul 2>&1
timeout /t 2 /nobreak >nul
rem a connected tunnel (MasselGUARD.exe /service) still holds its exe: Windows allows renaming a running file, not overwriting it
for %%f in (MasselGUARD.exe MasselGUARDcli.exe tunnel.dll wireguard.dll) do if exist ""{destDir}\%%f"" ren ""{destDir}\%%f"" ""%%f.old-%random%""
robocopy ""{sourceDir}"" ""{destDir}"" /E /IS /IT /IM /NJH /NJS /NP >nul
if exist ""{sourceDir}\lang"" robocopy ""{sourceDir}\lang"" ""{destDir}\lang"" /E /IS /IT /IM /NJH /NJS /NP >nul
del ""{destDir}\*.old-*"" >nul 2>&1
sc start MasselGUARDsvc >nul 2>&1
start """" ""{exePath}""
del ""%~f0""
";
        }
    }

    public record ReleaseInfo(string TagName, string? ZipUrl, string? Sha256Url = null);
}
