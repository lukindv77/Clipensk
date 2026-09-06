using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedStorageCurrentSchemaV5MigrationTests
{
    [Fact]
    public async Task NewStorage_CreatesV5AndCatalogV2WithoutPolicyDefaults()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        Assert.Equal(5, environment.Scalar("SELECT SchemaVersion FROM DatabaseIdentity;"));
        Assert.Equal(5, environment.Scalar("PRAGMA user_version;"));
        Assert.Equal(2, environment.Scalar("PRAGMA user_version;", catalog: true));
        Assert.Null(await environment.Repository.ReadAsync());
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'GlobalCapturePolicy';", catalog: true));
    }

    [Fact]
    public async Task V4Migration_PreservesHistoryAndApplicationPolicyAndCanBeRepeated()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV4();
        environment.Execute("""
            INSERT INTO ApplicationIdentity VALUES ('00000000-0000-0000-0000-000000000001', '2026-09-06T00:00:00.0000000+00:00');
            INSERT INTO ApplicationCapturePolicy VALUES ('00000000-0000-0000-0000-000000000001', 'Allow');
            INSERT INTO ApplicationFormatCapturePolicy VALUES ('00000000-0000-0000-0000-000000000001', 'Text', 'Allow', 1024);
            INSERT INTO ClipboardHistoryEvent (EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate, SourceApplicationId)
            VALUES ('00000000-0000-0000-0000-000000000002', '2026-09-06T00:00:00.0000000+00:00', 0, 'UTC', '2026-09-06', '00000000-0000-0000-0000-000000000001');
            INSERT INTO ClipboardHistoryPayload (EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount, InlineCanonicalText)
            VALUES ('00000000-0000-0000-0000-000000000002', 0, 'Text', 'Text', 6, 'marker');
            """);
        Assert.True((await environment.ValidateAsync()).IsSuccess);
        Assert.Equal(5, environment.Scalar("PRAGMA user_version;"));
        Assert.Null(await environment.Repository.ReadAsync());
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE InlineCanonicalText = 'marker';"));
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ApplicationFormatCapturePolicy WHERE MaxBytes = 1024;"));
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent WHERE SourceApplicationId = '00000000-0000-0000-0000-000000000001';"));
        await environment.Repository.InitializeAsync(new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny));
        Assert.True((await environment.ValidateAsync()).IsSuccess);
        Assert.Equal(ClipboardCapturePolicyRule.Deny, (await environment.Repository.ReadAsync())!.Capture);
    }

    [Fact]
    public async Task InvalidCatalog_PreventsV4Mutation()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV4();
        environment.Execute("UPDATE DatabaseIdentity SET StorageId = '00000000-0000-0000-0000-000000000001';", catalog: true);
        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, (await environment.ValidateAsync()).Status);
        AssertUnmigrated(environment);
    }

    [Fact]
    public async Task MalformedV4History_PreventsBothCurrentAndCatalogMigration()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV4();
        environment.Execute("DROP INDEX IX_ClipboardHistoryPayload_FormatName;");
        environment.Execute("DROP TABLE ExternalPayloadAddressIndex; UPDATE DatabaseIdentity SET SchemaVersion = 1; PRAGMA user_version = 1;", catalog: true);
        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, (await environment.ValidateAsync()).Status);
        AssertUnmigrated(environment);
        Assert.Equal(1, environment.Scalar("PRAGMA user_version;", catalog: true));
    }

    [Fact]
    public async Task MigrationSqlFailure_RollsBackTablesAndVersionThenRetries()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV4();
        environment.Execute("CREATE TABLE GlobalFormatCapturePolicy (Marker TEXT); INSERT INTO GlobalFormatCapturePolicy VALUES ('preserved');");
        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, (await environment.ValidateAsync()).Status);
        AssertUnmigrated(environment);
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM GlobalFormatCapturePolicy WHERE Marker = 'preserved';"));
        environment.Execute("DROP TABLE GlobalFormatCapturePolicy;");
        Assert.True((await environment.ValidateAsync()).IsSuccess);
        Assert.Null(await environment.Repository.ReadAsync());
    }

    [Fact]
    public async Task CancellationInsideMigration_RollsBackTablesAndVersionThenRetries()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV4();
        using var cancellation = new CancellationTokenSource();
        environment.Execute("""
            CREATE TRIGGER cancel_migration AFTER UPDATE OF SchemaVersion ON DatabaseIdentity
            BEGIN SELECT cancel_setup(); END;
            """);
        environment.Factory.OnOpen = (connection, _) => connection.CreateFunction("cancel_setup", () =>
        {
            cancellation.Cancel();
            return 0;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => environment.ValidateAsync(cancellation.Token));
        environment.Factory.OnOpen = null;
        AssertUnmigrated(environment);
        environment.Execute("DROP TRIGGER cancel_migration;");
        Assert.True((await environment.ValidateAsync()).IsSuccess);
    }

    [Fact]
    public async Task MalformedV5PolicySchema_PreventsLegacyCatalogMigration()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute("ALTER TABLE GlobalCapturePolicy ADD COLUMN Unexpected TEXT;");
        environment.Execute("DROP TABLE ExternalPayloadAddressIndex; UPDATE DatabaseIdentity SET SchemaVersion = 1; PRAGMA user_version = 1;", catalog: true);
        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, (await environment.ValidateAsync()).Status);
        Assert.Equal(1, environment.Scalar("PRAGMA user_version;", catalog: true));
    }

    private static void AssertUnmigrated(GlobalPolicyTestEnvironment environment)
    {
        Assert.Equal(4, environment.Scalar("SELECT SchemaVersion FROM DatabaseIdentity;"));
        Assert.Equal(4, environment.Scalar("PRAGMA user_version;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE name = 'GlobalCapturePolicy';"));
    }
}
