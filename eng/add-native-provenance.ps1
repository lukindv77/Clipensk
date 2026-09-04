param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x64")]
    [string]$Architecture,

    [Parameter(Mandatory = $true)]
    [string]$ArtifactDirectory,

    [Parameter(Mandatory = $true)]
    [string]$BuildScript
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$artifactDirectoryPath = (Resolve-Path (Join-Path $repositoryRoot $ArtifactDirectory)).Path
$buildScriptPath = (Resolve-Path (Join-Path $repositoryRoot $BuildScript)).Path
$workflowRelativePath = ".github/workflows/sqlcipher-native.yml"
$workflowPath = (Resolve-Path (Join-Path $repositoryRoot $workflowRelativePath)).Path
$manifestPath = Join-Path $artifactDirectoryPath "native-build-manifest.json"
$dllPath = Join-Path $artifactDirectoryPath "sqlcipher.dll"

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Native build manifest not found at $manifestPath."
}
if (-not (Test-Path -LiteralPath $dllPath)) {
    throw "Native SQLCipher DLL not found at $dllPath."
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.architecture -ne $Architecture) {
    throw "Manifest architecture mismatch. Expected $Architecture, got $($manifest.architecture)."
}

$actualDllSha256 = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualDllSha256 -ne $manifest.sqlcipherDllSha256) {
    throw "sqlcipher.dll SHA-256 mismatch between artifact and manifest. Expected $($manifest.sqlcipherDllSha256), got $actualDllSha256."
}

$repositoryCommit = (& git.exe -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repositoryCommit)) {
    throw "Unable to resolve repository commit for native provenance."
}

$repositoryTree = (& git.exe -C $repositoryRoot rev-parse "HEAD^{tree}").Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($repositoryTree)) {
    throw "Unable to resolve repository tree for native provenance."
}

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_SHA) -and $env:GITHUB_SHA -ne $repositoryCommit) {
    throw "GitHub Actions SHA mismatch. GITHUB_SHA=$($env:GITHUB_SHA), checkout HEAD=$repositoryCommit."
}

$vsWhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vsWhere)) {
    throw "vswhere.exe not found while collecting native provenance."
}

$vsComponent = "Microsoft.VisualStudio.Component.VC.Tools.x86.x64"
$vsInstallationVersion = (& $vsWhere -latest -products * -requires $vsComponent -property installationVersion).Trim()
if ([string]::IsNullOrWhiteSpace($vsInstallationVersion)) {
    throw "Unable to resolve Visual Studio installation version for $Architecture."
}

$buildScriptRelativePath = [System.IO.Path]::GetRelativePath($repositoryRoot, $buildScriptPath).Replace("\", "/")
$buildScriptSha256 = (Get-FileHash -LiteralPath $buildScriptPath -Algorithm SHA256).Hash.ToLowerInvariant()
$workflowSha256 = (Get-FileHash -LiteralPath $workflowPath -Algorithm SHA256).Hash.ToLowerInvariant()

$provenance = [ordered]@{
    schemaVersion = 1
    repositoryCommit = $repositoryCommit
    repositoryTree = $repositoryTree
    buildScript = $buildScriptRelativePath
    buildScriptSha256 = $buildScriptSha256
    workflow = $workflowRelativePath
    workflowSha256 = $workflowSha256
    visualStudioInstallationVersion = $vsInstallationVersion
    sqlcipherSourceRepository = "https://github.com/sqlcipher/sqlcipher.git"
    opensslSourceUrl = "https://github.com/openssl/openssl/releases/download/openssl-$($manifest.opensslVersion)/openssl-$($manifest.opensslVersion).tar.gz"
    githubActions = [ordered]@{
        repository = $env:GITHUB_REPOSITORY
        sha = $env:GITHUB_SHA
        runId = $env:GITHUB_RUN_ID
        runAttempt = $env:GITHUB_RUN_ATTEMPT
        workflowRef = $env:GITHUB_WORKFLOW_REF
        runnerOs = $env:RUNNER_OS
        runnerArch = $env:RUNNER_ARCH
        runnerName = $env:RUNNER_NAME
        imageOs = $env:ImageOS
        imageVersion = $env:ImageVersion
    }
}

$manifest | Add-Member -NotePropertyName provenance -NotePropertyValue $provenance -Force
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host "Native provenance recorded for $Architecture."
Write-Host "Repository commit: $repositoryCommit"
Write-Host "Build script SHA-256: $buildScriptSha256"
Write-Host "Workflow SHA-256: $workflowSha256"
