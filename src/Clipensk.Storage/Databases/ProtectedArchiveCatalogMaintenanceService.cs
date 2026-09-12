using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;

namespace Clipensk.Storage.Databases;

public sealed class ProtectedArchiveCatalogMaintenanceService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory? _connectionFactory;

    public ProtectedArchiveCatalogMaintenanceService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<ArchiveSegmentDescriptor>> RebuildAsync(
        DateOnly currentCalendarDate,
        CancellationToken cancellationToken = default)
    {
        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(cancellationToken)
                .ConfigureAwait(false);

        return await new ProtectedArchiveSegmentCatalog(_session, _connectionFactory)
            .RebuildAsync(currentCalendarDate, cancellationToken)
            .ConfigureAwait(false);
    }
}
