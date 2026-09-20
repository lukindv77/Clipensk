using Clipensk.Core.Clipboard;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.History;

/// <summary>
/// Registers the journal search predicate as a scalar SQL function on one connection, so history
/// queries can filter by it without SQLite's own case folding (which does not cover Cyrillic).
/// </summary>
internal static class SqliteClipboardHistorySearchFunction
{
    public const string SqlName = "CLIPENSK_SEARCH_MATCH";

    public static void Register(SqliteConnection connection) =>
        connection.CreateFunction<string?, string, bool>(
            SqlName,
            ClipboardHistorySearchMatcher.Matches);
}
