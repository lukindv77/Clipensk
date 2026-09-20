namespace Clipensk.Core.History;

public interface ICurrentClipboardHistoryRepository
{
    /// <summary>
    /// Reads at most <paramref name="limit"/> complete events from Current within
    /// the inclusive calendar period, ordered by UTC time then EventId descending.
    /// The caller must supply a positive limit; there is no implicit period or limit.
    ///
    /// <paramref name="searchText"/>, when non-null and non-empty, restricts results to events
    /// with at least one payload whose stored <c>SearchText</c> contains it, case-insensitively
    /// (including non-ASCII alphabets). It never widens which physical database this call opens;
    /// the period alone decides that, per <c>docs/REQUIREMENTS.md</c> §8.
    /// </summary>
    ValueTask<IReadOnlyList<ClipboardHistoryEntry>> ReadAsync(
        JournalDateRange period,
        int limit,
        string? searchText = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the next complete events strictly older than the supplied cursor in
    /// UTC/EventId order. The period must match the cursor's period. An empty result
    /// means no further events were visible for that read; pages are separate snapshots.
    ///
    /// <paramref name="searchText"/> has the same meaning as in <see cref="ReadAsync"/> and must be
    /// the same value used to produce <paramref name="before"/>, so a page and its continuation
    /// filter identically.
    /// </summary>
    ValueTask<IReadOnlyList<ClipboardHistoryEntry>> ReadBeforeAsync(
        JournalDateRange period,
        int limit,
        ClipboardHistoryCursor before,
        string? searchText = null,
        CancellationToken cancellationToken = default);
}
