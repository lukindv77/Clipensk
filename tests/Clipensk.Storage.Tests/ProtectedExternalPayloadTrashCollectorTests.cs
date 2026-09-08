using System.Globalization;
using System.Security.Cryptography;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedExternalPayloadTrashCollectorTests
{
    [Fact]
    public async Task CollectAsync_StaleCatalogReservationMovesVerifiedOrphanToTrash()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly storedDate = ClosedDay(4);
        DateOnly deletionDate = ClosedDay(0);
        byte[] bytes = [1, 2, 3, 4, 5];
        ExternalPayloadAddress address = AddressForBytes(storedDate, bytes);
        string sourcePath = WriteManagedFile(environment, address, bytes);
        SeedCatalogAddress(environment, address);

        var collector = new ProtectedExternalPayloadTrashCollector(
            environment.Session,
            environment.Factory);
        ExternalPayloadTrashCollectionResult result = await collector.CollectAsync(deletionDate);

        string trashRelativePath = TrashRelativePath(deletionDate, address.RelativePath);
        string trashPath = Path.Combine(environment.Root, trashRelativePath);
        Assert.Equal(1, result.CollectedFileCount);
        Assert.Equal([trashRelativePath], result.TrashRelativePaths);
        Assert.False(File.Exists(sourcePath));
        Assert.Equal(bytes, File.ReadAllBytes(trashPath));
        Assert.Empty(ReadCatalogAddresses(environment));
    }

    [Fact]
    public async Task CollectAsync_LiveCurrentReferenceKeepsPhysicalFile()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly storedDate = ClosedDay(3);
        byte[] bytes = [10, 20, 30, 40];
        ExternalPayloadAddress address = AddressForBytes(storedDate, bytes);
        string sourcePath = WriteManagedFile(environment, address, bytes);
        SeedExternalHistory(environment, environment.CurrentPath, storedDate, address);

        var collector = new ProtectedExternalPayloadTrashCollector(
            environment.Session,
            environment.Factory);
        ExternalPayloadTrashCollectionResult result = await collector.CollectAsync(ClosedDay(0));

        Assert.Equal(0, result.CollectedFileCount);
        Assert.True(File.Exists(sourcePath));
        Assert.Equal([address], ReadCatalogAddresses(environment));
    }

    [Fact]
    public async Task CollectAsync_LiveArchiveReferenceKeepsPhysicalFile()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly storedDate = ClosedDay(5);
        var archiveFileName = new ArchiveFileName(301, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            archiveFileName,
            new JournalDateRange(storedDate, storedDate));

        byte[] bytes = [9, 8, 7, 6, 5, 4];
        ExternalPayloadAddress address = AddressForBytes(storedDate, bytes);
        string sourcePath = WriteManagedFile(environment, address, bytes);
        SeedExternalHistory(
            environment,
            Path.Combine(environment.Root, "Archive", archiveFileName.FileName),
            storedDate,
            address);

        var collector = new ProtectedExternalPayloadTrashCollector(
            environment.Session,
            environment.Factory);
        ExternalPayloadTrashCollectionResult result = await collector.CollectAsync(ClosedDay(0));

        Assert.Equal(0, result.CollectedFileCount);
        Assert.True(File.Exists(sourcePath));
        Assert.Equal([address], ReadCatalogAddresses(environment));
    }

    [Fact]
    public async Task CollectAsync_CanonicalObjectWithWrongBytesFailsClosed()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly storedDate = ClosedDay(6);
        DateOnly deletionDate = ClosedDay(0);
        byte[] expectedBytes = [1, 1, 2, 3, 5, 8];
        byte[] wrongBytes = [8, 5, 3, 2, 1, 1];
        ExternalPayloadAddress address = AddressForBytes(storedDate, expectedBytes);
        string sourcePath = WriteManagedFile(environment, address, wrongBytes);

        var collector = new ProtectedExternalPayloadTrashCollector(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            collector.CollectAsync(deletionDate));

        Assert.True(File.Exists(sourcePath));
        AssertDeletionDateTrashAbsent(environment, deletionDate);
    }

    [Fact]
    public async Task CollectAsync_NonCanonicalFilesAreIgnored()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly deletionDate = ClosedDay(0);
        string directory = Path.Combine(
            environment.Root,
            "Files",
            ClosedDay(2).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        string foreignPath = Path.Combine(directory, "notes.txt");
        File.WriteAllText(foreignPath, "not a Clipensk content-addressed object");

        var collector = new ProtectedExternalPayloadTrashCollector(
            environment.Session,
            environment.Factory);
        ExternalPayloadTrashCollectionResult result = await collector.CollectAsync(deletionDate);

        Assert.Equal(0, result.CollectedFileCount);
        Assert.True(File.Exists(foreignPath));
        AssertDeletionDateTrashAbsent(environment, deletionDate);
    }

    [Fact]
    public async Task CollectAsync_ExistingExactTrashCopyDeletesDuplicateSource()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly storedDate = ClosedDay(7);
        DateOnly deletionDate = ClosedDay(0);
        byte[] bytes = [42, 43, 44, 45];
        ExternalPayloadAddress address = AddressForBytes(storedDate, bytes);
        string sourcePath = WriteManagedFile(environment, address, bytes);
        string trashRelativePath = TrashRelativePath(deletionDate, address.RelativePath);
        string trashPath = Path.Combine(environment.Root, trashRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(trashPath)!);
        File.WriteAllBytes(trashPath, bytes);

        var collector = new ProtectedExternalPayloadTrashCollector(
            environment.Session,
            environment.Factory);
        ExternalPayloadTrashCollectionResult result = await collector.CollectAsync(deletionDate);

        Assert.Equal(1, result.CollectedFileCount);
        Assert.Equal([trashRelativePath], result.TrashRelativePaths);
        Assert.False(File.Exists(sourcePath));
        Assert.Equal(bytes, File.ReadAllBytes(trashPath));
    }

    [Fact]
    public async Task CollectAsync_CancellationBeforeFilesystemPublicationLeavesSource()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly storedDate = ClosedDay(8);
        DateOnly deletionDate = ClosedDay(0);
        byte[] bytes = [70, 71, 72];
        ExternalPayloadAddress address = AddressForBytes(storedDate, bytes);
        string sourcePath = WriteManagedFile(environment, address, bytes);
        using var cancellation = new CancellationTokenSource();

        environment.Factory.OnOpen = (connection, mode) =>
        {
            if (mode == SqliteOpenMode.ReadOnly &&
                string.Equals(
                    Path.GetFileName(connection.DataSource),
                    "storage-catalog.db",
                    StringComparison.OrdinalIgnoreCase))
            {
                cancellation.Cancel();
            }
        };

        var collector = new ProtectedExternalPayloadTrashCollector(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            collector.CollectAsync(deletionDate, cancellation.Token));

        environment.Factory.OnOpen = null;
        Assert.True(File.Exists(sourcePath));
        AssertDeletionDateTrashAbsent(environment, deletionDate);
    }

    [Fact]
    public async Task CollectAsync_WaitsForSessionMutationLease()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly storedDate = ClosedDay(9);
        byte[] bytes = [90, 91, 92, 93];
        ExternalPayloadAddress address = AddressForBytes(storedDate, bytes);
        string sourcePath = WriteManagedFile(environment, address, bytes);
        ProtectedStorageMutationLease held = await environment.Session.AcquireMutationLeaseAsync();

        var collector = new ProtectedExternalPayloadTrashCollector(
            environment.Session,
            environment.Factory);
        Task<ExternalPayloadTrashCollectionResult> collectTask =
            collector.CollectAsync(ClosedDay(0));
        Assert.False(collectTask.IsCompleted);

        held.Dispose();
        ExternalPayloadTrashCollectionResult result =
            await collectTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, result.CollectedFileCount);
        Assert.False(File.Exists(sourcePath));
    }

    private static ExternalPayloadAddress AddressForBytes(DateOnly storedDate, byte[] bytes)
    {
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new ExternalPayloadAddress(
            sha256,
            Path.Combine(
                storedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                sha256 + ".png"),
            bytes.Length);
    }

    private static string WriteManagedFile(
        GlobalPolicyTestEnvironment environment,
        ExternalPayloadAddress address,
        byte[] bytes)
    {
        string path = Path.Combine(environment.Root, "Files", address.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
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

    private static void SeedCatalogAddress(
        GlobalPolicyTestEnvironment environment,
        ExternalPayloadAddress address)
    {
        using SqliteConnection connection = environment.Factory.Open(
            environment.CatalogPath,
            environment.Key,
            SqliteOpenMode.ReadWrite);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ExternalPayloadAddressIndex (Sha256, RelativePath, SizeBytes)
            VALUES ($sha256, $relativePath, $sizeBytes);
            """;
        command.Parameters.AddWithValue("$sha256", address.Sha256);
        command.Parameters.AddWithValue("$relativePath", address.RelativePath);
        command.Parameters.AddWithValue("$sizeBytes", address.SizeBytes);
        command.ExecuteNonQuery();
    }

    private static ExternalPayloadAddress[] ReadCatalogAddresses(
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
            ORDER BY Sha256;
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

    private static void AssertDeletionDateTrashAbsent(
        GlobalPolicyTestEnvironment environment,
        DateOnly deletionDate)
    {
        string deletionDatePath = Path.Combine(
            environment.Root,
            "Trash",
            deletionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Assert.False(Directory.Exists(deletionDatePath));
    }

    private static string TrashRelativePath(
        DateOnly deletionDate,
        string filesRelativePath) =>
        Path.Combine(
            "Trash",
            deletionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            filesRelativePath);

    private static DateOnly ClosedDay(int daysAgo) =>
        DateOnly.FromDateTime(DateTime.Now).AddDays(-daysAgo);
}
