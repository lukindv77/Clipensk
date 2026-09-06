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
}
