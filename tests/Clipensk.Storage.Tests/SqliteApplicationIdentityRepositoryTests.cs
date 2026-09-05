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

public sealed class SqliteApplicationIdentityRepositoryTests
{
    [Fact]
    public async Task FindAliasesAsync_ReturnsPersistedExactAliases()
    {
        using TestDatabase database = TestDatabase.Create();
        ClipenskApplicationId id = ClipenskApplicationId.New();
        database.SeedIdentity(
            id,
            aumid: "Contoso.App_123!App",
            executablePath: "C:\\Apps\\Contoso.exe");
        var repository = database.CreateRepository();

        ApplicationIdentityAliasLookup result = await repository.FindAliasesAsync(
            new ApplicationIdentityObservation(
                "Contoso.App_123!App",
                "C:\\Apps\\Contoso.exe"));

        Assert.Equal(id, result.ApplicationUserModelIdApplicationId);
        Assert.Equal(id, result.ExecutablePathApplicationId);

        ApplicationIdentityAliasLookup differentCase = await repository.FindAliasesAsync(
            new ApplicationIdentityObservation(
                "Contoso.App_123!App",
                "c:\\apps\\contoso.exe"));
        Assert.Equal(id, differentCase.ApplicationUserModelIdApplicationId);
        Assert.Null(differentCase.ExecutablePathApplicationId);
    }

    [Fact]
    public async Task CreateAndBindAsync_PackagedIdentityCreatesBothAliasesAtomically()
    {
        using TestDatabase database = TestDatabase.Create();
        var repository = database.CreateRepository();
        var observation = new ApplicationIdentityObservation(
            "Contoso.App_123!App",
            "C:\\Apps\\Contoso.exe");

        ClipenskApplicationId created = await repository.CreateAndBindAsync(
            observation,
            ApplicationIdentityResolutionBasis.PackagedApplicationUserModelId);

        ApplicationIdentityAliasLookup lookup = await repository.FindAliasesAsync(observation);
        Assert.Equal(created, lookup.ApplicationUserModelIdApplicationId);
        Assert.Equal(created, lookup.ExecutablePathApplicationId);
        Assert.Equal(1, database.CountApplications());
        Assert.Equal(2, database.CountAliases());
    }

    [Fact]
    public async Task CreateAndBindAsync_PathConflictRollsBackNewApplication()
    {
        using TestDatabase database = TestDatabase.Create();
        ClipenskApplicationId existing = ClipenskApplicationId.New();
        database.SeedIdentity(existing, executablePath: "C:\\Apps\\Classic.exe");
        var repository = database.CreateRepository();

        ApplicationIdentityConflictException error =
            await Assert.ThrowsAsync<ApplicationIdentityConflictException>(async () =>
            {
                await repository.CreateAndBindAsync(
                    new ApplicationIdentityObservation(null, "C:\\Apps\\Classic.exe"),
                    ApplicationIdentityResolutionBasis.ExecutablePathAlias);
            });

        Assert.Equal(existing, error.ExecutablePathApplicationId);
        Assert.Equal(1, database.CountApplications());
        Assert.Equal(1, database.CountAliases());
    }

    [Fact]
    public async Task BindExecutablePathAliasAsync_IsIdempotentAndRejectsDifferentOwner()
    {
        using TestDatabase database = TestDatabase.Create();
        ClipenskApplicationId first = ClipenskApplicationId.New();
        ClipenskApplicationId second = ClipenskApplicationId.New();
        database.SeedIdentity(first);
        database.SeedIdentity(second);
        var repository = database.CreateRepository();

        await repository.BindExecutablePathAliasAsync(first, "C:\\Apps\\Shared.exe");
        await repository.BindExecutablePathAliasAsync(first, "C:\\Apps\\Shared.exe");

        ApplicationIdentityConflictException error =
            await Assert.ThrowsAsync<ApplicationIdentityConflictException>(async () =>
            {
                await repository.BindExecutablePathAliasAsync(
                    second,
                    "C:\\Apps\\Shared.exe");
            });

        Assert.Equal(first, error.ExecutablePathApplicationId);
        Assert.Equal(2, database.CountApplications());
        Assert.Equal(1, database.CountAliases());
    }

