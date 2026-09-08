using System.Globalization;
using System.Security.Cryptography;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedStorageCatalogReplacementServiceTests
{
    [Fact]
    public async Task ReplaceExistingCatalogAsync_DamagedCatalogPublishesReplacementAndQuarantinesExactBytes()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Session.Dispose();

        byte[] damagedCatalog = RandomNumberGenerator.GetBytes(1024);
        File.WriteAllBytes(environment.CatalogPath, damagedCatalog);

        var service = new ProtectedStorageCatalogReplacementService(environment.Factory);
        ProtectedStorageCatalogReplacementResult result = await service.ReplaceExistingCatalogAsync(
            environment.Root,
            environment.StorageId,
            environment.Key,
            DateOnly.FromDateTime(DateTime.Now));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.QuarantineRelativePath);
        string quarantinePath = Path.Combine(
            environment.Root,
            result.QuarantineRelativePath!);
        Assert.True(File.Exists(quarantinePath));
        Assert.Equal(damagedCatalog, File.ReadAllBytes(quarantinePath));
        Assert.True(File.Exists(environment.CatalogPath));
        Assert.Empty(ReplacementShadowDirectories(environment));

        ProtectedStorageDatabaseResult validation = await environment.Service.InitializeOrValidateAsync(
            environment.Root,
            environment.StorageId,
            environment.Key,
            allowInitialize: false);
        Assert.True(validation.IsSuccess);
        Assert.Equal(
            ProtectedStorageDatabaseService.CatalogSchemaVersion,
            ReadCatalogSchemaVersion(environment));
    }

    [Fact]
    public async Task ReplaceExistingCatalogAsync_MissingCatalogRefusesWithoutQuarantine()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Session.Dispose();
        File.Delete(environment.CatalogPath);

        var service = new ProtectedStorageCatalogReplacementService(environment.Factory);
        ProtectedStorageCatalogReplacementResult result = await service.ReplaceExistingCatalogAsync(
            environment.Root,
            environment.StorageId,
            environment.Key,
            DateOnly.FromDateTime(DateTime.Now));

        Assert.Equal(ProtectedStorageDatabaseStatus.MissingOrPartialStorage, result.Status);
        Assert.Null(result.QuarantineRelativePath);
        Assert.False(Directory.Exists(QuarantineDirectory(environment)));
        Assert.Empty(ReplacementShadowDirectories(environment));
    }

    [Fact]
    public async Task ReplaceExistingCatalogAsync_CancellationBeforePublicationLeavesOriginalUntouched()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Session.Dispose();
        byte[] damagedCatalog = RandomNumberGenerator.GetBytes(768);
        File.WriteAllBytes(environment.CatalogPath, damagedCatalog);

        using var cancellation = new CancellationTokenSource();
        environment.Factory.OnOpen = (connection, mode) =>
        {
            if (mode == SqliteOpenMode.ReadOnly &&
                Path.GetFileName(connection.DataSource)
                    .StartsWith(".clipensk-catalog-recovery-", StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }
        };

        var service = new ProtectedStorageCatalogReplacementService(environment.Factory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ReplaceExistingCatalogAsync(
                environment.Root,
                environment.StorageId,
                environment.Key,
                DateOnly.FromDateTime(DateTime.Now),
                cancellation.Token));

        environment.Factory.OnOpen = null;
        Assert.Equal(damagedCatalog, File.ReadAllBytes(environment.CatalogPath));
        Assert.False(Directory.Exists(QuarantineDirectory(environment)));
        Assert.Empty(ReplacementShadowDirectories(environment));
    }

    [Fact]
    public async Task ReplaceExistingCatalogAsync_CurrentChangeDuringBuildFailsClosed()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Session.Dispose();
        byte[] damagedCatalog = RandomNumberGenerator.GetBytes(640);
        File.WriteAllBytes(environment.CatalogPath, damagedCatalog);
        DateOnly day = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);
        int currentReadOnlyOpens = 0;
        bool injected = false;

        environment.Factory.OnOpen = (connection, mode) =>
        {
            if (injected ||
                mode != SqliteOpenMode.ReadOnly ||
                !string.Equals(
                    Path.GetFileName(connection.DataSource),
                    "current.db",
                    StringComparison.OrdinalIgnoreCase) ||
                Interlocked.Increment(ref currentReadOnlyOpens) != 2)
            {
                return;
            }

            injected = true;
            environment.Factory.OnOpen = null;
            SeedInlineHistory(environment, day);
        };

        var service = new ProtectedStorageCatalogReplacementService(environment.Factory);
        ProtectedStorageCatalogReplacementResult result = await service.ReplaceExistingCatalogAsync(
            environment.Root,
            environment.StorageId,
            environment.Key,
            DateOnly.FromDateTime(DateTime.Now));

        environment.Factory.OnOpen = null;
        Assert.True(injected);
        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, result.Status);
        Assert.Equal(damagedCatalog, File.ReadAllBytes(environment.CatalogPath));
        Assert.False(Directory.Exists(QuarantineDirectory(environment)));
        Assert.Empty(ReplacementShadowDirectories(environment));
    }

    [Fact]
    public async Task ReplaceExistingCatalogAsync_NewArchiveAfterAliasSnapshotFailsBeforeReplacement()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Session.Dispose();
        byte[] damagedCatalog = RandomNumberGenerator.GetBytes(896);
        File.WriteAllBytes(environment.CatalogPath, damagedCatalog);
        bool injected = false;
        string injectedArchive = Path.Combine(
            environment.Root,
            "Archive",
            "archive_000999.db");

        environment.Factory.OnOpen = (connection, mode) =>
        {
            if (injected ||
                mode != SqliteOpenMode.ReadOnly ||
                !Path.GetFileName(connection.DataSource)
                    .StartsWith(".clipensk-catalog-recovery-", StringComparison.Ordinal))
            {
                return;
            }

            injected = true;
            environment.Factory.OnOpen = null;
            File.WriteAllBytes(injectedArchive, [0x01, 0x02, 0x03]);
        };

        var service = new ProtectedStorageCatalogReplacementService(environment.Factory);
        ProtectedStorageCatalogReplacementResult result = await service.ReplaceExistingCatalogAsync(
            environment.Root,
            environment.StorageId,
            environment.Key,
            DateOnly.FromDateTime(DateTime.Now));

        environment.Factory.OnOpen = null;
        Assert.True(injected);
        Assert.Equal(ProtectedStorageDatabaseStatus.MissingOrPartialStorage, result.Status);
        Assert.Equal(damagedCatalog, File.ReadAllBytes(environment.CatalogPath));
        Assert.False(Directory.Exists(QuarantineDirectory(environment)));
        Assert.Empty(ReplacementShadowDirectories(environment));
    }

    private static void SeedInlineHistory(
        GlobalPolicyTestEnvironment environment,
        DateOnly day)
    {
        using SqliteConnection connection = environment.Factory.Open(
            environment.CurrentPath,
            environment.Key,
            SqliteOpenMode.ReadWrite);
        Guid eventId = Guid.NewGuid();
        using (SqliteCommand insertEvent = connection.CreateCommand())
        {
            insertEvent.CommandText = """
                INSERT INTO ClipboardHistoryEvent (
                    EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                    SourceApplicationId, SourceProcessId, SourceExecutablePath,
                    SourceApplicationUserModelId)
                VALUES (
                    $eventId, $eventUtc, 0, 'UTC', $calendarDate,
                    NULL, NULL, NULL, NULL);
                """;
            insertEvent.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
            insertEvent.Parameters.AddWithValue(
                "$eventUtc",
                day.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc)
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
            insertEvent.Parameters.AddWithValue(
                "$calendarDate",
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insertEvent.ExecuteNonQuery();
        }

        using SqliteCommand insertPayload = connection.CreateCommand();
        insertPayload.CommandText = """
            INSERT INTO ClipboardHistoryPayload (
                EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                InlineCanonicalText, SearchText, ExternalSha256,
                ExternalRelativePath, ExternalSizeBytes)
            VALUES (
                $eventId, 0, 'Text', 'InlineText', 1,
                'x', 'x', NULL, NULL, NULL);
            """;
        insertPayload.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
        insertPayload.ExecuteNonQuery();
    }

    private static int ReadCatalogSchemaVersion(GlobalPolicyTestEnvironment environment)
    {
        using SqliteConnection connection = environment.Factory.Open(
            environment.CatalogPath,
            environment.Key,
            SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT SchemaVersion FROM DatabaseIdentity WHERE SingletonId = 1;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string QuarantineDirectory(GlobalPolicyTestEnvironment environment) =>
        Path.Combine(environment.Root, "Current", "CatalogQuarantine");

    private static string[] ReplacementShadowDirectories(
        GlobalPolicyTestEnvironment environment) =>
        Directory.EnumerateDirectories(
                environment.Root,
                ".clipensk-catalog-replacement-*",
                SearchOption.TopDirectoryOnly)
            .ToArray();
}
