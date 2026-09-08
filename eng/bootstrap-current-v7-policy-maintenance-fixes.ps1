$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$path = 'tests/Clipensk.Storage.Tests/SqliteGlobalCapturePolicyMaintenanceRepositoryTests.cs'
$fullPath = Join-Path (Get-Location) $path
if (-not (Test-Path $fullPath)) {
    throw "Expected generated test file $path."
}

$text = [System.IO.File]::ReadAllText($fullPath)
if (-not $text.Contains('using Microsoft.Data.Sqlite;')) {
    $pattern = '(?m)^using Clipensk\.Storage\.Sqlite;\r?$'
    $matches = [regex]::Matches($text, $pattern)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one Storage.Sqlite import anchor in $path, found $($matches.Count)."
    }

    $text = [regex]::Replace(
        $text,
        $pattern,
        "using Clipensk.Storage.Sqlite;`r`nusing Microsoft.Data.Sqlite;",
        1)
    [System.IO.File]::WriteAllText(
        $fullPath,
        $text,
        [System.Text.UTF8Encoding]::new($false))
}
