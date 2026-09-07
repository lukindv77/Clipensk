using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Applications;
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

/// <summary>
/// Explicit pre-session recovery for the case where Current is present but
/// storage-catalog.db is missing. Normal unlock validation remains fail-closed.
/// </summary>
public sealed class ProtectedStorageCatalogRecoveryService
{
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;

    public ProtectedStorageCatalogRecoveryService(
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
    }

    public Task<ProtectedStorageDatabaseResult> RecoverMissingCatalogAsync(
        string dataRootPath,
        Guid storageId,
        ReadOnlyMemory<byte> masterKey,
        DateOnly currentCalendarDate,
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

        string root = Path.GetFullPath(dataRootPath);
        return Task.Run(
            () => RecoverCore(
                root,
                storageId,
                masterKey,
                currentCalendarDate,
                cancellationToken),
            cancellationToken);
    }

    private ProtectedStorageDatabaseResult RecoverCore(
        string dataRootPath,
        Guid storageId,
        ReadOnlyMemory<byte> masterKey,
        DateOnly currentCalendarDate,
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
        string currentPath = Path.Combine(currentDirectory, "current.db");
        string catalogPath = Path.Combine(currentDirectory, "storage-catalog.db");
        if (!File.Exists(currentPath) || File.Exists(catalogPath))
        {
            return new ProtectedStorageDatabaseResult(
                ProtectedStorageDatabaseStatus.MissingOrPartialStorage,
                WasInitialized: false);
        }

        string stagingPath = Path.Combine(
            currentDirectory,
            $".clipensk-catalog-recovery-{Guid.NewGuid():N}.tmp");

        try
        {
            RecoverySnapshot first = DeriveSnapshot(
                dataRootPath,
                currentPath,
                storageId,
                masterKey,
                currentCalendarDate,
                cancellationToken);

            CreateStagingCatalog(
                stagingPath,
                storageId,
                masterKey,
                first,
                cancellationToken);
            ValidateStagingCatalog(
                stagingPath,
                storageId,
                masterKey,
                first,
                cancellationToken);

            RecoverySnapshot second = DeriveSnapshot(
                dataRootPath,
                currentPath,
                storageId,
                masterKey,
                currentCalendarDate,
                cancellationToken);
            if (!SnapshotsEqual(first, second))
            {
                throw new InvalidDataException(
                    "Current/Archive source state changed during Catalog recovery; retry the operation.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(currentPath) || File.Exists(catalogPath))
            {
                throw new CatalogRecoveryStateException(
                    "Protected storage pair changed before recovered Catalog could be published.");
            }

            File.Move(stagingPath, catalogPath);

            // No cancellation check after publication. The staging Catalog was fully
            // validated before this atomic move, so durable publication remains success.
            return new ProtectedStorageDatabaseResult(
                ProtectedStorageDatabaseStatus.Success,
                WasInitialized: false);
        }
        catch (ProtectedStorageEncryptionUnavailableException)
        {
            return new ProtectedStorageDatabaseResult(
                ProtectedStorageDatabaseStatus.EncryptionEngineUnavailable,
                WasInitialized: false);
        }
        catch (CatalogRecoveryStateException)
        {
            return new ProtectedStorageDatabaseResult(
                ProtectedStorageDatabaseStatus.MissingOrPartialStorage,
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
        finally
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
    }

    private RecoverySnapshot DeriveSnapshot(
        string dataRootPath,
        string currentPath,
        Guid storageId,
        ReadOnlyMemory<byte> masterKey,
        DateOnly currentCalendarDate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var externalProjection = new ExternalProjectionAccumulator(
            Path.Combine(dataRootPath, "Files"));

        CurrentSource current = ReadCurrentSource(
            currentPath,
            storageId,
            masterKey,
            externalProjection,
            cancellationToken);

        string archiveDirectory = Path.Combine(dataRootPath, "Archive");
        string[] archiveNames = EnumerateArchiveNames(archiveDirectory, cancellationToken);
        var databaseIds = new HashSet<Guid>();
        var archives = new List<ArchiveSegmentDescriptor>(archiveNames.Length);

        foreach (string archiveName in archiveNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArchiveSource archive = ReadArchiveSource(
                Path.Combine(archiveDirectory, archiveName),
                archiveName,
                storageId,
                masterKey,
                externalProjection,
                cancellationToken);
            if (!databaseIds.Add(archive.Descriptor.DatabaseId))
            {
                throw new InvalidDataException(
                    "Multiple Archive files expose the same DatabaseId during Catalog recovery.");
            }
            archives.Add(archive.Descriptor);
        }

        StorageQueryPlanner.ValidateArchiveCoverage(archives);

        ArchiveSegmentDescriptor[] projectedArchives = archives
            .OrderBy(item => item.Coverage.StartDate)
            .ThenBy(item => item.Coverage.EndDate)
            .ThenBy(item => item.FileName, StringComparer.Ordinal)
            .Select(item => item with
            {
                IsSealed =
                    item.Coverage.EndDate < currentCalendarDate &&
                    !current.CalendarDates.Any(item.Coverage.Contains),
            })
            .ToArray();

        ExternalPayloadAddress[] externalAddresses = externalProjection.Addresses
            .OrderBy(item => item.Sha256, StringComparer.Ordinal)
            .ToArray();

        cancellationToken.ThrowIfCancellationRequested();
        return new RecoverySnapshot(
            current.Identity,
            externalAddresses,
            projectedArchives);
    }

    private CurrentSource ReadCurrentSource(
        string currentPath,
        Guid storageId,
        ReadOnlyMemory<byte> masterKey,
        ExternalProjectionAccumulator externalProjection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = _connectionFactory.Open(
            currentPath,
            masterKey,
            SqliteOpenMode.ReadOnly);
        EnableForeignKeys(connection);
        ProbeAndQuickCheck(connection, "Current");
        ValidateDatabaseIdentityColumns(connection, "Current");
        SourceIdentity identity = ReadNonArchiveIdentity(
            connection,
            storageId,
            DatabaseRole.Current,
            [1, 2, 3, 4, 5, ProtectedStorageDatabaseService.CurrentSchemaVersion]);
        ValidateUserVersion(connection, identity.SchemaVersion, "Current");

        if (identity.SchemaVersion >= 2)
        {
            ApplicationIdentitySqlSchema.ValidateTables(connection);
        }
        if (identity.SchemaVersion >= 3)
        {
            ApplicationCapturePolicySqlSchema.ValidateTables(connection);
        }
        if (identity.SchemaVersion >= 4)
        {
            ClipboardHistorySqlSchema.ValidateTables(connection);
        }
        if (identity.SchemaVersion >= 5)
        {
            GlobalCapturePolicySqlSchema.ValidateTables(connection);
        }
        if (identity.SchemaVersion >= ProtectedStorageDatabaseService.CurrentSchemaVersion)
        {
            CustomBinaryFormatConfigurationSqlSchema.ValidateTable(connection);
        }

        ValidateForeignKeys(connection, "Current");

        var calendarDates = new HashSet<DateOnly>();
        if (identity.SchemaVersion >= 4)
        {
            ReadCurrentCalendarDates(connection, calendarDates, cancellationToken);
            ScanExternalReferences(connection, externalProjection, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new CurrentSource(identity, calendarDates);
    }

    private ArchiveSource ReadArchiveSource(
        string archivePath,
        string archiveName,
        Guid storageId,
        ReadOnlyMemory<byte> masterKey,
        ExternalProjectionAccumulator externalProjection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ArchiveFileName.TryParse(archiveName, out ArchiveFileName archiveFileName) ||
            !string.Equals(archiveName, archiveFileName.FileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Archive file '{archiveName}' does not use the canonical Clipensk file name.");
        }

        using SqliteConnection connection = _connectionFactory.Open(
            archivePath,
            masterKey,
            SqliteOpenMode.ReadOnly);
        EnableForeignKeys(connection);
        ProbeAndQuickCheck(connection, $"Archive '{archiveName}'");
        ValidateDatabaseIdentityColumns(connection, $"Archive '{archiveName}'");
        DatabaseIdentity identity = ReadArchiveIdentity(
            connection,
            storageId,
            archiveFileName);
        ValidateUserVersion(
            connection,
            ProtectedArchiveDatabaseService.ArchiveSchemaVersion,
            $"Archive '{archiveName}'");
        ApplicationIdentitySqlSchema.ValidateTables(connection);
        ClipboardHistorySqlSchema.ValidateTables(connection);
        ValidateForeignKeys(connection, $"Archive '{archiveName}'");
        ValidateArchiveHistoryCoverage(connection, identity, cancellationToken);
        ScanExternalReferences(connection, externalProjection, cancellationToken);

        if (identity.CoverageStartDate is not DateOnly start ||
            identity.CoverageEndDate is not DateOnly end)
        {
            throw new InvalidDataException("Archive coverage is missing during Catalog recovery.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ArchiveSource(new ArchiveSegmentDescriptor(
            identity.DatabaseId,
            archiveFileName.FileName,
            new JournalDateRange(start, end),
            IsSealed: false));
    }

    private static SourceIdentity ReadNonArchiveIdentity(
        SqliteConnection connection,
        Guid expectedStorageId,
        DatabaseRole expectedRole,
        IReadOnlyCollection<int> allowedSchemaVersions)
    {
        EnsureSingleIdentityRow(connection);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT StorageId, DatabaseId, DatabaseRole, SchemaVersion, EncryptionVersion,
                   CreatedAtUtc, ArchiveBaseNumber, ArchiveSplitSequence,
                   CoverageStartDate, CoverageEndDate
            FROM DatabaseIdentity
            WHERE SingletonId = 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() ||
            !Guid.TryParse(reader.GetString(0), out Guid storageId) ||
            storageId != expectedStorageId ||
            !Guid.TryParse(reader.GetString(1), out Guid databaseId) ||
            databaseId == Guid.Empty ||
            !Enum.TryParse(reader.GetString(2), ignoreCase: false, out DatabaseRole role) ||
            role != expectedRole ||
            !allowedSchemaVersions.Contains(reader.GetInt32(3)) ||
            reader.GetInt32(4) != ProtectedStorageDatabaseService.CurrentEncryptionVersion ||
            !DateTimeOffset.TryParse(
                reader.GetString(5),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset createdAtUtc))
        {
            throw new InvalidDataException(
                $"{expectedRole} DatabaseIdentity does not match the requested storage.");
        }

        for (int index = 6; index <= 9; index++)
        {
            if (!reader.IsDBNull(index))
            {
                throw new InvalidDataException(
                    $"{expectedRole} DatabaseIdentity must not contain Archive coverage metadata.");
            }
        }

        return new SourceIdentity(databaseId, reader.GetInt32(3), createdAtUtc);
    }

    private static DatabaseIdentity ReadArchiveIdentity(
        SqliteConnection connection,
        Guid expectedStorageId,
        ArchiveFileName expectedFileName)
    {
        EnsureSingleIdentityRow(connection);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT StorageId, DatabaseId, DatabaseRole, SchemaVersion, EncryptionVersion,
                   CreatedAtUtc, ArchiveBaseNumber, ArchiveSplitSequence,
                   CoverageStartDate, CoverageEndDate
            FROM DatabaseIdentity
            WHERE SingletonId = 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() ||
            !Guid.TryParse(reader.GetString(0), out Guid storageId) ||
            storageId != expectedStorageId ||
            !Guid.TryParse(reader.GetString(1), out Guid databaseId) ||
            databaseId == Guid.Empty ||
            !string.Equals(
                reader.GetString(2),
                DatabaseRole.Archive.ToString(),
                StringComparison.Ordinal) ||
            reader.GetInt32(3) != ProtectedArchiveDatabaseService.ArchiveSchemaVersion ||
            reader.GetInt32(4) != ProtectedStorageDatabaseService.CurrentEncryptionVersion ||
            !DateTimeOffset.TryParse(
                reader.GetString(5),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset createdAtUtc) ||
            createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "Archive DatabaseIdentity does not match the requested storage.");
        }

        if (reader.IsDBNull(6) || reader.GetInt32(6) != expectedFileName.BaseNumber)
        {
            throw new InvalidDataException("Archive base number does not match file name.");
        }

        int? splitSequence = reader.IsDBNull(7) ? null : reader.GetInt32(7);
        if (expectedFileName.SplitSequence == ArchiveFileName.NoSplit)
        {
            if (splitSequence is not null)
            {
                throw new InvalidDataException(
                    "Unsplit Archive must store a null split sequence.");
            }
        }
        else if (splitSequence != expectedFileName.SplitSequence || splitSequence <= 0)
        {
            throw new InvalidDataException(
                "Archive split sequence does not match file name.");
        }

        if (reader.IsDBNull(8) || reader.IsDBNull(9) ||
            !DateOnly.TryParseExact(
                reader.GetString(8),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly start) ||
            !DateOnly.TryParseExact(
                reader.GetString(9),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly end) ||
            end < start)
        {
            throw new InvalidDataException("Archive coverage is invalid.");
        }

        return new DatabaseIdentity(
            storageId,
            databaseId,
            DatabaseRole.Archive,
            ProtectedArchiveDatabaseService.ArchiveSchemaVersion,
            ProtectedStorageDatabaseService.CurrentEncryptionVersion,
            createdAtUtc,
            expectedFileName.BaseNumber,
            splitSequence,
            start,
            end);
    }

    private static void ReadCurrentCalendarDates(
        SqliteConnection connection,
        ISet<DateOnly> calendarDates,
        CancellationToken cancellationToken)
    {
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
                    out DateOnly date))
            {
                throw new InvalidDataException(
                    "Current history contains an invalid CalendarDate during Catalog recovery.");
            }
            calendarDates.Add(date);
        }
    }

