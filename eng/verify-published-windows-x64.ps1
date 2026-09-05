param(
    [string]$PublishedRuntimeDirectory = "artifacts/publish/win-x64",
    [string]$SmokeDirectory = "artifacts/smoke/win-x64",
    [string]$VerificationDirectory = "artifacts/runtime-smoke/win-x64"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path $PSScriptRoot -Parent

function Get-RepositoryPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

$publishedDirectoryPath = Get-RepositoryPath $PublishedRuntimeDirectory
$smokeDirectoryPath = Get-RepositoryPath $SmokeDirectory
$verificationDirectoryPath = Get-RepositoryPath $VerificationDirectory

$runtimeManifestPath = Join-Path $publishedDirectoryPath "runtime-delivery-manifest.json"
$nativeManifestPath = Join-Path $publishedDirectoryPath "native-build-manifest.json"
$publishedDllPath = Join-Path $publishedDirectoryPath "sqlcipher.dll"
$forbiddenBundledDllPath = Join-Path $publishedDirectoryPath "e_sqlcipher.dll"
$smokeHostPath = Join-Path $smokeDirectoryPath "Clipensk.SqlCipher.Smoke.dll"

foreach ($requiredPath in @(
    $runtimeManifestPath,
    $nativeManifestPath,
    $publishedDllPath,
    $smokeHostPath
)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required published-runtime verification input not found at $requiredPath."
    }
}

if (Test-Path -LiteralPath $forbiddenBundledDllPath) {
    throw "Published runtime contains deprecated bundled e_sqlcipher.dll."
}

$runtimeManifest = Get-Content -LiteralPath $runtimeManifestPath -Raw | ConvertFrom-Json
$nativeManifest = Get-Content -LiteralPath $nativeManifestPath -Raw | ConvertFrom-Json

if ($runtimeManifest.schemaVersion -ne 1) {
    throw "Unsupported runtime delivery manifest schemaVersion $($runtimeManifest.schemaVersion)."
}
if ($runtimeManifest.architecture -ne "win-x64") {
    throw "Published runtime architecture must be win-x64, got $($runtimeManifest.architecture)."
}
if ($runtimeManifest.runtimeIdentifier -ne "win-x64") {
    throw "Published runtime identifier must be win-x64, got $($runtimeManifest.runtimeIdentifier)."
}
if ($runtimeManifest.windowsPackageType -ne "None") {
    throw "Published runtime verification currently requires WindowsPackageType=None."
}
if ($runtimeManifest.selfContained -ne $false) {
    throw "Published runtime verification expected selfContained=false."
}

$publishedDllSha256 = (Get-FileHash -LiteralPath $publishedDllPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($publishedDllSha256 -ne $runtimeManifest.sqlcipherDllSha256) {
    throw "Published SQLCipher hash does not match runtime delivery manifest."
}
if ($publishedDllSha256 -ne $nativeManifest.sqlcipherDllSha256) {
    throw "Published SQLCipher hash does not match native build manifest."
}

$nativeManifestSha256 = (Get-FileHash -LiteralPath $nativeManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($nativeManifestSha256 -ne $runtimeManifest.nativeBuildManifestSha256) {
    throw "Published native build manifest hash does not match runtime delivery manifest."
}
if ($null -eq $nativeManifest.provenance -or
    [string]::IsNullOrWhiteSpace($nativeManifest.provenance.repositoryCommit) -or
    $nativeManifest.provenance.repositoryCommit -ne $runtimeManifest.nativeRepositoryCommit) {
    throw "Published native provenance does not match runtime delivery manifest."
}

Remove-Item -LiteralPath $verificationDirectoryPath -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $verificationDirectoryPath -Force | Out-Null

Copy-Item `
    -Path (Join-Path $smokeDirectoryPath "*") `
    -Destination $verificationDirectoryPath `
    -Recurse `
    -Force

Get-ChildItem `
    -LiteralPath $verificationDirectoryPath `
    -Filter "sqlcipher.dll" `
    -File `
    -Recurse | Remove-Item -Force

$verificationSmokeHostPath = Join-Path $verificationDirectoryPath "Clipensk.SqlCipher.Smoke.dll"
if (-not (Test-Path -LiteralPath $verificationSmokeHostPath)) {
    throw "Verification smoke host was not copied to $verificationSmokeHostPath."
}
if (Get-ChildItem -LiteralPath $verificationDirectoryPath -Filter "sqlcipher.dll" -File -Recurse) {
    throw "Verification tree must not contain its own sqlcipher.dll."
}

$originalPath = $env:PATH
try {
    $env:PATH = "$publishedDirectoryPath;$originalPath"
    Push-Location $verificationDirectoryPath
    try {
        & dotnet $verificationSmokeHostPath
        if ($LASTEXITCODE -ne 0) {
            throw "Published runtime SQLCipher smoke failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    $env:PATH = $originalPath
}

Write-Host "Published Clipensk x64 runtime SQLCipher verification PASS."
Write-Host "Runtime: $publishedDirectoryPath"
Write-Host "sqlcipher.dll SHA-256: $publishedDllSha256"
