using System.Globalization;
using Clipensk.Core.Clipboard;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.History;

/// <summary>
/// Totals of one history purge over one or more databases, per
/// <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §4: what the confirmation screen shows before, and the
/// result reports after.
/// </summary>
public sealed record ClipboardHistoryPurgeSummary(
    int DeletedRepresentations,
    int DeletedExternalReferences,
    int DeletedRecords,
    int TrimmedRecords,
    IReadOnlyDictionary<string, int> DeletedRepresentationsByFormat)
{
    public static ClipboardHistoryPurgeSummary Empty { get; } = new(
        0,
        0,
        0,
        0,
        new Dictionary<string, int>(StringComparer.Ordinal));

    public bool IsEmpty => DeletedRepresentations == 0 && DeletedRecords == 0;

    public ClipboardHistoryPurgeSummary Add(ClipboardHistoryPurgeSummary other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var byFormat = new Dictionary<string, int>(DeletedRepresentationsByFormat, StringComparer.Ordinal);
        foreach ((string formatName, int count) in other.DeletedRepresentationsByFormat)
        {
            byFormat[formatName] = byFormat.GetValueOrDefault(formatName) + count;
        }

        return new ClipboardHistoryPurgeSummary(
            DeletedRepresentations + other.DeletedRepresentations,
            DeletedExternalReferences + other.DeletedExternalReferences,
            DeletedRecords + other.DeletedRecords,
            TrimmedRecords + other.TrimmedRecords,
            byFormat);
    }
}

/// <summary>
/// The rows one purge will delete from one history database (Current or an Archive — both use the
/// same history schema).
/// </summary>
internal sealed record ClipboardHistoryPurgePlan(
    IReadOnlyList<ClipboardHistoryPurgePlan.PayloadKey> Representations,
    IReadOnlyList<string> Records,
    ClipboardHistoryPurgeSummary Summary)
{
    internal sealed record PayloadKey(
        string EventId,
        long PayloadOrder,
        string FormatName,
        bool IsExternalReference);
}

/// <summary>
/// Sums the purge plans of several history databases, counting each logical event once. An event
/// can temporarily exist in two databases (a Current→Archive copy is committed before Current is
/// purged); both copies hold the same representations, so only the first plan that touches the
/// event counts it. Plans must therefore be added Current first, then Archives.
/// </summary>
internal sealed class ClipboardHistoryPurgeSummaryAccumulator
{
    private readonly HashSet<string> _countedEvents = new(StringComparer.Ordinal);

    public ClipboardHistoryPurgeSummary AddDistinct(ClipboardHistoryPurgePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var records = new HashSet<string>(
            plan.Records.Where(eventId => !_countedEvents.Contains(eventId)),
            StringComparer.Ordinal);
        var trimmed = new HashSet<string>(StringComparer.Ordinal);
        var byFormat = new Dictionary<string, int>(StringComparer.Ordinal);
        int representations = 0;
        int externalReferences = 0;
        foreach (ClipboardHistoryPurgePlan.PayloadKey key in plan.Representations)
        {
            if (_countedEvents.Contains(key.EventId))
            {
                continue;
            }

            representations++;
            byFormat[key.FormatName] = byFormat.GetValueOrDefault(key.FormatName) + 1;
            if (key.IsExternalReference)
            {
                externalReferences++;
            }
            if (!records.Contains(key.EventId))
            {
                trimmed.Add(key.EventId);
            }
        }

        _countedEvents.UnionWith(records);
        _countedEvents.UnionWith(trimmed);
        return new ClipboardHistoryPurgeSummary(
            representations,
            externalReferences,
            records.Count,
            trimmed.Count,
            byFormat);
    }
}

