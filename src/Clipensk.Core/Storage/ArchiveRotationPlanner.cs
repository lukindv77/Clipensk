using Clipensk.Core.History;
using Clipensk.Core.Settings;

namespace Clipensk.Core.Storage;

/// <summary>
/// Rotation metrics for one completed calendar day that contains at least one Current event.
/// Sparse calendar dates are expected; missing days are represented by gaps between metrics.
/// </summary>
public readonly record struct ArchiveRotationDayMetrics
{
    public ArchiveRotationDayMetrics(
        DateOnly calendarDate,
        long recordCount)
    {
        if (recordCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recordCount),
                "Archive rotation record count must be positive for an observed event day.");
        }

        CalendarDate = calendarDate;
        RecordCount = recordCount;
    }

    public DateOnly CalendarDate { get; }

    public long RecordCount { get; }
}

public sealed record ArchiveRotationPlan
{
    public ArchiveRotationPlan(
        IReadOnlyList<JournalDateRange> readySegments,
        JournalDateRange? tail)
    {
        ArgumentNullException.ThrowIfNull(readySegments);
        ReadySegments = readySegments;
        Tail = tail;
    }

    public IReadOnlyList<JournalDateRange> ReadySegments { get; }

    public JournalDateRange? Tail { get; }

    public bool HasReadySegments => ReadySegments.Count > 0;
}

/// <summary>
/// Plans threshold-ready Archive ranges from ordered completed Current event days.
/// The newest under-threshold range remains as Tail and is not considered ready for automatic
/// rotation. Physical-size rotation is storage-backed and intentionally fails closed here.
/// </summary>
public sealed class ArchiveRotationPlanner
{
    public ArchiveRotationPlan Build(
        IReadOnlyList<ArchiveRotationDayMetrics> orderedEventDays,
        ArchiveRotationSettings settings,
        DateOnly currentLocalDate)
    {
        ArgumentNullException.ThrowIfNull(orderedEventDays);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();

        if (settings.MaxBytes.HasValue)
        {
            throw new NotSupportedException(
                "Physical Archive size rotation requires storage-backed measurement of the actual database file.");
        }

        if (orderedEventDays.Count == 0)
        {
            return new ArchiveRotationPlan(
                Array.Empty<JournalDateRange>(),
                tail: null);
        }

        ValidateOrderedClosedEventDays(orderedEventDays, currentLocalDate);

        var readySegments = new List<JournalDateRange>();
        DateOnly? segmentStart = null;
        DateOnly segmentEnd = default;
        long segmentRecordCount = 0;
        bool segmentRecordCountExceeded = false;

        foreach (ArchiveRotationDayMetrics day in orderedEventDays)
        {
            if (segmentStart is null)
            {
                StartSegment(
                    day,
                    settings,
                    out segmentStart,
                    out segmentEnd,
                    out segmentRecordCount,
                    out segmentRecordCountExceeded);

                if (CurrentSegmentTriggers(
                        segmentStart.Value,
                        segmentEnd,
                        segmentRecordCountExceeded,
                        settings))
                {
                    readySegments.Add(new JournalDateRange(segmentStart.Value, segmentEnd));
                    segmentStart = null;
                }

                continue;
            }

            bool candidateRecordCountExceeded = segmentRecordCountExceeded;
            long candidateRecordCount = segmentRecordCount;
            if (settings.MaxRecordCount is long maxRecords && !candidateRecordCountExceeded)
            {
                if (day.RecordCount > maxRecords - candidateRecordCount)
                {
                    candidateRecordCountExceeded = true;
                }
                else
                {
                    candidateRecordCount += day.RecordCount;
                }
            }

            bool candidateCalendarSpanExceeded =
                settings.MaxCalendarDays is int maxDays &&
                day.CalendarDate.DayNumber - segmentStart.Value.DayNumber + 1 > maxDays;

            if (ThresholdCombinationTriggers(
                    candidateRecordCountExceeded,
                    candidateCalendarSpanExceeded,
                    settings))
            {
                readySegments.Add(new JournalDateRange(segmentStart.Value, segmentEnd));

                StartSegment(
                    day,
                    settings,
                    out segmentStart,
                    out segmentEnd,
                    out segmentRecordCount,
                    out segmentRecordCountExceeded);

                if (CurrentSegmentTriggers(
                        segmentStart.Value,
                        segmentEnd,
                        segmentRecordCountExceeded,
                        settings))
                {
                    readySegments.Add(new JournalDateRange(segmentStart.Value, segmentEnd));
                    segmentStart = null;
                }

                continue;
            }

            segmentEnd = day.CalendarDate;
            segmentRecordCount = candidateRecordCount;
            segmentRecordCountExceeded = candidateRecordCountExceeded;
        }

        JournalDateRange? tail = segmentStart is DateOnly tailStart
            ? new JournalDateRange(tailStart, segmentEnd)
            : null;

        return new ArchiveRotationPlan(
            readySegments.ToArray(),
            tail);
    }

    private static void ValidateOrderedClosedEventDays(
        IReadOnlyList<ArchiveRotationDayMetrics> orderedEventDays,
        DateOnly currentLocalDate)
    {
        DateOnly? previous = null;
        foreach (ArchiveRotationDayMetrics day in orderedEventDays)
        {
            if (day.CalendarDate >= currentLocalDate)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(orderedEventDays),
                    "Archive rotation accepts only completed calendar days.");
            }

            if (previous is DateOnly previousDate &&
                day.CalendarDate <= previousDate)
            {
                throw new ArgumentException(
                    "Archive rotation day metrics must be strictly increasing by calendar date.",
                    nameof(orderedEventDays));
            }

            previous = day.CalendarDate;
        }
    }

    private static void StartSegment(
        ArchiveRotationDayMetrics day,
        ArchiveRotationSettings settings,
        out DateOnly? segmentStart,
        out DateOnly segmentEnd,
        out long segmentRecordCount,
        out bool segmentRecordCountExceeded)
    {
        segmentStart = day.CalendarDate;
        segmentEnd = day.CalendarDate;

        if (settings.MaxRecordCount is long maxRecords)
        {
            segmentRecordCountExceeded = day.RecordCount > maxRecords;
            segmentRecordCount = segmentRecordCountExceeded
                ? 0
                : day.RecordCount;
        }
        else
        {
            segmentRecordCountExceeded = false;
            segmentRecordCount = 0;
        }
    }

    private static bool CurrentSegmentTriggers(
        DateOnly segmentStart,
        DateOnly segmentEnd,
        bool recordCountExceeded,
        ArchiveRotationSettings settings)
    {
        bool calendarSpanExceeded =
            settings.MaxCalendarDays is int maxDays &&
            segmentEnd.DayNumber - segmentStart.DayNumber + 1 > maxDays;

        return ThresholdCombinationTriggers(
            recordCountExceeded,
            calendarSpanExceeded,
            settings);
    }

    private static bool ThresholdCombinationTriggers(
        bool recordCountExceeded,
        bool calendarSpanExceeded,
        ArchiveRotationSettings settings)
    {
        int configuredThresholdCount = 0;
        int exceededThresholdCount = 0;

        if (settings.MaxRecordCount.HasValue)
        {
            configuredThresholdCount++;
            if (recordCountExceeded)
            {
                exceededThresholdCount++;
            }
        }

        if (settings.MaxCalendarDays.HasValue)
        {
            configuredThresholdCount++;
            if (calendarSpanExceeded)
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
}
