using Clipensk.Core.Storage;

namespace Clipensk.Storage.Clipboard;

/// <summary>
/// Owns the protected-storage mutation gate for one durable policy-maintenance operation.
/// Disposing without completing releases the in-process gate but intentionally leaves the
/// durable marker in Current so a later protected session can resume recovery fail-closed.
/// </summary>
public sealed class ProtectedPolicyMaintenanceSession : IDisposable
{
    private ProtectedPolicyMaintenanceCoordinator? _coordinator;
    private ProtectedStorageMutationLease? _mutationLease;
    private bool _completed;

    internal ProtectedPolicyMaintenanceSession(
        ProtectedPolicyMaintenanceCoordinator coordinator,
        ProtectedStorageMutationLease mutationLease,
        PendingPolicyMaintenanceSnapshot snapshot)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _mutationLease = mutationLease ?? throw new ArgumentNullException(nameof(mutationLease));
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public PendingPolicyMaintenanceSnapshot Snapshot { get; private set; }

    public bool IsCompleted => _completed;

    public async ValueTask UpdateStateAsync(
        string stateJson,
        CancellationToken cancellationToken = default)
    {
        ProtectedPolicyMaintenanceCoordinator coordinator = GetCoordinator();
        Snapshot = await coordinator
            .UpdateOwnedStateAsync(Snapshot.OperationId, stateJson, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        ProtectedPolicyMaintenanceCoordinator coordinator = GetCoordinator();
        await coordinator
            .CompleteOwnedAsync(Snapshot.OperationId, cancellationToken)
            .ConfigureAwait(false);

        _completed = true;
        Dispose();
    }

    public void Dispose()
    {
        _coordinator = null;
        Interlocked.Exchange(ref _mutationLease, null)?.Dispose();
        GC.SuppressFinalize(this);
    }

    private ProtectedPolicyMaintenanceCoordinator GetCoordinator() =>
        _coordinator ?? throw new ObjectDisposedException(nameof(ProtectedPolicyMaintenanceSession));
}
