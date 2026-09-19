using Clipensk.Core.Storage;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Sqlite;

namespace Clipensk.Storage.Databases;

public sealed record ProtectedStorageStartupRecoveryResult(
    Guid? ArchiveSplitOperationId,
    IReadOnlyList<ArchiveSegmentDescriptor> ArchiveSplitDescriptors,
    ArchiveRotationRecoveryResult ArchiveRotation,
    PolicyMaintenanceResumeDispatchResult PolicyMaintenance)
{
    public bool HadPendingWork =>
        ArchiveSplitOperationId.HasValue
        || ArchiveRotation.HadPendingOperation
        || PolicyMaintenance.HadPendingOperation;
}

/// <summary>
/// Completes every durable storage operation that may have been interrupted, in the single order
/// the runtime is allowed to use before clipboard capture resumes.
///
/// The order is Archive split, then Archive rotation, then policy maintenance:
///
/// <list type="bullet">
/// <item>Split and rotation cannot both be pending. Each start path fails closed on the other's
/// marker (<see cref="SqlitePendingArchiveSplitRepository"/> and
/// <see cref="SqlitePendingArchiveRotationRepository"/>), so at most one of the two has work and a
/// fixed order stays deterministic without having to know which one crashed.</item>
/// <item>Both must reach a settled state before the policy-maintenance continuation runs. That
/// continuation performs Archive cleanup and Catalog maintenance over the same segments, and a
/// half-published rotation would expose it to an Archive set the Catalog does not yet describe.</item>
/// </list>
///
/// Every phase service remains authoritative for its own validation, mutation leases, crash
/// boundaries, and cancellation semantics. This coordinator only sequences them and never swallows
/// a failure: the caller stays fail-closed and must not resume capture when this throws.
/// </summary>
public sealed class ProtectedStorageStartupRecoveryCoordinator
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly SqlitePendingArchiveSplitRepository _pendingSplitRepository;
    private readonly ProtectedArchiveSplitRecoveryService _splitRecovery;
    private readonly ProtectedArchiveRotationRecoveryService _rotationRecovery;
    private readonly ProtectedPolicyMaintenanceResumeDispatcher _policyMaintenanceResume;

    public ProtectedStorageStartupRecoveryCoordinator(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        IKeyedSqliteConnectionFactory factory =
            connectionFactory ?? new SqlCipherConnectionFactory();
        _pendingSplitRepository = new SqlitePendingArchiveSplitRepository(session, factory);
        _splitRecovery = new ProtectedArchiveSplitRecoveryService(session, factory);
        _rotationRecovery = new ProtectedArchiveRotationRecoveryService(session, factory);
        _policyMaintenanceResume = new ProtectedPolicyMaintenanceResumeDispatcher(session, factory);
    }

    public async Task<ProtectedStorageStartupRecoveryResult> RecoverAsync(
        DateOnly currentLocalDate,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        Guid? splitOperationId = null;
        IReadOnlyList<ArchiveSegmentDescriptor> splitDescriptors = [];
        PendingArchiveSplitOperation? pendingSplit =
            await _pendingSplitRepository.ReadAsync(token).ConfigureAwait(false);
        if (pendingSplit is not null)
        {
            splitOperationId = pendingSplit.OperationId;
            splitDescriptors = await _splitRecovery
                .RecoverAsync(pendingSplit.OperationId, currentLocalDate, token)
                .ConfigureAwait(false);
        }

        token.ThrowIfCancellationRequested();
        ArchiveRotationRecoveryResult rotation = await _rotationRecovery
            .RecoverAsync(currentLocalDate, token)
            .ConfigureAwait(false);

        token.ThrowIfCancellationRequested();
        PolicyMaintenanceResumeDispatchResult policyMaintenance = await _policyMaintenanceResume
            .ResumeAsync(currentLocalDate, token)
            .ConfigureAwait(false);

        return new ProtectedStorageStartupRecoveryResult(
            splitOperationId,
            splitDescriptors,
            rotation,
            policyMaintenance);
    }
}
