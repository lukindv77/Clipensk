using System.Globalization;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.ExternalFiles;

public sealed class SqliteExternalPayloadAddressIndex : IExternalPayloadAddressIndex
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _catalogDatabasePath;
    private readonly string _filesRootPath;
    private readonly string _filesRootPrefix;

    public SqliteExternalPayloadAddressIndex(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();

        string dataRootPath = Path.GetFullPath(session.DataRootPath);
        _catalogDatabasePath = Path.Combine(dataRootPath, "Current", "storage-catalog.db");
        _filesRootPath = Path.TrimEndingDirectorySeparator(Path.Combine(dataRootPath, "Files"));
        _filesRootPrefix = _filesRootPath + Path.DirectorySeparatorChar;
    }

    public ValueTask<ExternalPayloadAddress?> FindAsync(
        string sha256,
        CancellationToken cancellationToken = default)
    {
        ValidateSha256(sha256);

        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCatalog(SqliteOpenMode.ReadOnly, token);
        ExternalPayloadAddress? result = Find(connection, sha256);
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(result);
    }

    public ValueTask<ExternalPayloadAddress> GetOrAddAsync(
        ExternalPayloadAddress candidate,
        CancellationToken cancellationToken = default)
    {
        ValidateAddress(candidate);

        using CancellationTokenSource linkedCancellation = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCatalog(SqliteOpenMode.ReadWrite, token);
        using SqliteTransaction transaction = connection.BeginTransaction();

        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO ExternalPayloadAddressIndex (
                    Sha256,
                    RelativePath,
                    SizeBytes)
                VALUES ($sha256, $relativePath, $sizeBytes);
                """;
            insert.Parameters.AddWithValue("$sha256", candidate.Sha256);
            insert.Parameters.AddWithValue("$relativePath", candidate.RelativePath);
            insert.Parameters.AddWithValue("$sizeBytes", candidate.SizeBytes);
            insert.ExecuteNonQuery();
        }

        ExternalPayloadAddress? stored = Find(connection, transaction, candidate.Sha256);
        if (stored is null)
        {
            throw new InvalidDataException(
                "External payload address reservation conflicted with existing catalog state.");
        }

        if (stored.SizeBytes != candidate.SizeBytes)
        {
            throw new InvalidDataException(
                "Existing external payload address size conflicts with the candidate payload.");
        }

        // The persisted path wins for a duplicate SHA. A candidate constructed from a
        // later capture date must never relocate an already stored external payload.
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return ValueTask.FromResult(stored);
    }

    private CancellationTokenSource CreateLinkedCancellation(CancellationToken callerToken)
    {
        return CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            callerToken);
    }

    private SqliteConnection OpenValidatedCatalog(
        SqliteOpenMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }

        SqliteConnection connection = _connectionFactory.Open(
            _catalogDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            mode);
        try
        {
            ValidateCatalogDatabase(connection);
            ExternalPayloadCatalogSqlSchema.ValidateTables(connection);
            cancellationToken.ThrowIfCancellationRequested();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void ValidateCatalogDatabase(SqliteConnection connection)
    {
        int schemaVersion;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT StorageId, DatabaseRole, SchemaVersion
                FROM DatabaseIdentity
                WHERE SingletonId = 1;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read() ||
                !Guid.TryParse(reader.GetString(0), out Guid storageId) ||
                storageId != _session.StorageId ||
                !string.Equals(
                    reader.GetString(1),
                    DatabaseRole.StorageCatalog.ToString(),
                    StringComparison.Ordinal) ||
                reader.GetInt32(2) < ExternalPayloadCatalogSqlSchema.RequiredCatalogSchemaVersion)
            {
                throw new InvalidDataException(
                    "External payload index requires the expected StorageCatalog at schema v2 or later.");
            }

            schemaVersion = reader.GetInt32(2);
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
        {
            throw new InvalidDataException(
                "StorageCatalog user_version does not match DatabaseIdentity.");
        }
    }

    private ExternalPayloadAddress? Find(SqliteConnection connection, string sha256)
    {
        return Find(connection, transaction: null, sha256);
    }

    private ExternalPayloadAddress? Find(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sha256)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT RelativePath, SizeBytes
            FROM ExternalPayloadAddressIndex
            WHERE Sha256 = $sha256;
            """;
        command.Parameters.AddWithValue("$sha256", sha256);

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var address = new ExternalPayloadAddress(
            sha256,
            reader.GetString(0),
            reader.GetInt64(1));
        ValidateAddress(address);
        return address;
    }

    private void ValidateAddress(ExternalPayloadAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        ValidateSha256(address.Sha256);

        if (string.IsNullOrWhiteSpace(address.RelativePath) ||
            Path.IsPathRooted(address.RelativePath) ||
            address.SizeBytes < 0)
        {
            throw new InvalidDataException("External payload address has invalid persisted metadata.");
        }

        string candidatePath = Path.GetFullPath(
            Path.Combine(_filesRootPath, address.RelativePath));
        if (!candidatePath.StartsWith(_filesRootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "External payload address escapes the configured Files root.");
        }
    }

    private static void ValidateSha256(string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (sha256.Length != 64 ||
            sha256.Any(character =>
                character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "SHA-256 must be a lowercase 64-character hexadecimal string.",
                nameof(sha256));
        }
    }
}
