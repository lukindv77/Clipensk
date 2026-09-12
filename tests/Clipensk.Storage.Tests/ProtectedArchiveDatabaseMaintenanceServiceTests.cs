using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveDatabaseMaintenanceServiceTests
{
    [Fact]
    public async Task VacuumAsync_ValidatesBeforeWriteAndPreservesIdentity()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var fileName = new ArchiveFileName(41, ArchiveFileName.NoSplit);
        DatabaseIdentity created = await archiveService.CreateAsync(
            fileName,
            Range(2026, 7, 1, 2026, 7, 31));
        environment.Factory.Modes.Clear();

        var service = new ProtectedArchiveDatabaseMaintenanceService(
            environment.Session,
            environment.Factory);
        DatabaseIdentity maintained = await service.VacuumAsync(fileName);

        Assert.Equal(created, maintained);
        Assert.Equal(
            new[] { SqliteOpenMode.ReadOnly, SqliteOpenMode.ReadWrite },
            environment.Factory.Modes);

        DatabaseIdentity validated = await archiveService.ValidateAsync(fileName);
        Assert.Equal(created, validated);
    }

    [Fact]
    public async Task OptimizeAsync_ValidatesBeforeWriteAndPreservesIdentity()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var fileName = new ArchiveFileName(42, 3);
        DatabaseIdentity created = await archiveService.CreateAsync(
            fileName,
            Range(2026, 8, 1, 2026, 8, 15));
        environment.Factory.Modes.Clear();

        var service = new ProtectedArchiveDatabaseMaintenanceService(
            environment.Session,
            environment.Factory);
        DatabaseIdentity maintained = await service.OptimizeAsync(fileName);

        Assert.Equal(created, maintained);
        Assert.Equal(
            new[] { SqliteOpenMode.ReadOnly, SqliteOpenMode.ReadWrite },
            environment.Factory.Modes);

        DatabaseIdentity validated = await archiveService.ValidateAsync(fileName);
        Assert.Equal(created, validated);
    }

    [Fact]
    public async Task VacuumAsync_HoldsMutationLeaseThroughReadWriteOpen()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var fileName = new ArchiveFileName(43, ArchiveFileName.NoSplit);
        await new ProtectedArchiveDatabaseService(environment.Session, environment.Factory)
            .CreateAsync(fileName, Range(2026, 6, 1, 2026, 6, 30));

        using var readWriteEntered = new ManualResetEventSlim();
        using var allowReadWrite = new ManualResetEventSlim();
        environment.Factory.OnOpen = (connection, mode) =>
        {
            if (mode == SqliteOpenMode.ReadWrite &&
                string.Equals(
                    Path.GetFileName(connection.DataSource),
                    fileName.FileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                readWriteEntered.Set();
                allowReadWrite.Wait(TimeSpan.FromSeconds(10));
            }
        };

        var service = new ProtectedArchiveDatabaseMaintenanceService(
            environment.Session,
            environment.Factory);
        Task<DatabaseIdentity> maintenanceTask = service.VacuumAsync(fileName);

        Assert.True(readWriteEntered.Wait(TimeSpan.FromSeconds(10)));
        using var competingCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using ProtectedStorageMutationLease competing =
                await environment.Session.AcquireMutationLeaseAsync(competingCancellation.Token);
        });

        allowReadWrite.Set();
        _ = await maintenanceTask;

        environment.Factory.OnOpen = null;
        using ProtectedStorageMutationLease after =
            await environment.Session.AcquireMutationLeaseAsync();
    }

    [Fact]
    public async Task OptimizeAsync_CancellationWhileWaitingForMutationLeaseDoesNotOpenArchive()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var fileName = new ArchiveFileName(44, ArchiveFileName.NoSplit);
        await new ProtectedArchiveDatabaseService(environment.Session, environment.Factory)
            .CreateAsync(fileName, Range(2026, 5, 1, 2026, 5, 31));
        environment.Factory.Modes.Clear();

        using ProtectedStorageMutationLease blocking =
            await environment.Session.AcquireMutationLeaseAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var service = new ProtectedArchiveDatabaseMaintenanceService(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.OptimizeAsync(fileName, cancellation.Token));
        Assert.Empty(environment.Factory.Modes);
    }

    [Fact]
    public async Task VacuumAsync_InvalidArchiveNeverOpensReadWrite()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var fileName = new ArchiveFileName(45, ArchiveFileName.NoSplit);
        await new ProtectedArchiveDatabaseService(environment.Session, environment.Factory)
            .CreateAsync(fileName, Range(2026, 4, 1, 2026, 4, 30));

        using (SqliteConnection connection = environment.Factory.Open(
                   ArchivePath(environment, fileName),
                   environment.Key,
                   SqliteOpenMode.ReadWrite))
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = $"UPDATE DatabaseIdentity SET StorageId = '{Guid.NewGuid():D}';";
            command.ExecuteNonQuery();
        }
        environment.Factory.Modes.Clear();

        var service = new ProtectedArchiveDatabaseMaintenanceService(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.VacuumAsync(fileName));

        Assert.Equal(new[] { SqliteOpenMode.ReadOnly }, environment.Factory.Modes);
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

    private static string ArchivePath(
        GlobalPolicyTestEnvironment environment,
        ArchiveFileName fileName) =>
        Path.Combine(environment.Root, "Archive", fileName.FileName);
}
