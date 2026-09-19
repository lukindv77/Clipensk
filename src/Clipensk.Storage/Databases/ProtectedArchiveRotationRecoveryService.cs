using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

public sealed record ArchiveRotationRecoveryResult(
    bool HadPendingOperation,
    Guid OperationId,
    ArchiveRotationPhase ResumedFromPhase,
    IReadOnlyList<ArchiveSegmentDescriptor> Descriptors);

/// <summary>
/// Roll-forward recovery for a pending Archive Rotation, per
/// <c>docs/ARCHIVE_ROTATION_PROTOCOL.md</c> §12.
///
/// Recovery treats the durable plan as immutable and inspects actual filesystem and database state
/// before every action. It never re-plans, never recalculates filenames or DatabaseIds, and after
/// publication has begun it only moves forward. Each phase is completed by the same service that
/// owns that phase in normal execution, so recovery and normal execution cannot diverge.
///
/// This must run before clipboard capture is allowed to resume.
/// </summary>
public sealed class ProtectedArchiveRotationRecoveryService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly SqlitePendingArchiveRotationRepository _pendingRotationRepository;
    private readonly ProtectedArchiveRotationShadowBuilder _shadowBuilder;
    private readonly ProtectedArchiveRotationPublisher _publisher;
    private readonly ProtectedArchiveRotationSourcePurgeService _purgeService;
    private readonly ProtectedArchiveRotationCatalogPublisher _catalogPublisher;
    private readonly string _archiveDirectory;
    private readonly string _currentDatabasePath;

    public ProtectedArchiveRotationRecoveryService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _pendingRotationRepository = new SqlitePendingArchiveRotationRepository(
            session,
            _connectionFactory);
        _shadowBuilder = new ProtectedArchiveRotationShadowBuilder(session, _connectionFactory);
        _publisher = new ProtectedArchiveRotationPublisher(session, _connectionFactory);
        _purgeService = new ProtectedArchiveRotationSourcePurgeService(session, _connectionFactory);
        _catalogPublisher = new ProtectedArchiveRotationCatalogPublisher(session, _connectionFactory);
        string dataRootPath = Path.GetFullPath(session.DataRootPath);
        _archiveDirectory = Path.Combine(dataRootPath, "Archive");
        _currentDatabasePath = Path.Combine(dataRootPath, "Current", "current.db");
    }

    public async Task<ArchiveRotationRecoveryResult> RecoverAsync(
        DateOnly currentLocalDate,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        PendingArchiveRotationOperation? pending =
            await _pendingRotationRepository.ReadAsync(token).ConfigureAwait(false);
        if (pending is null)
        {
            return new ArchiveRotationRecoveryResult(
                HadPendingOperation: false,
                Guid.Empty,
                ArchiveRotationPhase.Planned,
                Array.Empty<ArchiveSegmentDescriptor>());
        }

        ArchiveRotationPhase resumedFrom = pending.Phase;
        Guid operationId = pending.OperationId;

        if (pending.Phase == ArchiveRotationPhase.Planned)
        {
            await AdvancePlannedToReadyAsync(pending, token).ConfigureAwait(false);
            pending = await _pendingRotationRepository.ReadAsync(token).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "Pending archive rotation disappeared during recovery.");
        }

        if (pending.Phase == ArchiveRotationPhase.ReadyToPublish)
        {
            pending = await _publisher.PublishOrRecoverAsync(operationId, token)
                .ConfigureAwait(false);
        }

        if (pending.Phase == ArchiveRotationPhase.PhysicalPublished)
        {
            ArchiveRotationSourcePurgeResult purge = await _purgeService
                .PurgeOrRecoverAsync(operationId, currentLocalDate, token)
                .ConfigureAwait(false);
            pending = purge.Operation;
        }

        IReadOnlyList<ArchiveSegmentDescriptor> descriptors = await _catalogPublisher
            .PublishOrRecoverAsync(operationId, currentLocalDate, token)
            .ConfigureAwait(false);

        return new ArchiveRotationRecoveryResult(
            HadPendingOperation: true,
            operationId,
            resumedFrom,
            descriptors);
    }

    /// <summary>
    /// <c>Planned</c> means no final Archive publication may have started, so a planned canonical
    /// final file already present is unexpected and fails closed. Staging may be incomplete, so the
    /// shadows are rebuilt from Current under the immutable plan and revalidated before the phase
    /// advances.
    /// </summary>
    private async Task AdvancePlannedToReadyAsync(
        PendingArchiveRotationOperation operation,
        CancellationToken token)
    {
        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(token).ConfigureAwait(false);

        foreach (PendingArchiveRotationTarget target in operation.Targets)
        {
            token.ThrowIfCancellationRequested();
            string finalPath = Path.Combine(_archiveDirectory, target.FileName.FileName);
            if (File.Exists(finalPath) || Directory.Exists(finalPath))
            {
                throw new InvalidDataException(
                    $"Planned Archive rotation target '{target.FileName.FileName}' already exists "
                        + "while the operation is still Planned.");
            }
        }

        RebuildPlannedShadows(operation, mutationLease, token);

        var plan = new ArchiveRotationShadowPlan(
            operation.OperationId,
            operation.PolicySnapshot,
            operation.Targets,
            OpenTail: null);
        _shadowBuilder.ValidateShadowSet(plan, mutationLease, token);

        token.ThrowIfCancellationRequested();
        AdvanceToReadyToPublish(operation, token);
    }

    /// <summary>
    /// Rebuilds each planned shadow from Current using the immutable plan. Rebuilding is safe here
    /// because Current still holds every planned source row in this phase, and it removes any
    /// partially written shadow a crash may have left behind.
    /// </summary>
    private void RebuildPlannedShadows(
        PendingArchiveRotationOperation operation,
        ProtectedStorageMutationLease mutationLease,
        CancellationToken token)
    {
        _shadowBuilder.RebuildPlannedShadows(
            operation.OperationId,
            operation.Targets,
            mutationLease,
            token);
    }

    private void AdvanceToReadyToPublish(
        PendingArchiveRotationOperation operation,
        CancellationToken token)
    {
        using SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadWrite);
        using (SqliteCommand foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
            foreignKeys.ExecuteNonQuery();
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        _ = SqlitePendingArchiveRotationRepository.AdvancePhaseInTransaction(
            connection,
            transaction,
            operation.OperationId,
            ArchiveRotationPhase.Planned,
            ArchiveRotationPhase.ReadyToPublish,
            token);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }
}
