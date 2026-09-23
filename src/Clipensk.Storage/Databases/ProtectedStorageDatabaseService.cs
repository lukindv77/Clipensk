using System.Globalization;
using Clipensk.Core.Security;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

public sealed class ProtectedStorageDatabaseService : IProtectedStorageDatabaseService
{
    private const int LegacyCurrentSchemaVersion = 1;
    private const int ApplicationIdentityCurrentSchemaVersion = 2;
    private const int ApplicationPolicyCurrentSchemaVersion = 3;
    private const int HistoryCurrentSchemaVersion = 4;
    private const int GlobalCapturePolicyCurrentSchemaVersion = 5;
    private const int CustomBinaryConfigurationCurrentSchemaVersion = 6;
    private const int PendingPolicyMaintenanceCurrentSchemaVersion = 7;
    private const int ApplicationDiscoveredFormatCurrentSchemaVersion = 8;
    private const int PendingArchiveSplitCurrentSchemaVersion = 9;
    private const int PendingArchiveRotationCurrentSchemaVersion = 10;
    private const int ApplicationGroupMemberCurrentSchemaVersion = 11;
    private const int LegacyCatalogSchemaVersion = 1;
    private const int ExternalPayloadCatalogSchemaVersion = 2;

    public const int CurrentSchemaVersion = 12;
    public const int CatalogSchemaVersion = 3;
    public const int CurrentEncryptionVersion = 1;

    private const int SqliteNotADatabase = 26;

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
        if (masterKey.Length != StorageKeyMaterial.LengthBytes)
        {
            throw new ArgumentException("Ключ хранилища должен содержать 48 байт: MasterKey и соль.", nameof(masterKey));
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

    public Task<ProtectedStorageIdentityResult> IdentifyAsync(
        string dataRootPath,
        ReadOnlyMemory<byte> storageKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRootPath);
        if (storageKey.Length != StorageKeyMaterial.LengthBytes)
        {
            throw new ArgumentException("Ключ хранилища должен содержать 48 байт: MasterKey и соль.", nameof(storageKey));
        }

        return Task.Run(
            () => IdentifyCore(Path.GetFullPath(dataRootPath), storageKey, cancellationToken),
            cancellationToken);
    }

