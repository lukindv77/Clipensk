using System.Globalization;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.ExternalFiles;

public sealed class ProtectedExternalPayloadCatalogRebuildService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;
    private readonly string _catalogDatabasePath;
    private readonly string _archiveDirectoryPath;
    private readonly string _filesRootPath;
    private readonly string _filesRootPrefix;

    public ProtectedExternalPayloadCatalogRebuildService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();

        string root = Path.GetFullPath(session.DataRootPath);
        _currentDatabasePath = Path.Combine(root, "Current", "current.db");
        _catalogDatabasePath = Path.Combine(root, "Current", "storage-catalog.db");
        _archiveDirectoryPath = Path.Combine(root, "Archive");
        _filesRootPath = Path.TrimEndingDirectorySeparator(Path.Combine(root, "Files"));
        _filesRootPrefix = _filesRootPath + Path.DirectorySeparatorChar;
    }

    public async Task<IReadOnlyList<ExternalPayloadAddress>> RebuildAsync(
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                _session.CancellationToken,
                cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var projection = new ProjectionAccumulator();

        // Current must be observed before Archive. Current->Archive transfer commits
        // the Archive copy before purging Current, so this order cannot lose a
        // durable external reference when transfer overlaps the rebuild.
        await Task.Run(
            () => ScanHistoryDatabase(
                _currentDatabasePath,
                DatabaseRole.Current,
                expectedDatabaseId: null,
                projection,
                token),
            CancellationToken.None).ConfigureAwait(false);

        string[] archiveNamesBefore = await Task.Run(
            () => EnumerateArchiveNames(token),
            CancellationToken.None).ConfigureAwait(false);

        var archiveService = new ProtectedArchiveDatabaseService(
            _session,
            _connectionFactory);
        foreach (string fileNameText in archiveNamesBefore)
        {
            token.ThrowIfCancellationRequested();
            if (!ArchiveFileName.TryParse(fileNameText, out ArchiveFileName archiveFileName) ||
                !string.Equals(fileNameText, archiveFileName.FileName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Archive file '{fileNameText}' does not use the canonical Clipensk file name.");
            }

            DatabaseIdentity identityBefore = await archiveService
                .ValidateAsync(archiveFileName, token)
                .ConfigureAwait(false);

            string archivePath = Path.Combine(_archiveDirectoryPath, archiveFileName.FileName);
            await Task.Run(
                () => ScanHistoryDatabase(
                    archivePath,
                    DatabaseRole.Archive,
                    identityBefore.DatabaseId,
                    projection,
                    token),
                CancellationToken.None).ConfigureAwait(false);

            DatabaseIdentity identityAfter = await archiveService
                .ValidateAsync(archiveFileName, token)
                .ConfigureAwait(false);
            if (identityBefore != identityAfter)
            {
                throw new InvalidOperationException(
                    $"Archive '{archiveFileName.FileName}' identity changed during external payload catalog rebuild.");
            }
        }

        string[] archiveNamesAfter = await Task.Run(
            () => EnumerateArchiveNames(token),
            CancellationToken.None).ConfigureAwait(false);
        if (!archiveNamesBefore.SequenceEqual(archiveNamesAfter, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Archive directory changed during external payload catalog rebuild; retry the operation.");
        }

        ExternalPayloadAddress[] addresses = projection
            .Addresses
            .OrderBy(address => address.Sha256, StringComparer.Ordinal)
            .ToArray();

        await Task.Run(
            () => ReplaceCatalogProjection(addresses, token),
            CancellationToken.None).ConfigureAwait(false);

        return Array.AsReadOnly(addresses);
    }

    private void ScanHistoryDatabase(
        string databasePath,
        DatabaseRole expectedRole,
        Guid? expectedDatabaseId,
        ProjectionAccumulator projection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureActiveSession();

        using SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadOnly);
        ValidateHistoryDatabase(
            connection,
            expectedRole,
            expectedDatabaseId,
            cancellationToken);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                PayloadKind,
                CanonicalByteCount,
                InlineCanonicalText IS NULL,
                SearchText IS NULL,
                ExternalSha256,
                ExternalRelativePath,
                ExternalSizeBytes
            FROM ClipboardHistoryPayload
            ORDER BY EventId COLLATE BINARY, PayloadOrder;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadPayloadReference(reader, projection);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private void ValidateHistoryDatabase(
        SqliteConnection connection,
        DatabaseRole expectedRole,
        Guid? expectedDatabaseId,
        CancellationToken cancellationToken)
    {
        int schemaVersion;
        using (SqliteCommand identity = connection.CreateCommand())
        {
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
                !string.Equals(
                    reader.GetString(2),
                    expectedRole.ToString(),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"External payload catalog rebuild requires the expected {expectedRole} database.");
            }

            schemaVersion = reader.GetInt32(3);
            if ((expectedRole == DatabaseRole.Current &&
                 schemaVersion < ClipboardHistorySqlSchema.RequiredCurrentSchemaVersion) ||
                (expectedRole == DatabaseRole.Archive &&
                 schemaVersion != ProtectedArchiveDatabaseService.ArchiveSchemaVersion))
            {
                throw new InvalidDataException(
                    $"Database role {expectedRole} does not expose the required clipboard history schema version.");
            }
        }

        using (SqliteCommand userVersion = connection.CreateCommand())
        {
            userVersion.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
            {
                throw new InvalidDataException(
                    $"Database role {expectedRole} user_version does not match DatabaseIdentity.");
            }
        }

        ClipboardHistorySqlSchema.ValidateTables(connection);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void ReadPayloadReference(
        SqliteDataReader reader,
        ProjectionAccumulator projection)
    {
        string payloadKind = reader.GetString(0);
        long canonicalByteCount = reader.GetInt64(1);
        bool inlineIsNull = reader.GetInt32(2) == 1;
        bool searchTextIsNull = reader.GetInt32(3) == 1;
        bool externalShaIsNull = reader.IsDBNull(4);
        bool externalPathIsNull = reader.IsDBNull(5);
        bool externalSizeIsNull = reader.IsDBNull(6);

        if (canonicalByteCount < 0)
        {
            throw new InvalidDataException(
                "History contains a negative canonical byte count during external payload catalog rebuild.");
        }

        if (payloadKind is "Text" or "Link" or "StorageItems")
        {
            if (inlineIsNull || !externalShaIsNull || !externalPathIsNull || !externalSizeIsNull)
            {
                throw new InvalidDataException(
                    "History inline payload representation is invalid during external payload catalog rebuild.");
            }
            return;
        }

        if (payloadKind is not ("PngImage" or "CustomBinary"))
        {
            throw new InvalidDataException(
                "History contains an unsupported payload kind during external payload catalog rebuild.");
        }

        if (!inlineIsNull || !searchTextIsNull ||
            externalShaIsNull || externalPathIsNull || externalSizeIsNull)
        {
            throw new InvalidDataException(
                "History external payload representation is incomplete during external payload catalog rebuild.");
        }

        var address = new ExternalPayloadAddress(
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6));
        if (address.SizeBytes != canonicalByteCount)
        {
            throw new InvalidDataException(
                "History external payload size does not match its canonical byte count.");
        }

        ValidateAddress(address);
        projection.Add(address);
    }

    private void ValidateAddress(ExternalPayloadAddress address)
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
                "History contains invalid external payload address metadata.");
        }

        string candidatePath;
        try
        {
            candidatePath = Path.GetFullPath(
                Path.Combine(_filesRootPath, address.RelativePath));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException(
                "History external payload relative path is invalid.",
                exception);
        }

        if (!candidatePath.StartsWith(_filesRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "History external payload address escapes the configured Files root.");
        }
    }

    private string[] EnumerateArchiveNames(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_archiveDirectoryPath))
        {
            return [];
        }

        var names = new List<string>();
        foreach (string path in Directory.EnumerateFiles(
                     _archiveDirectoryPath,
                     "archive_*.db",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(path);
            if (!ArchiveFileName.TryParse(fileName, out ArchiveFileName parsed) ||
                !string.Equals(fileName, parsed.FileName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Archive file '{fileName}' does not use the canonical Clipensk file name.");
            }
            names.Add(fileName);
        }

        return names
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private void ReplaceCatalogProjection(
        IReadOnlyList<ExternalPayloadAddress> addresses,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureActiveSession();

        using SqliteConnection connection = _connectionFactory.Open(
            _catalogDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadWrite);
        ValidateCatalogDatabase(connection, cancellationToken);

        using SqliteTransaction transaction = connection.BeginTransaction();
        using (SqliteCommand delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM ExternalPayloadAddressIndex;";
            delete.ExecuteNonQuery();
        }

        foreach (ExternalPayloadAddress address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ExternalPayloadAddressIndex (
                    Sha256,
                    RelativePath,
                    SizeBytes)
                VALUES ($sha256, $relativePath, $sizeBytes);
                """;
            insert.Parameters.AddWithValue("$sha256", address.Sha256);
            insert.Parameters.AddWithValue("$relativePath", address.RelativePath);
            insert.Parameters.AddWithValue("$sizeBytes", address.SizeBytes);
            insert.ExecuteNonQuery();
        }

        // Last cancellation boundary. A committed replacement remains success.
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void ValidateCatalogDatabase(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        int schemaVersion;
        using (SqliteCommand identity = connection.CreateCommand())
        {
            identity.CommandText = """
                SELECT StorageId, DatabaseRole, SchemaVersion
                FROM DatabaseIdentity
                WHERE SingletonId = 1;
                """;
            using SqliteDataReader reader = identity.ExecuteReader();
            if (!reader.Read() ||
                !Guid.TryParseExact(reader.GetString(0), "D", out Guid storageId) ||
                storageId != _session.StorageId ||
                !string.Equals(
                    reader.GetString(1),
                    DatabaseRole.StorageCatalog.ToString(),
                    StringComparison.Ordinal) ||
                reader.GetInt32(2) < ArchiveSegmentCatalogSqlSchema.RequiredCatalogSchemaVersion)
            {
                throw new InvalidDataException(
                    "External payload catalog rebuild requires the expected StorageCatalog v3 or later.");
            }
            schemaVersion = reader.GetInt32(2);
        }

        using (SqliteCommand userVersion = connection.CreateCommand())
        {
            userVersion.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
            {
                throw new InvalidDataException(
                    "StorageCatalog user_version does not match DatabaseIdentity during external payload rebuild.");
            }
        }

        ExternalPayloadCatalogSqlSchema.ValidateTables(connection);
        ArchiveSegmentCatalogSqlSchema.ValidateTable(connection);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void EnsureActiveSession()
    {
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }
    }

    private sealed class ProjectionAccumulator
    {
        private readonly Dictionary<string, ExternalPayloadAddress> _bySha =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _shaByRelativePath =
            new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<ExternalPayloadAddress> Addresses => _bySha.Values;

        public void Add(ExternalPayloadAddress address)
        {
            if (_bySha.TryGetValue(address.Sha256, out ExternalPayloadAddress? existing))
            {
                if (existing != address)
                {
                    throw new InvalidDataException(
                        "The same external payload SHA-256 maps to conflicting persisted address metadata.");
                }
                return;
            }

            if (_shaByRelativePath.TryGetValue(address.RelativePath, out string? existingSha) &&
                !string.Equals(existingSha, address.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "One external payload relative path maps to multiple SHA-256 values.");
            }

            _bySha.Add(address.Sha256, address);
            _shaByRelativePath[address.RelativePath] = address.Sha256;
        }
    }
}
