using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedCurrentToArchiveTransferMutationLeaseTests
{
    [Fact]
    public async Task TransferAsync_HoldsSessionMutationLeaseBeforeAnyStorageOpen()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = ClosedDay(3);
        var range = new JournalDateRange(day, day);
        var archiveFileName = new ArchiveFileName(41, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(archiveFileName, range);

        Task<ProtectedStorageMutationLease>? competingLeaseTask = null;
        environment.Factory.OnOpen = (_, _) =>
        {
            if (competingLeaseTask is not null)
            {
                return;
            }

            competingLeaseTask = environment.Session.AcquireMutationLeaseAsync().AsTask();
            bool completedBeforeStorageWork = competingLeaseTask.IsCompleted;
            if (completedBeforeStorageWork && competingLeaseTask.Status == TaskStatus.RanToCompletion)
            {
                competingLeaseTask.Result.Dispose();
            }

            Assert.False(
                completedBeforeStorageWork,
                "Current-to-Archive transfer must own the session mutation lease before opening storage.");
        };

        try
        {
            var service = new ProtectedCurrentToArchiveTransferService(
                environment.Session,
                environment.Factory);

            CurrentToArchiveTransferResult result = await service.TransferAsync(
                archiveFileName,
                range);

            Assert.Equal(0, result.CopiedEventCount);
            Assert.Equal(0, result.PurgedEventCount);
            Assert.NotNull(competingLeaseTask);

            using ProtectedStorageMutationLease competingLease = await competingLeaseTask!;
        }
        finally
        {
            environment.Factory.OnOpen = null;
        }
    }

    [Fact]
    public async Task TransferAsync_CancellationWhileWaitingForMutationLeaseDoesNotOpenStorage()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = ClosedDay(4);
        var range = new JournalDateRange(day, day);
        var archiveFileName = new ArchiveFileName(42, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(archiveFileName, range);

        environment.Factory.Modes.Clear();
        using ProtectedStorageMutationLease heldLease =
            await environment.Session.AcquireMutationLeaseAsync();
        using var cancellation = new CancellationTokenSource();
        var service = new ProtectedCurrentToArchiveTransferService(
            environment.Session,
            environment.Factory);

        Task<CurrentToArchiveTransferResult> transferTask = service.TransferAsync(
            archiveFileName,
            range,
            cancellation.Token);

        Assert.False(transferTask.IsCompleted);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await transferTask);
        Assert.Empty(environment.Factory.Modes);
    }

    private static DateOnly ClosedDay(int daysAgo) =>
        DateOnly.FromDateTime(DateTime.Now).AddDays(-daysAgo);
}