    private ProtectedStorageIdentityResult IdentifyCore(
        string dataRootPath,
        ReadOnlyMemory<byte> storageKey,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> salt = StorageKeyMaterial.GetSalt(storageKey.Span);
        Span<byte> header = stackalloc byte[StorageSalt.LengthBytes];
        int attempted = 0;
        int rejected = 0;
        ProtectedStorageDatabaseStatus failure = ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity;

        foreach (string databasePath in StorageDatabaseFiles.EnumerateExisting(dataRootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A database with another salt was not written with this key and could not accept it.
            if (!StorageDatabaseFiles.TryReadSalt(databasePath, header) || !header.SequenceEqual(salt))
            {
                continue;
            }

            attempted++;
            try
            {
                if (TryReadStorageId(databasePath, storageKey) is { } storageId)
                {
                    return new ProtectedStorageIdentityResult(ProtectedStorageIdentityStatus.Identified, storageId);
                }
            }
            catch (ProtectedStorageEncryptionUnavailableException)
            {
                return new ProtectedStorageIdentityResult(
                    ProtectedStorageIdentityStatus.Unreadable,
                    Guid.Empty,
                    ProtectedStorageDatabaseStatus.EncryptionEngineUnavailable);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == SqliteNotADatabase)
            {
                // SQLCipher checks the first page's HMAC: a wrong key reads as "not a database".
                rejected++;
            }
            catch (SqliteException)
            {
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                failure = ProtectedStorageDatabaseStatus.StorageFailure;
            }
        }

        if (attempted == 0)
        {
            return new ProtectedStorageIdentityResult(ProtectedStorageIdentityStatus.NoDatabases, Guid.Empty);
        }

        return rejected == attempted
            ? new ProtectedStorageIdentityResult(ProtectedStorageIdentityStatus.KeyRejected, Guid.Empty)
            : new ProtectedStorageIdentityResult(ProtectedStorageIdentityStatus.Unreadable, Guid.Empty, failure);
    }

    private Guid? TryReadStorageId(string databasePath, ReadOnlyMemory<byte> storageKey)
    {
        using SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            storageKey,
            SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT StorageId FROM DatabaseIdentity WHERE SingletonId = 1;";
        return command.ExecuteScalar() is string text &&
               Guid.TryParse(text, out Guid storageId) &&
               storageId != Guid.Empty
            ? storageId
            : null;
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
                int currentSchemaVersion = ValidateDatabase(
                    currentDatabasePath,
                    storageId,
                    DatabaseRole.Current,
                    masterKey,
                    cancellationToken,
                    LegacyCurrentSchemaVersion,
                    ApplicationIdentityCurrentSchemaVersion,
                    ApplicationPolicyCurrentSchemaVersion,
                    HistoryCurrentSchemaVersion,
                    GlobalCapturePolicyCurrentSchemaVersion,
                    CustomBinaryConfigurationCurrentSchemaVersion,
                    PendingPolicyMaintenanceCurrentSchemaVersion,
                    ApplicationDiscoveredFormatCurrentSchemaVersion,
                    PendingArchiveSplitCurrentSchemaVersion,
                    PendingArchiveRotationCurrentSchemaVersion,
                    ApplicationGroupMemberCurrentSchemaVersion,
                    CurrentSchemaVersion);

                // Critical rule: validate the whole protected pair before mutating either database.
                int catalogSchemaVersion = ValidateDatabase(
                    catalogDatabasePath,
                    storageId,
                    DatabaseRole.StorageCatalog,
                    masterKey,
                    cancellationToken,
                    LegacyCatalogSchemaVersion,
                    ExternalPayloadCatalogSchemaVersion,
                    CatalogSchemaVersion);

                if (currentSchemaVersion == LegacyCurrentSchemaVersion)
                {
                    MigrateCurrentFromV1ToV2(
                        currentDatabasePath,
                        storageId,
                        masterKey,
                        cancellationToken);
                    currentSchemaVersion = ApplicationIdentityCurrentSchemaVersion;
                }

                if (currentSchemaVersion == ApplicationIdentityCurrentSchemaVersion)
                {
                    MigrateCurrentFromV2ToV3(
                        currentDatabasePath,
                        storageId,
                        masterKey,
                        cancellationToken);
                    currentSchemaVersion = ApplicationPolicyCurrentSchemaVersion;
                }

                if (currentSchemaVersion == ApplicationPolicyCurrentSchemaVersion)
                {
                    MigrateCurrentFromV3ToV4(
                        currentDatabasePath,
                        storageId,
                        masterKey,
                        cancellationToken);
                    currentSchemaVersion = HistoryCurrentSchemaVersion;
                }

                if (currentSchemaVersion == HistoryCurrentSchemaVersion)
                {
                    MigrateCurrentFromV4ToV5(
                        currentDatabasePath,
                        storageId,
                        masterKey,
                        cancellationToken);
                    currentSchemaVersion = GlobalCapturePolicyCurrentSchemaVersion;
                }

                if (currentSchemaVersion == GlobalCapturePolicyCurrentSchemaVersion)
                {
                    MigrateCurrentFromV5ToV6(
                        currentDatabasePath,
                        storageId,
                        masterKey,
                        cancellationToken);
                    currentSchemaVersion = CustomBinaryConfigurationCurrentSchemaVersion;
                }

                if (currentSchemaVersion == CustomBinaryConfigurationCurrentSchemaVersion)
                {
                    MigrateCurrentFromV6ToV7(
                        currentDatabasePath,
                        storageId,
                        masterKey,
                        cancellationToken);
                    currentSchemaVersion = PendingPolicyMaintenanceCurrentSchemaVersion;
                }

                if (currentSchemaVersion == PendingPolicyMaintenanceCurrentSchemaVersion)
                {
                    MigrateCurrentFromV7ToV8(
                        currentDatabasePath,
                        storageId,
                        masterKey,
                        cancellationToken);
                    currentSchemaVersion = ApplicationDiscoveredFormatCurrentSchemaVersion;
                }

                if (currentSchemaVersion == ApplicationDiscoveredFormatCurrentSchemaVersion)
                {
                    MigrateCurrentFromV8ToV9(
                        currentDatabasePath,
                        storageId,
                        masterKey,
                        cancellationToken);
                    currentSchemaVersion = PendingArchiveSplitCurrentSchemaVersion;
                }

                if (currentSchemaVersion == PendingArchiveSplitCurrentSchemaVersion)
                {
                    MigrateCurrentFromV9ToV10(
                        currentDatabasePath,
                        storageId,
                        masterKey,
                        cancellationToken);
                    currentSchemaVersion = PendingArchiveRotationCurrentSchemaVersion;
                }

                if (currentSchemaVersion == PendingArchiveRotationCurrentSchemaVersion)
                {
                    MigrateCurrentFromV10ToV11(
                        currentDatabasePath,
                        storageId,
                        masterKey,
                        cancellationToken);
                    currentSchemaVersion = ApplicationGroupMemberCurrentSchemaVersion;
                }

                if (currentSchemaVersion == ApplicationGroupMemberCurrentSchemaVersion)
                {
                    MigrateCurrentFromV11ToV12(
                        currentDatabasePath,
                        storageId,
                        masterKey,
                        cancellationToken);
                    currentSchemaVersion = CurrentSchemaVersion;
                }

                if (catalogSchemaVersion == LegacyCatalogSchemaVersion)
                {
                    MigrateCatalogFromV1ToV2(
                        catalogDatabasePath,
                        storageId,
                        masterKey,
                        cancellationToken);
                    catalogSchemaVersion = ExternalPayloadCatalogSchemaVersion;
                }

                if (catalogSchemaVersion == ExternalPayloadCatalogSchemaVersion)
                {
                    MigrateCatalogFromV2ToV3(
                        catalogDatabasePath,
                        storageId,
                        masterKey,
                        cancellationToken);
                    catalogSchemaVersion = CatalogSchemaVersion;
                }

                ValidateDatabase(
                    currentDatabasePath,
                    storageId,
                    DatabaseRole.Current,
                    masterKey,
                    cancellationToken,
                    CurrentSchemaVersion);
                ValidateDatabase(
                    catalogDatabasePath,
                    storageId,
                    DatabaseRole.StorageCatalog,
                    masterKey,
                    cancellationToken,
                    CatalogSchemaVersion);

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
                cancellationToken,
                CurrentSchemaVersion);
            ValidateDatabase(
                catalogPath,
                storageId,
                DatabaseRole.StorageCatalog,
                masterKey,
                cancellationToken,
                CatalogSchemaVersion);

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
        int schemaVersion = GetSchemaVersion(role);

        using SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            masterKey,
            SqliteOpenMode.ReadWriteCreate);

