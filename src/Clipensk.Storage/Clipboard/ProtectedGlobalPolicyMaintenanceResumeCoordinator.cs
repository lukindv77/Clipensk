using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;

namespace Clipensk.Storage.Clipboard;

public sealed record GlobalPolicyMaintenanceResumeResult(
    bool HadPendingOperation,
    Guid? OperationId,
    ArchiveExternalPolicyMaintenanceResult? Archive,
    ExternalPayloadCatalogPolicyMaintenanceResult? Catalog,
    ExternalPayloadTrashPolicyMaintenanceResult? Trash,
    GlobalPolicyMaintenanceCompletionResult? Completion);

/// <summary>
/// Resumes the durable continuation phases of one global capture-policy maintenance operation in
/// order. Every phase owns its own durable commit boundary and remains idempotent. Runtime
/// quiescence/resume is intentionally owned by the application layer; this coordinator only
/// advances protected storage state while the caller keeps capture suspended.
/// </summary>
public sealed class ProtectedGlobalPolicyMaintenanceResumeCoordinator
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;

    public ProtectedGlobalPolicyMaintenanceResumeCoordinator(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
    }

    public async Task<GlobalPolicyMaintenanceResumeResult> ResumeAsync(
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
            return new GlobalPolicyMaintenanceResumeResult(
                HadPendingOperation: false,
                OperationId: null,
                Archive: null,
                Catalog: null,
                Trash: null,
                Completion: null);
        }

        if (!string.Equals(
                pending.OperationKind,
                ProtectedCurrentPolicyMaintenanceService.OperationKind,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending policy-maintenance operation is not a global capture-policy change.");
        }

        // Parse before any continuation call so malformed durable state fails closed without
        // letting an unrelated service become the first observer of the corruption.
        _ = GlobalPolicyMaintenanceStateCodec.Parse(pending.StateJson);
        token.ThrowIfCancellationRequested();

        ArchiveExternalPolicyMaintenanceResult archive =
            await new ProtectedArchiveExternalPolicyMaintenanceService(
                    _session,
                    _connectionFactory)
                .ApplyAsync(token)
                .ConfigureAwait(false);

        ExternalPayloadCatalogPolicyMaintenanceResult catalog =
            await new ProtectedExternalPayloadCatalogPolicyMaintenanceService(
                    _session,
                    _connectionFactory)
                .ApplyAsync(token)
                .ConfigureAwait(false);

        ExternalPayloadTrashPolicyMaintenanceResult trash =
            await new ProtectedExternalPayloadTrashPolicyMaintenanceService(
                    _session,
                    _connectionFactory)
                .ApplyAsync(deletionDate, token)
                .ConfigureAwait(false);

        GlobalPolicyMaintenanceCompletionResult completion =
            await new ProtectedGlobalPolicyMaintenanceCompletionService(
                    _session,
                    _connectionFactory)
                .CompleteAsync(token)
                .ConfigureAwait(false);

        return new GlobalPolicyMaintenanceResumeResult(
            HadPendingOperation: true,
            OperationId: pending.OperationId,
            Archive: archive,
            Catalog: catalog,
            Trash: trash,
            Completion: completion);
    }
}
