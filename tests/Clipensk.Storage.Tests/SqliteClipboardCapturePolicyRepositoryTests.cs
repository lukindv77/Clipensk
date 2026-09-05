using System.Globalization;
using Clipensk.Core.Application;
using Clipensk.Core.Clipboard;
using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.Tests;

public sealed class SqliteClipboardCapturePolicyRepositoryTests
{
    [Fact]
    public async Task GetGlobalPolicyAsync_ReturnsExplicitPolicyWithoutDatabaseDefault()
    {
        using TestDatabase database = TestDatabase.Create();
        var globalPolicy = new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny);
        var repository = database.CreateRepository(globalPolicy);

        ClipboardCapturePolicy result = await repository.GetGlobalPolicyAsync();

        Assert.Same(globalPolicy, result);
    }

    [Fact]
    public async Task SetAndGetApplicationPolicy_RoundTripsRulesFormatsAndLimits()
    {
        using TestDatabase database = TestDatabase.Create();
        DurableApplicationId applicationId = DurableApplicationId.New();
        database.SeedApplication(applicationId);
        var repository = database.CreateRepository(
            new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny));
        var policy = new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Allow,
            new Dictionary<string, ClipboardFormatCapturePolicy>
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Inherit, 1024),
                ["HTML Format"] = new(ClipboardCapturePolicyRule.Deny),
            });

        await repository.SetApplicationPolicyAsync(applicationId, policy);
        ClipboardCapturePolicy? stored = await repository.GetApplicationPolicyAsync(applicationId);

        Assert.NotNull(stored);
        Assert.Equal(ClipboardCapturePolicyRule.Allow, stored.Capture);
        Assert.Equal(2, stored.Formats.Count);
        Assert.Equal(
            new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Inherit, 1024),
            stored.Formats["Text"]);
        Assert.Equal(
            new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Deny),
            stored.Formats["HTML Format"]);
    }

    [Fact]
    public async Task GetApplicationPolicyAsync_NoOverrideReturnsNull()
    {
        using TestDatabase database = TestDatabase.Create();
        DurableApplicationId applicationId = DurableApplicationId.New();
        database.SeedApplication(applicationId);
        var repository = database.CreateRepository(
            new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));

        ClipboardCapturePolicy? stored = await repository.GetApplicationPolicyAsync(applicationId);

        Assert.Null(stored);
    }

    [Fact]
    public async Task SetApplicationPolicyAsync_ReplacesPriorFormatRowsInOnePolicySnapshot()
    {
        using TestDatabase database = TestDatabase.Create();
        DurableApplicationId applicationId = DurableApplicationId.New();
        database.SeedApplication(applicationId);
        var repository = database.CreateRepository(
            new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));

        await repository.SetApplicationPolicyAsync(
            applicationId,
            new ClipboardCapturePolicy(
                ClipboardCapturePolicyRule.Allow,
                new Dictionary<string, ClipboardFormatCapturePolicy>
                {
                    ["Text"] = new(ClipboardCapturePolicyRule.Allow, 256),
                    ["HTML Format"] = new(ClipboardCapturePolicyRule.Allow, 512),
                }));
        await repository.SetApplicationPolicyAsync(
            applicationId,
            new ClipboardCapturePolicy(
                ClipboardCapturePolicyRule.Deny,
                new Dictionary<string, ClipboardFormatCapturePolicy>
                {
                    ["Rich Text Format"] = new(ClipboardCapturePolicyRule.Inherit, 2048),
                }));

        ClipboardCapturePolicy? stored = await repository.GetApplicationPolicyAsync(applicationId);

        Assert.NotNull(stored);
        Assert.Equal(ClipboardCapturePolicyRule.Deny, stored.Capture);
        Assert.Single(stored.Formats);
        Assert.Equal(
            new ClipboardFormatCapturePolicy(ClipboardCapturePolicyRule.Inherit, 2048),
            stored.Formats["Rich Text Format"]);
        Assert.Equal(1, database.CountRows("ApplicationFormatCapturePolicy"));
    }

    [Fact]
    public async Task DeleteApplicationPolicyAsync_RemovesPolicyAndCascadesFormats()
    {
        using TestDatabase database = TestDatabase.Create();
        DurableApplicationId applicationId = DurableApplicationId.New();
        database.SeedApplication(applicationId);
        var repository = database.CreateRepository(
            new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));
        await repository.SetApplicationPolicyAsync(
            applicationId,
            new ClipboardCapturePolicy(
                ClipboardCapturePolicyRule.Allow,
                new Dictionary<string, ClipboardFormatCapturePolicy>
                {
                    ["Text"] = new(ClipboardCapturePolicyRule.Allow),
                }));

        await repository.DeleteApplicationPolicyAsync(applicationId);

        Assert.Null(await repository.GetApplicationPolicyAsync(applicationId));
        Assert.Equal(0, database.CountRows("ApplicationCapturePolicy"));
        Assert.Equal(0, database.CountRows("ApplicationFormatCapturePolicy"));
    }

    [Fact]
    public async Task SetApplicationPolicyAsync_UnknownApplicationIdCannotCreateOrphanPolicy()
    {
        using TestDatabase database = TestDatabase.Create();
        var repository = database.CreateRepository(
            new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));

        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await repository.SetApplicationPolicyAsync(
                DurableApplicationId.New(),
                new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny));
        });

        Assert.Equal(0, database.CountRows("ApplicationCapturePolicy"));
    }

    [Fact]
    public async Task Repository_RejectsCurrentSchemaVersionTwoWithoutPolicyTables()
    {
        using TestDatabase database = TestDatabase.Create(schemaVersion: 2);
        DurableApplicationId applicationId = DurableApplicationId.New();
        database.SeedApplication(applicationId);
        var repository = database.CreateRepository(
            new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await repository.GetApplicationPolicyAsync(applicationId);
        });
    }

    [Fact]
    public async Task Repository_AfterProtectedAccessRevocationCancelsBeforeDatabaseAccess()
    {
        using TestDatabase database = TestDatabase.Create();
        DurableApplicationId applicationId = DurableApplicationId.New();
        database.SeedApplication(applicationId);
        var repository = database.CreateRepository(
            new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));
        Assert.True(database.Lifecycle.TryBeginLock());

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await repository.GetApplicationPolicyAsync(applicationId);
        });
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await repository.SetApplicationPolicyAsync(
                applicationId,
                new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Deny));
        });
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly byte[] _key;
        private readonly PlainSqliteConnectionFactory _factory;
        private bool _disposed;

        private TestDatabase(string rootPath, Guid storageId, int schemaVersion)
        {
            RootPath = rootPath;
            StorageId = storageId;
            _key = Enumerable.Repeat((byte)0x33, 32).ToArray();
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
            CreateCurrentDatabase(schemaVersion);
        }

        public string RootPath { get; }

        public string CurrentDatabasePath { get; }

        public Guid StorageId { get; }

        public ProtectedApplicationLifecycle Lifecycle { get; }

        public ProtectedStorageSessionLease Session { get; }

        public static TestDatabase Create(
            int schemaVersion = ProtectedStorageDatabaseService.CurrentSchemaVersion)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "Clipensk.Storage.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new TestDatabase(root, Guid.NewGuid(), schemaVersion);
        }

        public SqliteClipboardCapturePolicyRepository CreateRepository(
            ClipboardCapturePolicy globalPolicy) =>
            new(Session, globalPolicy, _factory);

        public void SeedApplication(DurableApplicationId applicationId)
        {
            using SqliteConnection connection = _factory.Open(
                CurrentDatabasePath,
                Session.DangerousGetMasterKeyMemory(),
                SqliteOpenMode.ReadWrite);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
                VALUES ($applicationId, $createdAtUtc);
                """;
            command.Parameters.AddWithValue("$applicationId", applicationId.ToString());
            command.Parameters.AddWithValue(
                "$createdAtUtc",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        public int CountRows(string tableName)
        {
            using SqliteConnection connection = _factory.Open(
                CurrentDatabasePath,
                Session.DangerousGetMasterKeyMemory(),
                SqliteOpenMode.ReadOnly);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
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

        private void CreateCurrentDatabase(int schemaVersion)
        {
            using SqliteConnection connection = _factory.Open(
                CurrentDatabasePath,
                Session.DangerousGetMasterKeyMemory(),
                SqliteOpenMode.ReadWriteCreate);
            using SqliteCommand foreignKeys = connection.CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
            foreignKeys.ExecuteNonQuery();
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
                insertIdentity.Parameters.AddWithValue("$schemaVersion", schemaVersion);
                insertIdentity.Parameters.AddWithValue(
                    "$createdAtUtc",
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                insertIdentity.ExecuteNonQuery();
            }

            if (schemaVersion >= ApplicationIdentitySqlSchema.MinimumCurrentSchemaVersion)
            {
                ApplicationIdentitySqlSchema.CreateTables(connection, transaction);
            }
            if (schemaVersion >= ApplicationCapturePolicySqlSchema.MinimumCurrentSchemaVersion)
            {
                ApplicationCapturePolicySqlSchema.CreateTables(connection, transaction);
            }

            using (SqliteCommand userVersion = connection.CreateCommand())
            {
                userVersion.Transaction = transaction;
                userVersion.CommandText = $"PRAGMA user_version = {schemaVersion};";
                userVersion.ExecuteNonQuery();
            }

            transaction.Commit();
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
