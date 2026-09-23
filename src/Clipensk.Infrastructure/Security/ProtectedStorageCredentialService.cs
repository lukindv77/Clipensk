using System.Security.Cryptography;
using System.Text;
using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Konscious.Security.Cryptography;

namespace Clipensk.Infrastructure.Security;

/// <summary>
/// Derives the storage key without any metadata file: the salt comes from the headers of the
/// storage databases, the password is checked by opening one of them
/// (<c>docs/CRYPTOGRAPHY.md</c> §3, §4).
/// </summary>
public sealed class ProtectedStorageCredentialService : IProtectedStorageCredentialService
{
    private readonly IProtectedStorageDatabaseService _databaseService;
    private readonly KeyDerivationProfile _initializationProfile;
    private readonly IReadOnlyDictionary<int, KeyDerivationProfile> _supportedProfiles;

    public ProtectedStorageCredentialService(
        IProtectedStorageDatabaseService databaseService,
        KeyDerivationProfile? initializationProfile = null)
    {
        ArgumentNullException.ThrowIfNull(databaseService);
        _databaseService = databaseService;
        _initializationProfile = initializationProfile ?? KeyDerivationProfile.ProductionV1;
        _initializationProfile.Validate();
        if (_initializationProfile.SaltLengthBytes != StorageSalt.LengthBytes ||
            _initializationProfile.MasterKeyLengthBytes != StorageKeyMaterial.MasterKeyLengthBytes)
        {
            throw new ArgumentException(
                "Профиль KDF хранилища должен давать 32-байтовый MasterKey из 16-байтовой соли.",
                nameof(initializationProfile));
        }

        // A later profile is added here next to the current one, so storages created with either
        // keep opening (docs/CRYPTOGRAPHY.md §2).
        _supportedProfiles = new Dictionary<int, KeyDerivationProfile>
        {
            [_initializationProfile.ProfileVersion] = _initializationProfile,
        };
    }

    public Task<ProtectedStorageCredentialState> GetStateAsync(
        string dataRootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);

