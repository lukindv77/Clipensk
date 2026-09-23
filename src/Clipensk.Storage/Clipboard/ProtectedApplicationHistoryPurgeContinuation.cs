using Clipensk.Core.Applications;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Databases;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

public sealed record ApplicationHistoryPurgeResumeResult(
    Guid OperationId,
    ClipboardHistoryPurgeSummary ArchiveSummary,
    int ArchiveDatabaseCount);

internal enum ApplicationHistoryPurgeContinuationCheckpoint
{
    ArchiveDatabaseCommitted,
    ArchivePhaseMarked,
    CatalogRebuilt,
    CatalogPhaseMarked,
    TrashCollected,
    TrashPhaseMarked,
    BeforeClear,
}

/// <summary>
/// Rolls one committed <c>ApplicationHistoryPurge</c> operation forward through its Archive,
/// Catalog and Trash phases and clears its marker, per <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §5.3.
/// Every phase recomputes its rows from the rule fixed in the marker, so repeating a phase after a
/// crash is harmless. Each marker update first re-verifies that the root's policy and the group
/// still match what the operation started with.
/// </summary>
public sealed class ProtectedApplicationHistoryPurgeContinuation
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly Action<ApplicationHistoryPurgeContinuationCheckpoint>? _checkpoint;

    public ProtectedApplicationHistoryPurgeContinuation(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
        : this(session, connectionFactory, checkpoint: null)
    {
    }

    internal ProtectedApplicationHistoryPurgeContinuation(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory,
        Action<ApplicationHistoryPurgeContinuationCheckpoint>? checkpoint)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _checkpoint = checkpoint;
    }

    public async Task<ApplicationHistoryPurgeResumeResult> ResumeAsync(
        DateOnly deletionDate,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        (Guid operationId, ApplicationHistoryPurgeState state) = await Task.Run(
                () => ReadPendingOperation(token),
                CancellationToken.None)
            .ConfigureAwait(false);

        ClipboardHistoryPurgeSummary archiveSummary = ClipboardHistoryPurgeSummary.Empty;
        int archiveCount = 0;
        if (state.Archive == ApplicationHistoryPurgeState.Pending)
        {
            (archiveSummary, archiveCount) = await RunArchivePhaseAsync(operationId, state, token)
                .ConfigureAwait(false);
            state = state with { Archive = ApplicationHistoryPurgeState.Completed };
        }

        if (state.Catalog == ApplicationHistoryPurgeState.Pending)
        {
            await new ProtectedExternalPayloadCatalogRebuildService(_session, _connectionFactory)
                .RebuildAsync(token)
                .ConfigureAwait(false);
            _checkpoint?.Invoke(ApplicationHistoryPurgeContinuationCheckpoint.CatalogRebuilt);
            state = await MarkPhaseAsync(
                    operationId,
                    static current => current with { Catalog = ApplicationHistoryPurgeState.Completed },
                    token)
                .ConfigureAwait(false);
            _checkpoint?.Invoke(ApplicationHistoryPurgeContinuationCheckpoint.CatalogPhaseMarked);
        }

        if (state.Trash == ApplicationHistoryPurgeState.Pending)
        {
            await new ProtectedExternalPayloadTrashCollector(_session, _connectionFactory)
                .CollectAsync(deletionDate, token)
                .ConfigureAwait(false);
            _checkpoint?.Invoke(ApplicationHistoryPurgeContinuationCheckpoint.TrashCollected);
            state = await MarkPhaseAsync(
                    operationId,
                    static current => current with { Trash = ApplicationHistoryPurgeState.Completed },
                    token)
                .ConfigureAwait(false);
            _checkpoint?.Invoke(ApplicationHistoryPurgeContinuationCheckpoint.TrashPhaseMarked);
        }

        await ClearAsync(operationId, token).ConfigureAwait(false);

        // No cancellation check after the clearing COMMIT: the completed operation stays a success.
        return new ApplicationHistoryPurgeResumeResult(operationId, archiveSummary, archiveCount);
    }

    private (Guid OperationId, ApplicationHistoryPurgeState State) ReadPendingOperation(CancellationToken token)
    {
        using SqliteConnection connection = ApplicationGroupMaintenanceDatabase.OpenCurrent(
            _session,
            _connectionFactory,
            SqliteOpenMode.ReadOnly,
            token);
        using SqliteTransaction transaction = connection.BeginTransaction();
        PendingPolicyMaintenanceOperation operation =
            SqlitePendingPolicyMaintenanceRepository.ReadInTransaction(connection, transaction, token)
            ?? throw new InvalidOperationException("No application history purge is pending.");
        ApplicationHistoryPurgeState state = VerifyInTransaction(connection, transaction, operation, token);
        return (operation.OperationId, state);
    }

    private async Task<(ClipboardHistoryPurgeSummary Summary, int ArchiveCount)> RunArchivePhaseAsync(
        Guid operationId,
        ApplicationHistoryPurgeState state,
        CancellationToken token)
    {
        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        var archives = new ProtectedArchiveDatabaseService(_session, _connectionFactory);
        IReadOnlyList<ArchiveFileName> namesBefore = await Task.Run(
                () => ApplicationGroupMaintenanceDatabase.EnumerateArchives(_session, token),
                CancellationToken.None)
            .ConfigureAwait(false);

        ClipboardHistoryPurgeSummary summary = ClipboardHistoryPurgeSummary.Empty;
        var databaseIds = new HashSet<Guid>();
        foreach (ArchiveFileName fileName in namesBefore)
        {
            token.ThrowIfCancellationRequested();
            DatabaseIdentity identity = await archives.ValidateAsync(fileName, token).ConfigureAwait(false);
            if (!databaseIds.Add(identity.DatabaseId))
            {
                throw new InvalidDataException(
                    "Multiple archive files expose the same DatabaseId during the history purge.");
            }

            ClipboardHistoryPurgeSummary archiveSummary = await Task.Run(
                    () =>
                    {
                        using SqliteConnection connection = ApplicationGroupMaintenanceDatabase.OpenArchive(
                            _session,
                            _connectionFactory,
                            fileName,
                            identity.DatabaseId,
                            SqliteOpenMode.ReadWrite,
                            token);
                        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
                        ClipboardHistoryPurgePlan plan = ClipboardHistoryPurge.PlanInTransaction(
                            connection,
                            transaction,
                            state.SourceApplicationIds,
                            state.Rule,
                            token);
                        if (plan.Summary.IsEmpty)
                        {
                            return plan.Summary;
                        }

                        ClipboardHistoryPurge.ApplyInTransaction(connection, transaction, plan, token);
                        token.ThrowIfCancellationRequested();
                        transaction.Commit();
                        return plan.Summary;
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (!archiveSummary.IsEmpty)
            {
                DatabaseIdentity identityAfter = await archives.ValidateAsync(fileName, token).ConfigureAwait(false);
                if (identityAfter != identity)
                {
                    throw new InvalidOperationException(
                        $"Archive '{fileName.FileName}' identity changed during the history purge.");
                }

                _checkpoint?.Invoke(ApplicationHistoryPurgeContinuationCheckpoint.ArchiveDatabaseCommitted);
                token.ThrowIfCancellationRequested();
            }

            summary = summary.Add(archiveSummary);
        }

        IReadOnlyList<ArchiveFileName> namesAfter = await Task.Run(
                () => ApplicationGroupMaintenanceDatabase.EnumerateArchives(_session, token),
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!namesBefore.Select(static name => name.FileName)
                .SequenceEqual(namesAfter.Select(static name => name.FileName), StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The Archive directory changed during the history purge; the phase will be retried.");
        }

        await Task.Run(
                () => MarkPhaseInLease(
                    operationId,
                    static current => current with { Archive = ApplicationHistoryPurgeState.Completed },
                    token),
                CancellationToken.None)
            .ConfigureAwait(false);
        _checkpoint?.Invoke(ApplicationHistoryPurgeContinuationCheckpoint.ArchivePhaseMarked);
        return (summary, namesBefore.Count);
    }

    private async Task<ApplicationHistoryPurgeState> MarkPhaseAsync(
        Guid operationId,
        Func<ApplicationHistoryPurgeState, ApplicationHistoryPurgeState> advance,
        CancellationToken token)
    {
        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return await Task.Run(
                () => MarkPhaseInLease(operationId, advance, token),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private ApplicationHistoryPurgeState MarkPhaseInLease(
        Guid operationId,
        Func<ApplicationHistoryPurgeState, ApplicationHistoryPurgeState> advance,
        CancellationToken token)
    {
        using SqliteConnection connection = ApplicationGroupMaintenanceDatabase.OpenCurrent(
            _session,
            _connectionFactory,
            SqliteOpenMode.ReadWrite,
            token);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        PendingPolicyMaintenanceOperation operation = ReadExactOperation(connection, transaction, operationId, token);
        ApplicationHistoryPurgeState current = VerifyInTransaction(connection, transaction, operation, token);
        ApplicationHistoryPurgeState advanced = advance(current);
        if (advanced == current)
        {
            return current;
        }

        SqlitePendingPolicyMaintenanceRepository.UpdateStateInTransaction(
            connection,
            transaction,
            operationId,
            ApplicationHistoryPurgeStateCodec.Serialize(advanced),
            DateTimeOffset.UtcNow,
            token);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return advanced;
    }

    private async Task ClearAsync(Guid operationId, CancellationToken token)
    {
        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await Task.Run(
                () =>
                {
                    using SqliteConnection connection = ApplicationGroupMaintenanceDatabase.OpenCurrent(
                        _session,
                        _connectionFactory,
                        SqliteOpenMode.ReadWrite,
                        token);
                    using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
                    PendingPolicyMaintenanceOperation operation =
                        ReadExactOperation(connection, transaction, operationId, token);
                    ApplicationHistoryPurgeState state = VerifyInTransaction(connection, transaction, operation, token);
                    if (!state.IsFullyCompleted)
                    {
                        throw new InvalidOperationException(
                            "An application history purge cannot be cleared before every phase completed.");
                    }

                    SqlitePendingPolicyMaintenanceRepository.ClearInTransaction(
                        connection,
                        transaction,
                        operationId,
                        token);
                    _checkpoint?.Invoke(ApplicationHistoryPurgeContinuationCheckpoint.BeforeClear);
                    token.ThrowIfCancellationRequested();
                    transaction.Commit();
                },
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static PendingPolicyMaintenanceOperation ReadExactOperation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid operationId,
        CancellationToken token)
    {
        PendingPolicyMaintenanceOperation operation =
            SqlitePendingPolicyMaintenanceRepository.ReadInTransaction(connection, transaction, token)
            ?? throw new InvalidOperationException("The application history purge marker disappeared.");
        if (operation.OperationId != operationId)
        {
            throw new InvalidOperationException("The pending maintenance operation changed during the history purge.");
        }
        return operation;
    }

    /// <summary>
    /// Confirms the marker belongs to this operation kind and that Current still matches it: every
    /// source still belongs to the root's group and the root's personal policy is the one recorded.
    /// </summary>
    private static ApplicationHistoryPurgeState VerifyInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PendingPolicyMaintenanceOperation operation,
        CancellationToken token)
    {
        if (!string.Equals(operation.OperationKind, ApplicationHistoryPurgeState.OperationKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The pending maintenance operation is not an application history purge.");
        }

        ApplicationHistoryPurgeState state = ApplicationHistoryPurgeStateCodec.Parse(operation.StateJson);
        var root = new ApplicationId(Guid.ParseExact(state.RootApplicationId, "D"));
        ApplicationGroupSnapshot groups =
            SqliteApplicationGroupRepository.ReadSnapshotInTransaction(connection, transaction, token);
        foreach (string source in state.SourceApplicationIds)
        {
            if (groups.RootOf(new ApplicationId(Guid.ParseExact(source, "D"))) != root)
            {
                throw new InvalidDataException(
                    "An application in the history purge scope no longer belongs to the purge's group.");
            }
        }

        ClipboardCapturePolicy? rootPolicy =
            CapturePolicySql.ReadApplicationPolicyInTransaction(connection, transaction, root, token);
        string? fingerprint = rootPolicy is null
            ? null
            : ApplicationPolicyMaintenanceStateCodec.ComputePolicyFingerprint(rootPolicy);
        if (!string.Equals(fingerprint, state.RootPolicyFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The group root's personal policy changed while the history purge was pending.");
        }

        return state;
    }
}
