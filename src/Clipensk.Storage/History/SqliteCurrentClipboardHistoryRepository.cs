using System.Globalization;
using Clipensk.Core.Clipboard;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.History;

public sealed class SqliteCurrentClipboardHistoryRepository : ICurrentClipboardHistoryRepository
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;
    private readonly string _filesRootPath;

    public SqliteCurrentClipboardHistoryRepository(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        string root = Path.GetFullPath(session.DataRootPath);
        _currentDatabasePath = Path.Combine(root, "Current", "current.db");
        _filesRootPath = Path.Combine(root, "Files") + Path.DirectorySeparatorChar;
    }

    public ValueTask<IReadOnlyList<ClipboardHistoryEntry>> ReadAsync(
        JournalDateRange period,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return ReadCore(period, limit, before: null, cancellationToken);
    }

    public ValueTask<IReadOnlyList<ClipboardHistoryEntry>> ReadBeforeAsync(
        JournalDateRange period,
        int limit,
        ClipboardHistoryCursor before,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(before);
        if (before.Period != period)
        {
            throw new ArgumentException("History cursor belongs to a different calendar period.", nameof(before));
        }

        return ReadCore(period, limit, before, cancellationToken);
    }

    private ValueTask<IReadOnlyList<ClipboardHistoryEntry>> ReadCore(
        JournalDateRange period,
        int limit,
        ClipboardHistoryCursor? before,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCurrent(token);
        using SqliteCommand command = connection.CreateCommand();
        // Limit events BEFORE joining their payloads. A single SELECT keeps event
        // envelopes and all their payloads in the same SQLite read snapshot.
        command.CommandText = """
            WITH SelectedEvents AS (
                SELECT EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId,
                       CalendarDate, SourceApplicationId, SourceProcessId,
                       SourceExecutablePath, SourceApplicationUserModelId
                FROM ClipboardHistoryEvent
                WHERE CalendarDate >= $startDate AND CalendarDate <= $endDate
                  AND ($beforeUtc IS NULL
                       OR EventUtc < $beforeUtc
                       OR (EventUtc = $beforeUtc AND EventId COLLATE BINARY < $beforeId))
                ORDER BY EventUtc DESC, EventId COLLATE BINARY DESC
                LIMIT $limit
            )
            SELECT e.EventId, e.EventUtc, e.LocalOffsetMinutes, e.WindowsTimeZoneId,
                   e.CalendarDate, e.SourceApplicationId, e.SourceProcessId,
                   e.SourceExecutablePath, e.SourceApplicationUserModelId,
                   p.PayloadOrder, p.FormatName, p.PayloadKind, p.CanonicalByteCount,
                   p.InlineCanonicalText, p.SearchText, p.ExternalSha256,
                   p.ExternalRelativePath, p.ExternalSizeBytes
            FROM SelectedEvents e
            LEFT JOIN ClipboardHistoryPayload p ON p.EventId = e.EventId
            ORDER BY e.EventUtc DESC, e.EventId COLLATE BINARY DESC, p.PayloadOrder ASC;
            """;
        command.Parameters.AddWithValue("$startDate", period.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$endDate", period.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$beforeUtc", before is null
            ? DBNull.Value
            : before.UtcTimestamp.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$beforeId", before is null
            ? DBNull.Value
            : before.EventId.ToString("D"));

        var entries = new List<ClipboardHistoryEntry>();
        ClipboardHistoryEntry? current = null;
        List<ClipboardHistoryPayload>? payloads = null;
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                string storedEventId = reader.GetString(0);
                Guid eventId = ReadNonEmptyGuid(storedEventId, "EventId");
                if (!string.Equals(storedEventId, eventId.ToString("D"), StringComparison.Ordinal))
                {
                    throw new InvalidDataException("History EventId must retain the canonical lowercase form used by the sink and cursor.");
                }
                if (current is null || current.EventId != eventId)
                {
                    if (current is not null)
                    {
                        entries.Add(current);
                    }

                    payloads = new List<ClipboardHistoryPayload>();
                    current = ReadEvent(reader, eventId, payloads.AsReadOnly());
                }

                if (!reader.IsDBNull(9))
                {
                    ClipboardHistoryPayload payload = ReadPayload(reader);
                    if (payload.PayloadOrder != payloads!.Count)
                    {
                        throw new InvalidDataException("History payload order must be contiguous and start at zero.");
                    }
                    payloads.Add(payload);
                }
            }
        }

        if (current is not null)
        {
            entries.Add(current);
        }

        // Reads have no durable COMMIT: revoked access must not return a partial
        // result. The caller owns the returned snapshot and its UI/cache lifetime.
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<ClipboardHistoryEntry>>(entries.AsReadOnly());
    }

    private SqliteConnection OpenValidatedCurrent(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }

        SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadOnly);
        try
        {
            token.ThrowIfCancellationRequested();
            using (SqliteCommand identity = connection.CreateCommand())
            {
                identity.CommandText = """
                    SELECT StorageId, DatabaseRole, SchemaVersion
                    FROM DatabaseIdentity
                    WHERE SingletonId = 1;
                    """;
                int schemaVersion;
                using (SqliteDataReader reader = identity.ExecuteReader())
                {
                    if (!reader.Read() ||
                        !Guid.TryParse(reader.GetString(0), out Guid storageId) ||
                        storageId != _session.StorageId ||
                        reader.GetString(1) != DatabaseRole.Current.ToString() ||
                        reader.GetInt32(2) < ClipboardHistorySqlSchema.RequiredCurrentSchemaVersion)
                    {
                        throw new InvalidDataException("History reads require the expected Current database at schema v4 or later.");
                    }
                    schemaVersion = reader.GetInt32(2);
                }

                using SqliteCommand userVersion = connection.CreateCommand();
                userVersion.CommandText = "PRAGMA user_version;";
                if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
                {
                    throw new InvalidDataException("Current user_version does not match DatabaseIdentity for history reads.");
                }
            }

            ClipboardHistorySqlSchema.ValidateTables(connection);
            token.ThrowIfCancellationRequested();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static ClipboardHistoryEntry ReadEvent(
        SqliteDataReader reader,
        Guid eventId,
        IReadOnlyList<ClipboardHistoryPayload> payloads)
    {
        if (!DateTime.TryParseExact(reader.GetString(1), "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTime utc) || utc.Kind != DateTimeKind.Utc ||
            !DateOnly.TryParseExact(reader.GetString(4), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateOnly calendarDate))
        {
            throw new InvalidDataException("History event has invalid persisted time metadata.");
        }

        long offsetMinutes = reader.GetInt64(2);
        string timeZoneId = reader.GetString(3);
        if (offsetMinutes is < -840 or > 840 || string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new InvalidDataException("History event has invalid offset or time-zone metadata.");
        }

        // Use the stored offset and zone ID, never the current Windows time zone.
        var eventTime = new EventTimeContext(
            new DateTimeOffset(utc).ToOffset(TimeSpan.FromMinutes(offsetMinutes)), timeZoneId);
        if (eventTime.CalendarDate != calendarDate)
        {
            throw new InvalidDataException("History event calendar date disagrees with its persisted timestamp and offset.");
        }

        DurableApplicationId? applicationId = reader.IsDBNull(5)
            ? null
            : new DurableApplicationId(ReadNonEmptyGuid(reader.GetString(5), "SourceApplicationId"));
        ClipboardSourceApplication? source = null;
        string? executablePath = ReadNullableString(reader, 7);
        string? aumid = ReadNullableString(reader, 8);
        if (!reader.IsDBNull(6))
        {
            long processId = reader.GetInt64(6);
            if (processId is < 0 or > uint.MaxValue)
            {
                throw new InvalidDataException("History source process ID is invalid.");
            }
            source = new ClipboardSourceApplication((uint)processId, executablePath, aumid);
        }
        else if (executablePath is not null || aumid is not null)
        {
            throw new InvalidDataException("History source snapshot is missing its process ID.");
        }

        return new ClipboardHistoryEntry(eventId, eventTime, applicationId, source, payloads);
    }

    private ClipboardHistoryPayload ReadPayload(SqliteDataReader reader)
    {
        int order = reader.GetInt32(9);
        string formatName = reader.GetString(10);
        ClipboardHistoryPayloadKind kind = reader.GetString(11) switch
        {
            "Text" => ClipboardHistoryPayloadKind.Text,
            "Link" => ClipboardHistoryPayloadKind.Link,
            "PngImage" => ClipboardHistoryPayloadKind.PngImage,
            "CustomBinary" => ClipboardHistoryPayloadKind.CustomBinary,
            "StorageItems" => ClipboardHistoryPayloadKind.StorageItems,
            _ => throw new InvalidDataException("History contains an unsupported payload kind."),
        };
        long count = reader.GetInt64(12);
        string? inline = ReadNullableString(reader, 13);
        string? searchText = ReadNullableString(reader, 14);
        string? sha = ReadNullableString(reader, 15);
        string? path = ReadNullableString(reader, 16);
        long? size = reader.IsDBNull(17) ? null : reader.GetInt64(17);
        if (order < 0 || count < 0 || string.IsNullOrWhiteSpace(formatName))
        {
            throw new InvalidDataException("History contains invalid payload metadata.");
        }

        ClipboardHistoryExternalReference? external = null;
        if (kind is ClipboardHistoryPayloadKind.PngImage or ClipboardHistoryPayloadKind.CustomBinary)
        {
            if (inline is not null || searchText is not null || size != count ||
                sha is null || sha.Length != 64 || sha.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
                string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ||
                !Path.GetFullPath(Path.Combine(_filesRootPath, path)).StartsWith(_filesRootPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("History contains an invalid external payload reference.");
            }

            // Preserve the stored address verbatim, even if its file is missing.
            external = new ClipboardHistoryExternalReference(sha, path, size!.Value);
        }
        else if (inline is null || sha is not null || path is not null || size is not null ||
                 ClipboardCanonicalPayloadSize.MeasureUtf8Text(inline) != count)
        {
            throw new InvalidDataException("History inline representation does not match its canonical byte count.");
        }

        return new ClipboardHistoryPayload(order, formatName, kind, count, inline, searchText, external);
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static Guid ReadNonEmptyGuid(string value, string field)
    {
        if (!Guid.TryParseExact(value, "D", out Guid id) || id == Guid.Empty)
        {
            throw new InvalidDataException($"History {field} is invalid.");
        }
        return id;
    }
}
