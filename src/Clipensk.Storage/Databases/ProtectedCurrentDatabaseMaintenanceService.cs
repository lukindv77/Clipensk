using System.Globalization;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

/// <summary>
/// Runs physical SQLite maintenance for the active Current database without changing
/// logical storage identity, schema version, or storage-catalog projections.
/// </summary>
public sealed class ProtectedCurrentDatabaseMaintenanceService
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;

    public ProtectedCurrentDatabaseMaintenanceService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
    }

    public Task<DatabaseIdentity> VacuumAsync(CancellationToken cancellationToken = default) =>
        MaintainAsync("VACUUM;", cancellationToken);

    public Task<DatabaseIdentity> OptimizeAsync(CancellationToken cancellationToken = default) =>
        MaintainAsync("PRAGMA optimize;", cancellationToken);

    private async Task<DatabaseIdentity> MaintainAsync(
        string commandText,
        CancellationToken cancellationToken)
    {
        using ProtectedStorageMutationLease mutationLease =
            await _session.AcquireMutationLeaseAsync(cancellationToken)
                .ConfigureAwait(false);

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();

        // This is intentionally a pure read-only, current-version preflight. The normal storage
        // validator may migrate older schemas after validation; physical maintenance must never
        // turn VACUUM/optimize into an implicit schema migration.
        DatabaseIdentity identity = await Task.Run(
            () => ValidateCurrentPair(token),
            CancellationToken.None).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();
        await Task.Run(
            () => MaintainCurrentCore(commandText, token),
            CancellationToken.None).ConfigureAwait(false);

        return identity;
    }

    private DatabaseIdentity ValidateCurrentPair(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string currentDirectory = Path.Combine(
            Path.GetFullPath(_session.DataRootPath),
            "Current");
        string currentPath = Path.Combine(currentDirectory, "current.db");
        string catalogPath = Path.Combine(currentDirectory, "storage-catalog.db");

        if (!File.Exists(currentPath) || !File.Exists(catalogPath))
        {
            throw new InvalidDataException(
                "Current physical maintenance requires an existing Current + storage-catalog pair.");
        }

        ReadOnlyMemory<byte> key = _session.DangerousGetMasterKeyMemory();
        DatabaseIdentity currentIdentity = ValidateDatabase(
            currentPath,
            DatabaseRole.Current,
            ProtectedStorageDatabaseService.CurrentSchemaVersion,
            key,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        _ = ValidateDatabase(
            catalogPath,
            DatabaseRole.StorageCatalog,
            ProtectedStorageDatabaseService.CatalogSchemaVersion,
            key,
            cancellationToken);

        return currentIdentity;
    }

    private DatabaseIdentity ValidateDatabase(
        string databasePath,
        DatabaseRole expectedRole,
        int expectedSchemaVersion,
        ReadOnlyMemory<byte> masterKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            masterKey,
            SqliteOpenMode.ReadOnly);
        EnableForeignKeys(connection);
        cancellationToken.ThrowIfCancellationRequested();

        using (SqliteCommand keyProbe = connection.CreateCommand())
        {
            keyProbe.CommandText = "SELECT count(*) FROM sqlite_master;";
            _ = keyProbe.ExecuteScalar();
        }

        using (SqliteCommand quickCheck = connection.CreateCommand())
        {
            quickCheck.CommandText = "PRAGMA quick_check;";
            if (quickCheck.ExecuteScalar() is not string result ||
                !string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"{expectedRole} SQLite quick_check failed before physical maintenance.");
            }
        }

        using (SqliteCommand count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM DatabaseIdentity;";
            if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidDataException(
                    $"{expectedRole} DatabaseIdentity must contain exactly one row.");
            }
        }

        DatabaseIdentity identity;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT StorageId, DatabaseId, DatabaseRole, SchemaVersion, EncryptionVersion,
                       CreatedAtUtc, ArchiveBaseNumber, ArchiveSplitSequence,
                       CoverageStartDate, CoverageEndDate
                FROM DatabaseIdentity
                WHERE SingletonId = 1;
                """;

            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidDataException($"{expectedRole} DatabaseIdentity is missing.");
            }

            if (!Guid.TryParse(reader.GetString(0), out Guid storageId) ||
                storageId != _session.StorageId ||
                !Guid.TryParse(reader.GetString(1), out Guid databaseId) ||
                databaseId == Guid.Empty ||
                !Enum.TryParse(reader.GetString(2), ignoreCase: false, out DatabaseRole role) ||
                role != expectedRole ||
                reader.GetInt32(3) != expectedSchemaVersion ||
                reader.GetInt32(4) != ProtectedStorageDatabaseService.CurrentEncryptionVersion ||
                !DateTimeOffset.TryParse(
                    reader.GetString(5),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset createdAtUtc))
            {
                throw new InvalidDataException(
                    $"{expectedRole} DatabaseIdentity does not match the active storage.");
            }

            for (int index = 6; index <= 9; index++)
            {
                if (!reader.IsDBNull(index))
                {
                    throw new InvalidDataException(
                        "Current/Catalog DatabaseIdentity must not contain archive coverage.");
                }
            }

            identity = new DatabaseIdentity(
                storageId,
                databaseId,
                role,
                expectedSchemaVersion,
                ProtectedStorageDatabaseService.CurrentEncryptionVersion,
                createdAtUtc);
        }

        using (SqliteCommand userVersion = connection.CreateCommand())
        {
            userVersion.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) !=
                expectedSchemaVersion)
            {
                throw new InvalidDataException(
                    $"{expectedRole} user_version does not match the current schema version.");
            }
        }

        if (expectedRole == DatabaseRole.Current)
        {
            ApplicationIdentitySqlSchema.ValidateTables(connection);
            ApplicationCapturePolicySqlSchema.ValidateTables(connection);
            ClipboardHistorySqlSchema.ValidateTables(connection);
            GlobalCapturePolicySqlSchema.ValidateTables(connection);
            CustomBinaryFormatConfigurationSqlSchema.ValidateTable(connection);
            PendingPolicyMaintenanceSqlSchema.ValidateTable(connection);
            ApplicationDiscoveredFormatSqlSchema.ValidateTable(connection);
            PendingArchiveSplitSqlSchema.ValidateTables(connection);
        }
        else if (expectedRole == DatabaseRole.StorageCatalog)
        {
            ExternalPayloadCatalogSqlSchema.ValidateTables(connection);
            ArchiveSegmentCatalogSqlSchema.ValidateTable(connection);
        }
        else
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRole),
                expectedRole,
                "Current physical maintenance only validates Current and StorageCatalog databases.");
        }

        using (SqliteCommand foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check;";
            using SqliteDataReader reader = foreignKeys.ExecuteReader();
            if (reader.Read())
            {
                throw new InvalidDataException(
                    $"{expectedRole} contains invalid foreign-key references.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return identity;
    }

    private void MaintainCurrentCore(
        string commandText,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string currentPath = Path.Combine(
            Path.GetFullPath(_session.DataRootPath),
            "Current",
            "current.db");

        using SqliteConnection connection = _connectionFactory.Open(
            currentPath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadWrite);
        cancellationToken.ThrowIfCancellationRequested();

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = commandText;
            command.ExecuteNonQuery();
        }

        // VACUUM/PRAGMA optimize cannot be rolled back once SQLite has completed the command.
        // Do not turn late caller cancellation into a false rollback signal; verify the durable
        // result on the same keyed connection before releasing the mutation lease.
        using SqliteCommand quickCheck = connection.CreateCommand();
        quickCheck.CommandText = "PRAGMA quick_check;";
        if (quickCheck.ExecuteScalar() is not string result ||
            !string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Current SQLite quick_check failed after physical maintenance.");
        }
    }

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }
}
