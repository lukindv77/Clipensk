using Clipensk.Core.History;
using Clipensk.Core.Settings;
using Clipensk.Core.Storage;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ArchiveRotationPlannerTests
{
    private static readonly DateOnly Start = new(2026, 1, 1);

    [Fact]
    public void Build_NoDays_ReturnsNoRanges()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings { MaxCalendarDays = 7 };

        IReadOnlyList<JournalDateRange> result = planner.Build(
            Array.Empty<ArchiveRotationDayMetrics>(),
            settings);

        Assert.Empty(result);
    }

    [Fact]
    public void Build_MaxCalendarDays_PartitionsOnlyBetweenWholeDays()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings { MaxCalendarDays = 2 };
        ArchiveRotationDayMetrics[] days =
        [
            Day(0, 1, 10),
            Day(1, 1, 10),
            Day(2, 1, 10),
            Day(3, 1, 10),
            Day(4, 1, 10),
        ];

        IReadOnlyList<JournalDateRange> result = planner.Build(days, settings);

        AssertRanges(
            result,
            new JournalDateRange(Start, Start.AddDays(1)),
            new JournalDateRange(Start.AddDays(2), Start.AddDays(3)),
            new JournalDateRange(Start.AddDays(4), Start.AddDays(4)));
    }

    [Fact]
    public void Build_CountAndBytes_ExactThresholdRemainsInCurrentRange()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings
        {
            MaxRecordCount = 10,
            MaxBytes = 100,
            ThresholdMode = ArchiveRotationThresholdMode.Any,
        };
        ArchiveRotationDayMetrics[] days =
        [
            Day(0, 4, 40),
            Day(1, 6, 60),
            Day(2, 1, 1),
        ];

        IReadOnlyList<JournalDateRange> result = planner.Build(days, settings);

        AssertRanges(
            result,
            new JournalDateRange(Start, Start.AddDays(1)),
            new JournalDateRange(Start.AddDays(2), Start.AddDays(2)));
    }

    [Fact]
    public void Build_AnyMode_AnyConfiguredThresholdCanStartNextRange()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings
        {
            MaxRecordCount = 100,
            MaxBytes = 10,
            MaxCalendarDays = 30,
            ThresholdMode = ArchiveRotationThresholdMode.Any,
        };
        ArchiveRotationDayMetrics[] days =
        [
            Day(0, 1, 6),
            Day(1, 1, 5),
            Day(2, 1, 1),
        ];

        IReadOnlyList<JournalDateRange> result = planner.Build(days, settings);

        AssertRanges(
            result,
            new JournalDateRange(Start, Start),
            new JournalDateRange(Start.AddDays(1), Start.AddDays(2)));
    }

    [Fact]
    public void Build_AllMode_WaitsUntilAllConfiguredThresholdsWouldBeExceeded()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings
        {
            MaxRecordCount = 10,
            MaxBytes = 100,
            ThresholdMode = ArchiveRotationThresholdMode.All,
        };
        ArchiveRotationDayMetrics[] days =
        [
            Day(0, 6, 30),
            Day(1, 5, 30),
            Day(2, 1, 50),
        ];

        IReadOnlyList<JournalDateRange> result = planner.Build(days, settings);

        AssertRanges(
            result,
            new JournalDateRange(Start, Start.AddDays(1)),
            new JournalDateRange(Start.AddDays(2), Start.AddDays(2)));
    }

    [Fact]
    public void Build_OversizedSingleDay_RemainsWhole()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings
        {
            MaxRecordCount = 10,
            MaxBytes = 100,
            ThresholdMode = ArchiveRotationThresholdMode.Any,
        };
        ArchiveRotationDayMetrics[] days =
        [
            Day(0, 11, 110),
            Day(1, 1, 1),
            Day(2, 1, 1),
        ];

        IReadOnlyList<JournalDateRange> result = planner.Build(days, settings);

        AssertRanges(
            result,
            new JournalDateRange(Start, Start),
            new JournalDateRange(Start.AddDays(1), Start.AddDays(2)));
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
            () => planner.Build([Day(0, 1, 1)], settings));
    }

    [Fact]
    public void Build_GapInMetrics_FailsClosed()
    {
        var planner = new ArchiveRotationPlanner();
        var settings = new ArchiveRotationSettings { MaxCalendarDays = 7 };
        ArchiveRotationDayMetrics[] days =
        [
            Day(0, 1, 1),
            Day(2, 1, 1),
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
            Day(1, 1, 1),
            Day(0, 1, 1),
        ];

        Assert.Throws<ArgumentException>(() => planner.Build(days, settings));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void DayMetrics_NegativeValues_AreRejected(long recordCount, long byteCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ArchiveRotationDayMetrics(Start, recordCount, byteCount));
    }

    [Fact]
    public void Build_UnconfiguredSettings_FailsClosed()
    {
        var planner = new ArchiveRotationPlanner();

        Assert.Throws<ArgumentException>(
            () => planner.Build([Day(0, 1, 1)], new ArchiveRotationSettings()));
    }

    private static ArchiveRotationDayMetrics Day(int offset, long records, long bytes) =>
        new(Start.AddDays(offset), records, bytes);

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
