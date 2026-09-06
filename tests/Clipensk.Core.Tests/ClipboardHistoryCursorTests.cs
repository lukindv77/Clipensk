using Clipensk.Core.History;
using Xunit;

namespace Clipensk.Core.Tests;

public sealed class ClipboardHistoryCursorTests
{
    private static readonly JournalDateRange Period = new(new DateOnly(2026, 9, 6), new DateOnly(2026, 9, 6));

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Constructor_RejectsNonUtcTimestamp(DateTimeKind kind)
    {
        Assert.Throws<ArgumentException>(() => new ClipboardHistoryCursor(
            Period, new DateTime(2026, 9, 6, 12, 0, 0, kind), Guid.NewGuid()));
    }

    [Fact]
    public void Constructor_RejectsEmptyEventId()
    {
        Assert.Throws<ArgumentException>(() => new ClipboardHistoryCursor(
            Period, new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc), Guid.Empty));
    }

    [Fact]
    public void FromEntry_UsesStoredUtcAndCalendarDateDespiteDifferentUtcDay()
    {
        var eventTime = new EventTimeContext(
            new DateTimeOffset(2026, 9, 6, 0, 15, 0, TimeSpan.FromHours(7)), "Recorded Zone");
        var entry = new ClipboardHistoryEntry(Guid.NewGuid(), eventTime, null, null, []);

        ClipboardHistoryCursor cursor = ClipboardHistoryCursor.FromEntry(Period, entry);

        Assert.Equal(Period, cursor.Period);
        Assert.Equal(entry.EventId, cursor.EventId);
        Assert.Equal(new DateTime(2026, 9, 5, 17, 15, 0, DateTimeKind.Utc), cursor.UtcTimestamp);
        Assert.Equal(DateTimeKind.Utc, cursor.UtcTimestamp.Kind);
        Assert.Throws<ArgumentException>(() => ClipboardHistoryCursor.FromEntry(
            new JournalDateRange(new DateOnly(2026, 9, 5), new DateOnly(2026, 9, 5)), entry));
    }
}
