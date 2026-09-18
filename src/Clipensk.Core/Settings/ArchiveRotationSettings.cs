namespace Clipensk.Core.Settings;

public enum ArchiveRotationThresholdMode
{
    Any = 1,
    All = 2,
}

public sealed record ArchiveRotationSettings
{
    public long? MaxRecordCount { get; init; }

    public long? MaxBytes { get; init; }

    public int? MaxCalendarDays { get; init; }

    public ArchiveRotationThresholdMode? ThresholdMode { get; init; }

    public bool IsConfigured =>
        MaxRecordCount.HasValue || MaxBytes.HasValue || MaxCalendarDays.HasValue;

    public void Validate()
    {
        int configuredThresholdCount = 0;
        if (MaxRecordCount.HasValue)
        {
            configuredThresholdCount++;
        }

        if (MaxBytes.HasValue)
        {
            configuredThresholdCount++;
        }

        if (MaxCalendarDays.HasValue)
        {
            configuredThresholdCount++;
        }

        if (configuredThresholdCount == 0)
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

        if (ThresholdMode is ArchiveRotationThresholdMode mode &&
            mode is not ArchiveRotationThresholdMode.Any and not ArchiveRotationThresholdMode.All)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ThresholdMode),
                "Archive rotation threshold mode must be Any or All.");
        }

        if (configuredThresholdCount > 1 && ThresholdMode is null)
        {
            throw new ArgumentException(
                "Archive rotation with multiple thresholds must explicitly select Any or All threshold mode.",
                nameof(ThresholdMode));
        }
    }
}
