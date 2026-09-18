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

public sealed record ArchiveRotationPlan(
    IReadOnlyList<JournalDateRange> ReadyRanges,
    JournalDateRange? OpenRange);

/// <summary>
/// Plans rotation for ordered, contiguous complete calendar days using only thresholds that can be
/// evaluated without materializing Archive files. A range becomes ready after a complete day makes
/// the configured Any/All rule reach its threshold. The final not-yet-ready range remains open in
/// Current. Physical-size rotation is fail-closed here and belongs to storage-backed orchestration.
/// </summary>
public sealed class ArchiveRotationPlanner
{
    public ArchiveRotationPlan Build(
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
            return new ArchiveRotationPlan(
                Array.Empty<JournalDateRange>(),
                OpenRange: null);
        }

        ValidateContiguousOrder(orderedDays);

        var readyRanges = new List<JournalDateRange>();
        DateOnly? openStart = null;
        DateOnly openEnd = default;
        int openDayCount = 0;
        long openRecordCount = 0;

        foreach (ArchiveRotationDayMetrics day in orderedDays)
        {
            openStart ??= day.CalendarDate;
            openEnd = day.CalendarDate;
            openDayCount++;
            openRecordCount = SaturatingAdd(openRecordCount, day.RecordCount);

            if (!HasReachedConfiguredThresholds(
                    openDayCount,
                    openRecordCount,
                    settings))
            {
                continue;
            }

            readyRanges.Add(new JournalDateRange(openStart.Value, openEnd));
            openStart = null;
            openDayCount = 0;
            openRecordCount = 0;
        }

        JournalDateRange? openRange = openStart is DateOnly start
            ? new JournalDateRange(start, openEnd)
            : null;

        return new ArchiveRotationPlan(
            readyRanges.AsReadOnly(),
            openRange);
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

    private static bool HasReachedConfiguredThresholds(
        int segmentDayCount,
        long segmentRecordCount,
        ArchiveRotationSettings settings)
    {
        int configuredThresholdCount = 0;
        int reachedThresholdCount = 0;

        if (settings.MaxCalendarDays is int maxDays)
        {
            configuredThresholdCount++;
            if (segmentDayCount >= maxDays)
            {
                reachedThresholdCount++;
            }
        }

        if (settings.MaxRecordCount is long maxRecords)
        {
            configuredThresholdCount++;
            if (segmentRecordCount >= maxRecords)
            {
                reachedThresholdCount++;
            }
        }

        ArchiveRotationThresholdMode mode =
            settings.ThresholdMode ?? ArchiveRotationThresholdMode.Any;
        return mode == ArchiveRotationThresholdMode.All
            ? reachedThresholdCount == configuredThresholdCount
            : reachedThresholdCount > 0;
    }

    private static long SaturatingAdd(long current, long next) =>
        next > long.MaxValue - current
            ? long.MaxValue
            : current + next;
}
