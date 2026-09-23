namespace Clipensk.Core.History;

/// <summary>
/// Restricts a journal read to events from a set of source applications and/or to events holding a
/// representation of one exact clipboard format, per <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §4
/// and §8. Applied within each already-selected physical database; it never changes which databases
/// a read opens.
/// </summary>
public sealed class ClipboardHistoryFilter : IEquatable<ClipboardHistoryFilter>
{
    public ClipboardHistoryFilter(
        IEnumerable<Guid>? sourceApplicationIds = null,
        string? formatName = null)
    {
        if (sourceApplicationIds is not null)
        {
            var sources = new SortedSet<Guid>();
            foreach (Guid sourceApplicationId in sourceApplicationIds)
            {
                if (sourceApplicationId == Guid.Empty)
                {
                    throw new ArgumentException(
                        "A source application filter cannot contain an empty identifier.",
                        nameof(sourceApplicationIds));
                }
                if (!sources.Add(sourceApplicationId))
                {
                    throw new ArgumentException(
                        "A source application filter cannot repeat an identifier.",
                        nameof(sourceApplicationIds));
                }
            }
            SourceApplicationIds = sources.ToArray();
        }

        if (formatName is not null && string.IsNullOrWhiteSpace(formatName))
        {
            throw new ArgumentException("A format filter requires a clipboard format name.", nameof(formatName));
        }
        FormatName = formatName;
    }

    /// <summary>
    /// <see langword="null"/> matches any source, including events without a known source; an empty
    /// list matches no event. Sorted, without repeats.
    /// </summary>
    public IReadOnlyList<Guid>? SourceApplicationIds { get; }

    /// <summary>
    /// <see langword="null"/> matches any event; otherwise only events with at least one
    /// representation whose <c>FormatName</c> is exactly this value (ordinal, case-sensitive — the
    /// same comparison capture policy uses).
    /// </summary>
    public string? FormatName { get; }

    public static ClipboardHistoryFilter ForSource(Guid sourceApplicationId) => new([sourceApplicationId]);

    public bool Equals(ClipboardHistoryFilter? other) =>
        other is not null &&
        string.Equals(FormatName, other.FormatName, StringComparison.Ordinal) &&
        (SourceApplicationIds is null
            ? other.SourceApplicationIds is null
            : other.SourceApplicationIds is not null &&
              SourceApplicationIds.SequenceEqual(other.SourceApplicationIds));

    public override bool Equals(object? obj) => Equals(obj as ClipboardHistoryFilter);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(FormatName, StringComparer.Ordinal);
        hash.Add(SourceApplicationIds is null ? -1 : SourceApplicationIds.Count);
        foreach (Guid sourceApplicationId in SourceApplicationIds ?? [])
        {
            hash.Add(sourceApplicationId);
        }
        return hash.ToHashCode();
    }
}
