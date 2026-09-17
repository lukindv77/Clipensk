using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

public enum ArchiveSplitPublicationCheckpoint
{
    BeforeAdditionalMove = 0,
    AfterAdditionalMove = 1,
    AfterAdditionalValidation = 2,
    BeforeSourceReplace = 3,
    AfterSourceReplace = 4,
    AfterSourceValidation = 5,
    BeforePhysicalPhaseCommit = 6,
    AfterPhysicalPhaseCommit = 7,
}

public sealed class ProtectedArchiveSplitPublisher
{
    private const string SourceBackupFileName = "source-backup.db";

    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly SqlitePendingArchiveSplitRepository _pendingSplitRepository;
    private readonly ProtectedArchiveSplitShadowBuilder _shadowBuilder;
    private readonly ProtectedArchiveDatabaseService _archiveService;
    private readonly Action<ArchiveSplitPublicationCheckpoint, int?>? _faultInjector;
    private readonly string _archiveDirectory;
    private readonly string _currentDatabasePath;

    public ProtectedArchiveSplitPublisher(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null,
        Action<ArchiveSplitPublicationCheckpoint, int?>? faultInjector = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _pendingSplitRepository = new SqlitePendingArchiveSplitRepository(
            session,
            _connectionFactory);
        _shadowBuilder = new ProtectedArchiveSplitShadowBuilder(
            session,
            _connectionFactory);
        _archiveService = new ProtectedArchiveDatabaseService(
            session,
            _connectionFactory);
        _faultInjector = faultInjector;

        string dataRootPath = Path.GetFullPath(session.DataRootPath);
        _archiveDirectory = Path.Combine(dataRootPath, "Archive");
        _currentDatabasePath = Path.Combine(dataRootPath, "Current", "current.db");
    }

