using System.Globalization;
using Clipensk.Core.Storage;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

public sealed class SqliteCustomBinaryFormatConfigurationRepository :
    ICustomBinaryFormatConfigurationRepository
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;

    public SqliteCustomBinaryFormatConfigurationRepository(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _currentDatabasePath = Path.Combine(
            Path.GetFullPath(session.DataRootPath),
            "Current",
            "current.db");
    }

    public ValueTask<string?> ReadFileExtensionAsync(
        string formatName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);
        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linked.Token;
        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadOnly, token);
        string? extension = ReadExtension(connection, transaction: null, formatName, token);
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(extension);
    }

    public ValueTask InitializeAsync(
        string formatName,
        string fileExtension,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(formatName);
        string normalizedExtension = ExternalPayloadAddressFactory
            .NormalizeCustomBinaryExtension(fileExtension);

        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linked.Token;
        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        if (ReadExtension(connection, transaction, formatName, token) is not null)
        {
            throw new InvalidOperationException(
                $"Custom binary format '{formatName}' already has a configured file extension; changes require cleanup.");
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO CustomBinaryFormatConfiguration (FormatName, FileExtension)
                VALUES ($formatName, $fileExtension);
                """;
            command.Parameters.AddWithValue("$formatName", formatName);
            command.Parameters.AddWithValue("$fileExtension", normalizedExtension);
            command.ExecuteNonQuery();
        }

        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return ValueTask.CompletedTask;
    }

    private CancellationTokenSource CreateLinkedCancellation(CancellationToken callerToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(_session.CancellationToken, callerToken);

    private SqliteConnection OpenValidatedCurrent(SqliteOpenMode mode, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }

        SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            mode);
        try
        {
            token.ThrowIfCancellationRequested();
            ValidateIdentity(connection);
            CustomBinaryFormatConfigurationSqlSchema.ValidateTable(connection);
            token.ThrowIfCancellationRequested();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void ValidateIdentity(SqliteConnection connection)
    {
        int version;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT SingletonId, StorageId, DatabaseRole, SchemaVersion FROM DatabaseIdentity;";
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(0) != 1 ||
                !Guid.TryParse(reader.GetString(1), out Guid storageId) || storageId != _session.StorageId ||
                !string.Equals(reader.GetString(2), DatabaseRole.Current.ToString(), StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Custom binary format configuration requires the expected Current identity.");
            }

            version = reader.GetInt32(3);
            if (version < CustomBinaryFormatConfigurationSqlSchema.MinimumCurrentSchemaVersion || reader.Read())
            {
                throw new InvalidDataException(
                    "Custom binary format configuration requires Current schema v6 or later.");
            }
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != version)
        {
            throw new InvalidDataException(
                "Current user_version does not match its custom binary configuration identity.");
        }
    }

    private static string? ReadExtension(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string formatName,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT FileExtension
            FROM CustomBinaryFormatConfiguration
            WHERE FormatName = $formatName COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$formatName", formatName);
        object? value = command.ExecuteScalar();
        if (value is null or DBNull)
        {
            return null;
        }

        if (value is not string stored)
        {
            throw new InvalidDataException(
                "Custom binary file extension has an invalid storage type.");
        }

        string normalized;
        try
        {
            normalized = ExternalPayloadAddressFactory.NormalizeCustomBinaryExtension(stored);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Custom binary file extension is invalid.", exception);
        }

        if (!string.Equals(stored, normalized, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Custom binary file extension is not stored in canonical form.");
        }

        token.ThrowIfCancellationRequested();
        return stored;
    }
}
