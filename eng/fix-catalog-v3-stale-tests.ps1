$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Replace-Exact {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Old,
        [Parameter(Mandatory = $true)][string]$New
    )

    $text = Get-Content -LiteralPath $Path -Raw
    if ($text.Contains($New)) {
        return
    }
    if (-not $text.Contains($Old)) {
        throw "Expected stale Catalog v2 test anchor was not found in $Path."
    }

    Set-Content -LiteralPath $Path -Value $text.Replace($Old, $New) -Encoding utf8NoBOM -NoNewline
}

Replace-Exact `
    -Path 'tests/Clipensk.Storage.Tests/ProtectedStorageCurrentSchemaV5MigrationTests.cs' `
    -Old @'
    public async Task NewStorage_CreatesLatestCurrentAndCatalogV2WithoutPolicyDefaults()
'@ `
    -New @'
    public async Task NewStorage_CreatesLatestCurrentAndCatalogV3WithoutPolicyDefaults()
'@

Replace-Exact `
    -Path 'tests/Clipensk.Storage.Tests/ProtectedStorageCurrentSchemaV5MigrationTests.cs' `
    -Old @'
        Assert.Equal(2, environment.Scalar("PRAGMA user_version;", catalog: true));
'@ `
    -New @'
        Assert.Equal(ProtectedStorageDatabaseService.CatalogSchemaVersion, environment.Scalar("PRAGMA user_version;", catalog: true));
'@

Replace-Exact `
    -Path 'tests/Clipensk.Storage.Tests/ProtectedStorageCurrentSchemaV4MigrationTests.cs' `
    -Old @'
    public async Task Initialize_CreatesLatestCurrentWithHistorySchemaAndCatalogV2()
'@ `
    -New @'
    public async Task Initialize_CreatesLatestCurrentWithHistorySchemaAndCatalogV3()
'@

Replace-Exact `
    -Path 'tests/Clipensk.Storage.Tests/ProtectedStorageCurrentSchemaV4MigrationTests.cs' `
    -Old @'
            Assert.Equal(2, ReadSchemaVersion(factory, CatalogPath(root), key));
'@ `
    -New @'
            Assert.Equal(ProtectedStorageDatabaseService.CatalogSchemaVersion, ReadSchemaVersion(factory, CatalogPath(root), key));
'@

Write-Host 'Updated stale latest Catalog version expectations.'
