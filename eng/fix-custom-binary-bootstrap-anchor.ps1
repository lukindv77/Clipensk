$ErrorActionPreference = 'Stop'

$path = 'eng/apply-custom-binary-extension-bootstrap.ps1'
$text = Get-Content -LiteralPath $path -Raw
$old = 'if (expectedRole == DatabaseRole.Current &&`n            schemaVersion >= CurrentSchemaVersion)'
$new = 'if (expectedRole == DatabaseRole.Current && schemaVersion >= CurrentSchemaVersion)'

if (-not $text.Contains($old)) {
    throw 'Expected bootstrap validation anchor was not found.'
}

$text = $text.Replace($old, $new)
[System.IO.File]::WriteAllText(
    $path,
    $text,
    [System.Text.UTF8Encoding]::new($false))