        EnableForeignKeys(connection);
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
            insert.Parameters.AddWithValue("$schemaVersion", schemaVersion);
            insert.Parameters.AddWithValue("$encryptionVersion", CurrentEncryptionVersion);
            insert.Parameters.AddWithValue(
                "$createdAtUtc",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }

        if (role == DatabaseRole.Current)
        {
            ApplicationIdentitySqlSchema.CreateTables(connection, transaction);
            ApplicationCapturePolicySqlSchema.CreateTables(connection, transaction);
            ClipboardHistorySqlSchema.CreateTables(connection, transaction);
            GlobalCapturePolicySqlSchema.CreateTables(connection, transaction);
            CustomBinaryFormatConfigurationSqlSchema.CreateTable(connection, transaction);
            PendingPolicyMaintenanceSqlSchema.CreateTable(connection, transaction);
            ApplicationDiscoveredFormatSqlSchema.CreateTable(connection, transaction);
            PendingArchiveSplitSqlSchema.CreateTables(connection, transaction);
            PendingArchiveRotationSqlSchema.CreateTables(connection, transaction);
            ApplicationGroupSqlSchema.CreateTables(connection, transaction);
        }
        else if (role == DatabaseRole.StorageCatalog)
        {
            ExternalPayloadCatalogSqlSchema.CreateTables(connection, transaction);
            ArchiveSegmentCatalogSqlSchema.CreateTable(connection, transaction);
        }

