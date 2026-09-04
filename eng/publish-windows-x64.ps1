param(
    [string]$NativeArtifactDirectory = "artifacts/native/win-x64",
    [string]$OutputDirectory = "artifacts/publish/win-x64",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
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

$nativeDirectoryPath = Get-RepositoryPath $NativeArtifactDirectory
$outputDirectoryPath = Get-RepositoryPath $OutputDirectory
$appProjectPath = Join-Path $repositoryRoot "src/Clipensk.App/Clipensk.App.csproj"
$nativeManifestPath = Join-Path $nativeDirectoryPath "native-build-manifest.json"
$nativeDllPath = Join-Path $nativeDirectoryPath "sqlcipher.dll"

if (-not (Test-Path -LiteralPath $nativeManifestPath)) {
    throw "Native build manifest not found at $nativeManifestPath."
}
if (-not (Test-Path -LiteralPath $nativeDllPath)) {
    throw "Verified native SQLCipher DLL not found at $nativeDllPath."
}
if (-not (Test-Path -LiteralPath $appProjectPath)) {
    throw "Clipensk.App project not found at $appProjectPath."
}

$appProject = [xml](Get-Content -LiteralPath $appProjectPath -Raw)
$platformValues = @(
    $appProject.Project.PropertyGroup |
        ForEach-Object { [string]$_.Platforms } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
$runtimeIdentifierValues = @(
    $appProject.Project.PropertyGroup |
        ForEach-Object { [string]$_.RuntimeIdentifiers } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
$packageTypeValues = @(
    $appProject.Project.PropertyGroup |
        ForEach-Object { [string]$_.WindowsPackageType } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)

if ($platformValues.Count -ne 1 -or $platformValues[0] -ne "x64") {
    throw "Clipensk.App must declare exactly Platforms=x64 for this delivery path."
}
if ($runtimeIdentifierValues.Count -ne 1 -or $runtimeIdentifierValues[0] -ne "win-x64") {
    throw "Clipensk.App must declare exactly RuntimeIdentifiers=win-x64 for this delivery path."
}
if ($packageTypeValues.Count -ne 1 -or $packageTypeValues[0] -ne "None") {
    throw "This delivery path is only for the current unpackaged WindowsPackageType=None host."
}

$nativeManifest = Get-Content -LiteralPath $nativeManifestPath -Raw | ConvertFrom-Json
if ($nativeManifest.architecture -ne "win-x64") {
    throw "Native artifact architecture must be win-x64, got $($nativeManifest.architecture)."
}
if ([string]::IsNullOrWhiteSpace($nativeManifest.sqlcipherDllSha256)) {
    throw "Native build manifest does not contain sqlcipherDllSha256."
}
if ($null -eq $nativeManifest.provenance -or
    [string]::IsNullOrWhiteSpace($nativeManifest.provenance.repositoryCommit)) {
    throw "Native build manifest does not contain verified repository provenance."
}

$nativeManifestSha256 = (Get-FileHash -LiteralPath $nativeManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sourceDllSha256 = (Get-FileHash -LiteralPath $nativeDllPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sourceDllSha256 -ne $nativeManifest.sqlcipherDllSha256) {
    throw "Native SQLCipher hash mismatch before publish. Expected $($nativeManifest.sqlcipherDllSha256), got $sourceDllSha256."
}

$requiredLicenseFiles = @(
    "SQLCipher-LICENSE.txt",
    "SQLite-LICENSE.md",
    "OpenSSL-LICENSE.txt"
)
foreach ($licenseFile in $requiredLicenseFiles) {
    $licensePath = Join-Path $nativeDirectoryPath $licenseFile
    if (-not (Test-Path -LiteralPath $licensePath)) {
        throw "Required native license file not found at $licensePath."
    }
}

Remove-Item -LiteralPath $outputDirectoryPath -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $outputDirectoryPath -Force | Out-Null

& dotnet publish $appProjectPath `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained false `
    -p:Platform=x64 `
    --output $outputDirectoryPath
if ($LASTEXITCODE -ne 0) {
    throw "Clipensk.App x64 publish failed with exit code $LASTEXITCODE."
}

$publishedExePath = Join-Path $outputDirectoryPath "Clipensk.App.exe"
$publishedManagedPath = Join-Path $outputDirectoryPath "Clipensk.App.dll"
$publishedDllPath = Join-Path $outputDirectoryPath "sqlcipher.dll"

if (-not (Test-Path -LiteralPath $publishedExePath) -or
    -not (Test-Path -LiteralPath $publishedManagedPath)) {
    throw "Clipensk.App publish output is incomplete."
}

if (Test-Path -LiteralPath $publishedDllPath) {
    throw "dotnet publish produced an unexpected sqlcipher.dll before verified native staging."
}

$forbiddenBundledDll = Join-Path $outputDirectoryPath "e_sqlcipher.dll"
if (Test-Path -LiteralPath $forbiddenBundledDll) {
    throw "dotnet publish produced deprecated bundled e_sqlcipher.dll."
}

Copy-Item -LiteralPath $nativeDllPath -Destination $publishedDllPath
Copy-Item -LiteralPath $nativeManifestPath -Destination (Join-Path $outputDirectoryPath "native-build-manifest.json")

$nativeLicenseDirectory = Join-Path $outputDirectoryPath "licenses/native"
New-Item -ItemType Directory -Path $nativeLicenseDirectory -Force | Out-Null
foreach ($licenseFile in $requiredLicenseFiles) {
    Copy-Item `
        -LiteralPath (Join-Path $nativeDirectoryPath $licenseFile) `
        -Destination (Join-Path $nativeLicenseDirectory $licenseFile)
}

$publishedDllSha256 = (Get-FileHash -LiteralPath $publishedDllPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($publishedDllSha256 -ne $sourceDllSha256) {
    throw "Published SQLCipher DLL differs from verified native artifact. Expected $sourceDllSha256, got $publishedDllSha256."
}

$runtimeManifest = [ordered]@{
    schemaVersion = 1
    architecture = "win-x64"
    runtimeIdentifier = "win-x64"
    configuration = $Configuration
    selfContained = $false
    appProject = "src/Clipensk.App/Clipensk.App.csproj"
    windowsPackageType = "None"
    sqlcipherDllSha256 = $publishedDllSha256
    nativeBuildManifest = "native-build-manifest.json"
    nativeBuildManifestSha256 = $nativeManifestSha256
    nativeRepositoryCommit = $nativeManifest.provenance.repositoryCommit
}
$runtimeManifest | ConvertTo-Json -Depth 4 | Set-Content `
    -LiteralPath (Join-Path $outputDirectoryPath "runtime-delivery-manifest.json") `
    -Encoding UTF8

Write-Host "Clipensk x64 unpackaged runtime publish complete."
Write-Host "Output: $outputDirectoryPath"
Write-Host "sqlcipher.dll SHA-256: $publishedDllSha256"
Write-Host "Native manifest SHA-256: $nativeManifestSha256"
