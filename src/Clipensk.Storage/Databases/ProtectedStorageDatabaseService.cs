using System.Globalization;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

public sealed class ProtectedStorageDatabaseService : IProtectedStorageDatabaseService
{
    public const int CurrentSchemaVersion = 1;
    public const int CurrentEncryptionVersion = 1;

    private readonly IKeyedSqliteConnectionFactory _connectionFactory;

    public ProtectedStorageDatabaseService(IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
    }

    public Task<ProtectedStorageDatabaseResult> InitializeOrValidateAsync(
        string dataRootPath,
        Guid storageId,
        ReadOnlyMemory<byte> masterKey,
        bool allowInitialize,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        if (storageId == Guid.Empty)
        {
            throw new ArgumentException("StorageId не может быть пустым.", nameof(storageId));
        }
        if (masterKey.Length != 32)
        {
            throw new ArgumentException("MasterKey должен содержать 32 байта.", nameof(masterKey));
        }

        return Task.Run(
            () => InitializeOrValidateCore(
                Path.GetFullPath(dataRootPath),
                storageId,
                masterKey,
                allowInitialize,
                cancellationToken),
            cancellationToken);
    }

    private ProtectedStorageDatabaseResult InitializeOrValidateCore(
        string dataRootPath,
        Guid storageId,
        ReadOnlyMemory<byte> masterKey,
        bool allowInitialize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(dataRootPath))
        {
            return new ProtectedStorageDatabaseResult(
                ProtectedStorageDatabaseStatus.StorageFailure,
                WasInitialized: false);
        }

        string currentDirectory = Path.Combine(dataRootPath, "Current");
        string currentDatabasePath = Path.Combine(currentDirectory, "current.db");
        string catalogDatabasePath = Path.Combine(currentDirectory, "storage-catalog.db");

        bool currentExists = File.Exists(currentDatabasePath);
        bool catalogExists = File.Exists(catalogDatabasePath);

        if (currentExists != catalogExists)
        {
            return new ProtectedStorageDatabaseResult(
                ProtectedStorageDatabaseStatus.MissingOrPartialStorage,
                WasInitialized: false);
        }

        try
        {
            if (currentExists)
            {
                ValidateDatabase(
                    currentDatabasePath,
                    storageId,
                    DatabaseRole.Current,
                    masterKey,
                    cancellationToken);
                ValidateDatabase(
                    catalogDatabasePath,
                    storageId,
                    DatabaseRole.StorageCatalog,
                    masterKey,
                    cancellationToken);

                EnsureAncillaryDirectories(dataRootPath);
                return new ProtectedStorageDatabaseResult(
                    ProtectedStorageDatabaseStatus.Success,
                    WasInitialized: false);
            }

            if (!allowInitialize || HasArchiveDatabase(dataRootPath))
            {
                return new ProtectedStorageDatabaseResult(
                    ProtectedStorageDatabaseStatus.MissingOrPartialStorage,
                    WasInitialized: false);
            }

            if (Directory.Exists(currentDirectory))
            {
                if (Directory.EnumerateFileSystemEntries(currentDirectory).Any())
                {
                    return new ProtectedStorageDatabaseResult(
                        ProtectedStorageDatabaseStatus.MissingOrPartialStorage,
                        WasInitialized: false);
                }

                Directory.Delete(currentDirectory);
            }

            InitializeDatabasePair(
                dataRootPath,
                currentDirectory,
                storageId,
                masterKey,
                cancellationToken);
            EnsureAncillaryDirectories(dataRootPath);

            return new ProtectedStorageDatabaseResult(
                ProtectedStorageDatabaseStatus.Success,
                WasInitialized: true);
        }
        catch (ProtectedStorageEncryptionUnavailableException)
        {
            return new ProtectedStorageDatabaseResult(
                ProtectedStorageDatabaseStatus.EncryptionEngineUnavailable,
                WasInitialized: false);
        }
        catch (SqliteException)
        {
            return new ProtectedStorageDatabaseResult(
                ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity,
                WasInitialized: false);
        }
        catch (InvalidDataException)
        {
            return new ProtectedStorageDatabaseResult(
                ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity,
                WasInitialized: false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new ProtectedStorageDatabaseResult(
                ProtectedStorageDatabaseStatus.StorageFailure,
                WasInitialized: false);
        }
    }

