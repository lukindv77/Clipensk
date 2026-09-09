using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class SqlitePendingPolicyMaintenanceRepositoryTests
{
    [Fact]
    public async Task ReadAsync_NewStorageReturnsNull()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqlitePendingPolicyMaintenanceRepository(
            environment.Session,
            environment.Factory);

        PendingPolicyMaintenanceOperation? operation = await repository.ReadAsync();

        Assert.Null(operation);
    }

    [Fact]
    public async Task StartUpdateClear_RoundTripsAcrossReopenedSession()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqlitePendingPolicyMaintenanceRepository(
            environment.Session,
            environment.Factory);

        PendingPolicyMaintenanceOperation started = await repository.StartAsync(
            "GlobalCapturePolicyChange",
            "{\"phase\":\"current\"}");

        Assert.NotEqual(Guid.Empty, started.OperationId);
        Assert.Equal("GlobalCapturePolicyChange", started.OperationKind);
        Assert.Equal("{\"phase\":\"current\"}", started.StateJson);
        Assert.Equal(TimeSpan.Zero, started.CreatedAtUtc.Offset);
        Assert.Equal(started.CreatedAtUtc, started.UpdatedAtUtc);

        environment.ReopenSession();
        repository = new SqlitePendingPolicyMaintenanceRepository(
            environment.Session,
            environment.Factory);

        PendingPolicyMaintenanceOperation? reopened = await repository.ReadAsync();
        Assert.Equal(started, reopened);

        PendingPolicyMaintenanceOperation updated = await repository.UpdateStateAsync(
            started.OperationId,
            "{\"phase\":\"archives\"}");

        Assert.Equal(started.OperationId, updated.OperationId);
        Assert.Equal(started.OperationKind, updated.OperationKind);
        Assert.Equal(started.CreatedAtUtc, updated.CreatedAtUtc);
        Assert.True(updated.UpdatedAtUtc >= started.UpdatedAtUtc);
        Assert.Equal("{\"phase\":\"archives\"}", updated.StateJson);
        Assert.Equal(updated, await repository.ReadAsync());

        await repository.ClearAsync(started.OperationId);

        Assert.Null(await repository.ReadAsync());
    }

    [Fact]
    public async Task StartAsync_WhenOperationExistsRejectsAndPreservesExistingOperation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqlitePendingPolicyMaintenanceRepository(
            environment.Session,
            environment.Factory);
        PendingPolicyMaintenanceOperation started = await repository.StartAsync(
            "GlobalCapturePolicyChange",
            "{\"phase\":1}");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repository.StartAsync("OtherOperation", "{\"phase\":2}"));

        Assert.Equal(started, await repository.ReadAsync());
    }

    [Fact]
    public async Task UpdateAndClear_RejectStaleOperationOwnership()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqlitePendingPolicyMaintenanceRepository(
            environment.Session,
            environment.Factory);
        PendingPolicyMaintenanceOperation started = await repository.StartAsync(
            "GlobalCapturePolicyChange",
            "{\"phase\":1}");
        Guid staleOperationId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repository.UpdateStateAsync(staleOperationId, "{\"phase\":2}"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repository.ClearAsync(staleOperationId));

        Assert.Equal(started, await repository.ReadAsync());
    }

    [Fact]
    public async Task ReadAsync_MalformedPersistedOperationFailsClosed()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute("""
            INSERT INTO PendingPolicyMaintenance (
                SingletonId, OperationId, OperationKind, StateJson, CreatedAtUtc, UpdatedAtUtc)
            VALUES (
                1,
                'not-a-guid',
                'GlobalCapturePolicyChange',
                '{"phase":1}',
                '2026-09-08T00:00:00.0000000+00:00',
                '2026-09-08T00:00:00.0000000+00:00');
            """);
        var repository = new SqlitePendingPolicyMaintenanceRepository(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await repository.ReadAsync());
    }

    [Fact]
    public async Task StartAsync_CancellationWhileWaitingForMutationLeaseLeavesMarkerAbsent()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        using ProtectedStorageMutationLease heldLease =
            await environment.Session.AcquireMutationLeaseAsync();
        using var cancellation = new CancellationTokenSource();
        var repository = new SqlitePendingPolicyMaintenanceRepository(
            environment.Session,
            environment.Factory);

        Task<PendingPolicyMaintenanceOperation> startTask = repository
            .StartAsync(
                "GlobalCapturePolicyChange",
                "{\"phase\":1}",
                cancellation.Token)
            .AsTask();
        Assert.False(startTask.IsCompleted);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startTask);
        Assert.Null(await repository.ReadAsync());
    }

    [Fact]
    public async Task InvalidArgumentsFailBeforeOpeningStorage()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Factory.Modes.Clear();
        var repository = new SqlitePendingPolicyMaintenanceRepository(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await repository.StartAsync(" ", "{}"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await repository.UpdateStateAsync(Guid.Empty, "{}"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await repository.ClearAsync(Guid.Empty));

        Assert.Empty(environment.Factory.Modes);
    }
}
