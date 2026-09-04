using Clipensk.Core.Application;
using Clipensk.Core.History;
using Clipensk.Core.Storage;

namespace Clipensk.Core.Tests;

public sealed class StorageRulesTests
{
    [Fact]
    public void QueryPlanner_SelectsOnlyIntersectingArchives()
    {
        var planner = new StorageQueryPlanner();
        var requested = new JournalDateRange(new DateOnly(2026, 8, 15), new DateOnly(2026, 9, 4));
        var current = new CurrentStoreDescriptor(
            new JournalDateRange(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 4)));
        ArchiveSegmentDescriptor[] archives =
        [
            Segment("archive_000001.db", 2026, 1, 1, 2026, 6, 30),
            Segment("archive_000002.db", 2026, 7, 1, 2026, 8, 31),
        ];

        StorageQueryPlan plan = planner.Build(requested, current, archives);

        Assert.True(plan.QueryCurrent);
        Assert.Single(plan.ArchiveSegments);
        Assert.Equal("archive_000002.db", plan.ArchiveSegments[0].FileName);
    }

    [Fact]
    public void QueryPlanner_RejectsOverlappingArchiveDays()
    {
        var planner = new StorageQueryPlanner();
        var requested = new JournalDateRange(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        var current = new CurrentStoreDescriptor(null);
        ArchiveSegmentDescriptor[] archives =
        [
            Segment("archive_000001.db", 2026, 1, 1, 2026, 3, 31),
            Segment("archive_000002.db", 2026, 3, 31, 2026, 6, 30),
        ];

        Assert.Throws<InvalidOperationException>(() => planner.Build(requested, current, archives));
    }

    [Theory]
    [InlineData("archive_000025.db", 25, 0)]
    [InlineData("archive_000025_0001.db", 25, 1)]
    [InlineData("archive_000025_0012.db", 25, 12)]
    public void ArchiveFileName_ParsesAcceptedFamily(string fileName, int baseNumber, int split)
    {
        Assert.True(ArchiveFileName.TryParse(fileName, out ArchiveFileName result));
        Assert.Equal(baseNumber, result.BaseNumber);
        Assert.Equal(split, result.SplitSequence);
        Assert.Equal(fileName, result.FileName);
    }

    [Fact]
    public void EventTimeContext_PreservesCalendarDateAndZoneIdentity()
    {
        var timestamp = new DateTimeOffset(2026, 9, 4, 23, 45, 0, TimeSpan.FromHours(7));
        var context = new EventTimeContext(timestamp, "N. Central Asia Standard Time");

        Assert.Equal(new DateOnly(2026, 9, 4), context.CalendarDate);
        Assert.Equal(TimeSpan.FromHours(7), context.Offset);
        Assert.Equal("N. Central Asia Standard Time", context.WindowsTimeZoneId);
        Assert.Equal(timestamp.UtcDateTime, context.UtcTimestamp);
    }

    [Fact]
    public void LockStateMachine_FollowsExpectedLifecycle()
    {
        var stateMachine = new ApplicationLockStateMachine();

        Assert.Equal(ApplicationLockState.Locked, stateMachine.Current);
        Assert.True(stateMachine.TryBeginUnlock());
        stateMachine.CompleteUnlock();
        Assert.Equal(ApplicationLockState.Unlocked, stateMachine.Current);
        Assert.True(stateMachine.TryBeginLock());
        stateMachine.CompleteLock();
        Assert.Equal(ApplicationLockState.Locked, stateMachine.Current);
    }

    private static ArchiveSegmentDescriptor Segment(
        string fileName,
        int sy, int sm, int sd,
        int ey, int em, int ed)
    {
        return new ArchiveSegmentDescriptor(
            Guid.NewGuid(),
            fileName,
            new JournalDateRange(new DateOnly(sy, sm, sd), new DateOnly(ey, em, ed)),
            IsSealed: true);
    }
}
