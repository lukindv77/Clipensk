$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$path = 'tests/Clipensk.Storage.Tests/SqliteGlobalCapturePolicyMaintenanceRepositoryTests.cs'
$fullPath = Join-Path (Get-Location) $path
if (-not (Test-Path $fullPath)) {
    throw "Expected generated test file $path."
}

$text = [System.IO.File]::ReadAllText($fullPath)
if (-not $text.Contains('using Clipensk.Core.Storage;')) {
    $pattern = '(?m)^using Clipensk\.Storage\.Clipboard;\r?$'
    $matches = [regex]::Matches($text, $pattern)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one Clipboard import anchor in $path, found $($matches.Count)."
    }

    $text = [regex]::Replace(
        $text,
        $pattern,
        "using Clipensk.Core.Storage;`r`nusing Clipensk.Storage.Clipboard;",
        1)
    [System.IO.File]::WriteAllText(
        $fullPath,
        $text,
        [System.Text.UTF8Encoding]::new($false))
}
