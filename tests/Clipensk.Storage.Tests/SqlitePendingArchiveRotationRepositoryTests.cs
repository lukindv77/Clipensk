using Clipensk.Core.History;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class SqlitePendingArchiveRotationRepositoryTests
{
    private static readonly ArchiveRotationSettings CountAndDayPolicy = new()
    {
        MaxRecordCount = 1000,
        MaxCalendarDays = 10,
        ThresholdMode = ArchiveRotationThresholdMode.All,
    };

    [Fact]
    public async Task StartAdvanceAndClear_PreservesImmutablePlan()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        PendingArchiveRotationTarget[] targets =
        [
            new(0, new ArchiveFileName(101, ArchiveFileName.NoSplit), Guid.NewGuid(),
                Range(2026, 1, 1, 2026, 1, 10), 1200, 40960),
            new(1, new ArchiveFileName(102, ArchiveFileName.NoSplit), Guid.NewGuid(),
                Range(2026, 1, 11, 2026, 1, 20), 1500, 53248),
        ];

        PendingArchiveRotationOperation started =
            await repository.StartAsync(CountAndDayPolicy, targets);

        Assert.Equal(ArchiveRotationPhase.Planned, started.Phase);
        Assert.Equal(CountAndDayPolicy, started.PolicySnapshot);
        Assert.Equal(targets, started.Targets);

        PendingArchiveRotationOperation? persisted = await repository.ReadAsync();
        Assert.NotNull(persisted);
        Assert.Equal(started.OperationId, persisted!.OperationId);
        Assert.Equal(CountAndDayPolicy, persisted.PolicySnapshot);
        Assert.Equal(targets, persisted.Targets);
        Assert.Equal(started.CreatedAtUtc, persisted.CreatedAtUtc);

        ArchiveRotationPhase[] phases =
        [
            ArchiveRotationPhase.ReadyToPublish,
            ArchiveRotationPhase.PhysicalPublished,
            ArchiveRotationPhase.SourcePurged,
            ArchiveRotationPhase.CatalogPublished,
        ];
        ArchiveRotationPhase previous = ArchiveRotationPhase.Planned;
        foreach (ArchiveRotationPhase next in phases)
        {
            PendingArchiveRotationOperation advanced =
                await repository.AdvancePhaseAsync(started.OperationId, previous, next);
            Assert.Equal(next, advanced.Phase);
            Assert.Equal(targets, advanced.Targets);
            previous = next;
        }

        await repository.ClearCompletedAsync(started.OperationId);
        Assert.Null(await repository.ReadAsync());
    }

    [Fact]
    public async Task StartAsync_RejectsSecondOperation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        _ = await repository.StartAsync(CountAndDayPolicy, SingleTarget(201));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repository.StartAsync(CountAndDayPolicy, SingleTarget(202)));
    }

    [Fact]
    public async Task AdvancePhase_RejectsSkippedOrStaleTransition()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        PendingArchiveRotationOperation started =
            await repository.StartAsync(CountAndDayPolicy, SingleTarget(203));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await repository.AdvancePhaseAsync(
                started.OperationId,
                ArchiveRotationPhase.Planned,
                ArchiveRotationPhase.PhysicalPublished));

        _ = await repository.AdvancePhaseAsync(
            started.OperationId,
            ArchiveRotationPhase.Planned,
            ArchiveRotationPhase.ReadyToPublish);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repository.AdvancePhaseAsync(
                started.OperationId,
                ArchiveRotationPhase.Planned,
                ArchiveRotationPhase.ReadyToPublish));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repository.AdvancePhaseAsync(
                Guid.NewGuid(),
                ArchiveRotationPhase.ReadyToPublish,
                ArchiveRotationPhase.PhysicalPublished));
    }

    [Fact]
    public async Task ClearCompleted_RejectsPhaseBeforeCatalogPublished()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        PendingArchiveRotationOperation started =
            await repository.StartAsync(CountAndDayPolicy, SingleTarget(204));

        _ = await repository.AdvancePhaseAsync(
            started.OperationId, ArchiveRotationPhase.Planned, ArchiveRotationPhase.ReadyToPublish);
        _ = await repository.AdvancePhaseAsync(
            started.OperationId, ArchiveRotationPhase.ReadyToPublish, ArchiveRotationPhase.PhysicalPublished);
        _ = await repository.AdvancePhaseAsync(
            started.OperationId, ArchiveRotationPhase.PhysicalPublished, ArchiveRotationPhase.SourcePurged);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repository.ClearCompletedAsync(started.OperationId));
        Assert.NotNull(await repository.ReadAsync());
    }

    [Fact]
    public async Task StartAsync_RejectsCoverageGapAndDoesNotPersistMarker()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        PendingArchiveRotationTarget[] gapped =
        [
            new(0, new ArchiveFileName(205, ArchiveFileName.NoSplit), Guid.NewGuid(),
                Range(2026, 2, 1, 2026, 2, 10), 10, 4096),
            new(1, new ArchiveFileName(206, ArchiveFileName.NoSplit), Guid.NewGuid(),
                Range(2026, 2, 12, 2026, 2, 20), 10, 4096),
        ];

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await repository.StartAsync(CountAndDayPolicy, gapped));
        Assert.Null(await repository.ReadAsync());
    }

    [Fact]
    public async Task StartAsync_RejectsSplitSuffixTarget()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        PendingArchiveRotationTarget[] suffixed =
        [
            new(0, new ArchiveFileName(207, 1), Guid.NewGuid(),
                Range(2026, 3, 1, 2026, 3, 10), 10, 4096),
        ];

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await repository.StartAsync(CountAndDayPolicy, suffixed));
        Assert.Null(await repository.ReadAsync());
    }

    [Fact]
    public async Task StartAsync_RejectsNonIncreasingBaseNumbers()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        PendingArchiveRotationTarget[] descending =
        [
            new(0, new ArchiveFileName(209, ArchiveFileName.NoSplit), Guid.NewGuid(),
                Range(2026, 4, 1, 2026, 4, 10), 10, 4096),
            new(1, new ArchiveFileName(208, ArchiveFileName.NoSplit), Guid.NewGuid(),
                Range(2026, 4, 11, 2026, 4, 20), 10, 4096),
        ];

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await repository.StartAsync(CountAndDayPolicy, descending));
        Assert.Null(await repository.ReadAsync());
    }

    [Fact]
    public async Task StartAsync_RejectsMissingShadowPhysicalSizeEvidence()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        PendingArchiveRotationTarget[] sizeless =
        [
            new(0, new ArchiveFileName(210, ArchiveFileName.NoSplit), Guid.NewGuid(),
                Range(2026, 5, 1, 2026, 5, 10), 10, 0),
        ];

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await repository.StartAsync(CountAndDayPolicy, sizeless));
        Assert.Null(await repository.ReadAsync());
    }

    [Fact]
    public async Task StartAsync_RejectsEmptyPlanAndUnusableThresholdMode()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await repository.StartAsync(CountAndDayPolicy, Array.Empty<PendingArchiveRotationTarget>()));

        var ambiguous = new ArchiveRotationSettings
        {
            MaxRecordCount = 100,
            MaxCalendarDays = 5,
        };
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await repository.StartAsync(ambiguous, SingleTarget(211)));
        Assert.Null(await repository.ReadAsync());
    }

    [Fact]
    public async Task StartAsync_PersistsPhysicalSizeOnlyPolicyAndZeroRecordRange()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        var physicalOnly = new ArchiveRotationSettings { MaxBytes = 5_242_880 };
        PendingArchiveRotationTarget[] targets =
        [
            new(0, new ArchiveFileName(212, ArchiveFileName.NoSplit), Guid.NewGuid(),
                Range(2026, 6, 1, 2026, 6, 4), 0, 20480),
        ];

        _ = await repository.StartAsync(physicalOnly, targets);

        PendingArchiveRotationOperation? persisted = await repository.ReadAsync();
        Assert.NotNull(persisted);
        Assert.Equal(physicalOnly, persisted!.PolicySnapshot);
        Assert.Null(persisted.PolicySnapshot.ThresholdMode);
        Assert.Equal(targets, persisted.Targets);
    }

    [Fact]
    public async Task PendingSplitAndPendingRotation_BlockEachOther()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var rotations = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        var splits = new SqlitePendingArchiveSplitRepository(environment.Session, environment.Factory);
        var source = new ArchiveFileName(41, ArchiveFileName.NoSplit);
        Guid sourceId = Guid.NewGuid();
        PendingArchiveSplitSegment[] splitSegments =
        [
            new(0, source, sourceId, Range(2026, 7, 1, 2026, 7, 10)),
            new(1, source.NextSplit(1), Guid.NewGuid(), Range(2026, 7, 11, 2026, 7, 20)),
        ];

        PendingArchiveRotationOperation rotation =
            await rotations.StartAsync(CountAndDayPolicy, SingleTarget(213));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await splits.StartAsync(source, sourceId, Range(2026, 7, 1, 2026, 7, 20), splitSegments));
        Assert.Null(await splits.ReadAsync());

        await AdvanceToCompletionAsync(rotations, rotation.OperationId);
        await rotations.ClearCompletedAsync(rotation.OperationId);

        _ = await splits.StartAsync(source, sourceId, Range(2026, 7, 1, 2026, 7, 20), splitSegments);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rotations.StartAsync(CountAndDayPolicy, SingleTarget(214)));
        Assert.Null(await rotations.ReadAsync());
    }

    [Fact]
    public async Task ReadAsync_RejectsPersistedPlanThatLostATarget()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        PendingArchiveRotationTarget[] targets =
        [
            new(0, new ArchiveFileName(215, ArchiveFileName.NoSplit), Guid.NewGuid(),
                Range(2026, 8, 1, 2026, 8, 10), 10, 4096),
            new(1, new ArchiveFileName(216, ArchiveFileName.NoSplit), Guid.NewGuid(),
                Range(2026, 8, 11, 2026, 8, 20), 10, 4096),
        ];
        _ = await repository.StartAsync(CountAndDayPolicy, targets);

        environment.Execute("DELETE FROM PendingArchiveRotationTarget WHERE SegmentOrder = 0;");

        await Assert.ThrowsAsync<InvalidDataException>(async () => await repository.ReadAsync());
    }

    [Fact]
    public async Task ReadAsync_RequiresCurrentSchemaV10()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqlitePendingArchiveRotationRepository(environment.Session, environment.Factory);
        environment.DowngradeToV9();

        await Assert.ThrowsAsync<InvalidDataException>(async () => await repository.ReadAsync());
    }

    private static async Task AdvanceToCompletionAsync(
        SqlitePendingArchiveRotationRepository repository,
        Guid operationId)
    {
        _ = await repository.AdvancePhaseAsync(
            operationId, ArchiveRotationPhase.Planned, ArchiveRotationPhase.ReadyToPublish);
        _ = await repository.AdvancePhaseAsync(
            operationId, ArchiveRotationPhase.ReadyToPublish, ArchiveRotationPhase.PhysicalPublished);
        _ = await repository.AdvancePhaseAsync(
            operationId, ArchiveRotationPhase.PhysicalPublished, ArchiveRotationPhase.SourcePurged);
        _ = await repository.AdvancePhaseAsync(
            operationId, ArchiveRotationPhase.SourcePurged, ArchiveRotationPhase.CatalogPublished);
    }

    private static PendingArchiveRotationTarget[] SingleTarget(int baseNumber) =>
    [
        new(0, new ArchiveFileName(baseNumber, ArchiveFileName.NoSplit), Guid.NewGuid(),
            Range(2026, 9, 1, 2026, 9, 10), 25, 8192),
    ];

    private static JournalDateRange Range(
        int startYear, int startMonth, int startDay,
        int endYear, int endMonth, int endDay) =>
        new(new DateOnly(startYear, startMonth, startDay), new DateOnly(endYear, endMonth, endDay));
}
