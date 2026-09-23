using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;

namespace Clipensk.Storage.Databases;

/// <param name="CurrentCreated">A new <c>current.db</c> was published, whatever happened after.</param>
/// <param name="CatalogQuarantineRelativePath">Where the replaced Catalog was kept, if one was replaced.</param>
public sealed record ProtectedCurrentRestartResult(
    ProtectedStorageDatabaseStatus Status,
    bool CurrentCreated,
    string? CatalogQuarantineRelativePath)
{
    public bool IsSuccess => Status == ProtectedStorageDatabaseStatus.Success;
}

/// <summary>
/// "Start the current database anew" for a storage whose <c>current.db</c> was lost
/// (<c>docs/CURRENT_RESTART.md</c>): a new empty Current with the same <c>StorageId</c> and key,
/// the Catalog rebuilt from it and the Archive, then the normal pair validation. Runs before a
/// session, like Catalog recovery, and only on an explicit, confirmed user action.
/// </summary>
public sealed class ProtectedCurrentRestartService
{
    private readonly ProtectedStorageDatabaseService _databases;
    private readonly ProtectedStorageCatalogRecoveryService _catalogRecovery;
    private readonly ProtectedStorageCatalogReplacementService _catalogReplacement;

    public ProtectedCurrentRestartService(IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        IKeyedSqliteConnectionFactory factory = connectionFactory ?? new SqlCipherConnectionFactory();
        _databases = new ProtectedStorageDatabaseService(factory);
        _catalogRecovery = new ProtectedStorageCatalogRecoveryService(factory);
        _catalogReplacement = new ProtectedStorageCatalogReplacementService(factory);
    }

    public async Task<ProtectedCurrentRestartResult> RestartAsync(
        string dataRootPath,
        Guid storageId,
        ReadOnlyMemory<byte> storageKey,
        DateOnly currentCalendarDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        string root = Path.GetFullPath(dataRootPath);

        ProtectedStorageDatabaseResult created = await _databases
            .StartCurrentAnewAsync(root, storageId, storageKey, cancellationToken)
            .ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            return new ProtectedCurrentRestartResult(created.Status, CurrentCreated: false, null);
        }

        // From here on the new Current stays: it is empty and valid, and a failed Catalog rebuild
        // leaves a state the lock screen's Catalog recovery already handles (§4).
        string catalogPath = Path.Combine(
            root,
            StorageDatabaseFiles.CurrentDirectoryName,
            StorageDatabaseFiles.CatalogFileName);
        string? quarantine = null;
        if (File.Exists(catalogPath))
        {
            ProtectedStorageCatalogReplacementResult replaced = await _catalogReplacement
                .ReplaceExistingCatalogAsync(root, storageId, storageKey, currentCalendarDate, cancellationToken)
                .ConfigureAwait(false);
            if (!replaced.IsSuccess)
            {
                return new ProtectedCurrentRestartResult(replaced.Status, CurrentCreated: true, null);
            }

            quarantine = replaced.QuarantineRelativePath;
        }
        else
        {
            ProtectedStorageDatabaseResult recovered = await _catalogRecovery
                .RecoverMissingCatalogAsync(root, storageId, storageKey, currentCalendarDate, cancellationToken)
                .ConfigureAwait(false);
            if (!recovered.IsSuccess)
            {
                return new ProtectedCurrentRestartResult(recovered.Status, CurrentCreated: true, null);
            }
        }

        ProtectedStorageDatabaseResult validated = await _databases
            .InitializeOrValidateAsync(root, storageId, storageKey, allowInitialize: false, cancellationToken)
            .ConfigureAwait(false);
        return new ProtectedCurrentRestartResult(validated.Status, CurrentCreated: true, quarantine);
    }
}
