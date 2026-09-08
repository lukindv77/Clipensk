$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Utf8NoBom([string]$Path, [string]$Content) {
    [System.IO.File]::WriteAllText(
        (Join-Path (Get-Location) $Path),
        $Content,
        [System.Text.UTF8Encoding]::new($false))
}

function Replace-Exactly([string]$Path, [string]$Old, [string]$New) {
    $fullPath = Join-Path (Get-Location) $Path
    $text = [System.IO.File]::ReadAllText($fullPath)
    $count = ([regex]::Matches($text, [regex]::Escape($Old))).Count
    if ($count -ne 1) {
        throw "Expected exactly one anchor in $Path, found $count."
    }
    [System.IO.File]::WriteAllText(
        $fullPath,
        $text.Replace($Old, $New),
        [System.Text.UTF8Encoding]::new($false))
}

$databaseServicePath = 'src/Clipensk.Storage/Databases/ProtectedStorageDatabaseService.cs'
$databaseService = [System.IO.File]::ReadAllText((Join-Path (Get-Location) $databaseServicePath))
if ($databaseService.Contains('public const int CurrentSchemaVersion = 6;')) {
    Replace-Exactly $databaseServicePath @'
    private const int GlobalCapturePolicyCurrentSchemaVersion = 5;
    private const int LegacyCatalogSchemaVersion = 1;
'@ @'
    private const int GlobalCapturePolicyCurrentSchemaVersion = 5;
    private const int CustomBinaryConfigurationCurrentSchemaVersion = 6;
    private const int LegacyCatalogSchemaVersion = 1;
'@

    Replace-Exactly $databaseServicePath @'
    public const int CurrentSchemaVersion = 6;
'@ @'
    public const int CurrentSchemaVersion = 7;
'@

    Replace-Exactly $databaseServicePath @'
                    HistoryCurrentSchemaVersion,
                    GlobalCapturePolicyCurrentSchemaVersion,
                    CurrentSchemaVersion);
'@ @'
                    HistoryCurrentSchemaVersion,
                    GlobalCapturePolicyCurrentSchemaVersion,
                    CustomBinaryConfigurationCurrentSchemaVersion,
                    CurrentSchemaVersion);
'@

    Replace-Exactly $databaseServicePath @'
                if (currentSchemaVersion == GlobalCapturePolicyCurrentSchemaVersion)
                {
                    MigrateCurrentFromV5ToV6(
                        currentDatabasePath,
                        storageId,
                        masterKey,
                        cancellationToken);
                    currentSchemaVersion = CurrentSchemaVersion;
                }

                if (catalogSchemaVersion == LegacyCatalogSchemaVersion)
'@ @'
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
                    currentSchemaVersion = CurrentSchemaVersion;
                }

                if (catalogSchemaVersion == LegacyCatalogSchemaVersion)
'@

    Replace-Exactly $databaseServicePath @'
            GlobalCapturePolicySqlSchema.CreateTables(connection, transaction);
            CustomBinaryFormatConfigurationSqlSchema.CreateTable(connection, transaction);
'@ @'
            GlobalCapturePolicySqlSchema.CreateTables(connection, transaction);
            CustomBinaryFormatConfigurationSqlSchema.CreateTable(connection, transaction);
            GlobalCapturePolicyMaintenanceSqlSchema.CreateTable(connection, transaction);
