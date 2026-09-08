using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedStorageCurrentSchemaV7MigrationTests
{
    [Fact]
    public async Task V6Migration_CreatesEmptyPendingMaintenanceTable()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DowngradeToV6(environment);

        Assert.True((await environment.ValidateAsync()).IsSuccess);
        Assert.Equal(ProtectedStorageDatabaseService.CurrentSchemaVersion,
            environment.Scalar("PRAGMA user_version;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingStorageMaintenance;"));
    }

    [Fact]
    public async Task InvalidCatalog_PreventsV6Mutation()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DowngradeToV6(environment);
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
        DowngradeToV6(environment);
        environment.Execute("""
            CREATE TABLE PendingStorageMaintenance (Marker TEXT);
            INSERT INTO PendingStorageMaintenance VALUES ('preserved');
            """);

        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity,
            (await environment.ValidateAsync()).Status);
        AssertUnmigrated(environment, tableMustBeAbsent: false);
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM PendingStorageMaintenance WHERE Marker = 'preserved';"));

        environment.Execute("DROP TABLE PendingStorageMaintenance;");
        Assert.True((await environment.ValidateAsync()).IsSuccess);
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingStorageMaintenance;"));
    }

    [Fact]
    public async Task CancellationInsideMigration_RollsBackTableAndVersionThenRetries()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DowngradeToV6(environment);
        using var cancellation = new CancellationTokenSource();
        environment.Execute("""
            CREATE TRIGGER cancel_v7_migration AFTER UPDATE OF SchemaVersion ON DatabaseIdentity
            BEGIN SELECT cancel_v7(); END;
            """);
        environment.Factory.OnOpen = (connection, _) => connection.CreateFunction("cancel_v7", () =>
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

    private static void DowngradeToV6(GlobalPolicyTestEnvironment environment) => environment.Execute("""
        DROP TABLE PendingStorageMaintenance;
        UPDATE DatabaseIdentity SET SchemaVersion = 6;
        PRAGMA user_version = 6;
        """);

    private static void AssertUnmigrated(
        GlobalPolicyTestEnvironment environment,
        bool tableMustBeAbsent = true)
    {
        Assert.Equal(6, environment.Scalar("SELECT SchemaVersion FROM DatabaseIdentity;"));
        Assert.Equal(6, environment.Scalar("PRAGMA user_version;"));
        if (tableMustBeAbsent)
        {
            Assert.Equal(0, environment.Scalar(
                "SELECT COUNT(*) FROM sqlite_master WHERE name = 'PendingStorageMaintenance';"));
        }
    }
}
