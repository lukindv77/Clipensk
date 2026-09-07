$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Replace-Exact {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Old,
        [Parameter(Mandatory = $true)][string]$New,
        [string]$AppliedMarker
    )

    $text = Get-Content -LiteralPath $Path -Raw
    if (-not [string]::IsNullOrEmpty($AppliedMarker) -and $text.Contains($AppliedMarker)) {
        return
    }
    if (-not $text.Contains($Old)) {
        throw "Expected bootstrap anchor was not found in $Path."
    }

    $updated = $text.Replace($Old, $New)
    Set-Content -LiteralPath $Path -Value $updated -Encoding utf8NoBOM -NoNewline
}

function Write-ExactFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrEmpty($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    if (Test-Path -LiteralPath $Path) {
        $existing = Get-Content -LiteralPath $Path -Raw
        if ($existing -eq $Content) {
            return
        }
        throw "Bootstrap target already exists with unexpected content: $Path"
    }

    Set-Content -LiteralPath $Path -Value $Content -Encoding utf8NoBOM -NoNewline
}

$databaseService = 'src/Clipensk.Storage/Databases/ProtectedStorageDatabaseService.cs'

Replace-Exact -Path $databaseService -Old @'
    private const int GlobalCapturePolicyCurrentSchemaVersion = 5;
    private const int LegacyCatalogSchemaVersion = 1;

    public const int CurrentSchemaVersion = 6;
    public const int CatalogSchemaVersion = 2;
'@ -New @'
    private const int GlobalCapturePolicyCurrentSchemaVersion = 5;
    private const int LegacyCatalogSchemaVersion = 1;
    private const int ExternalPayloadCatalogSchemaVersion = 2;

    public const int CurrentSchemaVersion = 6;
    public const int CatalogSchemaVersion = 3;
'@ -AppliedMarker 'private const int ExternalPayloadCatalogSchemaVersion = 2;'

Replace-Exact -Path $databaseService -Old @'
                    cancellationToken,
                    LegacyCatalogSchemaVersion,
                    CatalogSchemaVersion);
'@ -New @'
                    cancellationToken,
                    LegacyCatalogSchemaVersion,
                    ExternalPayloadCatalogSchemaVersion,
                    CatalogSchemaVersion);
'@ -AppliedMarker 'LegacyCatalogSchemaVersion,`n                    ExternalPayloadCatalogSchemaVersion,'

Replace-Exact -Path $databaseService -Old @'
                if (catalogSchemaVersion == LegacyCatalogSchemaVersion)
                {
                    MigrateCatalogFromV1ToV2(
                        catalogDatabasePath,
                        storageId,
                        masterKey,
                        cancellationToken);
                }

                ValidateDatabase(
'@ -New @'
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
'@ -AppliedMarker 'MigrateCatalogFromV2ToV3('

Replace-Exact -Path $databaseService -Old @'
        else if (role == DatabaseRole.StorageCatalog)
        {
            ExternalPayloadCatalogSqlSchema.CreateTables(connection, transaction);
        }
'@ -New @'
        else if (role == DatabaseRole.StorageCatalog)
        {
            ExternalPayloadCatalogSqlSchema.CreateTables(connection, transaction);
            ArchiveSegmentCatalogSqlSchema.CreateTable(connection, transaction);
        }
'@ -AppliedMarker 'ArchiveSegmentCatalogSqlSchema.CreateTable(connection, transaction);'

Replace-Exact -Path $databaseService -Old @'
        UpdateCatalogSchemaVersion(
            connection,
            transaction,
            expectedStorageId,
            LegacyCatalogSchemaVersion,
            CatalogSchemaVersion);
        SetUserVersion(connection, transaction, CatalogSchemaVersion);
'@ -New @'
        UpdateCatalogSchemaVersion(
            connection,
            transaction,
            expectedStorageId,
            LegacyCatalogSchemaVersion,
            ExternalPayloadCatalogSchemaVersion);
        SetUserVersion(connection, transaction, ExternalPayloadCatalogSchemaVersion);
'@ -AppliedMarker 'LegacyCatalogSchemaVersion,`n            ExternalPayloadCatalogSchemaVersion);'

