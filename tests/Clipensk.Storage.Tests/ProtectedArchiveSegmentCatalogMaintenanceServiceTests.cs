using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveSegmentCatalogMaintenanceServiceTests
{
    [Fact]
    public async Task RebuildAsync_RepairsStaleArchiveProjection()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        var firstRange = new JournalDateRange(today.AddDays(-20), today.AddDays(-11));
        var secondRange = new JournalDateRange(today.AddDays(-10), today.AddDays(-1));
        var firstArchive = new ArchiveFileName(51, ArchiveFileName.NoSplit);
        var secondArchive = new ArchiveFileName(52, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);

        await archiveService.CreateAsync(firstArchive, firstRange);
        ArchiveSegmentDescriptor initial = Assert.Single(await catalog.RebuildAsync(today));
        Assert.Equal(firstArchive.FileName, initial.FileName);

        await archiveService.CreateAsync(secondArchive, secondRange);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.ValidateConsistencyAsync(today));

        var maintenance = new ProtectedArchiveSegmentCatalogMaintenanceService(
            environment.Session,
            environment.Factory);
        IReadOnlyList<ArchiveSegmentDescriptor> rebuilt = await maintenance.RebuildAsync();

        Assert.Equal(2, rebuilt.Count);
        Assert.Contains(rebuilt, item => item.FileName == firstArchive.FileName);
        Assert.Contains(rebuilt, item => item.FileName == secondArchive.FileName);
        Assert.Equal(rebuilt, await catalog.ValidateConsistencyAsync(today));
    }

    [Fact]
    public async Task RebuildAsync_WaitsForMutationLeaseBeforeOpeningDatabases()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        using ProtectedStorageMutationLease heldLease =
            await environment.Session.AcquireMutationLeaseAsync();
        environment.Factory.Modes.Clear();
        using var cancellation = new CancellationTokenSource();
        var maintenance = new ProtectedArchiveSegmentCatalogMaintenanceService(
            environment.Session,
            environment.Factory);

        Task<IReadOnlyList<ArchiveSegmentDescriptor>> rebuildTask =
            maintenance.RebuildAsync(cancellation.Token);

        Assert.False(rebuildTask.IsCompleted);
        Assert.Empty(environment.Factory.Modes);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rebuildTask);
        Assert.Empty(environment.Factory.Modes);
    }
}
