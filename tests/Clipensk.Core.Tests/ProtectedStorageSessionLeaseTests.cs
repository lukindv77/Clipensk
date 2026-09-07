using Clipensk.Core.Application;
using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ProtectedStorageSessionLeaseTests
{
    [Fact]
    public void Create_ExposesStorageContextWhileProtectedAccessIsActive()
    {
        var lifecycle = CreateUnlockedLifecycle();
        byte[] key = [1, 2, 3, 4];
        Guid storageId = Guid.NewGuid();

        using var session = ProtectedStorageSessionLease.Create(
            lifecycle,
            @"C:\ClipenskData",
            storageId,
            new MasterKeyLease(key));

        Assert.Equal(@"C:\ClipenskData", session.DataRootPath);
        Assert.Equal(storageId, session.StorageId);
        Assert.True(session.IsActive);
        Assert.False(session.CancellationToken.IsCancellationRequested);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, session.DangerousGetMasterKeyMemory().ToArray());
    }

    [Fact]
    public void BeginLock_CancelsSessionAndZeroesOwnedMasterKey()
    {
        var lifecycle = CreateUnlockedLifecycle();
        byte[] key = [1, 2, 3, 4];
        using var session = ProtectedStorageSessionLease.Create(
            lifecycle,
            @"C:\ClipenskData",
            Guid.NewGuid(),
            new MasterKeyLease(key));
        CancellationToken token = session.CancellationToken;

        Assert.True(lifecycle.TryBeginLock());

        Assert.False(session.IsActive);
        Assert.True(token.IsCancellationRequested);
        Assert.All(key, value => Assert.Equal((byte)0, value));
        Assert.Throws<InvalidOperationException>(() => session.DangerousGetMasterKeyMemory());
    }

    [Fact]
    public void BeginLock_ZeroesOwnedMasterKeyWhenCancellationCallbackThrows()
    {
        var lifecycle = CreateUnlockedLifecycle();
        byte[] key = [1, 2, 3, 4];
        using var session = ProtectedStorageSessionLease.Create(
            lifecycle,
            @"C:\ClipenskData",
            Guid.NewGuid(),
            new MasterKeyLease(key));
        using CancellationTokenRegistration registration = session.CancellationToken.Register(
            static () => throw new InvalidOperationException("callback failure"));

        bool started = lifecycle.TryBeginLock();

        Assert.True(started);
        Assert.Equal(ApplicationLockState.Locking, lifecycle.LockState);
        Assert.False(session.IsActive);
        Assert.True(session.CancellationToken.IsCancellationRequested);
        Assert.All(key, value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public async Task MutationLease_SerializesHolders()
    {
        var lifecycle = CreateUnlockedLifecycle();
        using var session = ProtectedStorageSessionLease.Create(
            lifecycle,
            @"C:\ClipenskData",
            Guid.NewGuid(),
            new MasterKeyLease([1, 2, 3, 4]));

        ProtectedStorageMutationLease first = await session.AcquireMutationLeaseAsync();
        Task<ProtectedStorageMutationLease> secondTask =
            session.AcquireMutationLeaseAsync().AsTask();

        Assert.False(secondTask.IsCompleted);

        first.Dispose();
        using ProtectedStorageMutationLease second =
            await secondTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task MutationLease_CallerCancellationCancelsWaiter()
    {
        var lifecycle = CreateUnlockedLifecycle();
        using var session = ProtectedStorageSessionLease.Create(
            lifecycle,
            @"C:\ClipenskData",
            Guid.NewGuid(),
            new MasterKeyLease([1, 2, 3, 4]));
        using ProtectedStorageMutationLease first = await session.AcquireMutationLeaseAsync();
        using var cancellation = new CancellationTokenSource();

        Task<ProtectedStorageMutationLease> waiter =
            session.AcquireMutationLeaseAsync(cancellation.Token).AsTask();
        Assert.False(waiter.IsCompleted);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
    }

    [Fact]
    public async Task MutationLease_BeginLockCancelsWaiterAndHeldLeaseCanStillRelease()
    {
        var lifecycle = CreateUnlockedLifecycle();
        byte[] key = [1, 2, 3, 4];
        using var session = ProtectedStorageSessionLease.Create(
            lifecycle,
            @"C:\ClipenskData",
            Guid.NewGuid(),
            new MasterKeyLease(key));
        ProtectedStorageMutationLease first = await session.AcquireMutationLeaseAsync();
        Task<ProtectedStorageMutationLease> waiter =
            session.AcquireMutationLeaseAsync().AsTask();

        Assert.False(waiter.IsCompleted);
        Assert.True(lifecycle.TryBeginLock());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        first.Dispose();
        first.Dispose();
        Assert.False(session.IsActive);
        Assert.All(key, value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void Dispose_ZeroesOwnedMasterKeyAndKeepsCancelledSessionTokenObservable()
    {
        var lifecycle = CreateUnlockedLifecycle();
        byte[] key = [1, 2, 3, 4];
        var session = ProtectedStorageSessionLease.Create(
            lifecycle,
            @"C:\ClipenskData",
            Guid.NewGuid(),
            new MasterKeyLease(key));
        CancellationToken token = session.CancellationToken;

        session.Dispose();

        Assert.False(session.IsActive);
        Assert.True(token.IsCancellationRequested);
        Assert.True(session.CancellationToken.IsCancellationRequested);
        Assert.All(key, value => Assert.Equal((byte)0, value));
        Assert.Throws<ObjectDisposedException>(() => session.DangerousGetMasterKeyMemory());
    }

    [Fact]
    public void Dispose_ZeroesOwnedMasterKeyEvenWhenCancellationCallbackThrows()
    {
        var lifecycle = CreateUnlockedLifecycle();
        byte[] key = [1, 2, 3, 4];
        var session = ProtectedStorageSessionLease.Create(
            lifecycle,
            @"C:\ClipenskData",
            Guid.NewGuid(),
            new MasterKeyLease(key));
        using CancellationTokenRegistration registration = session.CancellationToken.Register(
            static () => throw new InvalidOperationException("callback failure"));

        Assert.Throws<AggregateException>(() => session.Dispose());

        Assert.False(session.IsActive);
        Assert.True(session.CancellationToken.IsCancellationRequested);
        Assert.All(key, value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void Create_WhileLockedFailsClosedAndZeroesCandidateMasterKey()
    {
        var lifecycle = new ProtectedApplicationLifecycle(isDataRootConfigured: true);
        byte[] key = [1, 2, 3, 4];

        Assert.Throws<InvalidOperationException>(() => ProtectedStorageSessionLease.Create(
            lifecycle,
            @"C:\ClipenskData",
            Guid.NewGuid(),
            new MasterKeyLease(key)));

        Assert.All(key, value => Assert.Equal((byte)0, value));
    }

    private static ProtectedApplicationLifecycle CreateUnlockedLifecycle()
    {
        var lifecycle = new ProtectedApplicationLifecycle(isDataRootConfigured: true);
        Assert.True(lifecycle.TryBeginUnlock());
        lifecycle.CompleteUnlock();
        return lifecycle;
    }
}
