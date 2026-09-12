using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;

namespace Clipensk.Storage.Databases;

/// <summary>
/// Rebuilds the persisted archive-segment projection from validated physical storage while
/// serializing the scan/write against other protected storage mutations in the active session.
/// </summary>
public sealed class ProtectedArchiveSegmentCatalogMaintenanceService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;

    public ProtectedArchiveSegmentCatalogMaintenanceService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
    }

    public async Task<IReadOnlyList<ArchiveSegmentDescriptor>> RebuildAsync(
        CancellationToken cancellationToken = default)
    {
        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(cancellationToken).ConfigureAwait(false);

        DateOnly currentLocalDate = DateOnly.FromDateTime(DateTime.Now);
        return await new ProtectedArchiveSegmentCatalog(_session, _connectionFactory)
            .RebuildAsync(currentLocalDate, cancellationToken)
            .ConfigureAwait(false);
    }
}
