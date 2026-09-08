using System.Globalization;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

internal sealed class SqlitePendingPolicyMaintenanceReader
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;

    public SqlitePendingPolicyMaintenanceReader(
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

    public ValueTask<bool> HasPendingAsync(CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }

        using SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadOnly);
        ValidateCurrent(connection, token);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT SingletonId FROM PendingPolicyMaintenance LIMIT 2;";
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(false);
        }

        if (reader.GetValue(0) is not long singletonId || singletonId != 1 || reader.Read())
        {
            throw new InvalidDataException("PendingPolicyMaintenance contains invalid marker rows.");
        }

        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(true);
    }

    private void ValidateCurrent(SqliteConnection connection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = ON;";
            command.ExecuteNonQuery();
        }

        int version;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT SingletonId, StorageId, DatabaseRole, SchemaVersion FROM DatabaseIdentity;";
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read() ||
                reader.GetInt64(0) != 1 ||
                !Guid.TryParse(reader.GetString(1), out Guid storageId) ||
                storageId != _session.StorageId ||
                !string.Equals(
                    reader.GetString(2),
                    DatabaseRole.Current.ToString(),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Pending policy maintenance requires the expected Current identity.");
            }

            version = reader.GetInt32(3);
            if (reader.Read())
            {
                throw new InvalidDataException(
                    "Pending policy maintenance requires exactly one Current identity row.");
            }

            if (version < PendingPolicyMaintenanceSqlSchema.MinimumCurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    "Pending policy maintenance requires Current schema v7 or later.");
            }
        }

        using (SqliteCommand userVersion = connection.CreateCommand())
        {
            userVersion.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != version)
            {
                throw new InvalidDataException("Current user_version does not match its identity.");
            }
        }

        PendingPolicyMaintenanceSqlSchema.ValidateTable(connection);
        token.ThrowIfCancellationRequested();
    }
}
