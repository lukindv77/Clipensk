$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-Text([string]$Path) {
    return [System.IO.File]::ReadAllText((Join-Path (Get-Location) $Path))
}

function Write-Text([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText(
        (Join-Path (Get-Location) $Path),
        $Text,
        [System.Text.UTF8Encoding]::new($false))
}

function Replace-RegexOnce(
    [string]$Path,
    [string]$Pattern,
    [string]$Replacement)
{
    $text = Read-Text $Path
    $regex = [regex]::new($Pattern)
    $matches = $regex.Matches($text)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one regex anchor in $Path, found $($matches.Count)."
    }

    $match = $matches[0]
    $updated = $text.Substring(0, $match.Index) +
        $Replacement +
        $text.Substring($match.Index + $match.Length)
    Write-Text $Path $updated
}

# Generated maintenance repository tests need Microsoft.Data.Sqlite for SqliteOpenMode,
# SqliteConnection and SqliteCommand.
$maintenanceTestsPath = 'tests/Clipensk.Storage.Tests/SqliteGlobalCapturePolicyMaintenanceRepositoryTests.cs'
if (-not (Test-Path $maintenanceTestsPath)) {
    throw "Expected generated test file $maintenanceTestsPath."
}
$maintenanceTests = Read-Text $maintenanceTestsPath
if (-not $maintenanceTests.Contains('using Microsoft.Data.Sqlite;')) {
    Replace-RegexOnce `
        $maintenanceTestsPath `
        '(?m)^using Clipensk\.Storage\.Sqlite;\r?$' `
        "using Clipensk.Storage.Sqlite;`r`nusing Microsoft.Data.Sqlite;"
}

# CHECK constraints remain production enforcement. The malformed-phase repository test
# deliberately bypasses CHECK on the SAME SQLite connection so the read boundary still
# proves that malformed persisted data is rejected rather than mapped to a phase.
Replace-RegexOnce `
    $maintenanceTestsPath `
    '(?s)        environment\.Execute\("PRAGMA ignore_check_constraints = ON;"\);\r?\n        environment\.Execute\(\$"""\r?\n            INSERT INTO GlobalCapturePolicyMaintenance \(.*?            """\);\r?\n        var repository =' `
@'
        using (SqliteConnection connection = environment.Factory.Open(
            environment.CurrentPath,
            environment.Key,
            SqliteOpenMode.ReadWrite))
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText = $"""
                PRAGMA ignore_check_constraints = ON;
                INSERT INTO GlobalCapturePolicyMaintenance (
                    SingletonId, OperationId, Phase, StartedAtUtc)
                VALUES (1, '{operationId}', '{phase}', '{startedAtUtc}');
                """;
            insert.ExecuteNonQuery();
        }
        var repository =
'@

# Current v7 is now a valid schema, so the old corruption case must use a genuinely
# mismatched future user_version.
Replace-RegexOnce `
    'tests/Clipensk.Storage.Tests/SqliteGlobalClipboardCapturePolicyRepositoryTests.cs' `
    '\[InlineData\("PRAGMA user_version = 7;"\)\]' `
    '[InlineData("PRAGMA user_version = 8;")]'

# Composition now performs two explicit Current ReadOnly snapshots: maintenance marker
# preflight followed by persisted global policy. Keep the configured-case assertion exact.
Replace-RegexOnce `
    'tests/Clipensk.Storage.Tests/ProtectedClipboardDeliveryServicesTests.cs' `
    '        Assert\.Single\(environment\.Factory\.Modes\);' `
    '        Assert.Equal(2, environment.Factory.Modes.Count);'

# Missing-Catalog recovery must remain compatible with every historical Current schema,
# including v6 after latest advances to v7. Thresholds belong to the schema family that
# introduced each table, not to the moving latest version.
$recoveryPath = 'src/Clipensk.Storage/Databases/ProtectedStorageCatalogRecoveryService.cs'
Replace-RegexOnce `
    $recoveryPath `
    '\[1, 2, 3, 4, 5, ProtectedStorageDatabaseService\.CurrentSchemaVersion\]\);' `
    'Enumerable.Range(1, ProtectedStorageDatabaseService.CurrentSchemaVersion).ToArray());'

Replace-RegexOnce `
    $recoveryPath `
    '(?s)        if \(identity\.SchemaVersion >= ProtectedStorageDatabaseService\.CurrentSchemaVersion\)\r?\n        \{\r?\n            CustomBinaryFormatConfigurationSqlSchema\.ValidateTable\(connection\);\r?\n        \}' `
