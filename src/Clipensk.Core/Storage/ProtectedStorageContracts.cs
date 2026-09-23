namespace Clipensk.Core.Storage;

public enum DatabaseRole
{
    Current = 0,
    StorageCatalog = 1,
    Archive = 2,
}

public sealed record DatabaseIdentity(
    Guid StorageId,
    Guid DatabaseId,
    DatabaseRole Role,
    int SchemaVersion,
    int EncryptionVersion,
    DateTimeOffset CreatedAtUtc,
    int? ArchiveBaseNumber = null,
    int? ArchiveSplitSequence = null,
    DateOnly? CoverageStartDate = null,
    DateOnly? CoverageEndDate = null);

public enum ProtectedStorageDatabaseStatus
{
    Success = 0,
    EncryptionEngineUnavailable = 1,
    MissingOrPartialStorage = 2,
    InvalidDatabaseIdentity = 3,
    StorageFailure = 4,
}

public sealed record ProtectedStorageDatabaseResult(
    ProtectedStorageDatabaseStatus Status,
    bool WasInitialized)
{
    public bool IsSuccess => Status == ProtectedStorageDatabaseStatus.Success;
}

public enum ProtectedStorageIdentityStatus
{
    /// <summary>A database opened with the key and named the storage it belongs to.</summary>
    Identified = 0,

    /// <summary>Every database carrying the key's salt refused the key: it is the wrong key.</summary>
    KeyRejected = 1,

    /// <summary>No database carries the key's salt.</summary>
    NoDatabases = 2,

    /// <summary>
    /// No database could be identified, and not only because the key was refused; see
    /// <see cref="ProtectedStorageIdentityResult.FailureStatus"/>.
    /// </summary>
    Unreadable = 3,
}

public sealed record ProtectedStorageIdentityResult(
    ProtectedStorageIdentityStatus Status,
    Guid StorageId,
    ProtectedStorageDatabaseStatus FailureStatus = ProtectedStorageDatabaseStatus.Success)
{
    public bool IsIdentified => Status == ProtectedStorageIdentityStatus.Identified && StorageId != Guid.Empty;
}

public interface IProtectedStorageDatabaseService
{
    /// <summary>
    /// Tries the key on the storage databases whose header carries the key's salt, and returns
    /// the <c>StorageId</c> of the first one it opens (<c>docs/CRYPTOGRAPHY.md</c> §4). Nothing
    /// is written.
    /// </summary>
    Task<ProtectedStorageIdentityResult> IdentifyAsync(
        string dataRootPath,
        ReadOnlyMemory<byte> storageKey,
        CancellationToken cancellationToken = default);

    Task<ProtectedStorageDatabaseResult> InitializeOrValidateAsync(
        string dataRootPath,
        Guid storageId,
        ReadOnlyMemory<byte> masterKey,
        bool allowInitialize,
        CancellationToken cancellationToken = default);
}
