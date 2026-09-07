using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

public sealed class ProtectedArchiveDatabaseService
{
    public const int ArchiveSchemaVersion = 1;

    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;

    public ProtectedArchiveDatabaseService(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
    }

    public Task<DatabaseIdentity> CreateAsync(
        ArchiveFileName archiveFileName,
        JournalDateRange coverage,
        CancellationToken cancellationToken = default)
    {
        ValidateArchiveFileNameValue(archiveFileName);
        return Task.Run(
            () =>
            {
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                    _session.CancellationToken,
                    cancellationToken);
                return CreateCore(archiveFileName, coverage, linked.Token);
            },
            CancellationToken.None);
    }

    public Task<DatabaseIdentity> ValidateAsync(
        ArchiveFileName archiveFileName,
        CancellationToken cancellationToken = default)
    {
        ValidateArchiveFileNameValue(archiveFileName);
        return Task.Run(
            () =>
            {
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                    _session.CancellationToken,
                    cancellationToken);
                linked.Token.ThrowIfCancellationRequested();
                string databasePath = GetArchiveDatabasePath(archiveFileName);
                return ValidateDatabase(databasePath, archiveFileName, expectedCoverage: null, linked.Token);
            },
            CancellationToken.None);
    }

    private DatabaseIdentity CreateCore(
        ArchiveFileName archiveFileName,
        JournalDateRange coverage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = _session.DangerousGetMasterKeyMemory();

        string archiveDirectory = Path.Combine(Path.GetFullPath(_session.DataRootPath), "Archive");
        Directory.CreateDirectory(archiveDirectory);

        string finalPath = Path.Combine(archiveDirectory, archiveFileName.FileName);
        if (File.Exists(finalPath))
        {
            throw new InvalidOperationException(
                $"Archive database '{archiveFileName.FileName}' already exists.");
        }

        string stagingPath = Path.Combine(
            archiveDirectory,
            $".clipensk-archive-create-{Guid.NewGuid():N}.tmp");

        try
        {
            CreateDatabase(stagingPath, archiveFileName, coverage, cancellationToken);
            DatabaseIdentity identity = ValidateDatabase(
                stagingPath,
                archiveFileName,
                coverage,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(finalPath))
            {
                throw new InvalidOperationException(
                    $"Archive database '{archiveFileName.FileName}' appeared during creation.");
            }

            File.Move(stagingPath, finalPath);
            return identity;
        }
        finally
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
    }

    private void CreateDatabase(
        string databasePath,
        ArchiveFileName archiveFileName,
        JournalDateRange coverage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlyMemory<byte> masterKey = _session.DangerousGetMasterKeyMemory();

        using SqliteConnection connection = _connectionFactory.Open(
            databasePath,
            masterKey,
            SqliteOpenMode.ReadWriteCreate);
        EnableForeignKeys(connection);
        cancellationToken.ThrowIfCancellationRequested();

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

        Guid databaseId = Guid.NewGuid();
        DateTimeOffset createdAtUtc = DateTimeOffset.UtcNow;
        using (SqliteCommand insertIdentity = connection.CreateCommand())
        {
            insertIdentity.Transaction = transaction;
            insertIdentity.CommandText = """
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
                VALUES (
                    1,
                    $storageId,
                    $databaseId,
                    $role,
                    $schemaVersion,
                    $encryptionVersion,
                    $createdAtUtc,
                    $archiveBaseNumber,
                    $archiveSplitSequence,
                    $coverageStartDate,
                    $coverageEndDate);
                """;
            insertIdentity.Parameters.AddWithValue("$storageId", _session.StorageId.ToString("D"));
            insertIdentity.Parameters.AddWithValue("$databaseId", databaseId.ToString("D"));
            insertIdentity.Parameters.AddWithValue("$role", DatabaseRole.Archive.ToString());
            insertIdentity.Parameters.AddWithValue("$schemaVersion", ArchiveSchemaVersion);
            insertIdentity.Parameters.AddWithValue(
                "$encryptionVersion",
                ProtectedStorageDatabaseService.CurrentEncryptionVersion);
            insertIdentity.Parameters.AddWithValue(
                "$createdAtUtc",
                createdAtUtc.ToString("O", CultureInfo.InvariantCulture));
            insertIdentity.Parameters.AddWithValue("$archiveBaseNumber", archiveFileName.BaseNumber);
            insertIdentity.Parameters.AddWithValue(
                "$archiveSplitSequence",
                archiveFileName.SplitSequence == ArchiveFileName.NoSplit
                    ? DBNull.Value
                    : archiveFileName.SplitSequence);
            insertIdentity.Parameters.AddWithValue(
                "$coverageStartDate",
                coverage.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insertIdentity.Parameters.AddWithValue(
                "$coverageEndDate",
                coverage.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insertIdentity.ExecuteNonQuery();
        }

        ApplicationIdentitySqlSchema.CreateTables(connection, transaction);
        ClipboardHistorySqlSchema.CreateTables(connection, transaction);

        using (SqliteCommand userVersion = connection.CreateCommand())
        {
            userVersion.Transaction = transaction;
            userVersion.CommandText = $"PRAGMA user_version = {ArchiveSchemaVersion};";
            userVersion.ExecuteNonQuery();
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private DatabaseIdentity ValidateDatabase(
        string databasePath,
        ArchiveFileName expectedFileName,
        JournalDateRange? expectedCoverage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlyMemory<byte> masterKey = _session.DangerousGetMasterKeyMemory();

        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException("Archive database was not found.", databasePath);
        }

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
                throw new InvalidDataException("Archive SQLite quick_check failed.");
            }
        }

        ValidateDatabaseIdentityColumns(connection);

        using (SqliteCommand count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM DatabaseIdentity;";
            if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidDataException("Archive DatabaseIdentity must contain exactly one row.");
            }
        }

        DatabaseIdentity identity = ReadAndValidateIdentity(
            connection,
            expectedFileName,
            expectedCoverage);

        using (SqliteCommand userVersion = connection.CreateCommand())
        {
            userVersion.CommandText = "PRAGMA user_version;";
            if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) !=
                ArchiveSchemaVersion)
            {
                throw new InvalidDataException("Archive user_version does not match schema version.");
            }
        }

        ApplicationIdentitySqlSchema.ValidateTables(connection);
        ClipboardHistorySqlSchema.ValidateTables(connection);
        ValidateForeignKeys(connection);
        ValidateHistoryCoverage(connection, identity, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        return identity;
    }

    private DatabaseIdentity ReadAndValidateIdentity(
        SqliteConnection connection,
        ArchiveFileName expectedFileName,
        JournalDateRange? expectedCoverage)
    {
        using SqliteCommand command = connection.CreateCommand();
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
            throw new InvalidDataException("Archive DatabaseIdentity is missing.");
        }

        if (!Guid.TryParse(reader.GetString(0), out Guid storageId) || storageId != _session.StorageId ||
            !Guid.TryParse(reader.GetString(1), out Guid databaseId) || databaseId == Guid.Empty ||
            !Enum.TryParse(reader.GetString(2), ignoreCase: false, out DatabaseRole role) ||
            role != DatabaseRole.Archive ||
            reader.GetInt32(3) != ArchiveSchemaVersion ||
            reader.GetInt32(4) != ProtectedStorageDatabaseService.CurrentEncryptionVersion ||
            !DateTimeOffset.TryParse(
                reader.GetString(5),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset createdAtUtc))
        {
            throw new InvalidDataException("Archive DatabaseIdentity does not match the active storage.");
        }

        if (reader.IsDBNull(6) || reader.GetInt32(6) != expectedFileName.BaseNumber)
        {
            throw new InvalidDataException("Archive base number does not match the file name.");
        }

        int? splitSequence = reader.IsDBNull(7) ? null : reader.GetInt32(7);
        if (expectedFileName.SplitSequence == ArchiveFileName.NoSplit)
        {
            if (splitSequence is not null)
            {
                throw new InvalidDataException("Unsplit archive must store a null split sequence.");
            }
        }
        else if (splitSequence != expectedFileName.SplitSequence || splitSequence <= 0)
        {
            throw new InvalidDataException("Archive split sequence does not match the file name.");
        }

        if (reader.IsDBNull(8) || reader.IsDBNull(9) ||
            !DateOnly.TryParseExact(
                reader.GetString(8),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly coverageStart) ||
            !DateOnly.TryParseExact(
                reader.GetString(9),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly coverageEnd) ||
            coverageEnd < coverageStart)
        {
            throw new InvalidDataException("Archive coverage is invalid.");
        }

        var coverage = new JournalDateRange(coverageStart, coverageEnd);
        if (expectedCoverage is { } expected && expected != coverage)
        {
            throw new InvalidDataException("Archive coverage does not match the requested range.");
        }

        return new DatabaseIdentity(
            storageId,
            databaseId,
            DatabaseRole.Archive,
            ArchiveSchemaVersion,
            ProtectedStorageDatabaseService.CurrentEncryptionVersion,
            createdAtUtc,
            expectedFileName.BaseNumber,
            splitSequence,
            coverageStart,
            coverageEnd);
    }

    private static void ValidateDatabaseIdentityColumns(SqliteConnection connection)
    {
        string[] expected =
        [
            "SingletonId",
            "StorageId",
            "DatabaseId",
            "DatabaseRole",
            "SchemaVersion",
            "EncryptionVersion",
            "CreatedAtUtc",
            "ArchiveBaseNumber",
            "ArchiveSplitSequence",
            "CoverageStartDate",
            "CoverageEndDate",
        ];

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('DatabaseIdentity');";
        using SqliteDataReader reader = command.ExecuteReader();

        int index = 0;
        while (reader.Read())
        {
            if (index >= expected.Length ||
                !string.Equals(reader.GetString(1), expected[index], StringComparison.Ordinal))
            {
                throw new InvalidDataException("Archive DatabaseIdentity column contract is invalid.");
            }
            index++;
        }

        if (index != expected.Length)
        {
            throw new InvalidDataException("Archive DatabaseIdentity is missing required columns.");
        }
    }

    private static void ValidateForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using SqliteDataReader reader = command.ExecuteReader();
        if (reader.Read())
        {
            throw new InvalidDataException("Archive contains invalid foreign-key references.");
        }
    }

    private static void ValidateHistoryCoverage(
        SqliteConnection connection,
        DatabaseIdentity identity,
        CancellationToken cancellationToken)
    {
        if (identity.CoverageStartDate is not DateOnly start ||
            identity.CoverageEndDate is not DateOnly end)
        {
            throw new InvalidDataException("Archive coverage is missing.");
        }

        var coverage = new JournalDateRange(start, end);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT CalendarDate FROM ClipboardHistoryEvent;";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DateOnly.TryParseExact(
                    reader.GetString(0),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly calendarDate) ||
                !coverage.Contains(calendarDate))
            {
                throw new InvalidDataException(
                    "Archive history contains an event outside assigned coverage.");
            }
        }
    }

    private string GetArchiveDatabasePath(ArchiveFileName archiveFileName)
    {
        string archiveDirectory = Path.Combine(Path.GetFullPath(_session.DataRootPath), "Archive");
        return Path.Combine(archiveDirectory, archiveFileName.FileName);
    }

    private static void ValidateArchiveFileNameValue(ArchiveFileName archiveFileName)
    {
        if (!ArchiveFileName.TryParse(archiveFileName.FileName, out ArchiveFileName parsed) ||
            parsed != archiveFileName)
        {
            throw new ArgumentOutOfRangeException(
                nameof(archiveFileName),
                "Archive file name value is outside the canonical Clipensk range.");
        }
    }

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }
}
