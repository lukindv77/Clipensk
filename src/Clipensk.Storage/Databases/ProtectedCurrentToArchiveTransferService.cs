using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

public sealed record CurrentToArchiveTransferResult(
    int CopiedEventCount,
    int PurgedEventCount);

/// <summary>
/// Moves complete closed calendar-day ranges from Current into an existing Archive v1 database.
/// Archive publication is committed and verified before Current rows are purged, so retry after
/// interruption is idempotent and never requires deleting the only durable copy first.
/// </summary>
public sealed class ProtectedCurrentToArchiveTransferService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;

    public ProtectedCurrentToArchiveTransferService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
    }

    public async Task<CurrentToArchiveTransferResult> TransferAsync(
        ArchiveFileName archiveFileName,
        JournalDateRange transferRange,
        CancellationToken cancellationToken = default)
    {
        ValidateArchiveFileNameValue(archiveFileName);
        DateOnly currentLocalDate = DateOnly.FromDateTime(DateTime.Now);
        if (transferRange.EndDate >= currentLocalDate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transferRange),
                "Current-to-Archive transfer accepts only completed calendar days.");
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        // Serialize the complete transfer publication boundary with capture persistence,
        // policy maintenance and other protected storage mutations. The lease is acquired
        // before any Archive/Current observation so a cleanup operation cannot interleave
        // between the transfer snapshot, Archive publication and exact Current purge.
        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var archiveService = new ProtectedArchiveDatabaseService(_session, _connectionFactory);
        DatabaseIdentity archiveIdentity = await ValidateArchiveSetAsync(
                archiveService,
                archiveFileName,
                token)
            .ConfigureAwait(false);
        EnsureTransferRangeInsideArchiveCoverage(transferRange, archiveIdentity);

        return await Task.Run(
            () => TransferCore(
                archiveFileName,
                transferRange,
                archiveService,
                token),
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<DatabaseIdentity> ValidateArchiveSetAsync(
        ProtectedArchiveDatabaseService archiveService,
        ArchiveFileName targetArchiveFileName,
        CancellationToken cancellationToken)
    {
        string archiveDirectory = Path.Combine(Path.GetFullPath(_session.DataRootPath), "Archive");
        if (!Directory.Exists(archiveDirectory))
        {
            throw new DirectoryNotFoundException("Archive directory was not found.");
        }

        var descriptors = new List<ArchiveSegmentDescriptor>();
        DatabaseIdentity? targetIdentity = null;
        foreach (string archivePath in Directory.EnumerateFiles(
                     archiveDirectory,
                     "archive_*.db",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string persistedFileName = Path.GetFileName(archivePath);
            if (!ArchiveFileName.TryParse(persistedFileName, out ArchiveFileName parsedFileName) ||
                !string.Equals(
                    persistedFileName,
                    parsedFileName.FileName,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Archive file '{persistedFileName}' does not use a canonical Clipensk name.");
            }

            DatabaseIdentity identity = await archiveService
                .ValidateAsync(parsedFileName, cancellationToken)
                .ConfigureAwait(false);
            if (identity.CoverageStartDate is not DateOnly coverageStart ||
                identity.CoverageEndDate is not DateOnly coverageEnd)
            {
                throw new InvalidDataException(
                    $"Archive '{persistedFileName}' does not have assigned coverage.");
            }

            descriptors.Add(new ArchiveSegmentDescriptor(
                identity.DatabaseId,
                parsedFileName.FileName,
                new JournalDateRange(coverageStart, coverageEnd),
                IsSealed: false));

            if (parsedFileName == targetArchiveFileName)
            {
                targetIdentity = identity;
            }
        }

        if (targetIdentity is null)
        {
            throw new FileNotFoundException(
                "Target Archive database was not found.",
                Path.Combine(archiveDirectory, targetArchiveFileName.FileName));
        }

        StorageQueryPlanner.ValidateArchiveCoverage(descriptors);
        cancellationToken.ThrowIfCancellationRequested();
        return targetIdentity;
    }

    private CurrentToArchiveTransferResult TransferCore(
        ArchiveFileName archiveFileName,
        JournalDateRange transferRange,
        ProtectedArchiveDatabaseService archiveService,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CurrentTransferBatch batch = ReadCurrentBatch(transferRange, cancellationToken);
        if (batch.Events.Count == 0)
        {
            return new CurrentToArchiveTransferResult(0, 0);
        }

        CopyBatchToArchive(archiveFileName, batch, cancellationToken);

        // ReadOnly verification happens after the Archive commit and before Current is touched.
        // Cancellation or corruption here leaves Current intact and a retry can compare/reuse
        // the already copied Archive rows.
        _ = archiveService.ValidateAsync(archiveFileName, cancellationToken)
            .GetAwaiter()
            .GetResult();

        int purged = PurgeExactCurrentBatch(transferRange, batch, cancellationToken);

        // Do not check cancellation after the Current commit. Once both durable phases completed,
        // a late cancellation must not turn success into a reported failure.
        return new CurrentToArchiveTransferResult(batch.Events.Count, purged);
    }

    private CurrentTransferBatch ReadCurrentBatch(
        JournalDateRange transferRange,
        CancellationToken cancellationToken)
    {
        string currentPath = Path.Combine(
            Path.GetFullPath(_session.DataRootPath),
            "Current",
            "current.db");

        using SqliteConnection connection = _connectionFactory.Open(
            currentPath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadOnly);
        EnableForeignKeys(connection);
        ValidateCurrentDatabase(connection, cancellationToken);
        return ReadCurrentBatch(connection, transaction: null, transferRange, cancellationToken);
    }

    private CurrentTransferBatch ReadCurrentBatch(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        JournalDateRange transferRange,
        CancellationToken cancellationToken)
    {
        string start = transferRange.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string end = transferRange.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var events = new List<TransferEventRow>();

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                       SourceApplicationId, SourceProcessId, SourceExecutablePath,
                       SourceApplicationUserModelId
                FROM ClipboardHistoryEvent
                WHERE CalendarDate >= $start AND CalendarDate <= $end
                ORDER BY EventUtc, EventId COLLATE BINARY;
                """;
            command.Parameters.AddWithValue("$start", start);
            command.Parameters.AddWithValue("$end", end);

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                events.Add(new TransferEventRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    []));
            }
        }

        for (int eventIndex = 0; eventIndex < events.Count; eventIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransferEventRow current = events[eventIndex];
            List<TransferPayloadRow> payloads = ReadPayloads(
                connection,
                transaction,
                current.EventId,
                cancellationToken);
            events[eventIndex] = current with { Payloads = payloads };
        }

        var applicationCreatedAt = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string applicationId in events
                     .Select(item => item.SourceApplicationId)
                     .Where(item => item is not null)
                     .Cast<string>()
                     .Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SqliteCommand identity = connection.CreateCommand();
            identity.Transaction = transaction;
            identity.CommandText = """
                SELECT CreatedAtUtc
                FROM ApplicationIdentity
                WHERE ApplicationId = $applicationId COLLATE BINARY;
                """;
            identity.Parameters.AddWithValue("$applicationId", applicationId);
            object? createdAt = identity.ExecuteScalar();
            if (createdAt is not string createdAtUtc)
            {
                throw new InvalidDataException(
                    $"Current source application '{applicationId}' is missing.");
            }
            applicationCreatedAt.Add(applicationId, createdAtUtc);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new CurrentTransferBatch(events, applicationCreatedAt);
    }

    private static List<TransferPayloadRow> ReadPayloads(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string eventId,
        CancellationToken cancellationToken)
    {
        var payloads = new List<TransferPayloadRow>();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                   InlineCanonicalText, SearchText, ExternalSha256,
                   ExternalRelativePath, ExternalSizeBytes
            FROM ClipboardHistoryPayload
            WHERE EventId = $eventId COLLATE BINARY
            ORDER BY PayloadOrder;
            """;
        command.Parameters.AddWithValue("$eventId", eventId);

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            payloads.Add(new TransferPayloadRow(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8)));
        }
        return payloads;
    }

    private void CopyBatchToArchive(
        ArchiveFileName archiveFileName,
        CurrentTransferBatch batch,
        CancellationToken cancellationToken)
    {
        string archivePath = Path.Combine(
            Path.GetFullPath(_session.DataRootPath),
            "Archive",
            archiveFileName.FileName);

        using SqliteConnection connection = _connectionFactory.Open(
            archivePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteTransaction transaction = connection.BeginTransaction();
        foreach ((string applicationId, string createdAtUtc) in batch.ApplicationCreatedAt)
        {
            EnsureArchiveApplicationIdentity(
                connection,
                transaction,
                applicationId,
                createdAtUtc);
        }

        foreach (TransferEventRow item in batch.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadArchiveEvent(connection, transaction, item.EventId, cancellationToken) is { } existing)
            {
                if (!EventRowsEqual(existing, item))
                {
                    throw new InvalidDataException(
                        $"Archive event '{item.EventId}' conflicts with Current.");
                }
                continue;
            }

            InsertArchiveEvent(connection, transaction, item);
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private static void EnsureArchiveApplicationIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string applicationId,
        string createdAtUtc)
    {
        using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT CreatedAtUtc
                FROM ApplicationIdentity
                WHERE ApplicationId = $applicationId COLLATE BINARY;
                """;
            read.Parameters.AddWithValue("$applicationId", applicationId);
            object? existing = read.ExecuteScalar();
            if (existing is string existingCreatedAt)
            {
                if (!string.Equals(existingCreatedAt, createdAtUtc, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Archive application identity '{applicationId}' conflicts with Current.");
                }
                return;
            }
            if (existing is not null)
            {
                throw new InvalidDataException(
                    $"Archive application identity '{applicationId}' is malformed.");
            }
        }

        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
            VALUES ($applicationId, $createdAtUtc);
            """;
        insert.Parameters.AddWithValue("$applicationId", applicationId);
        insert.Parameters.AddWithValue("$createdAtUtc", createdAtUtc);
        insert.ExecuteNonQuery();
    }

    private static TransferEventRow? TryReadArchiveEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string eventId,
        CancellationToken cancellationToken)
    {
        TransferEventRow? result = null;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                       SourceApplicationId, SourceProcessId, SourceExecutablePath,
                       SourceApplicationUserModelId
                FROM ClipboardHistoryEvent
                WHERE EventId = $eventId COLLATE BINARY;
                """;
            command.Parameters.AddWithValue("$eventId", eventId);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }
            result = new TransferEventRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                []);
            if (reader.Read())
            {
                throw new InvalidDataException(
                    $"Archive event '{eventId}' is duplicated.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result! with
        {
            Payloads = ReadPayloads(connection, transaction, eventId, cancellationToken),
        };
    }

    private static void InsertArchiveEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransferEventRow item)
    {
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ClipboardHistoryEvent (
                    EventId, EventUtc, LocalOffsetMinutes, WindowsTimeZoneId, CalendarDate,
                    SourceApplicationId, SourceProcessId, SourceExecutablePath,
                    SourceApplicationUserModelId)
                VALUES (
                    $eventId, $eventUtc, $localOffsetMinutes, $windowsTimeZoneId, $calendarDate,
                    $sourceApplicationId, $sourceProcessId, $sourceExecutablePath,
                    $sourceApplicationUserModelId);
                """;
            insert.Parameters.AddWithValue("$eventId", item.EventId);
            insert.Parameters.AddWithValue("$eventUtc", item.EventUtc);
            insert.Parameters.AddWithValue("$localOffsetMinutes", item.LocalOffsetMinutes);
            insert.Parameters.AddWithValue("$windowsTimeZoneId", item.WindowsTimeZoneId);
            insert.Parameters.AddWithValue("$calendarDate", item.CalendarDate);
            insert.Parameters.AddWithValue(
                "$sourceApplicationId",
                (object?)item.SourceApplicationId ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "$sourceProcessId",
                (object?)item.SourceProcessId ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "$sourceExecutablePath",
                (object?)item.SourceExecutablePath ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "$sourceApplicationUserModelId",
                (object?)item.SourceApplicationUserModelId ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        foreach (TransferPayloadRow payload in item.Payloads)
        {
            using SqliteCommand insertPayload = connection.CreateCommand();
            insertPayload.Transaction = transaction;
            insertPayload.CommandText = """
                INSERT INTO ClipboardHistoryPayload (
                    EventId, PayloadOrder, FormatName, PayloadKind, CanonicalByteCount,
                    InlineCanonicalText, SearchText, ExternalSha256,
                    ExternalRelativePath, ExternalSizeBytes)
                VALUES (
                    $eventId, $payloadOrder, $formatName, $payloadKind, $canonicalByteCount,
                    $inlineCanonicalText, $searchText, $externalSha256,
                    $externalRelativePath, $externalSizeBytes);
                """;
            insertPayload.Parameters.AddWithValue("$eventId", item.EventId);
            insertPayload.Parameters.AddWithValue("$payloadOrder", payload.PayloadOrder);
            insertPayload.Parameters.AddWithValue("$formatName", payload.FormatName);
            insertPayload.Parameters.AddWithValue("$payloadKind", payload.PayloadKind);
            insertPayload.Parameters.AddWithValue("$canonicalByteCount", payload.CanonicalByteCount);
            insertPayload.Parameters.AddWithValue(
                "$inlineCanonicalText",
                (object?)payload.InlineCanonicalText ?? DBNull.Value);
            insertPayload.Parameters.AddWithValue(
                "$searchText",
                (object?)payload.SearchText ?? DBNull.Value);
            insertPayload.Parameters.AddWithValue(
                "$externalSha256",
                (object?)payload.ExternalSha256 ?? DBNull.Value);
            insertPayload.Parameters.AddWithValue(
                "$externalRelativePath",
                (object?)payload.ExternalRelativePath ?? DBNull.Value);
            insertPayload.Parameters.AddWithValue(
                "$externalSizeBytes",
                (object?)payload.ExternalSizeBytes ?? DBNull.Value);
            insertPayload.ExecuteNonQuery();
        }
    }

    private int PurgeExactCurrentBatch(
        JournalDateRange transferRange,
        CurrentTransferBatch expectedBatch,
        CancellationToken cancellationToken)
    {
        string currentPath = Path.Combine(
            Path.GetFullPath(_session.DataRootPath),
            "Current",
            "current.db");

        using SqliteConnection connection = _connectionFactory.Open(
            currentPath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);
        ValidateCurrentDatabase(connection, cancellationToken);

        using SqliteTransaction transaction = connection.BeginTransaction();
        CurrentTransferBatch currentBatch = ReadCurrentBatch(
            connection,
            transaction,
            transferRange,
            cancellationToken);
        if (!BatchesEqual(expectedBatch, currentBatch))
        {
            throw new InvalidOperationException(
                "Current history changed during Archive transfer. Retry the transfer.");
        }

        string start = transferRange.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string end = transferRange.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        using SqliteCommand delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM ClipboardHistoryEvent
            WHERE CalendarDate >= $start AND CalendarDate <= $end;
            """;
        delete.Parameters.AddWithValue("$start", start);
        delete.Parameters.AddWithValue("$end", end);
        int deleted = delete.ExecuteNonQuery();
        if (deleted != expectedBatch.Events.Count)
        {
            throw new InvalidDataException(
                "Current purge count does not match the verified transfer batch.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return deleted;
    }

    private void ValidateCurrentDatabase(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using (SqliteCommand probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT count(*) FROM sqlite_master;";
            _ = probe.ExecuteScalar();
        }

        using (SqliteCommand quickCheck = connection.CreateCommand())
        {
            quickCheck.CommandText = "PRAGMA quick_check;";
            if (quickCheck.ExecuteScalar() is not string result ||
                !string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Current SQLite quick_check failed before Archive transfer.");
            }
        }

        using (SqliteCommand identity = connection.CreateCommand())
        {
            identity.CommandText = """
                SELECT StorageId, DatabaseRole, SchemaVersion, EncryptionVersion
                FROM DatabaseIdentity
                WHERE SingletonId = 1;
                """;
            using SqliteDataReader reader = identity.ExecuteReader();
            if (!reader.Read() ||
                !Guid.TryParse(reader.GetString(0), out Guid storageId) ||
                storageId != _session.StorageId ||
                !Enum.TryParse(reader.GetString(1), ignoreCase: false, out DatabaseRole role) ||
                role != DatabaseRole.Current ||
                reader.GetInt32(2) != ProtectedStorageDatabaseService.CurrentSchemaVersion ||
                reader.GetInt32(3) != ProtectedStorageDatabaseService.CurrentEncryptionVersion ||
                reader.Read())
            {
                throw new InvalidDataException("Current DatabaseIdentity is invalid for Archive transfer.");
            }
        }

        using (SqliteCommand version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture) !=
                ProtectedStorageDatabaseService.CurrentSchemaVersion)
            {
                throw new InvalidDataException("Current user_version is invalid for Archive transfer.");
            }
        }

        ApplicationIdentitySqlSchema.ValidateTables(connection);
        ClipboardHistorySqlSchema.ValidateTables(connection);

        using (SqliteCommand foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            using SqliteDataReader reader = foreignKeys.ExecuteReader();
            if (reader.Read())
            {
                throw new InvalidDataException("Current contains invalid foreign-key references.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool BatchesEqual(CurrentTransferBatch left, CurrentTransferBatch right)
    {
        if (left.Events.Count != right.Events.Count ||
            left.ApplicationCreatedAt.Count != right.ApplicationCreatedAt.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Events.Count; index++)
        {
            if (!EventRowsEqual(left.Events[index], right.Events[index]))
            {
                return false;
            }
        }

        foreach ((string applicationId, string createdAtUtc) in left.ApplicationCreatedAt)
        {
            if (!right.ApplicationCreatedAt.TryGetValue(applicationId, out string? rightCreatedAt) ||
                !string.Equals(createdAtUtc, rightCreatedAt, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static bool EventRowsEqual(TransferEventRow left, TransferEventRow right) =>
        string.Equals(left.EventId, right.EventId, StringComparison.Ordinal) &&
        string.Equals(left.EventUtc, right.EventUtc, StringComparison.Ordinal) &&
        left.LocalOffsetMinutes == right.LocalOffsetMinutes &&
        string.Equals(left.WindowsTimeZoneId, right.WindowsTimeZoneId, StringComparison.Ordinal) &&
        string.Equals(left.CalendarDate, right.CalendarDate, StringComparison.Ordinal) &&
        string.Equals(left.SourceApplicationId, right.SourceApplicationId, StringComparison.Ordinal) &&
        left.SourceProcessId == right.SourceProcessId &&
        string.Equals(left.SourceExecutablePath, right.SourceExecutablePath, StringComparison.Ordinal) &&
        string.Equals(
            left.SourceApplicationUserModelId,
            right.SourceApplicationUserModelId,
            StringComparison.Ordinal) &&
        left.Payloads.SequenceEqual(right.Payloads);

    private static void EnsureTransferRangeInsideArchiveCoverage(
        JournalDateRange transferRange,
        DatabaseIdentity archiveIdentity)
    {
        if (archiveIdentity.CoverageStartDate is not DateOnly coverageStart ||
            archiveIdentity.CoverageEndDate is not DateOnly coverageEnd ||
            transferRange.StartDate < coverageStart ||
            transferRange.EndDate > coverageEnd)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transferRange),
                "Transfer range must be fully inside the Archive assigned coverage.");
        }
    }

    private static void ValidateArchiveFileNameValue(ArchiveFileName archiveFileName)
    {
        if (!ArchiveFileName.TryParse(archiveFileName.FileName, out ArchiveFileName parsed) ||
            parsed != archiveFileName)
        {
            throw new ArgumentOutOfRangeException(
                nameof(archiveFileName),
                "Archive file name value is outside the canonical Clipensk range.");
        }
    }

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }

    private sealed record CurrentTransferBatch(
        IReadOnlyList<TransferEventRow> Events,
        IReadOnlyDictionary<string, string> ApplicationCreatedAt);

    private sealed record TransferEventRow(
        string EventId,
        string EventUtc,
        int LocalOffsetMinutes,
        string WindowsTimeZoneId,
        string CalendarDate,
        string? SourceApplicationId,
        int? SourceProcessId,
        string? SourceExecutablePath,
        string? SourceApplicationUserModelId,
        IReadOnlyList<TransferPayloadRow> Payloads);

    private sealed record TransferPayloadRow(
        int PayloadOrder,
        string FormatName,
        string PayloadKind,
        long CanonicalByteCount,
        string? InlineCanonicalText,
        string? SearchText,
        string? ExternalSha256,
        string? ExternalRelativePath,
        long? ExternalSizeBytes);
}