'@

    Replace-Exactly $databaseServicePath @'
        UpdateCurrentSchemaVersion(
            connection, transaction, expectedStorageId,
            GlobalCapturePolicyCurrentSchemaVersion, CurrentSchemaVersion);
        SetUserVersion(connection, transaction, CurrentSchemaVersion);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }
    private void MigrateCatalogFromV1ToV2(
'@ @'
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
        GlobalCapturePolicyMaintenanceSqlSchema.CreateTable(connection, transaction);
        UpdateCurrentSchemaVersion(
            connection, transaction, expectedStorageId,
            CustomBinaryConfigurationCurrentSchemaVersion, CurrentSchemaVersion);
        SetUserVersion(connection, transaction, CurrentSchemaVersion);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void MigrateCatalogFromV1ToV2(
'@

    Replace-Exactly $databaseServicePath @'
        if (expectedRole == DatabaseRole.Current && schemaVersion >= CurrentSchemaVersion)
        {
            CustomBinaryFormatConfigurationSqlSchema.ValidateTable(connection);
        }

        if (expectedRole == DatabaseRole.StorageCatalog &&
'@ @'
        if (expectedRole == DatabaseRole.Current &&
            schemaVersion >= CustomBinaryConfigurationCurrentSchemaVersion)
        {
            CustomBinaryFormatConfigurationSqlSchema.ValidateTable(connection);
        }

        if (expectedRole == DatabaseRole.Current && schemaVersion >= CurrentSchemaVersion)
        {
            GlobalCapturePolicyMaintenanceSqlSchema.ValidateTable(connection);
        }

        if (expectedRole == DatabaseRole.StorageCatalog &&
'@
}

$schemaPath = 'src/Clipensk.Storage/Clipboard/GlobalCapturePolicyMaintenanceSqlSchema.cs'
if (-not (Test-Path $schemaPath)) {
    Write-Utf8NoBom $schemaPath @'
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

internal static class GlobalCapturePolicyMaintenanceSqlSchema
{
    public const int MinimumCurrentSchemaVersion = 7;

    public static void CreateTable(SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE GlobalCapturePolicyMaintenance (
                SingletonId INTEGER NOT NULL PRIMARY KEY CHECK (SingletonId = 1),
                OperationId TEXT NOT NULL CHECK (length(OperationId) > 0),
                Phase TEXT NOT NULL CHECK (Phase IN ('ArchiveCleanup', 'CatalogRebuild', 'TrashCollection')),
                StartedAtUtc TEXT NOT NULL CHECK (length(StartedAtUtc) > 0)
            );
            """;
        command.ExecuteNonQuery();
    }

    public static void ValidateTable(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('GlobalCapturePolicyMaintenance');";
        using SqliteDataReader reader = command.ExecuteReader();

        ExpectedColumn[] expected =
        [
            new("SingletonId", "INTEGER", NotNull: true, PrimaryKeyOrder: 1),
            new("OperationId", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
            new("Phase", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
            new("StartedAtUtc", "TEXT", NotNull: true, PrimaryKeyOrder: 0),
        ];

        int index = 0;
        while (reader.Read())
        {
            if (index >= expected.Length)
            {
                throw new InvalidDataException(
                    "GlobalCapturePolicyMaintenance contains unexpected columns.");
            }

            ExpectedColumn column = expected[index++];
            if (!string.Equals(reader.GetString(1), column.Name, StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(2), column.Type, StringComparison.OrdinalIgnoreCase) ||
                reader.GetInt32(3) != (column.NotNull ? 1 : 0) ||
                reader.GetInt32(5) != column.PrimaryKeyOrder)
            {
                throw new InvalidDataException(
                    "GlobalCapturePolicyMaintenance column contract is invalid.");
            }
        }

        if (index != expected.Length)
        {
            throw new InvalidDataException(
                "GlobalCapturePolicyMaintenance is missing required columns.");
        }
    }

    private sealed record ExpectedColumn(
        string Name,
        string Type,
        bool NotNull,
        int PrimaryKeyOrder);
}
'@
}

$repositoryPath = 'src/Clipensk.Storage/Clipboard/SqliteGlobalCapturePolicyMaintenanceRepository.cs'
if (-not (Test-Path $repositoryPath)) {
    Write-Utf8NoBom $repositoryPath @'
using System.Globalization;
using Clipensk.Core.Storage;
using Clipensk.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace Clipensk.Storage.Clipboard;

public enum GlobalCapturePolicyMaintenancePhase
{
    ArchiveCleanup,
    CatalogRebuild,
    TrashCollection,
}

public sealed record GlobalCapturePolicyMaintenanceState(
    Guid OperationId,
    GlobalCapturePolicyMaintenancePhase Phase,
    DateTimeOffset StartedAtUtc);

public sealed class SqliteGlobalCapturePolicyMaintenanceRepository
{
    private readonly ProtectedStorageSessionLease _session;
    private readonly IKeyedSqliteConnectionFactory _connectionFactory;
    private readonly string _currentDatabasePath;

    public SqliteGlobalCapturePolicyMaintenanceRepository(
        ProtectedStorageSessionLease session,
        IKeyedSqliteConnectionFactory? connectionFactory = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _connectionFactory = connectionFactory ?? new SqlCipherConnectionFactory();
        _currentDatabasePath = Path.Combine(
            Path.GetFullPath(session.DataRootPath),
            "Current",
            "current.db");
    }

    public ValueTask<GlobalCapturePolicyMaintenanceState?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            _session.CancellationToken,
            cancellationToken);
        CancellationToken token = linked.Token;
        token.ThrowIfCancellationRequested();
        if (!_session.IsActive)
        {
            throw new OperationCanceledException(_session.CancellationToken);
        }

        using SqliteConnection connection = _connectionFactory.Open(
            _currentDatabasePath,
            _session.DangerousGetMasterKeyMemory(),
            SqliteOpenMode.ReadOnly);
        EnableForeignKeys(connection);
        ValidateCurrentDatabase(connection);
        GlobalCapturePolicyMaintenanceSqlSchema.ValidateTable(connection);

        using SqliteCommand count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM GlobalCapturePolicyMaintenance;";
        long rowCount = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (rowCount == 0)
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult<GlobalCapturePolicyMaintenanceState?>(null);
        }
        if (rowCount != 1)
        {
            throw new InvalidDataException(
                "GlobalCapturePolicyMaintenance must contain zero or one row.");
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT SingletonId, OperationId, Phase, StartedAtUtc
            FROM GlobalCapturePolicyMaintenance
            WHERE SingletonId = 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != 1)
        {
            throw new InvalidDataException(
                "GlobalCapturePolicyMaintenance singleton row is invalid.");
        }

        string operationText = reader.GetString(1);
        if (!Guid.TryParseExact(operationText, "D", out Guid operationId) ||
            operationId == Guid.Empty ||
            !string.Equals(operationText, operationId.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "GlobalCapturePolicyMaintenance contains an invalid OperationId.");
        }

        string phaseText = reader.GetString(2);
        if (!Enum.TryParse(phaseText, ignoreCase: false, out GlobalCapturePolicyMaintenancePhase phase) ||
            !string.Equals(phaseText, phase.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "GlobalCapturePolicyMaintenance contains an invalid phase.");
        }

        string startedText = reader.GetString(3);
        if (!DateTimeOffset.TryParse(
                startedText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset startedAtUtc) ||
            startedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "GlobalCapturePolicyMaintenance contains an invalid UTC start timestamp.");
        }

        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult<GlobalCapturePolicyMaintenanceState?>(
            new GlobalCapturePolicyMaintenanceState(operationId, phase, startedAtUtc));
    }

    private void ValidateCurrentDatabase(SqliteConnection connection)
    {
        using SqliteCommand identity = connection.CreateCommand();
        identity.CommandText = """
            SELECT StorageId, DatabaseRole, SchemaVersion
            FROM DatabaseIdentity
            WHERE SingletonId = 1;
            """;
        using SqliteDataReader reader = identity.ExecuteReader();
        if (!reader.Read() ||
            !Guid.TryParse(reader.GetString(0), out Guid storageId) ||
            storageId != _session.StorageId ||
            !string.Equals(reader.GetString(1), DatabaseRole.Current.ToString(), StringComparison.Ordinal) ||
            reader.GetInt32(2) < GlobalCapturePolicyMaintenanceSqlSchema.MinimumCurrentSchemaVersion)
        {
            throw new InvalidDataException(
                "Policy maintenance repository requires Current schema v7 or later.");
        }

        int schemaVersion = reader.GetInt32(2);
        using SqliteCommand userVersion = connection.CreateCommand();
        userVersion.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(userVersion.ExecuteScalar(), CultureInfo.InvariantCulture) != schemaVersion)
        {
            throw new InvalidDataException(
                "Current database user_version does not match the policy maintenance schema contract.");
        }
    }

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
    }
}
'@
}

$deliveryPath = 'src/Clipensk.Storage/Clipboard/ProtectedClipboardDeliveryServices.cs'
$delivery = [System.IO.File]::ReadAllText((Join-Path (Get-Location) $deliveryPath))
if (-not $delivery.Contains('SqliteGlobalCapturePolicyMaintenanceRepository')) {
    Replace-Exactly $deliveryPath @'
        EnsureActive(session, token);

        var globalPolicies = new SqliteGlobalClipboardCapturePolicyRepository(session, connectionFactory);
'@ @'
        EnsureActive(session, token);

        var maintenance = new SqliteGlobalCapturePolicyMaintenanceRepository(session, connectionFactory);
        if (await maintenance.ReadAsync(token).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException(
                "Clipboard capture cannot be composed while global policy maintenance is pending.");
        }
        EnsureActive(session, token);

        var globalPolicies = new SqliteGlobalClipboardCapturePolicyRepository(session, connectionFactory);
'@
}

$environmentPath = 'tests/Clipensk.Storage.Tests/GlobalPolicyTestEnvironment.cs'
$environment = [System.IO.File]::ReadAllText((Join-Path (Get-Location) $environmentPath))
if (-not $environment.Contains('DowngradeToV6')) {
    Replace-Exactly $environmentPath @'
    public void DowngradeToV5() => Execute("""
        DROP TABLE CustomBinaryFormatConfiguration;
        UPDATE DatabaseIdentity SET SchemaVersion = 5;
        PRAGMA user_version = 5;
        """);

    public Task<ProtectedStorageDatabaseResult> ValidateAsync(CancellationToken token = default) =>
'@ @'
    public void DowngradeToV5() => Execute("""
        DROP TABLE GlobalCapturePolicyMaintenance;
        DROP TABLE CustomBinaryFormatConfiguration;
        UPDATE DatabaseIdentity SET SchemaVersion = 5;
        PRAGMA user_version = 5;
        """);

    public void DowngradeToV6() => Execute("""
        DROP TABLE GlobalCapturePolicyMaintenance;
        UPDATE DatabaseIdentity SET SchemaVersion = 6;
        PRAGMA user_version = 6;
        """);

    public Task<ProtectedStorageDatabaseResult> ValidateAsync(CancellationToken token = default) =>
'@

    Replace-Exactly $environmentPath @'
    public void DowngradeToV4() => Execute("""
        DROP TABLE CustomBinaryFormatConfiguration;
        DROP TABLE GlobalFormatCapturePolicy;
'@ @'
    public void DowngradeToV4() => Execute("""
        DROP TABLE GlobalCapturePolicyMaintenance;
        DROP TABLE CustomBinaryFormatConfiguration;
        DROP TABLE GlobalFormatCapturePolicy;
'@
}

$deliveryTestsPath = 'tests/Clipensk.Storage.Tests/ProtectedClipboardDeliveryServicesTests.cs'
$deliveryTests = [System.IO.File]::ReadAllText((Join-Path (Get-Location) $deliveryTestsPath))
if (-not $deliveryTests.Contains('PendingGlobalPolicyMaintenance_BlocksComposition')) {
    $deliveryTests = $deliveryTests.Replace(
        'Assert.Equal(new[] { SqliteOpenMode.ReadOnly }, environment.Factory.Modes);',
        'Assert.Equal(new[] { SqliteOpenMode.ReadOnly, SqliteOpenMode.ReadOnly }, environment.Factory.Modes);')
    $anchor = @'
    [Theory]
    [InlineData(ClipboardCapturePolicyRule.Allow)]
'@
    $test = @'
    [Fact]
    public async Task PendingGlobalPolicyMaintenance_BlocksCompositionBeforePolicyRead()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(new ClipboardCapturePolicy(ClipboardCapturePolicyRule.Allow));
        environment.Execute("""
            INSERT INTO GlobalCapturePolicyMaintenance (
                SingletonId, OperationId, Phase, StartedAtUtc)
            VALUES (
                1,
                '11111111-1111-1111-1111-111111111111',
                'ArchiveCleanup',
                '2026-09-08T08:00:00.0000000+00:00');
            """);
        var factory = new RecordingFactory();
        environment.Factory.Modes.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ProtectedClipboardDeliveryServices.TryCreateAsync(
                environment.Session, factory, new NoExtensionRequests(), environment.Factory));

        Assert.Equal(new[] { SqliteOpenMode.ReadOnly }, environment.Factory.Modes);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, factory.ProcessCount);
    }

    [Theory]
    [InlineData(ClipboardCapturePolicyRule.Allow)]
'@
    if (-not $deliveryTests.Contains($anchor)) {
        throw "Delivery test insertion anchor not found."
    }
    $deliveryTests = $deliveryTests.Replace($anchor, $test)
    Write-Utf8NoBom $deliveryTestsPath $deliveryTests
}

$migrationTestsPath = 'tests/Clipensk.Storage.Tests/ProtectedStorageCurrentSchemaV7MigrationTests.cs'
if (-not (Test-Path $migrationTestsPath)) {
    Write-Utf8NoBom $migrationTestsPath @'
using Clipensk.Core.Clipboard;
using Clipensk.Core.Storage;
using Clipensk.Storage.Databases;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class ProtectedStorageCurrentSchemaV7MigrationTests
{
    [Fact]
    public async Task V6Migration_PreservesExistingStateAndCreatesEmptyMaintenanceTable()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        await environment.Repository.InitializeAsync(new ClipboardCapturePolicy(
            ClipboardCapturePolicyRule.Allow,
            new Dictionary<string, ClipboardFormatCapturePolicy>
            {
                ["Text"] = new(ClipboardCapturePolicyRule.Allow, 4096),
            }));
        environment.Execute("""
            INSERT INTO CustomBinaryFormatConfiguration (FormatName, FileExtension)
            VALUES ('Example.Custom', '.payload');
            """);
        environment.DowngradeToV6();

        Assert.True((await environment.ValidateAsync()).IsSuccess);
        Assert.Equal(ProtectedStorageDatabaseService.CurrentSchemaVersion,
            environment.Scalar("PRAGMA user_version;"));
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM CustomBinaryFormatConfiguration WHERE FormatName = 'Example.Custom';"));
        Assert.Equal(0, environment.Scalar(
            "SELECT COUNT(*) FROM GlobalCapturePolicyMaintenance;"));
        ClipboardCapturePolicy? policy = await environment.Repository.ReadAsync();
        Assert.NotNull(policy);
        Assert.Equal(4096, policy.Formats["Text"].MaxBytes);
    }

    [Fact]
    public async Task InvalidCatalog_PreventsV6Mutation()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV6();
        environment.Execute(
            "UPDATE DatabaseIdentity SET StorageId = '00000000-0000-0000-0000-000000000001';",
            catalog: true);

        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity,
            (await environment.ValidateAsync()).Status);
        AssertUnmigrated(environment);
    }

    [Fact]
    public async Task MigrationSqlFailure_RollsBackVersionAndCanRetry()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV6();
        environment.Execute("""
            CREATE TABLE GlobalCapturePolicyMaintenance (Marker TEXT);
            INSERT INTO GlobalCapturePolicyMaintenance VALUES ('preserved');
            """);

        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity,
            (await environment.ValidateAsync()).Status);
        AssertUnmigrated(environment, tableMustBeAbsent: false);
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM GlobalCapturePolicyMaintenance WHERE Marker = 'preserved';"));

        environment.Execute("DROP TABLE GlobalCapturePolicyMaintenance;");
        Assert.True((await environment.ValidateAsync()).IsSuccess);
        Assert.Equal(0, environment.Scalar("SELECT COUNT(*) FROM GlobalCapturePolicyMaintenance;"));
    }

    [Fact]
    public async Task CancellationInsideMigration_RollsBackTableAndVersionThenRetries()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.DowngradeToV6();
        using var cancellation = new CancellationTokenSource();
        environment.Execute("""
            CREATE TRIGGER cancel_v7_migration AFTER UPDATE OF SchemaVersion ON DatabaseIdentity
            BEGIN SELECT cancel_setup(); END;
            """);
        environment.Factory.OnOpen = (connection, _) => connection.CreateFunction("cancel_setup", () =>
        {
            cancellation.Cancel();
            return 0;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            environment.ValidateAsync(cancellation.Token));
        environment.Factory.OnOpen = null;
        AssertUnmigrated(environment);
        environment.Execute("DROP TRIGGER cancel_v7_migration;");
        Assert.True((await environment.ValidateAsync()).IsSuccess);
    }

    [Fact]
    public async Task MalformedV7MaintenanceSchema_IsRejectedFailClosed()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute("""
            DROP TABLE GlobalCapturePolicyMaintenance;
            CREATE TABLE GlobalCapturePolicyMaintenance (
                SingletonId INTEGER PRIMARY KEY,
                OperationId TEXT,
                Phase TEXT,
                StartedAtUtc TEXT);
            """);

        Assert.Equal(
            ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity,
            (await environment.ValidateAsync()).Status);
    }

    private static void AssertUnmigrated(
        GlobalPolicyTestEnvironment environment,
        bool tableMustBeAbsent = true)
    {
        Assert.Equal(6, environment.Scalar("SELECT SchemaVersion FROM DatabaseIdentity;"));
        Assert.Equal(6, environment.Scalar("PRAGMA user_version;"));
        if (tableMustBeAbsent)
        {
            Assert.Equal(0, environment.Scalar(
                "SELECT COUNT(*) FROM sqlite_master WHERE name = 'GlobalCapturePolicyMaintenance';"));
        }
    }
}
'@
}

$repositoryTestsPath = 'tests/Clipensk.Storage.Tests/SqliteGlobalCapturePolicyMaintenanceRepositoryTests.cs'
if (-not (Test-Path $repositoryTestsPath)) {
    Write-Utf8NoBom $repositoryTestsPath @'
using Clipensk.Storage.Clipboard;
using Clipensk.Storage.Sqlite;
using Xunit;

namespace Clipensk.Storage.Tests;

public sealed class SqliteGlobalCapturePolicyMaintenanceRepositoryTests
{
    [Fact]
    public async Task ReadAsync_EmptyTableReturnsNullReadOnly()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        var repository = new SqliteGlobalCapturePolicyMaintenanceRepository(
            environment.Session,
            environment.Factory);
        environment.Factory.Modes.Clear();

        Assert.Null(await repository.ReadAsync());
        Assert.Equal(new[] { SqliteOpenMode.ReadOnly }, environment.Factory.Modes);
    }

    [Fact]
    public async Task ReadAsync_ReturnsExactPersistedState()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute("""
            INSERT INTO GlobalCapturePolicyMaintenance (
                SingletonId, OperationId, Phase, StartedAtUtc)
            VALUES (
                1,
                '11111111-1111-1111-1111-111111111111',
                'CatalogRebuild',
                '2026-09-08T08:00:00.0000000+00:00');
            """);
        var repository = new SqliteGlobalCapturePolicyMaintenanceRepository(
            environment.Session,
            environment.Factory);

        GlobalCapturePolicyMaintenanceState? state = await repository.ReadAsync();

        Assert.NotNull(state);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), state.OperationId);
        Assert.Equal(GlobalCapturePolicyMaintenancePhase.CatalogRebuild, state.Phase);
        Assert.Equal(TimeSpan.Zero, state.StartedAtUtc.Offset);
    }

    [Theory]
    [InlineData("NOT-A-GUID", "ArchiveCleanup", "2026-09-08T08:00:00.0000000+00:00")]
    [InlineData("11111111-1111-1111-1111-111111111111", "Unknown", "2026-09-08T08:00:00.0000000+00:00")]
    [InlineData("11111111-1111-1111-1111-111111111111", "TrashCollection", "2026-09-08T08:00:00.0000000+03:00")]
    public async Task ReadAsync_MalformedPersistedStateFailsClosed(
        string operationId,
        string phase,
        string startedAtUtc)
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute("PRAGMA ignore_check_constraints = ON;");
        environment.Execute($"""
            INSERT INTO GlobalCapturePolicyMaintenance (
                SingletonId, OperationId, Phase, StartedAtUtc)
            VALUES (1, '{operationId}', '{phase}', '{startedAtUtc}');
            """);
        var repository = new SqliteGlobalCapturePolicyMaintenanceRepository(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await repository.ReadAsync());
    }

    [Fact]
    public async Task ReadAsync_LockedSessionCancelsBeforeOpen()
    {
        using var environment = await GlobalPolicyTestEnvironment.CreateAsync();
        Assert.True(environment.Lifecycle.TryBeginLock());
        environment.Factory.Modes.Clear();
        var repository = new SqliteGlobalCapturePolicyMaintenanceRepository(
            environment.Session,
            environment.Factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await repository.ReadAsync());
        Assert.Empty(environment.Factory.Modes);
    }
}
'@
}
