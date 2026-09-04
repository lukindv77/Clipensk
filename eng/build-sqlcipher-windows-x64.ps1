param(
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\native\win-x64")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$SqlCipherCommit = "810db22f575ee7cf94ea96a3e91622b5fcece3dc"
$SqlCipherVersion = "4.17.0"
$OpenSslVersion = "3.5.8"
$OpenSslSha256 = "a8f84a39918ec6415ce765d9b429d313ba97b8143169c172e734b9514464f5b2"

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$workRoot = Join-Path $repositoryRoot ".native-build\sqlcipher-x64"
$opensslArchive = Join-Path $workRoot "openssl-$OpenSslVersion.tar.gz"
$opensslSource = Join-Path $workRoot "openssl-$OpenSslVersion"
$opensslInstall = Join-Path $workRoot "openssl-install"
$sqlcipherSource = Join-Path $workRoot "sqlcipher"

Remove-Item $workRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $OutputDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item $workRoot -ItemType Directory -Force | Out-Null
New-Item $OutputDirectory -ItemType Directory -Force | Out-Null

$transcriptPath = Join-Path $OutputDirectory "native-build.log"
Start-Transcript -Path $transcriptPath -Force | Out-Null
try {
    Write-Host "Downloading OpenSSL $OpenSslVersion source..."
    $opensslUri = "https://github.com/openssl/openssl/releases/download/openssl-$OpenSslVersion/openssl-$OpenSslVersion.tar.gz"
    Invoke-WebRequest -Uri $opensslUri -OutFile $opensslArchive

    $actualOpenSslSha256 = (Get-FileHash $opensslArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualOpenSslSha256 -ne $OpenSslSha256) {
        throw "OpenSSL source SHA-256 mismatch. Expected $OpenSslSha256, got $actualOpenSslSha256."
    }

    & tar.exe -xzf $opensslArchive -C $workRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to extract OpenSSL source."
    }

    Write-Host "Fetching SQLCipher commit $SqlCipherCommit..."
    & git.exe init $sqlcipherSource | Out-Null
    & git.exe -C $sqlcipherSource remote add origin https://github.com/sqlcipher/sqlcipher.git
    & git.exe -C $sqlcipherSource fetch --depth=1 origin $SqlCipherCommit
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to fetch SQLCipher source."
    }
    & git.exe -C $sqlcipherSource checkout --detach FETCH_HEAD | Out-Null
    $actualSqlCipherCommit = (& git.exe -C $sqlcipherSource rev-parse HEAD).Trim()
    if ($actualSqlCipherCommit -ne $SqlCipherCommit) {
        throw "SQLCipher source commit mismatch. Expected $SqlCipherCommit, got $actualSqlCipherCommit."
    }

    $vsWhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vsWhere)) {
        throw "vswhere.exe not found. Visual Studio 2022 Build Tools are required."
    }

    $vsInstall = (& $vsWhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
    if ([string]::IsNullOrWhiteSpace($vsInstall)) {
        throw "Visual Studio C++ toolchain not found."
    }

    $vcVars = Join-Path $vsInstall "VC\Auxiliary\Build\vcvars64.bat"
    if (-not (Test-Path $vcVars)) {
        throw "vcvars64.bat not found at $vcVars."
    }

    $nmakeArgsPath = Join-Path $sqlcipherSource "clipensk-nmake-x64.args"
    @"
FOR_WIN10=1
PLATFORM=x64
USE_AMALGAMATION=1
NO_TCL=1
USE_CRT_DLL=1
USE_NATIVE_LIBPATHS=1
SQLITE3DLL=sqlcipher.dll
SQLITE3LIB=sqlcipher.lib
SQLITE3EXE=sqlcipher.exe
CCOPTS=-I$opensslInstall\include
LDFLAGS=$opensslInstall\lib\libcrypto.lib
"LTLIBS=Advapi32.lib User32.lib Kernel32.lib Crypt32.lib Ws2_32.lib Bcrypt.lib"
"OPT_FEATURE_FLAGS=-DSQLITE_TEMP_STORE=2 -DSQLITE_HAS_CODEC=1 -DSQLITE_EXTRA_INIT=sqlcipher_extra_init -DSQLITE_EXTRA_SHUTDOWN=sqlcipher_extra_shutdown -DSQLCIPHER_CRYPTO_OPENSSL=1 -DSQLITE_THREADSAFE=1 -DSQLITE_ENABLE_FTS5=1 -DSQLITE_ENABLE_RTREE=1 -DSQLITE_MAX_ATTACHED=125"
"@ | Set-Content -Path $nmakeArgsPath -Encoding Ascii

    $buildBatch = Join-Path $workRoot "build-native.cmd"
    $batch = @"
@echo off
setlocal
call "$vcVars"
if errorlevel 1 exit /b %errorlevel%

cd /d "$opensslSource"
perl Configure VC-WIN64A no-shared no-tests no-asm --prefix="$opensslInstall" --openssldir="$opensslInstall\ssl"
if errorlevel 1 exit /b %errorlevel%
nmake clean
if errorlevel 1 exit /b %errorlevel%
nmake
if errorlevel 1 exit /b %errorlevel%
nmake install_sw
if errorlevel 1 exit /b %errorlevel%

cd /d "$sqlcipherSource"
nmake /f Makefile.msc clean
if errorlevel 1 exit /b %errorlevel%
nmake /f Makefile.msc sqlcipher.dll @clipensk-nmake-x64.args
if errorlevel 1 exit /b %errorlevel%

endlocal
"@
    Set-Content -Path $buildBatch -Value $batch -Encoding Ascii

    $nativeStdoutPath = Join-Path $OutputDirectory "native-cmd.stdout.log"
    $nativeStderrPath = Join-Path $OutputDirectory "native-cmd.stderr.log"
    $nativeCmdLogPath = Join-Path $OutputDirectory "native-cmd.log"
    $nativeProcess = Start-Process -FilePath "cmd.exe" `
        -ArgumentList @("/d", "/c", "`"$buildBatch`"") `
        -Wait `
        -PassThru `
        -NoNewWindow `
        -RedirectStandardOutput $nativeStdoutPath `
        -RedirectStandardError $nativeStderrPath

    @(
        "=== STDOUT ==="
        if (Test-Path $nativeStdoutPath) { Get-Content $nativeStdoutPath }
        "=== STDERR ==="
        if (Test-Path $nativeStderrPath) { Get-Content $nativeStderrPath }
    ) | Set-Content -Path $nativeCmdLogPath -Encoding UTF8

    Get-Content $nativeCmdLogPath | ForEach-Object { Write-Host $_ }
    if ($nativeProcess.ExitCode -ne 0) {
        throw "Native SQLCipher build failed with exit code $($nativeProcess.ExitCode). See native-cmd.log for compiler/linker output."
    }

    $sqlcipherDll = Join-Path $sqlcipherSource "sqlcipher.dll"
    $sqlcipherLib = Join-Path $sqlcipherSource "sqlcipher.lib"
    if (-not (Test-Path $sqlcipherDll)) {
        throw "SQLCipher build did not produce sqlcipher.dll."
    }
    if (-not (Test-Path $sqlcipherLib)) {
        throw "SQLCipher build did not produce sqlcipher.lib."
    }

    $dumpbinExports = & cmd.exe /d /c "call `"$vcVars`" >nul && dumpbin /exports `"$sqlcipherDll`""
    if ($LASTEXITCODE -ne 0) {
        throw "dumpbin /exports failed for sqlcipher.dll."
    }
    foreach ($requiredExport in @("sqlite3_open_v2", "sqlite3_key", "sqlite3_rekey")) {
        if ($dumpbinExports -notmatch [regex]::Escape($requiredExport)) {
            throw "sqlcipher.dll does not export required symbol $requiredExport."
        }
    }

    $dumpbinHeaders = & cmd.exe /d /c "call `"$vcVars`" >nul && dumpbin /headers `"$sqlcipherDll`""
    if ($LASTEXITCODE -ne 0) {
        throw "dumpbin /headers failed for sqlcipher.dll."
    }
    if ($dumpbinHeaders -notmatch "(?im)\b8664 machine \(x64\)") {
        throw "sqlcipher.dll is not an x64 PE image."
    }

    Copy-Item $sqlcipherDll (Join-Path $OutputDirectory "sqlcipher.dll") -Force
    Copy-Item $sqlcipherLib (Join-Path $OutputDirectory "sqlcipher.lib") -Force
    Copy-Item (Join-Path $sqlcipherSource "LICENSE.txt") (Join-Path $OutputDirectory "SQLCipher-LICENSE.txt") -Force
    Copy-Item (Join-Path $sqlcipherSource "SQLITE_LICENSE.md") (Join-Path $OutputDirectory "SQLite-LICENSE.md") -Force
    Copy-Item (Join-Path $opensslSource "LICENSE.txt") (Join-Path $OutputDirectory "OpenSSL-LICENSE.txt") -Force

    $dllSha256 = (Get-FileHash (Join-Path $OutputDirectory "sqlcipher.dll") -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = [ordered]@{
        architecture = "win-x64"
        sqlcipherVersion = $SqlCipherVersion
        sqlcipherCommit = $SqlCipherCommit
        opensslVersion = $OpenSslVersion
        opensslSourceSha256 = $OpenSslSha256
        sqlcipherDllSha256 = $dllSha256
        buildUtc = [DateTimeOffset]::UtcNow.ToString("O")
    }
    $manifest | ConvertTo-Json | Set-Content (Join-Path $OutputDirectory "native-build-manifest.json") -Encoding UTF8

    Write-Host "Native SQLCipher x64 build complete."
    Write-Host "sqlcipher.dll SHA-256: $dllSha256"
}
finally {
    Stop-Transcript | Out-Null
}
