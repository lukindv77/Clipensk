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
