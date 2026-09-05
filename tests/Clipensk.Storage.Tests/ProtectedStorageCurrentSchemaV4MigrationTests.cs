using System.Globalization;
using System.Security.Cryptography;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Databases;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;
using DurableApplicationId = Clipensk.Core.Applications.ApplicationId;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedStorageCurrentSchemaV4MigrationTests
{
    [Fact]
    public async Task Initialize_CreatesCurrentV4HistorySchemaAndLeavesCatalogV1()
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
            Assert.Equal(4, ReadSchemaVersion(factory, CurrentPath(root), key));
            Assert.Equal(1, ReadSchemaVersion(factory, CatalogPath(root), key));
            Assert.True(HasTable(factory, CurrentPath(root), key, "ApplicationIdentity"));
            Assert.True(HasTable(factory, CurrentPath(root), key, "ApplicationCapturePolicy"));
            Assert.True(HasTable(factory, CurrentPath(root), key, "ClipboardHistoryEvent"));
            Assert.True(HasTable(factory, CurrentPath(root), key, "ClipboardHistoryPayload"));
            Assert.False(HasTable(factory, CatalogPath(root), key, "ClipboardHistoryEvent"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_MigratesCurrentV3ToV4AndPreservesIdentityAndPolicyRows()
    {
        string root = CreateTemporaryDirectory();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        Guid storageId = Guid.NewGuid();
        DurableApplicationId applicationId = DurableApplicationId.New();
        var factory = new PlainSqliteConnectionFactory();

        try
        {
            CreateCurrentV3(
                factory,
                CurrentPath(root),
                key,
                storageId,
                applicationId,
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
            Assert.Equal(4, ReadSchemaVersion(factory, CurrentPath(root), key));
            Assert.Equal(1, CountRows(factory, CurrentPath(root), key, "ApplicationIdentity"));
            Assert.Equal(1, CountRows(factory, CurrentPath(root), key, "ApplicationIdentityAlias"));
            Assert.Equal(1, CountRows(factory, CurrentPath(root), key, "ApplicationCapturePolicy"));
            Assert.Equal(1, CountRows(factory, CurrentPath(root), key, "ApplicationFormatCapturePolicy"));
            Assert.True(HasTable(factory, CurrentPath(root), key, "ClipboardHistoryEvent"));
            Assert.True(HasTable(factory, CurrentPath(root), key, "ClipboardHistoryPayload"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_InvalidCatalogDoesNotMutateCurrentV3()
    {
        string root = CreateTemporaryDirectory();
        byte[] key = RandomNumberGenerator.GetBytes(32);
        Guid storageId = Guid.NewGuid();
        var factory = new PlainSqliteConnectionFactory();

        try
        {
            CreateCurrentV3(
                factory,
                CurrentPath(root),
                key,
                storageId,
                DurableApplicationId.New(),
                @"C:\Apps\Classic.exe");
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
            Assert.Equal(3, ReadSchemaVersion(factory, CurrentPath(root), key));
            Assert.False(HasTable(factory, CurrentPath(root), key, "ClipboardHistoryEvent"));
            Assert.False(HasTable(factory, CurrentPath(root), key, "ClipboardHistoryPayload"));
            Assert.Equal(1, CountRows(factory, CurrentPath(root), key, "ApplicationCapturePolicy"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_MalformedCurrentV3DoesNotCreateHistoryTables()
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
            Assert.Equal(3, ReadSchemaVersion(factory, CurrentPath(root), key));
            Assert.False(HasTable(factory, CurrentPath(root), key, "ClipboardHistoryEvent"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_CurrentV2RunsThroughPolicyAndHistoryMigrations()
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
                @"C:\Apps\V2.exe");
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
            Assert.Equal(4, ReadSchemaVersion(factory, CurrentPath(root), key));
            Assert.Equal(1, CountRows(factory, CurrentPath(root), key, "ApplicationIdentity"));
            Assert.True(HasTable(factory, CurrentPath(root), key, "ApplicationCapturePolicy"));
            Assert.True(HasTable(factory, CurrentPath(root), key, "ClipboardHistoryEvent"));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_LegacyCurrentV1RunsThroughAllCurrentMigrations()
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
            Assert.Equal(4, ReadSchemaVersion(factory, CurrentPath(root), key));
            Assert.True(HasTable(factory, CurrentPath(root), key, "ApplicationIdentity"));
            Assert.True(HasTable(factory, CurrentPath(root), key, "ApplicationCapturePolicy"));
            Assert.True(HasTable(factory, CurrentPath(root), key, "ClipboardHistoryEvent"));
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
        string executablePath)
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
                InsertApplicationWithPathAlias(connection, transaction, applicationId, executablePath);
            });
    }

    private static void CreateCurrentV3(
        IKeyedSqliteConnectionFactory factory,
        string path,
        ReadOnlyMemory<byte> key,
        Guid storageId,
        DurableApplicationId applicationId,
        string executablePath)
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
                ApplicationCapturePolicySqlSchema.CreateTables(connection, transaction);
                InsertApplicationWithPathAlias(connection, transaction, applicationId, executablePath);

                using SqliteCommand appPolicy = connection.CreateCommand();
                appPolicy.Transaction = transaction;
                appPolicy.CommandText = """
                    INSERT INTO ApplicationCapturePolicy (ApplicationId, CaptureRule)
                    VALUES ($applicationId, 'Allow');
                    """;
                appPolicy.Parameters.AddWithValue("$applicationId", applicationId.ToString());
                appPolicy.ExecuteNonQuery();

                using SqliteCommand formatPolicy = connection.CreateCommand();
                formatPolicy.Transaction = transaction;
                formatPolicy.CommandText = """
                    INSERT INTO ApplicationFormatCapturePolicy (
                        ApplicationId, FormatName, CaptureRule, MaxBytes)
                    VALUES ($applicationId, 'Text', 'Allow', 4096);
                    """;
                formatPolicy.Parameters.AddWithValue("$applicationId", applicationId.ToString());
                formatPolicy.ExecuteNonQuery();
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

    private static void InsertApplicationWithPathAlias(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DurableApplicationId applicationId,
        string executablePath)
    {
        string createdAtUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        using (SqliteCommand application = connection.CreateCommand())
        {
            application.Transaction = transaction;
            application.CommandText = """
                INSERT INTO ApplicationIdentity (ApplicationId, CreatedAtUtc)
                VALUES ($applicationId, $createdAtUtc);
                """;
            application.Parameters.AddWithValue("$applicationId", applicationId.ToString());
            application.Parameters.AddWithValue("$createdAtUtc", createdAtUtc);
            application.ExecuteNonQuery();
        }

        using (SqliteCommand alias = connection.CreateCommand())
        {
            alias.Transaction = transaction;
            alias.CommandText = """
                INSERT INTO ApplicationIdentityAlias (
                    AliasType, AliasValue, ApplicationId, CreatedAtUtc)
                VALUES ($aliasType, $aliasValue, $applicationId, $createdAtUtc);
                """;
            alias.Parameters.AddWithValue("$aliasType", ApplicationIdentitySqlSchema.ExecutablePathAliasType);
            alias.Parameters.AddWithValue("$aliasValue", executablePath);
            alias.Parameters.AddWithValue("$applicationId", applicationId.ToString());
            alias.Parameters.AddWithValue("$createdAtUtc", createdAtUtc);
            alias.ExecuteNonQuery();
        }
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
