using System.Globalization;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

public sealed class SqlitePendingPolicyMaintenanceRepository
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;

    public SqlitePendingPolicyMaintenanceRepository(
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

    public ValueTask<PendingPolicyMaintenanceOperation?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadOnly, token);
        PendingPolicyMaintenanceOperation? operation = ReadOperation(connection, transaction: null, token);
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(operation);
    }

    public async ValueTask<PendingPolicyMaintenanceOperation> StartAsync(
        string operationKind,
        string stateJson,
        CancellationToken cancellationToken = default)
    {
        ValidateRequiredText(operationKind, nameof(operationKind));
        ValidateRequiredText(stateJson, nameof(stateJson));

        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);
        using SqliteTransaction transaction = connection.BeginTransaction();
        if (ReadOperation(connection, transaction, token) is not null)
        {
            throw new InvalidOperationException(
                "A pending policy-maintenance operation already exists.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var operation = new PendingPolicyMaintenanceOperation(
            Guid.NewGuid(),
            operationKind,
            stateJson,
            now,
            now);

        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO PendingPolicyMaintenance (
                    SingletonId,
                    OperationId,
                    OperationKind,
                    StateJson,
                    CreatedAtUtc,
                    UpdatedAtUtc)
                VALUES (1, $operationId, $operationKind, $stateJson, $createdAtUtc, $updatedAtUtc);
                """;
            insert.Parameters.AddWithValue("$operationId", operation.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$operationKind", operation.OperationKind);
            insert.Parameters.AddWithValue("$stateJson", operation.StateJson);
            insert.Parameters.AddWithValue("$createdAtUtc", FormatUtc(operation.CreatedAtUtc));
            insert.Parameters.AddWithValue("$updatedAtUtc", FormatUtc(operation.UpdatedAtUtc));
            insert.ExecuteNonQuery();
        }

        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return operation;
    }

    public async ValueTask<PendingPolicyMaintenanceOperation> UpdateStateAsync(
        Guid operationId,
        string stateJson,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId, nameof(operationId));
        ValidateRequiredText(stateJson, nameof(stateJson));

        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);
        using SqliteTransaction transaction = connection.BeginTransaction();
        PendingPolicyMaintenanceOperation current =
            ReadRequiredExactOperation(connection, transaction, operationId, token);

        DateTimeOffset updatedAtUtc = DateTimeOffset.UtcNow;
        if (updatedAtUtc < current.UpdatedAtUtc)
        {
            updatedAtUtc = current.UpdatedAtUtc;
        }

        using (SqliteCommand update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE PendingPolicyMaintenance
                SET StateJson = $stateJson,
                    UpdatedAtUtc = $updatedAtUtc
                WHERE SingletonId = 1 AND OperationId = $operationId COLLATE BINARY;
                """;
            update.Parameters.AddWithValue("$stateJson", stateJson);
            update.Parameters.AddWithValue("$updatedAtUtc", FormatUtc(updatedAtUtc));
            update.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    "Pending policy-maintenance operation changed before state update.");
            }
        }

        var updated = current with
        {
            StateJson = stateJson,
            UpdatedAtUtc = updatedAtUtc,
        };

        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return updated;
    }

    public async ValueTask ClearAsync(
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ValidateOperationId(operationId, nameof(operationId));

        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);
        using SqliteTransaction transaction = connection.BeginTransaction();
        _ = ReadRequiredExactOperation(connection, transaction, operationId, token);

        using (SqliteCommand delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM PendingPolicyMaintenance
                WHERE SingletonId = 1 AND OperationId = $operationId COLLATE BINARY;
                """;
            delete.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
            if (delete.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    "Pending policy-maintenance operation changed before clear.");
            }
        }

        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private PendingPolicyMaintenanceOperation ReadRequiredExactOperation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        CancellationToken token)
    {
        PendingPolicyMaintenanceOperation? current = ReadOperation(connection, transaction, token);
        if (current is null)
        {
            throw new InvalidOperationException(
                "No pending policy-maintenance operation exists.");
        }

        if (current.OperationId != operationId)
        {
            throw new InvalidOperationException(
                "Pending policy-maintenance operation ownership does not match the requested operation.");
        }

        return current;
    }

    private static PendingPolicyMaintenanceOperation? ReadOperation(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT SingletonId, OperationId, OperationKind, StateJson, CreatedAtUtc, UpdatedAtUtc
            FROM PendingPolicyMaintenance
            ORDER BY SingletonId
            LIMIT 2;
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        if (reader.GetInt64(0) != 1 ||
            !Guid.TryParseExact(reader.GetString(1), "D", out Guid operationId) ||
            operationId == Guid.Empty)
        {
            throw new InvalidDataException(
                "Pending policy-maintenance operation identity is invalid.");
        }

        string operationKind = reader.GetString(2);
        string stateJson = reader.GetString(3);
        if (string.IsNullOrWhiteSpace(operationKind) || string.IsNullOrWhiteSpace(stateJson))
        {
            throw new InvalidDataException(
                "Pending policy-maintenance operation contains empty required state.");
        }

        DateTimeOffset createdAtUtc = ParseUtc(reader.GetString(4), "CreatedAtUtc");
        DateTimeOffset updatedAtUtc = ParseUtc(reader.GetString(5), "UpdatedAtUtc");
        if (updatedAtUtc < createdAtUtc)
        {
            throw new InvalidDataException(
                "Pending policy-maintenance timestamps are not monotonic.");
        }

        if (reader.Read())
        {
            throw new InvalidDataException(
                "PendingPolicyMaintenance contains more than one operation row.");
        }

        token.ThrowIfCancellationRequested();
        return new PendingPolicyMaintenanceOperation(
            operationId,
            operationKind,
            stateJson,
            createdAtUtc,
            updatedAtUtc);
    }

    private CancellationTokenSource CreateLinkedCancellation(CancellationToken callerToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(_session.CancellationToken, callerToken);

    private SqliteConnection OpenValidatedCurrent(
        SqliteOpenMode mode,
        CancellationToken token)
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
            using (SqliteCommand foreignKeys = connection.CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
                foreignKeys.ExecuteNonQuery();
            }

            int version;
            using (SqliteCommand identity = connection.CreateCommand())
            {
                identity.CommandText = """
                    SELECT SingletonId, StorageId, DatabaseRole, SchemaVersion
                    FROM DatabaseIdentity;
                    """;
                using SqliteDataReader reader = identity.ExecuteReader();
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
                if (reader.Read() || version < PendingPolicyMaintenanceSqlSchema.MinimumCurrentSchemaVersion)
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
                    throw new InvalidDataException(
                        "Current user_version does not match its identity.");
                }
            }

            PendingPolicyMaintenanceSqlSchema.ValidateTable(connection);
            token.ThrowIfCancellationRequested();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtc(string value, string fieldName)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed) ||
            parsed.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                $"Pending policy-maintenance {fieldName} is not a canonical UTC timestamp.");
        }

        return parsed;
    }

    private static void ValidateOperationId(Guid operationId, string parameterName)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Operation id cannot be empty.");
        }
    }

    private static void ValidateRequiredText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }
    }
}
