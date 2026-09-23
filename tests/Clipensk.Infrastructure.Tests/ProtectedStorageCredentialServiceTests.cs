using System.Security.Cryptography;
using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Clipensk.Infrastructure.Security;
using Xunit;

namespace Clipensk.Infrastructure.Tests;

/// <summary>
/// The storage key without a metadata file (docs/CRYPTOGRAPHY.md §3, §4): the salt is read from
/// the headers of the storage databases, and the password is checked by the database service.
/// </summary>
public sealed class ProtectedStorageCredentialServiceTests : IDisposable
{
    private const string Password = "пароль-Clipensk-1";

    private static readonly KeyDerivationProfile TestProfile = new(
        ProfileVersion: 1,
        Argon2Version: 0x13,
        MemoryKiB: 19_456,
        Iterations: 2,
        Parallelism: 1,
        SaltLengthBytes: 16,
        MasterKeyLengthBytes: 32);

    private readonly string _root;
    private readonly FakeDatabaseService _databases = new();
    private readonly ProtectedStorageCredentialService _service;

    public ProtectedStorageCredentialServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Clipensk.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _service = new ProtectedStorageCredentialService(_databases, TestProfile);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ANewStorage_GetsAFreshSaltWithItsProfileNumber_AndOpensAgainFromItsDatabases()
    {
        Assert.Equal(ProtectedStorageCredentialState.Uninitialized, await _service.GetStateAsync(_root));

        ProtectedStorageUnlockResult created =
            await _service.UnlockOrInitializeAsync(_root, Password, allowInitialize: true);

        Assert.True(created.IsSuccess);
        Assert.True(created.IsNewStorage);
        Assert.NotEqual(Guid.Empty, created.StorageId);
        byte[] key = created.MasterKey!.DangerousGetMemory().ToArray();
        created.MasterKey.Dispose();
        Assert.Equal(StorageKeyMaterial.LengthBytes, key.Length);
        byte[] salt = StorageKeyMaterial.GetSalt(key).ToArray();
        Assert.Equal(TestProfile.ProfileVersion, StorageSalt.GetProfileVersion(salt));
        Assert.Empty(_databases.TriedSalts);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));

        // SQLCipher writes the salt into every database it creates with this key.
        WriteDatabase(StorageDatabaseFiles.CurrentRelativePath, salt);
        WriteDatabase(StorageDatabaseFiles.CatalogRelativePath, salt);
        _databases.Accept(key, created.StorageId);
        Assert.Equal(ProtectedStorageCredentialState.Ready, await _service.GetStateAsync(_root));

        ProtectedStorageUnlockResult reopened =
            await _service.UnlockOrInitializeAsync(_root, Password, allowInitialize: false);

        Assert.True(reopened.IsSuccess);
        Assert.False(reopened.IsNewStorage);
        Assert.Equal(created.StorageId, reopened.StorageId);
        Assert.Equal(key, reopened.MasterKey!.DangerousGetMemory().ToArray());
        reopened.MasterKey.Dispose();

        ProtectedStorageUnlockResult wrong =
            await _service.UnlockOrInitializeAsync(_root, "другой-пароль", allowInitialize: false);

        Assert.Equal(ProtectedStorageUnlockStatus.InvalidPassword, wrong.Status);
        Assert.Null(wrong.MasterKey);
        Assert.Equal(Guid.Empty, wrong.StorageId);
    }

    [Fact]
    public async Task ExistingDatabases_AreNeverOfferedANewPassword()
    {
        // The danger this replaces: with storage-crypto.json lost, the old design offered to set a
        // password and created new metadata beside the untouched databases.
        byte[] key = await CreateStorageKeyAsync();
        WriteDatabase(StorageDatabaseFiles.CurrentRelativePath, StorageKeyMaterial.GetSalt(key).ToArray());
        _databases.Accept(key, Guid.NewGuid());

        ProtectedStorageUnlockResult result =
            await _service.UnlockOrInitializeAsync(_root, "совсем-новый-пароль", allowInitialize: true);

        Assert.Equal(ProtectedStorageUnlockStatus.InvalidPassword, result.Status);
        Assert.False(result.IsNewStorage);
        Assert.Null(result.MasterKey);
        Assert.Single(_databases.TriedSalts);
    }

    [Fact]
    public async Task AnEmptyDataRoot_IsNotInitialized_UnlessThePasswordWasEnteredAsANewOne()
    {
        ProtectedStorageUnlockResult result =
            await _service.UnlockOrInitializeAsync(_root, Password, allowInitialize: false);

        Assert.Equal(ProtectedStorageUnlockStatus.InvalidStorage, result.Status);
        Assert.Null(result.MasterKey);
    }

    [Fact]
    public async Task ASurvivingArchive_IsEnoughToDeriveTheKey()
    {
        byte[] key = await CreateStorageKeyAsync();
        Guid storageId = Guid.NewGuid();
        WriteDatabase(Path.Combine("Archive", "archive_000001.db"), StorageKeyMaterial.GetSalt(key).ToArray());
        _databases.Accept(key, storageId);

        Assert.Equal(ProtectedStorageCredentialState.Ready, await _service.GetStateAsync(_root));
        ProtectedStorageUnlockResult result =
            await _service.UnlockOrInitializeAsync(_root, Password, allowInitialize: false);

        Assert.True(result.IsSuccess);
        Assert.Equal(storageId, result.StorageId);
        result.MasterKey!.Dispose();
    }

    [Fact]
    public async Task TheSaltMostDatabasesCarry_IsTriedFirst()
    {
        byte[] key = await CreateStorageKeyAsync();
        byte[] salt = StorageKeyMaterial.GetSalt(key).ToArray();
        byte[] damaged = OtherSaltOfTheSameProfile(salt);
        WriteDatabase(StorageDatabaseFiles.CurrentRelativePath, damaged);
        WriteDatabase(StorageDatabaseFiles.CatalogRelativePath, salt);
        WriteDatabase(Path.Combine("Archive", "archive_000001.db"), salt);
        _databases.Accept(key, Guid.NewGuid());

        ProtectedStorageUnlockResult result =
            await _service.UnlockOrInitializeAsync(_root, Password, allowInitialize: false);

        Assert.True(result.IsSuccess);
        Assert.Equal([salt], _databases.TriedSalts);
        result.MasterKey!.Dispose();
    }

    [Fact]
    public async Task ARejectedSalt_GivesWayToTheNextOne()
    {
        // One damaged header against one good one: Current is consulted first, is refused, and the
        // Catalog's salt still opens the storage instead of reporting a wrong password.
        byte[] key = await CreateStorageKeyAsync();
        byte[] salt = StorageKeyMaterial.GetSalt(key).ToArray();
        byte[] damaged = OtherSaltOfTheSameProfile(salt);
        WriteDatabase(StorageDatabaseFiles.CurrentRelativePath, damaged);
        WriteDatabase(StorageDatabaseFiles.CatalogRelativePath, salt);
        _databases.Accept(key, Guid.NewGuid());

        ProtectedStorageUnlockResult result =
            await _service.UnlockOrInitializeAsync(_root, Password, allowInitialize: false);

        Assert.True(result.IsSuccess);
        Assert.Equal([damaged, salt], _databases.TriedSalts);
        result.MasterKey!.Dispose();
    }

    [Fact]
    public async Task DatabasesWithoutAUsableSalt_AreInvalid_AndNotTakenForAnEmptyDataRoot()
    {
        byte[] unknownProfile = RandomNumberGenerator.GetBytes(StorageSalt.LengthBytes);
        unknownProfile[0] = 0xEE;
        WriteDatabase(StorageDatabaseFiles.CurrentRelativePath, unknownProfile);
        Directory.CreateDirectory(Path.Combine(_root, "Current"));
        await File.WriteAllBytesAsync(Path.Combine(_root, StorageDatabaseFiles.CatalogRelativePath), [0x01, 0x02]);

        Assert.Equal(ProtectedStorageCredentialState.Invalid, await _service.GetStateAsync(_root));
        ProtectedStorageUnlockResult result =
            await _service.UnlockOrInitializeAsync(_root, Password, allowInitialize: true);

        Assert.Equal(ProtectedStorageUnlockStatus.InvalidStorage, result.Status);
        Assert.Null(result.MasterKey);
        Assert.Empty(_databases.TriedSalts);
    }

    [Fact]
    public async Task TheEarlierFormat_IsInvalid_EvenWithoutDatabases()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_root, StorageDatabaseFiles.LegacyCryptoMetadataFileName),
            "{ \"SchemaVersion\": 1 }");

        Assert.Equal(ProtectedStorageCredentialState.Invalid, await _service.GetStateAsync(_root));
        ProtectedStorageUnlockResult result =
            await _service.UnlockOrInitializeAsync(_root, Password, allowInitialize: true);

        Assert.Equal(ProtectedStorageUnlockStatus.InvalidStorage, result.Status);
        Assert.Null(result.MasterKey);
        Assert.Equal(
            ["storage-crypto.json"],
            Directory.EnumerateFileSystemEntries(_root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task UnreadableDatabases_AreReportedAsSuch_NotAsAWrongPassword()
    {
        byte[] key = await CreateStorageKeyAsync();
        WriteDatabase(StorageDatabaseFiles.CurrentRelativePath, StorageKeyMaterial.GetSalt(key).ToArray());
        _databases.Forced = new ProtectedStorageIdentityResult(
            ProtectedStorageIdentityStatus.Unreadable,
            Guid.Empty,
            ProtectedStorageDatabaseStatus.StorageFailure);

        ProtectedStorageUnlockResult result =
            await _service.UnlockOrInitializeAsync(_root, Password, allowInitialize: false);

        Assert.Equal(ProtectedStorageUnlockStatus.StorageUnavailable, result.Status);
        Assert.Equal(ProtectedStorageDatabaseStatus.StorageFailure, result.StorageStatus);
        Assert.Null(result.MasterKey);
    }

    [Fact]
    public void AProfileThatDoesNotFitTheStorageFormat_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new ProtectedStorageCredentialService(
            _databases,
            TestProfile with { SaltLengthBytes = 32 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProtectedStorageCredentialService(
            _databases,
            TestProfile with { ProfileVersion = 256 }));
    }

    /// <summary>The key a new storage in this data root gets for <see cref="Password"/>.</summary>
    private async Task<byte[]> CreateStorageKeyAsync()
    {
        ProtectedStorageUnlockResult created =
            await _service.UnlockOrInitializeAsync(_root, Password, allowInitialize: true);
        Assert.True(created.IsNewStorage);
        using MasterKeyLease lease = created.MasterKey!;
        return lease.DangerousGetMemory().ToArray();
    }

    private static byte[] OtherSaltOfTheSameProfile(byte[] salt)
    {
        byte[] other = RandomNumberGenerator.GetBytes(StorageSalt.LengthBytes);
        other[0] = salt[0];
        return other;
    }

    private void WriteDatabase(string relativePath, byte[] salt)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [.. salt, .. RandomNumberGenerator.GetBytes(4096 - salt.Length)]);
    }

    private sealed class FakeDatabaseService : IProtectedStorageDatabaseService
    {
        private byte[]? _acceptedKey;
        private Guid _storageId;

        public List<byte[]> TriedSalts { get; } = [];

        public ProtectedStorageIdentityResult? Forced { get; set; }

        public void Accept(byte[] key, Guid storageId)
        {
            _acceptedKey = key;
            _storageId = storageId;
        }

        public Task<ProtectedStorageIdentityResult> IdentifyAsync(
            string dataRootPath,
            ReadOnlyMemory<byte> storageKey,
            CancellationToken cancellationToken = default)
        {
            TriedSalts.Add(StorageKeyMaterial.GetSalt(storageKey.Span).ToArray());
            return Task.FromResult(
                Forced ??
                (_acceptedKey is not null && storageKey.Span.SequenceEqual(_acceptedKey)
                    ? new ProtectedStorageIdentityResult(ProtectedStorageIdentityStatus.Identified, _storageId)
                    : new ProtectedStorageIdentityResult(ProtectedStorageIdentityStatus.KeyRejected, Guid.Empty)));
        }

        public Task<ProtectedStorageDatabaseResult> InitializeOrValidateAsync(
            string dataRootPath,
            Guid storageId,
            ReadOnlyMemory<byte> masterKey,
            bool allowInitialize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
