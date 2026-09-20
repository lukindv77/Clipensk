using System.Security.AccessControl;
using System.Security.Principal;

namespace Clipensk.Windows.Security;

/// <summary>
/// Restricts a configured data root to the Windows user who configured it, per explicit product
/// decision: another user of the same computer must not reach this user's storage. The databases
/// are already SQLCipher-encrypted, so this is not what keeps their contents secret — it is what
/// stops another account from reading, replacing or deleting the files at all.
///
/// Applied when a data root is configured, not on every start: rewriting the ACL of a folder the
/// user chose, behind their back, on every launch would be a surprising thing for a resident app
/// to do to a directory it does not exclusively own.
/// </summary>
public static class WindowsDataRootProtectionService
{
    /// <summary>
    /// Grants the current user, SYSTEM and Administrators full control and removes everything
    /// else, including inherited access. SYSTEM and Administrators are kept deliberately: dropping
    /// them does not increase privacy on a machine where an administrator can take ownership
    /// anyway, but it does break backup, antivirus and repair tooling.
    ///
    /// Returns <c>false</c> when the volume cannot express this (FAT32/exFAT removable media, some
    /// network shares). The caller keeps the chosen location and warns the user rather than failing
    /// the whole configuration: the user explicitly picked that folder, and its contents stay
    /// encrypted either way.
    /// </summary>
    public static bool TryProtect(string dataRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);

        try
        {
            var directory = new DirectoryInfo(dataRootPath);
            directory.Create();

            SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is null)
            {
                return false;
            }

            DirectorySecurity security = directory.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            FileSystemAccessRule[] existingRules = security
                .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToArray();
            foreach (FileSystemAccessRule existing in existingRules)
            {
                security.RemoveAccessRuleAll(existing);
            }

            Allow(security, currentUser);
            Allow(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
            Allow(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

            directory.SetAccessControl(security);
            return true;
        }
        catch (Exception)
        {
            // Unsupported file system, a share that does not carry NTFS ACLs, or a folder this user
            // cannot re-permission. Never fatal — see the summary.
            return false;
        }
    }

    private static void Allow(DirectorySecurity security, SecurityIdentifier identity) =>
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
}
