using Clipensk.Core.Storage;

namespace Clipensk.Core.Security;

public enum ProtectedStorageCredentialState
{
    /// <summary>The data root holds no storage database: a password is set, not entered.</summary>
    Uninitialized = 0,

    /// <summary>Storage databases are present and carry a usable salt.</summary>
    Ready = 1,

    /// <summary>
    /// Storage databases are present but none carries a usable salt, or the data root is of the
    /// unsupported earlier format. Unlocking is refused.
    /// </summary>
    Invalid = 2,
}

public enum ProtectedStorageUnlockStatus
{
    Success = 0,
    InvalidPassword = 1,

    /// <summary>No usable storage: see <see cref="ProtectedStorageCredentialState.Invalid"/>.</summary>
    InvalidStorage = 2,

    /// <summary>
    /// The databases could not be read for a reason other than the password; see
    /// <see cref="ProtectedStorageUnlockResult.StorageStatus"/>.
    /// </summary>
    StorageUnavailable = 3,
}

/// <param name="IsNewStorage">
/// The data root held no storage database: the key is for a fresh salt and <paramref name="StorageId"/>
/// is new; the databases are still to be created.
/// </param>
/// <param name="MasterKey">The storage key (<see cref="StorageKeyMaterial"/>): MasterKey and salt.</param>
public sealed record ProtectedStorageUnlockResult(
    ProtectedStorageUnlockStatus Status,
    bool IsNewStorage,
    Guid StorageId,
    MasterKeyLease? MasterKey,
    ProtectedStorageDatabaseStatus StorageStatus = ProtectedStorageDatabaseStatus.Success)
{
    public bool IsSuccess =>
        Status == ProtectedStorageUnlockStatus.Success &&
        StorageId != Guid.Empty &&
        MasterKey is not null;
}

/// <summary>
/// Derives the storage key from the password. There is no metadata file: the salt is read from the
/// headers of the storage databases and the password is checked by opening one
/// (<c>docs/CRYPTOGRAPHY.md</c> §3, §4).
/// </summary>
public interface IProtectedStorageCredentialService
{
    Task<ProtectedStorageCredentialState> GetStateAsync(
        string dataRootPath,
        CancellationToken cancellationToken = default);

    /// <param name="allowInitialize">
    /// Whether a new storage may be started when the data root holds no database: true only when
    /// the password was entered as a new one. Otherwise an empty data root is
    /// <see cref="ProtectedStorageUnlockStatus.InvalidStorage"/>.
    /// </param>
    Task<ProtectedStorageUnlockResult> UnlockOrInitializeAsync(
        string dataRootPath,
        string password,
        bool allowInitialize,
        CancellationToken cancellationToken = default);
}
