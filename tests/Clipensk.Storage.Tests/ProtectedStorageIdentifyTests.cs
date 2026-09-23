using System.Security.Cryptography;
using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

/// <summary>
/// The password check without a metadata file: a key is tried on the storage databases that carry
/// its salt (docs/CRYPTOGRAPHY.md §4). Plain SQLite stands in for SQLCipher here, so the "salt" of
/// every test database is the plain SQLite header and the factory refuses a wrong key the way
/// SQLCipher does, with SQLITE_NOTADB.
/// </summary>
public sealed class ProtectedStorageIdentifyTests : IDisposable
{
    private static readonly byte[] PlainSqliteHeader = "SQLite format 3\0"u8.ToArray();

    private readonly string _root;
    private readonly byte[] _masterKey = RandomNumberGenerator.GetBytes(StorageKeyMaterial.MasterKeyLengthBytes);
    private readonly byte[] _key;

    public ProtectedStorageIdentifyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Clipensk.Storage.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _key = StorageKeyMaterial.Compose(_masterKey, PlainSqliteHeader);
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_key);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task TheRightKey_NamesTheStorage()
    {
        var factory = new KeyCheckingConnectionFactory(_masterKey);
        Guid storageId = await CreateStorageAsync(factory);

        ProtectedStorageIdentityResult result =
            await new ProtectedStorageDatabaseService(factory).IdentifyAsync(_root, _key);

        Assert.Equal(ProtectedStorageIdentityStatus.Identified, result.Status);
        Assert.Equal(storageId, result.StorageId);
        Assert.True(result.IsIdentified);
        Assert.Equal([Current], factory.Opened.Select(Relative));
    }

    [Fact]
    public async Task AWrongKey_IsRefusedByEveryDatabase()
    {
        var factory = new KeyCheckingConnectionFactory(_masterKey);
        await CreateStorageAsync(factory);
        byte[] wrongKey = StorageKeyMaterial.Compose(
            RandomNumberGenerator.GetBytes(StorageKeyMaterial.MasterKeyLengthBytes),
            PlainSqliteHeader);
        factory.Opened.Clear();

        ProtectedStorageIdentityResult result =
            await new ProtectedStorageDatabaseService(factory).IdentifyAsync(_root, wrongKey);

        Assert.Equal(ProtectedStorageIdentityStatus.KeyRejected, result.Status);
        Assert.Equal(Guid.Empty, result.StorageId);
        Assert.Equal([Current, Catalog], factory.Opened.Select(Relative));
    }

    [Fact]
    public async Task DatabasesWithAnotherSalt_AreNotTried()
    {
        var factory = new KeyCheckingConnectionFactory(_masterKey);
        await CreateStorageAsync(factory);
        byte[] otherSalt = RandomNumberGenerator.GetBytes(StorageSalt.LengthBytes);
        factory.Opened.Clear();

        ProtectedStorageIdentityResult result = await new ProtectedStorageDatabaseService(factory)
            .IdentifyAsync(_root, StorageKeyMaterial.Compose(_masterKey, otherSalt));

        Assert.Equal(ProtectedStorageIdentityStatus.NoDatabases, result.Status);
        Assert.Empty(factory.Opened);
    }

    [Fact]
    public async Task AnEmptyDataRoot_HasNoDatabases()
    {
        ProtectedStorageIdentityResult result = await new ProtectedStorageDatabaseService(
            new KeyCheckingConnectionFactory(_masterKey)).IdentifyAsync(_root, _key);

        Assert.Equal(ProtectedStorageIdentityStatus.NoDatabases, result.Status);
    }

    [Fact]
    public async Task ADamagedCurrent_IsSkipped_AndTheCatalogNamesTheStorage()
    {
        var factory = new KeyCheckingConnectionFactory(_masterKey);
        Guid storageId = await CreateStorageAsync(factory);
        factory.Damaged.Add(Path.Combine(_root, Current));

        ProtectedStorageIdentityResult result =
            await new ProtectedStorageDatabaseService(factory).IdentifyAsync(_root, _key);

        Assert.Equal(ProtectedStorageIdentityStatus.Identified, result.Status);
        Assert.Equal(storageId, result.StorageId);
    }

    [Fact]
    public async Task EveryDatabaseDamaged_IsUnreadable_NotAWrongPassword()
    {
        var factory = new KeyCheckingConnectionFactory(_masterKey);
        await CreateStorageAsync(factory);
        factory.Damaged.Add(Path.Combine(_root, Current));
        factory.Damaged.Add(Path.Combine(_root, Catalog));

        ProtectedStorageIdentityResult result =
            await new ProtectedStorageDatabaseService(factory).IdentifyAsync(_root, _key);

        Assert.Equal(ProtectedStorageIdentityStatus.Unreadable, result.Status);
        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, result.FailureStatus);
    }

    [Fact]
    public async Task ASurvivingArchive_IsEnoughToNameTheStorage()
    {
        var factory = new KeyCheckingConnectionFactory(_masterKey);
        Guid storageId = await CreateStorageAsync(factory);
        Directory.CreateDirectory(Path.Combine(_root, "Archive"));
        File.Copy(Path.Combine(_root, Current), Path.Combine(_root, "Archive", "archive_000001.db"));
        Directory.Delete(Path.Combine(_root, "Current"), recursive: true);

        ProtectedStorageIdentityResult result =
            await new ProtectedStorageDatabaseService(factory).IdentifyAsync(_root, _key);

        Assert.Equal(ProtectedStorageIdentityStatus.Identified, result.Status);
        Assert.Equal(storageId, result.StorageId);
    }

    [Fact]
    public async Task ACopyCutShortByAPasswordChange_DoesNotTurnARefusedKeyIntoAnUnreadableStorage()
    {
        // Phase 1 of a password change was interrupted and the new password is entered: every
        // database refuses it, and the one copy there is was cut short. That is a wrong password
        // for the storage, not damage (docs/PASSWORD_CHANGE_PROTOCOL.md §5).
        var factory = new KeyCheckingConnectionFactory(_masterKey);
        await CreateStorageAsync(factory);
        string copy = Path.Combine(_root, Current) + StorageDatabaseFiles.PasswordChangeCopySuffix;
        File.Copy(Path.Combine(_root, Current), copy);
        factory.Rejecting.Add(Path.Combine(_root, Current));
        factory.Rejecting.Add(Path.Combine(_root, Catalog));
        factory.Damaged.Add(copy);

        ProtectedStorageIdentityResult result =
            await new ProtectedStorageDatabaseService(factory).IdentifyAsync(_root, _key);

        Assert.Equal(ProtectedStorageIdentityStatus.KeyRejected, result.Status);
    }

    [Fact]
    public async Task AKeyWithoutItsSalt_IsRejectedUpFront()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new ProtectedStorageDatabaseService(
            new KeyCheckingConnectionFactory(_masterKey)).IdentifyAsync(_root, _masterKey));
    }

    private static string Current => Path.Combine("Current", "current.db");

    private static string Catalog => Path.Combine("Current", "storage-catalog.db");

    private string Relative(string path) => Path.GetRelativePath(_root, path);

    private async Task<Guid> CreateStorageAsync(KeyCheckingConnectionFactory factory)
    {
        Guid storageId = Guid.NewGuid();
        ProtectedStorageDatabaseResult created = await new ProtectedStorageDatabaseService(factory)
            .InitializeOrValidateAsync(_root, storageId, _key, allowInitialize: true);
        Assert.True(created.IsSuccess);
        factory.Opened.Clear();
        return storageId;
    }

    private sealed class KeyCheckingConnectionFactory : IKeyedSqliteConnectionFactory
    {
        private const int SqliteCorrupt = 11;
        private const int SqliteNotADatabase = 26;

        private static readonly object ProviderGate = new();
        private static bool _initialized;

        private readonly byte[] _masterKey;

        public KeyCheckingConnectionFactory(byte[] masterKey)
        {
            _masterKey = masterKey;
            lock (ProviderGate)
            {
                if (!_initialized)
                {
                    SQLitePCL.Batteries.Init();
                    _initialized = true;
                }
            }
        }

        public List<string> Opened { get; } = [];

        public HashSet<string> Damaged { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Files that refuse every key, as databases encrypted with another key do.</summary>
        public HashSet<string> Rejecting { get; } = new(StringComparer.OrdinalIgnoreCase);

        public SqliteConnection Open(string databasePath, ReadOnlyMemory<byte> masterKey, SqliteOpenMode mode)
        {
            string path = Path.GetFullPath(databasePath);
            Opened.Add(path);
            if (Rejecting.Contains(path) ||
                !StorageKeyMaterial.GetMasterKey(masterKey.Span).SequenceEqual(_masterKey))
            {
                throw new SqliteException("file is not a database", SqliteNotADatabase);
            }
            if (Damaged.Contains(path))
            {
                throw new SqliteException("database disk image is malformed", SqliteCorrupt);
            }

            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = mode,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }
    }
}
