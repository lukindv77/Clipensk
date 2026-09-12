using System.Globalization;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedCurrentDatabaseMaintenanceServiceTests
{
    [Fact]
    public async Task VacuumAsync_ValidatesPairReadOnlyBeforeWriteAndPreservesIdentity()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DatabaseIdentity before = ReadCurrentIdentity(environment);
        environment.Factory.Modes.Clear();

        var service = new ProtectedCurrentDatabaseMaintenanceService(
            environment.Session,
            environment.Factory);
        DatabaseIdentity maintained = await service.VacuumAsync();

        Assert.Equal(before, maintained);
        Assert.Equal(
            new[]
            {
                SqliteOpenMode.ReadOnly,
                SqliteOpenMode.ReadOnly,
                SqliteOpenMode.ReadWrite,
            },
            environment.Factory.Modes);

        DatabaseIdentity after = ReadCurrentIdentity(environment);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task VacuumAsync_HoldsMutationLeaseThroughReadWriteOpen()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        using var readWriteEntered = new ManualResetEventSlim();
        using var allowReadWrite = new ManualResetEventSlim();
        environment.Factory.OnOpen = (connection, mode) =>
        {
            if (mode == SqliteOpenMode.ReadWrite &&
                string.Equals(
                    Path.GetFullPath(connection.DataSource),
                    Path.GetFullPath(environment.CurrentPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                readWriteEntered.Set();
                allowReadWrite.Wait(TimeSpan.FromSeconds(10));
            }
        };

        var service = new ProtectedCurrentDatabaseMaintenanceService(
            environment.Session,
            environment.Factory);
        Task<DatabaseIdentity> maintenanceTask = service.VacuumAsync();

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
    public async Task OptimizeAsync_CancellationWhileWaitingForMutationLeaseDoesNotOpenStorage()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Factory.Modes.Clear();

        using ProtectedStorageMutationLease blocking =
            await environment.Session.AcquireMutationLeaseAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var service = new ProtectedCurrentDatabaseMaintenanceService(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.OptimizeAsync(cancellation.Token));
        Assert.Empty(environment.Factory.Modes);
    }

    [Fact]
    public async Task VacuumAsync_InvalidCurrentNeverOpensReadWrite()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute(
            $"UPDATE DatabaseIdentity SET StorageId = '{Guid.NewGuid():D}';");
        environment.Factory.Modes.Clear();

        var service = new ProtectedCurrentDatabaseMaintenanceService(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.VacuumAsync());

        Assert.Equal(new[] { SqliteOpenMode.ReadOnly }, environment.Factory.Modes);
    }

    [Fact]
    public async Task OptimizeAsync_InvalidCatalogNeverOpensReadWrite()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute(
            $"UPDATE DatabaseIdentity SET StorageId = '{Guid.NewGuid():D}';",
            catalog: true);
        environment.Factory.Modes.Clear();

        var service = new ProtectedCurrentDatabaseMaintenanceService(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.OptimizeAsync());

        Assert.Equal(
            new[] { SqliteOpenMode.ReadOnly, SqliteOpenMode.ReadOnly },
            environment.Factory.Modes);
    }

    [Fact]
    public async Task VacuumAsync_OldCurrentSchemaIsRejectedWithoutMigration()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV7();
        environment.Factory.Modes.Clear();

        var service = new ProtectedCurrentDatabaseMaintenanceService(
            environment.Session,
            environment.Factory);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.VacuumAsync());

        Assert.Equal(new[] { SqliteOpenMode.ReadOnly }, environment.Factory.Modes);
        Assert.Equal(7, environment.Scalar("PRAGMA user_version;"));
    }

    private static DatabaseIdentity ReadCurrentIdentity(GlobalPolicyTestEnvironment environment)
    {
        using SqliteConnection connection = environment.Factory.Open(
            environment.CurrentPath,
            environment.Key,
            SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT StorageId, DatabaseId, DatabaseRole, SchemaVersion, EncryptionVersion, CreatedAtUtc
            FROM DatabaseIdentity
            WHERE SingletonId = 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());

        return new DatabaseIdentity(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Enum.Parse<DatabaseRole>(reader.GetString(2), ignoreCase: false),
            reader.GetInt32(3),
            reader.GetInt32(4),
            DateTimeOffset.Parse(
                reader.GetString(5),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind));
    }
}
