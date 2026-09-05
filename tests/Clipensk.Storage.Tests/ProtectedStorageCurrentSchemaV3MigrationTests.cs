using System.Globalization;
using System.Security.Cryptography;
using Clipensk.Core.Applications;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Databases;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedStorageCurrentSchemaV3MigrationTests
{
    [Fact]
    public async Task Initialize_CreatesCurrentV3PolicySchemaAndLeavesCatalogV1()
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

            string current = CurrentPath(root);
            string catalog = CatalogPath(root);
            Assert.Equal(3, ReadSchemaVersion(factory, current, key));
            Assert.Equal(1, ReadSchemaVersion(factory, catalog, key));
            Assert.True(HasTable(factory, current, key, "ApplicationIdentity"));
            Assert.True(HasTable(factory, current, key, "ApplicationIdentityAlias"));
            Assert.True(HasTable(factory, current, key, "ApplicationCapturePolicy"));
            Assert.True(HasTable(factory, current, key, "ApplicationFormatCapturePolicy"));
            Assert.False(HasTable(factory, catalog, key, "ApplicationIdentity"));
            Assert.False(HasTable(factory, catalog, key, "ApplicationCapturePolicy"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_MigratesCurrentV2ToV3AndPreservesIdentityRows()
    {
        string root = CreateTemporaryDirectory();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        Guid storageId = Guid.NewGuid();
        DurableApplicationId applicationId = DurableApplicationId.New();
        var factory = new PlainSqliteConnectionFactory();

        try
        {
            CreateCurrentV2(
                factory,
                CurrentPath(root),
                key,
                storageId,
                applicationId,
                "Contoso.App_123!App",
                @"C:\Apps\Contoso.exe");
            CreateDatabaseIdentity(
                factory,
                CatalogPath(root),
                key,
                storageId,
                DatabaseRole.StorageCatalog,
                schemaVersion: 1);

            var service = new ProtectedStorageDatabaseService(factory);
            ProtectedStorageDatabaseResult result = await service.InitializeOrValidateAsync(
                root,
                storageId,
                key,
                allowInitialize: false);

            Assert.True(result.IsSuccess);
            Assert.False(result.WasInitialized);
            Assert.Equal(3, ReadSchemaVersion(factory, CurrentPath(root), key));
            Assert.True(HasTable(factory, CurrentPath(root), key, "ApplicationCapturePolicy"));
            Assert.True(HasTable(factory, CurrentPath(root), key, "ApplicationFormatCapturePolicy"));
            Assert.Equal(1, CountRows(factory, CurrentPath(root), key, "ApplicationIdentity"));
            Assert.Equal(2, CountRows(factory, CurrentPath(root), key, "ApplicationIdentityAlias"));
            Assert.Equal(
                applicationId.ToString(),
                ReadAliasApplicationId(
                    factory,
                    CurrentPath(root),
                    key,
                    ApplicationIdentitySqlSchema.AumidAliasType,
                    "Contoso.App_123!App"));
            Assert.Equal(
                applicationId.ToString(),
                ReadAliasApplicationId(
                    factory,
                    CurrentPath(root),
                    key,
                    ApplicationIdentitySqlSchema.ExecutablePathAliasType,
                    @"C:\Apps\Contoso.exe"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_InvalidCatalogDoesNotMutateCurrentV2()
    {
        string root = CreateTemporaryDirectory();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        Guid storageId = Guid.NewGuid();
        DurableApplicationId applicationId = DurableApplicationId.New();
        var factory = new PlainSqliteConnectionFactory();

        try
        {
            CreateCurrentV2(
                factory,
                CurrentPath(root),
                key,
                storageId,
                applicationId,
                aumid: null,
                executablePath: @"C:\Apps\Classic.exe");
            CreateDatabaseIdentity(
                factory,
                CatalogPath(root),
                key,
                Guid.NewGuid(),
                DatabaseRole.StorageCatalog,
                schemaVersion: 1);

            var service = new ProtectedStorageDatabaseService(factory);
            ProtectedStorageDatabaseResult result = await service.InitializeOrValidateAsync(
                root,
                storageId,
                key,
                allowInitialize: false);

            Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, result.Status);
            Assert.Equal(2, ReadSchemaVersion(factory, CurrentPath(root), key));
            Assert.False(HasTable(factory, CurrentPath(root), key, "ApplicationCapturePolicy"));
            Assert.False(HasTable(factory, CurrentPath(root), key, "ApplicationFormatCapturePolicy"));
            Assert.Equal(1, CountRows(factory, CurrentPath(root), key, "ApplicationIdentity"));
            Assert.Equal(1, CountRows(factory, CurrentPath(root), key, "ApplicationIdentityAlias"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_RejectsCurrentV3WithMalformedPolicyForeignKey()
    {
        string root = CreateTemporaryDirectory();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        Guid storageId = Guid.NewGuid();
        var factory = new PlainSqliteConnectionFactory();

        try
        {
            CreateMalformedCurrentV3(factory, CurrentPath(root), key, storageId);
            CreateDatabaseIdentity(
                factory,
                CatalogPath(root),
                key,
                storageId,
                DatabaseRole.StorageCatalog,
                schemaVersion: 1);

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

    [Fact]
    public async Task Validate_LegacyCurrentV1RunsThroughV2AndV3()
    {
        string root = CreateTemporaryDirectory();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        Guid storageId = Guid.NewGuid();
        var factory = new PlainSqliteConnectionFactory();

        try
        {
            CreateDatabaseIdentity(
                factory,
                CurrentPath(root),
                key,
                storageId,
                DatabaseRole.Current,
                schemaVersion: 1);
            CreateDatabaseIdentity(
                factory,
                CatalogPath(root),
                key,
                storageId,
                DatabaseRole.StorageCatalog,
                schemaVersion: 1);

            var service = new ProtectedStorageDatabaseService(factory);
            ProtectedStorageDatabaseResult result = await service.InitializeOrValidateAsync(
                root,
                storageId,
                key,
                allowInitialize: false);

            Assert.True(result.IsSuccess);
            Assert.Equal(3, ReadSchemaVersion(factory, CurrentPath(root), key));
            Assert.True(HasTable(factory, CurrentPath(root), key, "ApplicationIdentity"));
            Assert.True(HasTable(factory, CurrentPath(root), key, "ApplicationCapturePolicy"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CreateCurrentV2(
        IKeyedSqliteConnectionFactory factory,
        string path,
        ReadOnlyMemory<byte> key,
        Guid storageId,
        DurableApplicationId applicationId,
        string? aumid,
        string? executablePath)
    {
        CreateDatabaseIdentity(
            factory,
            path,
            key,
            storageId,
            DatabaseRole.Current,
            schemaVersion: 2,
            configureTransaction: (connection, transaction) =>
            {
                ApplicationIdentitySqlSchema.CreateTables(connection, transaction);
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
            });
    }

    private static void CreateMalformedCurrentV3(
        IKeyedSqliteConnectionFactory factory,
        string path,
        ReadOnlyMemory<byte> key,
        Guid storageId)
    {
        CreateDatabaseIdentity(
            factory,
            path,
            key,
            storageId,
            DatabaseRole.Current,
            schemaVersion: 3,
            configureTransaction: (connection, transaction) =>
            {
                ApplicationIdentitySqlSchema.CreateTables(connection, transaction);

                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    CREATE TABLE ApplicationCapturePolicy (
                        ApplicationId TEXT NOT NULL PRIMARY KEY,
                        CaptureRule TEXT NOT NULL,
                        FOREIGN KEY (ApplicationId)
                            REFERENCES ApplicationIdentity(ApplicationId)
                    );

                    CREATE TABLE ApplicationFormatCapturePolicy (
                        ApplicationId TEXT NOT NULL,
                        FormatName TEXT NOT NULL,
                        CaptureRule TEXT NOT NULL,
                        MaxBytes INTEGER NULL,
                        PRIMARY KEY (ApplicationId, FormatName),
                        FOREIGN KEY (ApplicationId)
                            REFERENCES ApplicationCapturePolicy(ApplicationId)
                            ON DELETE CASCADE
                    );
                    """;
                command.ExecuteNonQuery();
            });
    }

    private static void CreateDatabaseIdentity(
        IKeyedSqliteConnectionFactory factory,
        string path,
        ReadOnlyMemory<byte> key,
        Guid storageId,
        DatabaseRole role,
        int schemaVersion,
        Action<SqliteConnection, SqliteTransaction>? configureTransaction = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using SqliteConnection connection = factory.Open(path, key, SqliteOpenMode.ReadWriteCreate);
        using (SqliteCommand foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys = ON;";
            foreignKeys.ExecuteNonQuery();
        }
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

        configureTransaction?.Invoke(connection, transaction);

        using (SqliteCommand userVersion = connection.CreateCommand())
        {
            userVersion.Transaction = transaction;
            userVersion.CommandText = $"PRAGMA user_version = {schemaVersion};";
            userVersion.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void InsertAlias(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string aliasType,
        string aliasValue,
        DurableApplicationId applicationId,
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

    private static string? ReadAliasApplicationId(
        IKeyedSqliteConnectionFactory factory,
        string path,
        ReadOnlyMemory<byte> key,
        string aliasType,
        string aliasValue)
    {
        using SqliteConnection connection = factory.Open(path, key, SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ApplicationId
            FROM ApplicationIdentityAlias
            WHERE AliasType = $aliasType AND AliasValue = $aliasValue;
            """;
        command.Parameters.AddWithValue("$aliasType", aliasType);
        command.Parameters.AddWithValue("$aliasValue", aliasValue);
        return command.ExecuteScalar() as string;
    }

    private static int CountRows(
        IKeyedSqliteConnectionFactory factory,
        string path,
        ReadOnlyMemory<byte> key,
        string tableName)
    {
        using SqliteConnection connection = factory.Open(path, key, SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
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

    private static string CurrentPath(string root) =>
        Path.Combine(root, "Current", "current.db");

    private static string CatalogPath(string root) =>
        Path.Combine(root, "Current", "storage-catalog.db");

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
