using System.Globalization;
using Clipensk.Core.Application;
using Clipensk.Core.Applications;
using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using ClipenskApplicationId = Clipensk.Core.Applications.ApplicationId;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class SqliteApplicationIdentityCatalogTests
{
    [Fact]
    public async Task ReadAllAsync_ReturnsGroupedAliasesAndIdentitiesWithoutAliasesInStableOrder()
    {
        using TestDatabase database = TestDatabase.Create();
        var olderId = new ClipenskApplicationId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var newerId = new ClipenskApplicationId(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        DateTimeOffset olderCreated = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset newerCreated = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

        database.SeedIdentity(olderId, olderCreated);
        database.SeedIdentity(
            newerId,
            newerCreated,
            (ApplicationIdentitySqlSchema.ExecutablePathAliasType, "C:\\Apps\\Zulu.exe"),
            (ApplicationIdentitySqlSchema.AumidAliasType, "Contoso.App_123!App"),
            (ApplicationIdentitySqlSchema.ExecutablePathAliasType, "C:\\Apps\\Alpha.exe"));

        var catalog = database.CreateCatalog();

        IReadOnlyList<ApplicationIdentityCatalogEntry> result = await catalog.ReadAllAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal(newerId, result[0].ApplicationId);
        Assert.Equal(newerCreated, result[0].CreatedAtUtc);
        Assert.Equal(new[] { "Contoso.App_123!App" }, result[0].ApplicationUserModelIds);
        Assert.Equal(
            new[] { "C:\\Apps\\Alpha.exe", "C:\\Apps\\Zulu.exe" },
            result[0].ExecutablePaths);
        Assert.Equal(olderId, result[1].ApplicationId);
        Assert.Equal(olderCreated, result[1].CreatedAtUtc);
        Assert.Empty(result[1].ApplicationUserModelIds);
        Assert.Empty(result[1].ExecutablePaths);
    }

    [Fact]
    public async Task ReadAllAsync_RejectsMalformedPersistedIdentityTimestamp()
    {
        using TestDatabase database = TestDatabase.Create();
        database.InsertRawIdentity(Guid.NewGuid().ToString("D"), "not-a-timestamp");
        var catalog = database.CreateCatalog();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await catalog.ReadAllAsync();
        });
    }

    [Fact]
    public async Task ReadAllAsync_RejectsEmptyPersistedAliasValue()
    {
        using TestDatabase database = TestDatabase.Create();
        ClipenskApplicationId id = ClipenskApplicationId.New();
        DateTimeOffset created = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        database.SeedIdentity(id, created);
        database.InsertRawAlias(
            ApplicationIdentitySqlSchema.AumidAliasType,
            string.Empty,
            id.ToString(),
            created.ToString("O", CultureInfo.InvariantCulture));
        var catalog = database.CreateCatalog();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await catalog.ReadAllAsync();
        });
    }

    [Fact]
    public async Task ReadAllAsync_RejectsOrphanPersistedAlias()
    {
        using TestDatabase database = TestDatabase.Create();
        DateTimeOffset created = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        database.InsertRawAlias(
            ApplicationIdentitySqlSchema.ExecutablePathAliasType,
            "C:\\Apps\\Orphan.exe",
            Guid.NewGuid().ToString("D"),
            created.ToString("O", CultureInfo.InvariantCulture));
        var catalog = database.CreateCatalog();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await catalog.ReadAllAsync();
        });
    }

    [Fact]
    public async Task ReadAllAsync_AfterProtectedAccessRevocationCancelsBeforeRead()
    {
        using TestDatabase database = TestDatabase.Create();
        var catalog = database.CreateCatalog();
        Assert.True(database.Lifecycle.TryBeginLock());

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await catalog.ReadAllAsync();
        });
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly byte[] _key;
        private readonly PlainSqliteConnectionFactory _factory;
        private bool _disposed;

        private TestDatabase(string rootPath, Guid storageId)
        {
            RootPath = rootPath;
            StorageId = storageId;
            _key = Enumerable.Repeat((byte)0x5A, 32).ToArray();
            _factory = new PlainSqliteConnectionFactory();

            Lifecycle = new ProtectedApplicationLifecycle(isDataRootConfigured: true);
            Assert.True(Lifecycle.TryBeginUnlock());
            Lifecycle.CompleteUnlock();
            Session = ProtectedStorageSessionLease.Create(
                Lifecycle,
                rootPath,
                storageId,
                new MasterKeyLease(_key));

            string currentDirectory = Path.Combine(rootPath, "Current");
            Directory.CreateDirectory(currentDirectory);
            CurrentDatabasePath = Path.Combine(currentDirectory, "current.db");
            CreateCurrentDatabase();
        }

        public string RootPath { get; }

        public string CurrentDatabasePath { get; }

        public Guid StorageId { get; }

        public ProtectedApplicationLifecycle Lifecycle { get; }

        public ProtectedStorageSessionLease Session { get; }

        public static TestDatabase Create()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "Clipensk.Storage.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestDatabase(root, Guid.NewGuid());
        }

        public SqliteApplicationIdentityCatalog CreateCatalog() =>
            new(Session, _factory);

        public void SeedIdentity(
            ClipenskApplicationId applicationId,
            DateTimeOffset createdAtUtc,
            params (string AliasType, string AliasValue)[] aliases)
        {
            string createdAt = createdAtUtc.ToString("O", CultureInfo.InvariantCulture);
            using SqliteConnection connection = OpenWriteConnection();
            using SqliteTransaction transaction = connection.BeginTransaction();

            using (SqliteCommand insertIdentity = connection.CreateCommand())
            {
                insertIdentity.Transaction = transaction;
                insertIdentity.CommandText = """
                    INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
                    VALUES ($applicationId, $createdAtUtc);
                    """;
                insertIdentity.Parameters.AddWithValue("$applicationId", applicationId.ToString());
                insertIdentity.Parameters.AddWithValue("$createdAtUtc", createdAt);
                insertIdentity.ExecuteNonQuery();
            }

            foreach ((string aliasType, string aliasValue) in aliases)
            {
                InsertAlias(
                    connection,
                    transaction,
                    aliasType,
                    aliasValue,
                    applicationId.ToString(),
                    createdAt);
            }

            transaction.Commit();
        }

        public void InsertRawIdentity(string applicationId, string createdAtUtc)
        {
            using SqliteConnection connection = OpenWriteConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
                VALUES ($applicationId, $createdAtUtc);
                """;
            command.Parameters.AddWithValue("$applicationId", applicationId);
            command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc);
            command.ExecuteNonQuery();
        }

        public void InsertRawAlias(
            string aliasType,
            string aliasValue,
            string applicationId,
            string createdAtUtc)
        {
            using SqliteConnection connection = OpenWriteConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ApplicationIdentityAlias (
                    AliasType, AliasValue, ApplicationId, CreatedAtUtc)
                VALUES ($aliasType, $aliasValue, $applicationId, $createdAtUtc);
                """;
            command.Parameters.AddWithValue("$aliasType", aliasType);
            command.Parameters.AddWithValue("$aliasValue", aliasValue);
            command.Parameters.AddWithValue("$applicationId", applicationId);
            command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc);
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Session.Dispose();
            Directory.Delete(RootPath, recursive: true);
        }

        private SqliteConnection OpenWriteConnection() =>
            _factory.Open(
                CurrentDatabasePath,
                Session.DangerousGetMasterKeyMemory(),
                SqliteOpenMode.ReadWrite);

        private void CreateCurrentDatabase()
        {
            using SqliteConnection connection = _factory.Open(
                CurrentDatabasePath,
                Session.DangerousGetMasterKeyMemory(),
                SqliteOpenMode.ReadWriteCreate);
            using SqliteTransaction transaction = connection.BeginTransaction();

            using (SqliteCommand createIdentity = connection.CreateCommand())
            {
                createIdentity.Transaction = transaction;
                createIdentity.CommandText = """
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
                createIdentity.ExecuteNonQuery();
            }

            using (SqliteCommand insertIdentity = connection.CreateCommand())
            {
                insertIdentity.Transaction = transaction;
                insertIdentity.CommandText = """
                    INSERT INTO DatabaseIdentity (
                        SingletonId, StorageId, DatabaseId, DatabaseRole,
                        SchemaVersion, EncryptionVersion, CreatedAtUtc,
                        ArchiveBaseNumber, ArchiveSplitSequence,
                        CoverageStartDate, CoverageEndDate)
                    VALUES (1, $storageId, $databaseId, $role, $schemaVersion, 1,
                            $createdAtUtc, NULL, NULL, NULL, NULL);
                    """;
                insertIdentity.Parameters.AddWithValue("$storageId", StorageId.ToString("D"));
                insertIdentity.Parameters.AddWithValue("$databaseId", Guid.NewGuid().ToString("D"));
                insertIdentity.Parameters.AddWithValue("$role", DatabaseRole.Current.ToString());
                insertIdentity.Parameters.AddWithValue(
                    "$schemaVersion",
                    ApplicationIdentitySqlSchema.RequiredCurrentSchemaVersion);
                insertIdentity.Parameters.AddWithValue(
                    "$createdAtUtc",
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                insertIdentity.ExecuteNonQuery();
            }

            ApplicationIdentitySqlSchema.CreateTables(connection, transaction);

            using (SqliteCommand userVersion = connection.CreateCommand())
            {
                userVersion.Transaction = transaction;
                userVersion.CommandText =
                    $"PRAGMA user_version = {ApplicationIdentitySqlSchema.RequiredCurrentSchemaVersion};";
                userVersion.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        private static void InsertAlias(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string aliasType,
            string aliasValue,
            string applicationId,
            string createdAtUtc)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ApplicationIdentityAlias (
                    AliasType, AliasValue, ApplicationId, CreatedAtUtc)
                VALUES ($aliasType, $aliasValue, $applicationId, $createdAtUtc);
                """;
            command.Parameters.AddWithValue("$aliasType", aliasType);
            command.Parameters.AddWithValue("$aliasValue", aliasValue);
            command.Parameters.AddWithValue("$applicationId", applicationId);
            command.Parameters.AddWithValue("$createdAtUtc", createdAtUtc);
            command.ExecuteNonQuery();
        }
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
