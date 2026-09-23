using System.Globalization;
using System.Text;
using Clipensk.Core.History;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.History;

/// <summary>
/// Translates a <see cref="ClipboardHistoryFilter"/> into predicates over <c>ClipboardHistoryEvent</c>
/// rows, shared by the Current and Archive history readers so both filter identically.
/// </summary>
internal static class ClipboardHistoryFilterSql
{
    /// <summary>
    /// Adds the filter's parameters to <paramref name="command"/> and returns its predicates, each
    /// prefixed with <c>AND</c>, for a <c>WHERE</c> clause whose unqualified columns are those of
    /// <c>ClipboardHistoryEvent</c>. Returns an empty string for no filter.
    /// </summary>
    public static string AddPredicates(SqliteCommand command, ClipboardHistoryFilter? filter)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (filter is null)
        {
            return string.Empty;
        }

        var sql = new StringBuilder();
        if (filter.SourceApplicationIds is { } sources)
        {
            if (sources.Count == 0)
            {
                sql.Append(" AND 0");
            }
            else
            {
                var names = new List<string>(sources.Count);
                for (int index = 0; index < sources.Count; index++)
                {
                    string name = "$filterSource" + index.ToString(CultureInfo.InvariantCulture);
                    command.Parameters.AddWithValue(name, sources[index].ToString("D"));
                    names.Add(name);
                }
                sql.Append(" AND SourceApplicationId IN (").Append(string.Join(", ", names)).Append(')');
            }
        }

        if (filter.FormatName is { } formatName)
        {
            command.Parameters.AddWithValue("$filterFormatName", formatName);
            sql.Append("""
                 AND EventId IN (
                    SELECT EventId FROM ClipboardHistoryPayload
                    WHERE FormatName = $filterFormatName COLLATE BINARY)
                """);
        }

        return sql.ToString();
    }
}
