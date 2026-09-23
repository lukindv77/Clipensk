using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedStorageCurrentSchemaV11MigrationTests
{
    private const string ApplicationId = "44444444-4444-4444-4444-444444444444";

    [Fact]
    public async Task V10Migration_PassesThroughV11AndPreservesExistingState()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));
        environment.DowngradeToV10();
        environment.Execute($"""
            INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
            VALUES ('{ApplicationId}', '2026-01-01T00:00:00.0000000+00:00');
            INSERT INTO ApplicationCapturePolicy (ApplicationId, CaptureRule)
            VALUES ('{ApplicationId}', 'Deny');
            """);

        Assert.True((await environment.ValidateAsync()).IsSuccess);
        Assert.Equal(12, environment.Scalar("PRAGMA user_version;"));
        Assert.Equal(12, environment.Scalar("SELECT SchemaVersion FROM DatabaseIdentity;"));
        Assert.Equal(1, environment.Scalar(
            $"SELECT COUNT(*) FROM ApplicationCapturePolicy WHERE ApplicationId = '{ApplicationId}';"));
        Assert.Equal(1, environment.Scalar(
            $"SELECT COUNT(*) FROM ApplicationGroupMembership WHERE ApplicationId = '{ApplicationId}';"));
        Assert.Equal(0, environment.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ApplicationGroupMember';"));
    }

    [Fact]
    public async Task InvalidCatalog_PreventsV10Mutation()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV10();
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
        environment.DowngradeToV10();
        environment.Execute("""
            CREATE TABLE ApplicationGroupMember (Marker TEXT);
            INSERT INTO ApplicationGroupMember VALUES ('preserved');
            """);

        Assert.Equal(
            ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity,
            (await environment.ValidateAsync()).Status);
        AssertUnmigrated(environment, groupTableMustBeAbsent: false);
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM ApplicationGroupMember WHERE Marker = 'preserved';"));

        environment.Execute("DROP TABLE ApplicationGroupMember;");
        Assert.True((await environment.ValidateAsync()).IsSuccess);
        Assert.Equal(12, environment.Scalar("PRAGMA user_version;"));
        Assert.Equal(0, environment.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ApplicationGroupMember';"));
    }

    [Fact]
    public async Task CancellationInsideMigration_RollsBackTableAndVersionThenRetries()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV10();
        using var cancellation = new CancellationTokenSource();
        environment.Execute("""
            CREATE TRIGGER cancel_v11_migration AFTER UPDATE OF SchemaVersion ON DatabaseIdentity
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
        environment.Execute("DROP TRIGGER cancel_v11_migration;");
        Assert.True((await environment.ValidateAsync()).IsSuccess);
    }

    [Theory]
    [InlineData("DROP INDEX IX_ApplicationGroupMember_ParentApplicationId;")]
    [InlineData("""
        DROP TABLE ApplicationGroupMember;
        CREATE TABLE ApplicationGroupMember (
            ApplicationId TEXT NOT NULL PRIMARY KEY,
            ParentApplicationId TEXT NOT NULL,
            RetainedFromApplicationId TEXT NULL,
            JoinedAtUtc TEXT NOT NULL,
            FOREIGN KEY (ApplicationId) REFERENCES ApplicationIdentity(ApplicationId) ON DELETE CASCADE,
            FOREIGN KEY (ParentApplicationId) REFERENCES ApplicationIdentity(ApplicationId),
            FOREIGN KEY (RetainedFromApplicationId) REFERENCES ApplicationIdentity(ApplicationId));
        CREATE INDEX IX_ApplicationGroupMember_ParentApplicationId ON ApplicationGroupMember(ParentApplicationId);
        """)]
    [InlineData("""
        DROP TABLE ApplicationGroupMember;
        CREATE TABLE ApplicationGroupMember (
            ApplicationId TEXT NOT NULL PRIMARY KEY,
            ParentApplicationId TEXT NOT NULL,
            JoinedAtUtc TEXT NOT NULL,
            FOREIGN KEY (ApplicationId) REFERENCES ApplicationIdentity(ApplicationId),
            FOREIGN KEY (ParentApplicationId) REFERENCES ApplicationIdentity(ApplicationId));
        CREATE INDEX IX_ApplicationGroupMember_ParentApplicationId ON ApplicationGroupMember(ParentApplicationId);
        """)]
    public async Task TamperedV11GroupTableContract_FailsValidationBeforeMigrating(string tamper)
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV11();
        environment.Execute(tamper);

        Assert.Equal(
            ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity,
            (await environment.ValidateAsync()).Status);
        Assert.Equal(11, environment.Scalar("PRAGMA user_version;"));
    }

    private static void AssertUnmigrated(
        GlobalPolicyTestEnvironment environment,
        bool groupTableMustBeAbsent = true)
    {
        Assert.Equal(10, environment.Scalar("SELECT SchemaVersion FROM DatabaseIdentity;"));
        Assert.Equal(10, environment.Scalar("PRAGMA user_version;"));
        if (groupTableMustBeAbsent)
        {
            Assert.Equal(0, environment.Scalar(
                "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ApplicationGroupMember';"));
        }
    }
}