    private static void ValidateArchiveHistoryCoverage(
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
                    out DateOnly date) ||
                !coverage.Contains(date))
            {
                throw new InvalidDataException(
                    "Archive history contains an event outside assigned coverage.");
            }
        }
    }

    private static void ScanExternalReferences(
        SqliteConnection connection,
        ExternalProjectionAccumulator projection,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT PayloadKind, CanonicalByteCount,
                   InlineCanonicalText IS NULL, SearchText IS NULL,
                   ExternalSha256, ExternalRelativePath, ExternalSizeBytes
            FROM ClipboardHistoryPayload
            ORDER BY EventId COLLATE BINARY, PayloadOrder;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string payloadKind = reader.GetString(0);
            long canonicalByteCount = reader.GetInt64(1);
            bool inlineIsNull = reader.GetInt32(2) == 1;
            bool searchTextIsNull = reader.GetInt32(3) == 1;
            bool shaIsNull = reader.IsDBNull(4);
            bool pathIsNull = reader.IsDBNull(5);
            bool sizeIsNull = reader.IsDBNull(6);

            if (canonicalByteCount < 0)
            {
                throw new InvalidDataException(
                    "History contains a negative canonical byte count during Catalog recovery.");
            }

            if (payloadKind is "Text" or "Link" or "StorageItems")
            {
                if (inlineIsNull || !shaIsNull || !pathIsNull || !sizeIsNull)
                {
                    throw new InvalidDataException(
                        "History inline payload representation is invalid during Catalog recovery.");
                }
                continue;
            }

            if (payloadKind is not ("PngImage" or "CustomBinary") ||
                !inlineIsNull || !searchTextIsNull ||
                shaIsNull || pathIsNull || sizeIsNull)
            {
                throw new InvalidDataException(
                    "History external payload representation is invalid during Catalog recovery.");
            }

            var address = new ExternalPayloadAddress(
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt64(6));
            if (address.SizeBytes != canonicalByteCount)
            {
                throw new InvalidDataException(
                    "History external payload size does not match canonical byte count.");
            }
            projection.Add(address);
        }
    }

    private void CreateStagingCatalog(
        string stagingPath,
        Guid storageId,
        ReadOnlyMemory<byte> masterKey,
        RecoverySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = _connectionFactory.Open(
            stagingPath,
            masterKey,
            SqliteOpenMode.ReadWriteCreate);
        EnableForeignKeys(connection);
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
                VALUES (
                    1, $storageId, $databaseId, $role,
                    $schemaVersion, $encryptionVersion, $createdAtUtc,
                    NULL, NULL, NULL, NULL);
                """;
            insertIdentity.Parameters.AddWithValue("$storageId", storageId.ToString("D"));
            insertIdentity.Parameters.AddWithValue("$databaseId", Guid.NewGuid().ToString("D"));
            insertIdentity.Parameters.AddWithValue(
                "$role",
                DatabaseRole.StorageCatalog.ToString());
            insertIdentity.Parameters.AddWithValue(
                "$schemaVersion",
                ProtectedStorageDatabaseService.CatalogSchemaVersion);
            insertIdentity.Parameters.AddWithValue(
                "$encryptionVersion",
                ProtectedStorageDatabaseService.CurrentEncryptionVersion);
            insertIdentity.Parameters.AddWithValue(
                "$createdAtUtc",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            insertIdentity.ExecuteNonQuery();
        }

        ExternalPayloadCatalogSqlSchema.CreateTables(connection, transaction);
        ArchiveSegmentCatalogSqlSchema.CreateTable(connection, transaction);

        foreach (ExternalPayloadAddress address in snapshot.ExternalAddresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ExternalPayloadAddressIndex (Sha256, RelativePath, SizeBytes)
                VALUES ($sha256, $relativePath, $sizeBytes);
                """;
            insert.Parameters.AddWithValue("$sha256", address.Sha256);
            insert.Parameters.AddWithValue("$relativePath", address.RelativePath);
            insert.Parameters.AddWithValue("$sizeBytes", address.SizeBytes);
            insert.ExecuteNonQuery();
        }

        foreach (ArchiveSegmentDescriptor descriptor in snapshot.Archives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ArchiveSegmentIndex (
                    DatabaseId, FileName, CoverageStartDate, CoverageEndDate, IsSealed)
                VALUES (
                    $databaseId, $fileName, $coverageStart, $coverageEnd, $isSealed);
                """;
            insert.Parameters.AddWithValue("$databaseId", descriptor.DatabaseId.ToString("D"));
            insert.Parameters.AddWithValue("$fileName", descriptor.FileName);
            insert.Parameters.AddWithValue(
                "$coverageStart",
                descriptor.Coverage.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue(
                "$coverageEnd",
                descriptor.Coverage.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$isSealed", descriptor.IsSealed ? 1 : 0);
            insert.ExecuteNonQuery();
        }

        using (SqliteCommand userVersion = connection.CreateCommand())
        {
            userVersion.Transaction = transaction;
            userVersion.CommandText =
                $"PRAGMA user_version = {ProtectedStorageDatabaseService.CatalogSchemaVersion};";
            userVersion.ExecuteNonQuery();
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void ValidateStagingCatalog(
        string stagingPath,
        Guid storageId,
        ReadOnlyMemory<byte> masterKey,
        RecoverySnapshot expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = _connectionFactory.Open(
            stagingPath,
            masterKey,
            SqliteOpenMode.ReadOnly);
        ProbeAndQuickCheck(connection, "Recovered Catalog staging database");
        ValidateDatabaseIdentityColumns(connection, "Recovered Catalog staging database");
        SourceIdentity identity = ReadNonArchiveIdentity(
            connection,
            storageId,
            DatabaseRole.StorageCatalog,
            [ProtectedStorageDatabaseService.CatalogSchemaVersion]);
        ValidateUserVersion(
            connection,
            ProtectedStorageDatabaseService.CatalogSchemaVersion,
            "Recovered Catalog staging database");
        ExternalPayloadCatalogSqlSchema.ValidateTables(connection);
        ArchiveSegmentCatalogSqlSchema.ValidateTable(connection);

        ExternalPayloadAddress[] actualExternal = ReadCatalogExternalAddresses(connection);
        ArchiveSegmentDescriptor[] actualArchives = ReadCatalogArchives(connection);
        if (!expected.ExternalAddresses.SequenceEqual(actualExternal) ||
            !expected.Archives.SequenceEqual(actualArchives))
        {
            throw new InvalidDataException(
                "Recovered Catalog staging projection does not match source derivation.");
        }

        _ = identity;
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static ExternalPayloadAddress[] ReadCatalogExternalAddresses(
        SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT Sha256, RelativePath, SizeBytes
            FROM ExternalPayloadAddressIndex
            ORDER BY Sha256 COLLATE BINARY;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<ExternalPayloadAddress>();
        while (reader.Read())
        {
            result.Add(new ExternalPayloadAddress(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2)));
        }
        return result.ToArray();
    }

    private static ArchiveSegmentDescriptor[] ReadCatalogArchives(
        SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DatabaseId, FileName, CoverageStartDate, CoverageEndDate, IsSealed
            FROM ArchiveSegmentIndex
            ORDER BY CoverageStartDate, CoverageEndDate, FileName COLLATE BINARY;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<ArchiveSegmentDescriptor>();
        while (reader.Read())
        {
            if (!Guid.TryParse(reader.GetString(0), out Guid databaseId) ||
                databaseId == Guid.Empty ||
                !ArchiveFileName.TryParse(reader.GetString(1), out ArchiveFileName fileName) ||
                !string.Equals(reader.GetString(1), fileName.FileName, StringComparison.Ordinal) ||
                !DateOnly.TryParseExact(
                    reader.GetString(2),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly start) ||
                !DateOnly.TryParseExact(
                    reader.GetString(3),
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly end) ||
                end < start ||
                reader.GetInt32(4) is not (0 or 1))
            {
                throw new InvalidDataException(
                    "Recovered Catalog contains invalid ArchiveSegmentIndex data.");
            }
            result.Add(new ArchiveSegmentDescriptor(
                databaseId,
                fileName.FileName,
                new JournalDateRange(start, end),
                reader.GetInt32(4) == 1));
        }
        return result.ToArray();
    }

    private static void ProbeAndQuickCheck(
        SqliteConnection connection,
        string databaseName)
    {
        using (SqliteCommand probe = connection.CreateCommand())
        {
            probe.CommandText = "SELECT count(*) FROM sqlite_master;";
            _ = probe.ExecuteScalar();
        }
        using SqliteCommand quickCheck = connection.CreateCommand();
        quickCheck.CommandText = "PRAGMA quick_check;";
        if (quickCheck.ExecuteScalar() is not string result ||
            !string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{databaseName} SQLite quick_check failed.");
        }
    }

    private static void EnsureSingleIdentityRow(SqliteConnection connection)
    {
        using SqliteCommand count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM DatabaseIdentity;";
        if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidDataException(
                "DatabaseIdentity must contain exactly one row during Catalog recovery.");
        }
    }

    private static void ValidateDatabaseIdentityColumns(
        SqliteConnection connection,
        string databaseName)
    {
        ExpectedIdentityColumn[] expected =
        [
            new("SingletonId", "INTEGER", true, 1),
            new("StorageId", "TEXT", true, 0),
            new("DatabaseId", "TEXT", true, 0),
            new("DatabaseRole", "TEXT", true, 0),
            new("SchemaVersion", "INTEGER", true, 0),
            new("EncryptionVersion", "INTEGER", true, 0),
            new("CreatedAtUtc", "TEXT", true, 0),
            new("ArchiveBaseNumber", "INTEGER", false, 0),
            new("ArchiveSplitSequence", "INTEGER", false, 0),
            new("CoverageStartDate", "TEXT", false, 0),
            new("CoverageEndDate", "TEXT", false, 0),
        ];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('DatabaseIdentity');";
        using SqliteDataReader reader = command.ExecuteReader();
        int index = 0;
        while (reader.Read())
        {
            if (index >= expected.Length)
            {
                throw new InvalidDataException(
                    $"{databaseName} DatabaseIdentity contains unexpected columns.");
            }
            ExpectedIdentityColumn column = expected[index++];
            if (!string.Equals(reader.GetString(1), column.Name, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(2), column.Type, StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt32(3) != (column.NotNull ? 1 : 0) ||
                reader.GetInt32(5) != column.PrimaryKeyOrder)
            {
                throw new InvalidDataException(
                    $"{databaseName} DatabaseIdentity column contract is invalid.");
            }
        }
        if (index != expected.Length)
        {
            throw new InvalidDataException(
                $"{databaseName} DatabaseIdentity is missing required columns.");
        }
    }

    private static void ValidateUserVersion(
        SqliteConnection connection,
        int schemaVersion,
        string databaseName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
        {
            throw new InvalidDataException(
                $"{databaseName} user_version does not match DatabaseIdentity.");
        }
    }

    private static void ValidateForeignKeys(
        SqliteConnection connection,
        string databaseName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using SqliteDataReader reader = command.ExecuteReader();
        if (reader.Read())
        {
            throw new InvalidDataException(
                $"{databaseName} contains invalid foreign-key references.");
        }
    }

    private static string[] EnumerateArchiveNames(
        string archiveDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(archiveDirectory))
        {
            return [];
        }
        var result = new List<string>();
        foreach (string path in Directory.EnumerateFiles(
                     archiveDirectory,
                     "archive_*.db",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(path);
            if (!ArchiveFileName.TryParse(name, out ArchiveFileName parsed) ||
                !string.Equals(name, parsed.FileName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Archive file '{name}' does not use the canonical Clipensk file name.");
            }
            result.Add(name);
        }
        return result.OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    private static bool SnapshotsEqual(
        RecoverySnapshot left,
        RecoverySnapshot right) =>
        left.CurrentIdentity == right.CurrentIdentity &&
        left.ExternalAddresses.SequenceEqual(right.ExternalAddresses) &&
        left.Archives.SequenceEqual(right.Archives);

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }

    private sealed record RecoverySnapshot(
        SourceIdentity CurrentIdentity,
        ExternalPayloadAddress[] ExternalAddresses,
        ArchiveSegmentDescriptor[] Archives);

    private sealed record SourceIdentity(
        Guid DatabaseId,
        int SchemaVersion,
        DateTimeOffset CreatedAtUtc);

    private sealed record CurrentSource(
        SourceIdentity Identity,
        HashSet<DateOnly> CalendarDates);

    private sealed record ArchiveSource(ArchiveSegmentDescriptor Descriptor);

    private sealed record ExpectedIdentityColumn(
        string Name,
        string Type,
        bool NotNull,
        int PrimaryKeyOrder);

    private sealed class CatalogRecoveryStateException : Exception
    {
        public CatalogRecoveryStateException(string message) : base(message)
        {
        }
    }

    private sealed class ExternalProjectionAccumulator
    {
        private readonly string _filesRoot;
        private readonly string _filesRootPrefix;
        private readonly Dictionary<string, ExternalPayloadAddress> _bySha =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _shaByPath =
            new(StringComparer.OrdinalIgnoreCase);

        public ExternalProjectionAccumulator(string filesRoot)
        {
            _filesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(filesRoot));
            _filesRootPrefix = _filesRoot + Path.DirectorySeparatorChar;
        }

        public IEnumerable<ExternalPayloadAddress> Addresses => _bySha.Values;

        public void Add(ExternalPayloadAddress address)
        {
            ValidateAddress(address);
            if (_bySha.TryGetValue(address.Sha256, out ExternalPayloadAddress? existing))
            {
                if (existing != address)
                {
                    throw new InvalidDataException(
                        "The same external payload SHA-256 maps to conflicting persisted metadata.");
                }
                return;
            }
            if (_shaByPath.TryGetValue(address.RelativePath, out string? existingSha) &&
                !string.Equals(existingSha, address.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "One external payload relative path maps to multiple SHA-256 values.");
            }
            _bySha.Add(address.Sha256, address);
            _shaByPath.Add(address.RelativePath, address.Sha256);
        }

        private void ValidateAddress(ExternalPayloadAddress address)
        {
            if (address.Sha256.Length != 64 ||
                address.Sha256.Any(character =>
                    !((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f'))) ||
                address.SizeBytes < 0 ||
                string.IsNullOrWhiteSpace(address.RelativePath) ||
                Path.IsPathRooted(address.RelativePath))
            {
                throw new InvalidDataException(
                    "History contains invalid external payload address metadata.");
            }

            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(_filesRoot, address.RelativePath));
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidDataException(
                    "History external payload relative path is invalid.",
                    exception);
            }

            if (!candidate.StartsWith(_filesRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "History external payload address escapes the configured Files root.");
            }
        }
    }
}
