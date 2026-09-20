using Clipensk.Core.Security;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class IdleAutoLockTriggerTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromMinutes(5);

    [Fact]
    public void ShouldLock_DoesNotFireBelowTheThreshold()
    {
        var trigger = new IdleAutoLockTrigger(Threshold);

        Assert.False(trigger.ShouldLock(TimeSpan.FromMinutes(4)));
        Assert.False(trigger.ShouldLock(TimeSpan.Zero));
    }

    [Fact]
    public void ShouldLock_FiresExactlyOnceWhenIdleReachesTheThreshold()
    {
        var trigger = new IdleAutoLockTrigger(Threshold);

        Assert.True(trigger.ShouldLock(Threshold));
        // Still idle past the threshold on the next poll: must not fire again.
        Assert.False(trigger.ShouldLock(Threshold + TimeSpan.FromMinutes(1)));
        Assert.False(trigger.ShouldLock(Threshold + TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void ShouldLock_ReArmsAfterActivityResumesBelowTheThreshold()
    {
        var trigger = new IdleAutoLockTrigger(Threshold);
        Assert.True(trigger.ShouldLock(Threshold));

        // Activity resumed: idle duration drops back below the threshold.
        Assert.False(trigger.ShouldLock(TimeSpan.Zero));

        // A fresh idle period crossing the threshold must fire again.
        Assert.True(trigger.ShouldLock(Threshold));
    }

    [Fact]
    public void Reset_ReArmsWithoutRequiringActivityToBeObserved()
    {
        var trigger = new IdleAutoLockTrigger(Threshold);
        Assert.True(trigger.ShouldLock(Threshold));

        trigger.Reset();

        Assert.True(trigger.ShouldLock(Threshold));
    }

    [Fact]
    public void Constructor_RejectsANonPositiveThreshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IdleAutoLockTrigger(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IdleAutoLockTrigger(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void ShouldLock_RejectsANegativeIdleDuration()
    {
        var trigger = new IdleAutoLockTrigger(Threshold);

        Assert.Throws<ArgumentOutOfRangeException>(() => trigger.ShouldLock(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void ShouldLock_FiresExactlyAtTheThresholdBoundary()
    {
        // The threshold is reached, not exceeded, per docs/REQUIREMENTS.md's ">=" convention used
        // elsewhere for threshold semantics (see ArchiveRotationSettings.HasReachedThresholds).
        var trigger = new IdleAutoLockTrigger(Threshold);

        Assert.False(trigger.ShouldLock(Threshold - TimeSpan.FromMilliseconds(1)));
        Assert.True(trigger.ShouldLock(Threshold));
    }
}