    [Fact]
    public async Task FindAliasesAsync_RejectsCurrentSchemaVersionOne()
    {
        using TestDatabase database = TestDatabase.Create(
            schemaVersion: 1,
            createIdentityTables: false);
        var repository = database.CreateRepository();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await repository.FindAliasesAsync(
                new ApplicationIdentityObservation(null, "C:\\Apps\\Classic.exe"));
        });
    }

    [Fact]
    public async Task FindAliasesAsync_RejectsVersionTwoWithoutRequiredTables()
    {
        using TestDatabase database = TestDatabase.Create(
            schemaVersion: ApplicationIdentitySqlSchema.RequiredCurrentSchemaVersion,
            createIdentityTables: false);
        var repository = database.CreateRepository();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await repository.FindAliasesAsync(
                new ApplicationIdentityObservation(null, "C:\\Apps\\Classic.exe"));
        });
    }

    [Fact]
    public async Task FindAliasesAsync_RejectsDifferentStorageIdentity()
    {
        using TestDatabase database = TestDatabase.Create(
            databaseStorageId: Guid.NewGuid());
        var repository = database.CreateRepository();

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await repository.FindAliasesAsync(
                new ApplicationIdentityObservation(null, "C:\\Apps\\Classic.exe"));
        });
    }

    [Fact]
    public async Task FindAliasesAsync_AfterProtectedAccessRevocationCancelsBeforeRead()
    {
        using TestDatabase database = TestDatabase.Create();
        var repository = database.CreateRepository();
        Assert.True(database.Lifecycle.TryBeginLock());

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await repository.FindAliasesAsync(
                new ApplicationIdentityObservation(null, "C:\\Apps\\Classic.exe"));
        });
    }

    [Fact]
    public async Task CreateAndBindAsync_RejectsPathBasisWhenAumidIsPresent()
    {
        using TestDatabase database = TestDatabase.Create();
        var repository = database.CreateRepository();

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await repository.CreateAndBindAsync(
                new ApplicationIdentityObservation(
                    "Contoso.App_123!App",
                    "C:\\Apps\\Contoso.exe"),
                ApplicationIdentityResolutionBasis.ExecutablePathAlias);
        });

        Assert.Equal(0, database.CountApplications());
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly byte[] _key;
        private readonly PlainSqliteConnectionFactory _factory;
        private bool _disposed;

        private TestDatabase(
            string rootPath,
            Guid sessionStorageId,
            Guid databaseStorageId,
            int schemaVersion,
            bool createIdentityTables)
        {
            RootPath = rootPath;
            StorageId = sessionStorageId;
            _key = Enumerable.Repeat((byte)0x5A, 32).ToArray();
            _factory = new PlainSqliteConnectionFactory();

            Lifecycle = new ProtectedApplicationLifecycle(isDataRootConfigured: true);
            Assert.True(Lifecycle.TryBeginUnlock());
            Lifecycle.CompleteUnlock();
            Session = ProtectedStorageSessionLease.Create(
                Lifecycle,
                rootPath,
                sessionStorageId,
                new MasterKeyLease(_key));

            string currentDirectory = Path.Combine(rootPath, "Current");
            Directory.CreateDirectory(currentDirectory);
            CurrentDatabasePath = Path.Combine(currentDirectory, "current.db");
            CreateCurrentDatabase(
                databaseStorageId,
                schemaVersion,
                createIdentityTables);
        }

        public string RootPath { get; }

        public string CurrentDatabasePath { get; }

        public Guid StorageId { get; }

        public ProtectedApplicationLifecycle Lifecycle { get; }

        public ProtectedStorageSessionLease Session { get; }

        public static TestDatabase Create(
            int schemaVersion = ApplicationIdentitySqlSchema.RequiredCurrentSchemaVersion,
            bool createIdentityTables = true,
            Guid? databaseStorageId = null)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "Clipensk.Storage.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Guid sessionStorageId = Guid.NewGuid();
            return new TestDatabase(
                root,
                sessionStorageId,
                databaseStorageId ?? sessionStorageId,
                schemaVersion,
                createIdentityTables);
        }

        public SqliteApplicationIdentityRepository CreateRepository() =>
            new(Session, _factory);

        public void SeedIdentity(
            ClipenskApplicationId applicationId,
            string? aumid = null,
            string? executablePath = null)
        {
            using SqliteConnection connection = _factory.Open(
                CurrentDatabasePath,
                Session.DangerousGetMasterKeyMemory(),
                SqliteOpenMode.ReadWrite);
            using SqliteTransaction transaction = connection.BeginTransaction();
            string createdAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            using (SqliteCommand insertApplication = connection.CreateCommand())
            {
                insertApplication.Transaction = transaction;
                insertApplication.CommandText = """
                    INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
                    VALUES ($applicationId, $createdAtUtc);
                    """;
                insertApplication.Parameters.AddWithValue("$applicationId", applicationId.ToString());
                insertApplication.Parameters.AddWithValue("$createdAtUtc", createdAt);
                insertApplication.ExecuteNonQuery();
            }

            if (aumid is not null)
            {
                InsertAlias(
                    connection,
                    transaction,
                    ApplicationIdentitySqlSchema.AumidAliasType,
                    aumid,
                    applicationId,
                    createdAt);
            }
            if (executablePath is not null)
            {
                InsertAlias(
                    connection,
                    transaction,
                    ApplicationIdentitySqlSchema.ExecutablePathAliasType,
                    executablePath,
                    applicationId,
                    createdAt);
            }

            transaction.Commit();
        }

        public int CountApplications() => CountRows("ApplicationIdentity");

        public int CountAliases() => CountRows("ApplicationIdentityAlias");

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

        private void CreateCurrentDatabase(
            Guid databaseStorageId,
            int schemaVersion,
            bool createIdentityTables)
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
                insertIdentity.Parameters.AddWithValue("$storageId", databaseStorageId.ToString("D"));
                insertIdentity.Parameters.AddWithValue("$databaseId", Guid.NewGuid().ToString("D"));
                insertIdentity.Parameters.AddWithValue("$role", DatabaseRole.Current.ToString());
                insertIdentity.Parameters.AddWithValue("$schemaVersion", schemaVersion);
                insertIdentity.Parameters.AddWithValue(
                    "$createdAtUtc",
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                insertIdentity.ExecuteNonQuery();
            }

            if (createIdentityTables)
            {
                ApplicationIdentitySqlSchema.CreateTables(connection, transaction);
            }

            using (SqliteCommand userVersion = connection.CreateCommand())
            {
                userVersion.Transaction = transaction;
                userVersion.CommandText = $"PRAGMA user_version = {schemaVersion};";
                userVersion.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        private int CountRows(string tableName)
        {
            using SqliteConnection connection = _factory.Open(
                CurrentDatabasePath,
                Session.DangerousGetMasterKeyMemory(),
                SqliteOpenMode.ReadOnly);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static void InsertAlias(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string aliasType,
            string aliasValue,
            ClipenskApplicationId applicationId,
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
            command.Parameters.AddWithValue("$applicationId", applicationId.ToString());
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