@'
        if (identity.SchemaVersion >=
            CustomBinaryFormatConfigurationSqlSchema.MinimumCurrentSchemaVersion)
        {
            CustomBinaryFormatConfigurationSqlSchema.ValidateTable(connection);
        }
        if (identity.SchemaVersion >=
            GlobalCapturePolicyMaintenanceSqlSchema.MinimumCurrentSchemaVersion)
        {
            GlobalCapturePolicyMaintenanceSqlSchema.ValidateTable(connection);
        }
'@

# Regression coverage for pre-session recovery compatibility and v7 validation.
$recoveryTestsPath = 'tests/Clipensk.Storage.Tests/ProtectedStorageCatalogRecoveryServiceTests.cs'
$recoveryTests = Read-Text $recoveryTestsPath
if (-not $recoveryTests.Contains('RecoverMissingCatalogAsync_LegacyCurrentV6RemainsRecoverableWithoutImplicitMigration')) {
    Replace-RegexOnce `
        $recoveryTestsPath `
        '    \[Fact\]\r?\n    public async Task RecoverMissingCatalogAsync_ExistingCatalogRefusesWithoutReplacingIt\(\)' `
@'
    [Fact]
    public async Task RecoverMissingCatalogAsync_LegacyCurrentV6RemainsRecoverableWithoutImplicitMigration()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute("""
            INSERT INTO CustomBinaryFormatConfiguration (FormatName, FileExtension)
            VALUES ('Example.Custom', '.payload');
            """);
        environment.DowngradeToV6();
        environment.Session.Dispose();
        File.Delete(environment.CatalogPath);

        var recovery = new ProtectedStorageCatalogRecoveryService(environment.Factory);
        ProtectedStorageDatabaseResult result = await recovery.RecoverMissingCatalogAsync(
            environment.Root,
            environment.StorageId,
            environment.Key,
            DateOnly.FromDateTime(DateTime.Now));

        Assert.True(result.IsSuccess);
        Assert.True(File.Exists(environment.CatalogPath));
        Assert.Equal(6, environment.Scalar("SELECT SchemaVersion FROM DatabaseIdentity;"));
        Assert.Equal(6, environment.Scalar("PRAGMA user_version;"));
        Assert.Equal(1, environment.Scalar(
            "SELECT COUNT(*) FROM CustomBinaryFormatConfiguration WHERE FormatName = 'Example.Custom';"));

        ProtectedStorageDatabaseResult normalValidation =
            await environment.Service.InitializeOrValidateAsync(
                environment.Root,
                environment.StorageId,
                environment.Key,
                allowInitialize: false);
        Assert.True(normalValidation.IsSuccess);
        Assert.Equal(ProtectedStorageDatabaseService.CurrentSchemaVersion,
            environment.Scalar("PRAGMA user_version;"));
    }

    [Fact]
    public async Task RecoverMissingCatalogAsync_MalformedV7MaintenanceSchemaFailsBeforePublication()
    {
        using GlobalPolicyTestEnvironment environment = await GlobalPolicyTestEnvironment.CreateAsync();
        environment.Execute("""
            DROP TABLE GlobalCapturePolicyMaintenance;
            CREATE TABLE GlobalCapturePolicyMaintenance (
                SingletonId INTEGER PRIMARY KEY,
                OperationId TEXT,
                Phase TEXT,
                StartedAtUtc TEXT);
            """);
        environment.Session.Dispose();
        File.Delete(environment.CatalogPath);

        var recovery = new ProtectedStorageCatalogRecoveryService(environment.Factory);
        ProtectedStorageDatabaseResult result = await recovery.RecoverMissingCatalogAsync(
            environment.Root,
            environment.StorageId,
            environment.Key,
            DateOnly.FromDateTime(DateTime.Now));

        Assert.Equal(ProtectedStorageDatabaseStatus.InvalidDatabaseIdentity, result.Status);
        Assert.False(File.Exists(environment.CatalogPath));
        Assert.Empty(RecoveryStagingFiles(environment));
    }

    [Fact]
    public async Task RecoverMissingCatalogAsync_ExistingCatalogRefusesWithoutReplacingIt()
'@
}
