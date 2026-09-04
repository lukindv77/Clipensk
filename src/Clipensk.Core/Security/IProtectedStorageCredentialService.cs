namespace Clipensk.Core.Security;

public enum ProtectedStorageCredentialState
{
    Uninitialized = 0,
    Ready = 1,
    Invalid = 2,
}

public enum ProtectedStorageUnlockStatus
{
    Success = 0,
    InvalidPassword = 1,
    InvalidMetadata = 2,
}

public sealed record ProtectedStorageUnlockResult(
    ProtectedStorageUnlockStatus Status,
    bool WasInitialized,
    bool IsStorageInitialized,
    Guid StorageId,
    MasterKeyLease? MasterKey)
{
    public bool IsSuccess =>
        Status == ProtectedStorageUnlockStatus.Success &&
        StorageId != Guid.Empty &&
        MasterKey is not null;
}

public interface IProtectedStorageCredentialService
{
    Task<ProtectedStorageCredentialState> GetStateAsync(
        string dataRootPath,
        CancellationToken cancellationToken = default);

    Task<ProtectedStorageUnlockResult> UnlockOrInitializeAsync(
        string dataRootPath,
        string password,
        CancellationToken cancellationToken = default);

    Task MarkStorageInitializedAsync(
        string dataRootPath,
        Guid storageId,
        CancellationToken cancellationToken = default);
}
