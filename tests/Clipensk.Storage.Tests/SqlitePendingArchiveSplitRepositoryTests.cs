using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class SqlitePendingArchiveSplitRepositoryTests
{
    [Fact]
    public async Task StartAdvanceAndClear_PreservesImmutablePlan()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        UpgradeTestCurrentToV9(environment);

        var source = new ArchiveFileName(25, ArchiveFileName.NoSplit);
        Guid sourceId = Guid.NewGuid();
        PendingArchiveSplitSegment[] segments =
        [
            new(0, source, sourceId, Range(2026, 1, 1, 2026, 1, 10)),
            new(1, source.NextSplit(1), Guid.NewGuid(), Range(2026, 1, 11, 2026, 1, 20)),
            new(2, source.NextSplit(2), Guid.NewGuid(), Range(2026, 1, 21, 2026, 1, 31)),
        ];

        var repository = new SqlitePendingArchiveSplitRepository(environment.Session, environment.Factory);
        PendingArchiveSplitOperation started = await repository.StartAsync(
            source,
            sourceId,
            Range(2026, 1, 1, 2026, 1, 31),
            segments);

        Assert.Equal(ArchiveSplitPhase.Planned, started.Phase);
        Assert.Equal(segments, started.Segments);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repository.StartAsync(
                source,
                sourceId,
                Range(2026, 1, 1, 2026, 1, 31),
                segments));

        PendingArchiveSplitOperation ready = await repository.AdvancePhaseAsync(
            started.OperationId,
            ArchiveSplitPhase.Planned,
            ArchiveSplitPhase.ReadyToPublish);
        PendingArchiveSplitOperation physical = await repository.AdvancePhaseAsync(
            started.OperationId,
            ArchiveSplitPhase.ReadyToPublish,
            ArchiveSplitPhase.PhysicalPublished);
        PendingArchiveSplitOperation catalog = await repository.AdvancePhaseAsync(
            started.OperationId,
            ArchiveSplitPhase.PhysicalPublished,
            ArchiveSplitPhase.CatalogPublished);

        Assert.Equal(ArchiveSplitPhase.ReadyToPublish, ready.Phase);
        Assert.Equal(ArchiveSplitPhase.PhysicalPublished, physical.Phase);
        Assert.Equal(ArchiveSplitPhase.CatalogPublished, catalog.Phase);
        Assert.Equal(started.Segments, catalog.Segments);

        await repository.ClearCompletedAsync(started.OperationId);
        Assert.Null(await repository.ReadAsync());
    }

    [Fact]
    public async Task AdvancePhase_RejectsSkippedOrStaleTransition()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        UpgradeTestCurrentToV9(environment);
        var repository = new SqlitePendingArchiveSplitRepository(environment.Session, environment.Factory);
        PendingArchiveSplitOperation started = await StartTwoSegmentOperation(repository);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await repository.AdvancePhaseAsync(
                started.OperationId,
                ArchiveSplitPhase.Planned,
                ArchiveSplitPhase.PhysicalPublished));

        _ = await repository.AdvancePhaseAsync(
            started.OperationId,
            ArchiveSplitPhase.Planned,
            ArchiveSplitPhase.ReadyToPublish);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repository.AdvancePhaseAsync(
                started.OperationId,
                ArchiveSplitPhase.Planned,
                ArchiveSplitPhase.ReadyToPublish));
    }

    [Fact]
    public async Task ClearCompleted_RejectsNonFinalPhase()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        UpgradeTestCurrentToV9(environment);
        var repository = new SqlitePendingArchiveSplitRepository(environment.Session, environment.Factory);
        PendingArchiveSplitOperation started = await StartTwoSegmentOperation(repository);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repository.ClearCompletedAsync(started.OperationId));
        Assert.NotNull(await repository.ReadAsync());
    }

    [Fact]
    public async Task StartAsync_RejectsGapAndDoesNotPersistMarker()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        UpgradeTestCurrentToV9(environment);
        var repository = new SqlitePendingArchiveSplitRepository(environment.Session, environment.Factory);
        var source = new ArchiveFileName(31, ArchiveFileName.NoSplit);
        Guid sourceId = Guid.NewGuid();
        PendingArchiveSplitSegment[] invalid =
        [
            new(0, source, sourceId, Range(2026, 2, 1, 2026, 2, 10)),
            new(1, source.NextSplit(1), Guid.NewGuid(), Range(2026, 2, 12, 2026, 2, 28)),
        ];

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await repository.StartAsync(
                source,
                sourceId,
                Range(2026, 2, 1, 2026, 2, 28),
                invalid));
        Assert.Null(await repository.ReadAsync());
    }

    private static async Task<PendingArchiveSplitOperation> StartTwoSegmentOperation(
        SqlitePendingArchiveSplitRepository repository)
    {
        var source = new ArchiveFileName(30, ArchiveFileName.NoSplit);
        Guid sourceId = Guid.NewGuid();
        return await repository.StartAsync(
            source,
            sourceId,
            Range(2026, 3, 1, 2026, 3, 31),
            [
                new(0, source, sourceId, Range(2026, 3, 1, 2026, 3, 15)),
                new(1, source.NextSplit(1), Guid.NewGuid(), Range(2026, 3, 16, 2026, 3, 31)),
            ]);
    }

    private static void UpgradeTestCurrentToV9(GlobalPolicyTestEnvironment environment)
    {
        environment.Execute("""
            CREATE TABLE PendingArchiveSplit (
                SingletonId INTEGER NOT NULL PRIMARY KEY CHECK (SingletonId = 1),
                OperationId TEXT NOT NULL UNIQUE CHECK (length(OperationId) > 0),
                SourceFileName TEXT NOT NULL CHECK (length(SourceFileName) > 0),
                SourceDatabaseId TEXT NOT NULL CHECK (length(SourceDatabaseId) > 0),
                SourceCoverageStartDate TEXT NOT NULL CHECK (length(SourceCoverageStartDate) > 0),
                SourceCoverageEndDate TEXT NOT NULL CHECK (length(SourceCoverageEndDate) > 0),
                Phase INTEGER NOT NULL CHECK (Phase >= 0 AND Phase <= 3),
                CreatedAtUtc TEXT NOT NULL CHECK (length(CreatedAtUtc) > 0)
            );
            CREATE TABLE PendingArchiveSplitSegment (
                OperationId TEXT NOT NULL,
                SegmentOrder INTEGER NOT NULL CHECK (SegmentOrder >= 0),
                FileName TEXT NOT NULL CHECK (length(FileName) > 0),
                DatabaseId TEXT NOT NULL CHECK (length(DatabaseId) > 0),
                CoverageStartDate TEXT NOT NULL CHECK (length(CoverageStartDate) > 0),
                CoverageEndDate TEXT NOT NULL CHECK (length(CoverageEndDate) > 0),
                PRIMARY KEY (OperationId, SegmentOrder),
                UNIQUE (OperationId, FileName),
                UNIQUE (OperationId, DatabaseId),
                FOREIGN KEY (OperationId) REFERENCES PendingArchiveSplit(OperationId) ON DELETE CASCADE
            );
            UPDATE DatabaseIdentity SET SchemaVersion = 9 WHERE DatabaseRole = 'Current';
            PRAGMA user_version = 9;
            """);
    }

    private static JournalDateRange Range(
        int startYear,
        int startMonth,
        int startDay,
        int endYear,
        int endMonth,
        int endDay) =>
        new(
            new DateOnly(startYear, startMonth, startDay),
            new DateOnly(endYear, endMonth, endDay));
}
