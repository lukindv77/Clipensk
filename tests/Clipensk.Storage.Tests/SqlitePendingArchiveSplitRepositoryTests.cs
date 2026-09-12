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

    [Fact]
    public async Task StartAsync_RejectsNoncanonicalArchiveFileNameBeforeOpening()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqlitePendingArchiveSplitRepository(environment.Session, environment.Factory);
        var source = new ArchiveFileName(1_000_000, ArchiveFileName.NoSplit);
        Guid sourceId = Guid.NewGuid();
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await repository.StartAsync(
                source,
                sourceId,
                Range(2026, 4, 1, 2026, 4, 30),
                [
                    new(0, source, sourceId, Range(2026, 4, 1, 2026, 4, 15)),
                    new(1, source.NextSplit(1), Guid.NewGuid(), Range(2026, 4, 16, 2026, 4, 30)),
                ]));

        Assert.Empty(environment.Factory.Modes);
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
