using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedStorageCurrentSchemaV7MigrationTests
{
    [Fact]
    public async Task V6Migration_PreservesExistingStateAndCreatesEmptyMaintenanceTable()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Allow,
            new Dictionary<string, ClipboardFormatCapturePolicy>
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Allow, 4096),
            }));
        environment.Execute("""
            INSERT INTO CustomBinaryFormatConfiguration (FormatName, FileExtension)
            VALUES ('Example.Custom', '.payload');
            """);
        environment.DowngradeToV6();

        Assert.True((await environment.ValidateAsync()).IsSuccess);
        Assert.Equal(ProtectedStorageDatabaseService.CurrentSchemaVersion,
            environment.Scalar("PRAGMA user_version;"));
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM CustomBinaryFormatConfiguration WHERE FormatName = 'Example.Custom';"));
        Assert.Equal(0, environment.Scalar(
            "SELECT COUNT(*) FROM GlobalCapturePolicyMaintenance;"));
        ClipboardCapturePolicy? policy = await environment.Repository.ReadAsync();
        Assert.NotNull(policy);
        Assert.Equal(4096, policy.Formats["Text"].MaxBytes);
    }

    [Fact]
    public async Task InvalidCatalog_PreventsV6Mutation()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV6();
        environment.Execute(
            "UPDATE DatabaseIdentity SET StorageId = '00000000-0000-0000-0000-000000000001';",
            catalog: true);

        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity,
            (await environment.ValidateAsync()).Status);
        AssertUnmigrated(environment);
    }

    [Fact]
    public async Task MigrationSqlFailure_RollsBackVersionAndCanRetry()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV6();
        environment.Execute("""
            CREATE TABLE GlobalCapturePolicyMaintenance (Marker TEXT);
            INSERT INTO GlobalCapturePolicyMaintenance VALUES ('preserved');
            """);

        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity,
            (await environment.ValidateAsync()).Status);
        AssertUnmigrated(environment, tableMustBeAbsent: false);
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM GlobalCapturePolicyMaintenance WHERE Marker = 'preserved';"));

        environment.Execute("DROP TABLE GlobalCapturePolicyMaintenance;");
        Assert.True((await environment.ValidateAsync()).IsSuccess);
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM GlobalCapturePolicyMaintenance;"));
    }

    [Fact]
    public async Task CancellationInsideMigration_RollsBackTableAndVersionThenRetries()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV6();
        using var cancellation = new CancellationTokenSource();
        environment.Execute("""
            CREATE TRIGGER cancel_v7_migration AFTER UPDATE OF SchemaVersion ON DatabaseIdentity
            BEGIN SELECT cancel_setup(); END;
            """);
        environment.Factory.OnOpen = (connection, _) => connection.CreateFunction("cancel_setup", () =>
        {
            cancellation.Cancel();
            return 0;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            environment.ValidateAsync(cancellation.Token));
        environment.Factory.OnOpen = null;
        AssertUnmigrated(environment);
        environment.Execute("DROP TRIGGER cancel_v7_migration;");
        Assert.True((await environment.ValidateAsync()).IsSuccess);
    }

    [Fact]
    public async Task MalformedV7MaintenanceSchema_IsRejectedFailClosed()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute("""
            DROP TABLE GlobalCapturePolicyMaintenance;
            CREATE TABLE GlobalCapturePolicyMaintenance (
                SingletonId INTEGER PRIMARY KEY,
                OperationId TEXT,
                Phase TEXT,
                StartedAtUtc TEXT);
            """);

        Assert.Equal(
            ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity,
            (await environment.ValidateAsync()).Status);
    }

    private static void AssertUnmigrated(
        GlobalPolicyTestEnvironment environment,
        bool tableMustBeAbsent = true)
    {
        Assert.Equal(6, environment.Scalar("SELECT SchemaVersion FROM DatabaseIdentity;"));
        Assert.Equal(6, environment.Scalar("PRAGMA user_version;"));
        if (tableMustBeAbsent)
        {
            Assert.Equal(0, environment.Scalar(
                "SELECT COUNT(*) FROM sqlite_master WHERE name = 'GlobalCapturePolicyMaintenance';"));
        }
    }
}