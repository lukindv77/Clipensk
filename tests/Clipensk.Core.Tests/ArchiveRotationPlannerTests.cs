using Clipensk.Core.History;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ArchiveRotationPlannerTests
{
    private static readonly DateOnly Start = new(2026, 1, 1);
    private static readonly DateOnly CurrentLocalDate = new(2026, 2, 1);

    [Fact]
    public void Build_NoDays_ReturnsNoReadySegmentsAndNoTail()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings { MaxCalendarDays = 7 };

        ArchiveRotationPlan result = planner.Build(
            Array.Empty<ArchiveRotationDayMetrics>(),
            settings,
            CurrentLocalDate);

        Assert.Empty(result.ReadySegments);
        Assert.Null(result.Tail);
        Assert.False(result.HasReadySegments);
    }

    [Fact]
    public void Build_MaxCalendarDays_LeavesNewestUnderThresholdTail()
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

        ArchiveRotationPlan result = planner.Build(days, settings, CurrentLocalDate);

        AssertRanges(
            result.ReadySegments,
            new JournalDateRange(Start, Start.AddDays(1)),
            new JournalDateRange(Start.AddDays(2), Start.AddDays(3)));
        Assert.Equal(
            new JournalDateRange(Start.AddDays(4), Start.AddDays(4)),
            result.Tail);
    }

    [Fact]
    public void Build_SparseCalendarGap_DoesNotCreateEmptyArchive()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings { MaxCalendarDays = 3 };
        ArchiveRotationDayMetrics[] days =
        [
            Day(0, 1),
            Day(5, 1),
        ];

        ArchiveRotationPlan result = planner.Build(days, settings, CurrentLocalDate);

        AssertRanges(
            result.ReadySegments,
            new JournalDateRange(Start, Start));
        Assert.Equal(
            new JournalDateRange(Start.AddDays(5), Start.AddDays(5)),
            result.Tail);
    }

    [Fact]
    public void Build_RecordCount_ExactThresholdRemainsInCurrentSegment()
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

        ArchiveRotationPlan result = planner.Build(days, settings, CurrentLocalDate);

        AssertRanges(
            result.ReadySegments,
            new JournalDateRange(Start, Start.AddDays(1)));
        Assert.Equal(
            new JournalDateRange(Start.AddDays(2), Start.AddDays(2)),
            result.Tail);
    }

    [Fact]
    public void Build_AnyMode_AnyConfiguredThresholdCanCloseSegment()
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

        ArchiveRotationPlan result = planner.Build(days, settings, CurrentLocalDate);

        AssertRanges(
            result.ReadySegments,
            new JournalDateRange(Start, Start.AddDays(1)));
        Assert.Equal(
            new JournalDateRange(Start.AddDays(2), Start.AddDays(2)),
            result.Tail);
    }

    [Fact]
    public void Build_AllMode_WaitsUntilAllConfiguredThresholdsWouldBeExceeded()
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
            Day(0, 6),
            Day(1, 5),
            Day(2, 1),
            Day(3, 1),
        ];

        ArchiveRotationPlan result = planner.Build(days, settings, CurrentLocalDate);

        AssertRanges(
            result.ReadySegments,
            new JournalDateRange(Start, Start.AddDays(1)));
        Assert.Equal(
            new JournalDateRange(Start.AddDays(2), Start.AddDays(3)),
            result.Tail);
    }

    [Fact]
    public void Build_OversizedSingleDay_IsReadyWithoutSplittingDay()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings
        {
            MaxRecordCount = 10,
        };

        ArchiveRotationPlan result = planner.Build(
            [Day(0, 11)],
            settings,
            CurrentLocalDate);

        AssertRanges(
            result.ReadySegments,
            new JournalDateRange(Start, Start));
        Assert.Null(result.Tail);
    }

    [Fact]
    public void Build_UnderThresholdHistory_RemainsTail()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings
        {
            MaxRecordCount = 10,
        };

        ArchiveRotationPlan result = planner.Build(
            [Day(0, 2), Day(1, 3)],
            settings,
            CurrentLocalDate);

        Assert.Empty(result.ReadySegments);
        Assert.Equal(
            new JournalDateRange(Start, Start.AddDays(1)),
            result.Tail);
    }

    [Fact]
    public void Build_RecordCountOverflowBoundary_FailsSafeWithoutArithmeticOverflow()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings
        {
            MaxRecordCount = long.MaxValue,
        };
        ArchiveRotationDayMetrics[] days =
        [
            Day(0, long.MaxValue),
            Day(1, 1),
        ];

        ArchiveRotationPlan result = planner.Build(days, settings, CurrentLocalDate);

        AssertRanges(
            result.ReadySegments,
            new JournalDateRange(Start, Start));
        Assert.Equal(
            new JournalDateRange(Start.AddDays(1), Start.AddDays(1)),
            result.Tail);
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
            () => planner.Build([Day(0, 1)], settings, CurrentLocalDate));
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
            () => planner.Build([Day(0, 1)], settings, CurrentLocalDate));
    }

    [Fact]
    public void Build_DuplicateOrOutOfOrderMetrics_FailClosed()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings { MaxCalendarDays = 7 };

        Assert.Throws<ArgumentException>(
            () => planner.Build(
                [Day(1, 1), Day(0, 1)],
                settings,
                CurrentLocalDate));

        Assert.Throws<ArgumentException>(
            () => planner.Build(
                [Day(0, 1), Day(0, 2)],
                settings,
                CurrentLocalDate));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DayMetrics_NonPositiveRecordCount_IsRejected(long recordCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ArchiveRotationDayMetrics(Start, recordCount));
    }

    [Fact]
    public void Build_CurrentOrFutureDay_FailsClosed()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings { MaxCalendarDays = 7 };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => planner.Build(
                [new ArchiveRotationDayMetrics(CurrentLocalDate, 1)],
                settings,
                CurrentLocalDate));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => planner.Build(
                [new ArchiveRotationDayMetrics(CurrentLocalDate.AddDays(1), 1)],
                settings,
                CurrentLocalDate));
    }

    [Fact]
    public void Build_UnconfiguredSettings_FailsClosed()
    {
        var planner = new ArchiveRotationPlanner();

        Assert.Throws<ArgumentException>(
            () => planner.Build(
                [Day(0, 1)],
                new ArchiveRotationSettings(),
                CurrentLocalDate));
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
