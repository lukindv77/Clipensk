using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedPolicyMaintenanceCoordinatorTests
{
    private const string InitialState = "{\"phase\":\"prepared\"}";
    private const string UpdatedState = "{\"phase\":\"current-cleaned\"}";

    [Fact]
    public async Task BeginGlobalCapturePolicyChangeAsync_PersistsMarkerAndOwnsMutationLease()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var coordinator = new ProtectedPolicyMaintenanceCoordinator(environment.Session, environment.Factory);
        ProtectedPolicyMaintenanceSession maintenance =
            await coordinator.BeginGlobalCapturePolicyChangeAsync(InitialState);

        try
        {
            Assert.NotEqual(Guid.Empty, maintenance.Snapshot.OperationId);
            Assert.Equal(
                ProtectedPolicyMaintenanceCoordinator.GlobalCapturePolicyChangeOperationKind,
                maintenance.Snapshot.OperationKind);
            Assert.Equal(InitialState, maintenance.Snapshot.StateJson);
            Assert.Equal(TimeSpan.Zero, maintenance.Snapshot.CreatedAtUtc.Offset);
            Assert.Equal(maintenance.Snapshot.CreatedAtUtc, maintenance.Snapshot.UpdatedAtUtc);
            Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));

            Task<ProtectedStorageMutationLease> competingLease =
                environment.Session.AcquireMutationLeaseAsync().AsTask();
            Assert.False(competingLease.IsCompleted);

            maintenance.Dispose();

            using ProtectedStorageMutationLease acquired =
                await competingLease.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
        }
        finally
        {
            maintenance.Dispose();
        }
    }

    [Fact]
    public async Task Resume_Update_AndComplete_PreserveExactOwnerUntilDurableCompletion()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var coordinator = new ProtectedPolicyMaintenanceCoordinator(environment.Session, environment.Factory);
        ProtectedPolicyMaintenanceSession first =
            await coordinator.BeginGlobalCapturePolicyChangeAsync(InitialState);
        PendingPolicyMaintenanceSnapshot original = first.Snapshot;
        first.Dispose();

        ProtectedPolicyMaintenanceSession? resumed =
            await coordinator.ResumeGlobalCapturePolicyChangeAsync();
        Assert.NotNull(resumed);
        try
        {
            Assert.Equal(original.OperationId, resumed.Snapshot.OperationId);
            Assert.Equal(original.OperationKind, resumed.Snapshot.OperationKind);
            Assert.Equal(original.CreatedAtUtc, resumed.Snapshot.CreatedAtUtc);
            Assert.Equal(InitialState, resumed.Snapshot.StateJson);

            await resumed.UpdateStateAsync(UpdatedState);

            Assert.Equal(original.OperationId, resumed.Snapshot.OperationId);
            Assert.Equal(original.CreatedAtUtc, resumed.Snapshot.CreatedAtUtc);
            Assert.Equal(UpdatedState, resumed.Snapshot.StateJson);
            Assert.True(resumed.Snapshot.UpdatedAtUtc >= original.UpdatedAtUtc);
            Assert.Equal(
                UpdatedState,
                ScalarText(environment, "SELECT StateJson FROM PendingPolicyMaintenance WHERE SingletonId = 1;"));

            Task<ProtectedStorageMutationLease> competingLease =
                environment.Session.AcquireMutationLeaseAsync().AsTask();
            Assert.False(competingLease.IsCompleted);

            await resumed.CompleteAsync();

            Assert.True(resumed.IsCompleted);
            Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
            using ProtectedStorageMutationLease acquired =
                await competingLease.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            resumed.Dispose();
        }

        Assert.Null(await coordinator.ResumeGlobalCapturePolicyChangeAsync());
    }

    [Fact]
    public async Task BeginGlobalCapturePolicyChangeAsync_RejectsExistingDurableMarker()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var coordinator = new ProtectedPolicyMaintenanceCoordinator(environment.Session, environment.Factory);
        ProtectedPolicyMaintenanceSession first =
            await coordinator.BeginGlobalCapturePolicyChangeAsync(InitialState);
        Guid operationId = first.Snapshot.OperationId;
        first.Dispose();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await coordinator.BeginGlobalCapturePolicyChangeAsync(UpdatedState));

        Assert.Equal(
            operationId.ToString("D"),
            ScalarText(environment, "SELECT OperationId FROM PendingPolicyMaintenance WHERE SingletonId = 1;"));
    }

    [Fact]
    public async Task BeginGlobalCapturePolicyChangeAsync_RejectsInvalidStateBeforeOpeningStorage()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var coordinator = new ProtectedPolicyMaintenanceCoordinator(environment.Session, environment.Factory);
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await coordinator.BeginGlobalCapturePolicyChangeAsync("{"));

        Assert.Empty(environment.Factory.Modes);
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task ResumeGlobalCapturePolicyChangeAsync_RejectsUnknownOperationKindAndReleasesLease()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute("""
            INSERT INTO PendingPolicyMaintenance (
                SingletonId, OperationId, OperationKind, StateJson, CreatedAtUtc, UpdatedAtUtc)
            VALUES (
                1,
                '11111111-1111-1111-1111-111111111111',
                'UnknownMaintenance',
                '{}',
                '2026-09-08T00:00:00.0000000+00:00',
                '2026-09-08T00:00:00.0000000+00:00');
            """);
        var coordinator = new ProtectedPolicyMaintenanceCoordinator(environment.Session, environment.Factory);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await coordinator.ResumeGlobalCapturePolicyChangeAsync());

        using ProtectedStorageMutationLease lease =
            await environment.Session.AcquireMutationLeaseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
    }

    [Fact]
    public async Task BeginGlobalCapturePolicyChangeAsync_CancellationDuringInsertRollsBackAndReleasesLease()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        environment.Execute("""
            CREATE TRIGGER cancel_pending_policy AFTER INSERT ON PendingPolicyMaintenance
            BEGIN SELECT cancel_pending_policy(); END;
            """);
        environment.Factory.OnOpen = (connection, _) =>
            connection.CreateFunction("cancel_pending_policy", () =>
            {
                cancellation.Cancel();
                return 0;
            });
        var coordinator = new ProtectedPolicyMaintenanceCoordinator(environment.Session, environment.Factory);

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await coordinator.BeginGlobalCapturePolicyChangeAsync(InitialState, cancellation.Token));
        }
        finally
        {
            environment.Factory.OnOpen = null;
        }

        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM PendingPolicyMaintenance;"));
        using ProtectedStorageMutationLease lease =
            await environment.Session.AcquireMutationLeaseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static string ScalarText(GlobalPolicyTestEnvironment environment, string sql)
    {
        using SqliteConnection connection = environment.Factory.Open(
            environment.CurrentPath,
            environment.Key,
            SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Assert.IsType<string>(command.ExecuteScalar());
    }
}
