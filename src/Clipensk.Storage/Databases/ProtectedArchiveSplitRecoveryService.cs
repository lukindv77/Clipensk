using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;

namespace Clipensk.Storage.Databases;

/// <summary>
/// Continues one durable Archive split from its persisted phase until the operation is complete.
/// The individual phase services remain authoritative for their own validation, mutation leases,
/// crash boundaries, and cancellation semantics; this coordinator only selects the next phase and
/// chains successful phases together.
/// </summary>
public sealed class ProtectedArchiveSplitRecoveryService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly SqlitePendingArchiveSplitRepository _pendingSplitRepository;
    private readonly ProtectedArchiveSplitShadowBuilder _shadowBuilder;
    private readonly ProtectedArchiveSplitPublisher _physicalPublisher;
    private readonly ProtectedArchiveSplitCatalogPublisher _catalogPublisher;

    public ProtectedArchiveSplitRecoveryService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        IKeyedSqliteConnectionFactory factory =
            connectionFactory ?? new SqlCipherConnectionFactory();
        _pendingSplitRepository = new SqlitePendingArchiveSplitRepository(session, factory);
        _shadowBuilder = new ProtectedArchiveSplitShadowBuilder(session, factory);
        _physicalPublisher = new ProtectedArchiveSplitPublisher(session, factory);
        _catalogPublisher = new ProtectedArchiveSplitCatalogPublisher(session, factory);
    }

    public async Task<IReadOnlyList<ArchiveSegmentDescriptor>> RecoverAsync(
        Guid operationId,
        DateOnly currentCalendarDate,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "Archive split operation id cannot be empty.",
                nameof(operationId));
        }

        PendingArchiveSplitOperation operation =
            await ReadOwnedOperationAsync(operationId, cancellationToken).ConfigureAwait(false);

        if (operation.Phase == ArchiveSplitPhase.Planned)
        {
            operation = await _shadowBuilder
                .BuildAsync(operationId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (operation.Phase == ArchiveSplitPhase.ReadyToPublish)
        {
            operation = await _physicalPublisher
                .PublishOrRecoverAsync(operationId, cancellationToken)
                .ConfigureAwait(false);
        }

        if (operation.Phase is ArchiveSplitPhase.PhysicalPublished or ArchiveSplitPhase.CatalogPublished)
        {
            return await _catalogPublisher
                .PublishOrRecoverAsync(operationId, currentCalendarDate, cancellationToken)
                .ConfigureAwait(false);
        }

        throw new InvalidDataException(
            $"Archive split recovery reached unsupported phase '{operation.Phase}'.");
    }

    private async ValueTask<PendingArchiveSplitOperation> ReadOwnedOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        PendingArchiveSplitOperation operation =
            await _pendingSplitRepository.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No pending archive split exists.");
        if (operation.OperationId != operationId)
        {
            throw new InvalidOperationException(
                "Pending archive split ownership does not match the requested operation.");
        }

        return operation;
    }
}
