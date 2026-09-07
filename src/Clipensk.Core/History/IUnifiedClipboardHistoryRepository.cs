namespace Clipensk.Core.History;

public interface IUnifiedClipboardHistoryRepository
{
    /// <summary>
    /// Reads at most <paramref name="limit"/> logical events from Current + selected
    /// Archive segments within the inclusive calendar period. Results are ordered by
    /// UTC timestamp then EventId descending. A temporarily duplicated logical event
    /// is returned once with every verified physical location.
    /// </summary>
    ValueTask<IReadOnlyList<UnifiedClipboardHistoryEntry>> ReadAsync(
        JournalDateRange period,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the next logical events strictly older than <paramref name="before"/>
    /// in the same global UTC/EventId order. The cursor must belong to the same
    /// calendar period.
    /// </summary>
    ValueTask<IReadOnlyList<UnifiedClipboardHistoryEntry>> ReadBeforeAsync(
        JournalDateRange period,
        int limit,
        ClipboardHistoryCursor before,
        CancellationToken cancellationToken = default);
}
