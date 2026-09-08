$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$path = 'tests/Clipensk.Storage.Tests/SqliteGlobalCapturePolicyMaintenanceRepositoryTests.cs'
$fullPath = Join-Path (Get-Location) $path
if (-not (Test-Path $fullPath)) {
    throw "Expected generated test file $path."
}

$text = [System.IO.File]::ReadAllText($fullPath)
if (-not $text.Contains('using Clipensk.Core.Storage;')) {
    $old = "using Clipensk.Storage.Clipboard;`nusing Clipensk.Storage.Sqlite;"
    $new = "using Clipensk.Core.Storage;`nusing Clipensk.Storage.Clipboard;`nusing Clipensk.Storage.Sqlite;"
    $count = ([regex]::Matches($text, [regex]::Escape($old))).Count
    if ($count -ne 1) {
        throw "Expected exactly one import anchor in $path, found $count."
    }

    [System.IO.File]::WriteAllText(
        $fullPath,
        $text.Replace($old, $new),
        [System.Text.UTF8Encoding]::new($false))
}