Replace-Exact -Path $databaseService -Old @'
    private static void UpdateCurrentSchemaVersion(
'@ -New @'
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
'@ -AppliedMarker 'private void MigrateCatalogFromV2ToV3('

Replace-Exact -Path $databaseService -Old @'
        if (expectedRole == DatabaseRole.StorageCatalog &&
            schemaVersion >= CatalogSchemaVersion)
        {
            ExternalPayloadCatalogSqlSchema.ValidateTables(connection);
        }
'@ -New @'
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
'@ -AppliedMarker 'ArchiveSegmentCatalogSqlSchema.ValidateTable(connection);'

$schemaContent = @'
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Databases;

internal static class ArchiveSegmentCatalogSqlSchema
{
    public const int RequiredCatalogSchemaVersion = 3;

    public static void CreateTable(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE ArchiveSegmentIndex (
                DatabaseId TEXT NOT NULL PRIMARY KEY CHECK (length(DatabaseId) = 36),
                FileName TEXT NOT NULL CHECK (length(FileName) > 0),
                CoverageStartDate TEXT NOT NULL CHECK (length(CoverageStartDate) = 10),
                CoverageEndDate TEXT NOT NULL
                    CHECK (length(CoverageEndDate) = 10 AND CoverageEndDate >= CoverageStartDate),
                IsSealed INTEGER NOT NULL CHECK (IsSealed IN (0, 1))
            );

            CREATE UNIQUE INDEX UX_ArchiveSegmentIndex_FileName
                ON ArchiveSegmentIndex(FileName);

            CREATE INDEX IX_ArchiveSegmentIndex_Coverage
                ON ArchiveSegmentIndex(CoverageStartDate, CoverageEndDate);
            """;
        command.ExecuteNonQuery();
    }

    public static void ValidateTable(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using (SqliteCommand tableInfo = connection.CreateCommand())
        {
            tableInfo.CommandText = "PRAGMA table_info('ArchiveSegmentIndex');";
            using SqliteDataReader reader = tableInfo.ExecuteReader();

            ExpectedColumn[] expected =
            [
                new("DatabaseId", "TEXT", true, 1),
                new("FileName", "TEXT", true, 0),
                new("CoverageStartDate", "TEXT", true, 0),
                new("CoverageEndDate", "TEXT", true, 0),
                new("IsSealed", "INTEGER", true, 0),
            ];

            int index = 0;
            while (reader.Read())
            {
                if (index >= expected.Length)
                {
                    throw new InvalidDataException(
                        "ArchiveSegmentIndex contains unexpected columns.");
                }

                ExpectedColumn column = expected[index++];
                if (!string.Equals(reader.GetString(1), column.Name, StringComparison.Ordinal) ||
                    !string.Equals(reader.GetString(2), column.Type, StringComparison.OrdinalIgnoreCase) ||
                    reader.GetInt32(3) != (column.NotNull ? 1 : 0) ||
                    reader.GetInt32(5) != column.PrimaryKeyOrder)
                {
                    throw new InvalidDataException(
                        "ArchiveSegmentIndex column contract is invalid.");
                }
            }

            if (index != expected.Length)
            {
                throw new InvalidDataException(
                    "ArchiveSegmentIndex is missing required columns.");
            }
        }

        ValidateIndex(
            connection,
            "UX_ArchiveSegmentIndex_FileName",
            unique: true,
            ["FileName"]);
        ValidateIndex(
            connection,
            "IX_ArchiveSegmentIndex_Coverage",
            unique: false,
            ["CoverageStartDate", "CoverageEndDate"]);
    }

    private static void ValidateIndex(
        SqliteConnection connection,
        string indexName,
        bool unique,
        IReadOnlyList<string> expectedColumns)
    {
        bool found = false;
        using (SqliteCommand indexList = connection.CreateCommand())
        {
            indexList.CommandText = "PRAGMA index_list('ArchiveSegmentIndex');";
            using SqliteDataReader reader = indexList.ExecuteReader();
            while (reader.Read())
            {
                if (!string.Equals(reader.GetString(1), indexName, StringComparison.Ordinal))
                {
                    continue;
                }

                found = true;
                if ((reader.GetInt32(2) == 1) != unique)
                {
                    throw new InvalidDataException(
                        $"Archive segment index '{indexName}' uniqueness contract is invalid.");
                }
            }
        }

        if (!found)
        {
            throw new InvalidDataException(
                $"Archive segment index '{indexName}' is missing.");
        }

        using SqliteCommand indexInfo = connection.CreateCommand();
        indexInfo.CommandText = $"PRAGMA index_info('{indexName}');";
        using SqliteDataReader indexReader = indexInfo.ExecuteReader();
        int columnIndex = 0;
        while (indexReader.Read())
        {
            if (columnIndex >= expectedColumns.Count ||
                !string.Equals(
                    indexReader.GetString(2),
                    expectedColumns[columnIndex],
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Archive segment index '{indexName}' column contract is invalid.");
            }

            columnIndex++;
        }

        if (columnIndex != expectedColumns.Count)
        {
            throw new InvalidDataException(
                $"Archive segment index '{indexName}' is missing required columns.");
        }
    }

    private sealed record ExpectedColumn(
        string Name,
        string Type,
        bool NotNull,
        int PrimaryKeyOrder);
}
'@
Write-ExactFile -Path 'src/Clipensk.Storage/Databases/ArchiveSegmentCatalogSqlSchema.cs' -Content $schemaContent

$catalogContent = @'
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

    public async Task<IReadOnlyList<ArchiveSegmentDescriptor>> RebuildAsync(
        DateOnly currentCalendarDate,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linkedCancellation =
            CreateLinkedCancellation(cancellationToken);
        CancellationToken token = linkedCancellation.Token;
        token.ThrowIfCancellationRequested();

        string[] archivePaths = await Task.Run(
            () => EnumerateArchivePaths(token),
            CancellationToken.None).ConfigureAwait(false);

        var archiveService = new ProtectedArchiveDatabaseService(_session, _connectionFactory);
        var discovered = new List<ArchiveSegmentDescriptor>(archivePaths.Length);
        var databaseIds = new HashSet<Guid>();

        foreach (string archivePath in archivePaths)
        {
            token.ThrowIfCancellationRequested();
            string fileNameText = Path.GetFileName(archivePath);
            if (!ArchiveFileName.TryParse(fileNameText, out ArchiveFileName archiveFileName) ||
                !string.Equals(fileNameText, archiveFileName.FileName, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Archive file '{fileNameText}' does not use the canonical Clipensk file name.");
            }

            DatabaseIdentity identity = await archiveService
                .ValidateAsync(archiveFileName, token)
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
            () => DeriveAndPersistProjection(discovered, currentCalendarDate, token),
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

    private IReadOnlyList<ArchiveSegmentDescriptor> DeriveAndPersistProjection(
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
        return projected;
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
'@
Write-ExactFile -Path 'src/Clipensk.Storage/Databases/ProtectedArchiveSegmentCatalog.cs' -Content $catalogContent

$repositoryTests = @'
using Clipensk.Core.History;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedArchiveSegmentCatalogTests
{
    [Fact]
    public async Task RebuildAsync_ProjectsValidatedArchivesAndRoundTrips()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);

        DatabaseIdentity later = await archiveService.CreateAsync(
            new ArchiveFileName(2, ArchiveFileName.NoSplit),
            Range(2026, 8, 1, 2026, 8, 31));
        DatabaseIdentity earlier = await archiveService.CreateAsync(
            new ArchiveFileName(1, ArchiveFileName.NoSplit),
            Range(2026, 7, 1, 2026, 7, 31));

        IReadOnlyList<ArchiveSegmentDescriptor> rebuilt = await catalog.RebuildAsync(
            new DateOnly(2026, 9, 7));
        IReadOnlyList<ArchiveSegmentDescriptor> persisted = await catalog.ReadAsync();

        Assert.Equal(2, rebuilt.Count);
        Assert.Equal(rebuilt, persisted);
        Assert.Equal(earlier.DatabaseId, rebuilt[0].DatabaseId);
        Assert.Equal("archive_000001.db", rebuilt[0].FileName);
        Assert.True(rebuilt[0].IsSealed);
        Assert.Equal(later.DatabaseId, rebuilt[1].DatabaseId);
        Assert.Equal("archive_000002.db", rebuilt[1].FileName);
        Assert.True(rebuilt[1].IsSealed);
    }

    [Fact]
    public async Task RebuildAsync_CurrentDuplicateKeepsPastArchiveUnsealedUntilPurge()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            new ArchiveFileName(1, ArchiveFileName.NoSplit),
            Range(2026, 9, 1, 2026, 9, 5));
        InsertCurrentEvent(environment, "11111111-1111-1111-1111-111111111111", "2026-09-03");

        IReadOnlyList<ArchiveSegmentDescriptor> withDuplicate = await catalog.RebuildAsync(
            new DateOnly(2026, 9, 7));
        Assert.False(Assert.Single(withDuplicate).IsSealed);

        environment.Execute(
            "DELETE FROM ClipboardHistoryEvent WHERE EventId = '11111111-1111-1111-1111-111111111111';");

        IReadOnlyList<ArchiveSegmentDescriptor> afterPurge = await catalog.RebuildAsync(
            new DateOnly(2026, 9, 7));
        Assert.True(Assert.Single(afterPurge).IsSealed);
    }

    [Fact]
    public async Task RebuildAsync_CoverageEndingTodayIsNotSealed()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            new ArchiveFileName(1, ArchiveFileName.NoSplit),
            Range(2026, 9, 1, 2026, 9, 7));

        IReadOnlyList<ArchiveSegmentDescriptor> result = await catalog.RebuildAsync(
            new DateOnly(2026, 9, 7));

        Assert.False(Assert.Single(result).IsSealed);
    }

    [Fact]
    public async Task RebuildAsync_OverlapFailsBeforeReplacingExistingProjection()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var archiveService = new ProtectedArchiveDatabaseService(environment.Session, environment.Factory);
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        await archiveService.CreateAsync(
            new ArchiveFileName(1, ArchiveFileName.NoSplit),
            Range(2026, 9, 1, 2026, 9, 5));
        await catalog.RebuildAsync(new DateOnly(2026, 9, 10));

        await archiveService.CreateAsync(
            new ArchiveFileName(2, ArchiveFileName.NoSplit),
            Range(2026, 9, 5, 2026, 9, 9));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.RebuildAsync(new DateOnly(2026, 9, 10)));

        ArchiveSegmentDescriptor persisted = Assert.Single(await catalog.ReadAsync());
        Assert.Equal("archive_000001.db", persisted.FileName);
    }

    [Fact]
    public async Task RebuildAsync_NonCanonicalArchiveFileFailsBeforeCatalogMutation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        string invalidPath = Path.Combine(environment.Root, "Archive", "archive_bad.db");
        await File.WriteAllBytesAsync(invalidPath, [0x01, 0x02]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.RebuildAsync(new DateOnly(2026, 9, 7)));

        Assert.Empty(await catalog.ReadAsync());
    }

    [Fact]
    public async Task RebuildAsync_CallerCancellationBeforeWorkDoesNotMutateCatalog()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var catalog = new ProtectedArchiveSegmentCatalog(environment.Session, environment.Factory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            catalog.RebuildAsync(new DateOnly(2026, 9, 7), cancellation.Token));

        Assert.Empty(await catalog.ReadAsync());
    }

    private static JournalDateRange Range(
        int sy, int sm, int sd,
        int ey, int em, int ed) =>
        new(new DateOnly(sy, sm, sd), new DateOnly(ey, em, ed));

    private static void InsertCurrentEvent(
        GlobalPolicyTestEnvironment environment,
        string eventId,
        string calendarDate)
    {
        environment.Execute($"""
            INSERT INTO ClipboardHistoryEvent (
                EventId,
                EventUtc,
                LocalOffsetMinutes,
                WindowsTimeZoneId,
                CalendarDate,
                SourceApplicationId,
                SourceProcessId,
                SourceExecutablePath,
                SourceApplicationUserModelId)
            VALUES (
                '{eventId}',
                '{calendarDate}T12:00:00.0000000+00:00',
                0,
                'UTC',
                '{calendarDate}',
                NULL, NULL, NULL, NULL);
            """);
    }
}
'@
Write-ExactFile -Path 'tests/Clipensk.Storage.Tests/ProtectedArchiveSegmentCatalogTests.cs' -Content $repositoryTests

$migrationTests = @'
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedStorageCatalogSchemaV3MigrationTests
{
    [Fact]
    public async Task Validate_MigratesCatalogV2ToV3AndPreservesExternalPayloadIndex()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute("""
            INSERT INTO ExternalPayloadAddressIndex (Sha256, RelativePath, SizeBytes)
            VALUES (
                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                '2026-09-01/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.png',
                42);
            """, catalog: true);
        DowngradeCatalogToV2(environment);

        ProtectedStorageDatabaseResult result = await environment.ValidateAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(3, environment.Scalar(
            "SELECT SchemaVersion FROM DatabaseIdentity WHERE SingletonId = 1;",
            catalog: true));
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ArchiveSegmentIndex';",
            catalog: true));
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM ExternalPayloadAddressIndex;",
            catalog: true));
    }

    [Fact]
    public async Task Validate_InvalidCurrentDoesNotMigrateCatalogV2()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DowngradeCatalogToV2(environment);
        environment.Execute($"""
            UPDATE DatabaseIdentity
            SET StorageId = '{Guid.NewGuid():D}'
            WHERE SingletonId = 1;
            """);

        ProtectedStorageDatabaseResult result = await environment.ValidateAsync();

        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, result.Status);
        Assert.Equal(2, environment.Scalar(
            "SELECT SchemaVersion FROM DatabaseIdentity WHERE SingletonId = 1;",
            catalog: true));
        Assert.Equal(0, environment.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ArchiveSegmentIndex';",
            catalog: true));
    }

    [Fact]
    public async Task Validate_MalformedCatalogV2FailsBeforeV3Mutation()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DowngradeCatalogToV2(environment);
        environment.Execute(
            "DROP INDEX UX_ExternalPayloadAddressIndex_RelativePath;",
            catalog: true);

        ProtectedStorageDatabaseResult result = await environment.ValidateAsync();

        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, result.Status);
        Assert.Equal(2, environment.Scalar(
            "SELECT SchemaVersion FROM DatabaseIdentity WHERE SingletonId = 1;",
            catalog: true));
        Assert.Equal(0, environment.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ArchiveSegmentIndex';",
            catalog: true));
    }

    [Fact]
    public async Task Validate_CancellationInsideV3MigrationRollsBackAndRetrySucceeds()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        DowngradeCatalogToV2(environment);
        using var cancellation = new CancellationTokenSource();
        int catalogOpenCount = 0;
        string expectedCatalogPath = Path.GetFullPath(environment.CatalogPath);
        environment.Factory.OnOpen = (connection, _) =>
        {
            if (string.Equals(
                    Path.GetFullPath(connection.DataSource),
                    expectedCatalogPath,
                    StringComparison.OrdinalIgnoreCase) &&
                ++catalogOpenCount == 2)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            environment.ValidateAsync(cancellation.Token));

        environment.Factory.OnOpen = null;
        Assert.Equal(2, environment.Scalar(
            "SELECT SchemaVersion FROM DatabaseIdentity WHERE SingletonId = 1;",
            catalog: true));
        Assert.Equal(0, environment.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ArchiveSegmentIndex';",
            catalog: true));

        ProtectedStorageDatabaseResult retry = await environment.ValidateAsync();
        Assert.True(retry.IsSuccess);
        Assert.Equal(3, environment.Scalar(
            "SELECT SchemaVersion FROM DatabaseIdentity WHERE SingletonId = 1;",
            catalog: true));
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ArchiveSegmentIndex';",
            catalog: true));
    }

    private static void DowngradeCatalogToV2(GlobalPolicyTestEnvironment environment)
    {
        environment.Execute("""
            DROP TABLE ArchiveSegmentIndex;
            UPDATE DatabaseIdentity SET SchemaVersion = 2 WHERE SingletonId = 1;
            PRAGMA user_version = 2;
            """, catalog: true);
    }
}
'@
Write-ExactFile -Path 'tests/Clipensk.Storage.Tests/ProtectedStorageCatalogSchemaV3MigrationTests.cs' -Content $migrationTests

$legacyMigrationTests = 'tests/Clipensk.Storage.Tests/ProtectedStorageCatalogSchemaV2MigrationTests.cs'
Replace-Exact -Path $legacyMigrationTests -Old 'Validate_MigratesCatalogV1ToV2AndPreservesCurrentHistory' -New 'Validate_MigratesCatalogV1ToLatestAndPreservesCurrentHistory' -AppliedMarker 'Validate_MigratesCatalogV1ToLatestAndPreservesCurrentHistory'
Replace-Exact -Path $legacyMigrationTests -Old 'Assert.Equal(2, environment.ReadSchemaVersion(environment.CatalogPath));' -New 'Assert.Equal(3, environment.ReadSchemaVersion(environment.CatalogPath));' -AppliedMarker 'Assert.Equal(3, environment.ReadSchemaVersion(environment.CatalogPath));'
Replace-Exact -Path $legacyMigrationTests -Old @'
        Assert.True(environment.HasTable(environment.CatalogPath, "ExternalPayloadAddressIndex"));
        Assert.Equal(1, environment.CountRows(environment.CurrentPath, "ClipboardHistoryEvent"));
'@ -New @'
        Assert.True(environment.HasTable(environment.CatalogPath, "ExternalPayloadAddressIndex"));
        Assert.True(environment.HasTable(environment.CatalogPath, "ArchiveSegmentIndex"));
        Assert.Equal(1, environment.CountRows(environment.CurrentPath, "ClipboardHistoryEvent"));
'@ -AppliedMarker 'Assert.True(environment.HasTable(environment.CatalogPath, "ArchiveSegmentIndex"));'
Replace-Exact -Path $legacyMigrationTests -Old @'
            Assert.Equal(
                2,
                ProtectedStorageCatalogSchemaV2MigrationTests.ReadSchemaVersion(
'@ -New @'
            Assert.Equal(
                3,
                ProtectedStorageCatalogSchemaV2MigrationTests.ReadSchemaVersion(
'@ -AppliedMarker 'Assert.Equal(`n                3,`n                ProtectedStorageCatalogSchemaV2MigrationTests.ReadSchemaVersion('
Replace-Exact -Path $legacyMigrationTests -Old @'
            using SqliteTransaction transaction = connection.BeginTransaction();

            using (SqliteCommand drop = connection.CreateCommand())
            {
                drop.Transaction = transaction;
                drop.CommandText = "DROP TABLE ExternalPayloadAddressIndex;";
'@ -New @'
            using SqliteTransaction transaction = connection.BeginTransaction();

            using (SqliteCommand dropArchiveSegments = connection.CreateCommand())
            {
                dropArchiveSegments.Transaction = transaction;
                dropArchiveSegments.CommandText = "DROP TABLE ArchiveSegmentIndex;";
                dropArchiveSegments.ExecuteNonQuery();
            }

            using (SqliteCommand drop = connection.CreateCommand())
            {
                drop.Transaction = transaction;
                drop.CommandText = "DROP TABLE ExternalPayloadAddressIndex;";
'@ -AppliedMarker 'dropArchiveSegments.CommandText = "DROP TABLE ArchiveSegmentIndex;";'

Write-Host 'Catalog v3 archive segment implementation applied.'
