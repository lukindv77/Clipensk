using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.ExternalFiles;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveSplitRecoveryServiceTests
{
    private static readonly DateOnly CurrentCalendarDate = new(2026, 9, 17);

    [Theory]
    [InlineData(ArchiveSplitPhase.Planned)]
    [InlineData(ArchiveSplitPhase.ReadyToPublish)]
    [InlineData(ArchiveSplitPhase.PhysicalPublished)]
    [InlineData(ArchiveSplitPhase.CatalogPublished)]
    public async Task RecoverAsync_CompletesFromEveryDurablePhase(
        ArchiveSplitPhase startPhase)
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        PendingArchiveSplitOperation operation =
            await CreatePendingSplitAsync(environment);
        operation = await AdvanceToPhaseAsync(environment, operation, startPhase);
        Assert.Equal(startPhase, operation.Phase);

        var service = new ProtectedArchiveSplitRecoveryService(
            environment.Session,
            environment.Factory);
        IReadOnlyList<ArchiveSegmentDescriptor> recovered =
            await service.RecoverAsync(
                operation.OperationId,
                CurrentCalendarDate);

        Assert.Equal(operation.Segments.Count, recovered.Count);
        for (int index = 0; index < operation.Segments.Count; index++)
        {
            PendingArchiveSplitSegment planned = operation.Segments[index];
            ArchiveSegmentDescriptor actual = recovered[index];
            Assert.Equal(planned.DatabaseId, actual.DatabaseId);
            Assert.Equal(planned.FileName.FileName, actual.FileName);
            Assert.Equal(planned.Coverage, actual.Coverage);
        }

        Assert.Null(await new SqlitePendingArchiveSplitRepository(
            environment.Session,
            environment.Factory).ReadAsync());
        Assert.Equal(
            recovered.ToArray(),
            (await new ProtectedArchiveSegmentCatalog(
                environment.Session,
                environment.Factory).ValidateConsistencyAsync(CurrentCalendarDate)).ToArray());
        Assert.True((await environment.ValidateAsync()).IsSuccess);
    }

    [Fact]
    public async Task RecoverAsync_RejectsDifferentOperationId()
    {
        using GlobalPolicyTestEnvironment environment =
            await GlobalPolicyTestEnvironment.CreateAsync();
        PendingArchiveSplitOperation operation =
            await CreatePendingSplitAsync(environment);
        var service = new ProtectedArchiveSplitRecoveryService(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RecoverAsync(Guid.NewGuid(), CurrentCalendarDate));

        PendingArchiveSplitOperation persisted =
            Assert.IsType<PendingArchiveSplitOperation>(
                await new SqlitePendingArchiveSplitRepository(
                    environment.Session,
                    environment.Factory).ReadAsync());
        Assert.Equal(operation.OperationId, persisted.OperationId);
        Assert.Equal(ArchiveSplitPhase.Planned, persisted.Phase);
    }

    private static async Task<PendingArchiveSplitOperation> CreatePendingSplitAsync(
        GlobalPolicyTestEnvironment environment)
    {
        var sourceFileName = new ArchiveFileName(
            81,
            ArchiveFileName.NoSplit);
        var sourceCoverage = new JournalDateRange(
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 4));
        DatabaseIdentity sourceIdentity = await new ProtectedArchiveDatabaseService(
            environment.Session,
            environment.Factory).CreateAsync(sourceFileName, sourceCoverage);

        _ = await new ProtectedExternalPayloadCatalogRebuildService(
            environment.Session,
            environment.Factory).RebuildAsync();
        _ = await new ProtectedArchiveCatalogMaintenanceService(
            environment.Session,
            environment.Factory).RebuildAsync(CurrentCalendarDate);

        JournalDateRange[] ranges =
        [
            new(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 2)),
            new(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 4)),
        ];
        IReadOnlyList<PendingArchiveSplitSegment> segments =
            new ArchiveSplitPlanner().Build(
                sourceFileName,
                sourceIdentity.DatabaseId,
                sourceCoverage,
                ranges,
                [sourceFileName],
                Guid.NewGuid);

        return await new SqlitePendingArchiveSplitRepository(
            environment.Session,
            environment.Factory).StartAsync(
                sourceFileName,
                sourceIdentity.DatabaseId,
                sourceCoverage,
                segments);
    }

    private static async Task<PendingArchiveSplitOperation> AdvanceToPhaseAsync(
        GlobalPolicyTestEnvironment environment,
        PendingArchiveSplitOperation operation,
        ArchiveSplitPhase targetPhase)
    {
        if (targetPhase == ArchiveSplitPhase.Planned)
        {
            return operation;
        }

        var builder = new ProtectedArchiveSplitShadowBuilder(
            environment.Session,
            environment.Factory);
        operation = await builder.BuildAsync(operation.OperationId);
        if (targetPhase == ArchiveSplitPhase.ReadyToPublish)
        {
            return operation;
        }

        var publisher = new ProtectedArchiveSplitPublisher(
            environment.Session,
            environment.Factory);
        operation = await publisher.PublishOrRecoverAsync(operation.OperationId);
        if (targetPhase == ArchiveSplitPhase.PhysicalPublished)
        {
            return operation;
        }

        bool injected = false;
        var catalogPublisher = new ProtectedArchiveSplitCatalogPublisher(
            environment.Session,
            environment.Factory,
            checkpoint =>
            {
                if (!injected && checkpoint == ArchiveSplitCatalogCheckpoint.AfterCatalogPhaseCommit)
                {
                    injected = true;
                    throw new SimulatedCrashException(checkpoint);
                }
            });
        await Assert.ThrowsAsync<SimulatedCrashException>(() =>
            catalogPublisher.PublishOrRecoverAsync(
                operation.OperationId,
                CurrentCalendarDate));
        Assert.True(injected);

        return Assert.IsType<PendingArchiveSplitOperation>(
            await new SqlitePendingArchiveSplitRepository(
                environment.Session,
                environment.Factory).ReadAsync());
    }

    private sealed class SimulatedCrashException(ArchiveSplitCatalogCheckpoint checkpoint)
        : Exception($"Simulated crash at {checkpoint}.");
}
