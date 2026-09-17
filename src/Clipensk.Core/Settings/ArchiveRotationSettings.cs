namespace Clipensk.Core.Settings;

public sealed record ArchiveRotationSettings
{
    public long? MaxRecordCount { get; init; }

    public long? MaxBytes { get; init; }

    public int? MaxCalendarDays { get; init; }

    public bool IsConfigured =>
        MaxRecordCount.HasValue || MaxBytes.HasValue || MaxCalendarDays.HasValue;

    public void Validate()
    {
        if (!IsConfigured)
        {
            throw new ArgumentException(
                "Archive rotation must configure at least one threshold.");
        }

        if (MaxRecordCount is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRecordCount),
                "Archive rotation record-count threshold must be positive.");
        }

        if (MaxBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxBytes),
                "Archive rotation byte threshold must be positive.");
        }

        if (MaxCalendarDays is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxCalendarDays),
                "Archive rotation calendar-day threshold must be positive.");
        }
    }
}
