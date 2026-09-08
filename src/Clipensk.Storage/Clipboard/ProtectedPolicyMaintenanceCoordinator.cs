using System.Globalization;
using System.Text.Json;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

/// <summary>
/// Creates or resumes one durable global capture-policy maintenance operation while owning the
/// protected-storage mutation gate. App runtime quiescence is a separate caller responsibility
/// and must precede Begin for destructive policy maintenance.
/// </summary>
public sealed class ProtectedPolicyMaintenanceCoordinator
{
    public const string GlobalCapturePolicyChangeOperationKind = "GlobalCapturePolicyChange";

    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;

    public ProtectedPolicyMaintenanceCoordinator(
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

    public async ValueTask<ProtectedPolicyMaintenanceSession> BeginGlobalCapturePolicyChangeAsync(
        string initialStateJson,
        CancellationToken cancellationToken = default)
    {
        ValidateStateJsonArgument(initialStateJson);
        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        bool ownershipTransferred = false;
        try
        {
            PendingPolicyMaintenanceSnapshot snapshot = BeginCore(initialStateJson, token);
            var result = new ProtectedPolicyMaintenanceSession(this, mutationLease, snapshot);
            ownershipTransferred = true;
            return result;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                mutationLease.Dispose();
            }
        }
    }

    public async ValueTask<ProtectedPolicyMaintenanceSession?> ResumeGlobalCapturePolicyChangeAsync(
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        bool ownershipTransferred = false;
        try
        {
            PendingPolicyMaintenanceSnapshot? snapshot = ReadCore(token);
            if (snapshot is null)
            {
                return null;
            }

            if (!string.Equals(
                    snapshot.OperationKind,
                    GlobalCapturePolicyChangeOperationKind,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Pending policy maintenance operation kind is not supported by global-policy recovery.");
            }

            token.ThrowIfCancellationRequested();
            var result = new ProtectedPolicyMaintenanceSession(this, mutationLease, snapshot);
            ownershipTransferred = true;
            return result;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                mutationLease.Dispose();
            }
        }
    }

