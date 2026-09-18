using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.History;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

/// <summary>
/// Shared Archive v1 shadow construction and exact source→shadow comparison.
///
/// Archive Split builds shadows from an existing Archive database and Archive Rotation builds them
/// from Current, but the clipboard history contract (<c>ClipboardHistoryEvent</c>,
/// <c>ClipboardHistoryPayload</c>, <c>ApplicationIdentity</c>, <c>ApplicationIdentityAlias</c>) is
/// identical in Current v4+ and Archive v1. Both operations therefore share this writer instead of
/// carrying parallel copy/verify implementations that could drift apart.
///
/// External payload files are never copied or rewritten here: only history ownership of their
/// persisted references moves.
/// </summary>
internal static class ArchiveShadowWriter
{
    public static void CreateArchiveV1Schema(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid storageId,
        ArchiveFileName fileName,
        Guid databaseId,
        JournalDateRange coverage,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Archive database creation time must be UTC.",
                nameof(createdAtUtc));
        }

        using (SqliteCommand createIdentity = connection.CreateCommand())
        {
            createIdentity.Transaction = transaction;
            createIdentity.CommandText = """
                CREATE TABLE DatabaseIdentity (
                    SingletonId INTEGER NOT NULL PRIMARY KEY CHECK (SingletonId = 1),
                    StorageId TEXT NOT NULL,
                    DatabaseId TEXT NOT NULL UNIQUE,
                    DatabaseRole TEXT NOT NULL,
                    SchemaVersion INTEGER NOT NULL,
                    EncryptionVersion INTEGER NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    ArchiveBaseNumber INTEGER NULL,
                    ArchiveSplitSequence INTEGER NULL,
                    CoverageStartDate TEXT NULL,
                    CoverageEndDate TEXT NULL
                );
                """;
            createIdentity.ExecuteNonQuery();
        }

        using (SqliteCommand insertIdentity = connection.CreateCommand())
        {
            insertIdentity.Transaction = transaction;
            insertIdentity.CommandText = """
                INSERT INTO DatabaseIdentity (
                    SingletonId,
                    StorageId,
                    DatabaseId,
                    DatabaseRole,
                    SchemaVersion,
                    EncryptionVersion,
                    CreatedAtUtc,
                    ArchiveBaseNumber,
                    ArchiveSplitSequence,
                    CoverageStartDate,
                    CoverageEndDate)
                VALUES (
                    1,
                    $storageId,
                    $databaseId,
                    $role,
                    $schemaVersion,
                    $encryptionVersion,
                    $createdAtUtc,
                    $archiveBaseNumber,
                    $archiveSplitSequence,
                    $coverageStartDate,
                    $coverageEndDate);
                """;
            insertIdentity.Parameters.AddWithValue("$storageId", storageId.ToString("D"));
            insertIdentity.Parameters.AddWithValue("$databaseId", databaseId.ToString("D"));
            insertIdentity.Parameters.AddWithValue("$role", DatabaseRole.Archive.ToString());
            insertIdentity.Parameters.AddWithValue(
                "$schemaVersion",
                ProtectedArchiveDatabaseService.ArchiveSchemaVersion);
            insertIdentity.Parameters.AddWithValue(
                "$encryptionVersion",
                ProtectedStorageDatabaseService.CurrentEncryptionVersion);
            insertIdentity.Parameters.AddWithValue(
                "$createdAtUtc",
                createdAtUtc.ToString("O", CultureInfo.InvariantCulture));
            insertIdentity.Parameters.AddWithValue("$archiveBaseNumber", fileName.BaseNumber);
            insertIdentity.Parameters.AddWithValue(
                "$archiveSplitSequence",
                fileName.SplitSequence == ArchiveFileName.NoSplit
                    ? DBNull.Value
                    : fileName.SplitSequence);
            insertIdentity.Parameters.AddWithValue(
                "$coverageStartDate",
                FormatDate(coverage.StartDate));
            insertIdentity.Parameters.AddWithValue(
                "$coverageEndDate",
                FormatDate(coverage.EndDate));
            insertIdentity.ExecuteNonQuery();
        }

        ApplicationIdentitySqlSchema.CreateTables(connection, transaction);
        ClipboardHistorySqlSchema.CreateTables(connection, transaction);

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.Transaction = transaction;
        userVersion.CommandText =
            $"PRAGMA user_version = {ProtectedArchiveDatabaseService.ArchiveSchemaVersion};";
        userVersion.ExecuteNonQuery();
    }

    /// <summary>
    /// Copies every history row whose persisted <c>CalendarDate</c> falls inside
    /// <paramref name="coverage"/>, together with the application identity rows the history foreign
    /// key contract requires.
    /// </summary>
    public static void CopyCoverage(
        SqliteConnection source,
        SqliteConnection target,
        SqliteTransaction transaction,
        JournalDateRange coverage,
        CancellationToken token,
        bool mergeApplicationRows = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(transaction);

        CopyApplicationIdentities(source, target, transaction, coverage, token, mergeApplicationRows);
        CopyApplicationAliases(source, target, transaction, coverage, token, mergeApplicationRows);
        CopyHistoryEvents(source, target, transaction, coverage, token);
        CopyHistoryPayloads(source, target, transaction, coverage, token);
    }

    /// <summary>
    /// Rewrites staging-only coverage while a rotation candidate shadow is still being extended
    /// day by day. Final assigned coverage becomes immutable once the durable marker is committed,
    /// so this must never be used on a published Archive.
    /// </summary>
    public static void UpdateStagingCoverage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JournalDateRange coverage)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE DatabaseIdentity
            SET CoverageStartDate = $coverageStart,
                CoverageEndDate = $coverageEnd
            WHERE SingletonId = 1;
            """;
        command.Parameters.AddWithValue("$coverageStart", FormatDate(coverage.StartDate));
        command.Parameters.AddWithValue("$coverageEnd", FormatDate(coverage.EndDate));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException("Staged Archive identity row is missing.");
        }
    }

    /// <summary>
    /// Exact source→shadow cross-check. <paramref name="operationLabel"/> names the operation in
    /// failure messages, for example <c>"Archive split"</c> or <c>"Archive rotation"</c>.
    /// </summary>
    public static void CompareCoverage(
        SqliteConnection source,
        SqliteConnection staged,
        JournalDateRange coverage,
        string operationLabel,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationLabel);

        CompareApplicationIdentities(source, staged, coverage, operationLabel, token);
        CompareApplicationAliases(source, staged, coverage, operationLabel, token);
        CompareHistoryEvents(source, staged, coverage, operationLabel, token);
        CompareHistoryPayloads(source, staged, coverage, operationLabel, token);
    }

    /// <summary>
    /// Full connection-level validation of a staged or final Archive v1 database against the
    /// planned identity. The caller owns path/filename checks and opening the connection.
    /// </summary>
    public static DatabaseIdentity ValidateShadowDatabase(
        SqliteConnection connection,
        Guid expectedStorageId,
        ArchiveFileName expectedFileName,
        Guid expectedDatabaseId,
        JournalDateRange expectedCoverage,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(connection);
        token.ThrowIfCancellationRequested();

        using (SqliteCommand keyProbe = connection.CreateCommand())
        {
            keyProbe.CommandText = "SELECT count(*) FROM sqlite_master;";
            _ = keyProbe.ExecuteScalar();
        }

        using (SqliteCommand quickCheck = connection.CreateCommand())
        {
            quickCheck.CommandText = "PRAGMA quick_check;";
            if (quickCheck.ExecuteScalar() is not string result ||
                !string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Archive SQLite quick_check failed.");
            }
        }

        ValidateDatabaseIdentityColumns(connection);

        using (SqliteCommand count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM DatabaseIdentity;";
            if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidDataException(
                    "Archive DatabaseIdentity must contain exactly one row.");
            }
        }

        DatabaseIdentity identity = ReadAndValidateIdentity(
            connection,
            expectedStorageId,
            expectedFileName,
            expectedDatabaseId,
            expectedCoverage);

        using (SqliteCommand userVersion = connection.CreateCommand())
        {
            userVersion.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) !=
                ProtectedArchiveDatabaseService.ArchiveSchemaVersion)
            {
                throw new InvalidDataException(
                    "Archive user_version does not match schema version.");
            }
        }

        ApplicationIdentitySqlSchema.ValidateTables(connection);
        ClipboardHistorySqlSchema.ValidateTables(connection);
        ValidateForeignKeys(connection);
        ValidateHistoryCoverage(connection, identity, token);

        token.ThrowIfCancellationRequested();
        return identity;
    }

    public static bool IsCanonicalArchiveFileName(ArchiveFileName fileName) =>
        ArchiveFileName.TryParse(fileName.FileName, out ArchiveFileName parsed) &&
        parsed == fileName &&
        string.Equals(parsed.FileName, fileName.FileName, StringComparison.Ordinal);

    private static DatabaseIdentity ReadAndValidateIdentity(
        SqliteConnection connection,
        Guid expectedStorageId,
        ArchiveFileName expectedFileName,
        Guid expectedDatabaseId,
        JournalDateRange expectedCoverage)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT StorageId,
                   DatabaseId,
                   DatabaseRole,
                   SchemaVersion,
                   EncryptionVersion,
                   CreatedAtUtc,
                   ArchiveBaseNumber,
                   ArchiveSplitSequence,
                   CoverageStartDate,
                   CoverageEndDate
            FROM DatabaseIdentity
            WHERE SingletonId = 1;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException("Archive DatabaseIdentity is missing.");
        }

        if (!Guid.TryParse(reader.GetString(0), out Guid storageId) ||
            storageId != expectedStorageId ||
            !Guid.TryParse(reader.GetString(1), out Guid databaseId) ||
            databaseId != expectedDatabaseId ||
            !Enum.TryParse(reader.GetString(2), ignoreCase: false, out DatabaseRole role) ||
            role != DatabaseRole.Archive ||
            reader.GetInt32(3) != ProtectedArchiveDatabaseService.ArchiveSchemaVersion ||
            reader.GetInt32(4) != ProtectedStorageDatabaseService.CurrentEncryptionVersion ||
            !DateTimeOffset.TryParse(
                reader.GetString(5),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset createdAtUtc) ||
            createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "Archive DatabaseIdentity does not match the planned identity.");
        }

        if (reader.IsDBNull(6) || reader.GetInt32(6) != expectedFileName.BaseNumber)
        {
            throw new InvalidDataException(
                "Archive base number does not match the planned file name.");
        }

        int? splitSequence = reader.IsDBNull(7) ? null : reader.GetInt32(7);
        if (expectedFileName.SplitSequence == ArchiveFileName.NoSplit)
        {
            if (splitSequence is not null)
            {
                throw new InvalidDataException(
                    "Unsplit archive must store a null split sequence.");
            }
        }
        else if (splitSequence != expectedFileName.SplitSequence || splitSequence <= 0)
        {
            throw new InvalidDataException(
                "Archive split sequence does not match the planned file name.");
        }

        if (reader.IsDBNull(8) ||
            reader.IsDBNull(9) ||
            !DateOnly.TryParseExact(
                reader.GetString(8),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly coverageStart) ||
            !DateOnly.TryParseExact(
                reader.GetString(9),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly coverageEnd) ||
            coverageEnd < coverageStart)
        {
            throw new InvalidDataException("Archive coverage is invalid.");
        }

        var coverage = new JournalDateRange(coverageStart, coverageEnd);
        if (coverage != expectedCoverage)
        {
            throw new InvalidDataException("Archive coverage does not match the planned range.");
        }

        return new DatabaseIdentity(
            storageId,
            databaseId,
            DatabaseRole.Archive,
            ProtectedArchiveDatabaseService.ArchiveSchemaVersion,
            ProtectedStorageDatabaseService.CurrentEncryptionVersion,
            createdAtUtc,
            expectedFileName.BaseNumber,
            splitSequence,
            coverageStart,
            coverageEnd);
    }

    private static void ValidateDatabaseIdentityColumns(SqliteConnection connection)
    {
        ExpectedIdentityColumn[] expected =
        [
            new("SingletonId", "INTEGER", NotNull: true, PrimaryKeyOrder: 1),
            new("StorageId", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
            new("DatabaseId", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
            new("DatabaseRole", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
            new("SchemaVersion", "INTEGER", NotNull: true, PrimaryKeyOrder: 0),
            new("EncryptionVersion", "INTEGER", NotNull: true, PrimaryKeyOrder: 0),
            new("CreatedAtUtc", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
            new("ArchiveBaseNumber", "INTEGER", NotNull: false, PrimaryKeyOrder: 0),
            new("ArchiveSplitSequence", "INTEGER", NotNull: false, PrimaryKeyOrder: 0),
            new("CoverageStartDate", "TEXT", NotNull: false, PrimaryKeyOrder: 0),
            new("CoverageEndDate", "TEXT", NotNull: false, PrimaryKeyOrder: 0),
        ];

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('DatabaseIdentity');";
        using SqliteDataReader reader = command.ExecuteReader();

        int index = 0;
        while (reader.Read())
        {
            if (index >= expected.Length)
            {
                throw new InvalidDataException(
                    "Archive DatabaseIdentity contains unexpected columns.");
            }

            ExpectedIdentityColumn column = expected[index++];
            if (!string.Equals(reader.GetString(1), column.Name, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(2), column.Type, StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt32(3) != (column.NotNull ? 1 : 0) ||
                reader.GetInt32(5) != column.PrimaryKeyOrder)
            {
                throw new InvalidDataException(
                    "Archive DatabaseIdentity column contract is invalid.");
            }
        }

        if (index != expected.Length)
        {
            throw new InvalidDataException(
                "Archive DatabaseIdentity is missing required columns.");
        }
    }

    private static void ValidateForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using SqliteDataReader reader = command.ExecuteReader();
        if (reader.Read())
        {
            throw new InvalidDataException("Archive contains invalid foreign-key references.");
        }
    }

    private static void ValidateHistoryCoverage(
        SqliteConnection connection,
        DatabaseIdentity identity,
        CancellationToken token)
    {
        if (identity.CoverageStartDate is not DateOnly start ||
            identity.CoverageEndDate is not DateOnly end)
        {
            throw new InvalidDataException("Archive coverage is missing.");
        }

        var coverage = new JournalDateRange(start, end);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT CalendarDate FROM ClipboardHistoryEvent;";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (!DateOnly.TryParseExact(
                    reader.GetString(0),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly calendarDate) ||
                !coverage.Contains(calendarDate))
            {
                throw new InvalidDataException(
                    "Archive history contains an event outside assigned coverage.");
            }
        }
    }

    private sealed record ExpectedIdentityColumn(
        string Name,
        string Type,
        bool NotNull,
        int PrimaryKeyOrder);

    private static void CopyApplicationIdentities(
        SqliteConnection source,
        SqliteConnection target,
        SqliteTransaction transaction,
        JournalDateRange coverage,
        CancellationToken token,
        bool mergeApplicationRows)
    {
        using SqliteCommand read = CreateCoverageCommand(source, ApplicationIdentitySourceSql, coverage);
        using SqliteDataReader reader = read.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            using SqliteCommand insert = target.CreateCommand();
            insert.Transaction = transaction;
            // Extending a rotation candidate re-encounters identities seen on earlier days; those
            // rows are deduplicated by definition, and the final cross-check still verifies the
            // complete coverage exactly.
            insert.CommandText = mergeApplicationRows
                ? """
                  INSERT OR IGNORE INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
                  VALUES ($applicationId, $createdAtUtc);
                  """
                : """
                  INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
                  VALUES ($applicationId, $createdAtUtc);
                  """;
            insert.Parameters.AddWithValue("$applicationId", reader.GetString(0));
            insert.Parameters.AddWithValue("$createdAtUtc", reader.GetString(1));
            insert.ExecuteNonQuery();
        }
    }

    private static void CopyApplicationAliases(
        SqliteConnection source,
        SqliteConnection target,
        SqliteTransaction transaction,
        JournalDateRange coverage,
        CancellationToken token,
        bool mergeApplicationRows)
    {
        using SqliteCommand read = CreateCoverageCommand(source, ApplicationAliasSourceSql, coverage);
        using SqliteDataReader reader = read.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            using SqliteCommand insert = target.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = mergeApplicationRows
                ? """
                  INSERT OR IGNORE INTO ApplicationIdentityAlias (
                      AliasType, AliasValue, ApplicationId, CreatedAtUtc)
                  VALUES (
                      $aliasType, $aliasValue, $applicationId, $createdAtUtc);
                  """
                : """
                  INSERT INTO ApplicationIdentityAlias (
                      AliasType, AliasValue, ApplicationId, CreatedAtUtc)
                  VALUES (
                      $aliasType, $aliasValue, $applicationId, $createdAtUtc);
                  """;
            insert.Parameters.AddWithValue("$aliasType", reader.GetString(0));
            insert.Parameters.AddWithValue("$aliasValue", reader.GetString(1));
            insert.Parameters.AddWithValue("$applicationId", reader.GetString(2));
            insert.Parameters.AddWithValue("$createdAtUtc", reader.GetString(3));
            insert.ExecuteNonQuery();
        }
    }

    private static void CopyHistoryEvents(
        SqliteConnection source,
        SqliteConnection target,
        SqliteTransaction transaction,
        JournalDateRange coverage,
        CancellationToken token)
    {
        using SqliteCommand read = CreateCoverageCommand(source, HistoryEventSourceSql, coverage);
        using SqliteDataReader reader = read.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            using SqliteCommand insert = target.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ClipboardHistoryEvent (
                    EventId,
                    EventUtc,
                    LocalOffsetMinutes,
                    WindowsTimeZoneId,
                    CalendarDate,
                    SourceApplicationId,
                    SourceProcessId,
                    SourceExecutablePath,
                    SourceApplicationUserModelId)
                VALUES (
                    $eventId,
                    $eventUtc,
                    $localOffsetMinutes,
                    $windowsTimeZoneId,
                    $calendarDate,
                    $sourceApplicationId,
                    $sourceProcessId,
                    $sourceExecutablePath,
                    $sourceApplicationUserModelId);
                """;
            AddReaderValue(insert, "$eventId", reader, 0);
            AddReaderValue(insert, "$eventUtc", reader, 1);
            AddReaderValue(insert, "$localOffsetMinutes", reader, 2);
            AddReaderValue(insert, "$windowsTimeZoneId", reader, 3);
            AddReaderValue(insert, "$calendarDate", reader, 4);
            AddReaderValue(insert, "$sourceApplicationId", reader, 5);
            AddReaderValue(insert, "$sourceProcessId", reader, 6);
            AddReaderValue(insert, "$sourceExecutablePath", reader, 7);
            AddReaderValue(insert, "$sourceApplicationUserModelId", reader, 8);
            insert.ExecuteNonQuery();
        }
    }

    private static void CopyHistoryPayloads(
        SqliteConnection source,
        SqliteConnection target,
        SqliteTransaction transaction,
        JournalDateRange coverage,
        CancellationToken token)
    {
        using SqliteCommand read = CreateCoverageCommand(source, HistoryPayloadSourceSql, coverage);
        using SqliteDataReader reader = read.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            using SqliteCommand insert = target.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ClipboardHistoryPayload (
                    EventId,
                    PayloadOrder,
                    FormatName,
                    PayloadKind,
                    CanonicalByteCount,
                    InlineCanonicalText,
                    SearchText,
                    ExternalSha256,
                    ExternalRelativePath,
                    ExternalSizeBytes)
                VALUES (
                    $eventId,
                    $payloadOrder,
                    $formatName,
                    $payloadKind,
                    $canonicalByteCount,
                    $inlineCanonicalText,
                    $searchText,
                    $externalSha256,
                    $externalRelativePath,
                    $externalSizeBytes);
                """;
            AddReaderValue(insert, "$eventId", reader, 0);
            AddReaderValue(insert, "$payloadOrder", reader, 1);
            AddReaderValue(insert, "$formatName", reader, 2);
            AddReaderValue(insert, "$payloadKind", reader, 3);
            AddReaderValue(insert, "$canonicalByteCount", reader, 4);
            AddReaderValue(insert, "$inlineCanonicalText", reader, 5);
            AddReaderValue(insert, "$searchText", reader, 6);
            AddReaderValue(insert, "$externalSha256", reader, 7);
            AddReaderValue(insert, "$externalRelativePath", reader, 8);
            AddReaderValue(insert, "$externalSizeBytes", reader, 9);
            insert.ExecuteNonQuery();
        }
    }

    private static void CompareApplicationIdentities(
        SqliteConnection source,
        SqliteConnection staged,
        JournalDateRange coverage,
        string operationLabel,
        CancellationToken token)
    {
        using SqliteCommand sourceCommand = CreateCoverageCommand(
            source,
            ApplicationIdentitySourceSql,
            coverage);
        using SqliteCommand stagedCommand = staged.CreateCommand();
        stagedCommand.CommandText = """
            SELECT ApplicationId, CreatedAtUtc
            FROM ApplicationIdentity
            ORDER BY ApplicationId COLLATE BINARY;
            """;
        CompareQueryResults(
            sourceCommand,
            stagedCommand,
            $"{operationLabel} staged application identities do not match source.",
            token);
    }

    private static void CompareApplicationAliases(
        SqliteConnection source,
        SqliteConnection staged,
        JournalDateRange coverage,
        string operationLabel,
        CancellationToken token)
    {
        using SqliteCommand sourceCommand = CreateCoverageCommand(
            source,
            ApplicationAliasSourceSql,
            coverage);
        using SqliteCommand stagedCommand = staged.CreateCommand();
        stagedCommand.CommandText = """
            SELECT AliasType, AliasValue, ApplicationId, CreatedAtUtc
            FROM ApplicationIdentityAlias
            ORDER BY AliasType COLLATE BINARY,
                     AliasValue COLLATE BINARY;
            """;
        CompareQueryResults(
            sourceCommand,
            stagedCommand,
            $"{operationLabel} staged application aliases do not match source.",
            token);
    }

    private static void CompareHistoryEvents(
        SqliteConnection source,
        SqliteConnection staged,
        JournalDateRange coverage,
        string operationLabel,
        CancellationToken token)
    {
        using SqliteCommand sourceCommand = CreateCoverageCommand(
            source,
            HistoryEventSourceSql,
            coverage);
        using SqliteCommand stagedCommand = staged.CreateCommand();
        stagedCommand.CommandText = """
            SELECT EventId,
                   EventUtc,
                   LocalOffsetMinutes,
                   WindowsTimeZoneId,
                   CalendarDate,
                   SourceApplicationId,
                   SourceProcessId,
                   SourceExecutablePath,
                   SourceApplicationUserModelId
            FROM ClipboardHistoryEvent
            ORDER BY EventId COLLATE BINARY;
            """;
        CompareQueryResults(
            sourceCommand,
            stagedCommand,
            $"{operationLabel} staged event set or event content does not match source.",
            token);
    }

    private static void CompareHistoryPayloads(
        SqliteConnection source,
        SqliteConnection staged,
        JournalDateRange coverage,
        string operationLabel,
        CancellationToken token)
    {
        using SqliteCommand sourceCommand = CreateCoverageCommand(
            source,
            HistoryPayloadSourceSql,
            coverage);
        using SqliteCommand stagedCommand = staged.CreateCommand();
        stagedCommand.CommandText = """
            SELECT EventId,
                   PayloadOrder,
                   FormatName,
                   PayloadKind,
                   CanonicalByteCount,
                   InlineCanonicalText,
                   SearchText,
                   ExternalSha256,
                   ExternalRelativePath,
                   ExternalSizeBytes
            FROM ClipboardHistoryPayload
            ORDER BY EventId COLLATE BINARY,
                     PayloadOrder;
            """;
        CompareQueryResults(
            sourceCommand,
            stagedCommand,
            $"{operationLabel} staged payload set or payload content does not match source.",
            token);
    }

    private static void CompareQueryResults(
        SqliteCommand sourceCommand,
        SqliteCommand stagedCommand,
        string failureMessage,
        CancellationToken token)
    {
        using SqliteDataReader sourceReader = sourceCommand.ExecuteReader();
        using SqliteDataReader stagedReader = stagedCommand.ExecuteReader();

        if (sourceReader.FieldCount != stagedReader.FieldCount)
        {
            throw new InvalidDataException(failureMessage);
        }

        while (sourceReader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (!stagedReader.Read())
            {
                throw new InvalidDataException(failureMessage);
            }

            for (int index = 0; index < sourceReader.FieldCount; index++)
            {
                object sourceValue = sourceReader.GetValue(index);
                object stagedValue = stagedReader.GetValue(index);
                if (!Equals(sourceValue, stagedValue))
                {
                    throw new InvalidDataException(failureMessage);
                }
            }
        }

        if (stagedReader.Read())
        {
            throw new InvalidDataException(failureMessage);
        }
    }

    private static SqliteCommand CreateCoverageCommand(
        SqliteConnection connection,
        string commandText,
        JournalDateRange coverage)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("$coverageStart", FormatDate(coverage.StartDate));
        command.Parameters.AddWithValue("$coverageEnd", FormatDate(coverage.EndDate));
        return command;
    }

    private static void AddReaderValue(
        SqliteCommand command,
        string parameterName,
        SqliteDataReader reader,
        int ordinal)
    {
        command.Parameters.AddWithValue(
            parameterName,
            reader.IsDBNull(ordinal) ? DBNull.Value : reader.GetValue(ordinal));
    }

    private static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private const string ApplicationIdentitySourceSql = """
        SELECT DISTINCT a.ApplicationId, a.CreatedAtUtc
        FROM ApplicationIdentity AS a
        JOIN ClipboardHistoryEvent AS e
          ON e.SourceApplicationId = a.ApplicationId
        WHERE e.CalendarDate >= $coverageStart
          AND e.CalendarDate <= $coverageEnd
        ORDER BY a.ApplicationId COLLATE BINARY;
        """;

    private const string ApplicationAliasSourceSql = """
        SELECT DISTINCT
               alias.AliasType,
               alias.AliasValue,
               alias.ApplicationId,
               alias.CreatedAtUtc
        FROM ApplicationIdentityAlias AS alias
        WHERE EXISTS (
            SELECT 1
            FROM ClipboardHistoryEvent AS e
            WHERE e.SourceApplicationId = alias.ApplicationId
              AND e.CalendarDate >= $coverageStart
              AND e.CalendarDate <= $coverageEnd
        )
        ORDER BY alias.AliasType COLLATE BINARY,
                 alias.AliasValue COLLATE BINARY;
        """;

    private const string HistoryEventSourceSql = """
        SELECT EventId,
               EventUtc,
               LocalOffsetMinutes,
               WindowsTimeZoneId,
               CalendarDate,
               SourceApplicationId,
               SourceProcessId,
               SourceExecutablePath,
               SourceApplicationUserModelId
        FROM ClipboardHistoryEvent
        WHERE CalendarDate >= $coverageStart
          AND CalendarDate <= $coverageEnd
        ORDER BY EventId COLLATE BINARY;
        """;

    private const string HistoryPayloadSourceSql = """
        SELECT payload.EventId,
               payload.PayloadOrder,
               payload.FormatName,
               payload.PayloadKind,
               payload.CanonicalByteCount,
               payload.InlineCanonicalText,
               payload.SearchText,
               payload.ExternalSha256,
               payload.ExternalRelativePath,
               payload.ExternalSizeBytes
        FROM ClipboardHistoryPayload AS payload
        JOIN ClipboardHistoryEvent AS event
          ON event.EventId = payload.EventId
        WHERE event.CalendarDate >= $coverageStart
          AND event.CalendarDate <= $coverageEnd
        ORDER BY payload.EventId COLLATE BINARY,
                 payload.PayloadOrder;
        """;
}