        return Task.Run(() =>
        {
            StorageScan scan = Scan(Path.GetFullPath(dataRootPath));
            if (scan.IsLegacy)
            {
                return ProtectedStorageCredentialState.Invalid;
            }

            if (scan.DatabaseCount == 0)
            {
                return ProtectedStorageCredentialState.Uninitialized;
            }

            return scan.Salts.Count == 0
                ? ProtectedStorageCredentialState.Invalid
                : ProtectedStorageCredentialState.Ready;
        }, cancellationToken);
    }

    public async Task<ProtectedStorageUnlockResult> UnlockOrInitializeAsync(
        string dataRootPath,
        string password,
        bool allowInitialize,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        ArgumentException.ThrowIfNullOrEmpty(password);

        string normalizedRoot = Path.GetFullPath(dataRootPath);
        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException(normalizedRoot);
        }

        StorageScan scan = await Task.Run(() => Scan(normalizedRoot), cancellationToken);
        if (scan.IsLegacy)
        {
            return Failure(ProtectedStorageUnlockStatus.InvalidStorage);
        }

        if (scan.DatabaseCount == 0)
        {
            // An empty data root starts a new storage only when the password was entered as a new
            // one; one that was an existing storage when the screen was shown is never replaced.
            return allowInitialize
                ? await InitializeAsync(password, cancellationToken)
                : Failure(ProtectedStorageUnlockStatus.InvalidStorage);
        }

        if (scan.Salts.Count == 0)
        {
            return Failure(ProtectedStorageUnlockStatus.InvalidStorage);
        }

        foreach (byte[] salt in scan.Salts)
        {
            KeyDerivationProfile profile = _supportedProfiles[StorageSalt.GetProfileVersion(salt)];
            byte[]? storageKey = await DeriveStorageKeyAsync(password, salt, profile, cancellationToken);
            try
            {
                ProtectedStorageIdentityResult identity =
                    await _databaseService.IdentifyAsync(normalizedRoot, storageKey, cancellationToken);
                switch (identity.Status)
                {
                    case ProtectedStorageIdentityStatus.Identified when identity.IsIdentified:
                        // An interrupted password change is settled before anything opens the
                        // storage (PASSWORD_CHANGE_PROTOCOL.md §5).
                        PasswordChangeRecoveryOutcome pending;
                        try
                        {
                            pending = await _databaseService.ResolvePendingPasswordChangeAsync(
                                normalizedRoot,
                                storageKey,
                                cancellationToken);
                        }
                        catch (Exception exception) when (
                            exception is IOException or UnauthorizedAccessException or InvalidDataException)
                        {
                            return Failure(
                                ProtectedStorageUnlockStatus.StorageUnavailable,
                                ProtectedStorageDatabaseStatus.StorageFailure);
                        }

                        if (pending == PasswordChangeRecoveryOutcome.NewPasswordRequired)
                        {
                            return Failure(ProtectedStorageUnlockStatus.NewPasswordRequired);
                        }

                        if (pending == PasswordChangeRecoveryOutcome.OldPasswordStillValid)
                        {
                            // Only the unfinished copies accept this password: it is not the storage's.
                            continue;
                        }

                        var lease = new MasterKeyLease(storageKey);
                        storageKey = null;
                        return new ProtectedStorageUnlockResult(
                            ProtectedStorageUnlockStatus.Success,
                            IsNewStorage: false,
                            identity.StorageId,
                            lease);

                    case ProtectedStorageIdentityStatus.KeyRejected:
                        // Wrong password for this salt; a less common salt may still be the
                        // storage's own when the most common one came from damaged headers.
                        continue;

                    case ProtectedStorageIdentityStatus.NoDatabases:
                        // The databases went away between the scan and the attempt.
                        return Failure(ProtectedStorageUnlockStatus.InvalidStorage);

                    default:
                        return Failure(
                            ProtectedStorageUnlockStatus.StorageUnavailable,
                            identity.FailureStatus == ProtectedStorageDatabaseStatus.Success
                                ? ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity
                                : identity.FailureStatus);
                }
            }
            finally
            {
                if (storageKey is not null)
                {
                    CryptographicOperations.ZeroMemory(storageKey);
                }
            }
        }

        return Failure(ProtectedStorageUnlockStatus.InvalidPassword);
    }

    public async Task<MasterKeyLease> DeriveStorageKeyAsync(
        string password,
        ReadOnlyMemory<byte> salt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        if (!_supportedProfiles.TryGetValue(StorageSalt.GetProfileVersion(salt.Span), out KeyDerivationProfile? profile))
        {
            throw new NotSupportedException("The storage salt names an unknown key derivation profile.");
        }

        return new MasterKeyLease(
            await DeriveStorageKeyAsync(password, salt.ToArray(), profile, cancellationToken));
    }

    private async Task<ProtectedStorageUnlockResult> InitializeAsync(
        string password,
        CancellationToken cancellationToken)
    {
        byte[] salt = StorageSalt.Create(_initializationProfile);
        byte[] storageKey = await DeriveStorageKeyAsync(password, salt, _initializationProfile, cancellationToken);
        return new ProtectedStorageUnlockResult(
            ProtectedStorageUnlockStatus.Success,
            IsNewStorage: true,
            Guid.NewGuid(),
            new MasterKeyLease(storageKey));
    }

    private StorageScan Scan(string normalizedRoot)
    {
        bool isLegacy = File.Exists(Path.Combine(normalizedRoot, StorageDatabaseFiles.LegacyCryptoMetadataFileName));
        IReadOnlyList<string> databases = StorageDatabaseFiles.EnumerateExisting(normalizedRoot);

        // Distinct usable salts, the one most databases carry first and, between equally common
        // ones, the one met first (Current, Catalog, then archives by name).
        var salts = new List<(byte[] Salt, int Count, int FirstIndex)>();
        byte[] header = new byte[StorageSalt.LengthBytes];
        for (int index = 0; index < databases.Count; index++)
        {
            if (!StorageDatabaseFiles.TryReadSalt(databases[index], header) ||
                !_supportedProfiles.ContainsKey(StorageSalt.GetProfileVersion(header)))
            {
                continue;
            }

            int existing = salts.FindIndex(entry => entry.Salt.AsSpan().SequenceEqual(header));
            if (existing < 0)
            {
                salts.Add(((byte[])header.Clone(), 1, index));
            }
            else
            {
                salts[existing] = salts[existing] with { Count = salts[existing].Count + 1 };
            }
        }

        return new StorageScan(
            isLegacy,
            databases.Count,
            salts
                .OrderByDescending(static entry => entry.Count)
                .ThenBy(static entry => entry.FirstIndex)
                .Select(static entry => entry.Salt)
                .ToArray());
    }

    private static Task<byte[]> DeriveStorageKeyAsync(
        string password,
        byte[] salt,
        KeyDerivationProfile profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
            byte[]? masterKey = null;
            try
            {
                using var argon2 = new Argon2id(passwordBytes)
                {
                    Salt = salt,
                    MemorySize = profile.MemoryKiB,
                    Iterations = profile.Iterations,
                    DegreeOfParallelism = profile.Parallelism,
                };

                masterKey = argon2.GetBytes(profile.MasterKeyLengthBytes);
                return StorageKeyMaterial.Compose(masterKey, salt);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
                if (masterKey is not null)
                {
                    CryptographicOperations.ZeroMemory(masterKey);
                }
            }
        }, cancellationToken);
    }

    private static ProtectedStorageUnlockResult Failure(
        ProtectedStorageUnlockStatus status,
        ProtectedStorageDatabaseStatus storageStatus = ProtectedStorageDatabaseStatus.Success) =>
        new(status, IsNewStorage: false, Guid.Empty, MasterKey: null, storageStatus);

    private sealed record StorageScan(bool IsLegacy, int DatabaseCount, IReadOnlyList<byte[]> Salts);
}
