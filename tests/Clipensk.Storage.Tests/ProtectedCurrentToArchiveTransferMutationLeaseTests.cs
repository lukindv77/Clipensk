using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedCurrentToArchiveTransferMutationLeaseTests
{
    [Fact]
    public async Task TransferAsync_HoldsSessionMutationLeaseBeforeCurrentObservation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DateOnly day = ClosedDay(3);
        var range = new JournalDateRange(day, day);
        var archiveFileName = new ArchiveFileName(41, ArchiveFileName.NoSplit);
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        await archiveService.CreateAsync(archiveFileName, range);

        Task<ProtectedStorageMutationLease>? competingLeaseTask = null;
        environment.Factory.OnOpen = (connection, mode) =>
        {
            if (competingLeaseTask is not null ||
                mode != SqliteOpenMode.ReadOnly ||
                !string.Equals(
                    Path.GetFileName(connection.DataSource),
                    "current.db",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            competingLeaseTask = environment.Session.AcquireMutationLeaseAsync().AsTask();
            bool completedBeforeCurrentObservation = competingLeaseTask.IsCompleted;
            if (completedBeforeCurrentObservation &&
                competingLeaseTask.Status == TaskStatus.RanToCompletion)
            {
                competingLeaseTask.Result.Dispose();
            }

            Assert.False(
                completedBeforeCurrentObservation,
                "Current-to-Archive transfer must own the session mutation lease before reading Current.");
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

    private static DateOnly ClosedDay(int daysAgo) =>
        DateOnly.FromDateTime(DateTime.Now).AddDays(-daysAgo);
}
