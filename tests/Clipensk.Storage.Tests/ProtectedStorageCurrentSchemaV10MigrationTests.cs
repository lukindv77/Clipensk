using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedStorageCurrentSchemaV10MigrationTests
{
    [Fact]
    public async Task V9Migration_PreservesExistingStateAndCreatesEmptyArchiveRotationTables()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV9();
        environment.Execute("""
            INSERT INTO PendingArchiveSplit (
                SingletonId, OperationId, SourceFileName, SourceDatabaseId,
                SourceCoverageStartDate, SourceCoverageEndDate, Phase, CreatedAtUtc)
            VALUES (
                1,
                '22222222-2222-2222-2222-222222222222',
                'archive_000025.db',
                '33333333-3333-3333-3333-333333333333',
                '2026-01-01',
                '2026-01-31',
                0,
                '2026-01-01T00:00:00.0000000+00:00');
            """);

        Assert.True((await environment.ValidateAsync()).IsSuccess);
        Assert.Equal(
            ProtectedStorageDatabaseService.CurrentSchemaVersion,
            environment.Scalar("PRAGMA user_version;"));
        Assert.Equal(
            ProtectedStorageDatabaseService.CurrentSchemaVersion,
            environment.Scalar("SELECT SchemaVersion FROM DatabaseIdentity;"));
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM PendingArchiveSplit WHERE SourceFileName = 'archive_000025.db';"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingArchiveRotation;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingArchiveRotationTarget;"));
    }

    [Fact]
    public async Task InvalidCatalog_PreventsV9Mutation()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV9();
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
        environment.DowngradeToV9();
        environment.Execute("""
            CREATE TABLE PendingArchiveRotation (Marker TEXT);
            INSERT INTO PendingArchiveRotation VALUES ('preserved');
            """);

        Assert.Equal(
            ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity,
            (await environment.ValidateAsync()).Status);
        AssertUnmigrated(environment, operationTableMustBeAbsent: false);
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM PendingArchiveRotation WHERE Marker = 'preserved';"));

        environment.Execute("DROP TABLE PendingArchiveRotation;");
        Assert.True((await environment.ValidateAsync()).IsSuccess);
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingArchiveRotation;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingArchiveRotationTarget;"));
    }

    [Fact]
    public async Task CancellationInsideMigration_RollsBackTablesAndVersionThenRetries()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV9();
        using var cancellation = new CancellationTokenSource();
        environment.Execute("""
            CREATE TRIGGER cancel_v10_migration AFTER UPDATE OF SchemaVersion ON DatabaseIdentity
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
        environment.Execute("DROP TRIGGER cancel_v10_migration;");
        Assert.True((await environment.ValidateAsync()).IsSuccess);
    }

    private static void AssertUnmigrated(
        GlobalPolicyTestEnvironment environment,
        bool operationTableMustBeAbsent = true)
    {
        Assert.Equal(9, environment.Scalar("SELECT SchemaVersion FROM DatabaseIdentity;"));
        Assert.Equal(9, environment.Scalar("PRAGMA user_version;"));
        if (operationTableMustBeAbsent)
        {
            Assert.Equal(0, environment.Scalar(
                "SELECT COUNT(*) FROM sqlite_master WHERE name = 'PendingArchiveRotation';"));
        }
        Assert.Equal(0, environment.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE name = 'PendingArchiveRotationTarget';"));
    }
}
