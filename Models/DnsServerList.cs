using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MasselGUARD.Services;

namespace MasselGUARD.Models
{
    /// <summary>A value the user fills in for a resolver (for example the NextDNS configuration id). <see cref="Token"/> is an
    /// upper-case word that appears in the entry's DoH template; the app shows an empty field labelled with it and replaces it
    /// with what the user types.</summary>
    public sealed class DnsListParam
    {
        public string Token { get; init; } = "";
    }

    /// <summary>One resolver of the DNS server list (format: schemaVersion 1, see docs/DnsList-Format.md). Everything in it is
    /// language-neutral except <see cref="Description"/>, which is plain text in the list's own language (English); the words for
    /// <see cref="Blocks"/> and <see cref="Logging"/> come from the app's language files.</summary>
    public sealed class DnsListServer
    {
        public string Id { get; init; } = "";
        /// <summary>The service's own name, as its provider calls it (not translated).</summary>
        public string Name { get; init; } = "";
        public string Provider { get; init; } = "";
        public string Website { get; init; } = "";
        public string PrivacyPolicy { get; init; } = "";
        public string Country { get; init; } = "";
        public string Description { get; init; } = "";
        /// <summary>What the resolver blocks, as codes (<see cref="DnsServerList.KnownBlocks"/> are translated by the app, others show as written).</summary>
        public List<string> Blocks { get; init; } = new();
        public string Logging { get; init; } = "unknown";
        public List<string> V4 { get; init; } = new();
        public List<string> V6 { get; init; } = new();
        public string Doh { get; init; } = "";
        public bool EncryptedOnly { get; init; }
        public List<DnsListParam> Parameters { get; init; } = new();
    }

    /// <summary>A parsed list plus how many entries were skipped as invalid.</summary>
    public sealed class DnsListData
    {
        public List<DnsListServer> Servers { get; init; } = new();
        public int Skipped { get; init; }
        /// <summary>Provider files skipped because they use a schemaVersion this app does not know (they need a newer MasselGUARD).</summary>
        public int SkippedFiles { get; init; }
        public string Generated { get; init; } = "";
    }

    /// <summary>
    /// The public DNS server list the "Browse DNS servers" picker shows: parsing, validation, search,
    /// and conversion of a picked entry into a <see cref="DnsProfile"/>. PURE and WPF-free (CLI-shared,
    /// selftested). The list is data from the internet, so every entry is validated here and an invalid
    /// entry is skipped, never repaired; the final profile goes through the same
    /// <see cref="RpcValidator.Profile"/> rules the service applies.
    /// </summary>
    public static class DnsServerList
    {
        public const int SupportedSchema = 1;
        /// <summary>The <c>error</c> text of <see cref="ParseProvider"/> / <see cref="ParseIndex"/> for a file of another schemaVersion.</summary>
        public const string UnsupportedSchemaError = "unsupported schemaVersion";
        public const int MaxBytes = 256 * 1024;
        public const int MaxServers = 500;
        public const int MaxProviderFiles = 100;
        public const int MaxIndexBytes = 16 * 1024;
        public const int MaxProviderBytes = 64 * 1024;

        /// <summary>The block categories the app has words for (a list may use others; they show as written).</summary>
        public static readonly string[] KnownBlocks = { "malware", "ads", "trackers", "adult", "social", "gambling", "proxies" };
        private static readonly string[] Loggings = { "none", "minimal", "short-term", "anonymized", "configurable", "unknown" };
        private static readonly Regex IdRx = new("^[a-z0-9][a-z0-9-]{1,63}$", RegexOptions.Compiled);
        private static readonly Regex TokenRx = new("^[A-Z][A-Z0-9_]{1,31}$", RegexOptions.Compiled);
        private static readonly Regex BlockRx = new("^[a-z][a-z0-9-]{1,23}$", RegexOptions.Compiled);
        private static readonly Regex FileRx = new("^[a-z0-9][a-z0-9-]{0,40}$", RegexOptions.Compiled);
        private static readonly Regex InputRx = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

        // ── Parsing ───────────────────────────────────────────────────────────

        /// <summary>Parses the FLAT combined shape (<c>{"schemaVersion":1,"generated","servers":[...]}</c>, the built-in snapshot).
        /// Null with <paramref name="error"/> set when the file as a whole is unusable (bad JSON, unknown schemaVersion, too big);
        /// invalid single entries are skipped.</summary>
        public static DnsListData? Parse(string? json, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(json)) { error = "empty list"; return null; }
            if (Encoding.UTF8.GetByteCount(json) > MaxBytes) { error = "list too large"; return null; }
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { error = "not a list"; return null; }
                if (!root.TryGetProperty("schemaVersion", out var sv) || sv.ValueKind != JsonValueKind.Number || sv.GetInt32() != SupportedSchema)
                { error = UnsupportedSchemaError; return null; }
                if (!root.TryGetProperty("servers", out var arr) || arr.ValueKind != JsonValueKind.Array) { error = "no servers"; return null; }

