using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

public enum ArchiveSplitCatalogCheckpoint
{
    BeforeCatalogReplacement = 0,
    AfterCatalogReplacement = 1,
    AfterCatalogValidation = 2,
    BeforeCatalogPhaseCommit = 3,
    AfterCatalogPhaseCommit = 4,
    AfterBackupDelete = 5,
    AfterStagingDelete = 6,
    BeforeMarkerClear = 7,
}

public sealed class ProtectedArchiveSplitCatalogPublisher
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly SqlitePendingArchiveSplitRepository _pendingSplitRepository;
    private readonly ProtectedArchiveDatabaseService _archiveService;
    private readonly ProtectedArchiveSegmentCatalog _archiveCatalog;
    private readonly ProtectedStorageCatalogReplacementService _catalogReplacement;
    private readonly ProtectedArchiveSplitPublisher _physicalPublisher;
    private readonly ProtectedArchiveSplitShadowBuilder _shadowBuilder;
    private readonly Action<ArchiveSplitCatalogCheckpoint>? _faultInjector;
    private readonly string _dataRootPath;
    private readonly string _archiveDirectory;
    private readonly string _currentDatabasePath;
    private readonly string _catalogDatabasePath;
    private readonly string _filesRootPath;
    private readonly string _filesRootPrefix;

    public ProtectedArchiveSplitCatalogPublisher(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null,
        Action<ArchiveSplitCatalogCheckpoint>? faultInjector = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _pendingSplitRepository = new SqlitePendingArchiveSplitRepository(
            session,
            _connectionFactory);
        _archiveService = new ProtectedArchiveDatabaseService(
            session,
            _connectionFactory);
        _archiveCatalog = new ProtectedArchiveSegmentCatalog(
            session,
            _connectionFactory);
        _catalogReplacement = new ProtectedStorageCatalogReplacementService(
            _connectionFactory);
        _physicalPublisher = new ProtectedArchiveSplitPublisher(
            session,
            _connectionFactory);
        _shadowBuilder = new ProtectedArchiveSplitShadowBuilder(
            session,
            _connectionFactory);
        _faultInjector = faultInjector;

        _dataRootPath = Path.GetFullPath(session.DataRootPath);
        _archiveDirectory = Path.Combine(_dataRootPath, "Archive");
        _currentDatabasePath = Path.Combine(_dataRootPath, "Current", "current.db");
        _catalogDatabasePath = Path.Combine(_dataRootPath, "Current", "storage-catalog.db");
        _filesRootPath = Path.TrimEndingDirectorySeparator(Path.Combine(_dataRootPath, "Files"));
        _filesRootPrefix = _filesRootPath + Path.DirectorySeparatorChar;
    }

    public async Task<IReadOnlyList<ArchiveSegmentDescriptor>> PublishOrRecoverAsync(
        Guid operationId,
        DateOnly currentCalendarDate,
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

        PendingArchiveSplitOperation operation =
            await _pendingSplitRepository.ReadAsync(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No pending archive split exists.");
        if (operation.OperationId != operationId)
        {
            throw new InvalidOperationException(
                "Pending archive split ownership does not match the requested operation.");
        }
        if (operation.Phase is not (ArchiveSplitPhase.PhysicalPublished or ArchiveSplitPhase.CatalogPublished))
        {
            throw new InvalidOperationException(
                "Archive split Catalog publication requires PhysicalPublished or CatalogPublished phase.");
        }

        ValidatePhysicalSet(operation, token);

        IReadOnlyList<ArchiveSegmentDescriptor> descriptors;
        if (operation.Phase == ArchiveSplitPhase.PhysicalPublished)
        {
            ValidateRequiredBackup(operation);
            Hit(ArchiveSplitCatalogCheckpoint.BeforeCatalogReplacement);
            token.ThrowIfCancellationRequested();

            ProtectedStorageCatalogReplacementResult replacement =
                await _catalogReplacement.ReplaceExistingCatalogAsync(
                    _dataRootPath,
                    _session.StorageId,
                    _session.DangerousGetMasterKeyMemory(),
                    currentCalendarDate,
                    token).ConfigureAwait(false);
            if (!replacement.IsSuccess)
            {
                throw new InvalidDataException(
                    $"Archive split Catalog replacement failed with status {replacement.Status}.");
            }

            Hit(ArchiveSplitCatalogCheckpoint.AfterCatalogReplacement);
            descriptors = await ValidateCatalogAsync(
                operation,
                currentCalendarDate,
                token).ConfigureAwait(false);
            ValidateRequiredBackup(operation);
            Hit(ArchiveSplitCatalogCheckpoint.AfterCatalogValidation);
            Hit(ArchiveSplitCatalogCheckpoint.BeforeCatalogPhaseCommit);
            token.ThrowIfCancellationRequested();

            operation = AdvanceToCatalogPublished(operation, token);

            // After this durable phase commit caller cancellation is not rollback.
            // Recovery can always finish cleanup from CatalogPublished.
            Hit(ArchiveSplitCatalogCheckpoint.AfterCatalogPhaseCommit);
        }
        else
        {
            descriptors = await ValidateCatalogAsync(
                operation,
                currentCalendarDate,
                token).ConfigureAwait(false);
        }

        CancellationToken cleanupToken = _session.CancellationToken;
        ValidatePhysicalSet(operation, cleanupToken);
        DeleteBackupIfPresent(operation.OperationId);
        Hit(ArchiveSplitCatalogCheckpoint.AfterBackupDelete);
        DeleteStagingIfPresent(operation.OperationId);
        Hit(ArchiveSplitCatalogCheckpoint.AfterStagingDelete);
        Hit(ArchiveSplitCatalogCheckpoint.BeforeMarkerClear);
        ClearCompleted(operation.OperationId);
        return descriptors;
    }

    private async Task<IReadOnlyList<ArchiveSegmentDescriptor>> ValidateCatalogAsync(
        PendingArchiveSplitOperation operation,
        DateOnly currentCalendarDate,
        CancellationToken token)
    {
        IReadOnlyList<ArchiveSegmentDescriptor> descriptors =
            await _archiveCatalog.ValidateConsistencyAsync(
                currentCalendarDate,
                token).ConfigureAwait(false);
        ValidatePlannedDescriptors(operation, descriptors);
        ValidateExternalPayloadCatalogConsistency(token);
        return descriptors;
    }

    private void ValidatePhysicalSet(
        PendingArchiveSplitOperation operation,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        foreach (PendingArchiveSplitSegment segment in operation.Segments)
        {
            token.ThrowIfCancellationRequested();
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

        var descriptors = new List<ArchiveSegmentDescriptor>();
        if (Directory.Exists(_archiveDirectory))
        {
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
                    IsSealed: false));
            }
        }

        StorageQueryPlanner.ValidateArchiveCoverage(descriptors);
    }

    private static void ValidatePlannedDescriptors(
        PendingArchiveSplitOperation operation,
        IReadOnlyList<ArchiveSegmentDescriptor> descriptors)
    {
        var byFileName = descriptors.ToDictionary(
            item => item.FileName,
            StringComparer.Ordinal);
        foreach (PendingArchiveSplitSegment segment in operation.Segments)
        {
            if (!byFileName.TryGetValue(segment.FileName.FileName, out ArchiveSegmentDescriptor? descriptor) ||
                descriptor.DatabaseId != segment.DatabaseId ||
                descriptor.Coverage != segment.Coverage)
            {
                throw new InvalidDataException(
                    $"Storage Catalog does not match planned Archive split segment '{segment.FileName.FileName}'.");
            }
        }
    }

    private void ValidateRequiredBackup(PendingArchiveSplitOperation operation)
    {
        string backupPath = _physicalPublisher.GetBackupPath(operation.OperationId);
        if (!File.Exists(backupPath) || Directory.Exists(backupPath))
        {
            throw new InvalidDataException(
                "Archive split full-source backup is required until CatalogPublished is durable.");
        }
    }

    private void DeleteBackupIfPresent(Guid operationId)
    {
        string backupPath = _physicalPublisher.GetBackupPath(operationId);
        if (Directory.Exists(backupPath))
        {
            throw new InvalidDataException(
                "Archive split backup path is occupied by a directory.");
        }
        if (File.Exists(backupPath))
        {
            File.Delete(backupPath);
        }
    }

    private void DeleteStagingIfPresent(Guid operationId)
    {
        string stagingDirectory = _shadowBuilder.GetStagingDirectory(operationId);
        if (File.Exists(stagingDirectory))
        {
            throw new InvalidDataException(
                "Archive split staging path is occupied by a file.");
        }
        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }
    }

    private PendingArchiveSplitOperation AdvanceToCatalogPublished(
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
                ArchiveSplitPhase.PhysicalPublished,
                ArchiveSplitPhase.CatalogPublished,
                token);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return updated;
    }

    private void ClearCompleted(Guid operationId)
    {
        using SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();
        SqlitePendingArchiveSplitRepository.ClearCompletedInTransaction(
            connection,
            transaction,
            operationId,
            CancellationToken.None);
        transaction.Commit();
    }

    private void ValidateExternalPayloadCatalogConsistency(CancellationToken token)
    {
        ExternalPayloadAddress[] expected = BuildExternalPayloadProjection(token);
        ExternalPayloadAddress[] persisted = ReadExternalPayloadCatalog(token);
        if (!expected.SequenceEqual(persisted))
        {
            throw new InvalidDataException(
                "External payload Catalog projection does not match validated Current/Archive history.");
        }
    }

    private ExternalPayloadAddress[] BuildExternalPayloadProjection(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bySha = new Dictionary<string, ExternalPayloadAddress>(StringComparer.Ordinal);
        var shaByRelativePath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        ScanExternalPayloads(
            _currentDatabasePath,
            DatabaseRole.Current,
            expectedDatabaseId: null,
            bySha,
            shaByRelativePath,
            token);

        if (Directory.Exists(_archiveDirectory))
        {
            foreach (string path in Directory.EnumerateFiles(
                         _archiveDirectory,
                         "archive_*.db",
                         SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                string leafName = Path.GetFileName(path);
                if (!ArchiveFileName.TryParse(leafName, out ArchiveFileName fileName) ||
                    !string.Equals(fileName.FileName, leafName, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Archive file '{leafName}' does not use the canonical Clipensk file name.");
                }
                DatabaseIdentity identity = _archiveService
                    .ValidateAsync(fileName, token)
                    .GetAwaiter()
                    .GetResult();
                ScanExternalPayloads(
                    path,
                    DatabaseRole.Archive,
                    identity.DatabaseId,
                    bySha,
                    shaByRelativePath,
                    token);
            }
        }

        return bySha.Values
            .OrderBy(item => item.Sha256, StringComparer.Ordinal)
            .ToArray();
    }

    private void ScanExternalPayloads(
        string databasePath,
        DatabaseRole expectedRole,
        Guid? expectedDatabaseId,
        Dictionary<string, ExternalPayloadAddress> bySha,
        Dictionary<string, string> shaByRelativePath,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadOnly);
        ValidateHistoryDatabase(connection, expectedRole, expectedDatabaseId, token);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT PayloadKind, CanonicalByteCount, InlineCanonicalText IS NULL,
                   SearchText IS NULL, ExternalSha256, ExternalRelativePath, ExternalSizeBytes
            FROM ClipboardHistoryPayload
            ORDER BY EventId COLLATE BINARY, PayloadOrder;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            string payloadKind = reader.GetString(0);
            long canonicalByteCount = reader.GetInt64(1);
            bool inlineIsNull = reader.GetInt32(2) == 1;
            bool searchIsNull = reader.GetInt32(3) == 1;
            bool shaIsNull = reader.IsDBNull(4);
            bool pathIsNull = reader.IsDBNull(5);
            bool sizeIsNull = reader.IsDBNull(6);

            if (payloadKind is "Text" or "Link" or "StorageItems")
            {
                if (inlineIsNull || !shaIsNull || !pathIsNull || !sizeIsNull)
                {
                    throw new InvalidDataException(
                        "History inline payload representation is invalid during Archive split Catalog validation.");
                }
                continue;
            }
            if (payloadKind is not ("PngImage" or "CustomBinary") ||
                !inlineIsNull || !searchIsNull || shaIsNull || pathIsNull || sizeIsNull)
            {
                throw new InvalidDataException(
                    "History external payload representation is invalid during Archive split Catalog validation.");
            }

            var address = new ExternalPayloadAddress(
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6));
            if (canonicalByteCount < 0 || address.SizeBytes != canonicalByteCount)
            {
                throw new InvalidDataException(
                    "History external payload size is invalid during Archive split Catalog validation.");
            }
            ValidateExternalAddress(address);

            if (bySha.TryGetValue(address.Sha256, out ExternalPayloadAddress? existing))
            {
                if (existing != address)
                {
                    throw new InvalidDataException(
                        "The same external payload SHA-256 maps to conflicting metadata.");
                }
            }
            else
            {
                if (shaByRelativePath.TryGetValue(address.RelativePath, out string? existingSha) &&
                    !string.Equals(existingSha, address.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "One external payload relative path maps to multiple SHA-256 values.");
                }
                bySha.Add(address.Sha256, address);
                shaByRelativePath[address.RelativePath] = address.Sha256;
            }
        }
    }

    private void ValidateHistoryDatabase(
        SqliteConnection connection,
        DatabaseRole expectedRole,
        Guid? expectedDatabaseId,
        CancellationToken token)
    {
        using SqliteCommand identity = connection.CreateCommand();
        identity.CommandText = """
            SELECT StorageId, DatabaseId, DatabaseRole, SchemaVersion
            FROM DatabaseIdentity
            WHERE SingletonId = 1;
            """;
        using SqliteDataReader reader = identity.ExecuteReader();
        if (!reader.Read() ||
            !Guid.TryParseExact(reader.GetString(0), "D", out Guid storageId) ||
            storageId != _session.StorageId ||
            !Guid.TryParseExact(reader.GetString(1), "D", out Guid databaseId) ||
            databaseId == Guid.Empty ||
            (expectedDatabaseId is Guid requiredDatabaseId && databaseId != requiredDatabaseId) ||
            !string.Equals(reader.GetString(2), expectedRole.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Archive split Catalog validation requires the expected {expectedRole} database.");
        }
        int schemaVersion = reader.GetInt32(3);
        reader.Close();
        if ((expectedRole == DatabaseRole.Current &&
             schemaVersion < ClipboardHistorySqlSchema.RequiredCurrentSchemaVersion) ||
            (expectedRole == DatabaseRole.Archive &&
             schemaVersion != ProtectedArchiveDatabaseService.ArchiveSchemaVersion))
        {
            throw new InvalidDataException(
                $"Database role {expectedRole} has an invalid history schema version.");
        }
        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
        {
            throw new InvalidDataException(
                $"Database role {expectedRole} user_version does not match DatabaseIdentity.");
        }
        ClipboardHistorySqlSchema.ValidateTables(connection);
        token.ThrowIfCancellationRequested();
    }

    private ExternalPayloadAddress[] ReadExternalPayloadCatalog(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteConnection connection = _connectionFactory.Open(
            _catalogDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadOnly);
        ValidateCatalogDatabase(connection, token);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT Sha256, RelativePath, SizeBytes
            FROM ExternalPayloadAddressIndex
            ORDER BY Sha256 COLLATE BINARY;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<ExternalPayloadAddress>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            var address = new ExternalPayloadAddress(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2));
            ValidateExternalAddress(address);
            result.Add(address);
        }
        return result.ToArray();
    }

    private void ValidateCatalogDatabase(SqliteConnection connection, CancellationToken token)
    {
        using SqliteCommand identity = connection.CreateCommand();
        identity.CommandText = """
            SELECT StorageId, DatabaseRole, SchemaVersion
            FROM DatabaseIdentity
            WHERE SingletonId = 1;
            """;
        using SqliteDataReader reader = identity.ExecuteReader();
        if (!reader.Read() ||
            !Guid.TryParseExact(reader.GetString(0), "D", out Guid storageId) ||
            storageId != _session.StorageId ||
            !string.Equals(reader.GetString(1), DatabaseRole.StorageCatalog.ToString(), StringComparison.Ordinal) ||
            reader.GetInt32(2) < ArchiveSegmentCatalogSqlSchema.RequiredCatalogSchemaVersion)
        {
            throw new InvalidDataException(
                "Archive split Catalog validation requires StorageCatalog v3 or later.");
        }
        int schemaVersion = reader.GetInt32(2);
        reader.Close();
        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
        {
            throw new InvalidDataException(
                "StorageCatalog user_version does not match DatabaseIdentity.");
        }
        ExternalPayloadCatalogSqlSchema.ValidateTables(connection);
        ArchiveSegmentCatalogSqlSchema.ValidateTable(connection);
        token.ThrowIfCancellationRequested();
    }

    private void ValidateExternalAddress(ExternalPayloadAddress address)
    {
        if (address.Sha256.Length != 64 ||
            address.Sha256.Any(character =>
                !((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f'))) ||
            address.SizeBytes < 0 ||
            string.IsNullOrWhiteSpace(address.RelativePath) ||
            Path.IsPathRooted(address.RelativePath))
        {
            throw new InvalidDataException(
                "External payload Catalog contains invalid address metadata.");
        }
        string candidatePath = Path.GetFullPath(
            Path.Combine(_filesRootPath, address.RelativePath));
        if (!candidatePath.StartsWith(_filesRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "External payload Catalog address escapes the configured Files root.");
        }
    }

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

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }

    private void Hit(ArchiveSplitCatalogCheckpoint checkpoint) =>
        _faultInjector?.Invoke(checkpoint);
}
