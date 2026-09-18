using Clipensk.Core.History;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ArchiveRotationPlannerTests
{
    private static readonly DateOnly Start = new(2026, 1, 1);

    [Fact]
    public void Build_NoDays_ReturnsNoReadyRangesAndNoOpenRange()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings { MaxCalendarDays = 7 };

        ArchiveRotationPlan result = planner.Build(
            Array.Empty<ArchiveRotationDayMetrics>(),
            settings);

        Assert.Empty(result.ReadyRanges);
        Assert.Null(result.OpenRange);
    }

    [Fact]
    public void Build_MaxCalendarDays_LeavesIncompleteTailOpen()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings { MaxCalendarDays = 2 };
        ArchiveRotationDayMetrics[] days =
        [
            Day(0, 1),
            Day(1, 1),
            Day(2, 1),
            Day(3, 1),
            Day(4, 1),
        ];

        ArchiveRotationPlan result = planner.Build(days, settings);

        AssertRanges(
            result.ReadyRanges,
            new JournalDateRange(Start, Start.AddDays(1)),
            new JournalDateRange(Start.AddDays(2), Start.AddDays(3)));
        Assert.Equal(
            new JournalDateRange(Start.AddDays(4), Start.AddDays(4)),
            result.OpenRange);
    }

    [Fact]
    public void Build_RecordCount_ExactThresholdBecomesReady()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings
        {
            MaxRecordCount = 10,
        };
        ArchiveRotationDayMetrics[] days =
        [
            Day(0, 4),
            Day(1, 6),
            Day(2, 1),
        ];

        ArchiveRotationPlan result = planner.Build(days, settings);

        AssertRanges(
            result.ReadyRanges,
            new JournalDateRange(Start, Start.AddDays(1)));
        Assert.Equal(
            new JournalDateRange(Start.AddDays(2), Start.AddDays(2)),
            result.OpenRange);
    }

    [Fact]
    public void Build_AnyMode_ReadyWhenAnyConfiguredThresholdIsReached()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings
        {
            MaxRecordCount = 100,
            MaxCalendarDays = 2,
            ThresholdMode = ArchiveRotationThresholdMode.Any,
        };
        ArchiveRotationDayMetrics[] days =
        [
            Day(0, 1),
            Day(1, 1),
            Day(2, 1),
        ];

        ArchiveRotationPlan result = planner.Build(days, settings);

        AssertRanges(
            result.ReadyRanges,
            new JournalDateRange(Start, Start.AddDays(1)));
        Assert.Equal(
            new JournalDateRange(Start.AddDays(2), Start.AddDays(2)),
            result.OpenRange);
    }

    [Fact]
    public void Build_AllMode_WaitsUntilEveryConfiguredThresholdIsReached()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings
        {
            MaxRecordCount = 10,
            MaxCalendarDays = 2,
            ThresholdMode = ArchiveRotationThresholdMode.All,
        };
        ArchiveRotationDayMetrics[] days =
        [
            Day(0, 4),
            Day(1, 4),
            Day(2, 3),
            Day(3, 1),
        ];

        ArchiveRotationPlan result = planner.Build(days, settings);

        AssertRanges(
            result.ReadyRanges,
            new JournalDateRange(Start, Start.AddDays(2)));
        Assert.Equal(
            new JournalDateRange(Start.AddDays(3), Start.AddDays(3)),
            result.OpenRange);
    }

    [Fact]
    public void Build_AllMode_ExactThresholdsBecomeReady()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings
        {
            MaxRecordCount = 10,
            MaxCalendarDays = 2,
            ThresholdMode = ArchiveRotationThresholdMode.All,
        };
        ArchiveRotationDayMetrics[] days =
        [
            Day(0, 4),
            Day(1, 6),
        ];

        ArchiveRotationPlan result = planner.Build(days, settings);

        AssertRanges(
            result.ReadyRanges,
            new JournalDateRange(Start, Start.AddDays(1)));
        Assert.Null(result.OpenRange);
    }

    [Fact]
    public void Build_OversizedSingleDay_BecomesReadyWhole()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings
        {
            MaxRecordCount = 10,
        };
        ArchiveRotationDayMetrics[] days =
        [
            Day(0, 11),
            Day(1, 1),
            Day(2, 1),
        ];

        ArchiveRotationPlan result = planner.Build(days, settings);

        AssertRanges(
            result.ReadyRanges,
            new JournalDateRange(Start, Start));
        Assert.Equal(
            new JournalDateRange(Start.AddDays(1), Start.AddDays(2)),
            result.OpenRange);
    }

    [Fact]
    public void Build_ZeroRecordDays_CountTowardCalendarSpan()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings
        {
            MaxCalendarDays = 3,
        };
        ArchiveRotationDayMetrics[] days =
        [
            Day(0, 1),
            Day(1, 0),
            Day(2, 0),
            Day(3, 1),
        ];

        ArchiveRotationPlan result = planner.Build(days, settings);

        AssertRanges(
            result.ReadyRanges,
            new JournalDateRange(Start, Start.AddDays(2)));
        Assert.Equal(
            new JournalDateRange(Start.AddDays(3), Start.AddDays(3)),
            result.OpenRange);
    }

    [Fact]
    public void Build_NoThresholdReached_LeavesEntireInputOpen()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings
        {
            MaxRecordCount = 100,
        };
        ArchiveRotationDayMetrics[] days =
        [
            Day(0, 4),
            Day(1, 6),
        ];

        ArchiveRotationPlan result = planner.Build(days, settings);

        Assert.Empty(result.ReadyRanges);
        Assert.Equal(
            new JournalDateRange(Start, Start.AddDays(1)),
            result.OpenRange);
    }

    [Fact]
    public void Build_AllCompletedRanges_HasNoOpenTail()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings
        {
            MaxCalendarDays = 2,
        };
        ArchiveRotationDayMetrics[] days =
        [
            Day(0, 1),
            Day(1, 1),
            Day(2, 1),
            Day(3, 1),
        ];

        ArchiveRotationPlan result = planner.Build(days, settings);

        AssertRanges(
            result.ReadyRanges,
            new JournalDateRange(Start, Start.AddDays(1)),
            new JournalDateRange(Start.AddDays(2), Start.AddDays(3)));
        Assert.Null(result.OpenRange);
    }

    [Fact]
    public void Build_RecordCountOverflow_SaturatesAndReachesThreshold()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings
        {
            MaxRecordCount = long.MaxValue,
        };
        ArchiveRotationDayMetrics[] days =
        [
            Day(0, long.MaxValue - 2),
            Day(1, 10),
        ];

        ArchiveRotationPlan result = planner.Build(days, settings);

        AssertRanges(
            result.ReadyRanges,
            new JournalDateRange(Start, Start.AddDays(1)));
        Assert.Null(result.OpenRange);
    }

    [Fact]
    public void Build_PhysicalSizeThreshold_FailsClosed()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings
        {
            MaxBytes = 1024,
        };

        Assert.Throws<NotSupportedException>(
            () => planner.Build([Day(0, 1)], settings));
    }

    [Fact]
    public void Build_MultipleThresholdsWithoutMode_FailsClosed()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings
        {
            MaxRecordCount = 10,
            MaxCalendarDays = 7,
        };

        Assert.Throws<ArgumentException>(
            () => planner.Build([Day(0, 1)], settings));
    }

    [Fact]
    public void Build_GapInMetrics_FailsClosed()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings { MaxCalendarDays = 7 };
        ArchiveRotationDayMetrics[] days =
        [
            Day(0, 1),
            Day(2, 1),
        ];

        Assert.Throws<ArgumentException>(() => planner.Build(days, settings));
    }

    [Fact]
    public void Build_DuplicateOrOutOfOrderMetrics_FailsClosed()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings { MaxCalendarDays = 7 };
        ArchiveRotationDayMetrics[] days =
        [
            Day(1, 1),
            Day(0, 1),
        ];

        Assert.Throws<ArgumentException>(() => planner.Build(days, settings));
    }

    [Fact]
    public void DayMetrics_NegativeRecordCount_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ArchiveRotationDayMetrics(Start, -1));
    }

    [Fact]
    public void Build_UnconfiguredSettings_FailsClosed()
    {
        var planner = new ArchiveRotationPlanner();

        Assert.Throws<ArgumentException>(
            () => planner.Build([Day(0, 1)], new ArchiveRotationSettings()));
    }

    private static ArchiveRotationDayMetrics Day(int offset, long records) =>
        new(Start.AddDays(offset), records);

    private static void AssertRanges(
        IReadOnlyList<JournalDateRange> actual,
        params JournalDateRange[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index], actual[index]);
        }
    }
}
