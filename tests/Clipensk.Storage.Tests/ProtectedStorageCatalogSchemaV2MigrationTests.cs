using System.Globalization;
using System.Security.Cryptography;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedStorageCatalogSchemaV2MigrationTests
{
    [Fact]
    public async Task Validate_MigratesCatalogV1ToV2AndPreservesCurrentHistory()
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        environment.InsertCurrentHistoryMarker();
        environment.DowngradeCatalogToV1();

        ProtectedStorageDatabaseResult result = await environment.Service.InitializeOrValidateAsync(
            environment.Root,
            environment.StorageId,
            environment.Key,
            allowInitialize: false);

        Assert.True(result.IsSuccess);
        Assert.False(result.WasInitialized);
        Assert.Equal(2, environment.ReadSchemaVersion(environment.CatalogPath));
        Assert.True(environment.HasTable(environment.CatalogPath, "ExternalPayloadAddressIndex"));
        Assert.Equal(1, environment.CountRows(environment.CurrentPath, "ClipboardHistoryEvent"));
    }

    [Fact]
    public async Task Validate_MalformedCatalogV2FailsClosedAndPreservesCurrentHistory()
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        environment.InsertCurrentHistoryMarker();
        environment.DropCatalogRelativePathIndex();

        ProtectedStorageDatabaseResult result = await environment.Service.InitializeOrValidateAsync(
            environment.Root,
            environment.StorageId,
            environment.Key,
            allowInitialize: false);

        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, result.Status);
        Assert.Equal(2, environment.ReadSchemaVersion(environment.CatalogPath));
        Assert.Equal(1, environment.CountRows(environment.CurrentPath, "ClipboardHistoryEvent"));
    }

    [Fact]
    public async Task Validate_InvalidCurrentDoesNotMigrateLegacyCatalogV1()
    {
        using TestEnvironment environment = await TestEnvironment.CreateAsync();
        environment.DowngradeCatalogToV1();
        environment.CorruptCurrentStorageId();

        ProtectedStorageDatabaseResult result = await environment.Service.InitializeOrValidateAsync(
            environment.Root,
            environment.StorageId,
            environment.Key,
            allowInitialize: false);

        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, result.Status);
        Assert.Equal(1, environment.ReadSchemaVersion(environment.CatalogPath));
        Assert.False(environment.HasTable(environment.CatalogPath, "ExternalPayloadAddressIndex"));
    }

    private sealed class TestEnvironment : IDisposable
    {
        private TestEnvironment(
            string root,
            byte[] key,
            Guid storageId,
            PlainSqliteConnectionFactory factory,
            ProtectedStorageDatabaseService service)
        {
            Root = root;
            Key = key;
            StorageId = storageId;
            Factory = factory;
            Service = service;
        }

        public string Root { get; }
        public byte[] Key { get; }
        public Guid StorageId { get; }
        public PlainSqliteConnectionFactory Factory { get; }
        public ProtectedStorageDatabaseService Service { get; }

        public string CurrentPath => Path.Combine(Root, "Current", "current.db");
        public string CatalogPath => Path.Combine(Root, "Current", "storage-catalog.db");

        public static async Task<TestEnvironment> CreateAsync()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "Clipensk.Storage.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            byte[] key = RandomNumberGenerator.GetBytes(32);
            Guid storageId = Guid.NewGuid();
            var factory = new PlainSqliteConnectionFactory();
            var service = new ProtectedStorageDatabaseService(factory);

            ProtectedStorageDatabaseResult result = await service.InitializeOrValidateAsync(
                root,
                storageId,
                key,
                allowInitialize: true);
            Assert.True(result.IsSuccess);
            Assert.Equal(2, ReadSchemaVersion(factory, CatalogPathFor(root), key));

            return new TestEnvironment(root, key, storageId, factory, service);
        }

        public void InsertCurrentHistoryMarker()
        {
            using SqliteConnection connection = Factory.Open(CurrentPath, Key, SqliteOpenMode.ReadWrite);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ClipboardHistoryEvent (
                    EventId,
                    EventUtc,
                    LocalOffsetMinutes,
                    WindowsTimeZoneId,
                    CalendarDate,
                    SourceApplicationId,
                    SourceProcessId,
                    SourceExecutablePath,
                    SourceApplicationUserModelId)
                VALUES (
                    $eventId,
                    $eventUtc,
                    0,
                    'UTC',
                    '2026-09-06',
                    NULL,
                    NULL,
                    NULL,
                    NULL);
                """;
            command.Parameters.AddWithValue("$eventId", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue(
                "$eventUtc",
                new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero)
                    .ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        public void DowngradeCatalogToV1()
        {
            using SqliteConnection connection = Factory.Open(CatalogPath, Key, SqliteOpenMode.ReadWrite);
            using SqliteTransaction transaction = connection.BeginTransaction();

            using (SqliteCommand drop = connection.CreateCommand())
            {
                drop.Transaction = transaction;
                drop.CommandText = "DROP TABLE ExternalPayloadAddressIndex;";
                drop.ExecuteNonQuery();
            }

            using (SqliteCommand identity = connection.CreateCommand())
            {
                identity.Transaction = transaction;
                identity.CommandText = "UPDATE DatabaseIdentity SET SchemaVersion = 1 WHERE SingletonId = 1;";
                Assert.Equal(1, identity.ExecuteNonQuery());
            }

            using (SqliteCommand userVersion = connection.CreateCommand())
            {
                userVersion.Transaction = transaction;
                userVersion.CommandText = "PRAGMA user_version = 1;";
                userVersion.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        public void DropCatalogRelativePathIndex()
        {
            using SqliteConnection connection = Factory.Open(CatalogPath, Key, SqliteOpenMode.ReadWrite);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP INDEX UX_ExternalPayloadAddressIndex_RelativePath;";
            command.ExecuteNonQuery();
        }

        public void CorruptCurrentStorageId()
        {
            using SqliteConnection connection = Factory.Open(CurrentPath, Key, SqliteOpenMode.ReadWrite);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE DatabaseIdentity SET StorageId = $storageId WHERE SingletonId = 1;";
            command.Parameters.AddWithValue("$storageId", Guid.NewGuid().ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        public int ReadSchemaVersion(string path) => ReadSchemaVersion(Factory, path, Key);

        public bool HasTable(string path, string tableName)
        {
            using SqliteConnection connection = Factory.Open(path, Key, SqliteOpenMode.ReadOnly);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $tableName;";
            command.Parameters.AddWithValue("$tableName", tableName);
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }

        public int CountRows(string path, string tableName)
        {
            using SqliteConnection connection = Factory.Open(path, Key, SqliteOpenMode.ReadOnly);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Key);
            Directory.Delete(Root, recursive: true);
        }

        private static string CatalogPathFor(string root) =>
            Path.Combine(root, "Current", "storage-catalog.db");
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

    public sealed class PlainSqliteConnectionFactory : IKeyedSqliteConnectionFactory
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
