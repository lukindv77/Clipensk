namespace Clipensk.Core.History;

public sealed record EventTimeContext(
    DateTimeOffset Timestamp,
    string WindowsTimeZoneId)
{
    public DateTime UtcTimestamp => Timestamp.UtcDateTime;

    public TimeSpan Offset => Timestamp.Offset;

    public DateOnly CalendarDate => DateOnly.FromDateTime(Timestamp.DateTime);

    public static EventTimeContext CaptureNow(TimeProvider? timeProvider = null)
    {
        timeProvider ??= TimeProvider.System;
        DateTimeOffset utcNow = timeProvider.GetUtcNow();
        TimeZoneInfo localZone = TimeZoneInfo.Local;
        DateTimeOffset localNow = TimeZoneInfo.ConvertTime(utcNow, localZone);

        return new EventTimeContext(localNow, localZone.Id);
    }
}
