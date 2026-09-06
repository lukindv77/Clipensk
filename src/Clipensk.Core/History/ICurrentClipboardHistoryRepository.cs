namespace Clipensk.Core.History;

public interface ICurrentClipboardHistoryRepository
{
    /// <summary>
    /// Reads at most <paramref name="limit"/> complete events from Current within
    /// the inclusive calendar period, ordered by UTC time then EventId descending.
    /// The caller must supply a positive limit; there is no implicit period or limit.
    /// </summary>
    ValueTask<IReadOnlyList<ClipboardHistoryEntry>> ReadAsync(
        JournalDateRange period,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the next complete events strictly older than the supplied cursor in
    /// UTC/EventId order. The period must match the cursor's period. An empty result
    /// means no further events were visible for that read; pages are separate snapshots.
    /// </summary>
    ValueTask<IReadOnlyList<ClipboardHistoryEntry>> ReadBeforeAsync(
        JournalDateRange period,
        int limit,
        ClipboardHistoryCursor before,
        CancellationToken cancellationToken = default);
}