/// <summary>
/// Plans and applies a history purge inside a caller-owned transaction. Purging is per
/// representation: a disallowed representation is deleted on its own, and a record left without any
/// representation — including one that was already empty — is deleted as a whole.
/// </summary>
internal static class ClipboardHistoryPurge
{
    public static ClipboardHistoryPurgePlan PlanInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<string> sourceApplicationIds,
        ClipboardHistoryPurgeRule rule,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sourceApplicationIds);
        ArgumentNullException.ThrowIfNull(rule);
        token.ThrowIfCancellationRequested();

        var representations = new List<ClipboardHistoryPurgePlan.PayloadKey>();
        var records = new List<string>();
        var byFormat = new Dictionary<string, int>(StringComparer.Ordinal);
        int externalReferences = 0;
        int trimmedRecords = 0;
        if (sourceApplicationIds.Count == 0)
        {
            return new ClipboardHistoryPurgePlan(representations, records, ClipboardHistoryPurgeSummary.Empty);
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT e.EventId, p.PayloadOrder, p.FormatName, p.PayloadKind
            FROM ClipboardHistoryEvent e
            LEFT JOIN ClipboardHistoryPayload p ON p.EventId = e.EventId
            WHERE e.SourceApplicationId IN ({AddSourceParameters(command, sourceApplicationIds)})
            ORDER BY e.EventId COLLATE BINARY, p.PayloadOrder;
            """;

        string? currentEvent = null;
        int currentTotal = 0;
        int currentDeleted = 0;
        void CloseEvent()
        {
            if (currentEvent is null)
            {
                return;
            }
            if (currentDeleted == currentTotal)
            {
                records.Add(currentEvent);
            }
            else if (currentDeleted > 0)
            {
                trimmedRecords++;
            }
        }

        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                string eventId = ReadCanonicalGuid(reader.GetString(0));
                if (!string.Equals(eventId, currentEvent, StringComparison.Ordinal))
                {
                    CloseEvent();
                    currentEvent = eventId;
                    currentTotal = 0;
                    currentDeleted = 0;
                }

                if (reader.IsDBNull(1))
                {
                    continue;
                }

                long payloadOrder = reader.GetInt64(1);
                string formatName = reader.GetString(2);
                string payloadKind = reader.GetString(3);
                if (payloadOrder < 0 || string.IsNullOrWhiteSpace(formatName) || !IsKnownPayloadKind(payloadKind))
                {
                    throw new InvalidDataException("Clipboard history contains an invalid representation row.");
                }

                currentTotal++;
                if (rule.Retains(formatName))
                {
                    continue;
                }

                currentDeleted++;
                bool isExternalReference = payloadKind is "PngImage" or "CustomBinary";
                representations.Add(new ClipboardHistoryPurgePlan.PayloadKey(
                    eventId,
                    payloadOrder,
                    formatName,
                    isExternalReference));
                byFormat[formatName] = byFormat.GetValueOrDefault(formatName) + 1;
                if (isExternalReference)
                {
                    externalReferences++;
                }
            }
        }

        CloseEvent();
        token.ThrowIfCancellationRequested();
        return new ClipboardHistoryPurgePlan(
            representations,
            records,
            new ClipboardHistoryPurgeSummary(
                representations.Count,
                externalReferences,
                records.Count,
                trimmedRecords,
                byFormat));
    }

    public static void ApplyInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ClipboardHistoryPurgePlan plan,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(plan);

        foreach (ClipboardHistoryPurgePlan.PayloadKey key in plan.Representations)
        {
            token.ThrowIfCancellationRequested();
            using SqliteCommand delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM ClipboardHistoryPayload
                WHERE EventId = $eventId COLLATE BINARY
                  AND PayloadOrder = $payloadOrder;
                """;
            delete.Parameters.AddWithValue("$eventId", key.EventId);
            delete.Parameters.AddWithValue("$payloadOrder", key.PayloadOrder);
            if (delete.ExecuteNonQuery() != 1)
            {
                throw new InvalidDataException("A clipboard representation changed during the history purge.");
            }
        }

        foreach (string eventId in plan.Records)
        {
            token.ThrowIfCancellationRequested();
            using SqliteCommand delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM ClipboardHistoryEvent
                WHERE EventId = $eventId COLLATE BINARY
                  AND NOT EXISTS (
                      SELECT 1 FROM ClipboardHistoryPayload
                      WHERE ClipboardHistoryPayload.EventId = $eventId COLLATE BINARY);
                """;
            delete.Parameters.AddWithValue("$eventId", eventId);
            if (delete.ExecuteNonQuery() != 1)
            {
                throw new InvalidDataException("A clipboard record changed during the history purge.");
            }
        }
    }

    /// <summary>
    /// Makes deleted content overwritten in the database file rather than left in free pages.
    /// </summary>
    public static void EnableSecureDelete(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA secure_delete = ON;";
        command.ExecuteNonQuery();
        using SqliteCommand verify = connection.CreateCommand();
        verify.CommandText = "PRAGMA secure_delete;";
        if (Convert.ToInt64(verify.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidOperationException("SQLite secure_delete could not be enabled for the history purge.");
        }
    }

    private static string AddSourceParameters(
        SqliteCommand command,
        IReadOnlyCollection<string> sourceApplicationIds)
    {
        var names = new List<string>(sourceApplicationIds.Count);
        int index = 0;
        foreach (string sourceApplicationId in sourceApplicationIds)
        {
            string name = "$source" + index.ToString(CultureInfo.InvariantCulture);
            command.Parameters.AddWithValue(name, ReadCanonicalGuid(sourceApplicationId));
            names.Add(name);
            index++;
        }
        return string.Join(", ", names);
    }

    private static string ReadCanonicalGuid(string value)
    {
        if (!Guid.TryParseExact(value, "D", out Guid parsed) ||
            parsed == Guid.Empty ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Clipboard history purge met a non-canonical identifier.");
        }
        return value;
    }

    private static bool IsKnownPayloadKind(string value) => value is
        "Text" or "Link" or "PngImage" or "CustomBinary" or "StorageItems";
}
