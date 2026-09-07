$ErrorActionPreference = 'Stop'

$patchPath = 'eng/custom-binary-extension-config.patch'
$excluded = @(
    'src/Clipensk.Storage/Databases/ProtectedStorageDatabaseService.cs',
    'tests/Clipensk.Storage.Tests/GlobalPolicyTestEnvironment.cs'
)

$lines = @(Get-Content -LiteralPath $patchPath)
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^@@ -(?<oldStart>\d+)(?:,(?<oldCount>\d+))? \+(?<newStart>\d+)(?:,(?<newCount>\d+))? @@(?<suffix>.*)$') {
        $oldStart = [int]$Matches.oldStart
        $newStart = [int]$Matches.newStart
        $suffix = $Matches.suffix
        $oldCount = 0
        $newCount = 0
        $j = $i + 1
        while ($j -lt $lines.Count -and
               -not $lines[$j].StartsWith('@@ ') -and
               -not $lines[$j].StartsWith('diff --git ')) {
            if ($lines[$j].StartsWith(' ') -or $lines[$j].StartsWith('-')) { $oldCount++ }
            if ($lines[$j].StartsWith(' ') -or $lines[$j].StartsWith('+')) { $newCount++ }
            $j++
        }
        $lines[$i] = "@@ -$oldStart,$oldCount +$newStart,$newCount @@$suffix"
    }
}
Set-Content -LiteralPath $patchPath -Value $lines -Encoding utf8NoBOM

$excludeArgs = @($excluded | ForEach-Object { "--exclude=$_" })
& git apply --check @excludeArgs $patchPath
if ($LASTEXITCODE -ne 0) { throw 'git apply --check failed.' }
& git apply @excludeArgs $patchPath
if ($LASTEXITCODE -ne 0) { throw 'git apply failed.' }

function Replace-Required {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Old,
        [Parameter(Mandatory)][string]$New
    )

    $text = Get-Content -LiteralPath $Path -Raw
    if (-not $text.Contains($Old)) {
        throw "Required context not found in $Path"
    }
    $updated = $text.Replace($Old, $New)
    if ($updated -eq $text) {
        throw "Required replacement made no change in $Path"
    }
    Set-Content -LiteralPath $Path -Value $updated -Encoding utf8NoBOM -NoNewline
}

$database = 'src/Clipensk.Storage/Databases/ProtectedStorageDatabaseService.cs'
$old = @'
    private const int HistoryCurrentSchemaVersion = 4;
    private const int LegacyCatalogSchemaVersion = 1;

    public const int CurrentSchemaVersion = 5;
'@
$new = @'
    private const int HistoryCurrentSchemaVersion = 4;
    private const int GlobalCapturePolicyCurrentSchemaVersion = 5;
    private const int LegacyCatalogSchemaVersion = 1;

    public const int CurrentSchemaVersion = 6;
'@
Replace-Required -Path $database -Old $old -New $new

$old = @'
                    ApplicationPolicyCurrentSchemaVersion,
                    HistoryCurrentSchemaVersion,
                    CurrentSchemaVersion);
'@
$new = @'
                    ApplicationPolicyCurrentSchemaVersion,
                    HistoryCurrentSchemaVersion,
                    GlobalCapturePolicyCurrentSchemaVersion,
                    CurrentSchemaVersion);
'@
Replace-Required -Path $database -Old $old -New $new

$old = @'
                if (currentSchemaVersion == HistoryCurrentSchemaVersion)
                {
                    MigrateCurrentFromV4ToV5(
                        currentDatabasePath,
                        storageId,
                        masterKey,
                        cancellationToken);
                }

                if (catalogSchemaVersion == LegacyCatalogSchemaVersion)
'@
$new = @'
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
                    currentSchemaVersion = CurrentSchemaVersion;
                }

                if (catalogSchemaVersion == LegacyCatalogSchemaVersion)
'@
Replace-Required -Path $database -Old $old -New $new

