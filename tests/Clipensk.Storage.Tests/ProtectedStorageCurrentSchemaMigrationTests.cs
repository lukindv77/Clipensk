using System.Globalization;
using System.Security.Cryptography;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedStorageCurrentSchemaMigrationTests
{
    [Fact]
    public async Task Initialize_CreatesCurrentV2AndCatalogV1()
    {
        string root = CreateTemporaryDirectory();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        Guid storageId = Guid.NewGuid();
        var factory = new PlainSqliteConnectionFactory();

        try
        {
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
            Assert.Equal(
                ProtectedStorageDatabaseService.CurrentSchemaVersion,
                ReadSchemaVersion(factory, current, key));
            Assert.Equal(
                ProtectedStorageDatabaseService.CatalogSchemaVersion,
                ReadSchemaVersion(factory, catalog, key));
            Assert.True(HasTable(factory, current, key, "ApplicationIdentity"));
            Assert.True(HasTable(factory, current, key, "ApplicationIdentityAlias"));
            Assert.False(HasTable(factory, catalog, key, "ApplicationIdentity"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_MigratesLegacyCurrentV1AfterPairValidation()
    {
        string root = CreateTemporaryDirectory();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        Guid storageId = Guid.NewGuid();
        var factory = new PlainSqliteConnectionFactory();

        try
        {
            CreateLegacyPair(factory, root, key, storageId, storageId);
            var service = new ProtectedStorageDatabaseService(factory);

            ProtectedStorageDatabaseResult result = await service.InitializeOrValidateAsync(
                root,
                storageId,
                key,
                allowInitialize: false);

            Assert.True(result.IsSuccess);
            Assert.False(result.WasInitialized);

            string current = Path.Combine(root, "Current", "current.db");
            string catalog = Path.Combine(root, "Current", "storage-catalog.db");
            Assert.Equal(
                ProtectedStorageDatabaseService.CurrentSchemaVersion,
                ReadSchemaVersion(factory, current, key));
            Assert.Equal(
                ProtectedStorageDatabaseService.CatalogSchemaVersion,
                ReadSchemaVersion(factory, catalog, key));
            Assert.True(HasTable(factory, current, key, "ApplicationIdentity"));
            Assert.True(HasTable(factory, current, key, "ApplicationIdentityAlias"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_InvalidCatalogDoesNotMutateLegacyCurrent()
    {
        string root = CreateTemporaryDirectory();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        Guid storageId = Guid.NewGuid();
        var factory = new PlainSqliteConnectionFactory();

        try
        {
            CreateLegacyPair(
                factory,
                root,
                key,
                currentStorageId: storageId,
                catalogStorageId: Guid.NewGuid());
            var service = new ProtectedStorageDatabaseService(factory);

            ProtectedStorageDatabaseResult result = await service.InitializeOrValidateAsync(
                root,
                storageId,
                key,
                allowInitialize: false);

            Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, result.Status);

            string current = Path.Combine(root, "Current", "current.db");
            Assert.Equal(1, ReadSchemaVersion(factory, current, key));
            Assert.False(HasTable(factory, current, key, "ApplicationIdentity"));
            Assert.False(HasTable(factory, current, key, "ApplicationIdentityAlias"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_RejectsCurrentV2WithoutApplicationIdentityTables()
    {
        string root = CreateTemporaryDirectory();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        Guid storageId = Guid.NewGuid();
        var factory = new PlainSqliteConnectionFactory();

        try
        {
            string currentDirectory = Path.Combine(root, "Current");
            Directory.CreateDirectory(currentDirectory);
            CreateDatabaseIdentity(
                factory,
                Path.Combine(currentDirectory, "current.db"),
                key,
                storageId,
                DatabaseRole.Current,
                ProtectedStorageDatabaseService.CurrentSchemaVersion);
            CreateDatabaseIdentity(
                factory,
                Path.Combine(currentDirectory, "storage-catalog.db"),
                key,
                storageId,
                DatabaseRole.StorageCatalog,
                ProtectedStorageDatabaseService.CatalogSchemaVersion);

            var service = new ProtectedStorageDatabaseService(factory);
            ProtectedStorageDatabaseResult result = await service.InitializeOrValidateAsync(
                root,
                storageId,
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

    private static void CreateLegacyPair(
        IKeyedSqliteConnectionFactory factory,
        string root,
        ReadOnlyMemory<byte> key,
        Guid currentStorageId,
        Guid catalogStorageId)
    {
        string currentDirectory = Path.Combine(root, "Current");
        Directory.CreateDirectory(currentDirectory);
        CreateDatabaseIdentity(
            factory,
            Path.Combine(currentDirectory, "current.db"),
            key,
            currentStorageId,
            DatabaseRole.Current,
            schemaVersion: 1);
        CreateDatabaseIdentity(
            factory,
            Path.Combine(currentDirectory, "storage-catalog.db"),
            key,
            catalogStorageId,
            DatabaseRole.StorageCatalog,
            schemaVersion: 1);
    }

    private static void CreateDatabaseIdentity(
        IKeyedSqliteConnectionFactory factory,
        string path,
        ReadOnlyMemory<byte> key,
        Guid storageId,
        DatabaseRole role,
        int schemaVersion)
    {
        using SqliteConnection connection = factory.Open(path, key, SqliteOpenMode.ReadWriteCreate);
        using SqliteTransaction transaction = connection.BeginTransaction();

        using (SqliteCommand create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = """
                CREATE TABLE DatabaseIdentity (
                    SingletonId INTEGER NOT NULL PRIMARY KEY CHECK (SingletonId = 1),
                    StorageId TEXT NOT NULL,
                    DatabaseId TEXT NOT NULL UNIQUE,
                    DatabaseRole TEXT NOT NULL,
                    SchemaVersion INTEGER NOT NULL,
                    EncryptionVersion INTEGER NOT NULL,
                    CreatedAtUtc TEXT NOT NULL,
                    ArchiveBaseNumber INTEGER NULL,
                    ArchiveSplitSequence INTEGER NULL,
                    CoverageStartDate TEXT NULL,
                    CoverageEndDate TEXT NULL
                );
                """;
            create.ExecuteNonQuery();
        }

        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO DatabaseIdentity (
                    SingletonId, StorageId, DatabaseId, DatabaseRole,
                    SchemaVersion, EncryptionVersion, CreatedAtUtc,
                    ArchiveBaseNumber, ArchiveSplitSequence,
                    CoverageStartDate, CoverageEndDate)
                VALUES (1, $storageId, $databaseId, $role, $schemaVersion, 1,
                        $createdAtUtc, NULL, NULL, NULL, NULL);
                """;
            insert.Parameters.AddWithValue("$storageId", storageId.ToString("D"));
            insert.Parameters.AddWithValue("$databaseId", Guid.NewGuid().ToString("D"));
            insert.Parameters.AddWithValue("$role", role.ToString());
            insert.Parameters.AddWithValue("$schemaVersion", schemaVersion);
            insert.Parameters.AddWithValue(
                "$createdAtUtc",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }

        using (SqliteCommand userVersion = connection.CreateCommand())
        {
            userVersion.Transaction = transaction;
            userVersion.CommandText = $"PRAGMA user_version = {schemaVersion};";
            userVersion.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static int ReadSchemaVersion(
        IKeyedSqliteConnectionFactory factory,
        string path,
        ReadOnlyMemory<byte> key)
    {
        using SqliteConnection connection = factory.Open(path, key, SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT SchemaVersion FROM DatabaseIdentity WHERE SingletonId = 1;";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool HasTable(
        IKeyedSqliteConnectionFactory factory,
        string path,
        ReadOnlyMemory<byte> key,
        string tableName)
    {
        using SqliteConnection connection = factory.Open(path, key, SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = $tableName;
            """;
        command.Parameters.AddWithValue("$tableName", tableName);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "Clipensk.Storage.Tests",
            Guid.NewGuid().ToString("N"));
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
