using System.Security.Cryptography;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedStorageDatabaseServiceTests
{
    [Fact]
    public async Task Initialize_CreatesCurrentAndCatalog_WithSelfDescribingIdentity()
    {
        string root = CreateTemporaryDirectory();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        Guid storageId = Guid.NewGuid();

        try
        {
            var factory = new PlainSqliteConnectionFactory();
            var service = new ProtectedStorageDatabaseService(factory);

            ProtectedStorageDatabaseResult result = await service.InitializeOrValidateAsync(
                root,
                storageId,
                key,
                allowInitialize: true);

            Assert.True(result.IsSuccess);
            Assert.True(result.WasInitialized);

            string current = Path.Combine(root, "Current", "current.db");
            string catalog = Path.Combine(root, "Current", "storage-catalog.db");
            Assert.True(File.Exists(current));
            Assert.True(File.Exists(catalog));
            Assert.True(Directory.Exists(Path.Combine(root, "Archive")));
            Assert.True(Directory.Exists(Path.Combine(root, "Files")));
            Assert.True(Directory.Exists(Path.Combine(root, "Trash")));
            Assert.True(Directory.Exists(Path.Combine(root, "Languages")));

            Assert.Equal(DatabaseRole.Current, ReadRole(factory, current, key, storageId));
            Assert.Equal(DatabaseRole.StorageCatalog, ReadRole(factory, catalog, key, storageId));

            ProtectedStorageDatabaseResult reopened = await service.InitializeOrValidateAsync(
                root,
                storageId,
                key,
                allowInitialize: false);
            Assert.True(reopened.IsSuccess);
            Assert.False(reopened.WasInitialized);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_RejectsDifferentStorageId()
    {
        string root = CreateTemporaryDirectory();
        byte[] key = RandomNumberGenerator.GetBytes(32);

        try
        {
            var service = new ProtectedStorageDatabaseService(new PlainSqliteConnectionFactory());
            Guid originalStorageId = Guid.NewGuid();

            Assert.True((await service.InitializeOrValidateAsync(
                root,
                originalStorageId,
                key,
                allowInitialize: true)).IsSuccess);

            ProtectedStorageDatabaseResult result = await service.InitializeOrValidateAsync(
                root,
                Guid.NewGuid(),
                key,
                allowInitialize: false);

            Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, result.Status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_RejectsPartialCurrentPair()
    {
        string root = CreateTemporaryDirectory();
        byte[] key = RandomNumberGenerator.GetBytes(32);

        try
        {
            var service = new ProtectedStorageDatabaseService(new PlainSqliteConnectionFactory());
            Guid storageId = Guid.NewGuid();

            Assert.True((await service.InitializeOrValidateAsync(
                root,
                storageId,
                key,
                allowInitialize: true)).IsSuccess);

            File.Delete(Path.Combine(root, "Current", "storage-catalog.db"));

            ProtectedStorageDatabaseResult result = await service.InitializeOrValidateAsync(
                root,
                storageId,
                key,
                allowInitialize: false);

            Assert.Equal(ProtectedStorageDatabaseStatus.MissingOrPartialStorage, result.Status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Initialize_RejectsArchiveEvidence_WhenCurrentPairIsMissing()
    {
        string root = CreateTemporaryDirectory();
        byte[] key = RandomNumberGenerator.GetBytes(32);

        try
        {
            string archive = Path.Combine(root, "Archive");
            Directory.CreateDirectory(archive);
            await File.WriteAllBytesAsync(Path.Combine(archive, "archive_000001.db"), [0x01]);

            var service = new ProtectedStorageDatabaseService(new PlainSqliteConnectionFactory());
            ProtectedStorageDatabaseResult result = await service.InitializeOrValidateAsync(
                root,
                Guid.NewGuid(),
                key,
                allowInitialize: true);

            Assert.Equal(ProtectedStorageDatabaseStatus.MissingOrPartialStorage, result.Status);
            Assert.False(Directory.Exists(Path.Combine(root, "Current")));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, recursive: true);
        }
    }

    private static DatabaseRole ReadRole(
        IKeyedSqliteConnectionFactory factory,
        string databasePath,
        ReadOnlyMemory<byte> key,
        Guid expectedStorageId)
    {
        using SqliteConnection connection = factory.Open(databasePath, key, SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT StorageId, DatabaseRole FROM DatabaseIdentity WHERE SingletonId = 1;";
        using SqliteDataReader reader = command.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(expectedStorageId.ToString("D"), reader.GetString(0));
        return Enum.Parse<DatabaseRole>(reader.GetString(1), ignoreCase: false);
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "Clipensk.Storage.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class PlainSqliteConnectionFactory : IKeyedSqliteConnectionFactory
    {
        private static readonly object ProviderGate = new();
        private static bool _initialized;

        public PlainSqliteConnectionFactory()
        {
            lock (ProviderGate)
            {
                if (!_initialized)
                {
                    SQLitePCL.Batteries.Init();
                    _initialized = true;
                }
            }
        }

        public SqliteConnection Open(
            string databasePath,
            ReadOnlyMemory<byte> masterKey,
            SqliteOpenMode mode)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(databasePath),
                Mode = mode,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }
    }
}
