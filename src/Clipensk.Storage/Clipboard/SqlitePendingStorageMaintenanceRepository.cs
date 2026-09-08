using System.Globalization;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

internal sealed class SqlitePendingStorageMaintenanceRepository
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;

    public SqlitePendingStorageMaintenanceRepository(
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

        using SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadOnly);
        ValidateCurrent(connection);
        PendingStorageMaintenanceSqlSchema.ValidateTable(connection);
        token.ThrowIfCancellationRequested();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM PendingStorageMaintenance WHERE SingletonId = 1);";
        bool hasPending = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(hasPending);
    }

    private void ValidateCurrent(SqliteConnection connection)
    {
        using SqliteCommand identity = connection.CreateCommand();
        identity.CommandText = """
            SELECT StorageId, DatabaseRole, SchemaVersion
            FROM DatabaseIdentity
            WHERE SingletonId = 1;
            """;
        using SqliteDataReader reader = identity.ExecuteReader();
        if (!reader.Read() ||
            !Guid.TryParse(reader.GetString(0), out Guid storageId) ||
            storageId != _session.StorageId ||
            !string.Equals(reader.GetString(1), DatabaseRole.Current.ToString(), StringComparison.Ordinal) ||
            reader.GetInt32(2) < PendingStorageMaintenanceSqlSchema.MinimumCurrentSchemaVersion)
        {
            throw new InvalidDataException("Pending maintenance repository requires Current schema v7 or later.");
        }

        int schemaVersion = reader.GetInt32(2);
        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
        {
            throw new InvalidDataException("Current user_version does not match pending-maintenance schema contract.");
        }
    }
}
