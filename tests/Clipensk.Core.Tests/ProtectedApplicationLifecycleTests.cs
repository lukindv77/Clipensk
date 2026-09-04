using Clipensk.Core.Application;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ProtectedApplicationLifecycleTests
{
    [Fact]
    public void MissingDataRoot_KeepsProtectedDataClosedAndBlocksUnlockAttempt()
    {
        var lifecycle = new ProtectedApplicationLifecycle(isDataRootConfigured: false);

        Assert.False(lifecycle.IsDataRootConfigured);
        Assert.Equal(ApplicationLockState.Locked, lifecycle.LockState);
        Assert.False(lifecycle.CanUseSafeShell);
        Assert.False(lifecycle.CanAccessProtectedData);
        Assert.False(lifecycle.TryBeginUnlock());
        Assert.Equal(ApplicationLockState.Locked, lifecycle.LockState);
    }

    [Fact]
    public void FirstRunCompletion_EnablesSafeShellButRemainsLocked()
    {
        var lifecycle = new ProtectedApplicationLifecycle(isDataRootConfigured: false);

        lifecycle.CompleteFirstRunConfiguration();

        Assert.True(lifecycle.IsDataRootConfigured);
        Assert.True(lifecycle.CanUseSafeShell);
        Assert.Equal(ApplicationLockState.Locked, lifecycle.LockState);
        Assert.False(lifecycle.CanAccessProtectedData);
    }

    [Fact]
    public void ProtectedData_OpensOnlyAfterCompletedUnlock()
    {
        var lifecycle = new ProtectedApplicationLifecycle(isDataRootConfigured: true);

        Assert.True(lifecycle.TryBeginUnlock());
        Assert.Equal(ApplicationLockState.Unlocking, lifecycle.LockState);
        Assert.False(lifecycle.CanAccessProtectedData);

        lifecycle.CompleteUnlock();

        Assert.Equal(ApplicationLockState.Unlocked, lifecycle.LockState);
        Assert.True(lifecycle.CanAccessProtectedData);
    }

    [Fact]
    public void CancelledUnlock_ReturnsToLockedState()
    {
        var lifecycle = new ProtectedApplicationLifecycle(isDataRootConfigured: true);

        Assert.True(lifecycle.TryBeginUnlock());
        lifecycle.CancelUnlock();

        Assert.Equal(ApplicationLockState.Locked, lifecycle.LockState);
        Assert.False(lifecycle.CanAccessProtectedData);
    }
}
