using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;

namespace Clipensk.Storage.Databases;

/// <summary>
/// Rebuilds the derived archive-segment catalog projection while holding the protected storage
/// mutation lease so the physical archive/current scan and catalog publication cannot interleave
/// with another storage mutation.
/// </summary>
public sealed class ProtectedArchiveCatalogMaintenanceService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;

    public ProtectedArchiveCatalogMaintenanceService(
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

        cancellationToken.ThrowIfCancellationRequested();
        DateOnly currentLocalDate = DateOnly.FromDateTime(DateTime.Now);

        return await new ProtectedArchiveSegmentCatalog(_session, _connectionFactory)
            .RebuildAsync(currentLocalDate, cancellationToken)
            .ConfigureAwait(false);
    }
}
