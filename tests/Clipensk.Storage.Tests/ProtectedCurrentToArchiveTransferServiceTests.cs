using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedCurrentToArchiveTransferServiceTests
{
    [Fact]
    public async Task TransferAsync_CopiesVerifiesAndPurgesClosedDay()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = ClosedDay(3);
        var range = new JournalDateRange(day, day);
        var archiveFileName = new ArchiveFileName(31, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(archiveFileName, range);

        Guid applicationId = Guid.NewGuid();
        Guid eventId = Guid.NewGuid();
        SeedCurrentEvent(environment, day, eventId, applicationId, includeExternalPayload: true);

        var service = new ProtectedCurrentToArchiveTransferService(environment.Session, environment.Factory);
        CurrentToArchiveTransferResult result = await service.TransferAsync(archiveFileName, range);

        Assert.Equal(1, result.CopiedEventCount);
        Assert.Equal(1, result.PurgedEventCount);
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryPayload;"));
        Assert.Equal(1, ArchiveScalar(environment, archiveFileName, "SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
        Assert.Equal(2, ArchiveScalar(environment, archiveFileName, "SELECT COUNT(*) FROM ClipboardHistoryPayload;"));
        Assert.Equal(1, ArchiveScalar(
            environment,
            archiveFileName,
            $"SELECT COUNT(*) FROM ApplicationIdentity WHERE ApplicationId='{applicationId:D}';"));
        Assert.Equal(0, ArchiveScalar(
            environment,
            archiveFileName,
            "SELECT COUNT(*) FROM ApplicationIdentityAlias;"));
        Assert.Equal(1, ArchiveScalar(
            environment,
            archiveFileName,
            "SELECT COUNT(*) FROM ClipboardHistoryPayload WHERE PayloadKind='PngImage' AND ExternalSizeBytes=3;"));
    }

    [Fact]
    public async Task TransferAsync_CancellationAfterArchiveCommitLeavesCurrentAndRetryCompletes()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = ClosedDay(4);
        var range = new JournalDateRange(day, day);
        var archiveFileName = new ArchiveFileName(32, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(archiveFileName, range);
        SeedCurrentEvent(environment, day, Guid.NewGuid(), Guid.NewGuid(), includeExternalPayload: false);

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

        var service = new ProtectedCurrentToArchiveTransferService(environment.Session, environment.Factory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.TransferAsync(archiveFileName, range, cancellation.Token));

        environment.Factory.OnOpen = null;
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
        Assert.Equal(1, ArchiveScalar(environment, archiveFileName, "SELECT COUNT(*) FROM ClipboardHistoryEvent;"));

        CurrentToArchiveTransferResult retry = await service.TransferAsync(archiveFileName, range);

        Assert.Equal(1, retry.CopiedEventCount);
        Assert.Equal(1, retry.PurgedEventCount);
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
        Assert.Equal(1, ArchiveScalar(environment, archiveFileName, "SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
    }

    [Fact]
    public async Task TransferAsync_CurrentChangeBeforePurgeRollsBackAndRetryCopiesWholeRange()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = ClosedDay(5);
        var range = new JournalDateRange(day, day);
        var archiveFileName = new ArchiveFileName(33, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(archiveFileName, range);
        SeedCurrentEvent(environment, day, Guid.NewGuid(), Guid.NewGuid(), includeExternalPayload: false);

        bool injected = false;
        environment.Factory.OnOpen = (connection, mode) =>
        {
            if (injected ||
                mode != SqliteOpenMode.ReadWrite ||
                !string.Equals(Path.GetFileName(connection.DataSource), "current.db", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            injected = true;
            Guid applicationId = Guid.NewGuid();
            Guid eventId = Guid.NewGuid();
            InsertEvent(connection, day, eventId, applicationId, includeExternalPayload: false);
        };

        var service = new ProtectedCurrentToArchiveTransferService(environment.Session, environment.Factory);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TransferAsync(archiveFileName, range));

        environment.Factory.OnOpen = null;
        Assert.Equal(2, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
        Assert.Equal(1, ArchiveScalar(environment, archiveFileName, "SELECT COUNT(*) FROM ClipboardHistoryEvent;"));

        CurrentToArchiveTransferResult retry = await service.TransferAsync(archiveFileName, range);

        Assert.Equal(2, retry.CopiedEventCount);
        Assert.Equal(2, retry.PurgedEventCount);
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
        Assert.Equal(2, ArchiveScalar(environment, archiveFileName, "SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
    }

    [Fact]
    public async Task TransferAsync_RejectsConflictingExistingArchiveEventWithoutPurgingCurrent()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = ClosedDay(6);
        var range = new JournalDateRange(day, day);
        var archiveFileName = new ArchiveFileName(34, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(archiveFileName, range);

        Guid eventId = Guid.NewGuid();
        SeedCurrentEvent(environment, day, eventId, applicationId: null, includeExternalPayload: false);
        ArchiveExecute(environment, archiveFileName, $"""
            INSERT INTO ClipboardHistoryEvent (
                EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                SourceApplicationId, SourceProcessId, SourceExecutablePath, SourceApplicationUserModelId)
            VALUES (
                '{eventId:D}',
                '{day:yyyy-MM-dd}T01:00:00.0000000+00:00',
                0,
                'UTC',
                '{day:yyyy-MM-dd}',
                NULL, NULL, NULL, NULL);
            """);

        var service = new ProtectedCurrentToArchiveTransferService(environment.Session, environment.Factory);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.TransferAsync(archiveFileName, range));

        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
        Assert.Equal(1, ArchiveScalar(environment, archiveFileName, "SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
    }

    [Fact]
    public async Task TransferAsync_RejectsCurrentOrFutureCalendarDayBeforeOpeningStorage()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        var range = new JournalDateRange(today, today);
        var archiveFileName = new ArchiveFileName(35, ArchiveFileName.NoSplit);
        environment.Factory.Modes.Clear();

        var service = new ProtectedCurrentToArchiveTransferService(environment.Session, environment.Factory);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.TransferAsync(archiveFileName, range));

        Assert.Empty(environment.Factory.Modes);
    }

    [Fact]
    public async Task TransferAsync_RejectsRangeOutsideArchiveCoverageWithoutTouchingCurrent()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly archiveDay = ClosedDay(8);
        DateOnly currentDay = ClosedDay(7);
        var archiveFileName = new ArchiveFileName(36, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            archiveFileName,
            new JournalDateRange(archiveDay, archiveDay));
        SeedCurrentEvent(environment, currentDay, Guid.NewGuid(), null, includeExternalPayload: false);

        var service = new ProtectedCurrentToArchiveTransferService(environment.Session, environment.Factory);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.TransferAsync(
                archiveFileName,
                new JournalDateRange(currentDay, currentDay)));

        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
        Assert.Equal(0, ArchiveScalar(environment, archiveFileName, "SELECT COUNT(*) FROM ClipboardHistoryEvent;"));
    }

    private static DateOnly ClosedDay(int daysAgo) =>
        DateOnly.FromDateTime(DateTime.Now).AddDays(-daysAgo);

    private static void SeedCurrentEvent(
        GlobalPolicyTestEnvironment environment,
        DateOnly day,
        Guid eventId,
        Guid? applicationId,
        bool includeExternalPayload)
    {
        using SqliteConnection connection = environment.Factory.Open(
            environment.CurrentPath,
            environment.Key,
            SqliteOpenMode.ReadWrite);
        InsertEvent(connection, day, eventId, applicationId, includeExternalPayload);
    }

    private static void InsertEvent(
        SqliteConnection connection,
        DateOnly day,
        Guid eventId,
        Guid? applicationId,
        bool includeExternalPayload)
    {
        if (applicationId is Guid appId)
        {
            using SqliteCommand identity = connection.CreateCommand();
            identity.CommandText = """
                INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
                VALUES ($applicationId, $createdAtUtc);
                """;
            identity.Parameters.AddWithValue("$applicationId", appId.ToString("D"));
            identity.Parameters.AddWithValue("$createdAtUtc", "2026-01-01T00:00:00.0000000+00:00");
            identity.ExecuteNonQuery();
        }

        using (SqliteCommand insertEvent = connection.CreateCommand())
        {
            insertEvent.CommandText = """
                INSERT INTO ClipboardHistoryEvent (
                    EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                    SourceApplicationId, SourceProcessId, SourceExecutablePath,
                    SourceApplicationUserModelId)
                VALUES (
                    $eventId, $eventUtc, 0, 'UTC', $calendarDate,
                    $sourceApplicationId, 1234, 'C:\\Apps\\Source.exe', 'Source.App');
                """;
            insertEvent.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
            insertEvent.Parameters.AddWithValue(
                "$eventUtc",
                $"{day:yyyy-MM-dd}T12:00:00.0000000+00:00");
            insertEvent.Parameters.AddWithValue("$calendarDate", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insertEvent.Parameters.AddWithValue(
                "$sourceApplicationId",
                applicationId is Guid appId ? appId.ToString("D") : DBNull.Value);
            insertEvent.ExecuteNonQuery();
        }

        using (SqliteCommand textPayload = connection.CreateCommand())
        {
            textPayload.CommandText = """
                INSERT INTO ClipboardHistoryPayload (
                    EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                    InlineCanonicalText, SearchText, ExternalSha256,
                    ExternalRelativePath, ExternalSizeBytes)
                VALUES ($eventId, 0, 'Text', 'Text', 5, 'hello', 'hello', NULL, NULL, NULL);
                """;
            textPayload.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
            textPayload.ExecuteNonQuery();
        }

        if (includeExternalPayload)
        {
            using SqliteCommand externalPayload = connection.CreateCommand();
            externalPayload.CommandText = """
                INSERT INTO ClipboardHistoryPayload (
                    EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                    InlineCanonicalText, SearchText, ExternalSha256,
                    ExternalRelativePath, ExternalSizeBytes)
                VALUES (
                    $eventId, 1, 'Bitmap', 'PngImage', 3,
                    NULL, NULL,
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                    $relativePath, 3);
                """;
            externalPayload.Parameters.AddWithValue("$eventId", eventId.ToString("D"));
            externalPayload.Parameters.AddWithValue(
                "$relativePath",
                $"{day:yyyy-MM-dd}/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png");
            externalPayload.ExecuteNonQuery();
        }
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
