using System.Globalization;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

public enum GlobalCapturePolicyMaintenancePhase
{
    ArchiveCleanup,
    CatalogRebuild,
    TrashCollection,
}

public sealed record GlobalCapturePolicyMaintenanceState(
    Guid OperationId,
    GlobalCapturePolicyMaintenancePhase Phase,
    DateTimeOffset StartedAtUtc);

public sealed class SqliteGlobalCapturePolicyMaintenanceRepository
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;

    public SqliteGlobalCapturePolicyMaintenanceRepository(
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

    public ValueTask<GlobalCapturePolicyMaintenanceState?> ReadAsync(
        CancellationToken cancellationToken = default)
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
        EnableForeignKeys(connection);
        ValidateCurrentDatabase(connection);
        GlobalCapturePolicyMaintenanceSqlSchema.ValidateTable(connection);

        using SqliteCommand count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM GlobalCapturePolicyMaintenance;";
        long rowCount = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (rowCount == 0)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<GlobalCapturePolicyMaintenanceState?>(null);
        }
        if (rowCount != 1)
        {
            throw new InvalidDataException(
                "GlobalCapturePolicyMaintenance must contain zero or one row.");
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT SingletonId, OperationId, Phase, StartedAtUtc
            FROM GlobalCapturePolicyMaintenance
            WHERE SingletonId = 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != 1)
        {
            throw new InvalidDataException(
                "GlobalCapturePolicyMaintenance singleton row is invalid.");
        }

        string operationText = reader.GetString(1);
        if (!Guid.TryParseExact(operationText, "D", out Guid operationId) ||
            operationId == Guid.Empty ||
            !string.Equals(operationText, operationId.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "GlobalCapturePolicyMaintenance contains an invalid OperationId.");
        }

        string phaseText = reader.GetString(2);
        if (!Enum.TryParse(phaseText, ignoreCase: false, out GlobalCapturePolicyMaintenancePhase phase) ||
            !string.Equals(phaseText, phase.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "GlobalCapturePolicyMaintenance contains an invalid phase.");
        }

        string startedText = reader.GetString(3);
        if (!DateTimeOffset.TryParse(
                startedText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset startedAtUtc) ||
            startedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "GlobalCapturePolicyMaintenance contains an invalid UTC start timestamp.");
        }

        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult<GlobalCapturePolicyMaintenanceState?>(
            new GlobalCapturePolicyMaintenanceState(operationId, phase, startedAtUtc));
    }

    private void ValidateCurrentDatabase(SqliteConnection connection)
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
            reader.GetInt32(2) < GlobalCapturePolicyMaintenanceSqlSchema.MinimumCurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "Policy maintenance repository requires Current schema v7 or later.");
        }

        int schemaVersion = reader.GetInt32(2);
        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
        {
            throw new InvalidDataException(
                "Current database user_version does not match the policy maintenance schema contract.");
        }
    }

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }
}