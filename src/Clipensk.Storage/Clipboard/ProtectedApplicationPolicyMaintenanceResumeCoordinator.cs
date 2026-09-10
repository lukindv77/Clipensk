using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;

namespace Clipensk.Storage.Clipboard;

public sealed record ApplicationPolicyMaintenanceResumeResult(
    bool HadPendingOperation,
    Guid? OperationId,
    ArchiveExternalApplicationPolicyMaintenanceResult? Archive,
    ExternalPayloadCatalogApplicationPolicyMaintenanceResult? Catalog,
    ExternalPayloadTrashApplicationPolicyMaintenanceResult? Trash,
    ApplicationPolicyMaintenanceCompletionResult? Completion);

/// <summary>
/// Resumes the durable continuation phases of one application capture-policy maintenance
/// operation in order. Runtime quiescence/resume is owned by the application layer; this
/// coordinator only advances protected storage while capture remains suspended.
/// </summary>
public sealed class ProtectedApplicationPolicyMaintenanceResumeCoordinator
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;

    public ProtectedApplicationPolicyMaintenanceResumeCoordinator(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
    }

    public async Task<ApplicationPolicyMaintenanceResumeResult> ResumeAsync(
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
            return new ApplicationPolicyMaintenanceResumeResult(
                HadPendingOperation: false,
                OperationId: null,
                Archive: null,
                Catalog: null,
                Trash: null,
                Completion: null);
        }

        if (!string.Equals(
                pending.OperationKind,
                ProtectedCurrentApplicationPolicyMaintenanceService.OperationKind,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The pending policy-maintenance operation is not an application capture-policy change.");
        }

        // Parse before any continuation call so malformed durable application state fails closed
        // before an unrelated service becomes the first observer of corruption.
        _ = ApplicationPolicyMaintenanceStateCodec.Parse(pending.StateJson);
        token.ThrowIfCancellationRequested();

        ArchiveExternalApplicationPolicyMaintenanceResult archive =
            await new ProtectedArchiveExternalApplicationPolicyMaintenanceService(
                    _session,
                    _connectionFactory)
                .ApplyAsync(token)
                .ConfigureAwait(false);

        ExternalPayloadCatalogApplicationPolicyMaintenanceResult catalog =
            await new ProtectedExternalPayloadCatalogApplicationPolicyMaintenanceService(
                    _session,
                    _connectionFactory)
                .ApplyAsync(token)
                .ConfigureAwait(false);

        ExternalPayloadTrashApplicationPolicyMaintenanceResult trash =
            await new ProtectedExternalPayloadTrashApplicationPolicyMaintenanceService(
                    _session,
                    _connectionFactory)
                .ApplyAsync(deletionDate, token)
                .ConfigureAwait(false);

        ApplicationPolicyMaintenanceCompletionResult completion =
            await new ProtectedApplicationPolicyMaintenanceCompletionService(
                    _session,
                    _connectionFactory)
                .CompleteAsync(token)
                .ConfigureAwait(false);

        return new ApplicationPolicyMaintenanceResumeResult(
            HadPendingOperation: true,
            OperationId: pending.OperationId,
            Archive: archive,
            Catalog: catalog,
            Trash: trash,
            Completion: completion);
    }
}
