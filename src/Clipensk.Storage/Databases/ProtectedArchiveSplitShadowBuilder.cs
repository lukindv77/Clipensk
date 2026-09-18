using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

public sealed class ProtectedArchiveSplitShadowBuilder
{
    private const string ArchiveSplitOperationLabel = "Archive split";

    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly SqlitePendingArchiveSplitRepository _pendingSplitRepository;
    private readonly string _archiveDirectory;
    private readonly string _currentDatabasePath;

    public ProtectedArchiveSplitShadowBuilder(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _pendingSplitRepository = new SqlitePendingArchiveSplitRepository(
            session,
            _connectionFactory);
        string dataRootPath = Path.GetFullPath(session.DataRootPath);
        _archiveDirectory = Path.Combine(dataRootPath, "Archive");
        _currentDatabasePath = Path.Combine(dataRootPath, "Current", "current.db");
    }

    public async Task<PendingArchiveSplitOperation> BuildAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Archive split operation id cannot be empty.",
                nameof(operationId));
        }

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);

        return await Task.Run(
            () => BuildCore(operationId, token),
            CancellationToken.None).ConfigureAwait(false);
    }

    internal void ValidateShadowSet(
        PendingArchiveSplitOperation operation,
        ProtectedStorageMutationLease mutationLease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(mutationLease);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        ValidateShadowSetCore(operation, linked.Token);
    }

    internal string GetStagingDirectory(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Archive split operation id cannot be empty.",
                nameof(operationId));
        }

        return Path.Combine(
            _archiveDirectory,
            $".clipensk-archive-split-{operationId:D}");
    }

    private PendingArchiveSplitOperation BuildCore(
        Guid operationId,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        PendingArchiveSplitOperation operation =
            _pendingSplitRepository.ReadAsync(token).AsTask().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("No pending archive split exists.");

        if (operation.OperationId != operationId)
        {
            throw new InvalidOperationException(
                "Pending archive split ownership does not match the requested operation.");
        }

        if (operation.Phase != ArchiveSplitPhase.Planned)
        {
            throw new InvalidOperationException(
                "Archive split shadow set can be built only from Planned phase.");
        }

        string sourcePath = GetFinalArchivePath(operation.SourceFileName);
        DatabaseIdentity sourceIdentity = ValidateArchiveDatabase(
            sourcePath,
            operation.SourceFileName,
            operation.SourceDatabaseId,
            operation.SourceCoverage,
            token);

        string stagingDirectory = GetStagingDirectory(operation.OperationId);
        ResetExactStagingDirectory(stagingDirectory, token);

        using SqliteConnection source = OpenDatabase(
            sourcePath,
            SqliteOpenMode.ReadOnly,
            token);

        foreach (PendingArchiveSplitSegment segment in operation.Segments)
        {
            token.ThrowIfCancellationRequested();
            string stagedPath = GetStagedArchivePath(stagingDirectory, segment.FileName);
            DateTimeOffset createdAtUtc = segment.SegmentOrder == 0
                ? sourceIdentity.CreatedAtUtc
                : operation.CreatedAtUtc;

            BuildSegment(
                source,
                stagedPath,
                segment,
                createdAtUtc,
                token);

            _ = ValidateArchiveDatabase(
                stagedPath,
                segment.FileName,
                segment.DatabaseId,
                segment.Coverage,
                token);
        }

        ValidateShadowSetCore(operation, token);
        RecheckFinalArchiveReservations(operation, token);

        return AdvanceToReadyToPublish(operation, token);
    }

    private void BuildSegment(
        SqliteConnection source,
        string stagedPath,
        PendingArchiveSplitSegment segment,
        DateTimeOffset createdAtUtc,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (File.Exists(stagedPath) || Directory.Exists(stagedPath))
        {
            throw new InvalidOperationException(
                $"Archive split staged path '{segment.FileName.FileName}' already exists.");
        }

        using SqliteConnection target = OpenDatabase(
            stagedPath,
            SqliteOpenMode.ReadWriteCreate,
            token);
        using SqliteTransaction transaction = target.BeginTransaction();

        ArchiveShadowWriter.CreateArchiveV1Schema(
            target,
            transaction,
            _session.StorageId,
            segment.FileName,
            segment.DatabaseId,
            segment.Coverage,
            createdAtUtc);
        ArchiveShadowWriter.CopyCoverage(
            source,
            target,
            transaction,
            segment.Coverage,
            token);

        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void ValidateShadowSetCore(
        PendingArchiveSplitOperation operation,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string sourcePath = GetFinalArchivePath(operation.SourceFileName);
        _ = ValidateArchiveDatabase(
            sourcePath,
            operation.SourceFileName,
            operation.SourceDatabaseId,
            operation.SourceCoverage,
            token);

        string stagingDirectory = GetStagingDirectory(operation.OperationId);
        if (!Directory.Exists(stagingDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Archive split staging directory '{stagingDirectory}' was not found.");
        }

        using SqliteConnection source = OpenDatabase(
            sourcePath,
            SqliteOpenMode.ReadOnly,
            token);

        foreach (PendingArchiveSplitSegment segment in operation.Segments)
        {
            token.ThrowIfCancellationRequested();
            string stagedPath = GetStagedArchivePath(stagingDirectory, segment.FileName);
            _ = ValidateArchiveDatabase(
                stagedPath,
                segment.FileName,
                segment.DatabaseId,
                segment.Coverage,
                token);

            using SqliteConnection staged = OpenDatabase(
                stagedPath,
                SqliteOpenMode.ReadOnly,
                token);
            ArchiveShadowWriter.CompareCoverage(
                source,
                staged,
                segment.Coverage,
                ArchiveSplitOperationLabel,
                token);
        }
    }

    private DatabaseIdentity ValidateArchiveDatabase(
        string databasePath,
        ArchiveFileName expectedFileName,
        Guid expectedDatabaseId,
        JournalDateRange expectedCoverage,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCanonicalArchiveFileName(expectedFileName) ||
            expectedDatabaseId == Guid.Empty)
        {
            throw new ArgumentException("Expected Archive identity is invalid.");
        }

        if (!string.Equals(
                Path.GetFileName(databasePath),
                expectedFileName.FileName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Archive database path does not match the expected canonical filename.");
        }

        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException(
                "Archive database was not found.",
                databasePath);
        }

        using SqliteConnection connection = OpenDatabase(
            databasePath,
            SqliteOpenMode.ReadOnly,
            token);

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
            if (Convert.ToInt64(
                    count.ExecuteScalar(),
                    CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidDataException(
                    "Archive DatabaseIdentity must contain exactly one row.");
            }
        }

        DatabaseIdentity identity = ReadAndValidateIdentity(
            connection,
            expectedFileName,
            expectedDatabaseId,
            expectedCoverage);

        using (SqliteCommand userVersion = connection.CreateCommand())
        {
            userVersion.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(
                    userVersion.ExecuteScalar(),
                    CultureInfo.InvariantCulture) !=
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

    private DatabaseIdentity ReadAndValidateIdentity(
        SqliteConnection connection,
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
            storageId != _session.StorageId ||
            !Guid.TryParse(reader.GetString(1), out Guid databaseId) ||
            databaseId != expectedDatabaseId ||
            !Enum.TryParse(
                reader.GetString(2),
                ignoreCase: false,
                out DatabaseRole role) ||
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

        if (reader.IsDBNull(6) ||
            reader.GetInt32(6) != expectedFileName.BaseNumber)
        {
            throw new InvalidDataException(
                "Archive base number does not match the planned file name.");
        }

        int? splitSequence = reader.IsDBNull(7)
            ? null
            : reader.GetInt32(7);
        if (expectedFileName.SplitSequence == ArchiveFileName.NoSplit)
        {
            if (splitSequence is not null)
            {
                throw new InvalidDataException(
                    "Unsplit archive must store a null split sequence.");
            }
        }
        else if (splitSequence != expectedFileName.SplitSequence ||
                 splitSequence <= 0)
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
            throw new InvalidDataException(
                "Archive coverage does not match the planned range.");
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

    private static void ValidateDatabaseIdentityColumns(
        SqliteConnection connection)
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
            if (!string.Equals(
                    reader.GetString(1),
                    column.Name,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    reader.GetString(2),
                    column.Type,
                    StringComparison.OrdinalIgnoreCase) ||
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
            throw new InvalidDataException(
                "Archive contains invalid foreign-key references.");
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
        command.CommandText =
            "SELECT DISTINCT CalendarDate FROM ClipboardHistoryEvent;";
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

    private void RecheckFinalArchiveReservations(
        PendingArchiveSplitOperation operation,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = ValidateArchiveDatabase(
            GetFinalArchivePath(operation.SourceFileName),
            operation.SourceFileName,
            operation.SourceDatabaseId,
            operation.SourceCoverage,
            token);

        for (int index = 1; index < operation.Segments.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            PendingArchiveSplitSegment segment = operation.Segments[index];
            string finalPath = GetFinalArchivePath(segment.FileName);
            if (File.Exists(finalPath) || Directory.Exists(finalPath))
            {
                throw new InvalidOperationException(
                    $"Reserved Archive filename '{segment.FileName.FileName}' is already occupied.");
            }
        }
    }

    private PendingArchiveSplitOperation AdvanceToReadyToPublish(
        PendingArchiveSplitOperation operation,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenDatabase(
            _currentDatabasePath,
            SqliteOpenMode.ReadWrite,
            token);
        using SqliteTransaction transaction = connection.BeginTransaction();

        PendingArchiveSplitOperation updated =
            SqlitePendingArchiveSplitRepository.AdvancePhaseInTransaction(
                connection,
                transaction,
                operation.OperationId,
                ArchiveSplitPhase.Planned,
                ArchiveSplitPhase.ReadyToPublish,
                token);

        token.ThrowIfCancellationRequested();
        transaction.Commit();

        return updated;
    }

    private SqliteConnection OpenDatabase(
        string databasePath,
        SqliteOpenMode mode,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ReadOnlyMemory<byte> masterKey = _session.DangerousGetMasterKeyMemory();
        SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            masterKey,
            mode);
        try
        {
            EnableForeignKeys(connection);
            token.ThrowIfCancellationRequested();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void ResetExactStagingDirectory(
        string stagingDirectory,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (File.Exists(stagingDirectory))
        {
            throw new InvalidOperationException(
                "Archive split staging path is occupied by a file.");
        }

        if (Directory.Exists(stagingDirectory))
        {
            FileAttributes attributes = File.GetAttributes(stagingDirectory);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "Archive split staging directory cannot be a reparse point.");
            }

            Directory.Delete(stagingDirectory, recursive: true);
        }

        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(stagingDirectory);
    }

    private string GetFinalArchivePath(ArchiveFileName fileName)
    {
        if (!IsCanonicalArchiveFileName(fileName))
        {
            throw new InvalidDataException(
                "Archive split plan contains a noncanonical filename.");
        }

        return Path.Combine(_archiveDirectory, fileName.FileName);
    }

    private static string GetStagedArchivePath(
        string stagingDirectory,
        ArchiveFileName fileName)
    {
        if (!IsCanonicalArchiveFileName(fileName))
        {
            throw new InvalidDataException(
                "Archive split plan contains a noncanonical filename.");
        }

        return Path.Combine(stagingDirectory, fileName.FileName);
    }

    private static bool IsCanonicalArchiveFileName(ArchiveFileName fileName) =>
        ArchiveFileName.TryParse(
            fileName.FileName,
            out ArchiveFileName parsed) &&
        parsed == fileName &&
        string.Equals(
            parsed.FileName,
            fileName.FileName,
            StringComparison.Ordinal);

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }

    private static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed record ExpectedIdentityColumn(
        string Name,
        string Type,
        bool NotNull,
        int PrimaryKeyOrder);
}
