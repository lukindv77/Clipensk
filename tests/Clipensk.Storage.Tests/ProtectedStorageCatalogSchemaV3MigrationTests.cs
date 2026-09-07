using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedStorageCatalogSchemaV3MigrationTests
{
    [Fact]
    public async Task Validate_MigratesCatalogV2ToV3AndPreservesExternalPayloadIndex()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute("""
            INSERT INTO ExternalPayloadAddressIndex (Sha256, RelativePath, SizeBytes)
            VALUES (
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                '2026-09-01/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png',
                42);
            """, catalog: true);
        DowngradeCatalogToV2(environment);

        ProtectedStorageDatabaseResult result = await environment.ValidateAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(3, environment.Scalar(
            "SELECT SchemaVersion FROM DatabaseIdentity WHERE SingletonId = 1;",
            catalog: true));
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ArchiveSegmentIndex';",
            catalog: true));
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM ExternalPayloadAddressIndex;",
            catalog: true));
    }

    [Fact]
    public async Task Validate_InvalidCurrentDoesNotMigrateCatalogV2()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DowngradeCatalogToV2(environment);
        environment.Execute($"""
            UPDATE DatabaseIdentity
            SET StorageId = '{Guid.NewGuid():D}'
            WHERE SingletonId = 1;
            """);

        ProtectedStorageDatabaseResult result = await environment.ValidateAsync();

        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, result.Status);
        Assert.Equal(2, environment.Scalar(
            "SELECT SchemaVersion FROM DatabaseIdentity WHERE SingletonId = 1;",
            catalog: true));
        Assert.Equal(0, environment.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ArchiveSegmentIndex';",
            catalog: true));
    }

    [Fact]
    public async Task Validate_MalformedCatalogV2FailsBeforeV3Mutation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DowngradeCatalogToV2(environment);
        environment.Execute(
            "DROP INDEX UX_ExternalPayloadAddressIndex_RelativePath;",
            catalog: true);

        ProtectedStorageDatabaseResult result = await environment.ValidateAsync();

        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, result.Status);
        Assert.Equal(2, environment.Scalar(
            "SELECT SchemaVersion FROM DatabaseIdentity WHERE SingletonId = 1;",
            catalog: true));
        Assert.Equal(0, environment.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ArchiveSegmentIndex';",
            catalog: true));
    }

    [Fact]
    public async Task Validate_CancellationInsideV3MigrationRollsBackAndRetrySucceeds()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DowngradeCatalogToV2(environment);
        using var cancellation = new CancellationTokenSource();
        int catalogOpenCount = 0;
        string expectedCatalogPath = Path.GetFullPath(environment.CatalogPath);
        environment.Factory.OnOpen = (connection, _) =>
        {
            if (string.Equals(
                    Path.GetFullPath(connection.DataSource),
                    expectedCatalogPath,
                    StringComparison.OrdinalIgnoreCase) &&
                ++catalogOpenCount == 2)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            environment.ValidateAsync(cancellation.Token));

        environment.Factory.OnOpen = null;
        Assert.Equal(2, environment.Scalar(
            "SELECT SchemaVersion FROM DatabaseIdentity WHERE SingletonId = 1;",
            catalog: true));
        Assert.Equal(0, environment.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ArchiveSegmentIndex';",
            catalog: true));

        ProtectedStorageDatabaseResult retry = await environment.ValidateAsync();
        Assert.True(retry.IsSuccess);
        Assert.Equal(3, environment.Scalar(
            "SELECT SchemaVersion FROM DatabaseIdentity WHERE SingletonId = 1;",
            catalog: true));
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ArchiveSegmentIndex';",
            catalog: true));
    }

    private static void DowngradeCatalogToV2(GlobalPolicyTestEnvironment environment)
    {
        environment.Execute("""
            DROP TABLE ArchiveSegmentIndex;
            UPDATE DatabaseIdentity SET SchemaVersion = 2 WHERE SingletonId = 1;
            PRAGMA user_version = 2;
            """, catalog: true);
    }
}