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

        Assert.True(token.IsCancellationRequested);
        Assert.All(key, value => Assert.Equal((byte)0, value));
        Assert.Throws<InvalidOperationException>(() => session.DangerousGetMasterKeyMemory());
    }

    [Fact]
    public void Dispose_ZeroesOwnedMasterKey()
    {
        var lifecycle = CreateUnlockedLifecycle();
        byte[] key = [1, 2, 3, 4];
        var session = ProtectedStorageSessionLease.Create(
            lifecycle,
            @"C:\ClipenskData",
            Guid.NewGuid(),
            new MasterKeyLease(key));

        session.Dispose();

        Assert.All(key, value => Assert.Equal((byte)0, value));
        Assert.Throws<ObjectDisposedException>(() => session.DangerousGetMasterKeyMemory());
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
