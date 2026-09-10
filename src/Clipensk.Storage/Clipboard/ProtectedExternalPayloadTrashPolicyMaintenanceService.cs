using System.Globalization;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

public sealed record ExternalPayloadTrashPolicyMaintenanceResult(
    PendingPolicyMaintenanceOperation Operation,
    int CollectedFileCount,
    IReadOnlyList<string> TrashRelativePaths,
    bool WasAlreadyCompleted);

internal enum ExternalPayloadTrashPolicyMaintenanceCheckpoint
{
    CollectionCompleted,
    BeforeMarkerCommit,
    AfterMarkerCommit,
}

/// <summary>
/// Continues a committed global capture-policy maintenance operation by collecting only
/// history-unreferenced managed external payload objects into Trash and then durably marking the
/// external Trash phase completed. Final completion and pending-marker removal remain later phases.
/// </summary>
public sealed class ProtectedExternalPayloadTrashPolicyMaintenanceService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;
    private readonly Action<ExternalPayloadTrashPolicyMaintenanceCheckpoint>? _checkpoint;

    public ProtectedExternalPayloadTrashPolicyMaintenanceService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
        : this(session, connectionFactory, checkpoint: null)
    {
    }

    internal ProtectedExternalPayloadTrashPolicyMaintenanceService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory,
        Action<ExternalPayloadTrashPolicyMaintenanceCheckpoint>? checkpoint)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _checkpoint = checkpoint;
        _currentDatabasePath = Path.Combine(
            Path.GetFullPath(session.DataRootPath),
            "Current",
            "current.db");
    }

    public async Task<ExternalPayloadTrashPolicyMaintenanceResult> ApplyAsync(
        DateOnly deletionDate,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        CurrentSnapshot snapshot = await ReadCurrentSnapshotAsync(token).ConfigureAwait(false);
        if (snapshot.State.ExternalTrashCollection == GlobalPolicyMaintenanceState.Completed)
        {
            token.ThrowIfCancellationRequested();
            return new ExternalPayloadTrashPolicyMaintenanceResult(
                snapshot.Operation,
                CollectedFileCount: 0,
                TrashRelativePaths: Array.Empty<string>(),
                WasAlreadyCompleted: true);
        }

        if (snapshot.State.CatalogRebuild != GlobalPolicyMaintenanceState.Completed)
        {
            throw new InvalidOperationException(
                "External Trash policy maintenance requires completed Catalog rebuild.");
        }

        var collector = new ProtectedExternalPayloadTrashCollector(
            _session,
            _connectionFactory);
        ExternalPayloadTrashCollectionResult collection =
            await collector.CollectAsync(deletionDate, token).ConfigureAwait(false);

        // Each collector move/delete is independently durable. Cancellation or failure after a
        // successful collection may therefore leave physical progress with the continuation marker
        // still pending. Retry is safe because already-collected objects are no longer in Files.
        _checkpoint?.Invoke(ExternalPayloadTrashPolicyMaintenanceCheckpoint.CollectionCompleted);
        token.ThrowIfCancellationRequested();

        PendingPolicyMaintenanceOperation completed =
            await MarkExternalTrashPhaseCompletedAsync(snapshot, token).ConfigureAwait(false);

        // There is intentionally no cancellation check after the marker COMMIT. Durable completion
        // must remain a successful result even when cancellation arrives immediately afterwards.
        return new ExternalPayloadTrashPolicyMaintenanceResult(
            completed,
            collection.CollectedFileCount,
            collection.TrashRelativePaths,
            WasAlreadyCompleted: false);
    }

    private async Task<CurrentSnapshot> ReadCurrentSnapshotAsync(CancellationToken token)
    {
        var pendingRepository = new SqlitePendingPolicyMaintenanceRepository(
            _session,
            _connectionFactory);
        PendingPolicyMaintenanceOperation operation =
            await pendingRepository.ReadAsync(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "External Trash policy maintenance requires a pending global policy-maintenance operation.");

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
        ClipboardCapturePolicy globalPolicy =
            await new SqliteGlobalClipboardCapturePolicyRepository(_session, _connectionFactory)
                .ReadAsync(token)
                .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "External Trash policy maintenance requires the committed global capture policy.");

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

    private async Task<PendingPolicyMaintenanceOperation> MarkExternalTrashPhaseCompletedAsync(
        CurrentSnapshot snapshot,
        CancellationToken token)
    {
        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        ClipboardCapturePolicy persistedGlobal =
            await new SqliteGlobalClipboardCapturePolicyRepository(_session, _connectionFactory)
                .ReadAsync(token)
                .ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "External Trash completion requires the committed global capture policy.");
        if (!string.Equals(
                GlobalPolicyMaintenanceStateCodec.ComputePolicyFingerprint(persistedGlobal),
                snapshot.State.PolicyFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The global capture policy changed before external Trash completion.");
        }

        // Retain the same mutation lease through the marker transaction. Supported global-policy
        // writers therefore cannot change policy between this revalidation and durable COMMIT.
        return await Task.Run(
                () => MarkExternalTrashPhaseCompletedCore(snapshot, token),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private PendingPolicyMaintenanceOperation MarkExternalTrashPhaseCompletedCore(
        CurrentSnapshot snapshot,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenValidatedCurrent(token);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

        PendingPolicyMaintenanceOperation current =
            SqlitePendingPolicyMaintenanceRepository.ReadInTransaction(
                connection,
                transaction,
                token)
            ?? throw new InvalidOperationException(
                "The pending policy-maintenance operation disappeared before external Trash completion.");

        if (current.OperationId != snapshot.Operation.OperationId ||
            !string.Equals(
                current.OperationKind,
                ProtectedCurrentPolicyMaintenanceService.OperationKind,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending policy-maintenance operation changed before external Trash completion.");
        }

        GlobalPolicyMaintenanceState currentState =
            GlobalPolicyMaintenanceStateCodec.Parse(current.StateJson);
        if (!string.Equals(
                currentState.PolicyFingerprint,
                snapshot.State.PolicyFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending policy-maintenance fingerprint changed before external Trash completion.");
        }

        if (currentState.CatalogRebuild != GlobalPolicyMaintenanceState.Completed)
        {
            throw new InvalidOperationException(
                "External Trash completion requires completed Catalog rebuild.");
        }

        if (currentState.ExternalTrashCollection == GlobalPolicyMaintenanceState.Completed)
        {
            token.ThrowIfCancellationRequested();
            return current;
        }

        GlobalPolicyMaintenanceState completedState =
            currentState with { ExternalTrashCollection = GlobalPolicyMaintenanceState.Completed };
        PendingPolicyMaintenanceOperation updated =
            SqlitePendingPolicyMaintenanceRepository.UpdateStateInTransaction(
                connection,
                transaction,
                current.OperationId,
                GlobalPolicyMaintenanceStateCodec.Serialize(completedState),
                DateTimeOffset.UtcNow,
                token);

        _checkpoint?.Invoke(ExternalPayloadTrashPolicyMaintenanceCheckpoint.BeforeMarkerCommit);

        // Final cancellation boundary for this phase. Once COMMIT succeeds, late cancellation
        // cannot demote the durable external Trash continuation state.
        token.ThrowIfCancellationRequested();
        transaction.Commit();

        _checkpoint?.Invoke(ExternalPayloadTrashPolicyMaintenanceCheckpoint.AfterMarkerCommit);
        return updated;
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
                    "External Trash policy maintenance requires the exact Current v7 identity.");
            }
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) !=
            ProtectedStorageDatabaseService.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "External Trash policy maintenance requires matching Current v7 user_version.");
        }
    }

    private sealed record CurrentSnapshot(
        PendingPolicyMaintenanceOperation Operation,
        GlobalPolicyMaintenanceState State);
}
