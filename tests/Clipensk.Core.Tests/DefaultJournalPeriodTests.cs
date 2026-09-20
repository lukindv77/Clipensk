using Clipensk.Core.Settings;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class DefaultJournalPeriodTests
{
    private static readonly DateOnly Today = new(2026, 3, 15);

    [Fact]
    public void ForDays_OneDaySpansOnlyToday()
    {
        (DateOnly start, DateOnly end) = DefaultJournalPeriod.ForDays(1, Today);

        Assert.Equal(Today, start);
        Assert.Equal(Today, end);
    }

    [Fact]
    public void ForDays_SevenDaysEndsOnTodayAndStartsSixDaysBefore()
    {
        (DateOnly start, DateOnly end) = DefaultJournalPeriod.ForDays(7, Today);

        Assert.Equal(new DateOnly(2026, 3, 9), start);
        Assert.Equal(Today, end);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ForDays_RejectsNonPositiveDayCounts(int days)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DefaultJournalPeriod.ForDays(days, Today));
    }

    [Fact]
    public void Validate_AcceptsNull()
    {
        DefaultJournalPeriod.Validate(null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_RejectsNonPositiveValues(int days)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DefaultJournalPeriod.Validate(days));
    }

    [Fact]
    public void Validate_AcceptsAPositiveValue()
    {
        DefaultJournalPeriod.Validate(30);
    }
}
