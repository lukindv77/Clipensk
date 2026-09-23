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

/// <summary>How an interrupted password change was settled (<c>docs/PASSWORD_CHANGE_PROTOCOL.md</c> §5).</summary>
public enum PasswordChangeRecoveryOutcome
{
    NothingPending = 0,

    /// <summary>The key opens every database: the change never switched anything; its copies are gone.</summary>
    RolledBack = 1,

    /// <summary>Every database opens with the key itself or through its copy: the change was finished.</summary>
    Completed = 2,

    /// <summary>Part of the storage already switched to the new password; this one is the old one.</summary>
    NewPasswordRequired = 3,

    /// <summary>The change never switched anything and this is the new password: the old one still applies.</summary>
    OldPasswordStillValid = 4,
}

public interface IProtectedStorageDatabaseService
{
    /// <summary>
    /// Settles an interrupted password change with the key the password gave, before anything else
    /// opens the storage. Does nothing when no change is pending.
    /// </summary>
    Task<PasswordChangeRecoveryOutcome> ResolvePendingPasswordChangeAsync(
        string dataRootPath,
        ReadOnlyMemory<byte> storageKey,
        CancellationToken cancellationToken = default);

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
