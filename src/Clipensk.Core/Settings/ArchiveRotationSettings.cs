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

    /// <summary>
    /// Evaluates the configured Any/All combination rule for one candidate segment. This is the
    /// single definition of the rule: the pure planner and storage-backed rotation both call it, so
    /// the semantics cannot drift between the two paths.
    ///
    /// <paramref name="physicalSizeBytes"/> must be supplied whenever <see cref="MaxBytes"/> is
    /// configured, because physical size is only knowable from a closed, validated Archive file.
    /// </summary>
    public bool HasReachedThresholds(
        int calendarDayCount,
        long recordCount,
        long? physicalSizeBytes)
    {
        if (calendarDayCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(calendarDayCount),
                "A candidate segment must contain at least one complete calendar day.");
        }

        if (recordCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recordCount),
                "Candidate record count cannot be negative.");
        }

        if (MaxBytes.HasValue && physicalSizeBytes is null)
        {
            throw new ArgumentNullException(
                nameof(physicalSizeBytes),
                "Physical Archive size rotation requires a measured database file size.");
        }

        int configuredThresholdCount = 0;
        int reachedThresholdCount = 0;

        if (MaxCalendarDays is int maxDays)
        {
            configuredThresholdCount++;
            if (calendarDayCount >= maxDays)
            {
                reachedThresholdCount++;
            }
        }

        if (MaxRecordCount is long maxRecords)
        {
            configuredThresholdCount++;
            if (recordCount >= maxRecords)
            {
                reachedThresholdCount++;
            }
        }

        if (MaxBytes is long maxBytes)
        {
            configuredThresholdCount++;
            if (physicalSizeBytes!.Value >= maxBytes)
            {
                reachedThresholdCount++;
            }
        }

        ArchiveRotationThresholdMode mode = ThresholdMode ?? ArchiveRotationThresholdMode.Any;
        return mode == ArchiveRotationThresholdMode.All
            ? reachedThresholdCount == configuredThresholdCount
            : reachedThresholdCount > 0;
    }

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
