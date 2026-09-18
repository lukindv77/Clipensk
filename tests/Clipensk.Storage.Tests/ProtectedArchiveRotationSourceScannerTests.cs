using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveRotationSourceScannerTests
{
    private static readonly DateOnly Today = new(2026, 3, 1);

    [Fact]
    public async Task ScanAsync_ReturnsContiguousWindowIncludingZeroRecordDays()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedEvents(environment, new DateOnly(2026, 2, 20), 2);
        SeedEvents(environment, new DateOnly(2026, 2, 23), 1);

        ArchiveRotationSourceScan scan = await Scanner(environment).ScanAsync(Today);

        Assert.Equal(4, scan.EligibleDays.Count);
        Assert.Equal(
            [
                new DateOnly(2026, 2, 20),
                new DateOnly(2026, 2, 21),
                new DateOnly(2026, 2, 22),
                new DateOnly(2026, 2, 23),
            ],
            scan.EligibleDays.Select(day => day.CalendarDate));
        Assert.Equal([2L, 0L, 0L, 1L], scan.EligibleDays.Select(day => day.RecordCount));
        Assert.Null(scan.AssignedArchiveCoverageEnd);
        Assert.Equal(1, scan.NextArchiveBaseNumber);
    }

    [Fact]
    public async Task ScanAsync_ExcludesCurrentAndFutureDays()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedEvents(environment, new DateOnly(2026, 2, 28), 1);
        SeedEvents(environment, Today, 3);
        SeedEvents(environment, new DateOnly(2026, 3, 2), 1);

        ArchiveRotationSourceScan scan = await Scanner(environment).ScanAsync(Today);

        Assert.Single(scan.EligibleDays);
        Assert.Equal(new DateOnly(2026, 2, 28), scan.EligibleDays[0].CalendarDate);
        Assert.Equal(1, scan.EligibleDays[0].RecordCount);
    }

    [Fact]
    public async Task ScanAsync_IsNoOpWhenNoClosedRowsExist()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        SeedEvents(environment, Today, 2);

        ArchiveRotationSourceScan scan = await Scanner(environment).ScanAsync(Today);

        Assert.Empty(scan.EligibleDays);
        Assert.Null(scan.AssignedArchiveCoverageEnd);
        Assert.Equal(1, scan.NextArchiveBaseNumber);
    }

    [Fact]
    public async Task ScanAsync_AllocatesAfterHighestCanonicalBaseNumberIncludingSplitFamily()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            new ArchiveFileName(5, ArchiveFileName.NoSplit),
            Range(2026, 1, 1, 2026, 1, 20));
        await archiveService.CreateAsync(
            new ArchiveFileName(9, 1),
            Range(2026, 1, 21, 2026, 1, 31));
        SeedEvents(environment, new DateOnly(2026, 2, 20), 1);

        ArchiveRotationSourceScan scan = await Scanner(environment).ScanAsync(Today);

        Assert.Equal(10, scan.NextArchiveBaseNumber);
        Assert.Equal(new DateOnly(2026, 1, 31), scan.AssignedArchiveCoverageEnd);
        Assert.Single(scan.EligibleDays);
        Assert.Equal(new DateOnly(2026, 2, 20), scan.EligibleDays[0].CalendarDate);
    }

    [Fact]
    public async Task ScanAsync_FailsClosedWhenCurrentHoldsRowsInsideAssignedCoverage()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            new ArchiveFileName(7, ArchiveFileName.NoSplit),
            Range(2026, 2, 1, 2026, 2, 10));
        SeedEvents(environment, new DateOnly(2026, 2, 5), 1);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await Scanner(environment).ScanAsync(Today));
    }

    [Fact]
    public async Task ScanAsync_FailsClosedOnOverlappingArchiveCoverage()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            new ArchiveFileName(11, ArchiveFileName.NoSplit),
            Range(2026, 1, 1, 2026, 1, 20));
        await archiveService.CreateAsync(
            new ArchiveFileName(12, ArchiveFileName.NoSplit),
            Range(2026, 1, 15, 2026, 1, 31));

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await Scanner(environment).ScanAsync(Today));
    }

    [Fact]
    public async Task ScanAsync_IsBlockedByPendingRotation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var rotations = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        await rotations.StartAsync(
            new ArchiveRotationSettings { MaxCalendarDays = 3 },
            [
                new(0, new ArchiveFileName(300, ArchiveFileName.NoSplit), Guid.NewGuid(),
                    Range(2026, 2, 1, 2026, 2, 3), 5, 4096),
            ]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Scanner(environment).ScanAsync(Today));
    }

    [Fact]
    public async Task ScanAsync_IsBlockedByPendingSplit()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var splits = new SqlitePendingArchiveSplitRepository(environment.Session, environment.Factory);
        var source = new ArchiveFileName(44, ArchiveFileName.NoSplit);
        Guid sourceId = Guid.NewGuid();
        await splits.StartAsync(
            source,
            sourceId,
            Range(2026, 1, 1, 2026, 1, 20),
            [
                new(0, source, sourceId, Range(2026, 1, 1, 2026, 1, 10)),
                new(1, source.NextSplit(1), Guid.NewGuid(), Range(2026, 1, 11, 2026, 1, 20)),
            ]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Scanner(environment).ScanAsync(Today));
    }

    [Fact]
    public async Task ScanAsync_RequiresActiveCurrentSchemaVersion()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        ProtectedArchiveRotationSourceScanner scanner = Scanner(environment);
        environment.DowngradeToV9();

        await Assert.ThrowsAsync<InvalidDataException>(async () => await scanner.ScanAsync(Today));
    }

    [Fact]
    public void AllocateBaseFileNames_IsConsecutiveAndUnsplit()
    {
        IReadOnlyList<ArchiveFileName> names =
            ProtectedArchiveRotationSourceScanner.AllocateBaseFileNames(101, 3);

        Assert.Equal(
            ["archive_000101.db", "archive_000102.db", "archive_000103.db"],
            names.Select(name => name.FileName));
        Assert.All(names, name => Assert.Equal(ArchiveFileName.NoSplit, name.SplitSequence));
        Assert.Empty(ProtectedArchiveRotationSourceScanner.AllocateBaseFileNames(101, 0));
    }

    [Fact]
    public void AllocateBaseFileNames_FailsClosedWhenNamespaceIsExhausted()
    {
        Assert.Equal(
            2,
            ProtectedArchiveRotationSourceScanner
                .AllocateBaseFileNames(ArchiveFileName.MaxBaseNumber - 1, 2)
                .Count);
        Assert.Throws<InvalidOperationException>(() =>
            ProtectedArchiveRotationSourceScanner.AllocateBaseFileNames(
                ArchiveFileName.MaxBaseNumber - 1,
                3));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProtectedArchiveRotationSourceScanner.AllocateBaseFileNames(0, 1));
    }

    private static ProtectedArchiveRotationSourceScanner Scanner(
        GlobalPolicyTestEnvironment environment) =>
        new(environment.Session, environment.Factory);

    private static void SeedEvents(
        GlobalPolicyTestEnvironment environment,
        DateOnly day,
        int count)
    {
        using SqliteConnection connection = environment.Factory.Open(
            environment.CurrentPath,
            environment.Key,
            SqliteOpenMode.ReadWrite);
        for (int index = 0; index < count; index++)
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO ClipboardHistoryEvent (
                    EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                    SourceApplicationId, SourceProcessId, SourceExecutablePath,
                    SourceApplicationUserModelId)
                VALUES ($eventId, $eventUtc, 0, 'UTC', $calendarDate, NULL, NULL, NULL, NULL);
                """;
            insert.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("D"));
            insert.Parameters.AddWithValue(
                "$eventUtc",
                $"{day:yyyy-MM-dd}T{index:00}:00:00.0000000+00:00");
            insert.Parameters.AddWithValue(
                "$calendarDate",
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }
    }

    private static JournalDateRange Range(
        int startYear, int startMonth, int startDay,
        int endYear, int endMonth, int endDay) =>
        new(new DateOnly(startYear, startMonth, startDay), new DateOnly(endYear, endMonth, endDay));
}
