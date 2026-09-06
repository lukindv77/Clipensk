namespace Clipensk.Core.History;

/// <summary>
/// An exclusive position in Current history's UTC/EventId descending order.
/// It retains no session, connection or payload and is bound to one calendar period.
/// </summary>
public sealed record ClipboardHistoryCursor
{
    public ClipboardHistoryCursor(JournalDateRange period, DateTime utcTimestamp, Guid eventId)
    {
        if (utcTimestamp.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("History cursor timestamp must be UTC.", nameof(utcTimestamp));
        }
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("History cursor EventId cannot be empty.", nameof(eventId));
        }

        Period = period;
        UtcTimestamp = utcTimestamp;
        EventId = eventId;
    }

    public JournalDateRange Period { get; }
    public DateTime UtcTimestamp { get; }
    public Guid EventId { get; }

    public static ClipboardHistoryCursor FromEntry(JournalDateRange period, ClipboardHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(entry.EventTime);
        if (!period.Contains(entry.EventTime.CalendarDate))
        {
            throw new ArgumentException("Cursor entry must belong to the requested period.", nameof(entry));
        }

        return new ClipboardHistoryCursor(period, entry.EventTime.UtcTimestamp, entry.EventId);
    }
}
