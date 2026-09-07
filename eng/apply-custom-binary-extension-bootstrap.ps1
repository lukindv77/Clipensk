$ErrorActionPreference = 'Stop'

function Read-Lf([string]$Path) {
    return (Get-Content -LiteralPath $Path -Raw).Replace("`r`n", "`n")
}

function Write-Lf([string]$Path, [string]$Content) {
    [System.IO.File]::WriteAllText(
        $Path,
        $Content.Replace("`r`n", "`n"),
        [System.Text.UTF8Encoding]::new($false))
}

function Replace-Required([string]$Path, [string]$Old, [string]$New) {
    $text = Read-Lf $Path
    $oldLf = $Old.Replace("`r`n", "`n")
    $newLf = $New.Replace("`r`n", "`n")
    $first = $text.IndexOf($oldLf, [StringComparison]::Ordinal)
    if ($first -lt 0) { throw "Required context not found in ${Path}: $oldLf" }
    if ($text.IndexOf($oldLf, $first + $oldLf.Length, [StringComparison]::Ordinal) -ge 0) {
        throw "Required context is not unique in ${Path}: $oldLf"
    }
    Write-Lf $Path ($text.Substring(0, $first) + $newLf + $text.Substring($first + $oldLf.Length))
}

$patchPath = 'eng/custom-binary-extension-config.patch'
$lines = @(Get-Content -LiteralPath $patchPath)
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^@@ -(?<oldStart>\d+)(?:,(?<oldCount>\d+))? \+(?<newStart>\d+)(?:,(?<newCount>\d+))? @@(?<suffix>.*)$') {
        $oldStart = [int]$Matches.oldStart
        $newStart = [int]$Matches.newStart
        $suffix = $Matches.suffix
        $oldCount = 0
        $newCount = 0
        $j = $i + 1
        while ($j -lt $lines.Count -and -not $lines[$j].StartsWith('@@ ') -and -not $lines[$j].StartsWith('diff --git ')) {
            if ($lines[$j].StartsWith(' ') -or $lines[$j].StartsWith('-')) { $oldCount++ }
            if ($lines[$j].StartsWith(' ') -or $lines[$j].StartsWith('+')) { $newCount++ }
            $j++
        }
        $lines[$i] = "@@ -$oldStart,$oldCount +$newStart,$newCount @@$suffix"
    }
}
Write-Lf $patchPath (($lines -join "`n") + "`n")

$excludeArgs = @(
    '--exclude=src/Clipensk.Storage/Databases/ProtectedStorageDatabaseService.cs',
    '--exclude=tests/Clipensk.Storage.Tests/GlobalPolicyTestEnvironment.cs'
)
& git apply --check @excludeArgs $patchPath
if ($LASTEXITCODE -ne 0) { throw 'git apply --check failed.' }
& git apply @excludeArgs $patchPath
if ($LASTEXITCODE -ne 0) { throw 'git apply failed.' }

$database = 'src/Clipensk.Storage/Databases/ProtectedStorageDatabaseService.cs'
Replace-Required $database `
    '    private const int HistoryCurrentSchemaVersion = 4;' `
    "    private const int HistoryCurrentSchemaVersion = 4;`n    private const int GlobalCapturePolicyCurrentSchemaVersion = 5;"
Replace-Required $database `
    '    public const int CurrentSchemaVersion = 5;' `
    '    public const int CurrentSchemaVersion = 6;'
Replace-Required $database `
    "                    HistoryCurrentSchemaVersion,`n                    CurrentSchemaVersion);" `
    "                    HistoryCurrentSchemaVersion,`n                    GlobalCapturePolicyCurrentSchemaVersion,`n                    CurrentSchemaVersion);"
Replace-Required $database `
    "                        cancellationToken);`n                }`n`n                if (catalogSchemaVersion == LegacyCatalogSchemaVersion)" `
    "                        cancellationToken);`n                    currentSchemaVersion = GlobalCapturePolicyCurrentSchemaVersion;`n                }`n`n                if (currentSchemaVersion == GlobalCapturePolicyCurrentSchemaVersion)`n                {`n                    MigrateCurrentFromV5ToV6(`n                        currentDatabasePath,`n                        storageId,`n                        masterKey,`n                        cancellationToken);`n                    currentSchemaVersion = CurrentSchemaVersion;`n                }`n`n                if (catalogSchemaVersion == LegacyCatalogSchemaVersion)"
Replace-Required $database `
    "            ClipboardHistorySqlSchema.CreateTables(connection, transaction);`n            GlobalCapturePolicySqlSchema.CreateTables(connection, transaction);" `
    "            ClipboardHistorySqlSchema.CreateTables(connection, transaction);`n            GlobalCapturePolicySqlSchema.CreateTables(connection, transaction);`n            CustomBinaryFormatConfigurationSqlSchema.CreateTable(connection, transaction);"
Replace-Required $database `
    "            HistoryCurrentSchemaVersion, CurrentSchemaVersion);`n        SetUserVersion(connection, transaction, CurrentSchemaVersion);" `
    "            HistoryCurrentSchemaVersion, GlobalCapturePolicyCurrentSchemaVersion);`n        SetUserVersion(connection, transaction, GlobalCapturePolicyCurrentSchemaVersion);"

$newMigration = @'
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

'@
Replace-Required $database `
    '    private void MigrateCatalogFromV1ToV2(' `
    ($newMigration + '    private void MigrateCatalogFromV1ToV2(')
Replace-Required $database `
    "        if (expectedRole == DatabaseRole.Current &&`n            schemaVersion >= CurrentSchemaVersion)`n        {`n            GlobalCapturePolicySqlSchema.ValidateTables(connection);`n        }" `
    "        if (expectedRole == DatabaseRole.Current &&`n            schemaVersion >= GlobalCapturePolicyCurrentSchemaVersion)`n        {`n            GlobalCapturePolicySqlSchema.ValidateTables(connection);`n        }`n`n        if (expectedRole == DatabaseRole.Current &&`n            schemaVersion >= CurrentSchemaVersion)`n        {`n            CustomBinaryFormatConfigurationSqlSchema.ValidateTable(connection);`n        }"

$environment = 'tests/Clipensk.Storage.Tests/GlobalPolicyTestEnvironment.cs'
Replace-Required $environment `
    '    public SqliteGlobalClipboardCapturePolicyRepository Repository => new(Session, Factory);' `
    "    public SqliteGlobalClipboardCapturePolicyRepository Repository => new(Session, Factory);`n    public SqliteCustomBinaryFormatConfigurationRepository CustomBinaryConfigurations =>`n        new(Session, Factory);"
Replace-Required $environment `
    "    public void DowngradeToV4() => Execute(`"`"`"`n        DROP TABLE GlobalFormatCapturePolicy;" `
    "    public void DowngradeToV4() => Execute(`"`"`"`n        DROP TABLE CustomBinaryFormatConfiguration;`n        DROP TABLE GlobalFormatCapturePolicy;"
Replace-Required $environment `
    "        PRAGMA user_version = 4;`n        `"`"`");`n`n    public Task<ProtectedStorageDatabaseResult> ValidateAsync" `
    "        PRAGMA user_version = 4;`n        `"`"`");`n`n    public void DowngradeToV5() => Execute(`"`"`"`n        DROP TABLE CustomBinaryFormatConfiguration;`n        UPDATE DatabaseIdentity SET SchemaVersion = 5;`n        PRAGMA user_version = 5;`n        `"`"`");`n`n    public Task<ProtectedStorageDatabaseResult> ValidateAsync"

git diff --check
if ($LASTEXITCODE -ne 0) { throw 'git diff --check failed.' }