                var list = new List<DnsListServer>();
                var ids = new HashSet<string>();
                int skipped = 0, seen = 0;
                foreach (var e in arr.EnumerateArray())
                {
                    if (++seen > MaxServers) { skipped++; continue; }
                    var s = ReadServer(e);
                    if (s == null || !ids.Add(s.Id)) { skipped++; continue; }
                    list.Add(s);
                }
                string gen = root.TryGetProperty("generated", out var g) && g.ValueKind == JsonValueKind.String ? Clip(g.GetString(), 40) : "";
                return new DnsListData { Servers = list, Skipped = skipped, Generated = gen };
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                error = "invalid list";
                return null;
            }
        }

        // ── The multi-file layout: index.json + one file per provider ─────────

        /// <summary>The provider file names of <c>index.json</c> (<c>{"schemaVersion":1,"version":"...","providers":["adguard",...]}</c>).
        /// Names are lower-case letters, digits and hyphens only (they become <c>servers/&lt;name&gt;.json</c>); duplicates are dropped.
        /// Null with <paramref name="error"/> when the index is unusable.</summary>
        public static List<string>? ParseIndex(string? json, out string version, out string? error)
        {
            version = ""; error = null;
            if (string.IsNullOrWhiteSpace(json)) { error = "empty index"; return null; }
            if (Encoding.UTF8.GetByteCount(json) > MaxIndexBytes) { error = "index too large"; return null; }
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { error = "not an index"; return null; }
                if (!root.TryGetProperty("schemaVersion", out var sv) || sv.ValueKind != JsonValueKind.Number || sv.GetInt32() != SupportedSchema)
                { error = UnsupportedSchemaError; return null; }
                if (!root.TryGetProperty("providers", out var arr) || arr.ValueKind != JsonValueKind.Array) { error = "no providers"; return null; }
                var names = new List<string>();
                foreach (var e in arr.EnumerateArray())
                {
                    var n = e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
                    if (FileRx.IsMatch(n) && !names.Contains(n) && names.Count < MaxProviderFiles) names.Add(n);
                }
                if (names.Count == 0) { error = "no valid provider names"; return null; }
                if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String) version = Clip(v.GetString(), 40);
                return names;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { error = "invalid index"; return null; }
        }

        /// <summary>Parses one provider file (<c>{"schemaVersion":1,"provider","website","privacyPolicy","country","servers":[...]}</c>) into
        /// flat entries that inherit the provider's fields. Invalid entries are skipped and counted.</summary>
        public static List<DnsListServer>? ParseProvider(string? json, out int skipped, out string? error)
        {
            skipped = 0; error = null;
            if (string.IsNullOrWhiteSpace(json)) { error = "empty provider file"; return null; }
            if (Encoding.UTF8.GetByteCount(json) > MaxProviderBytes) { error = "provider file too large"; return null; }
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { error = "not a provider file"; return null; }
                if (!root.TryGetProperty("schemaVersion", out var sv) || sv.ValueKind != JsonValueKind.Number || sv.GetInt32() != SupportedSchema)
                { error = UnsupportedSchemaError; return null; }
                if (!root.TryGetProperty("servers", out var arr) || arr.ValueKind != JsonValueKind.Array) { error = "no servers"; return null; }
                var list = new List<DnsListServer>();
                foreach (var e in arr.EnumerateArray())
                {
                    var s = list.Count < 50 ? ReadServer(e, root) : null;
                    if (s == null || list.Any(x => x.Id == s.Id)) skipped++; else list.Add(s);
                }
                return list;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { error = "invalid provider file"; return null; }
        }

        /// <summary>Parses the provider files an index names and joins them. A file of ANOTHER schemaVersion is skipped (and counted in
        /// <see cref="DnsListData.SkippedFiles"/>) so that one file written for a newer app cannot take the whole list down for an old one;
        /// a file that is missing or corrupt still fails the whole assembly (null + <paramref name="problem"/>), so the list never mixes versions.
        /// Null too when nothing is left.</summary>
        public static DnsListData? Assemble(List<string> names, string version, Func<string, string?> text, out string? problem)
        {
            problem = null;
            var parts = new List<List<DnsListServer>>();
            int skipped = 0, skippedFiles = 0;
            foreach (var n in names)
            {
                var t = text(n);
                int sk = 0;
                string? perr = null;
                var list = t == null ? null : ParseProvider(t, out sk, out perr);
                if (list == null && perr == UnsupportedSchemaError) { skippedFiles++; continue; }
                if (list == null) { problem = $"{n}.json: {perr ?? "missing"}"; return null; }
                skipped += sk;
                parts.Add(list);
            }
            var data = Combine(parts, skipped, version, skippedFiles);
            if (data.Servers.Count == 0) { problem = "no valid servers"; return null; }
            return data;
        }

        /// <summary>Joins the entries of all provider files (first file wins on a duplicate id, at most <see cref="MaxServers"/>).</summary>
        public static DnsListData Combine(IEnumerable<List<DnsListServer>> parts, int skipped, string version, int skippedFiles = 0)
        {
            var list = new List<DnsListServer>();
            var ids = new HashSet<string>();
            foreach (var part in parts)
                foreach (var s in part)
                    if (list.Count < MaxServers && ids.Add(s.Id)) list.Add(s); else skipped++;
            return new DnsListData { Servers = list, Skipped = skipped, SkippedFiles = skippedFiles, Generated = version };
        }

        // ── The user's own copy of the entries they added ─────────────────────

        /// <summary>The flat JSON (the same shape <see cref="Parse"/> reads) for a set of entries: the local copy of the servers the user picked.</summary>
        public static string ToFlatJson(IEnumerable<DnsListServer> servers, string generated = "")
        {
            var arr = servers.Select(s => new Dictionary<string, object?>
            {
                ["id"] = s.Id, ["name"] = s.Name, ["provider"] = s.Provider, ["website"] = s.Website, ["privacyPolicy"] = s.PrivacyPolicy,
                ["country"] = s.Country, ["description"] = s.Description, ["blocks"] = s.Blocks, ["logging"] = s.Logging,
                ["v4"] = s.V4, ["v6"] = s.V6, ["doh"] = s.Doh.Length > 0 ? s.Doh : null, ["encryptedOnly"] = s.EncryptedOnly,
                ["parameters"] = s.Parameters.Select(p => p.Token).ToList(),
            }).ToList();
            return JsonSerializer.Serialize(new Dictionary<string, object?> { ["schemaVersion"] = SupportedSchema, ["generated"] = generated, ["count"] = arr.Count, ["servers"] = arr },
                                            new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>The saved entries after adding <paramref name="add"/> (a newer copy replaces the same id) and dropping <paramref name="removeIds"/>.</summary>
        public static List<DnsListServer> MergeSaved(IEnumerable<DnsListServer> saved, IEnumerable<DnsListServer> add, IEnumerable<string> removeIds)
        {
            var drop = new HashSet<string>(removeIds);
            var addList = add.ToList();
            var ids = new HashSet<string>(addList.Select(a => a.Id));
            var result = saved.Where(s => !drop.Contains(s.Id) && !ids.Contains(s.Id)).ToList();
            result.AddRange(addList.Where(a => !drop.Contains(a.Id)));
            return result.Take(MaxServers).ToList();
        }

        /// <summary>The downloaded list plus the user's saved entries that are not in it (an entry removed from the online list stays
        /// visible while the user still has it, and the picker still works when the list could not be downloaded).</summary>
        public static DnsListData WithSaved(DnsListData? data, IEnumerable<DnsListServer> saved)
        {
            var list = data?.Servers.ToList() ?? new List<DnsListServer>();
            var ids = new HashSet<string>(list.Select(s => s.Id));
            foreach (var s in saved) if (list.Count < MaxServers && ids.Add(s.Id)) list.Add(s);
            return new DnsListData { Servers = list, Skipped = data?.Skipped ?? 0, SkippedFiles = data?.SkippedFiles ?? 0, Generated = data?.Generated ?? "" };
        }

        private static string Clip(string? s, int max) { var t = (s ?? "").Trim(); return t.Length > max ? t[..max] : t; }

        private static bool Clean(string s) => !s.Any(c => char.IsControl(c)) && !s.Contains('—');

        private static string Str(JsonElement e, string name, int max, bool required = false)
        {
            if (e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
            {
                var v = (p.GetString() ?? "").Trim();
                if (v.Length <= max && Clean(v)) return v;
            }
            return required ? "\0" : "";
        }

        private static string Url(JsonElement e, string name)
        {
            var v = Str(e, name, 200);
            return Uri.TryCreate(v, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? v : "";
        }

        private static List<string>? Addresses(JsonElement e, string name, AddressFamily family)
        {
            var list = new List<string>();
            if (!e.TryGetProperty(name, out var p)) return list;
            if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() > 4) return null;
            foreach (var item in p.EnumerateArray())
            {
                var s = item.ValueKind == JsonValueKind.String ? (item.GetString() ?? "").Trim() : "";
                if (s.Length is 0 or > 45 || !IPAddress.TryParse(s, out var ip) || ip.AddressFamily != family || !IsPublic(ip)) return null;
                if (!list.Contains(s)) list.Add(s);
            }
            return list;
        }

        /// <summary>Public unicast addresses only: a list entry must never point DNS at the local network or the machine itself.</summary>
        public static bool IsPublic(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)) return false;
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                if (b[0] == 10 || b[0] == 127 || b[0] == 0 || b[0] >= 224) return false;
                if (b[0] == 172 && b[1] is >= 16 and <= 31) return false;
                if (b[0] == 192 && b[1] == 168) return false;
                if (b[0] == 169 && b[1] == 254) return false;
                if (b[0] == 100 && b[1] is >= 64 and <= 127) return false;
                return true;
            }
            var v6 = ip.GetAddressBytes();
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv4MappedToIPv6) return false;
            if ((v6[0] & 0xFE) == 0xFC) return false;   // unique local fc00::/7
            return true;
        }

        private static DnsListServer? ReadServer(JsonElement e, JsonElement? prov = null)
        {
            if (e.ValueKind != JsonValueKind.Object) return null;
            var pe = prov ?? e;   // provider, website, privacyPolicy and country come from the provider file (or from the entry itself in the flat format)
            var id = Str(e, "id", 64, true);
            var name = Str(e, "name", 80, true);
            if (!IdRx.IsMatch(id) || name.Length == 0 || name == "\0") return null;
            // the description is optional, plain text; one that is present but invalid skips the entry
            string desc = "";
            if (e.TryGetProperty("description", out var dj) && dj.ValueKind != JsonValueKind.Null)
            {
                desc = Str(e, "description", 400, true);
                if (desc == "\0") return null;
            }
            var v4 = Addresses(e, "v4", AddressFamily.InterNetwork);
            var v6 = Addresses(e, "v6", AddressFamily.InterNetworkV6);
            if (v4 == null || v6 == null) return null;

            var doh = "";
            if (e.TryGetProperty("doh", out var dp) && dp.ValueKind != JsonValueKind.Null)
            {
                doh = Str(e, "doh", 300, true);
                if (doh == "\0") return null;
            }
            if (v4.Count == 0 && v6.Count == 0 && doh.Length == 0) return null;

            // parameters the user fills in: each token must occur in the DoH template (and no token may contain another).
            // An item is the token as a string, or an object { "token": "..." }.
            var pars = new List<DnsListParam>();
            if (e.TryGetProperty("parameters", out var pa) && pa.ValueKind == JsonValueKind.Array)
            {
                if (pa.GetArrayLength() > 4 || doh.Length == 0) return null;
                foreach (var pj in pa.EnumerateArray())
                {
                    var token = pj.ValueKind == JsonValueKind.String ? (pj.GetString() ?? "").Trim()
                              : pj.ValueKind == JsonValueKind.Object ? Str(pj, "token", 32, true) : "\0";
                    if (!TokenRx.IsMatch(token) || !doh.Contains(token)
                        || pars.Any(x => x.Token.Contains(token) || token.Contains(x.Token))) return null;
                    pars.Add(new DnsListParam { Token = token });
                }
            }
            if (pars.Count == 0 && Regex.IsMatch(doh, "YOUR_|CONFIG_ID|[{}<>]")) return null;   // an unfilled template without a parameter definition

            bool encOnly = e.TryGetProperty("encryptedOnly", out var eo) && eo.ValueKind == JsonValueKind.True;
            if (encOnly && doh.Length == 0) return null;

            var blocks = new List<string>();
            if (e.TryGetProperty("blocks", out var bl) && bl.ValueKind == JsonValueKind.Array)
                foreach (var b in bl.EnumerateArray())
                    if (b.ValueKind == JsonValueKind.String && b.GetString() is { } bs && BlockRx.IsMatch(bs) && !blocks.Contains(bs) && blocks.Count < 8) blocks.Add(bs);

            var logging = Str(e, "logging", 20);
            var server = new DnsListServer
            {
                Id = id, Name = name,
                Provider = Str(pe, "provider", 80), Website = Url(pe, "website"), PrivacyPolicy = Url(pe, "privacyPolicy"),
                Country = Str(pe, "country", 2), Description = desc, Blocks = blocks,
                Logging = Loggings.Contains(logging) ? logging : "unknown",
                V4 = v4, V6 = v6, Doh = doh, EncryptedOnly = encOnly, Parameters = pars,
            };
            // The same rules the service applies to a profile (a template with its token still in place is fine to check).
            return RpcValidator.Profile(ToProfile(server, null, validate: false)) == null ? server : null;
        }

        // ── Search and filter ─────────────────────────────────────────────────

        /// <summary>Filter keys for the picker's feature box: <c>block:&lt;code&gt;</c>, <c>enc</c> (answers encrypted queries only)
        /// and <c>param</c> (needs a value from the user).</summary>
        public static bool FilterMatches(DnsListServer s, string? filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            if (filter == "enc") return s.EncryptedOnly;
            if (filter == "param") return s.Parameters.Count > 0;
            if (filter.StartsWith("block:", StringComparison.Ordinal)) return s.Blocks.Contains(filter[6..]);
            return true;
        }

        /// <summary>Every word of <paramref name="query"/> must occur in the name, provider, description, country, the block codes
        /// or <paramref name="extraWords"/> (the app passes the translated block/logging words so a user can search in their own
        /// language), and the entry must pass <paramref name="filter"/>.</summary>
        public static bool Matches(DnsListServer s, string? query, string? filter, string extraWords = "")
        {
            if (!FilterMatches(s, filter)) return false;
            if (string.IsNullOrWhiteSpace(query)) return true;
            var hay = (s.Name + " " + s.Provider + " " + s.Description + " " + s.Country + " " + string.Join(' ', s.Blocks) + " " + s.Logging + " " + extraWords).ToLowerInvariant();
            return query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => hay.Contains(w));
        }

        // ── To a profile ──────────────────────────────────────────────────────

        /// <summary>A value for a list parameter: only letters, digits, - and _ (1-64; it ends up in a URL).</summary>
        public static bool ValidInput(string? value) => value != null && InputRx.IsMatch(value);

        /// <summary>True when every parameter of the entry has a valid value in <paramref name="values"/> (token to value).</summary>
        public static bool ParamsComplete(DnsListServer s, IReadOnlyDictionary<string, string>? values) =>
            s.Parameters.All(p => values != null && values.TryGetValue(p.Token, out var v) && ValidInput(v?.Trim()));

        /// <summary>A new profile (new id) from a list entry; <paramref name="values"/> (token to value) replace the entry's parameter tokens.
        /// Null when a parameter value is missing or invalid, or the result fails the service's rules.</summary>
        public static DnsProfile? ToProfile(DnsListServer s, IReadOnlyDictionary<string, string>? values, bool validate = true)
        {
            var doh = s.Doh;
            foreach (var prm in s.Parameters)
            {
                var v = values != null && values.TryGetValue(prm.Token, out var x) ? (x ?? "").Trim() : "";
                if (!validate) { if (v.Length == 0) v = "abc123"; }
                else if (!ValidInput(v)) return null;
                doh = doh.Replace(prm.Token, v);
            }
            var p = new DnsProfile
            {
                ListId = s.Id,
                Name = s.Name,
                Encryption = s.EncryptedOnly || (doh.Length > 0 && s.V4.Count + s.V6.Count == 0) ? "doh" : doh.Length > 0 ? "auto" : "plain",
                DohTemplate = doh,
                RequireEncryption = s.EncryptedOnly,
                V4Primary = s.V4.Count > 0 ? s.V4[0] : "", V4Secondary = s.V4.Count > 1 ? s.V4[1] : "",
                V6Primary = s.V6.Count > 0 ? s.V6[0] : "", V6Secondary = s.V6.Count > 1 ? s.V6[1] : "",
            };
            return !validate || RpcValidator.Profile(p) == null ? p : null;
        }

        /// <summary>True when the user already has this entry: same list id, same name, or the same first address / template.</summary>
        public static bool IsAdded(DnsListServer s, IEnumerable<DnsProfile> profiles) => FindAdded(s, profiles).Any();

        /// <summary>The profiles that are this entry (what "remove from my list" deletes).</summary>
        public static IEnumerable<DnsProfile> FindAdded(DnsListServer s, IEnumerable<DnsProfile> profiles) =>
            profiles.Where(p =>
                (p.ListId.Length > 0 && p.ListId == s.Id)
                || string.Equals(p.Name, s.Name, StringComparison.OrdinalIgnoreCase)
                || (s.V4.Count > 0 && p.V4Primary == s.V4[0] && SameTemplate(p, s))
                || (s.V4.Count == 0 && s.Doh.Length > 0 && string.Equals(p.DohTemplate, s.Doh, StringComparison.OrdinalIgnoreCase)));

        private static bool SameTemplate(DnsProfile p, DnsListServer s) =>
            s.Parameters.Count > 0 || string.Equals(p.DohTemplate ?? "", s.Doh, StringComparison.OrdinalIgnoreCase);

        // ── Where the list is downloaded from ─────────────────────────────────

        /// <summary>The folder URL of the list files on the main branch of a GitHub repository (index.json and servers/*.json
        /// are below it), or null when <paramref name="repoUrl"/> is not <c>https://github.com/owner/repo</c>.</summary>
        public static string? RawBase(string? repoUrl)
        {
            if (!Uri.TryCreate((repoUrl ?? "").Trim().TrimEnd('/'), UriKind.Absolute, out var u)) return null;
            if (u.Scheme != Uri.UriSchemeHttps || !u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;
            var parts = u.AbsolutePath.Trim('/').Split('/');
            if (parts.Length != 2 || !parts.All(x => Regex.IsMatch(x, @"^[A-Za-z0-9._-]{1,100}$"))) return null;
            return $"https://raw.githubusercontent.com/{parts[0]}/{parts[1]}/main/";
        }

        // ── Self-test ─────────────────────────────────────────────────────────

        private const string Sample = """
        {"schemaVersion":1,"generated":"2026-10-01","count":3,"servers":[
         {"id":"quad9","name":"Quad9 Secured","provider":"Quad9","website":"https://quad9.net/","privacyPolicy":"https://quad9.net/privacy/","country":"CH",
          "description":"Blocks malware.","blocks":["malware"],"logging":"minimal",
          "v4":["9.9.9.9","149.112.112.112"],"v6":["2620:fe::fe"],"doh":"https://dns.quad9.net/dns-query"},
         {"id":"nextdns","name":"NextDNS","provider":"NextDNS","country":"US","description":"Your own configuration.",
          "logging":"configurable","doh":"https://dns.nextdns.io/CONFIG_ID","parameters":["CONFIG_ID"]},
         {"id":"mullvad","name":"Mullvad DNS","provider":"Mullvad","country":"SE","blocks":[],
          "logging":"none","v4":["194.242.2.2"],"encryptedOnly":true,"doh":"https://dns.mullvad.net/dns-query"}]}
        """;

        public static (int pass, int fail, List<string> failures) RunSelfTest()
        {
            int pass = 0; var fails = new List<string>();
            void Check(bool ok, string what) { if (ok) pass++; else fails.Add(what); }
            DnsListData? Load(string json) => Parse(json, out _);
            string Mut(string from, string to) => Sample.Replace(from, to);
            Dictionary<string, string> V(string x) => new() { ["CONFIG_ID"] = x };

            var data = Parse(Sample, out var err);
            Check(data != null && err == null && data.Servers.Count == 3 && data.Skipped == 0, "parse: good sample");
            Check(data?.Generated == "2026-10-01", "parse: generated");

            // whole-file failures
            Check(Parse("", out _) == null, "parse: empty");
            Check(Parse("not json", out var e1) == null && e1 != null, "parse: bad json");
            Check(Load(Mut("\"schemaVersion\":1", "\"schemaVersion\":2")) == null, "parse: unknown schema");
            Check(Load(Mut("\"schemaVersion\":1,", "")) == null, "parse: no schema");
            Check(Load("[]") == null, "parse: not an object");
            Check(Parse(new string(' ', MaxBytes + 1) + Sample, out _) == null, "parse: too big");

            // entry skipping
            Check(Load(Mut("\"id\":\"quad9\"", "\"id\":\"Quad 9\""))?.Skipped == 1, "entry: bad id skipped");
            Check(Load(Mut("\"name\":\"Quad9 Secured\"", "\"name\":\"\""))?.Skipped == 1, "entry: empty name skipped");
            Check(Load(Mut("\"name\":\"Quad9 Secured\"", "\"name\":{\"en\":\"x\"}"))?.Skipped == 1, "entry: a name must be a plain string");
            Check(Load(Mut("\"9.9.9.9\"", "\"9.9.9.999\""))?.Skipped == 1, "entry: bad IPv4 skipped");
            Check(Load(Mut("\"9.9.9.9\"", "\"192.168.1.1\""))?.Skipped == 1, "entry: private IPv4 skipped");
            Check(Load(Mut("\"9.9.9.9\"", "\"127.0.0.1\""))?.Skipped == 1, "entry: loopback skipped");
            Check(Load(Mut("\"9.9.9.9\"", "\"169.254.1.1\""))?.Skipped == 1, "entry: link-local skipped");
            Check(Load(Mut("\"9.9.9.9\"", "\"2620:fe::fe\""))?.Skipped == 1, "entry: v6 address in v4 list skipped");
            Check(Load(Mut("2620:fe::fe", "fd00::1"))?.Skipped == 1, "entry: unique-local v6 skipped");
            Check(Load(Mut("\"https://dns.quad9.net/dns-query\"", "\"http://dns.quad9.net/dns-query\""))?.Skipped == 1, "entry: http DoH skipped");
            Check(Load(Mut("\"https://dns.quad9.net/dns-query\"", "\"https://x.net/a&calc\""))?.Skipped == 1, "entry: DoH metacharacter skipped");
            Check(Load(Mut("\"description\":\"Blocks malware.\",", ""))?.Skipped == 0, "entry: the description is optional");
            Check(Load(Mut("\"description\":\"Blocks malware.\"", "\"description\":{\"en\":\"x\"}"))?.Skipped == 1, "entry: a description must be a plain string");
            Check(Load(Mut("Blocks malware.", "Blocks\\u0007 malware."))?.Skipped == 1, "entry: control char skipped");
            Check(Load(Mut("Blocks malware.", "Blocks — malware."))?.Skipped == 1, "entry: em dash skipped");
            Check(Load(Mut("Blocks malware.", new string('x', 401)))?.Skipped == 1, "entry: over-long description skipped");
            Check(Load(Mut("\"parameters\":[\"CONFIG_ID\"]", "\"parameters\":[\"NOPE\"]"))?.Skipped == 1, "entry: token not in template skipped");
            Check(Load(Mut("\"parameters\":[\"CONFIG_ID\"]", "\"parameters\":[\"bad token\"]"))?.Skipped == 1, "entry: malformed token skipped");
            Check(Load(Mut(",\"parameters\":[\"CONFIG_ID\"]", ""))?.Skipped == 1, "entry: template without a parameter definition skipped");
            Check(Load(Mut("\"v4\":[\"194.242.2.2\"],\"encryptedOnly\":true,\"doh\":\"https://dns.mullvad.net/dns-query\"", "\"v4\":[\"194.242.2.2\"],\"encryptedOnly\":true"))?.Skipped == 1, "entry: encryptedOnly without doh skipped");
            Check(Load(Mut("\"v4\":[\"9.9.9.9\",\"149.112.112.112\"],\"v6\":[\"2620:fe::fe\"],\"doh\":\"https://dns.quad9.net/dns-query\"", "\"v4\":[]"))?.Skipped == 1, "entry: no address at all skipped");
            Check(Load(Mut("\"id\":\"nextdns\"", "\"id\":\"quad9\""))?.Skipped == 1, "entry: duplicate id skipped");
            Check(Load(Mut("\"logging\":\"minimal\"", "\"logging\":\"weird\""))?.Servers[0].Logging == "unknown", "entry: unknown logging becomes unknown");
            var bl = Load(Mut("\"blocks\":[\"malware\"]", "\"blocks\":[\"malware\",\"Bad Code\",\"malware\",\"gambling\"]"))?.Servers[0].Blocks;
            Check(bl != null && bl.Count == 2 && bl[0] == "malware" && bl[1] == "gambling", "entry: blocks keep valid codes (also unknown ones), drop bad and duplicate");

            if (data != null && data.Servers.Count == 3)
            {
                var q9 = data.Servers[0]; var nd = data.Servers[1]; var mv = data.Servers[2];
                Check(q9.Name == "Quad9 Secured" && q9.Description == "Blocks malware." && nd.Description == "Your own configuration." && mv.Description == "" && mv.Blocks.Count == 0, "entry: name, description and blocks as written");

                // parameters: the token is the label
                Check(nd.Parameters.Count == 1 && nd.Parameters[0].Token == "CONFIG_ID", "parameter: a plain string is the token");
                Check(!ParamsComplete(nd, null) && !ParamsComplete(nd, V("")) && ParamsComplete(nd, V(" abc123 ")) && ParamsComplete(q9, null), "parameter: complete only with a valid value");

                // search and filter
                Check(Matches(q9, "malware", null), "search: description or block word");
                Check(Matches(q9, "QUAD9 swiss", null) == false && Matches(q9, "quad9 ch", null), "search: all words, case-insensitive");
                Check(Matches(q9, "kwaadaardig", null) == false && Matches(q9, "kwaadaardig", null, "kwaadaardige software"), "search: translated words passed by the app");
                Check(Matches(q9, "  ", null), "search: blank matches all");
                Check(FilterMatches(q9, "block:malware") && !FilterMatches(q9, "block:adult") && FilterMatches(mv, "enc") && !FilterMatches(q9, "enc")
                      && FilterMatches(nd, "param") && !FilterMatches(q9, "param") && FilterMatches(q9, "") && FilterMatches(q9, null), "filter: block, encrypted only, parameter");

                // to profile
                var p = ToProfile(q9, null);
                Check(p != null && p.Name == "Quad9 Secured" && p.V4Primary == "9.9.9.9" && p.V4Secondary == "149.112.112.112" && p.V6Primary == "2620:fe::fe" && p.Encryption == "auto" && !p.RequireEncryption && p.ListId == "quad9", "profile: plain+doh entry");
                var pm = ToProfile(mv, null);
                Check(pm != null && pm.Encryption == "doh" && pm.RequireEncryption, "profile: encryptedOnly is DoH and fail-closed");
                Check(ToProfile(nd, null) == null && ToProfile(nd, V("a b")) == null && ToProfile(nd, V("../x")) == null && ToProfile(nd, V("")) == null && ToProfile(nd, V(new string('a', 65))) == null, "profile: bad or missing parameter refused");
                var pn = ToProfile(nd, V("abc-123_X"));
                Check(pn != null && pn.DohTemplate == "https://dns.nextdns.io/abc-123_X" && pn.Encryption == "doh", "profile: token replaced");
                Check(ToProfile(q9, null)!.Id != ToProfile(q9, null)!.Id, "profile: new id each time");

                // already added
                var have = new List<DnsProfile> { new() { Name = "my quad9", ListId = "quad9" } };
                Check(IsAdded(q9, have) && !IsAdded(mv, have), "added: by list id");
                Check(IsAdded(q9, new[] { new DnsProfile { Name = "QUAD9 secured" } }), "added: by name");
                var two = new List<DnsProfile> { new() { Name = "a", ListId = "quad9" }, new() { Name = "Quad9 Secured" }, new() { Name = "other" } };
                Check(FindAdded(q9, two).Count() == 2 && !FindAdded(mv, two).Any(), "added: FindAdded returns every matching profile");
                Check(IsAdded(q9, new[] { new DnsProfile { Name = "x", V4Primary = "9.9.9.9", DohTemplate = "https://dns.quad9.net/dns-query" } }), "added: by address and template");
                Check(!IsAdded(q9, new[] { new DnsProfile { Name = "x", V4Primary = "9.9.9.9", DohTemplate = "https://other.example/dns-query" } }), "added: same address other template is not the entry");
            }

            // the user's own copy of the entries they added
            if (data != null && data.Servers.Count == 3)
            {
                var json = ToFlatJson(data.Servers, "x");
                var back = Parse(json, out _);
                Check(back != null && back.Skipped == 0 && back.Servers.Count == 3, "saved: flat JSON round trip keeps every entry valid");
                Check(back != null && back.Servers.Zip(data.Servers).All(z => z.First.Id == z.Second.Id && z.First.Name == z.Second.Name && z.First.Description == z.Second.Description
                      && z.First.Blocks.SequenceEqual(z.Second.Blocks) && z.First.Logging == z.Second.Logging && z.First.V4.SequenceEqual(z.Second.V4) && z.First.V6.SequenceEqual(z.Second.V6)
                      && z.First.Doh == z.Second.Doh && z.First.EncryptedOnly == z.Second.EncryptedOnly && z.First.Parameters.Select(p => p.Token).SequenceEqual(z.Second.Parameters.Select(p => p.Token))
                      && z.First.Provider == z.Second.Provider && z.First.Country == z.Second.Country && z.First.Website == z.Second.Website), "saved: round trip keeps every field");
                var q9s = data.Servers[0]; var nds = data.Servers[1]; var mvs = data.Servers[2];
                var merged = MergeSaved(new[] { q9s }, new[] { nds }, Array.Empty<string>());
                Check(merged.Count == 2 && merged[0].Id == "quad9" && merged[1].Id == "nextdns", "saved: an added entry is appended");
                Check(MergeSaved(merged, new[] { mvs }, new[] { "quad9" }).Select(s => s.Id).SequenceEqual(new[] { "nextdns", "mullvad" }), "saved: a removed entry is dropped");
                Check(MergeSaved(merged, new[] { q9s }, Array.Empty<string>()).Count == 2, "saved: adding the same id again replaces it");
                var withSaved = WithSaved(new DnsListData { Servers = new List<DnsListServer> { q9s }, Skipped = 1, Generated = "g" }, new[] { q9s, nds });
                Check(withSaved.Servers.Count == 2 && withSaved.Skipped == 1 && withSaved.Generated == "g", "saved: WithSaved adds only entries the list does not have");
                Check(WithSaved(null, new[] { nds }).Servers.Count == 1 && WithSaved(null, Array.Empty<DnsListServer>()).Servers.Count == 0, "saved: usable without any downloaded list");
            }

            // a provider file of another schemaVersion is skipped, a corrupt one is not
            const string pGood = """{"schemaVersion":1,"provider":"A","servers":[{"id":"aaa","name":"A","v4":["9.9.9.31"]}]}""";
            const string pNew = """{"schemaVersion":2,"provider":"B","servers":[]}""";
            string? PText(string n) => n switch { "a" => pGood, "b" => pNew, "bad" => "{ not json", _ => null };
            var asm = Assemble(new List<string> { "a", "b" }, "v", PText, out var asmProblem);
            Check(asm != null && asmProblem == null && asm.Servers.Count == 1 && asm.SkippedFiles == 1, "assemble: a file of another schemaVersion is skipped and counted");
            Check(Assemble(new List<string> { "a", "bad" }, "v", PText, out var p2) == null && p2 != null && p2.StartsWith("bad.json"), "assemble: a corrupt file fails the whole list");
            Check(Assemble(new List<string> { "a", "missing" }, "v", PText, out var p3) == null && p3 != null, "assemble: a missing file fails the whole list");
            Check(Assemble(new List<string> { "b" }, "v", PText, out var p4) == null && p4 == "no valid servers", "assemble: nothing usable is an error");
            Check(ParseProvider(pNew, out _, out var pErr) == null && pErr == UnsupportedSchemaError && ParseIndex("""{"schemaVersion":2,"providers":["a"]}""", out _, out var iErr) == null && iErr == UnsupportedSchemaError, "schema: the unsupported-version error text is stable");
            // public address rules
            Check(IsPublic(IPAddress.Parse("9.9.9.9")) && IsPublic(IPAddress.Parse("2606:4700:4700::1111")), "public: ok");
            Check(!IsPublic(IPAddress.Parse("10.1.2.3")) && !IsPublic(IPAddress.Parse("172.20.0.1")) && !IsPublic(IPAddress.Parse("100.64.0.1")) && !IsPublic(IPAddress.Parse("224.0.0.1")), "public: private ranges refused");
            Check(!IsPublic(IPAddress.Parse("::1")) && !IsPublic(IPAddress.Parse("fe80::1")) && !IsPublic(IPAddress.Parse("::ffff:10.0.0.1")), "public: v6 local refused");

            // several parameters (strings or objects)
            const string twoJson = """{"schemaVersion":1,"provider":"X","servers":[{"id":"two","name":"Two","doh":"https://dns.example.net/ACCOUNT/DEVICE/dns-query","parameters":["ACCOUNT",{"token":"DEVICE"}]}]}""";
            var tw = ParseProvider(twoJson, out var twSkip, out _);
            Check(tw != null && tw.Count == 1 && tw[0].Parameters.Count == 2 && twSkip == 0, "parameters: two parameters (string and object form)");
            if (tw != null && tw.Count == 1)
            {
                var pv = ToProfile(tw[0], new Dictionary<string, string> { ["ACCOUNT"] = "a1", ["DEVICE"] = "d-2" });
                Check(pv != null && pv.DohTemplate == "https://dns.example.net/a1/d-2/dns-query", "parameters: every token replaced");
                Check(ToProfile(tw[0], new Dictionary<string, string> { ["ACCOUNT"] = "a1" }) == null, "parameters: one missing value refuses the profile");
            }
            Check(ParseProvider(twoJson.Replace("{\"token\":\"DEVICE\"}", "\"ACCOUNT\""), out var dupSkip, out _)?.Count == 0 && dupSkip == 1, "parameters: the same token twice skips the entry");
            Check(ParseProvider(twoJson.Replace("{\"token\":\"DEVICE\"}", "\"ACCOUNT_X\""), out var subSkip, out _)?.Count == 0 && subSkip == 1, "parameters: a token missing from the template skips the entry");

            // multi-file layout
            var idx = ParseIndex("""{"schemaVersion":1,"version":"2026-10-06","providers":["adguard","quad9","adguard","../evil","Bad","a b",""]}""", out var ver, out var ierr);
            Check(idx != null && ierr == null && ver == "2026-10-06" && idx.Count == 2 && idx[0] == "adguard" && idx[1] == "quad9", "index: valid names only, duplicates dropped");
            Check(ParseIndex("""{"schemaVersion":2,"providers":["a"]}""", out _, out _) == null, "index: unknown schemaVersion");
            Check(ParseIndex("""{"schemaVersion":1,"providers":["../x","C:"]}""", out _, out _) == null, "index: no valid name is an error");
            Check(ParseIndex("{}", out _, out _) == null && ParseIndex("nope", out _, out _) == null && ParseIndex("", out _, out _) == null, "index: missing, bad json, empty");
            Check(ParseIndex("""{"schemaVersion":1,"providers":[""" + string.Join(",", Enumerable.Range(0, 150).Select(i => "\"p" + i + "\"")) + "]}", out _, out _)?.Count == MaxProviderFiles, "index: at most MaxProviderFiles");

            const string prov = """
            {"schemaVersion":1,"provider":"Quad9","website":"https://quad9.net/","privacyPolicy":"https://quad9.net/privacy/","country":"CH","servers":[
              {"id":"quad9","name":"Quad9 Secured","description":"Blocks malware.","blocks":["malware"],"logging":"minimal","v4":["9.9.9.9"],"doh":"https://dns.quad9.net/dns-query"},
              {"id":"quad9-unsecured","name":"Quad9 Unsecured","blocks":[],"logging":"minimal","v4":["9.9.9.10"]},
              {"id":"bad id","name":"Broken","v4":["9.9.9.11"]},
              {"id":"quad9","name":"Duplicate","v4":["9.9.9.12"]}]}
            """;
            var pl = ParseProvider(prov, out var pskip, out var perr);
            Check(pl != null && perr == null && pl.Count == 2 && pskip == 2, "provider: valid entries kept, bad and duplicate skipped");
            Check(pl != null && pl.All(s => s.Provider == "Quad9" && s.Country == "CH" && s.Website == "https://quad9.net/" && s.PrivacyPolicy == "https://quad9.net/privacy/"), "provider: entries inherit the provider fields");
            Check(ParseProvider(prov.Replace("\"schemaVersion\":1", "\"schemaVersion\":3"), out _, out var e3) == null && e3 != null, "provider: unknown schemaVersion");
            Check(ParseProvider("""{"schemaVersion":1,"servers":"x"}""", out _, out _) == null && ParseProvider("[", out _, out _) == null, "provider: no servers array, bad json");
            Check(ParseProvider(new string(' ', MaxProviderBytes + 1) + prov, out _, out _) == null, "provider: too big");
            Check(ParseProvider(prov.Replace("\"v4\":[\"9.9.9.9\"]", "\"v4\":[\"10.0.0.1\"]"), out var s4, out _)?.Count == 2 && s4 == 2, "provider: private address skips the entry");

            if (pl != null)
            {
                var comb = Combine(new[] { pl, pl }, 1, "v");
                Check(comb.Servers.Count == 2 && comb.Skipped == 3 && comb.Generated == "v", "combine: duplicate ids across files dropped and counted");
            }

            // repository URL
            Check(RawBase("https://github.com/masselink/MasselGUARD-dnslist") == "https://raw.githubusercontent.com/masselink/MasselGUARD-dnslist/main/", "repo: official");
            Check(RawBase("https://github.com/a/b/") != null, "repo: trailing slash");
            Check(RawBase("http://github.com/a/b") == null && RawBase("https://evil.example/a/b") == null && RawBase("https://github.com/a") == null
               && RawBase("https://github.com/a/b/c") == null && RawBase("https://github.com.evil.net/a/b") == null && RawBase("") == null, "repo: only https github owner/repo");

            return (pass, fails.Count, fails);
        }
    }
}
