using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;

namespace Clipensk.Storage.Databases;

public sealed record CurrentToArchiveMaintenanceResult(
    CurrentToArchiveTransferResult Transfer,
    IReadOnlyList<ArchiveSegmentDescriptor> ArchiveSegments);

/// <summary>
/// Runs one manual Current-to-Archive maintenance transfer and refreshes the persisted Archive
/// segment projection after the durable transfer phases complete.
/// </summary>
public sealed class ProtectedCurrentToArchiveMaintenanceService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;

    public ProtectedCurrentToArchiveMaintenanceService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
    }

    public async Task<CurrentToArchiveMaintenanceResult> TransferAsync(
        ArchiveFileName archiveFileName,
        JournalDateRange transferRange,
        CancellationToken cancellationToken = default)
    {
        var transferService = new ProtectedCurrentToArchiveTransferService(
            _session,
            _connectionFactory);
        CurrentToArchiveTransferResult transfer = await transferService
            .TransferAsync(archiveFileName, transferRange, cancellationToken)
            .ConfigureAwait(false);

        // Derive sealing against the local date after the durable transfer finishes so an
        // operation that crosses midnight does not persist a projection based on yesterday.
        DateOnly currentLocalDate = DateOnly.FromDateTime(DateTime.Now);

        // The Archive and Current commits are already durable when TransferAsync returns.
        // Do not let a late caller cancellation report that completed transfer as failed.
        // RebuildAsync still links CancellationToken.None with the protected session token,
        // so lock/session revocation remains a fail-closed cancellation boundary.
        IReadOnlyList<ArchiveSegmentDescriptor> archiveSegments =
            await new ProtectedArchiveSegmentCatalog(_session, _connectionFactory)
                .RebuildAsync(currentLocalDate, CancellationToken.None)
                .ConfigureAwait(false);

        return new CurrentToArchiveMaintenanceResult(transfer, archiveSegments);
    }
}
