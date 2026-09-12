using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveCatalogMaintenanceServiceTests
{
    [Fact]
    public async Task RebuildAsync_HoldsMutationLeaseUntilCatalogWriteCompletes()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            new ArchiveFileName(1, ArchiveFileName.NoSplit),
            new JournalDateRange(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31)));

        using var catalogWriteEntered = new ManualResetEventSlim();
        using var allowCatalogWrite = new ManualResetEventSlim();
        environment.Factory.OnOpen = (connection, mode) =>
        {
            if (mode == SqliteOpenMode.ReadWrite &&
                string.Equals(
                    Path.GetFileName(connection.DataSource),
                    "storage-catalog.db",
                    StringComparison.OrdinalIgnoreCase))
            {
                catalogWriteEntered.Set();
                allowCatalogWrite.Wait(TimeSpan.FromSeconds(10));
            }
        };

        var service = new ProtectedArchiveCatalogMaintenanceService(
            environment.Session,
            environment.Factory);
        Task<IReadOnlyList<ArchiveSegmentDescriptor>> rebuildTask = service.RebuildAsync(
            new DateOnly(2026, 9, 12));

        Assert.True(catalogWriteEntered.Wait(TimeSpan.FromSeconds(10)));

        using var competingCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using ProtectedStorageMutationLease competing =
                await environment.Session.AcquireMutationLeaseAsync(competingCancellation.Token);
        });

        allowCatalogWrite.Set();
        IReadOnlyList<ArchiveSegmentDescriptor> rebuilt = await rebuildTask;
        Assert.Single(rebuilt);

        environment.Factory.OnOpen = null;
        using ProtectedStorageMutationLease after =
            await environment.Session.AcquireMutationLeaseAsync();
    }
}
