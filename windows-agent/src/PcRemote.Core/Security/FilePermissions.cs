// File ACLs for trust material.
//
// The pairing-token store and the TLS private key are DPAPI LocalMachine
// protected — which any local process can DECRYPT — so the file ACL is the real
// gate, not the encryption. Files created under ProgramData inherit the
// ProgramData defaults (Users: read), and the installer's directory ACEs do not
// strip that inheritance, so the service hardens these files itself at every
// write.

using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PcRemote.Core;

public static class FilePermissions
{
    /// <summary>Restricts <paramref name="path"/> to SYSTEM (full) and
    /// Administrators (full), disabling inheritance. Best effort: a failure is
    /// logged and the file keeps its inherited ACL rather than breaking the
    /// caller.</summary>
    public static void RestrictToSystemAndAdmins(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.ResetAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));
            info.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Log.Warn($"could not restrict '{Path.GetFileName(path)}' to SYSTEM/Admins: {ex.Message}");
        }
    }
}