$old = @'
            ClipboardHistorySqlSchema.CreateTables(connection, transaction);
            GlobalCapturePolicySqlSchema.CreateTables(connection, transaction);
'@
$new = @'
            ClipboardHistorySqlSchema.CreateTables(connection, transaction);
            GlobalCapturePolicySqlSchema.CreateTables(connection, transaction);
            CustomBinaryFormatConfigurationSqlSchema.CreateTable(connection, transaction);
'@
Replace-Required -Path $database -Old $old -New $new

$old = @'
        GlobalCapturePolicySqlSchema.CreateTables(connection, transaction);
        UpdateCurrentSchemaVersion(
            connection, transaction, expectedStorageId,
            HistoryCurrentSchemaVersion, CurrentSchemaVersion);
        SetUserVersion(connection, transaction, CurrentSchemaVersion);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void MigrateCatalogFromV1ToV2(
'@
$new = @'
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
            GlobalCapturePolicyCurrentSchemaVersion, CurrentSchemaVersion);
        SetUserVersion(connection, transaction, CurrentSchemaVersion);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private void MigrateCatalogFromV1ToV2(
'@
Replace-Required -Path $database -Old $old -New $new

$old = @'
        if (expectedRole == DatabaseRole.Current &&
            schemaVersion >= CurrentSchemaVersion)
        {
            GlobalCapturePolicySqlSchema.ValidateTables(connection);
        }

        if (expectedRole == DatabaseRole.StorageCatalog &&
'@
$new = @'
        if (expectedRole == DatabaseRole.Current &&
            schemaVersion >= GlobalCapturePolicyCurrentSchemaVersion)
        {
            GlobalCapturePolicySqlSchema.ValidateTables(connection);
        }

        if (expectedRole == DatabaseRole.Current &&
            schemaVersion >= CurrentSchemaVersion)
        {
            CustomBinaryFormatConfigurationSqlSchema.ValidateTable(connection);
        }

        if (expectedRole == DatabaseRole.StorageCatalog &&
'@
Replace-Required -Path $database -Old $old -New $new

$environment = 'tests/Clipensk.Storage.Tests/GlobalPolicyTestEnvironment.cs'
$old = @'
    public SqliteGlobalClipboardCapturePolicyRepository Repository => new(Session, Factory);

    public static async Task<GlobalPolicyTestEnvironment> CreateAsync()
'@
$new = @'
    public SqliteGlobalClipboardCapturePolicyRepository Repository => new(Session, Factory);
    public SqliteCustomBinaryFormatConfigurationRepository CustomBinaryConfigurations =>
        new(Session, Factory);

    public static async Task<GlobalPolicyTestEnvironment> CreateAsync()
'@
Replace-Required -Path $environment -Old $old -New $new

$old = @'
    public void DowngradeToV4() => Execute("""
        DROP TABLE GlobalFormatCapturePolicy;
        DROP TABLE GlobalCapturePolicy;
        UPDATE DatabaseIdentity SET SchemaVersion = 4;
        PRAGMA user_version = 4;
        """);

    public Task<ProtectedStorageDatabaseResult> ValidateAsync(CancellationToken token = default) =>
'@
$new = @'
    public void DowngradeToV4() => Execute("""
        DROP TABLE CustomBinaryFormatConfiguration;
        DROP TABLE GlobalFormatCapturePolicy;
        DROP TABLE GlobalCapturePolicy;
        UPDATE DatabaseIdentity SET SchemaVersion = 4;
        PRAGMA user_version = 4;
        """);

    public void DowngradeToV5() => Execute("""
        DROP TABLE CustomBinaryFormatConfiguration;
        UPDATE DatabaseIdentity SET SchemaVersion = 5;
        PRAGMA user_version = 5;
        """);

    public Task<ProtectedStorageDatabaseResult> ValidateAsync(CancellationToken token = default) =>
'@
Replace-Required -Path $environment -Old $old -New $new

git diff --check
if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }
