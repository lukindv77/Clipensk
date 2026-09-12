using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveCatalogMaintenanceServiceTests
{
    [Fact]
    public async Task RebuildAsync_ReplacesArchiveSegmentProjectionFromPhysicalStorage()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        var maintenance = new ProtectedArchiveCatalogMaintenanceService(
            environment.Session,
            environment.Factory);

        DatabaseIdentity archive = await archiveService.CreateAsync(
            new ArchiveFileName(1, ArchiveFileName.NoSplit),
            new JournalDateRange(today.AddDays(-2), today.AddDays(-1)));
        Assert.Empty(await catalog.ReadAsync());

        IReadOnlyList<ArchiveSegmentDescriptor> rebuilt = await maintenance.RebuildAsync();
        IReadOnlyList<ArchiveSegmentDescriptor> persisted = await catalog.ReadAsync();

        ArchiveSegmentDescriptor descriptor = Assert.Single(rebuilt);
        Assert.Equal(archive.DatabaseId, descriptor.DatabaseId);
        Assert.Equal("archive_000001.db", descriptor.FileName);
        Assert.True(descriptor.IsSealed);
        Assert.Equal(rebuilt, persisted);
    }

    [Fact]
    public async Task RebuildAsync_WaitsForExistingMutationLease()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var maintenance = new ProtectedArchiveCatalogMaintenanceService(
            environment.Session,
            environment.Factory);
        ProtectedStorageMutationLease blocker =
            await environment.Session.AcquireMutationLeaseAsync();

        Task<IReadOnlyList<ArchiveSegmentDescriptor>> rebuildTask = maintenance.RebuildAsync();
        Assert.False(rebuildTask.IsCompleted);

        blocker.Dispose();
        IReadOnlyList<ArchiveSegmentDescriptor> rebuilt = await rebuildTask;

        Assert.Empty(rebuilt);
    }

    [Fact]
    public async Task RebuildAsync_CancellationWhileWaitingForMutationLeaseDoesNotOpenDatabase()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var maintenance = new ProtectedArchiveCatalogMaintenanceService(
            environment.Session,
            environment.Factory);
        ProtectedStorageMutationLease blocker =
            await environment.Session.AcquireMutationLeaseAsync();
        environment.Factory.Modes.Clear();
        using var cancellation = new CancellationTokenSource();

        Task<IReadOnlyList<ArchiveSegmentDescriptor>> rebuildTask =
            maintenance.RebuildAsync(cancellation.Token);
        Assert.False(rebuildTask.IsCompleted);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rebuildTask);
        Assert.Empty(environment.Factory.Modes);

        blocker.Dispose();
    }
}
