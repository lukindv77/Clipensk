using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveSplitCoordinatorTests
{
    private static readonly DateOnly CurrentCalendarDate = new(2026, 9, 17);

    [Fact]
    public async Task SplitAsync_CompletesEndToEndAndClearsPendingMarker()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        var sourceFileName = new ArchiveFileName(81, ArchiveFileName.NoSplit);
        var sourceCoverage = new JournalDateRange(
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 31));

        _ = await new ProtectedArchiveDatabaseService(
            environment.Session,
            environment.Factory).CreateAsync(sourceFileName, sourceCoverage);
        _ = await new ProtectedArchiveCatalogMaintenanceService(
            environment.Session,
            environment.Factory).RebuildAsync(CurrentCalendarDate);

        var coordinator = new ProtectedArchiveSplitCoordinator(
            environment.Session,
            environment.Factory);
        IReadOnlyList<ArchiveSegmentDescriptor> result = await coordinator.SplitAsync(
            sourceFileName,
            [
                new JournalDateRange(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 15)),
                new JournalDateRange(new DateOnly(2026, 8, 16), new DateOnly(2026, 8, 31)),
            ],
            CurrentCalendarDate);

        Assert.Equal(2, result.Count);
        Assert.Equal(sourceFileName.FileName, result[0].FileName);
        Assert.Equal(new DateOnly(2026, 8, 15), result[0].Coverage.EndDate);
        Assert.Equal("archive_000081_0001.db", result[1].FileName);
        Assert.Equal(new DateOnly(2026, 8, 16), result[1].Coverage.StartDate);
        Assert.Null(await coordinator.ReadPendingAsync());
        Assert.Equal(
            result.ToArray(),
            (await new ProtectedArchiveSegmentCatalog(
                environment.Session,
                environment.Factory).ValidateConsistencyAsync(CurrentCalendarDate)).ToArray());
        Assert.True((await environment.ValidateAsync()).IsSuccess);
    }

    [Fact]
    public async Task ResumeAsync_FromPlannedOperationCompletesEndToEnd()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        var sourceFileName = new ArchiveFileName(82, ArchiveFileName.NoSplit);
        var sourceCoverage = new JournalDateRange(
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31));
        DatabaseIdentity sourceIdentity = await new ProtectedArchiveDatabaseService(
            environment.Session,
            environment.Factory).CreateAsync(sourceFileName, sourceCoverage);
        _ = await new ProtectedArchiveCatalogMaintenanceService(
            environment.Session,
            environment.Factory).RebuildAsync(CurrentCalendarDate);

        IReadOnlyList<PendingArchiveSplitSegment> plan = new ArchiveSplitPlanner().Build(
            sourceFileName,
            sourceIdentity.DatabaseId,
            sourceCoverage,
            [
                new JournalDateRange(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 10)),
                new JournalDateRange(new DateOnly(2026, 7, 11), new DateOnly(2026, 7, 20)),
                new JournalDateRange(new DateOnly(2026, 7, 21), new DateOnly(2026, 7, 31)),
            ],
            [sourceFileName],
            IdFactory(Id(701), Id(702)));
        PendingArchiveSplitOperation pending = await new SqlitePendingArchiveSplitRepository(
            environment.Session,
            environment.Factory).StartAsync(
                sourceFileName,
                sourceIdentity.DatabaseId,
                sourceCoverage,
                plan);

        var coordinator = new ProtectedArchiveSplitCoordinator(
            environment.Session,
            environment.Factory);
        IReadOnlyList<ArchiveSegmentDescriptor> result = await coordinator.ResumeAsync(
            pending.OperationId,
            CurrentCalendarDate);

        Assert.Equal(3, result.Count);
        Assert.Null(await coordinator.ReadPendingAsync());
        Assert.True((await environment.ValidateAsync()).IsSuccess);
    }

    private static Func<Guid> IdFactory(params Guid[] ids)
    {
        int index = 0;
        return () => index < ids.Length
            ? ids[index++]
            : throw new InvalidOperationException("Test DatabaseId factory exhausted.");
    }

    private static Guid Id(int value) =>
        new(value, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);
}