    private void InitializeDatabasePair(
        string dataRootPath,
        string finalCurrentDirectory,
        Guid storageId,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        string stagingRoot = Path.Combine(
            dataRootPath,
            ".clipensk-storage-init-" + Guid.NewGuid().ToString("N"));
        string stagingCurrentDirectory = Path.Combine(stagingRoot, "Current");

        Directory.CreateDirectory(stagingCurrentDirectory);
        try
        {
            string currentPath = Path.Combine(stagingCurrentDirectory, "current.db");
            string catalogPath = Path.Combine(stagingCurrentDirectory, "storage-catalog.db");

            CreateDatabase(
                currentPath,
                storageId,
                DatabaseRole.Current,
                masterKey,
                cancellationToken);
            CreateDatabase(
                catalogPath,
                storageId,
                DatabaseRole.StorageCatalog,
                masterKey,
                cancellationToken);

            ValidateDatabase(
                currentPath,
                storageId,
                DatabaseRole.Current,
                masterKey,
                cancellationToken);
            ValidateDatabase(
                catalogPath,
                storageId,
                DatabaseRole.StorageCatalog,
                masterKey,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(stagingCurrentDirectory, finalCurrentDirectory);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    private void CreateDatabase(
        string databasePath,
        Guid storageId,
        DatabaseRole role,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            masterKey,
            SqliteOpenMode.ReadWriteCreate);

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
                    SingletonId,
                    StorageId,
                    DatabaseId,
                    DatabaseRole,
                    SchemaVersion,
                    EncryptionVersion,
                    CreatedAtUtc,
                    ArchiveBaseNumber,
                    ArchiveSplitSequence,
                    CoverageStartDate,
                    CoverageEndDate)
                VALUES (1, $storageId, $databaseId, $role, $schemaVersion, $encryptionVersion,
                        $createdAtUtc, NULL, NULL, NULL, NULL);
                """;
            insert.Parameters.AddWithValue("$storageId", storageId.ToString("D"));
            insert.Parameters.AddWithValue("$databaseId", Guid.NewGuid().ToString("D"));
            insert.Parameters.AddWithValue("$role", role.ToString());
            insert.Parameters.AddWithValue("$schemaVersion", CurrentSchemaVersion);
            insert.Parameters.AddWithValue("$encryptionVersion", CurrentEncryptionVersion);
            insert.Parameters.AddWithValue(
                "$createdAtUtc",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }

        using (SqliteCommand userVersion = connection.CreateCommand())
        {
            userVersion.Transaction = transaction;
            userVersion.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion};";
            userVersion.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void ValidateDatabase(
        string databasePath,
        Guid expectedStorageId,
        DatabaseRole expectedRole,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            masterKey,
            SqliteOpenMode.ReadWrite);

        // Это первая операция, читающая страницы БД. Для SQLCipher она одновременно
        // подтверждает, что переданный ключ действительно открывает файл.
        using (SqliteCommand keyProbe = connection.CreateCommand())
        {
            keyProbe.CommandText = "SELECT count(*) FROM sqlite_master;";
            _ = keyProbe.ExecuteScalar();
        }

        using (SqliteCommand quickCheck = connection.CreateCommand())
        {
            quickCheck.CommandText = "PRAGMA quick_check;";
            string? result = quickCheck.ExecuteScalar() as string;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("SQLite quick_check не подтвердил целостность БД.");
            }
        }

        using (SqliteCommand count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM DatabaseIdentity;";
            if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidDataException("DatabaseIdentity должен содержать ровно одну запись.");
            }
        }

        using (SqliteCommand identity = connection.CreateCommand())
        {
            identity.CommandText = """
                SELECT StorageId, DatabaseId, DatabaseRole, SchemaVersion, EncryptionVersion,
                       CreatedAtUtc, ArchiveBaseNumber, ArchiveSplitSequence,
                       CoverageStartDate, CoverageEndDate
                FROM DatabaseIdentity
                WHERE SingletonId = 1;
                """;

            using SqliteDataReader reader = identity.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidDataException("DatabaseIdentity отсутствует.");
            }

            if (!Guid.TryParse(reader.GetString(0), out Guid storageId) ||
                storageId != expectedStorageId ||
                !Guid.TryParse(reader.GetString(1), out Guid databaseId) ||
                databaseId == Guid.Empty ||
                !Enum.TryParse(reader.GetString(2), ignoreCase: false, out DatabaseRole role) ||
                role != expectedRole ||
                reader.GetInt32(3) != CurrentSchemaVersion ||
                reader.GetInt32(4) != CurrentEncryptionVersion ||
                !DateTimeOffset.TryParse(
                    reader.GetString(5),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out _))
            {
                throw new InvalidDataException("DatabaseIdentity не соответствует ожидаемому storage.");
            }

            for (int index = 6; index <= 9; index++)
            {
                if (!reader.IsDBNull(index))
                {
                    throw new InvalidDataException(
                        "Current/Catalog DatabaseIdentity не должен содержать archive coverage.");
                }
            }
        }

        using (SqliteCommand userVersion = connection.CreateCommand())
        {
            userVersion.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != CurrentSchemaVersion)
            {
                throw new InvalidDataException("SQLite user_version не соответствует schema version.");
            }
        }
    }

    private static bool HasArchiveDatabase(string dataRootPath)
    {
        string archiveDirectory = Path.Combine(dataRootPath, "Archive");
        return Directory.Exists(archiveDirectory) &&
               Directory.EnumerateFiles(archiveDirectory, "archive_*.db", SearchOption.TopDirectoryOnly).Any();
    }

    private static void EnsureAncillaryDirectories(string dataRootPath)
    {
        Directory.CreateDirectory(Path.Combine(dataRootPath, "Archive"));
        Directory.CreateDirectory(Path.Combine(dataRootPath, "Files"));
        Directory.CreateDirectory(Path.Combine(dataRootPath, "Trash"));
        Directory.CreateDirectory(Path.Combine(dataRootPath, "Languages"));
    }
}
