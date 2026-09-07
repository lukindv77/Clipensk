using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedUnifiedClipboardHistoryRepositoryTests
{
    [Fact]
    public async Task ReadAsync_CurrentOnlyReturnsCurrentLocation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = ClosedDay(1);
        Guid eventId = Guid.NewGuid();
        SeedCurrentEvent(environment, day, eventId, "current");

        var repository = new ProtectedUnifiedClipboardHistoryRepository(
            environment.Session,
            environment.Factory);
        IReadOnlyList<UnifiedClipboardHistoryEntry> result = await repository.ReadAsync(
            new JournalDateRange(day, day),
            10);

        UnifiedClipboardHistoryEntry entry = Assert.Single(result);
        Assert.Equal(eventId, entry.Entry.EventId);
        ClipboardHistoryPhysicalLocation location = Assert.Single(entry.Locations);
        Assert.Equal(ClipboardHistoryPhysicalLocationKind.Current, location.Kind);
        Assert.Null(location.DatabaseId);
        Assert.Null(location.FileName);
    }

    [Fact]
    public async Task ReadAsync_TemporaryTransferDuplicateReturnsOneLogicalEntryWithBothLocations()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = ClosedDay(3);
        var range = new JournalDateRange(day, day);
        var archiveFileName = new ArchiveFileName(101, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        DatabaseIdentity archiveIdentity = await archiveService.CreateAsync(archiveFileName, range);
        Guid eventId = Guid.NewGuid();
        SeedCurrentEvent(environment, day, eventId, "duplicate");

        await CopyToArchiveWithoutPurgingCurrentAsync(environment, archiveFileName, range);
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        await catalog.RebuildAsync(DateOnly.FromDateTime(DateTime.Now));

        var repository = new ProtectedUnifiedClipboardHistoryRepository(
            environment.Session,
            environment.Factory);
        IReadOnlyList<UnifiedClipboardHistoryEntry> result = await repository.ReadAsync(range, 10);

        UnifiedClipboardHistoryEntry entry = Assert.Single(result);
        Assert.Equal(eventId, entry.Entry.EventId);
        Assert.Equal(2, entry.Locations.Count);
        Assert.Contains(
            entry.Locations,
            item => item.Kind == ClipboardHistoryPhysicalLocationKind.Current);
        ClipboardHistoryPhysicalLocation archiveLocation = Assert.Single(
            entry.Locations.Where(item => item.Kind == ClipboardHistoryPhysicalLocationKind.Archive));
        Assert.Equal(archiveIdentity.DatabaseId, archiveLocation.DatabaseId);
        Assert.Equal(archiveFileName.FileName, archiveLocation.FileName);
    }

    [Fact]
    public async Task ReadBeforeAsync_MergesGlobalOrderAcrossCurrentAndArchive()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly archiveDay = ClosedDay(5);
        DateOnly currentDay = ClosedDay(1);
        var archiveFileName = new ArchiveFileName(102, ArchiveFileName.NoSplit);
        var archiveRange = new JournalDateRange(archiveDay, archiveDay);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(archiveFileName, archiveRange);

        Guid archiveEventId = Guid.NewGuid();
        SeedCurrentEvent(environment, archiveDay, archiveEventId, "archive");
        var transfer = new ProtectedCurrentToArchiveTransferService(environment.Session, environment.Factory);
        await transfer.TransferAsync(archiveFileName, archiveRange);

        Guid currentEventId = Guid.NewGuid();
        SeedCurrentEvent(environment, currentDay, currentEventId, "current");
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        await catalog.RebuildAsync(DateOnly.FromDateTime(DateTime.Now));

        var period = new JournalDateRange(archiveDay, currentDay);
        var repository = new ProtectedUnifiedClipboardHistoryRepository(
            environment.Session,
            environment.Factory);

        IReadOnlyList<UnifiedClipboardHistoryEntry> firstPage = await repository.ReadAsync(period, 1);
        UnifiedClipboardHistoryEntry newest = Assert.Single(firstPage);
        Assert.Equal(currentEventId, newest.Entry.EventId);
        Assert.Equal(
            ClipboardHistoryPhysicalLocationKind.Current,
            Assert.Single(newest.Locations).Kind);

        ClipboardHistoryCursor cursor = ClipboardHistoryCursor.FromEntry(period, newest.Entry);
        IReadOnlyList<UnifiedClipboardHistoryEntry> secondPage = await repository.ReadBeforeAsync(
            period,
            10,
            cursor);

        UnifiedClipboardHistoryEntry older = Assert.Single(secondPage);
        Assert.Equal(archiveEventId, older.Entry.EventId);
        Assert.Equal(
            ClipboardHistoryPhysicalLocationKind.Archive,
            Assert.Single(older.Locations).Kind);
        Assert.Equal(archiveFileName.FileName, older.Locations[0].FileName);
    }

    [Fact]
    public async Task ReadAsync_OpensOnlyArchiveSegmentsSelectedForRequestedPeriod()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly requestedDay = ClosedDay(6);
        DateOnly unrelatedDay = ClosedDay(12);
        var requestedArchive = new ArchiveFileName(103, ArchiveFileName.NoSplit);
        var unrelatedArchive = new ArchiveFileName(104, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);

        await archiveService.CreateAsync(
            requestedArchive,
            new JournalDateRange(requestedDay, requestedDay));
        SeedCurrentEvent(environment, requestedDay, Guid.NewGuid(), "requested");
        var transfer = new ProtectedCurrentToArchiveTransferService(environment.Session, environment.Factory);
        await transfer.TransferAsync(
            requestedArchive,
            new JournalDateRange(requestedDay, requestedDay));

        await archiveService.CreateAsync(
            unrelatedArchive,
            new JournalDateRange(unrelatedDay, unrelatedDay));
        SeedCurrentEvent(environment, unrelatedDay, Guid.NewGuid(), "unrelated");
        await transfer.TransferAsync(
            unrelatedArchive,
            new JournalDateRange(unrelatedDay, unrelatedDay));

        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        await catalog.RebuildAsync(DateOnly.FromDateTime(DateTime.Now));

        var openedArchiveNames = new List<string>();
        environment.Factory.OnOpen = (connection, _) =>
        {
            string name = Path.GetFileName(connection.DataSource);
            if (name.StartsWith("archive_", StringComparison.Ordinal))
            {
                openedArchiveNames.Add(name);
            }
        };

        var repository = new ProtectedUnifiedClipboardHistoryRepository(
            environment.Session,
            environment.Factory);
        IReadOnlyList<UnifiedClipboardHistoryEntry> result = await repository.ReadAsync(
            new JournalDateRange(requestedDay, requestedDay),
            10);

        environment.Factory.OnOpen = null;
        Assert.Single(result);
        Assert.Contains(requestedArchive.FileName, openedArchiveNames);
        Assert.DoesNotContain(unrelatedArchive.FileName, openedArchiveNames);
    }

    [Fact]
    public async Task ReadAsync_StaleCatalogFailsClosedBeforeOpeningUnindexedArchive()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = ClosedDay(7);
        var archiveFileName = new ArchiveFileName(105, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            archiveFileName,
            new JournalDateRange(day, day));

        bool archiveOpened = false;
        environment.Factory.OnOpen = (connection, _) =>
        {
            if (string.Equals(
                    Path.GetFileName(connection.DataSource),
                    archiveFileName.FileName,
                    StringComparison.Ordinal))
            {
                archiveOpened = true;
            }
        };

        var repository = new ProtectedUnifiedClipboardHistoryRepository(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.ReadAsync(new JournalDateRange(day, day), 10));

        environment.Factory.OnOpen = null;
        Assert.False(archiveOpened);
    }

    [Fact]
    public async Task ReadAsync_ConflictingDuplicateFailsClosed()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = ClosedDay(8);
        var range = new JournalDateRange(day, day);
        var archiveFileName = new ArchiveFileName(106, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(archiveFileName, range);
        Guid eventId = Guid.NewGuid();
        SeedCurrentEvent(environment, day, eventId, "hello");
        await CopyToArchiveWithoutPurgingCurrentAsync(environment, archiveFileName, range);
        ArchiveExecute(environment, archiveFileName, $"""
            UPDATE ClipboardHistoryPayload
            SET InlineCanonicalText = 'HELLO', SearchText = 'HELLO'
            WHERE EventId = '{eventId:D}' AND PayloadOrder = 0;
            """);
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        await catalog.RebuildAsync(DateOnly.FromDateTime(DateTime.Now));

        var repository = new ProtectedUnifiedClipboardHistoryRepository(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.ReadAsync(range, 10));
    }

    [Fact]
    public async Task ReadAsync_CallerCancellationBeforeWorkReturnsNoPartialPage()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = ClosedDay(1);
        SeedCurrentEvent(environment, day, Guid.NewGuid(), "cancel");
        environment.Factory.Modes.Clear();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var repository = new ProtectedUnifiedClipboardHistoryRepository(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await repository.ReadAsync(
                new JournalDateRange(day, day),
                10,
                cancellation.Token));

        Assert.Empty(environment.Factory.Modes);
    }

    private static async Task CopyToArchiveWithoutPurgingCurrentAsync(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName archiveFileName,
        JournalDateRange range)
    {
        using var cancellation = new CancellationTokenSource();
        int archiveReadOnlyOpens = 0;
        environment.Factory.OnOpen = (connection, mode) =>
        {
            if (mode == SqliteOpenMode.ReadOnly &&
                string.Equals(
                    Path.GetFileName(connection.DataSource),
                    archiveFileName.FileName,
                    StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Increment(ref archiveReadOnlyOpens) == 2)
            {
                cancellation.Cancel();
            }
        };

        var transfer = new ProtectedCurrentToArchiveTransferService(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transfer.TransferAsync(archiveFileName, range, cancellation.Token));
        environment.Factory.OnOpen = null;

        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
        Assert.Equal(
            1,
            ArchiveScalar(
                environment,
                archiveFileName,
                "SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
    }

    private static DateOnly ClosedDay(int daysAgo) =>
        DateOnly.FromDateTime(DateTime.Now).AddDays(-daysAgo);

    private static void SeedCurrentEvent(
        GlobalPolicyTestEnvironment environment,
        DateOnly day,
        Guid eventId,
        string text)
    {
        long byteCount = System.Text.Encoding.UTF8.GetByteCount(text);
        using SqliteConnection connection = environment.Factory.Open(
            environment.CurrentPath,
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
                    NULL, 1234, 'C:\\Apps\\Source.exe', 'Source.App');
                """;
            insertEvent.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
            insertEvent.Parameters.AddWithValue(
                "$eventUtc",
                $"{day:yyyy-MM-dd}T12:00:00.0000000+00:00");
            insertEvent.Parameters.AddWithValue(
                "$calendarDate",
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insertEvent.ExecuteNonQuery();
        }

        using SqliteCommand payload = connection.CreateCommand();
        payload.CommandText = """
            INSERT INTO ClipboardHistoryPayload (
                EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                InlineCanonicalText, SearchText, ExternalSha256,
                ExternalRelativePath, ExternalSizeBytes)
            VALUES (
                $eventId, 0, 'Text', 'Text', $byteCount,
                $text, $text, NULL, NULL, NULL);
            """;
        payload.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
        payload.Parameters.AddWithValue("$byteCount", byteCount);
        payload.Parameters.AddWithValue("$text", text);
        payload.ExecuteNonQuery();
    }

    private static void ArchiveExecute(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName fileName,
        string sql)
    {
        using SqliteConnection connection = environment.Factory.Open(
            ArchivePath(environment, fileName),
            environment.Key,
            SqliteOpenMode.ReadWrite);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ArchiveScalar(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName fileName,
        string sql)
    {
        using SqliteConnection connection = environment.Factory.Open(
            ArchivePath(environment, fileName),
            environment.Key,
            SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string ArchivePath(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName fileName) =>
        Path.Combine(environment.Root, "Archive", fileName.FileName);
}
