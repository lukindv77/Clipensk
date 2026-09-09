using System.Globalization;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

public sealed record ExternalPayloadCatalogPolicyMaintenanceResult(
    PendingPolicyMaintenanceOperation Operation,
    int CatalogAddressCount,
    bool WasAlreadyCompleted);

internal enum ExternalPayloadCatalogPolicyMaintenanceCheckpoint
{
    RebuildCompleted,
    BeforeMarkerCommit,
    AfterMarkerCommit,
}

/// <summary>
/// Continues a committed global capture-policy maintenance operation by rebuilding only the
/// ExternalPayloadAddressIndex projection and then durably marking the Catalog phase completed.
/// Physical Trash collection, final completion and marker removal remain later phases.
/// </summary>
public sealed class ProtectedExternalPayloadCatalogPolicyMaintenanceService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;
    private readonly Action<ExternalPayloadCatalogPolicyMaintenanceCheckpoint>? _checkpoint;

    public ProtectedExternalPayloadCatalogPolicyMaintenanceService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
        : this(session, connectionFactory, checkpoint: null)
    {
    }

    internal ProtectedExternalPayloadCatalogPolicyMaintenanceService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory,
        Action<ExternalPayloadCatalogPolicyMaintenanceCheckpoint>? checkpoint)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _checkpoint = checkpoint;
        _currentDatabasePath = Path.Combine(
            Path.GetFullPath(session.DataRootPath),
            "Current",
            "current.db");
    }

    public async Task<ExternalPayloadCatalogPolicyMaintenanceResult> ApplyAsync(
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        CurrentSnapshot snapshot = await ReadCurrentSnapshotAsync(token).ConfigureAwait(false);
        if (snapshot.State.CatalogRebuild == GlobalPolicyMaintenanceState.Completed)
        {
            token.ThrowIfCancellationRequested();
            return new ExternalPayloadCatalogPolicyMaintenanceResult(
                snapshot.Operation,
                CatalogAddressCount: 0,
                WasAlreadyCompleted: true);
        }

        if (snapshot.State.ArchiveExternalReferenceCleanup !=
            GlobalPolicyMaintenanceState.Completed)
        {
            throw new InvalidOperationException(
                "Catalog policy maintenance requires completed Archive external-reference cleanup.");
        }

        var rebuild = new ProtectedExternalPayloadCatalogRebuildService(
            _session,
            _connectionFactory);
        IReadOnlyList<ExternalPayloadAddress> addresses =
            await rebuild.RebuildAsync(token).ConfigureAwait(false);

        // Rebuild owns the session mutation lease through its Catalog COMMIT. A capture that starts
        // after that COMMIT reserves its SHA under the same shared mutation lease before Current
        // history COMMIT, so the short gap before marker publication cannot make the projection
        // incomplete. The marker transaction still re-reads the latest state to avoid stale writes.
        _checkpoint?.Invoke(ExternalPayloadCatalogPolicyMaintenanceCheckpoint.RebuildCompleted);
        token.ThrowIfCancellationRequested();

        PendingPolicyMaintenanceOperation completed =
            await MarkCatalogPhaseCompletedAsync(snapshot, token).ConfigureAwait(false);

        // There is intentionally no cancellation check after the marker COMMIT. Durable completion
        // must remain a successful result even when cancellation arrives immediately afterwards.
        return new ExternalPayloadCatalogPolicyMaintenanceResult(
            completed,
            addresses.Count,
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
                "Catalog policy maintenance requires a pending global policy-maintenance operation.");

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
                "Catalog policy maintenance requires the committed global capture policy.");

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

    private async Task<PendingPolicyMaintenanceOperation> MarkCatalogPhaseCompletedAsync(
        CurrentSnapshot snapshot,
        CancellationToken token)
    {
        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        return await Task.Run(
                () => MarkCatalogPhaseCompletedCore(snapshot, token),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private PendingPolicyMaintenanceOperation MarkCatalogPhaseCompletedCore(
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
                "The pending policy-maintenance operation disappeared before Catalog completion.");

        if (current.OperationId != snapshot.Operation.OperationId ||
            !string.Equals(
                current.OperationKind,
                ProtectedCurrentPolicyMaintenanceService.OperationKind,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending policy-maintenance operation changed before Catalog completion.");
        }

        GlobalPolicyMaintenanceState currentState =
            GlobalPolicyMaintenanceStateCodec.Parse(current.StateJson);
        if (!string.Equals(
                currentState.PolicyFingerprint,
                snapshot.State.PolicyFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending policy-maintenance fingerprint changed before Catalog completion.");
        }

        if (currentState.ArchiveExternalReferenceCleanup !=
            GlobalPolicyMaintenanceState.Completed)
        {
            throw new InvalidOperationException(
                "Catalog completion requires completed Archive external-reference cleanup.");
        }

        if (currentState.CatalogRebuild == GlobalPolicyMaintenanceState.Completed)
        {
            token.ThrowIfCancellationRequested();
            return current;
        }

        GlobalPolicyMaintenanceState completedState =
            currentState with { CatalogRebuild = GlobalPolicyMaintenanceState.Completed };
        PendingPolicyMaintenanceOperation updated =
            SqlitePendingPolicyMaintenanceRepository.UpdateStateInTransaction(
                connection,
                transaction,
                current.OperationId,
                GlobalPolicyMaintenanceStateCodec.Serialize(completedState),
                DateTimeOffset.UtcNow,
                token);

        _checkpoint?.Invoke(ExternalPayloadCatalogPolicyMaintenanceCheckpoint.BeforeMarkerCommit);

        // Final cancellation boundary for this phase. Once COMMIT succeeds, late cancellation
        // cannot demote the durable Catalog continuation state.
        token.ThrowIfCancellationRequested();
        transaction.Commit();

        _checkpoint?.Invoke(ExternalPayloadCatalogPolicyMaintenanceCheckpoint.AfterMarkerCommit);
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
                    "Catalog policy maintenance requires the exact Current v7 identity.");
            }
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) !=
            ProtectedStorageDatabaseService.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "Catalog policy maintenance requires matching Current v7 user_version.");
        }
    }

    private sealed record CurrentSnapshot(
        PendingPolicyMaintenanceOperation Operation,
        GlobalPolicyMaintenanceState State);
}
