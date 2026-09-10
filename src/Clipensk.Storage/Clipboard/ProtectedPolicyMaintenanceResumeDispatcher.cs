using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;

namespace Clipensk.Storage.Clipboard;

public sealed record PolicyMaintenanceResumeDispatchResult(
    bool HadPendingOperation,
    Guid? OperationId,
    GlobalPolicyMaintenanceResumeResult? Global,
    ApplicationPolicyMaintenanceResumeResult? Application);

/// <summary>
/// Dispatches one durable pending capture-policy maintenance marker to the coordinator that owns
/// its operation kind. Unknown durable kinds fail closed before any continuation service is run.
/// Runtime quiescence/resume remains an application-layer responsibility.
/// </summary>
public sealed class ProtectedPolicyMaintenanceResumeDispatcher
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;

    public ProtectedPolicyMaintenanceResumeDispatcher(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
    }

    public async Task<PolicyMaintenanceResumeDispatchResult> ResumeAsync(
        DateOnly deletionDate,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        var pendingRepository = new SqlitePendingPolicyMaintenanceRepository(
            _session,
            _connectionFactory);
        PendingPolicyMaintenanceOperation? pending =
            await pendingRepository.ReadAsync(token).ConfigureAwait(false);
        if (pending is null)
        {
            token.ThrowIfCancellationRequested();
            return new PolicyMaintenanceResumeDispatchResult(
                HadPendingOperation: false,
                OperationId: null,
                Global: null,
                Application: null);
        }

        if (string.Equals(
                pending.OperationKind,
                ProtectedCurrentPolicyMaintenanceService.OperationKind,
                StringComparison.Ordinal))
        {
            GlobalPolicyMaintenanceResumeResult global =
                await new ProtectedGlobalPolicyMaintenanceResumeCoordinator(
                        _session,
                        _connectionFactory)
                    .ResumeAsync(deletionDate, token)
                    .ConfigureAwait(false);
            return new PolicyMaintenanceResumeDispatchResult(
                HadPendingOperation: true,
                OperationId: pending.OperationId,
                Global: global,
                Application: null);
        }

        if (string.Equals(
                pending.OperationKind,
                ProtectedCurrentApplicationPolicyMaintenanceService.OperationKind,
                StringComparison.Ordinal))
        {
            ApplicationPolicyMaintenanceResumeResult application =
                await new ProtectedApplicationPolicyMaintenanceResumeCoordinator(
                        _session,
                        _connectionFactory)
                    .ResumeAsync(deletionDate, token)
                    .ConfigureAwait(false);
            return new PolicyMaintenanceResumeDispatchResult(
                HadPendingOperation: true,
                OperationId: pending.OperationId,
                Global: null,
                Application: application);
        }

        throw new InvalidOperationException(
            $"Unsupported pending policy-maintenance operation kind '{pending.OperationKind}'.");
    }
}
