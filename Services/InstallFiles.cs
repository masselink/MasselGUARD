using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Copying program files over an installed copy that is still in use. The MasselGUARD service, every
    /// connected tunnel (each one is a <c>MasselGUARD.exe /service</c> process) and a running window all hold
    /// the exe open, so a plain overwrite fails with "being used by another process". Windows does let a
    /// running exe or dll be RENAMED, so the old file is moved aside (<c>name.old-&lt;stamp&gt;</c>) and the new one
    /// takes its place; the leftovers are deleted at a later start. WPF-free and CLI-shared.
    /// </summary>
    public static class InstallFiles
    {
        public const string OldMarker = ".old-";

        /// <summary>Copies <paramref name="src"/> to <paramref name="dst"/>. When the target is locked, the old file
        /// is renamed aside first. Returns true when the old file had to be moved aside.</summary>
        public static bool CopyOverwriting(string src, string dst)
        {
            try { File.Copy(src, dst, overwrite: true); return false; }
            catch (IOException) when (File.Exists(dst)) { /* in use: swap by renaming */ }
            catch (UnauthorizedAccessException) when (File.Exists(dst)) { }

            var aside = dst + OldMarker + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            File.Move(dst, aside);
            try { File.Copy(src, dst, overwrite: false); }
            catch { try { File.Move(aside, dst, overwrite: true); } catch { } throw; }   // put the old one back, nothing lost
            return true;
        }

        /// <summary>Deletes leftovers of earlier swaps in <paramref name="dir"/> (files still in use stay for next time).</summary>
        public static int CleanupOld(string dir)
        {
            int n = 0;
            try
            {
                foreach (var f in Directory.GetFiles(dir).Where(f => Path.GetFileName(f).Contains(OldMarker)))
                    try { File.Delete(f); n++; } catch { }
            }
            catch { }
            return n;
        }

        public static (int pass, int fail, List<string> failures) RunSelfTest()
        {
            int pass = 0; var fails = new List<string>();
            void Check(bool ok, string what) { if (ok) pass++; else fails.Add(what); }
            var dir = Path.Combine(Path.GetTempPath(), "mg-install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var src = Path.Combine(dir, "new.bin"); File.WriteAllText(src, "NEW");
                var dst = Path.Combine(dir, "app.exe");

                File.WriteAllText(dst, "OLD");
                Check(!CopyOverwriting(src, dst) && File.ReadAllText(dst) == "NEW", "install: a free target is simply overwritten");

                // A target that is open for reading with delete sharing behaves like a running exe:
                // it cannot be overwritten, but it can be renamed.
                File.WriteAllText(dst, "OLD");
                using (var held = new FileStream(dst, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    bool swapped = CopyOverwriting(src, dst);
                    Check(swapped && File.ReadAllText(dst) == "NEW", "install: a file in use is renamed aside and replaced");
                    Check(Directory.GetFiles(dir).Count(f => f.Contains(OldMarker)) == 1, "install: the old file is kept aside");
                }
                CleanupOld(dir);
                Check(!Directory.GetFiles(dir).Any(f => f.Contains(OldMarker)), "install: cleanup removes the leftovers");

                // Nothing to rename: a missing target is just a copy.
                var fresh = Path.Combine(dir, "fresh.exe");
                Check(!CopyOverwriting(src, fresh) && File.ReadAllText(fresh) == "NEW", "install: missing target is created");

                // A target that cannot even be renamed (no delete sharing) reports the failure and keeps the old file.
                File.WriteAllText(dst, "OLD");
                using (var locked = new FileStream(dst, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    bool threw = false;
                    try { CopyOverwriting(src, dst); } catch (IOException) { threw = true; }
                    Check(threw && File.Exists(dst), "install: a fully locked file fails cleanly, the old one stays");
                }
            }
            catch (Exception ex) { fails.Add("install threw: " + ex.Message); }
            finally { try { Directory.Delete(dir, true); } catch { } }
            return (pass, fails.Count, fails);
        }
    }
}
