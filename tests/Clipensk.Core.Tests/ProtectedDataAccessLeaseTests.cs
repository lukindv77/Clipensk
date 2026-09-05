using Clipensk.Core.Application;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ProtectedDataAccessLeaseTests
{
    [Fact]
    public void TryAcquire_ReturnsFalseWhileProtectedDataIsLocked()
    {
        var lifecycle = new ProtectedApplicationLifecycle(isDataRootConfigured: true);

        bool acquired = ProtectedDataAccessLease.TryAcquire(lifecycle, out ProtectedDataAccessLease? lease);

        Assert.False(acquired);
        Assert.Null(lease);
    }

    [Fact]
    public void Lease_IsCanceledAsSoonAsLockBegins()
    {
        var lifecycle = CreateUnlockedLifecycle();
        Assert.True(ProtectedDataAccessLease.TryAcquire(lifecycle, out ProtectedDataAccessLease? lease));
        ProtectedDataAccessLease acquiredLease = Assert.IsType<ProtectedDataAccessLease>(lease);
        using (acquiredLease)
        {
            CancellationToken token = acquiredLease.CancellationToken;
            Assert.False(token.IsCancellationRequested);

            Assert.True(lifecycle.TryBeginLock());

            Assert.True(token.IsCancellationRequested);
            Assert.Equal(ApplicationLockState.Locking, lifecycle.LockState);
        }
    }

    [Fact]
    public void NewUnlockEpoch_RequiresAndProvidesFreshLease()
    {
        var lifecycle = CreateUnlockedLifecycle();
        Assert.True(ProtectedDataAccessLease.TryAcquire(lifecycle, out ProtectedDataAccessLease? firstLease));
        ProtectedDataAccessLease acquiredFirstLease = Assert.IsType<ProtectedDataAccessLease>(firstLease);
        using (acquiredFirstLease)
        {
            CancellationToken firstToken = acquiredFirstLease.CancellationToken;

            Assert.True(lifecycle.TryBeginLock());
            lifecycle.CompleteLock();
            Assert.True(firstToken.IsCancellationRequested);

            Assert.True(lifecycle.TryBeginUnlock());
            lifecycle.CompleteUnlock();

            Assert.True(ProtectedDataAccessLease.TryAcquire(lifecycle, out ProtectedDataAccessLease? secondLease));
            ProtectedDataAccessLease acquiredSecondLease = Assert.IsType<ProtectedDataAccessLease>(secondLease);
            using (acquiredSecondLease)
            {
                Assert.False(acquiredSecondLease.CancellationToken.IsCancellationRequested);
                Assert.True(firstToken.IsCancellationRequested);
            }
        }
    }

    [Fact]
    public void DisposedLease_CannotExposeCancellationToken()
    {
        var lifecycle = CreateUnlockedLifecycle();
        Assert.True(ProtectedDataAccessLease.TryAcquire(lifecycle, out ProtectedDataAccessLease? lease));
        ProtectedDataAccessLease acquiredLease = Assert.IsType<ProtectedDataAccessLease>(lease);
        acquiredLease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = acquiredLease.CancellationToken);
    }

    private static ProtectedApplicationLifecycle CreateUnlockedLifecycle()
    {
        var lifecycle = new ProtectedApplicationLifecycle(isDataRootConfigured: true);
        Assert.True(lifecycle.TryBeginUnlock());
        lifecycle.CompleteUnlock();
        return lifecycle;
    }
}