        using (SqliteCommand userVersion = connection.CreateCommand())
        {
            userVersion.Transaction = transaction;
            userVersion.CommandText = $"PRAGMA user_version = {schemaVersion};";
            userVersion.ExecuteNonQuery();
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void MigrateCurrentFromV1ToV2(
        string databasePath,
        Guid expectedStorageId,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            masterKey,
            SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);

        using SqliteTransaction transaction = connection.BeginTransaction();
        ApplicationIdentitySqlSchema.CreateTables(connection, transaction);

        UpdateCurrentSchemaVersion(
            connection,
            transaction,
            expectedStorageId,
            LegacyCurrentSchemaVersion,
            ApplicationIdentityCurrentSchemaVersion);
        SetUserVersion(connection, transaction, ApplicationIdentityCurrentSchemaVersion);

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void MigrateCurrentFromV2ToV3(
        string databasePath,
        Guid expectedStorageId,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            masterKey,
            SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);
        ApplicationIdentitySqlSchema.ValidateTables(connection);

        using SqliteTransaction transaction = connection.BeginTransaction();
        ApplicationCapturePolicySqlSchema.CreateTables(connection, transaction);

        UpdateCurrentSchemaVersion(
            connection,
            transaction,
            expectedStorageId,
            ApplicationIdentityCurrentSchemaVersion,
            ApplicationPolicyCurrentSchemaVersion);
        SetUserVersion(connection, transaction, ApplicationPolicyCurrentSchemaVersion);

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void MigrateCurrentFromV3ToV4(
        string databasePath,
        Guid expectedStorageId,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            masterKey,
            SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);
        ApplicationIdentitySqlSchema.ValidateTables(connection);
        ApplicationCapturePolicySqlSchema.ValidateTables(connection);

        using SqliteTransaction transaction = connection.BeginTransaction();
        ClipboardHistorySqlSchema.CreateTables(connection, transaction);

        UpdateCurrentSchemaVersion(
            connection,
            transaction,
            expectedStorageId,
            ApplicationPolicyCurrentSchemaVersion,
            HistoryCurrentSchemaVersion);
        SetUserVersion(connection, transaction, HistoryCurrentSchemaVersion);

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void MigrateCurrentFromV4ToV5(
        string databasePath,
        Guid expectedStorageId,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = _connectionFactory.Open(
            databasePath, masterKey, SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);
        ApplicationIdentitySqlSchema.ValidateTables(connection);
        ApplicationCapturePolicySqlSchema.ValidateTables(connection);
        ClipboardHistorySqlSchema.ValidateTables(connection);

        using SqliteTransaction transaction = connection.BeginTransaction();
        GlobalCapturePolicySqlSchema.CreateTables(connection, transaction);
        UpdateCurrentSchemaVersion(
            connection, transaction, expectedStorageId,
            HistoryCurrentSchemaVersion, GlobalCapturePolicyCurrentSchemaVersion);
        SetUserVersion(connection, transaction, GlobalCapturePolicyCurrentSchemaVersion);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void MigrateCurrentFromV5ToV6(
        string databasePath,
        Guid expectedStorageId,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = _connectionFactory.Open(
            databasePath, masterKey, SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);
        ApplicationIdentitySqlSchema.ValidateTables(connection);
        ApplicationCapturePolicySqlSchema.ValidateTables(connection);
        ClipboardHistorySqlSchema.ValidateTables(connection);
        GlobalCapturePolicySqlSchema.ValidateTables(connection);

        using SqliteTransaction transaction = connection.BeginTransaction();
        CustomBinaryFormatConfigurationSqlSchema.CreateTable(connection, transaction);
        UpdateCurrentSchemaVersion(
            connection, transaction, expectedStorageId,
            GlobalCapturePolicyCurrentSchemaVersion, CustomBinaryConfigurationCurrentSchemaVersion);
        SetUserVersion(connection, transaction, CustomBinaryConfigurationCurrentSchemaVersion);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void MigrateCurrentFromV6ToV7(
        string databasePath,
        Guid expectedStorageId,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = _connectionFactory.Open(
            databasePath, masterKey, SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);
        ApplicationIdentitySqlSchema.ValidateTables(connection);
        ApplicationCapturePolicySqlSchema.ValidateTables(connection);
        ClipboardHistorySqlSchema.ValidateTables(connection);
        GlobalCapturePolicySqlSchema.ValidateTables(connection);
        CustomBinaryFormatConfigurationSqlSchema.ValidateTable(connection);

        using SqliteTransaction transaction = connection.BeginTransaction();
        PendingPolicyMaintenanceSqlSchema.CreateTable(connection, transaction);
        UpdateCurrentSchemaVersion(
            connection, transaction, expectedStorageId,
            CustomBinaryConfigurationCurrentSchemaVersion, PendingPolicyMaintenanceCurrentSchemaVersion);
        SetUserVersion(connection, transaction, PendingPolicyMaintenanceCurrentSchemaVersion);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void MigrateCurrentFromV7ToV8(
        string databasePath,
        Guid expectedStorageId,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = _connectionFactory.Open(
            databasePath, masterKey, SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);
        ApplicationIdentitySqlSchema.ValidateTables(connection);
        ApplicationCapturePolicySqlSchema.ValidateTables(connection);
        ClipboardHistorySqlSchema.ValidateTables(connection);
        GlobalCapturePolicySqlSchema.ValidateTables(connection);
        CustomBinaryFormatConfigurationSqlSchema.ValidateTable(connection);
        PendingPolicyMaintenanceSqlSchema.ValidateTable(connection);

        using SqliteTransaction transaction = connection.BeginTransaction();
        ApplicationDiscoveredFormatSqlSchema.CreateTable(connection, transaction);
        UpdateCurrentSchemaVersion(
            connection, transaction, expectedStorageId,
            PendingPolicyMaintenanceCurrentSchemaVersion, ApplicationDiscoveredFormatCurrentSchemaVersion);
        SetUserVersion(connection, transaction, ApplicationDiscoveredFormatCurrentSchemaVersion);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void MigrateCurrentFromV8ToV9(
        string databasePath,
        Guid expectedStorageId,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = _connectionFactory.Open(
            databasePath, masterKey, SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);
        ApplicationIdentitySqlSchema.ValidateTables(connection);
        ApplicationCapturePolicySqlSchema.ValidateTables(connection);
        ClipboardHistorySqlSchema.ValidateTables(connection);
        GlobalCapturePolicySqlSchema.ValidateTables(connection);
        CustomBinaryFormatConfigurationSqlSchema.ValidateTable(connection);
        PendingPolicyMaintenanceSqlSchema.ValidateTable(connection);
        ApplicationDiscoveredFormatSqlSchema.ValidateTable(connection);

        using SqliteTransaction transaction = connection.BeginTransaction();
        PendingArchiveSplitSqlSchema.CreateTables(connection, transaction);
        UpdateCurrentSchemaVersion(
            connection, transaction, expectedStorageId,
            ApplicationDiscoveredFormatCurrentSchemaVersion, PendingArchiveSplitCurrentSchemaVersion);
        SetUserVersion(connection, transaction, PendingArchiveSplitCurrentSchemaVersion);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void MigrateCurrentFromV9ToV10(
        string databasePath,
        Guid expectedStorageId,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = _connectionFactory.Open(
            databasePath, masterKey, SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);
        ApplicationIdentitySqlSchema.ValidateTables(connection);
        ApplicationCapturePolicySqlSchema.ValidateTables(connection);
        ClipboardHistorySqlSchema.ValidateTables(connection);
        GlobalCapturePolicySqlSchema.ValidateTables(connection);
        CustomBinaryFormatConfigurationSqlSchema.ValidateTable(connection);
        PendingPolicyMaintenanceSqlSchema.ValidateTable(connection);
        ApplicationDiscoveredFormatSqlSchema.ValidateTable(connection);
        PendingArchiveSplitSqlSchema.ValidateTables(connection);

        using SqliteTransaction transaction = connection.BeginTransaction();
        PendingArchiveRotationSqlSchema.CreateTables(connection, transaction);
        UpdateCurrentSchemaVersion(
            connection, transaction, expectedStorageId,
            PendingArchiveSplitCurrentSchemaVersion, PendingArchiveRotationCurrentSchemaVersion);
        SetUserVersion(connection, transaction, PendingArchiveRotationCurrentSchemaVersion);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void MigrateCurrentFromV10ToV11(
        string databasePath,
        Guid expectedStorageId,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = _connectionFactory.Open(
            databasePath, masterKey, SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);
        ApplicationIdentitySqlSchema.ValidateTables(connection);
        ApplicationCapturePolicySqlSchema.ValidateTables(connection);
        ClipboardHistorySqlSchema.ValidateTables(connection);
        GlobalCapturePolicySqlSchema.ValidateTables(connection);
        CustomBinaryFormatConfigurationSqlSchema.ValidateTable(connection);
        PendingPolicyMaintenanceSqlSchema.ValidateTable(connection);
        ApplicationDiscoveredFormatSqlSchema.ValidateTable(connection);
        PendingArchiveSplitSqlSchema.ValidateTables(connection);
        PendingArchiveRotationSqlSchema.ValidateTables(connection);

        using SqliteTransaction transaction = connection.BeginTransaction();
        ApplicationGroupMemberSqlSchema.CreateTable(connection, transaction);
        UpdateCurrentSchemaVersion(
            connection, transaction, expectedStorageId,
            PendingArchiveRotationCurrentSchemaVersion, ApplicationGroupMemberCurrentSchemaVersion);
        SetUserVersion(connection, transaction, ApplicationGroupMemberCurrentSchemaVersion);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    /// <summary>
    /// Replaces v11 root-based groups with group entities and turns every legacy personal policy
    /// into a group of its own, per <c>docs/APPLICATION_GROUP_PROTOCOL.md</c> §2.2.
    /// </summary>
    private void MigrateCurrentFromV11ToV12(
        string databasePath,
        Guid expectedStorageId,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = _connectionFactory.Open(
            databasePath, masterKey, SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);
        ApplicationIdentitySqlSchema.ValidateTables(connection);
        ApplicationCapturePolicySqlSchema.ValidateTables(connection);
        ClipboardHistorySqlSchema.ValidateTables(connection);
        GlobalCapturePolicySqlSchema.ValidateTables(connection);
        CustomBinaryFormatConfigurationSqlSchema.ValidateTable(connection);
        PendingPolicyMaintenanceSqlSchema.ValidateTable(connection);
        ApplicationDiscoveredFormatSqlSchema.ValidateTable(connection);
        PendingArchiveSplitSqlSchema.ValidateTables(connection);
        PendingArchiveRotationSqlSchema.ValidateTables(connection);
        ApplicationGroupMemberSqlSchema.ValidateTable(connection);

        using SqliteTransaction transaction = connection.BeginTransaction();
        ApplicationGroupV12Migration.MigrateInTransaction(
            connection,
            transaction,
            DateTimeOffset.UtcNow,
            cancellationToken);
        UpdateCurrentSchemaVersion(
            connection, transaction, expectedStorageId,
            ApplicationGroupMemberCurrentSchemaVersion, CurrentSchemaVersion);
        SetUserVersion(connection, transaction, CurrentSchemaVersion);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void MigrateCatalogFromV1ToV2(
        string databasePath,
        Guid expectedStorageId,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            masterKey,
            SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);

        using SqliteTransaction transaction = connection.BeginTransaction();
        ExternalPayloadCatalogSqlSchema.CreateTables(connection, transaction);

        UpdateCatalogSchemaVersion(
            connection,
            transaction,
            expectedStorageId,
            LegacyCatalogSchemaVersion,
            ExternalPayloadCatalogSchemaVersion);
        SetUserVersion(connection, transaction, ExternalPayloadCatalogSchemaVersion);

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void MigrateCatalogFromV2ToV3(
        string databasePath,
        Guid expectedStorageId,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            masterKey,
            SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);
        ExternalPayloadCatalogSqlSchema.ValidateTables(connection);

        using SqliteTransaction transaction = connection.BeginTransaction();
        ArchiveSegmentCatalogSqlSchema.CreateTable(connection, transaction);

        UpdateCatalogSchemaVersion(
            connection,
            transaction,
            expectedStorageId,
            ExternalPayloadCatalogSchemaVersion,
            CatalogSchemaVersion);
        SetUserVersion(connection, transaction, CatalogSchemaVersion);

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private static void UpdateCurrentSchemaVersion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid expectedStorageId,
        int oldSchemaVersion,
        int newSchemaVersion)
    {
        using SqliteCommand updateIdentity = connection.CreateCommand();
        updateIdentity.Transaction = transaction;
        updateIdentity.CommandText = """
            UPDATE DatabaseIdentity
            SET SchemaVersion = $newSchemaVersion
            WHERE SingletonId = 1
              AND StorageId = $storageId
              AND DatabaseRole = $role
              AND SchemaVersion = $oldSchemaVersion;
            """;
        updateIdentity.Parameters.AddWithValue("$newSchemaVersion", newSchemaVersion);
        updateIdentity.Parameters.AddWithValue("$storageId", expectedStorageId.ToString("D"));
        updateIdentity.Parameters.AddWithValue("$role", DatabaseRole.Current.ToString());
        updateIdentity.Parameters.AddWithValue("$oldSchemaVersion", oldSchemaVersion);
        if (updateIdentity.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException(
                $"Current v{oldSchemaVersion} identity changed before schema migration could be committed.");
        }
    }

    private static void UpdateCatalogSchemaVersion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid expectedStorageId,
        int oldSchemaVersion,
        int newSchemaVersion)
    {
        using SqliteCommand updateIdentity = connection.CreateCommand();
        updateIdentity.Transaction = transaction;
        updateIdentity.CommandText = """
            UPDATE DatabaseIdentity
            SET SchemaVersion = $newSchemaVersion
            WHERE SingletonId = 1
              AND StorageId = $storageId
              AND DatabaseRole = $role
              AND SchemaVersion = $oldSchemaVersion;
            """;
        updateIdentity.Parameters.AddWithValue("$newSchemaVersion", newSchemaVersion);
        updateIdentity.Parameters.AddWithValue("$storageId", expectedStorageId.ToString("D"));
        updateIdentity.Parameters.AddWithValue("$role", DatabaseRole.StorageCatalog.ToString());
        updateIdentity.Parameters.AddWithValue("$oldSchemaVersion", oldSchemaVersion);
        if (updateIdentity.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException(
                $"Catalog v{oldSchemaVersion} identity changed before schema migration could be committed.");
        }
    }

    private static void SetUserVersion(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int schemaVersion)
    {
        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.Transaction = transaction;
        userVersion.CommandText = $"PRAGMA user_version = {schemaVersion};";
        userVersion.ExecuteNonQuery();
    }

    private int ValidateDatabase(
        string databasePath,
        Guid expectedStorageId,
        DatabaseRole expectedRole,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken,
        params int[] allowedSchemaVersions)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (allowedSchemaVersions.Length == 0)
        {
            throw new ArgumentException("At least one schema version must be allowed.", nameof(allowedSchemaVersions));
        }

        using SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            masterKey,
            SqliteOpenMode.ReadWrite);
        EnableForeignKeys(connection);

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

        int schemaVersion;
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

            schemaVersion = reader.GetInt32(3);
            if (!Guid.TryParse(reader.GetString(0), out Guid storageId) ||
                storageId != expectedStorageId ||
                !Guid.TryParse(reader.GetString(1), out Guid databaseId) ||
                databaseId == Guid.Empty ||
                !Enum.TryParse(reader.GetString(2), ignoreCase: false, out DatabaseRole role) ||
                role != expectedRole ||
                !allowedSchemaVersions.Contains(schemaVersion) ||
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
            if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
            {
                throw new InvalidDataException("SQLite user_version не соответствует schema version.");
            }
        }

        if (expectedRole == DatabaseRole.Current &&
            schemaVersion >= ApplicationIdentityCurrentSchemaVersion)
        {
            ApplicationIdentitySqlSchema.ValidateTables(connection);
        }

        if (expectedRole == DatabaseRole.Current &&
            schemaVersion >= ApplicationPolicyCurrentSchemaVersion)
        {
            ApplicationCapturePolicySqlSchema.ValidateTables(connection);
        }

        if (expectedRole == DatabaseRole.Current && schemaVersion >= HistoryCurrentSchemaVersion)
        {
            ClipboardHistorySqlSchema.ValidateTables(connection);
        }

        if (expectedRole == DatabaseRole.Current &&
            schemaVersion >= GlobalCapturePolicyCurrentSchemaVersion)
        {
            GlobalCapturePolicySqlSchema.ValidateTables(connection);
        }

        if (expectedRole == DatabaseRole.Current &&
            schemaVersion >= CustomBinaryConfigurationCurrentSchemaVersion)
        {
            CustomBinaryFormatConfigurationSqlSchema.ValidateTable(connection);
        }

        if (expectedRole == DatabaseRole.Current &&
            schemaVersion >= PendingPolicyMaintenanceCurrentSchemaVersion)
        {
            PendingPolicyMaintenanceSqlSchema.ValidateTable(connection);
        }

        if (expectedRole == DatabaseRole.Current &&
            schemaVersion >= ApplicationDiscoveredFormatCurrentSchemaVersion)
        {
            ApplicationDiscoveredFormatSqlSchema.ValidateTable(connection);
        }

        if (expectedRole == DatabaseRole.Current &&
            schemaVersion >= PendingArchiveSplitCurrentSchemaVersion)
        {
            PendingArchiveSplitSqlSchema.ValidateTables(connection);
        }

        if (expectedRole == DatabaseRole.Current &&
            schemaVersion >= PendingArchiveRotationCurrentSchemaVersion)
        {
            PendingArchiveRotationSqlSchema.ValidateTables(connection);
        }

        if (expectedRole == DatabaseRole.Current &&
            schemaVersion == ApplicationGroupMemberCurrentSchemaVersion)
        {
            ApplicationGroupMemberSqlSchema.ValidateTable(connection);
        }

        if (expectedRole == DatabaseRole.Current &&
            schemaVersion >= ApplicationGroupSqlSchema.MinimumCurrentSchemaVersion)
        {
            ApplicationGroupSqlSchema.ValidateTables(connection);
        }

        if (expectedRole == DatabaseRole.StorageCatalog &&
            schemaVersion >= ExternalPayloadCatalogSchemaVersion)
        {
            ExternalPayloadCatalogSqlSchema.ValidateTables(connection);
        }

        if (expectedRole == DatabaseRole.StorageCatalog &&
            schemaVersion >= CatalogSchemaVersion)
        {
            ArchiveSegmentCatalogSqlSchema.ValidateTable(connection);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return schemaVersion;
    }

    private static int GetSchemaVersion(DatabaseRole role)
    {
        return role switch
        {
            DatabaseRole.Current => CurrentSchemaVersion,
            DatabaseRole.StorageCatalog => CatalogSchemaVersion,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unsupported database role."),
        };
    }

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
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
