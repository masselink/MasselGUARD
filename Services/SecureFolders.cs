using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MasselGUARD.Services
{
    /// <summary>
    /// Folder trust for the LocalSystem service (docs/ServiceBackend-Security.md).
    /// <list type="bullet">
    /// <item><see cref="Ensure"/>: the service's data folders under %ProgramData% are created with a protected DACL and a
    /// trusted OWNER, and an existing folder that a standard user created beforehand (owner = that user, or a
    /// junction) is moved aside instead of being trusted. (A DACL alone is not enough: the owner can always rewrite it.)</item>
    /// <item><see cref="CheckInstallFolder"/>: the folder the service runs from (and loads tunnel.dll / wireguard.dll from)
    /// must not be changeable by non-administrators, or any such user gets SYSTEM by replacing a file.</item>
    /// </list>
    /// WPF-free and CLI-shared; the ACL judgement is a pure function so it can be selftested.
    /// </summary>
    public static class SecureFolders
    {
        private static readonly SecurityIdentifier SidSystem = new(WellKnownSidType.LocalSystemSid, null);
        private static readonly SecurityIdentifier SidAdmins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        private static readonly SecurityIdentifier SidUsers  = new(WellKnownSidType.BuiltinUsersSid, null);
        private static readonly SecurityIdentifier SidTrustedInstaller = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");
        private const string SidCreatorOwner = "S-1-3-0";

        public static bool IsTrustedSid(string? sid) =>
            sid != null && (sid == SidSystem.Value || sid == SidAdmins.Value || sid == SidTrustedInstaller.Value);

        public readonly record struct AclEntry(string Sid, FileSystemRights Rights, bool Allow, bool InheritOnly);

        private const FileSystemRights TargetWrite =
            FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        private const FileSystemRights ParentWrite =
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        /// <summary>Pure: does this DACL + owner let a non-administrator change the object (or, for a parent folder,
        /// delete/rename it or rewrite its permissions)? Deny entries are ignored (conservative).</summary>
        public static bool IsRisky(IEnumerable<AclEntry> entries, string? ownerSid, bool isTarget)
        {
            if (!IsTrustedSid(ownerSid)) return true;   // the owner can always rewrite the DACL
            var mask = isTarget ? TargetWrite : ParentWrite;
            foreach (var e in entries)
            {
                if (!e.Allow || e.InheritOnly) continue;           // inherit-only entries only matter for the children, checked on their own
                if (IsTrustedSid(e.Sid) || e.Sid == SidCreatorOwner) continue;
                if ((e.Rights & mask) != 0) return true;
            }
            return false;
        }

        private static List<AclEntry> ReadAcl(FileSystemInfo fi, out string? owner)
        {
            FileSystemSecurity sec = fi is DirectoryInfo di ? di.GetAccessControl() : ((FileInfo)fi).GetAccessControl();
            owner = sec.GetOwner(typeof(SecurityIdentifier))?.Value;
            return sec.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                .Select(r => new AclEntry(r.IdentityReference.Value, r.FileSystemRights,
                                          r.AccessControlType == AccessControlType.Allow,
                                          (r.PropagationFlags & PropagationFlags.InheritOnly) != 0)).ToList();
        }

        /// <summary>Null when <paramref name="dir"/> (the folder the service runs from) and its program files
        /// cannot be changed by non-administrators; otherwise a message saying what is wrong.</summary>
        public static string? CheckInstallFolder(string dir, IEnumerable<string> files)
        {
            try
            {
                var di = new DirectoryInfo(dir);
                if (!di.Exists) return $"Folder not found: {dir}";
                for (DirectoryInfo? d = di; d != null; d = d.Parent)
                {
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0)
                        return $"'{d.FullName}' is a junction or symbolic link, so the service could be redirected to other files.";
                    var acl = ReadAcl(d, out var owner);
                    if (IsRisky(acl, owner, isTarget: d == di))
                        return d == di
                            ? $"The folder '{d.FullName}' can be changed by non-administrators. The service runs as SYSTEM from this folder, so a standard user could replace MasselGUARD.exe or a DLL and get SYSTEM rights. Install MasselGUARD to Program Files first."
                            : $"The parent folder '{d.FullName}' lets non-administrators rename, delete or re-permission the MasselGUARD folder, so the service could be redirected. Install MasselGUARD to Program Files first.";
                }
                foreach (var f in files.Where(File.Exists))
                {
                    var acl = ReadAcl(new FileInfo(f), out var owner);
                    if (IsRisky(acl, owner, isTarget: true))
                        return $"The file '{f}' can be changed by non-administrators. Install MasselGUARD to Program Files first.";
                }
                return null;
            }
            catch (Exception ex) { return $"The permissions of '{dir}' could not be checked ({ex.Message}); the service is not installed from a location it cannot verify."; }
        }

        // ── data folders ───────────────────────────────────────────────────────────

        private static DirectorySecurity BuildSecurity(bool usersCanRead)
        {
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);   // no inheritance from ProgramData
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(SidSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(SidAdmins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            if (usersCanRead)
                sec.AddAccessRule(new FileSystemAccessRule(SidUsers, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            return sec;
        }

        /// <summary>Makes sure <paramref name="path"/> is a real folder owned by SYSTEM/Administrators with a protected DACL.
        /// A folder that already exists with another owner (a standard user can create folders in ProgramData) or that is
        /// a junction is renamed to <c>name.untrusted-&lt;stamp&gt;</c> and replaced. Returns null on success, else why not
        /// (e.g. not running as administrator, so the new folder would be owned by the caller).</summary>
        public static string? Ensure(string path, bool usersCanRead)
        {
            try
            {
                if (File.Exists(path)) return $"'{path}' is a file.";
                if (Directory.Exists(path))
                {
                    var di = new DirectoryInfo(path);
                    bool link = (di.Attributes & FileAttributes.ReparsePoint) != 0;
                    ReadAcl(di, out var owner);
                    if (link || !IsTrustedSid(owner))
                    {
                        var aside = path.TrimEnd('\\', '/') + ".untrusted-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                        Directory.Move(path, aside);   // renames a junction itself, never its target
                    }
                }
                if (!Directory.Exists(path))
                {
                    var parent = Path.GetDirectoryName(path.TrimEnd('\\', '/'));
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    new DirectoryInfo(path).Create(BuildSecurity(usersCanRead));
                }
                else
                {
                    new DirectoryInfo(path).SetAccessControl(BuildSecurity(usersCanRead));
                }
                ReadAcl(new DirectoryInfo(path), out var owner2);
                if (!IsTrustedSid(owner2))
                    return $"'{path}' is not owned by SYSTEM or Administrators (owner {owner2}); it must be created by an elevated process.";
                return null;
            }
            catch (Exception ex) { return $"'{path}': {ex.Message}"; }
        }

        // ── self-test ───────────────────────────────────────────────────────────────

        public static (int pass, int fail, List<string> failures) RunSelfTest()
        {
            int pass = 0; var fails = new List<string>();
            void Check(bool ok, string what) { if (ok) pass++; else fails.Add(what); }
            const string Admin = "S-1-5-32-544", System_ = "S-1-5-18", Users = "S-1-5-32-545", Auth = "S-1-5-11", Me = "S-1-5-21-1-2-3-1001";
            const FileSystemRights Full = FileSystemRights.FullControl, Read = FileSystemRights.ReadAndExecute, Modify = FileSystemRights.Modify;

            Check(!IsRisky(new[] { new AclEntry(Admin, Full, true, false), new AclEntry(System_, Full, true, false), new AclEntry(Users, Read, true, false) }, Admin, true), "acl: admin-only write, users read = safe (Program Files style)");
            Check(IsRisky(new[] { new AclEntry(Admin, Full, true, false), new AclEntry(Auth, Modify, true, false) }, Admin, true), "acl: authenticated users can modify = risky");
            Check(IsRisky(new[] { new AclEntry(Me, Full, true, false) }, Admin, true), "acl: a single user with full control = risky");
            Check(IsRisky(new[] { new AclEntry(Admin, Full, true, false) }, Me, true), "acl: owned by a normal user = risky even with a clean DACL");
            Check(!IsRisky(new[] { new AclEntry(Users, Modify, true, true) }, Admin, true), "acl: inherit-only entries do not count for the folder itself");
            Check(!IsRisky(new[] { new AclEntry(Users, FileSystemRights.CreateDirectories | FileSystemRights.CreateFiles, true, false) }, Admin, false), "acl: creating siblings in a parent is fine (C:\\ root)");
            Check(IsRisky(new[] { new AclEntry(Users, FileSystemRights.DeleteSubdirectoriesAndFiles, true, false) }, Admin, false), "acl: deleting children of a parent = risky");
            Check(IsRisky(new[] { new AclEntry(Users, FileSystemRights.ChangePermissions, true, false) }, Admin, false), "acl: re-permissioning a parent = risky");
            Check(!IsRisky(new[] { new AclEntry(Users, Modify, false, false) }, Admin, true), "acl: deny entries are ignored (conservative on allow only)");
            Check(!IsRisky(new[] { new AclEntry(SidCreatorOwner, Full, true, false) }, Admin, true), "acl: CREATOR OWNER placeholder is not a principal");
            Check(IsTrustedSid(Admin) && IsTrustedSid(System_) && IsTrustedSid(SidTrustedInstaller.Value) && !IsTrustedSid(Users) && !IsTrustedSid(null), "acl: trusted owners");

            // real file system
            var temp = Path.Combine(Path.GetTempPath(), "mg-sec-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                Check(CheckInstallFolder(temp, new string[0]) != null, "install folder: a folder in %TEMP% is refused");
                Check(CheckInstallFolder(Path.Combine(temp, "missing"), new string[0]) != null, "install folder: a missing folder is refused");
                var sys = Environment.SystemDirectory;
                var sysResult = CheckInstallFolder(sys, new string[0]);
                Check(sysResult == null, "install folder: System32 is accepted" + (sysResult != null ? " (" + sysResult + ")" : ""));

                // Ensure: a folder created beforehand by a standard user must not be trusted.
                bool elevated = ServiceInstaller.IsElevated();
                var data = Path.Combine(temp, "data");
                Directory.CreateDirectory(data);                      // owner = this process' user
                File.WriteAllText(Path.Combine(data, "planted.txt"), "x");
                var res = Ensure(data, usersCanRead: false);
                // The folder we created ourselves is owned by this process' token owner: a standard user for a normal
                // process (untrusted), the Administrators group when elevated (already trusted, so it is kept).
                if (elevated)
                {
                    Check(res == null, "ensure: an elevated process ends with a trusted folder");
                }
                else
                {
                    Check(res != null, "ensure: a non-elevated process cannot end up with a trusted folder");
                    Check(Directory.GetDirectories(temp, "data.untrusted-*").Length == 1, "ensure: the pre-existing user-owned folder was moved aside, not trusted");
                    Check(!Directory.Exists(data) || IsTrustedSid(OwnerOf(data)) == false, "ensure: nothing untrusted is left at the trusted path");
                }
            }
            catch (Exception ex) { fails.Add("secure folders threw: " + ex.Message); }
            finally { try { Directory.Delete(temp, true); } catch { } }
            return (pass, fails.Count, fails);
        }

        private static string? OwnerOf(string path) { try { ReadAcl(new DirectoryInfo(path), out var o); return o; } catch { return null; } }
    }
}
