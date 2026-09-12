using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveSegmentCatalogTests
{
    [Fact]
    public async Task RebuildAsync_ProjectsValidatedArchivesAndRoundTrips()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);

        DatabaseIdentity later = await archiveService.CreateAsync(
            new ArchiveFileName(2, ArchiveFileName.NoSplit),
            Range(2026, 8, 1, 2026, 8, 31));
        DatabaseIdentity earlier = await archiveService.CreateAsync(
            new ArchiveFileName(1, ArchiveFileName.NoSplit),
            Range(2026, 7, 1, 2026, 7, 31));

        IReadOnlyList<ArchiveSegmentDescriptor> rebuilt = await catalog.RebuildAsync(
            new DateOnly(2026, 9, 7));
        IReadOnlyList<ArchiveSegmentDescriptor> persisted = await catalog.ReadAsync();

        Assert.Equal(2, rebuilt.Count);
        Assert.Equal(rebuilt, persisted);
        Assert.Equal(earlier.DatabaseId, rebuilt[0].DatabaseId);
        Assert.Equal("archive_000001.db", rebuilt[0].FileName);
        Assert.True(rebuilt[0].IsSealed);
        Assert.Equal(later.DatabaseId, rebuilt[1].DatabaseId);
        Assert.Equal("archive_000002.db", rebuilt[1].FileName);
        Assert.True(rebuilt[1].IsSealed);
    }

    [Fact]
    public async Task ValidateConsistencyAsync_MatchingProjectionUsesReadOnlyConnections()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            new ArchiveFileName(1, ArchiveFileName.NoSplit),
            Range(2026, 8, 1, 2026, 8, 31));
        IReadOnlyList<ArchiveSegmentDescriptor> rebuilt = await catalog.RebuildAsync(
            new DateOnly(2026, 9, 7));
        environment.Factory.Modes.Clear();

        IReadOnlyList<ArchiveSegmentDescriptor> validated = await catalog.ValidateConsistencyAsync(
            new DateOnly(2026, 9, 7));

        Assert.Equal(rebuilt, validated);
        Assert.Equal(
            new[]
            {
                SqliteOpenMode.ReadOnly,
                SqliteOpenMode.ReadOnly,
                SqliteOpenMode.ReadOnly,
            },
            environment.Factory.Modes);
    }

    [Fact]
    public async Task ValidateConsistencyAsync_NewPhysicalArchiveFailsWithoutMutatingCatalog()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            new ArchiveFileName(1, ArchiveFileName.NoSplit),
            Range(2026, 7, 1, 2026, 7, 31));
        await catalog.RebuildAsync(new DateOnly(2026, 9, 7));
        await archiveService.CreateAsync(
            new ArchiveFileName(2, ArchiveFileName.NoSplit),
            Range(2026, 8, 1, 2026, 8, 31));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.ValidateConsistencyAsync(new DateOnly(2026, 9, 7)));

        ArchiveSegmentDescriptor persisted = Assert.Single(await catalog.ReadAsync());
        Assert.Equal("archive_000001.db", persisted.FileName);
    }

    [Fact]
    public async Task ValidateConsistencyAsync_StaleSealingStateFailsWithoutMutatingCatalog()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            new ArchiveFileName(1, ArchiveFileName.NoSplit),
            Range(2026, 9, 1, 2026, 9, 5));
        IReadOnlyList<ArchiveSegmentDescriptor> rebuilt = await catalog.RebuildAsync(
            new DateOnly(2026, 9, 10));
        Assert.True(Assert.Single(rebuilt).IsSealed);

        InsertCurrentEvent(environment, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "2026-09-03");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.ValidateConsistencyAsync(new DateOnly(2026, 9, 10)));

        ArchiveSegmentDescriptor persisted = Assert.Single(await catalog.ReadAsync());
        Assert.True(persisted.IsSealed);
    }

    [Fact]
    public async Task ValidateConsistencyAsync_CallerCancellationBeforeWorkDoesNotOpenDatabase()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        environment.Factory.Modes.Clear();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            catalog.ValidateConsistencyAsync(
                new DateOnly(2026, 9, 7),
                cancellation.Token));

        Assert.Empty(environment.Factory.Modes);
    }

    [Fact]
    public async Task RebuildAsync_CurrentDuplicateKeepsPastArchiveUnsealedUntilPurge()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            new ArchiveFileName(1, ArchiveFileName.NoSplit),
            Range(2026, 9, 1, 2026, 9, 5));
        InsertCurrentEvent(environment, "11111111-1111-1111-1111-111111111111", "2026-09-03");

        IReadOnlyList<ArchiveSegmentDescriptor> withDuplicate = await catalog.RebuildAsync(
            new DateOnly(2026, 9, 7));
        Assert.False(Assert.Single(withDuplicate).IsSealed);

        environment.Execute(
            "DELETE FROM ClipboardHistoryEvent WHERE EventId = '11111111-1111-1111-1111-111111111111';");

        IReadOnlyList<ArchiveSegmentDescriptor> afterPurge = await catalog.RebuildAsync(
            new DateOnly(2026, 9, 7));
        Assert.True(Assert.Single(afterPurge).IsSealed);
    }

    [Fact]
    public async Task RebuildAsync_CoverageEndingTodayIsNotSealed()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            new ArchiveFileName(1, ArchiveFileName.NoSplit),
            Range(2026, 9, 1, 2026, 9, 7));

        IReadOnlyList<ArchiveSegmentDescriptor> result = await catalog.RebuildAsync(
            new DateOnly(2026, 9, 7));

        Assert.False(Assert.Single(result).IsSealed);
    }

    [Fact]
    public async Task RebuildAsync_OverlapFailsBeforeReplacingExistingProjection()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            new ArchiveFileName(1, ArchiveFileName.NoSplit),
            Range(2026, 9, 1, 2026, 9, 5));
        await catalog.RebuildAsync(new DateOnly(2026, 9, 10));

        await archiveService.CreateAsync(
            new ArchiveFileName(2, ArchiveFileName.NoSplit),
            Range(2026, 9, 5, 2026, 9, 9));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.RebuildAsync(new DateOnly(2026, 9, 10)));

        ArchiveSegmentDescriptor persisted = Assert.Single(await catalog.ReadAsync());
        Assert.Equal("archive_000001.db", persisted.FileName);
    }

    [Fact]
    public async Task RebuildAsync_NonCanonicalArchiveFileFailsBeforeCatalogMutation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        string invalidPath = Path.Combine(environment.Root, "Archive", "archive_bad.db");
        await File.WriteAllBytesAsync(invalidPath, [0x01, 0x02]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.RebuildAsync(new DateOnly(2026, 9, 7)));

        Assert.Empty(await catalog.ReadAsync());
    }

    [Fact]
    public async Task RebuildAsync_CallerCancellationBeforeWorkDoesNotMutateCatalog()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            catalog.RebuildAsync(new DateOnly(2026, 9, 7), cancellation.Token));

        Assert.Empty(await catalog.ReadAsync());
    }

    private static JournalDateRange Range(
        int sy, int sm, int sd,
        int ey, int em, int ed) =>
        new(new DateOnly(sy, sm, sd), new DateOnly(ey, em, ed));

    private static void InsertCurrentEvent(
        GlobalPolicyTestEnvironment environment,
        string eventId,
        string calendarDate)
    {
        environment.Execute($"""
            INSERT INTO ClipboardHistoryEvent (
                EventId,
                EventUtc,
                LocalOffsetMinutes,
                WindowsTimeZoneId,
                CalendarDate,
                SourceApplicationId,
                SourceProcessId,
                SourceExecutablePath,
                SourceApplicationUserModelId)
            VALUES (
                '{eventId}',
                '{calendarDate}T12:00:00.0000000+00:00',
                0,
                'UTC',
                '{calendarDate}',
                NULL, NULL, NULL, NULL);
            """);
    }
}
