using Clipensk.Core.History;
using Clipensk.Core.Settings;

namespace Clipensk.Core.Storage;

/// <summary>
/// Additive rotation metrics for one complete calendar day. Callers must include zero-record
/// days when they are part of the intended coverage so the planner can preserve contiguous ranges.
/// Physical Archive database size is intentionally not represented here because it is not an
/// additive per-day metric and must be measured by storage-backed rotation orchestration.
/// </summary>
public readonly record struct ArchiveRotationDayMetrics
{
    public ArchiveRotationDayMetrics(
        DateOnly calendarDate,
        long recordCount)
    {
        if (recordCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recordCount),
                "Archive rotation record count cannot be negative.");
        }

        CalendarDate = calendarDate;
        RecordCount = recordCount;
    }

    public DateOnly CalendarDate { get; }

    public long RecordCount { get; }
}

/// <summary>
/// Deterministically partitions ordered, contiguous calendar-day metrics using the thresholds that
/// can be evaluated without materializing Archive files. Physical-size rotation is fail-closed here
/// and belongs to storage-backed orchestration that measures the actual Archive database file.
/// </summary>
public sealed class ArchiveRotationPlanner
{
    public IReadOnlyList<JournalDateRange> Build(
        IReadOnlyList<ArchiveRotationDayMetrics> orderedDays,
        ArchiveRotationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(orderedDays);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        if (settings.MaxBytes.HasValue)
        {
            throw new NotSupportedException(
                "Physical Archive size rotation requires storage-backed measurement of the actual database file.");
        }

        if (orderedDays.Count == 0)
        {
            return Array.Empty<JournalDateRange>();
        }

        ValidateContiguousOrder(orderedDays);

        var ranges = new List<JournalDateRange>();
        ArchiveRotationDayMetrics first = orderedDays[0];
        DateOnly segmentStart = first.CalendarDate;
        DateOnly segmentEnd = first.CalendarDate;
        int segmentDayCount = 1;
        long segmentRecordCount = settings.MaxRecordCount.HasValue ? first.RecordCount : 0;

        for (int index = 1; index < orderedDays.Count; index++)
        {
            ArchiveRotationDayMetrics day = orderedDays[index];
            if (ShouldStartNextRange(
                    segmentDayCount,
                    segmentRecordCount,
                    day,
                    settings))
            {
                ranges.Add(new JournalDateRange(segmentStart, segmentEnd));
                segmentStart = day.CalendarDate;
                segmentEnd = day.CalendarDate;
                segmentDayCount = 1;
                segmentRecordCount = settings.MaxRecordCount.HasValue ? day.RecordCount : 0;
                continue;
            }

            segmentEnd = day.CalendarDate;
            segmentDayCount++;
            if (settings.MaxRecordCount.HasValue)
            {
                segmentRecordCount += day.RecordCount;
            }
        }

        ranges.Add(new JournalDateRange(segmentStart, segmentEnd));
        return ranges;
    }

    private static void ValidateContiguousOrder(
        IReadOnlyList<ArchiveRotationDayMetrics> orderedDays)
    {
        for (int index = 1; index < orderedDays.Count; index++)
        {
            DateOnly previous = orderedDays[index - 1].CalendarDate;
            DateOnly current = orderedDays[index].CalendarDate;
            if (current.DayNumber != previous.DayNumber + 1)
            {
                throw new ArgumentException(
                    "Archive rotation day metrics must be strictly ordered and contiguous by calendar date.",
                    nameof(orderedDays));
            }
        }
    }

    private static bool ShouldStartNextRange(
        int segmentDayCount,
        long segmentRecordCount,
        ArchiveRotationDayMetrics nextDay,
        ArchiveRotationSettings settings)
    {
        int configuredThresholdCount = 0;
        int exceededThresholdCount = 0;

        if (settings.MaxCalendarDays is int maxDays)
        {
            configuredThresholdCount++;
            if (segmentDayCount >= maxDays)
            {
                exceededThresholdCount++;
            }
        }

        if (settings.MaxRecordCount is long maxRecords)
        {
            configuredThresholdCount++;
            if (WouldExceed(segmentRecordCount, nextDay.RecordCount, maxRecords))
            {
                exceededThresholdCount++;
            }
        }

        ArchiveRotationThresholdMode mode =
            settings.ThresholdMode ?? ArchiveRotationThresholdMode.Any;
        return mode == ArchiveRotationThresholdMode.All
            ? exceededThresholdCount == configuredThresholdCount
            : exceededThresholdCount > 0;
    }

    private static bool WouldExceed(long current, long next, long maximum)
    {
        // Keep one oversized day intact. This method is only used when the current segment
        // already contains at least one day, so an oversized next day starts a new segment
        // when the configured threshold combination says it is time to rotate.
        return next > maximum || current > maximum - next;
    }
}
