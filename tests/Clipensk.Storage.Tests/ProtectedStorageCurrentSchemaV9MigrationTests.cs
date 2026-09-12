using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedStorageCurrentSchemaV9MigrationTests
{
    [Fact]
    public async Task V8Migration_PreservesExistingStateAndCreatesEmptyArchiveSplitTables()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV8();
        environment.Execute("""
            INSERT INTO ApplicationDiscoveredFormat (
                ApplicationId, FormatId, FormatName, FirstSeenAtUtc, LastSeenAtUtc)
            VALUES (
                '11111111-1111-1111-1111-111111111111',
                70001,
                'ExistingFormat',
                '2026-01-01T00:00:00.0000000+00:00',
                '2026-01-01T00:00:00.0000000+00:00');
            """);

        Assert.True((await environment.ValidateAsync()).IsSuccess);
        Assert.Equal(
            ProtectedStorageDatabaseService.CurrentSchemaVersion,
            environment.Scalar("PRAGMA user_version;"));
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM ApplicationDiscoveredFormat WHERE FormatName = 'ExistingFormat';"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingArchiveSplit;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingArchiveSplitSegment;"));
    }

    [Fact]
    public async Task InvalidCatalog_PreventsV8Mutation()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV8();
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
        environment.DowngradeToV8();
        environment.Execute("""
            CREATE TABLE PendingArchiveSplit (Marker TEXT);
            INSERT INTO PendingArchiveSplit VALUES ('preserved');
            """);

        Assert.Equal(
            ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity,
            (await environment.ValidateAsync()).Status);
        AssertUnmigrated(environment, operationTableMustBeAbsent: false);
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM PendingArchiveSplit WHERE Marker = 'preserved';"));

        environment.Execute("DROP TABLE PendingArchiveSplit;");
        Assert.True((await environment.ValidateAsync()).IsSuccess);
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingArchiveSplit;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingArchiveSplitSegment;"));
    }

    [Fact]
    public async Task CancellationInsideMigration_RollsBackTablesAndVersionThenRetries()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV8();
        using var cancellation = new CancellationTokenSource();
        environment.Execute("""
            CREATE TRIGGER cancel_v9_migration AFTER UPDATE OF SchemaVersion ON DatabaseIdentity
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
        environment.Execute("DROP TRIGGER cancel_v9_migration;");
        Assert.True((await environment.ValidateAsync()).IsSuccess);
    }

    private static void AssertUnmigrated(
        GlobalPolicyTestEnvironment environment,
        bool operationTableMustBeAbsent = true)
    {
        Assert.Equal(8, environment.Scalar("SELECT SchemaVersion FROM DatabaseIdentity;"));
        Assert.Equal(8, environment.Scalar("PRAGMA user_version;"));
        if (operationTableMustBeAbsent)
        {
            Assert.Equal(0, environment.Scalar(
                "SELECT COUNT(*) FROM sqlite_master WHERE name = 'PendingArchiveSplit';"));
        }
        Assert.Equal(0, environment.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE name = 'PendingArchiveSplitSegment';"));
    }
}
