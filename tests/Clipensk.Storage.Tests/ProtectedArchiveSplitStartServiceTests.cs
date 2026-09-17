using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveSplitStartServiceTests
{
    private static readonly DateOnly CurrentCalendarDate = new(2026, 9, 17);

    [Fact]
    public async Task StartAndCompleteAsync_CompletesSplitAndAllocatesAfterOccupiedFamilySuffix()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        var archiveService = new ProtectedArchiveDatabaseService(
            environment.Session,
            environment.Factory);
        var sourceFileName = new ArchiveFileName(90, ArchiveFileName.NoSplit);
        var sourceCoverage = new JournalDateRange(
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 4));
        _ = await archiveService.CreateAsync(sourceFileName, sourceCoverage);
        _ = await archiveService.CreateAsync(
            new ArchiveFileName(90, 3),
            new JournalDateRange(
                new DateOnly(2026, 7, 1),
                new DateOnly(2026, 7, 2)));

        IReadOnlyList<ArchiveSegmentDescriptor> catalog =
            await new ProtectedArchiveCatalogMaintenanceService(
                environment.Session,
                environment.Factory).RebuildAsync(CurrentCalendarDate);
        ArchiveSegmentDescriptor source = Assert.Single(
            catalog,
            descriptor => descriptor.FileName == sourceFileName.FileName);

        JournalDateRange[] requestedRanges =
        [
            new(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 2)),
            new(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 4)),
        ];
        IReadOnlyList<ArchiveSegmentDescriptor> result =
            await new ProtectedArchiveSplitStartService(
                environment.Session,
                environment.Factory).StartAndCompleteAsync(
                    source,
                    requestedRanges,
                    CurrentCalendarDate);

        Assert.Contains(
            result,
            descriptor => descriptor.FileName == sourceFileName.FileName &&
                descriptor.DatabaseId == source.DatabaseId &&
                descriptor.Coverage == requestedRanges[0]);
        Assert.Contains(
            result,
            descriptor => descriptor.FileName == new ArchiveFileName(90, 4).FileName &&
                descriptor.Coverage == requestedRanges[1]);
        Assert.Contains(
            result,
            descriptor => descriptor.FileName == new ArchiveFileName(90, 3).FileName);
        Assert.Null(await new SqlitePendingArchiveSplitRepository(
            environment.Session,
            environment.Factory).ReadAsync());
        Assert.True((await environment.ValidateAsync()).IsSuccess);
    }

    [Fact]
    public async Task StartAndCompleteAsync_StaleSourceSnapshotFailsBeforeMarkerCreation()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        ArchiveSegmentDescriptor source = await CreateSourceAndCatalogAsync(environment, 91);
        ArchiveSegmentDescriptor stale = source with { DatabaseId = Guid.NewGuid() };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProtectedArchiveSplitStartService(
                environment.Session,
                environment.Factory).StartAndCompleteAsync(
                    stale,
                    SplitInHalf(source.Coverage),
                    CurrentCalendarDate));

        Assert.Null(await new SqlitePendingArchiveSplitRepository(
            environment.Session,
            environment.Factory).ReadAsync());
        DatabaseIdentity identity = await new ProtectedArchiveDatabaseService(
            environment.Session,
            environment.Factory).ValidateAsync(
                ArchiveFileName.Parse(source.FileName));
        Assert.Equal(source.DatabaseId, identity.DatabaseId);
        Assert.Equal(source.Coverage.StartDate, identity.CoverageStartDate);
        Assert.Equal(source.Coverage.EndDate, identity.CoverageEndDate);
    }

    [Fact]
    public async Task StartAndCompleteAsync_InvalidPartitionFailsBeforeMarkerCreation()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        ArchiveSegmentDescriptor source = await CreateSourceAndCatalogAsync(environment, 92);
        JournalDateRange[] invalid =
        [
            new(source.Coverage.StartDate, source.Coverage.StartDate),
            new(source.Coverage.EndDate, source.Coverage.EndDate),
        ];

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new ProtectedArchiveSplitStartService(
                environment.Session,
                environment.Factory).StartAndCompleteAsync(
                    source,
                    invalid,
                    CurrentCalendarDate));

        Assert.Null(await new SqlitePendingArchiveSplitRepository(
            environment.Session,
            environment.Factory).ReadAsync());
    }

    [Fact]
    public async Task StartAndCompleteAsync_ExistingPendingOperationIsPreserved()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        ArchiveSegmentDescriptor source = await CreateSourceAndCatalogAsync(environment, 93);
        ArchiveFileName sourceFileName = ArchiveFileName.Parse(source.FileName);
        IReadOnlyList<JournalDateRange> ranges = SplitInHalf(source.Coverage);
        IReadOnlyList<PendingArchiveSplitSegment> segments = new ArchiveSplitPlanner().Build(
            sourceFileName,
            source.DatabaseId,
            source.Coverage,
            ranges,
            [sourceFileName],
            Guid.NewGuid);
        PendingArchiveSplitOperation existing = await new SqlitePendingArchiveSplitRepository(
            environment.Session,
            environment.Factory).StartAsync(
                sourceFileName,
                source.DatabaseId,
                source.Coverage,
                segments);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ProtectedArchiveSplitStartService(
                environment.Session,
                environment.Factory).StartAndCompleteAsync(
                    source,
                    ranges,
                    CurrentCalendarDate));

        PendingArchiveSplitOperation persisted =
            Assert.IsType<PendingArchiveSplitOperation>(
                await new SqlitePendingArchiveSplitRepository(
                    environment.Session,
                    environment.Factory).ReadAsync());
        Assert.Equal(existing.OperationId, persisted.OperationId);
        Assert.Equal(ArchiveSplitPhase.Planned, persisted.Phase);
    }

    private static async Task<ArchiveSegmentDescriptor> CreateSourceAndCatalogAsync(
        GlobalPolicyTestEnvironment environment,
        long baseNumber)
    {
        var sourceFileName = new ArchiveFileName(baseNumber, ArchiveFileName.NoSplit);
        var sourceCoverage = new JournalDateRange(
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 4));
        _ = await new ProtectedArchiveDatabaseService(
            environment.Session,
            environment.Factory).CreateAsync(sourceFileName, sourceCoverage);
        IReadOnlyList<ArchiveSegmentDescriptor> catalog =
            await new ProtectedArchiveCatalogMaintenanceService(
                environment.Session,
                environment.Factory).RebuildAsync(CurrentCalendarDate);
        return Assert.Single(
            catalog,
            descriptor => descriptor.FileName == sourceFileName.FileName);
    }

    private static IReadOnlyList<JournalDateRange> SplitInHalf(JournalDateRange coverage) =>
    [
        new(coverage.StartDate, coverage.StartDate.AddDays(1)),
        new(coverage.StartDate.AddDays(2), coverage.EndDate),
    ];
}
