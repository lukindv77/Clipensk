using System.Security.AccessControl;
using System.Security.Principal;

namespace Clipensk.Windows.Security;

public enum DataRootProtectionResult
{
    /// <summary>Only this Windows user, SYSTEM and Administrators can now reach the data root.</summary>
    Protected,

    /// <summary>The volume cannot express access rules (FAT32/exFAT, some network shares).</summary>
    Unsupported,

    /// <summary>
    /// The directory already existed and holds files Clipensk did not put there, so its access
    /// rules were left alone.
    /// </summary>
    SkippedNonEmptyDirectory,
}

/// <summary>
/// Restricts a configured data root to the Windows user who configured it, per explicit product
/// decision: another user of the same computer must not reach this user's storage. The databases
/// are already SQLCipher-encrypted, so this is not what keeps their contents secret — it is what
/// stops another account from reading, replacing or deleting the files at all.
///
/// Applied when a data root is configured, not on every start: rewriting the access rules of a
/// folder the user chose, behind their back, on every launch would be a surprising thing for a
/// resident app to do to a directory it does not exclusively own.
/// </summary>
public static class WindowsDataRootProtectionService
{
    /// <summary>
    /// Grants the current user, SYSTEM and Administrators full control of <paramref name="dataRootPath"/>
    /// and removes everything else, including inherited access. SYSTEM and Administrators are kept
    /// deliberately: dropping them does not increase privacy on a machine where an administrator
    /// can take ownership anyway, but it does break backup, antivirus and repair tooling.
    ///
    /// Only a directory Clipensk creates here, or one that is already empty, is re-permissioned.
    /// Pointing Clipensk at a populated folder must never strip other people's access to whatever
    /// else lives there — that is somebody's unrelated data, and the rules are applied to the whole
    /// subtree. Callers surface the outcome instead of failing: the chosen location stays usable
    /// either way, because its databases are encrypted regardless.
    /// </summary>
    public static DataRootProtectionResult Protect(string dataRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);

        try
        {
            var directory = new DirectoryInfo(dataRootPath);
            bool ownsDirectory = !directory.Exists;
            directory.Create();

            if (!ownsDirectory && directory.EnumerateFileSystemInfos().Any())
            {
                return DataRootProtectionResult.SkippedNonEmptyDirectory;
            }

            SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is null)
            {
                return DataRootProtectionResult.Unsupported;
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
            return DataRootProtectionResult.Protected;
        }
        catch (Exception)
        {
            // Unsupported file system, a share that does not carry NTFS access rules, or a folder
            // this user cannot re-permission. Never fatal — see the summary.
            return DataRootProtectionResult.Unsupported;
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
