using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedStorageCurrentSchemaV8MigrationTests
{
    [Fact]
    public async Task V7Migration_PreservesPendingStateAndCreatesEmptyDiscoveredFormatTable()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV7();
        environment.Execute("""
            INSERT INTO PendingPolicyMaintenance (
                SingletonId, OperationId, OperationKind, StateJson, CreatedAtUtc, UpdatedAtUtc)
            VALUES (
                1,
                '11111111-1111-1111-1111-111111111111',
                'ExistingMaintenance',
                '{}',
                '2026-01-01T00:00:00.0000000+00:00',
                '2026-01-01T00:00:00.0000000+00:00');
            """);

        Assert.True((await environment.ValidateAsync()).IsSuccess);
        Assert.Equal(
            ProtectedStorageDatabaseService.CurrentSchemaVersion,
            environment.Scalar("PRAGMA user_version;"));
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM PendingPolicyMaintenance WHERE OperationKind = 'ExistingMaintenance';"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ApplicationDiscoveredFormat;"));
    }

    [Fact]
    public async Task InvalidCatalog_PreventsV7Mutation()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV7();
        environment.Execute(
            "UPDATE DatabaseIdentity SET StorageId = '00000000-0000-0000-0000-000000000001';",
            catalog: true);

        Assert.Equal(
            ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity,
            (await environment.ValidateAsync()).Status);
        AssertUnmigrated(environment);
    }

    [Fact]
    public async Task MigrationSqlFailure_RollsBackVersionAndCanRetry()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV7();
        environment.Execute("""
            CREATE TABLE ApplicationDiscoveredFormat (Marker TEXT);
            INSERT INTO ApplicationDiscoveredFormat VALUES ('preserved');
            """);

        Assert.Equal(
            ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity,
            (await environment.ValidateAsync()).Status);
        AssertUnmigrated(environment, tableMustBeAbsent: false);
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM ApplicationDiscoveredFormat WHERE Marker = 'preserved';"));

        environment.Execute("DROP TABLE ApplicationDiscoveredFormat;");
        Assert.True((await environment.ValidateAsync()).IsSuccess);
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ApplicationDiscoveredFormat;"));
    }

    [Fact]
    public async Task CancellationInsideMigration_RollsBackTableAndVersionThenRetries()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV7();
        using var cancellation = new CancellationTokenSource();
        environment.Execute("""
            CREATE TRIGGER cancel_v8_migration AFTER UPDATE OF SchemaVersion ON DatabaseIdentity
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
        environment.Execute("DROP TRIGGER cancel_v8_migration;");
        Assert.True((await environment.ValidateAsync()).IsSuccess);
    }

    private static void AssertUnmigrated(
        GlobalPolicyTestEnvironment environment,
        bool tableMustBeAbsent = true)
    {
        Assert.Equal(7, environment.Scalar("SELECT SchemaVersion FROM DatabaseIdentity;"));
        Assert.Equal(7, environment.Scalar("PRAGMA user_version;"));
        if (tableMustBeAbsent)
        {
            Assert.Equal(0, environment.Scalar(
                "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ApplicationDiscoveredFormat';"));
        }
    }
}
