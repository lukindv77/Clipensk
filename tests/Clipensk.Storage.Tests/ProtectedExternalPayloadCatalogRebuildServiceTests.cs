using System.Globalization;
using System.Security.Cryptography;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedExternalPayloadCatalogRebuildServiceTests
{
    [Fact]
    public async Task RebuildAsync_CurrentOnlyReplacesStaleIndexWithoutRequiringPhysicalFile()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = ClosedDay(1);
        ExternalPayloadAddress expected = Address('a', day, 12);
        SeedExternalHistory(environment, environment.CurrentPath, day, Guid.NewGuid(), expected);
        SeedCatalogAddress(environment, Address('f', day.AddDays(-5), 99));

        Assert.False(File.Exists(Path.Combine(environment.Root, "Files", expected.RelativePath)));

        var service = new ProtectedExternalPayloadCatalogRebuildService(
            environment.Session,
            environment.Factory);
        IReadOnlyList<ExternalPayloadAddress> rebuilt = await service.RebuildAsync();

        Assert.Equal([expected], rebuilt);
        Assert.Equal([expected], ReadCatalogAddresses(environment));
        Assert.False(File.Exists(Path.Combine(environment.Root, "Files", expected.RelativePath)));
    }

    [Fact]
    public async Task RebuildAsync_RecoversArchiveOnlyReference()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = ClosedDay(2);
        var archiveFileName = new ArchiveFileName(201, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            archiveFileName,
            new JournalDateRange(day, day));
        ExternalPayloadAddress expected = Address('b', day, 23);
        SeedExternalHistory(
            environment,
            ArchivePath(environment, archiveFileName),
            day,
            Guid.NewGuid(),
            expected);

        var service = new ProtectedExternalPayloadCatalogRebuildService(
            environment.Session,
            environment.Factory);
        IReadOnlyList<ExternalPayloadAddress> rebuilt = await service.RebuildAsync();

        Assert.Equal([expected], rebuilt);
        Assert.Equal([expected], ReadCatalogAddresses(environment));
    }

    [Fact]
    public async Task RebuildAsync_ExactCurrentArchiveDuplicateCollapsesAndPreservesArchiveSegmentProjection()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = ClosedDay(3);
        var archiveFileName = new ArchiveFileName(202, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            archiveFileName,
            new JournalDateRange(day, day));
        ExternalPayloadAddress expected = Address('c', day, 34);
        SeedExternalHistory(environment, environment.CurrentPath, day, Guid.NewGuid(), expected);
        SeedExternalHistory(
            environment,
            ArchivePath(environment, archiveFileName),
            day,
            Guid.NewGuid(),
            expected);

        var segmentCatalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        await segmentCatalog.RebuildAsync(DateOnly.FromDateTime(DateTime.Now));
        ArchiveSegmentDescriptor[] segmentsBefore =
            (await segmentCatalog.ReadAsync()).ToArray();

        var service = new ProtectedExternalPayloadCatalogRebuildService(
            environment.Session,
            environment.Factory);
        IReadOnlyList<ExternalPayloadAddress> rebuilt = await service.RebuildAsync();
        ArchiveSegmentDescriptor[] segmentsAfter =
            (await segmentCatalog.ReadAsync()).ToArray();

        Assert.Equal([expected], rebuilt);
        Assert.Equal([expected], ReadCatalogAddresses(environment));
        Assert.Equal(segmentsBefore, segmentsAfter);
    }

    [Fact]
    public async Task RebuildAsync_ConflictingAddressForSameShaFailsBeforeCatalogMutation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = ClosedDay(4);
        var archiveFileName = new ArchiveFileName(203, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            archiveFileName,
            new JournalDateRange(day, day));

        ExternalPayloadAddress current = Address('d', day, 45);
        ExternalPayloadAddress conflicting = current with
        {
            RelativePath = Path.Combine(
                day.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                current.Sha256 + ".png"),
        };
        SeedExternalHistory(environment, environment.CurrentPath, day, Guid.NewGuid(), current);
        SeedExternalHistory(
            environment,
            ArchivePath(environment, archiveFileName),
            day,
            Guid.NewGuid(),
            conflicting);
        ExternalPayloadAddress sentinel = Address('e', day.AddDays(-8), 56);
        SeedCatalogAddress(environment, sentinel);

        var service = new ProtectedExternalPayloadCatalogRebuildService(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RebuildAsync());

        Assert.Equal([sentinel], ReadCatalogAddresses(environment));
    }

    [Fact]
    public async Task RebuildAsync_RelativePathCollisionBetweenHashesFailsBeforeCatalogMutation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = ClosedDay(5);
        var archiveFileName = new ArchiveFileName(204, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            archiveFileName,
            new JournalDateRange(day, day));

        ExternalPayloadAddress first = Address('1', day, 67);
        ExternalPayloadAddress second = Address('2', day, 67) with
        {
            RelativePath = first.RelativePath,
        };
        SeedExternalHistory(environment, environment.CurrentPath, day, Guid.NewGuid(), first);
        SeedExternalHistory(
            environment,
            ArchivePath(environment, archiveFileName),
            day,
            Guid.NewGuid(),
            second);
        ExternalPayloadAddress sentinel = Address('3', day.AddDays(-9), 78);
        SeedCatalogAddress(environment, sentinel);

        var service = new ProtectedExternalPayloadCatalogRebuildService(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RebuildAsync());

        Assert.Equal([sentinel], ReadCatalogAddresses(environment));
    }

    [Fact]
    public async Task CaptureSink_HoldsMutationLeaseBeforeExternalResolutionThroughHistoryCommit()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        byte[] bytes = [1, 2, 3, 4];
        DateOnly day = new(2026, 9, 6);
        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var expected = new ExternalPayloadAddress(
            sha,
            Path.Combine("2026-09-06", sha + ".png"),
            bytes.Length);
        var resolver = new BlockingExternalPayloadResolver(expected);
        var sink = new SqliteClipboardHistorySink(
            environment.Session,
            resolver,
            environment.Factory);

        Task storeTask = sink.StoreAsync(CreatePngCapture(bytes)).AsTask();
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<ProtectedStorageMutationLease> competingLease =
            environment.Session.AcquireMutationLeaseAsync().AsTask();
        Assert.False(competingLease.IsCompleted);

        resolver.Continue.TrySetResult(true);
        await storeTask;
        using ProtectedStorageMutationLease acquired =
            await competingLease.WaitAsync(TimeSpan.FromSeconds(2));

        var service = new ProtectedExternalPayloadCatalogRebuildService(
            environment.Session,
            environment.Factory);
        IReadOnlyList<ExternalPayloadAddress> rebuilt = await service.RebuildAsync();

        Assert.Equal([expected], rebuilt);
        Assert.Equal([expected], ReadCatalogAddresses(environment));
    }

    [Fact]
    public async Task RebuildAsync_WaitsForExistingMutationLease()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = ClosedDay(1);
        ExternalPayloadAddress expected = Address('4', day, 89);
        SeedExternalHistory(environment, environment.CurrentPath, day, Guid.NewGuid(), expected);
        ProtectedStorageMutationLease held = await environment.Session.AcquireMutationLeaseAsync();

        var service = new ProtectedExternalPayloadCatalogRebuildService(
            environment.Session,
            environment.Factory);
        Task<IReadOnlyList<ExternalPayloadAddress>> rebuildTask = service.RebuildAsync();
        Assert.False(rebuildTask.IsCompleted);

        held.Dispose();
        IReadOnlyList<ExternalPayloadAddress> rebuilt =
            await rebuildTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([expected], rebuilt);
    }

    private static ClipboardAcceptedCapture CreatePngCapture(byte[] bytes)
    {
        return new ClipboardAcceptedCapture(
            new ClipboardCaptureContext(
                new ClipboardCaptureRequest(
                    new EventTimeContext(
                        new DateTimeOffset(2026, 9, 6, 2, 0, 0, TimeSpan.Zero),
                        "UTC")),
                null),
            [
                new ClipboardCapturedPngImageContent(
                    new ClipboardContentReaderRoute(
                        new ClipboardSelectedFormat("Bitmap", 4096),
                        ClipboardContentReaderKind.PngImage),
                    bytes),
            ]);
    }

    private static ExternalPayloadAddress Address(
        char shaCharacter,
        DateOnly day,
        long size)
    {
        string sha = new(shaCharacter, 64);
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
        Guid eventId,
        ExternalPayloadAddress address)
    {
        using SqliteConnection connection = environment.Factory.Open(
            databasePath,
            environment.Key,
            SqliteOpenMode.ReadWrite);

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
            INSERT OR REPLACE INTO ExternalPayloadAddressIndex (
                Sha256, RelativePath, SizeBytes)
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

    private static string ArchivePath(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName fileName) =>
        Path.Combine(environment.Root, "Archive", fileName.FileName);

    private static DateOnly ClosedDay(int daysAgo) =>
        DateOnly.FromDateTime(DateTime.Now).AddDays(-daysAgo);

    private sealed class BlockingExternalPayloadResolver : IClipboardExternalPayloadAddressResolver
    {
        private readonly ExternalPayloadAddress _address;

        public BlockingExternalPayloadResolver(ExternalPayloadAddress address)
        {
            _address = address;
        }

        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Continue { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ExternalPayloadAddress> ResolveNormalizedPngAsync(
            DateOnly eventCalendarDate,
            ReadOnlyMemory<byte> pngBytes,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true);
            await Continue.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return _address;
        }

        public ValueTask<ExternalPayloadAddress> ResolveCustomBinaryAsync(
            DateOnly eventCalendarDate,
            string formatName,
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Custom binary resolution is not expected in this test.");
    }
}