    internal ValueTask<PendingPolicyMaintenanceSnapshot> UpdateOwnedStateAsync(
        Guid operationId,
        string stateJson,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Policy maintenance operation id cannot be empty.", nameof(operationId));
        }
        ValidateStateJsonArgument(stateJson);

        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linked.Token;
        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        PendingPolicyMaintenanceSnapshot current = RequireOwnedMarker(
            ReadPending(connection, transaction, token),
            operationId);
        DateTimeOffset updatedAtUtc = DateTimeOffset.UtcNow;
        if (updatedAtUtc < current.UpdatedAtUtc)
        {
            updatedAtUtc = current.UpdatedAtUtc;
        }

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE PendingPolicyMaintenance
                SET StateJson = $stateJson,
                    UpdatedAtUtc = $updatedAtUtc
                WHERE SingletonId = 1 AND OperationId = $operationId;
                """;
            command.Parameters.AddWithValue("$stateJson", stateJson);
            command.Parameters.AddWithValue(
                "$updatedAtUtc",
                updatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    "Pending policy maintenance marker ownership changed before state update.");
            }
        }

        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return ValueTask.FromResult(current with
        {
            StateJson = stateJson,
            UpdatedAtUtc = updatedAtUtc,
        });
    }

    internal ValueTask CompleteOwnedAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Policy maintenance operation id cannot be empty.", nameof(operationId));
        }

        using CancellationTokenSource linked = CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linked.Token;
        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        _ = RequireOwnedMarker(ReadPending(connection, transaction, token), operationId);
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM PendingPolicyMaintenance
                WHERE SingletonId = 1 AND OperationId = $operationId;
                """;
            command.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    "Pending policy maintenance marker ownership changed before completion.");
            }
        }

        token.ThrowIfCancellationRequested();
        transaction.Commit();
        // A committed completion remains successful even if cancellation arrives afterwards.
        return ValueTask.CompletedTask;
    }

    private PendingPolicyMaintenanceSnapshot BeginCore(
        string stateJson,
        CancellationToken token)
    {
        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadWrite, token);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        if (ReadPending(connection, transaction, token) is not null)
        {
            throw new InvalidOperationException(
                "A policy maintenance operation is already pending and must be resumed or recovered.");
        }

        Guid operationId = Guid.NewGuid();
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        var snapshot = new PendingPolicyMaintenanceSnapshot(
            operationId,
            GlobalCapturePolicyChangeOperationKind,
            stateJson,
            nowUtc,
            nowUtc);

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO PendingPolicyMaintenance (
                    SingletonId, OperationId, OperationKind, StateJson, CreatedAtUtc, UpdatedAtUtc)
                VALUES (
                    1, $operationId, $operationKind, $stateJson, $createdAtUtc, $updatedAtUtc);
                """;
            command.Parameters.AddWithValue("$operationId", operationId.ToString("D"));
            command.Parameters.AddWithValue("$operationKind", GlobalCapturePolicyChangeOperationKind);
            command.Parameters.AddWithValue("$stateJson", stateJson);
            command.Parameters.AddWithValue(
                "$createdAtUtc",
                nowUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue(
                "$updatedAtUtc",
                nowUtc.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        token.ThrowIfCancellationRequested();
        transaction.Commit();
        // Do not demote a durable marker to cancellation after COMMIT.
        return snapshot;
    }

    private PendingPolicyMaintenanceSnapshot? ReadCore(CancellationToken token)
    {
        using SqliteConnection connection = OpenValidatedCurrent(SqliteOpenMode.ReadOnly, token);
        PendingPolicyMaintenanceSnapshot? snapshot = ReadPending(connection, transaction: null, token);
        token.ThrowIfCancellationRequested();
        return snapshot;
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
            using (SqliteCommand foreignKeys = connection.CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
                foreignKeys.ExecuteNonQuery();
            }

            ValidateCurrentIdentity(connection);
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

    private void ValidateCurrentIdentity(SqliteConnection connection)
    {
        int version;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT SingletonId, StorageId, DatabaseRole, SchemaVersion FROM DatabaseIdentity;";
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
                    "Policy maintenance requires the expected Current database identity.");
            }

            version = reader.GetInt32(3);
            if (version < PendingPolicyMaintenanceSqlSchema.MinimumCurrentSchemaVersion || reader.Read())
            {
                throw new InvalidDataException(
                    "Policy maintenance requires Current schema v7 or later.");
            }
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != version)
        {
            throw new InvalidDataException("Current user_version does not match its identity.");
        }
    }

    private static PendingPolicyMaintenanceSnapshot? ReadPending(
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

        if (reader.GetValue(0) is not long singletonId || singletonId != 1 ||
            reader.GetValue(1) is not string operationIdText ||
            !Guid.TryParse(operationIdText, out Guid operationId) || operationId == Guid.Empty ||
            reader.GetValue(2) is not string operationKind || string.IsNullOrWhiteSpace(operationKind) ||
            reader.GetValue(3) is not string stateJson || !IsValidJson(stateJson) ||
            reader.GetValue(4) is not string createdAtText ||
            !TryParseUtcTimestamp(createdAtText, out DateTimeOffset createdAtUtc) ||
            reader.GetValue(5) is not string updatedAtText ||
            !TryParseUtcTimestamp(updatedAtText, out DateTimeOffset updatedAtUtc) ||
            updatedAtUtc < createdAtUtc)
        {
            throw new InvalidDataException("Pending policy maintenance marker contains invalid data.");
        }

        if (reader.Read())
        {
            throw new InvalidDataException(
                "Pending policy maintenance table contains more than one marker row.");
        }

        token.ThrowIfCancellationRequested();
        return new PendingPolicyMaintenanceSnapshot(
            operationId,
            operationKind,
            stateJson,
            createdAtUtc,
            updatedAtUtc);
    }

    private static PendingPolicyMaintenanceSnapshot RequireOwnedMarker(
        PendingPolicyMaintenanceSnapshot? snapshot,
        Guid operationId)
    {
        if (snapshot is null)
        {
            throw new InvalidOperationException("Pending policy maintenance marker is missing.");
        }
        if (snapshot.OperationId != operationId)
        {
            throw new InvalidOperationException(
                "Pending policy maintenance marker belongs to another operation.");
        }
        if (!string.Equals(
                snapshot.OperationKind,
                GlobalCapturePolicyChangeOperationKind,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Pending policy maintenance marker has an unsupported operation kind.");
        }
        return snapshot;
    }

    private static void ValidateStateJsonArgument(string stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson))
        {
            throw new ArgumentException(
                "Policy maintenance state JSON cannot be empty.",
                nameof(stateJson));
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(stateJson);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "Policy maintenance state must be valid JSON.",
                nameof(stateJson),
                exception);
        }
    }

    private static bool IsValidJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseUtcTimestamp(string value, out DateTimeOffset timestamp)
    {
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out timestamp) ||
            timestamp.Offset != TimeSpan.Zero)
        {
            timestamp = default;
            return false;
        }
        return true;
    }
}
