using System.Globalization;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

public sealed record GlobalPolicyMaintenanceCompletionResult(
    PendingPolicyMaintenanceOperation Operation,
    bool WasCompletionAlreadyCompleted);

internal enum GlobalPolicyMaintenanceCompletionCheckpoint
{
    BeforeCompletionCommit,
    AfterCompletionCommit,
    BeforeMarkerClearCommit,
    AfterMarkerClearCommit,
}

/// <summary>
/// Finalizes one committed global capture-policy maintenance operation. Completion is first
/// published durably in its own Current transaction. The pending marker is then removed in a
/// second transaction so a crash or cancellation between those commits remains unambiguously
/// resumable from a durable completed marker.
/// </summary>
public sealed class ProtectedGlobalPolicyMaintenanceCompletionService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;
    private readonly Action<GlobalPolicyMaintenanceCompletionCheckpoint>? _checkpoint;

    public ProtectedGlobalPolicyMaintenanceCompletionService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
        : this(session, connectionFactory, checkpoint: null)
    {
    }

    internal ProtectedGlobalPolicyMaintenanceCompletionService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory,
        Action<GlobalPolicyMaintenanceCompletionCheckpoint>? checkpoint)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _checkpoint = checkpoint;
        _currentDatabasePath = Path.Combine(
            Path.GetFullPath(session.DataRootPath),
            "Current",
            "current.db");
    }

    public async Task<GlobalPolicyMaintenanceCompletionResult> CompleteAsync(
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        CurrentSnapshot snapshot = await ReadCurrentSnapshotAsync(token).ConfigureAwait(false);
        return await Task.Run(
                () => CompleteCore(snapshot, token),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<CurrentSnapshot> ReadCurrentSnapshotAsync(CancellationToken token)
    {
        var pendingRepository = new SqlitePendingPolicyMaintenanceRepository(
            _session,
            _connectionFactory);
        PendingPolicyMaintenanceOperation operation =
            await pendingRepository.ReadAsync(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Global policy-maintenance completion requires a pending operation.");

        if (!string.Equals(
                operation.OperationKind,
                ProtectedCurrentPolicyMaintenanceService.OperationKind,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending policy-maintenance operation is not a global capture-policy change.");
        }

        GlobalPolicyMaintenanceState state =
            GlobalPolicyMaintenanceStateCodec.Parse(operation.StateJson);
        RequirePriorPhasesCompleted(state);

        ClipboardCapturePolicy globalPolicy =
            await new SqliteGlobalClipboardCapturePolicyRepository(_session, _connectionFactory)
                .ReadAsync(token)
                .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "Global policy-maintenance completion requires the committed global capture policy.");

        if (!string.Equals(
                GlobalPolicyMaintenanceStateCodec.ComputePolicyFingerprint(globalPolicy),
                state.PolicyFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The committed global capture policy does not match its maintenance marker.");
        }

        token.ThrowIfCancellationRequested();
        return new CurrentSnapshot(operation, state);
    }

    private GlobalPolicyMaintenanceCompletionResult CompleteCore(
        CurrentSnapshot snapshot,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenValidatedCurrent(token);

        PendingPolicyMaintenanceOperation completedOperation = snapshot.Operation;
        bool wasCompletionAlreadyCompleted =
            snapshot.State.Completion == GlobalPolicyMaintenanceState.Completed;

        if (!wasCompletionAlreadyCompleted)
        {
            using SqliteTransaction completionTransaction =
                connection.BeginTransaction(deferred: false);
            PendingPolicyMaintenanceOperation current =
                ReadRequiredExactOperation(connection, completionTransaction, snapshot, token);
            GlobalPolicyMaintenanceState currentState =
                GlobalPolicyMaintenanceStateCodec.Parse(current.StateJson);
            RequirePriorPhasesCompleted(currentState);

            if (currentState.Completion == GlobalPolicyMaintenanceState.Completed)
            {
                completedOperation = current;
                wasCompletionAlreadyCompleted = true;
            }
            else
            {
                GlobalPolicyMaintenanceState completedState =
                    currentState with { Completion = GlobalPolicyMaintenanceState.Completed };
                completedOperation =
                    SqlitePendingPolicyMaintenanceRepository.UpdateStateInTransaction(
                        connection,
                        completionTransaction,
                        current.OperationId,
                        GlobalPolicyMaintenanceStateCodec.Serialize(completedState),
                        DateTimeOffset.UtcNow,
                        token);

                _checkpoint?.Invoke(
                    GlobalPolicyMaintenanceCompletionCheckpoint.BeforeCompletionCommit);
                token.ThrowIfCancellationRequested();
                completionTransaction.Commit();
                _checkpoint?.Invoke(
                    GlobalPolicyMaintenanceCompletionCheckpoint.AfterCompletionCommit);
            }
        }

        // If cancellation arrives after the first COMMIT, leave the durable completed marker for
        // retry rather than partially hiding the operation by clearing it.
        token.ThrowIfCancellationRequested();

        using SqliteTransaction clearTransaction = connection.BeginTransaction(deferred: false);
        PendingPolicyMaintenanceOperation beforeClear =
            ReadRequiredExactOperation(connection, clearTransaction, snapshot, token);
        GlobalPolicyMaintenanceState clearState =
            GlobalPolicyMaintenanceStateCodec.Parse(beforeClear.StateJson);
        RequirePriorPhasesCompleted(clearState);
        if (clearState.Completion != GlobalPolicyMaintenanceState.Completed)
        {
            throw new InvalidOperationException(
                "Global policy-maintenance marker cannot be cleared before completion is durable.");
        }

        SqlitePendingPolicyMaintenanceRepository.ClearInTransaction(
            connection,
            clearTransaction,
            beforeClear.OperationId,
            token);

        _checkpoint?.Invoke(
            GlobalPolicyMaintenanceCompletionCheckpoint.BeforeMarkerClearCommit);
        token.ThrowIfCancellationRequested();
        clearTransaction.Commit();
        _checkpoint?.Invoke(
            GlobalPolicyMaintenanceCompletionCheckpoint.AfterMarkerClearCommit);

        // Intentionally no cancellation check after marker-clear COMMIT. At this point the full
        // maintenance operation is durably complete and late cancellation must not demote success.
        return new GlobalPolicyMaintenanceCompletionResult(
            completedOperation,
            wasCompletionAlreadyCompleted);
    }

    private PendingPolicyMaintenanceOperation ReadRequiredExactOperation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CurrentSnapshot snapshot,
        CancellationToken token)
    {
        PendingPolicyMaintenanceOperation current =
            SqlitePendingPolicyMaintenanceRepository.ReadInTransaction(
                connection,
                transaction,
                token)
            ?? throw new InvalidOperationException(
                "The pending global policy-maintenance operation disappeared before completion.");

        if (current.OperationId != snapshot.Operation.OperationId ||
            !string.Equals(
                current.OperationKind,
                ProtectedCurrentPolicyMaintenanceService.OperationKind,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending policy-maintenance operation changed before completion.");
        }

        GlobalPolicyMaintenanceState state =
            GlobalPolicyMaintenanceStateCodec.Parse(current.StateJson);
        if (!string.Equals(
                state.PolicyFingerprint,
                snapshot.State.PolicyFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending policy-maintenance fingerprint changed before completion.");
        }

        return current;
    }

    private static void RequirePriorPhasesCompleted(GlobalPolicyMaintenanceState state)
    {
        if (state.ArchiveExternalReferenceCleanup != GlobalPolicyMaintenanceState.Completed ||
            state.CatalogRebuild != GlobalPolicyMaintenanceState.Completed ||
            state.ExternalTrashCollection != GlobalPolicyMaintenanceState.Completed)
        {
            throw new InvalidOperationException(
                "Global policy-maintenance completion requires Archive, Catalog and Trash phases to be completed.");
        }
    }

    private SqliteConnection OpenValidatedCurrent(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }

        SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadWrite);
        try
        {
            using (SqliteCommand foreignKeys = connection.CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
                foreignKeys.ExecuteNonQuery();
            }

            ValidateCurrentIdentity(connection);
            GlobalCapturePolicySqlSchema.ValidateTables(connection);
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
        using (SqliteCommand identity = connection.CreateCommand())
        {
            identity.CommandText = """
                SELECT SingletonId, StorageId, DatabaseRole, SchemaVersion
                FROM DatabaseIdentity;
                """;
            using SqliteDataReader reader = identity.ExecuteReader();
            if (!reader.Read() ||
                reader.GetInt64(0) != 1 ||
                !Guid.TryParseExact(reader.GetString(1), "D", out Guid storageId) ||
                storageId != _session.StorageId ||
                !string.Equals(
                    reader.GetString(2),
                    DatabaseRole.Current.ToString(),
                    StringComparison.Ordinal) ||
                reader.GetInt32(3) != ProtectedStorageDatabaseService.CurrentSchemaVersion ||
                reader.Read())
            {
                throw new InvalidDataException(
                    "Global policy-maintenance completion requires the exact Current v7 identity.");
            }
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) !=
            ProtectedStorageDatabaseService.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "Global policy-maintenance completion requires matching Current v7 user_version.");
        }
    }

    private sealed record CurrentSnapshot(
        PendingPolicyMaintenanceOperation Operation,
        GlobalPolicyMaintenanceState State);
}
