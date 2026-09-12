using System.Globalization;
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.ExternalFiles;
using Clipensk.Storage.History;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

public sealed class ProtectedArchiveSegmentCatalog
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;
    private readonly string _catalogDatabasePath;
    private readonly string _archiveDirectoryPath;

    public ProtectedArchiveSegmentCatalog(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();

        string root = Path.GetFullPath(session.DataRootPath);
        _currentDatabasePath = Path.Combine(root, "Current", "current.db");
        _catalogDatabasePath = Path.Combine(root, "Current", "storage-catalog.db");
        _archiveDirectoryPath = Path.Combine(root, "Archive");
    }

    public async Task<IReadOnlyList<ArchiveSegmentDescriptor>> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linkedCancellation =
            CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linkedCancellation.Token;

        return await Task.Run(
            () => ReadCatalogCore(token),
            CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ArchiveSegmentDescriptor>> ValidateConsistencyAsync(
        DateOnly currentCalendarDate,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linkedCancellation =
            CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        IReadOnlyList<ArchiveSegmentDescriptor> projected = await DiscoverProjectionAsync(
            currentCalendarDate,
            token).ConfigureAwait(false);
        IReadOnlyList<ArchiveSegmentDescriptor> persisted = await Task.Run(
            () => ReadCatalogCore(token),
            CancellationToken.None).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();
        if (!projected.SequenceEqual(persisted))
        {
            throw new InvalidDataException(
                "Storage catalog archive projection does not match validated physical archives.");
        }

        return projected;
    }

    public async Task<IReadOnlyList<ArchiveSegmentDescriptor>> RebuildAsync(
        DateOnly currentCalendarDate,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linkedCancellation =
            CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        IReadOnlyList<ArchiveSegmentDescriptor> projected = await DiscoverProjectionAsync(
            currentCalendarDate,
            token).ConfigureAwait(false);

        await Task.Run(
            () => PersistProjection(projected, token),
            CancellationToken.None).ConfigureAwait(false);
        return projected;
    }

    private async Task<IReadOnlyList<ArchiveSegmentDescriptor>> DiscoverProjectionAsync(
        DateOnly currentCalendarDate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] archivePaths = await Task.Run(
            () => EnumerateArchivePaths(cancellationToken),
            CancellationToken.None).ConfigureAwait(false);

        var archiveService = new ProtectedArchiveDatabaseService(_session, _connectionFactory);
        var discovered = new List<ArchiveSegmentDescriptor>(archivePaths.Length);
        var databaseIds = new HashSet<Guid>();

        foreach (string archivePath in archivePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileNameText = Path.GetFileName(archivePath);
            if (!ArchiveFileName.TryParse(fileNameText, out ArchiveFileName archiveFileName) ||
                !string.Equals(fileNameText, archiveFileName.FileName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Archive file '{fileNameText}' does not use the canonical Clipensk file name.");
            }

            DatabaseIdentity identity = await archiveService
                .ValidateAsync(archiveFileName, cancellationToken)
                .ConfigureAwait(false);

            if (!databaseIds.Add(identity.DatabaseId))
            {
                throw new InvalidDataException(
                    "Multiple archive files expose the same DatabaseId.");
            }

            if (identity.CoverageStartDate is not DateOnly coverageStart ||
                identity.CoverageEndDate is not DateOnly coverageEnd)
            {
                throw new InvalidDataException("Archive coverage is missing.");
            }

            discovered.Add(new ArchiveSegmentDescriptor(
                identity.DatabaseId,
                archiveFileName.FileName,
                new JournalDateRange(coverageStart, coverageEnd),
                IsSealed: false));
        }

        ValidateNonOverlapping(discovered);

        return await Task.Run(
            () => DeriveProjection(discovered, currentCalendarDate, cancellationToken),
            CancellationToken.None).ConfigureAwait(false);
    }

    private IReadOnlyList<ArchiveSegmentDescriptor> ReadCatalogCore(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection connection = OpenValidatedCatalog(
            SqliteOpenMode.ReadOnly,
            cancellationToken);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DatabaseId, FileName, CoverageStartDate, CoverageEndDate, IsSealed
            FROM ArchiveSegmentIndex
            ORDER BY CoverageStartDate, CoverageEndDate, FileName;
            """;

        var descriptors = new List<ArchiveSegmentDescriptor>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            descriptors.Add(ReadDescriptor(reader));
        }

        ValidateNonOverlapping(descriptors);
        cancellationToken.ThrowIfCancellationRequested();
        return descriptors;
    }

    private IReadOnlyList<ArchiveSegmentDescriptor> DeriveProjection(
        IReadOnlyList<ArchiveSegmentDescriptor> discovered,
        DateOnly currentCalendarDate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using SqliteConnection currentConnection = OpenValidatedCurrent(cancellationToken);
        var projected = new List<ArchiveSegmentDescriptor>(discovered.Count);
        foreach (ArchiveSegmentDescriptor descriptor in discovered
                     .OrderBy(item => item.Coverage.StartDate)
                     .ThenBy(item => item.Coverage.EndDate)
                     .ThenBy(item => item.FileName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasPendingCurrentRows = HasCurrentRows(
                currentConnection,
                descriptor.Coverage);
            bool isSealed =
                descriptor.Coverage.EndDate < currentCalendarDate &&
                !hasPendingCurrentRows;

            projected.Add(descriptor with { IsSealed = isSealed });
        }

        cancellationToken.ThrowIfCancellationRequested();
        return projected;
    }

    private void PersistProjection(
        IReadOnlyList<ArchiveSegmentDescriptor> projected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteConnection catalogConnection = OpenValidatedCatalog(
            SqliteOpenMode.ReadWrite,
            cancellationToken);
        using SqliteTransaction transaction = catalogConnection.BeginTransaction();

        using (SqliteCommand delete = catalogConnection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM ArchiveSegmentIndex;";
            delete.ExecuteNonQuery();
        }

        foreach (ArchiveSegmentDescriptor descriptor in projected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SqliteCommand insert = catalogConnection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ArchiveSegmentIndex (
                    DatabaseId,
                    FileName,
                    CoverageStartDate,
                    CoverageEndDate,
                    IsSealed)
                VALUES (
                    $databaseId,
                    $fileName,
                    $coverageStartDate,
                    $coverageEndDate,
                    $isSealed);
                """;
            insert.Parameters.AddWithValue("$databaseId", descriptor.DatabaseId.ToString("D"));
            insert.Parameters.AddWithValue("$fileName", descriptor.FileName);
            insert.Parameters.AddWithValue(
                "$coverageStartDate",
                FormatDate(descriptor.Coverage.StartDate));
            insert.Parameters.AddWithValue(
                "$coverageEndDate",
                FormatDate(descriptor.Coverage.EndDate));
            insert.Parameters.AddWithValue("$isSealed", descriptor.IsSealed ? 1 : 0);
            insert.ExecuteNonQuery();
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private string[] EnumerateArchivePaths(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_archiveDirectoryPath))
        {
            return [];
        }

        var paths = new List<string>();
        foreach (string path in Directory.EnumerateFiles(
                     _archiveDirectoryPath,
                     "archive_*.db",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            paths.Add(path);
        }

        return paths
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
    }

    private SqliteConnection OpenValidatedCurrent(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureActiveSession();

        SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadOnly);
        try
        {
            ValidateDatabaseIdentity(
                connection,
                DatabaseRole.Current,
                ClipboardHistorySqlSchema.RequiredCurrentSchemaVersion);
            ClipboardHistorySqlSchema.ValidateTables(connection);
            cancellationToken.ThrowIfCancellationRequested();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private SqliteConnection OpenValidatedCatalog(
        SqliteOpenMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureActiveSession();

        SqliteConnection connection = _connectionFactory.Open(
            _catalogDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            mode);
        try
        {
            ValidateDatabaseIdentity(
                connection,
                DatabaseRole.StorageCatalog,
                ArchiveSegmentCatalogSqlSchema.RequiredCatalogSchemaVersion);
            ExternalPayloadCatalogSqlSchema.ValidateTables(connection);
            ArchiveSegmentCatalogSqlSchema.ValidateTable(connection);
            cancellationToken.ThrowIfCancellationRequested();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void ValidateDatabaseIdentity(
        SqliteConnection connection,
        DatabaseRole expectedRole,
        int minimumSchemaVersion)
    {
        int schemaVersion;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT StorageId, DatabaseRole, SchemaVersion
                FROM DatabaseIdentity
                WHERE SingletonId = 1;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read() ||
                !Guid.TryParse(reader.GetString(0), out Guid storageId) ||
                storageId != _session.StorageId ||
                !string.Equals(
                    reader.GetString(1),
                    expectedRole.ToString(),
                    StringComparison.Ordinal) ||
                reader.GetInt32(2) < minimumSchemaVersion)
            {
                throw new InvalidDataException(
                    $"Database role {expectedRole} does not match the active protected storage.");
            }

            schemaVersion = reader.GetInt32(2);
        }

        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
        {
            throw new InvalidDataException(
                $"Database role {expectedRole} user_version does not match DatabaseIdentity.");
        }
    }

    private void EnsureActiveSession()
    {
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }
    }

    private static bool HasCurrentRows(
        SqliteConnection connection,
        JournalDateRange coverage)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM ClipboardHistoryEvent
                WHERE CalendarDate >= $coverageStartDate
                  AND CalendarDate <= $coverageEndDate
                LIMIT 1
            );
            """;
        command.Parameters.AddWithValue("$coverageStartDate", FormatDate(coverage.StartDate));
        command.Parameters.AddWithValue("$coverageEndDate", FormatDate(coverage.EndDate));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static ArchiveSegmentDescriptor ReadDescriptor(SqliteDataReader reader)
    {
        if (!Guid.TryParse(reader.GetString(0), out Guid databaseId) || databaseId == Guid.Empty)
        {
            throw new InvalidDataException("Archive segment catalog DatabaseId is invalid.");
        }

        string fileName = reader.GetString(1);
        if (!ArchiveFileName.TryParse(fileName, out ArchiveFileName parsedFileName) ||
            !string.Equals(fileName, parsedFileName.FileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Archive segment catalog FileName is invalid.");
        }

        if (!TryParseDate(reader.GetString(2), out DateOnly coverageStart) ||
            !TryParseDate(reader.GetString(3), out DateOnly coverageEnd) ||
            coverageEnd < coverageStart)
        {
            throw new InvalidDataException("Archive segment catalog coverage is invalid.");
        }

        int isSealedValue = reader.GetInt32(4);
        if (isSealedValue is not 0 and not 1)
        {
            throw new InvalidDataException("Archive segment catalog sealing state is invalid.");
        }

        return new ArchiveSegmentDescriptor(
            databaseId,
            parsedFileName.FileName,
            new JournalDateRange(coverageStart, coverageEnd),
            IsSealed: isSealedValue == 1);
    }

    private static void ValidateNonOverlapping(
        IEnumerable<ArchiveSegmentDescriptor> descriptors)
    {
        ArchiveSegmentDescriptor[] ordered = descriptors
            .OrderBy(item => item.Coverage.StartDate)
            .ThenBy(item => item.Coverage.EndDate)
            .ThenBy(item => item.FileName, StringComparer.Ordinal)
            .ToArray();

        for (int index = 1; index < ordered.Length; index++)
        {
            ArchiveSegmentDescriptor previous = ordered[index - 1];
            ArchiveSegmentDescriptor current = ordered[index];
            if (previous.Coverage.EndDate >= current.Coverage.StartDate)
            {
                throw new InvalidDataException(
                    $"Archive coverage overlap detected between '{previous.FileName}' and '{current.FileName}'.");
            }
        }
    }

    private CancellationTokenSource CreateLinkedCancellation(CancellationToken callerToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            callerToken);

    private static string FormatDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
}
