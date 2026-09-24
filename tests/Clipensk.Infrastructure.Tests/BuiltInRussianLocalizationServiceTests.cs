using Clipensk.Infrastructure.Localization;
using Xunit;

namespace Clipensk.Infrastructure.Tests;

public sealed class BuiltInRussianLocalizationServiceTests
{
    private static readonly string[] MaintenanceAndRecoveryKeys =
    [
        "Maintenance.Vacuum",
        "Maintenance.Optimize",
        "Maintenance.VacuumCompleted",
        "Maintenance.OptimizeCompleted",
        "Maintenance.VacuumFailed",
        "Maintenance.OptimizeFailed",
        "Maintenance.CurrentVacuum",
        "Maintenance.CurrentOptimize",
        "Maintenance.CurrentVacuumCompleted",
        "Maintenance.CurrentOptimizeCompleted",
        "Maintenance.CurrentVacuumFailed",
        "Maintenance.CurrentOptimizeFailed",
        "Lock.CatalogRecovery.NotNeeded",
        "Lock.CatalogRecovery.Completed",
        "Lock.CatalogRecovery.Failed",
        "Lock.CatalogRecovery.Running",
        "Lock.CatalogRecovery.CheckAction",
        "Lock.CatalogRecovery.RecoverAction",
        "Lock.CatalogRecovery.ReplaceTitle",
        "Lock.CatalogRecovery.RecoverTitle",
        "Lock.CatalogRecovery.ReplaceBody",
        "Lock.CatalogRecovery.RecoverBody",
        "Lock.CatalogRecovery.ReplaceAction",
        "Lock.CatalogRecovery.Cancel",
        "Lock.CurrentMissing",
        "Lock.CurrentMissing.PasswordChangeCopy",
        "Lock.CurrentRestart.Action",
        "Lock.CurrentRestart.Title",
        "Lock.CurrentRestart.Body",
        "Lock.CurrentRestart.Confirm",
        "Lock.CurrentRestart.Running",
        "Lock.CurrentRestart.Completed",
        "Lock.CurrentRestart.CatalogFailed",
        "Lock.CurrentRestart.Failed",
        "Lock.PasswordChangeIncomplete",
        "PasswordChange.Start",
        "PasswordChange.Title",
        "PasswordChange.Body",
        "PasswordChange.Current",
        "PasswordChange.New",
        "PasswordChange.Confirm",
        "PasswordChange.Hint",
        "PasswordChange.Apply",
        "PasswordChange.Cancel",
        "PasswordChange.Mismatch",
        "PasswordChange.WrongCurrent",
        "PasswordChange.Same",
        "PasswordChange.LockFailed",
        "PasswordChange.ProgressTitle",
        "PasswordChange.Preparing",
        "PasswordChange.Progress",
        "PasswordChange.Switching",
        "PasswordChange.Cancelling",
        "PasswordChange.Completed",
        "PasswordChange.Cancelled",
        "PasswordChange.Failed",
        "PasswordChange.Refused.Pending",
        "PasswordChange.Refused.Space",
        "PasswordChange.Refused.Busy",
        "PasswordChange.Refused.Database",
        "Startup.UnsupportedWindows",
        "Crash.Startup",
        "Crash.Unhandled",
        "Crash.LogUnavailable",
        "Backup.Start",
        "Backup.ConfirmTitle",
        "Backup.ConfirmBody",
        "Backup.Confirm",
        "Backup.ProgressTitle",
        "Backup.Verifying",
        "Backup.LockFailed",
        "Backup.Completed",
        "Backup.ProtectionUnsupported",
        "Backup.Cancelled",
        "Backup.CancelledLeftover",
        "Backup.Failed",
        "Backup.FailedLeftover",
        "Backup.Refused.RelocationPending",
        "Backup.Refused.DataRootNotConfigured",
        "Backup.Refused.SourceMissing",
        "Backup.Refused.SourceNotStorage",
        "Backup.Refused.SourceContainsLink",
        "Backup.Refused.NameCollision",
        "Backup.Refused.TargetNested",
        "Backup.Refused.TargetIsFile",
        "Backup.Refused.TargetIsLink",
        "Backup.Refused.TargetMissing",
        "Backup.Refused.TargetNotEmpty",
        "Backup.Refused.InsufficientSpace",
        "Backup.Refused.SourceBusy",
        "OpenDataRoot.Start",
        "OpenDataRoot.ConfirmTitle",
        "OpenDataRoot.ConfirmBody",
        "OpenDataRoot.Confirm",
        "OpenDataRoot.LockFailed",
        "OpenDataRoot.Completed",
        "OpenDataRoot.ProtectionSkipped",
        "OpenDataRoot.Failed",
        "OpenDataRoot.Refused.RelocationPending",
        "OpenDataRoot.Refused.TargetMissing",
        "OpenDataRoot.Refused.TargetIsFile",
        "OpenDataRoot.Refused.TargetIsLink",
        "OpenDataRoot.Refused.TargetSameAsSource",
        "OpenDataRoot.Refused.TargetNested",
        "OpenDataRoot.Refused.TargetIncompleteBackup",
        "OpenDataRoot.Refused.TargetNotStorage",
        "OpenDataRoot.Refused.Unreadable",
    ];

    [Theory]
    [MemberData(nameof(MaintenanceAndRecoveryKeyData))]
    public void GetString_MaintenanceAndRecoveryKeysHaveBuiltInRussianValue(string key)
    {
        var service = new BuiltInRussianLocalizationService();

        string value = service.GetString(key);

        Assert.False(string.IsNullOrWhiteSpace(value));
        Assert.NotEqual(key, value);
    }

    public static IEnumerable<object[]> MaintenanceAndRecoveryKeyData =>
        MaintenanceAndRecoveryKeys.Select(key => new object[] { key });
}
