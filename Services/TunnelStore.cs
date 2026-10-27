using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MasselGUARD.Services
{
    /// <summary>
    /// The service's own copy of the tunnel configs (docs/ServiceBackend-Design.md, decision 1). The per-user
    /// DPAPI files in %APPDATA% cannot be read by the LocalSystem service; this store lives under
    /// %ProgramData% (ACL: SYSTEM + Administrators) and is encrypted with the machine scope, so the service
    /// can connect a tunnel by NAME without the window sending the whole config every time. The window pushes
    /// a config when its hash differs from the stored one. WPF-free and CLI-shared; the encryption is injected
    /// so the self-test needs no DPAPI.
    /// </summary>
    public interface ITunnelStore
    {
        void Put(string name, string conf, string? ownerSid = null);
        /// <summary>The user (SID) who stored this tunnel, null when unknown (older entries or the window of an admin).</summary>
        string? OwnerOf(string name);
        string? Get(string name);
        string? Hash(string name);
        bool Delete(string name);
        IReadOnlyList<string> Names();
        /// <summary>Deletes stored tunnels whose name is not in <paramref name="keep"/>; returns how many. With an
        /// <paramref name="ownerSid"/> only entries of that user (or without an owner) are considered.</summary>
        int Prune(IEnumerable<string> keep, string? ownerSid = null);
    }

    public sealed class FileTunnelStore : ITunnelStore
    {
        private sealed class Entry { public string Name { get; set; } = ""; public string Conf { get; set; } = ""; public string? OwnerSid { get; set; } }

        private readonly string _dir;
        private readonly Func<byte[], byte[]> _protect, _unprotect;
        private readonly object _lock = new();

        public FileTunnelStore(string dir, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect)
        { _dir = dir; _protect = protect; _unprotect = unprotect; }

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MasselGUARD.TunnelStore.v1");

        /// <summary>Machine-scope DPAPI with an app-specific entropy.</summary>
        public static FileTunnelStore ForMachine(string dir) => new(dir,
            b => ProtectedData.Protect(b, Entropy, DataProtectionScope.LocalMachine),
            b => ProtectedData.Unprotect(b, Entropy, DataProtectionScope.LocalMachine));

        public static string HashOf(string conf) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conf))).ToLowerInvariant();

        // The file name is a hash of the name, so no tunnel name can ever become a path or a device name.
        private string PathFor(string name) => Path.Combine(_dir,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToLowerInvariant())))[..32].ToLowerInvariant() + ".tun");

        public void Put(string name, string conf, string? ownerSid = null)
        {
            lock (_lock)
            {
                Directory.CreateDirectory(_dir);
                var existing = File.Exists(PathFor(name)) ? Read(PathFor(name)) : null;
                var json = JsonSerializer.SerializeToUtf8Bytes(new Entry { Name = name, Conf = conf, OwnerSid = ownerSid ?? existing?.OwnerSid });
                var tmp = PathFor(name) + ".tmp";
                File.WriteAllBytes(tmp, _protect(json));
                File.Move(tmp, PathFor(name), overwrite: true);
            }
        }

        private Entry? Read(string path)
        {
            try { return JsonSerializer.Deserialize<Entry>(_unprotect(File.ReadAllBytes(path))); }
            catch { return null; }
        }

        public string? Get(string name)
        {
            lock (_lock)
            {
                var p = PathFor(name);
                if (!File.Exists(p)) return null;
                var e = Read(p);
                return e != null && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) ? e.Conf : null;
            }
        }

        public string? OwnerOf(string name)
        {
            lock (_lock)
            {
                var p = PathFor(name);
                return File.Exists(p) ? Read(p)?.OwnerSid : null;
            }
        }

        public string? Hash(string name) => Get(name) is { } c ? HashOf(c) : null;

        public bool Delete(string name)
        {
            lock (_lock)
            {
                var p = PathFor(name);
                if (!File.Exists(p)) return false;
                File.Delete(p);
                return true;
            }
        }

        public IReadOnlyList<string> Names()
        {
            lock (_lock)
            {
                if (!Directory.Exists(_dir)) return new List<string>();
                return Directory.GetFiles(_dir, "*.tun").Select(Read).Where(e => e != null).Select(e => e!.Name).ToList();
            }
        }

        public int Prune(IEnumerable<string> keep, string? ownerSid = null)
        {
            var keepSet = new HashSet<string>(keep, StringComparer.OrdinalIgnoreCase);
            int n = 0;
            lock (_lock)
            {
                if (!Directory.Exists(_dir)) return 0;
                foreach (var f in Directory.GetFiles(_dir, "*.tun"))
                {
                    var e = Read(f);
                    if (e == null) { try { File.Delete(f); n++; } catch { } continue; }
                    bool mine = ownerSid == null || e.OwnerSid == null || string.Equals(e.OwnerSid, ownerSid, StringComparison.OrdinalIgnoreCase);
                    if (mine && !keepSet.Contains(e.Name)) { try { File.Delete(f); n++; } catch { } }
                }
            }
            return n;
        }

        public static (int pass, int fail, List<string> failures) RunSelfTest()
        {
            int pass = 0; var fails = new List<string>();
            void Check(bool ok, string what) { if (ok) pass++; else fails.Add(what); }
            var dir = Path.Combine(Path.GetTempPath(), "mg-store-" + Guid.NewGuid().ToString("N"));
            try
            {
                // Reversible stand-in cipher: the test must not depend on the machine's DPAPI.
                byte[] Xor(byte[] b) => b.Select(x => (byte)(x ^ 0x5A)).ToArray();
                var s = new FileTunnelStore(dir, Xor, Xor);

                Check(s.Get("Home") == null && s.Hash("Home") == null && s.Names().Count == 0, "store: empty at start");
                s.Put("Home", "[Interface]\nPrivateKey = a=\n");
                Check(s.Get("Home") == "[Interface]\nPrivateKey = a=\n", "store: put/get round trip");
                Check(s.Get("hOmE") != null, "store: names are case-insensitive");
                Check(s.Hash("Home") == HashOf("[Interface]\nPrivateKey = a=\n") && s.Hash("Home")!.Length == 64, "store: hash is sha256 of the config");
                var raw = Directory.GetFiles(dir, "*.tun").Single();
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(raw)).Contains("PrivateKey"), "store: file is not plaintext");
                Check(Path.GetFileName(raw).Length == 36, "store: file name is a hash, not the tunnel name");

                s.Put("Home", "[Interface]\nPrivateKey = b=\n");
                Check(s.Get("Home")!.Contains("b=") && s.Names().Count == 1, "store: put replaces");
                s.Put("Büro VPN", "x");
                s.Put("Work", "y");
                Check(s.Names().Count == 3 && s.Names().Contains("Büro VPN"), "store: names listed (unicode ok)");
                Check(s.Prune(new[] { "home", "Work" }) == 1 && s.Get("Büro VPN") == null && s.Get("Work") == "y", "store: prune keeps listed (case-insensitive), removes the rest");
                Check(s.Delete("Work") && !s.Delete("Work") && s.Get("Work") == null, "store: delete once");

                // owners
                s.Put("Mine", "a", "S-1-5-21-1-1-1-1001");
                s.Put("Theirs", "b", "S-1-5-21-1-1-1-1002");
                s.Put("Legacy", "c");
                Check(s.OwnerOf("Mine") == "S-1-5-21-1-1-1-1001" && s.OwnerOf("Legacy") == null, "store: owner recorded");
                s.Put("Mine", "a2");
                Check(s.OwnerOf("Mine") == "S-1-5-21-1-1-1-1001", "store: a put without an owner keeps the existing owner");
                s.Prune(new[] { "Home" }, "S-1-5-21-1-1-1-1001");
                Check(s.Get("Mine") == null && s.Get("Theirs") == "b" && s.Get("Legacy") == null, "store: prune with an owner leaves other users' tunnels alone");
                s.Delete("Theirs");

                File.WriteAllBytes(Path.Combine(dir, new string('0', 32) + ".tun"), new byte[] { 1, 2, 3 });
                Check(s.Names().Count == 1 && s.Prune(new[] { "Home" }) == 1, "store: a corrupt file is ignored and pruned");

                // The real machine-scope cipher round-trips too (any user may call it; the ACL is what protects the files).
                var real = ForMachine(Path.Combine(dir, "machine"));
                real.Put("T", "secret");
                Check(real.Get("T") == "secret", "store: machine-scope DPAPI round trip");
            }
            catch (Exception ex) { fails.Add("store threw: " + ex.Message); }
            finally { try { Directory.Delete(dir, true); } catch { } }
            return (pass, fails.Count, fails);
        }
    }
}
