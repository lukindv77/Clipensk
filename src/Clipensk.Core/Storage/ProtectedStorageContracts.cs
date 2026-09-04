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

public interface IProtectedStorageDatabaseService
{
    Task<ProtectedStorageDatabaseResult> InitializeOrValidateAsync(
        string dataRootPath,
        Guid storageId,
        ReadOnlyMemory<byte> masterKey,
        bool allowInitialize,
        CancellationToken cancellationToken = default);
}