    public async Task<PendingArchiveSplitOperation> PublishOrRecoverAsync(
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
            () => PublishOrRecoverCore(operationId, mutationLease, token),
            CancellationToken.None).ConfigureAwait(false);
    }

    public string GetBackupPath(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Archive split operation id cannot be empty.",
                nameof(operationId));
        }

        return Path.Combine(
            _shadowBuilder.GetStagingDirectory(operationId),
            SourceBackupFileName);
    }

    private PendingArchiveSplitOperation PublishOrRecoverCore(
        Guid operationId,
        ProtectedStorageMutationLease mutationLease,
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

        if (operation.Phase is ArchiveSplitPhase.PhysicalPublished or ArchiveSplitPhase.CatalogPublished)
        {
            ValidatePhysicalSet(operation, token);
            return operation;
        }

        if (operation.Phase != ArchiveSplitPhase.ReadyToPublish)
        {
            throw new InvalidOperationException(
                "Archive split physical publication requires ReadyToPublish phase.");
        }

        PendingArchiveSplitSegment firstSegment = operation.Segments[0];
        string stagingDirectory = _shadowBuilder.GetStagingDirectory(operation.OperationId);
        string backupPath = GetBackupPath(operation.OperationId);
        SourceDisposition sourceDisposition = InspectSource(operation, firstSegment, token);
        bool anyAdditionalAlreadyPublished = operation.Segments
            .Skip(1)
            .Any(segment => File.Exists(GetFinalPath(segment.FileName)));

        if (sourceDisposition == SourceDisposition.Original &&
            !anyAdditionalAlreadyPublished)
        {
            _shadowBuilder.ValidateShadowSet(operation, mutationLease, token);
        }

        PublishAdditionalSegments(operation, stagingDirectory, token);
        PublishSourceReplacement(
            operation,
            firstSegment,
            stagingDirectory,
            backupPath,
            token);

        ValidatePhysicalSet(operation, token);
        Hit(ArchiveSplitPublicationCheckpoint.BeforePhysicalPhaseCommit, null);
        token.ThrowIfCancellationRequested();

        PendingArchiveSplitOperation updated = AdvanceToPhysicalPublished(
            operation,
            token);

        // Deliberately do not observe caller cancellation after the durable phase commit.
        // A late cancellation must not turn durable success into a reported rollback.
        Hit(ArchiveSplitPublicationCheckpoint.AfterPhysicalPhaseCommit, null);
        return updated;
    }

    private void PublishAdditionalSegments(
        PendingArchiveSplitOperation operation,
        string stagingDirectory,
        CancellationToken token)
    {
        for (int index = 1; index < operation.Segments.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            PendingArchiveSplitSegment segment = operation.Segments[index];
            string stagedPath = Path.Combine(stagingDirectory, segment.FileName.FileName);
            string finalPath = GetFinalPath(segment.FileName);

            if (Directory.Exists(finalPath))
            {
                throw new InvalidOperationException(
                    $"Reserved Archive filename '{segment.FileName.FileName}' is occupied by a directory.");
            }

            if (File.Exists(finalPath))
            {
                if (File.Exists(stagedPath) || Directory.Exists(stagedPath))
                {
                    throw new InvalidOperationException(
                        $"Archive split segment '{segment.FileName.FileName}' exists in both staging and final locations.");
                }

                ValidateFinalSegment(segment, token);
                continue;
            }

            if (!File.Exists(stagedPath) || Directory.Exists(stagedPath))
            {
                throw new FileNotFoundException(
                    $"Staged Archive split segment '{segment.FileName.FileName}' was not found.",
                    stagedPath);
            }

            ValidateArchiveAtPath(
                stagedPath,
                segment.FileName,
                segment.DatabaseId,
                segment.Coverage,
                token);

            Hit(
                ArchiveSplitPublicationCheckpoint.BeforeAdditionalMove,
                segment.SegmentOrder);
            token.ThrowIfCancellationRequested();
            File.Move(stagedPath, finalPath);
            Hit(
                ArchiveSplitPublicationCheckpoint.AfterAdditionalMove,
                segment.SegmentOrder);

            ValidateFinalSegment(segment, token);
            Hit(
                ArchiveSplitPublicationCheckpoint.AfterAdditionalValidation,
                segment.SegmentOrder);
        }
    }

    private void PublishSourceReplacement(
        PendingArchiveSplitOperation operation,
        PendingArchiveSplitSegment firstSegment,
        string stagingDirectory,
        string backupPath,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SourceDisposition disposition = InspectSource(operation, firstSegment, token);
        string stagedFirstPath = Path.Combine(
            stagingDirectory,
            firstSegment.FileName.FileName);

        if (disposition == SourceDisposition.Replacement)
        {
            if (!File.Exists(backupPath) || Directory.Exists(backupPath))
            {
                throw new InvalidDataException(
                    "Archive split source is already replaced but its full-source backup is missing.");
            }

            ValidateArchiveAtPath(
                backupPath,
                operation.SourceFileName,
                operation.SourceDatabaseId,
                operation.SourceCoverage,
                token);

            if (File.Exists(stagedFirstPath) || Directory.Exists(stagedFirstPath))
            {
                throw new InvalidOperationException(
                    "Archive split first segment exists in staging after source replacement.");
            }

            return;
        }

        if (File.Exists(backupPath) || Directory.Exists(backupPath))
        {
            throw new InvalidOperationException(
                "Archive split backup path is unexpectedly occupied before source replacement.");
        }

        if (!File.Exists(stagedFirstPath) || Directory.Exists(stagedFirstPath))
        {
            throw new FileNotFoundException(
                "Staged first Archive split segment was not found.",
                stagedFirstPath);
        }

        ValidateArchiveAtPath(
            stagedFirstPath,
            firstSegment.FileName,
            firstSegment.DatabaseId,
            firstSegment.Coverage,
            token);

        Hit(ArchiveSplitPublicationCheckpoint.BeforeSourceReplace, 0);
        token.ThrowIfCancellationRequested();
        File.Replace(
            stagedFirstPath,
            GetFinalPath(operation.SourceFileName),
            backupPath);
        Hit(ArchiveSplitPublicationCheckpoint.AfterSourceReplace, 0);

        ValidateFinalSegment(firstSegment, token);
        ValidateArchiveAtPath(
            backupPath,
            operation.SourceFileName,
            operation.SourceDatabaseId,
            operation.SourceCoverage,
            token);
        Hit(ArchiveSplitPublicationCheckpoint.AfterSourceValidation, 0);
    }

    private SourceDisposition InspectSource(
        PendingArchiveSplitOperation operation,
        PendingArchiveSplitSegment firstSegment,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        DatabaseIdentity identity = _archiveService
            .ValidateAsync(operation.SourceFileName, token)
            .GetAwaiter()
            .GetResult();
        JournalDateRange coverage = GetCoverage(identity);

        if (identity.DatabaseId == operation.SourceDatabaseId &&
            coverage == operation.SourceCoverage)
        {
            return SourceDisposition.Original;
        }

        if (identity.DatabaseId == firstSegment.DatabaseId &&
            coverage == firstSegment.Coverage)
        {
            return SourceDisposition.Replacement;
        }

        throw new InvalidDataException(
            "Archive split source identity/coverage matches neither the original source nor the planned first segment.");
    }

    private void ValidatePhysicalSet(
        PendingArchiveSplitOperation operation,
        CancellationToken token)
    {
        foreach (PendingArchiveSplitSegment segment in operation.Segments)
        {
            token.ThrowIfCancellationRequested();
            ValidateFinalSegment(segment, token);
        }

        var descriptors = new List<ArchiveSegmentDescriptor>();
        foreach (string path in Directory.EnumerateFiles(
                     _archiveDirectory,
                     "archive_*.db",
                     SearchOption.TopDirectoryOnly))
        {
            token.ThrowIfCancellationRequested();
            string leafName = Path.GetFileName(path);
            if (!ArchiveFileName.TryParse(leafName, out ArchiveFileName fileName) ||
                !string.Equals(fileName.FileName, leafName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Archive directory contains noncanonical archive filename '{leafName}'.");
            }

            DatabaseIdentity identity = _archiveService
                .ValidateAsync(fileName, token)
                .GetAwaiter()
                .GetResult();
            descriptors.Add(new ArchiveSegmentDescriptor(
                identity.DatabaseId,
                fileName.FileName,
                GetCoverage(identity),
                IsSealed: true));
        }

        StorageQueryPlanner.ValidateArchiveCoverage(descriptors);
    }

    private void ValidateFinalSegment(
        PendingArchiveSplitSegment segment,
        CancellationToken token)
    {
        DatabaseIdentity identity = _archiveService
            .ValidateAsync(segment.FileName, token)
            .GetAwaiter()
            .GetResult();
        if (identity.DatabaseId != segment.DatabaseId ||
            GetCoverage(identity) != segment.Coverage)
        {
            throw new InvalidDataException(
                $"Published Archive split segment '{segment.FileName.FileName}' does not match the durable plan.");
        }
    }

    private void ValidateArchiveAtPath(
        string path,
        ArchiveFileName expectedFileName,
        Guid expectedDatabaseId,
        JournalDateRange expectedCoverage,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Archive database was not found.", path);
        }

        using SqliteConnection connection = _connectionFactory.Open(
            path,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadOnly);
        EnableForeignKeys(connection);

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

        using (SqliteCommand foreignKeyCheck = connection.CreateCommand())
        {
            foreignKeyCheck.CommandText = "PRAGMA foreign_key_check;";
            using SqliteDataReader reader = foreignKeyCheck.ExecuteReader();
            if (reader.Read())
            {
                throw new InvalidDataException(
                    "Archive contains invalid foreign-key references.");
            }
        }

        using (SqliteCommand version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture) !=
                ProtectedArchiveDatabaseService.ArchiveSchemaVersion)
            {
                throw new InvalidDataException("Archive schema version is invalid.");
            }
        }

        using SqliteCommand identityCommand = connection.CreateCommand();
        identityCommand.CommandText = """
            SELECT StorageId, DatabaseId, DatabaseRole, SchemaVersion,
                   EncryptionVersion, ArchiveBaseNumber, ArchiveSplitSequence,
                   CoverageStartDate, CoverageEndDate
            FROM DatabaseIdentity
            WHERE SingletonId = 1;
            """;
        using SqliteDataReader identityReader = identityCommand.ExecuteReader();
        if (!identityReader.Read() ||
            !Guid.TryParseExact(identityReader.GetString(0), "D", out Guid storageId) ||
            storageId != _session.StorageId ||
            !Guid.TryParseExact(identityReader.GetString(1), "D", out Guid databaseId) ||
            databaseId != expectedDatabaseId ||
            !string.Equals(
                identityReader.GetString(2),
                DatabaseRole.Archive.ToString(),
                StringComparison.Ordinal) ||
            identityReader.GetInt32(3) != ProtectedArchiveDatabaseService.ArchiveSchemaVersion ||
            identityReader.GetInt32(4) != ProtectedStorageDatabaseService.CurrentEncryptionVersion ||
            identityReader.IsDBNull(5) ||
            identityReader.GetInt32(5) != expectedFileName.BaseNumber ||
            !SplitSequenceMatches(identityReader, 6, expectedFileName) ||
            !TryReadCoverage(identityReader, 7, 8, out JournalDateRange coverage) ||
            coverage != expectedCoverage)
        {
            throw new InvalidDataException(
                "Archive database does not match the planned identity/coverage.");
        }

        if (identityReader.Read())
        {
            throw new InvalidDataException(
                "Archive DatabaseIdentity contains more than one singleton row.");
        }
        identityReader.Close();

        using SqliteCommand coverageCommand = connection.CreateCommand();
        coverageCommand.CommandText = """
            SELECT COUNT(*)
            FROM ClipboardHistoryEvent
            WHERE CalendarDate < $coverageStart OR CalendarDate > $coverageEnd;
            """;
        coverageCommand.Parameters.AddWithValue(
            "$coverageStart",
            FormatDate(expectedCoverage.StartDate));
        coverageCommand.Parameters.AddWithValue(
            "$coverageEnd",
            FormatDate(expectedCoverage.EndDate));
        if (Convert.ToInt64(coverageCommand.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
        {
            throw new InvalidDataException(
                "Archive history contains events outside planned coverage.");
        }
    }

    private PendingArchiveSplitOperation AdvanceToPhysicalPublished(
        PendingArchiveSplitOperation operation,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();

        PendingArchiveSplitOperation updated =
            SqlitePendingArchiveSplitRepository.AdvancePhaseInTransaction(
                connection,
                transaction,
                operation.OperationId,
                ArchiveSplitPhase.ReadyToPublish,
                ArchiveSplitPhase.PhysicalPublished,
                token);

        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return updated;
    }

    private string GetFinalPath(ArchiveFileName fileName) =>
        Path.Combine(_archiveDirectory, fileName.FileName);

    private void Hit(
        ArchiveSplitPublicationCheckpoint checkpoint,
        int? segmentOrder) =>
        _faultInjector?.Invoke(checkpoint, segmentOrder);

    private static JournalDateRange GetCoverage(DatabaseIdentity identity)
    {
        if (identity.CoverageStartDate is not DateOnly start ||
            identity.CoverageEndDate is not DateOnly end ||
            end < start)
        {
            throw new InvalidDataException("Archive coverage is missing or invalid.");
        }

        return new JournalDateRange(start, end);
    }

    private static bool SplitSequenceMatches(
        SqliteDataReader reader,
        int ordinal,
        ArchiveFileName expectedFileName)
    {
        if (expectedFileName.SplitSequence == ArchiveFileName.NoSplit)
        {
            return reader.IsDBNull(ordinal);
        }

        return !reader.IsDBNull(ordinal) &&
               reader.GetInt32(ordinal) == expectedFileName.SplitSequence;
    }

    private static bool TryReadCoverage(
        SqliteDataReader reader,
        int startOrdinal,
        int endOrdinal,
        out JournalDateRange coverage)
    {
        coverage = default;
        if (reader.IsDBNull(startOrdinal) ||
            reader.IsDBNull(endOrdinal) ||
            !DateOnly.TryParseExact(
                reader.GetString(startOrdinal),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly start) ||
            !DateOnly.TryParseExact(
                reader.GetString(endOrdinal),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly end) ||
            end < start)
        {
            return false;
        }

        coverage = new JournalDateRange(start, end);
        return true;
    }

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }

    private static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private enum SourceDisposition
    {
        Original = 0,
        Replacement = 1,
    }
}
