namespace Clipensk.Core.Clipboard;

/// <summary>
/// The single definition of what a journal search term matches against a stored
/// <c>SearchText</c> projection, per <c>docs/REQUIREMENTS.md</c> §8.
///
/// This exists because SQLite's built-in <c>LIKE</c>/<c>LOWER</c> only fold ASCII case by default;
/// the primary Clipensk UI is Russian, so a search that only matched Cyrillic on exact case would
/// be effectively broken for most real queries. Matching runs in .NET instead — via a custom SQL
/// function registered by the storage layer — so it gets .NET's Unicode-aware case folding, which
/// covers Cyrillic and other alphabets correctly.
/// </summary>
public static class ClipboardHistorySearchMatcher
{
    /// <summary>
    /// Trims a raw search box value and returns <c>null</c> when nothing meaningful remains, so
    /// "no search" has exactly one representation throughout the read path.
    /// </summary>
    public static string? Normalize(string? rawTerm)
    {
        if (rawTerm is null)
        {
            return null;
        }

        string trimmed = rawTerm.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// True when <paramref name="searchText"/> contains <paramref name="term"/>, ignoring case.
    /// A <c>null</c> or empty <paramref name="searchText"/> never matches: it means the payload has
    /// no search projection, not that it matches every term.
    /// </summary>
    public static bool Matches(string? searchText, string term)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        return !string.IsNullOrEmpty(searchText) &&
            searchText.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}
