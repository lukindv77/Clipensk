using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedStorageCatalogRecoveryServiceTests
{
    [Fact]
    public async Task RecoverMissingCatalogAsync_RebuildsBothProjectionsBeforeNormalValidation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        DateOnly archiveDay = today.AddDays(-5);
        DateOnly currentDay = today.AddDays(-1);
        var archiveFileName = new ArchiveFileName(401, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(
            environment.Session,
            environment.Factory);
        await archiveService.CreateAsync(
            archiveFileName,
            new JournalDateRange(archiveDay, archiveDay));

        ExternalPayloadAddress currentAddress = Address('a', currentDay, 11);
        ExternalPayloadAddress archiveAddress = Address('b', archiveDay, 22);
        SeedExternalHistory(
            environment,
            environment.CurrentPath,
            currentDay,
            currentAddress);
        SeedExternalHistory(
            environment,
            ArchivePath(environment, archiveFileName),
            archiveDay,
            archiveAddress);

        environment.Session.Dispose();
        File.Delete(environment.CatalogPath);

        var recovery = new ProtectedStorageCatalogRecoveryService(environment.Factory);
        ProtectedStorageDatabaseResult result = await recovery.RecoverMissingCatalogAsync(
            environment.Root,
            environment.StorageId,
            environment.Key,
            today);

        Assert.True(result.IsSuccess);
        Assert.False(result.WasInitialized);
        Assert.True(File.Exists(environment.CatalogPath));
        Assert.Equal(
            [currentAddress, archiveAddress],
            ReadExternalAddresses(environment)
                .OrderBy(item => item.Sha256, StringComparer.Ordinal)
                .ToArray());

        ArchiveSegmentDescriptor segment = Assert.Single(ReadArchiveSegments(environment));
        Assert.Equal(archiveFileName.FileName, segment.FileName);
        Assert.Equal(new JournalDateRange(archiveDay, archiveDay), segment.Coverage);
        Assert.True(segment.IsSealed);

        ProtectedStorageDatabaseResult normalValidation =
            await environment.Service.InitializeOrValidateAsync(
                environment.Root,
                environment.StorageId,
                environment.Key,
                allowInitialize: false);
        Assert.True(normalValidation.IsSuccess);
        Assert.False(normalValidation.WasInitialized);
    }

    [Fact]
    public async Task RecoverMissingCatalogAsync_ExistingCatalogRefusesWithoutReplacingIt()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        Guid before = ReadCatalogDatabaseId(environment);

        var recovery = new ProtectedStorageCatalogRecoveryService(environment.Factory);
        ProtectedStorageDatabaseResult result = await recovery.RecoverMissingCatalogAsync(
            environment.Root,
            environment.StorageId,
            environment.Key,
            DateOnly.FromDateTime(DateTime.Now));

        Assert.Equal(ProtectedStorageDatabaseStatus.MissingOrPartialStorage, result.Status);
        Assert.Equal(before, ReadCatalogDatabaseId(environment));
    }

    [Fact]
    public async Task RecoverMissingCatalogAsync_OverlappingArchiveCoverageFailsBeforePublication()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = DateOnly.FromDateTime(DateTime.Now).AddDays(-6);
        var archiveService = new ProtectedArchiveDatabaseService(
            environment.Session,
            environment.Factory);
        await archiveService.CreateAsync(
            new ArchiveFileName(402, ArchiveFileName.NoSplit),
            new JournalDateRange(day, day));
        await archiveService.CreateAsync(
            new ArchiveFileName(403, ArchiveFileName.NoSplit),
            new JournalDateRange(day, day));

        environment.Session.Dispose();
        File.Delete(environment.CatalogPath);

        var recovery = new ProtectedStorageCatalogRecoveryService(environment.Factory);
        ProtectedStorageDatabaseResult result = await recovery.RecoverMissingCatalogAsync(
            environment.Root,
            environment.StorageId,
            environment.Key,
            DateOnly.FromDateTime(DateTime.Now));

        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, result.Status);
        Assert.False(File.Exists(environment.CatalogPath));
        Assert.Empty(RecoveryStagingFiles(environment));
    }

    [Fact]
    public async Task RecoverMissingCatalogAsync_ConflictingExternalAddressFailsBeforePublication()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = DateOnly.FromDateTime(DateTime.Now).AddDays(-7);
        var archiveFileName = new ArchiveFileName(404, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(
            environment.Session,
            environment.Factory);
        await archiveService.CreateAsync(
            archiveFileName,
            new JournalDateRange(day, day));

        ExternalPayloadAddress current = Address('c', day, 33);
        ExternalPayloadAddress conflicting = current with
        {
            RelativePath = Path.Combine(
                day.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                current.Sha256 + ".png"),
        };
        SeedExternalHistory(environment, environment.CurrentPath, day, current);
        SeedExternalHistory(
            environment,
            ArchivePath(environment, archiveFileName),
            day,
            conflicting);

        environment.Session.Dispose();
        File.Delete(environment.CatalogPath);

        var recovery = new ProtectedStorageCatalogRecoveryService(environment.Factory);
        ProtectedStorageDatabaseResult result = await recovery.RecoverMissingCatalogAsync(
            environment.Root,
            environment.StorageId,
            environment.Key,
            DateOnly.FromDateTime(DateTime.Now));

        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, result.Status);
        Assert.False(File.Exists(environment.CatalogPath));
        Assert.Empty(RecoveryStagingFiles(environment));
    }

    [Fact]
    public async Task RecoverMissingCatalogAsync_CancellationBeforePublicationLeavesPairPartial()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);
        SeedExternalHistory(environment, environment.CurrentPath, day, Address('d', day, 44));
        environment.Session.Dispose();
        File.Delete(environment.CatalogPath);

        using var cancellation = new CancellationTokenSource();
        int currentReadOnlyOpens = 0;
        environment.Factory.OnOpen = (connection, mode) =>
        {
            if (mode == SqliteOpenMode.ReadOnly &&
                string.Equals(
                    Path.GetFileName(connection.DataSource),
                    "current.db",
                    StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Increment(ref currentReadOnlyOpens) == 2)
            {
                cancellation.Cancel();
            }
        };

        var recovery = new ProtectedStorageCatalogRecoveryService(environment.Factory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            recovery.RecoverMissingCatalogAsync(
                environment.Root,
                environment.StorageId,
                environment.Key,
                DateOnly.FromDateTime(DateTime.Now),
                cancellation.Token));

        environment.Factory.OnOpen = null;
        Assert.False(File.Exists(environment.CatalogPath));
        Assert.Empty(RecoveryStagingFiles(environment));
    }

    [Fact]
    public async Task RecoverMissingCatalogAsync_SourceChangeBetweenSnapshotsFailsClosed()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);
        SeedExternalHistory(environment, environment.CurrentPath, day, Address('e', day, 55));
        environment.Session.Dispose();
        File.Delete(environment.CatalogPath);

        bool injected = false;
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
            SeedExternalHistory(
                environment,
                environment.CurrentPath,
                day,
                Address('f', day, 66));
        };

        var recovery = new ProtectedStorageCatalogRecoveryService(environment.Factory);
        ProtectedStorageDatabaseResult result = await recovery.RecoverMissingCatalogAsync(
            environment.Root,
            environment.StorageId,
            environment.Key,
            DateOnly.FromDateTime(DateTime.Now));

        Assert.True(injected);
        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, result.Status);
        Assert.False(File.Exists(environment.CatalogPath));
        Assert.Empty(RecoveryStagingFiles(environment));
    }

    private static ExternalPayloadAddress Address(char value, DateOnly day, long size)
    {
        string sha = new(value, 64);
        return new ExternalPayloadAddress(
            sha,
            Path.Combine(
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                sha + ".png"),
            size);
    }

    private static void SeedExternalHistory(
        GlobalPolicyTestEnvironment environment,
        string databasePath,
        DateOnly day,
        ExternalPayloadAddress address)
    {
        using SqliteConnection connection = environment.Factory.Open(
            databasePath,
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
                    .ToString("O", CultureInfo.InvariantCulture));
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
                $eventId, 0, 'Bitmap', 'PngImage', $canonicalByteCount,
                NULL, NULL, $sha256, $relativePath, $sizeBytes);
            """;
        insertPayload.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
        insertPayload.Parameters.AddWithValue("$canonicalByteCount", address.SizeBytes);
        insertPayload.Parameters.AddWithValue("$sha256", address.Sha256);
        insertPayload.Parameters.AddWithValue("$relativePath", address.RelativePath);
        insertPayload.Parameters.AddWithValue("$sizeBytes", address.SizeBytes);
        insertPayload.ExecuteNonQuery();
    }

    private static Guid ReadCatalogDatabaseId(GlobalPolicyTestEnvironment environment)
    {
        using SqliteConnection connection = environment.Factory.Open(
            environment.CatalogPath,
            environment.Key,
            SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DatabaseId FROM DatabaseIdentity WHERE SingletonId = 1;";
        return Guid.Parse((string)command.ExecuteScalar()!);
    }

    private static ExternalPayloadAddress[] ReadExternalAddresses(
        GlobalPolicyTestEnvironment environment)
    {
        using SqliteConnection connection = environment.Factory.Open(
            environment.CatalogPath,
            environment.Key,
            SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT Sha256, RelativePath, SizeBytes
            FROM ExternalPayloadAddressIndex
            ORDER BY Sha256 COLLATE BINARY;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<ExternalPayloadAddress>();
        while (reader.Read())
        {
            result.Add(new ExternalPayloadAddress(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2)));
        }
        return result.ToArray();
    }

    private static ArchiveSegmentDescriptor[] ReadArchiveSegments(
        GlobalPolicyTestEnvironment environment)
    {
        using SqliteConnection connection = environment.Factory.Open(
            environment.CatalogPath,
            environment.Key,
            SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DatabaseId, FileName, CoverageStartDate, CoverageEndDate, IsSealed
            FROM ArchiveSegmentIndex
            ORDER BY FileName COLLATE BINARY;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<ArchiveSegmentDescriptor>();
        while (reader.Read())
        {
            result.Add(new ArchiveSegmentDescriptor(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                new JournalDateRange(
                    DateOnly.ParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    DateOnly.ParseExact(reader.GetString(3), "yyyy-MM-dd", CultureInfo.InvariantCulture)),
                reader.GetInt32(4) == 1));
        }
        return result.ToArray();
    }

    private static string[] RecoveryStagingFiles(
        GlobalPolicyTestEnvironment environment) =>
        Directory.EnumerateFiles(
                Path.Combine(environment.Root, "Current"),
                ".clipensk-catalog-recovery-*.tmp",
                SearchOption.TopDirectoryOnly)
            .ToArray();

    private static string ArchivePath(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName fileName) =>
        Path.Combine(environment.Root, "Archive", fileName.FileName);
}
