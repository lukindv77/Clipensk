param(
    [string]$OutputDirectory = "artifacts/app-startup/win-x64",
    [int]$StartupSeconds = 25
)

# Starts the published unpackaged Clipensk.App.exe the way a user does and requires it to stay up
# with its window shown: nothing else in CI ever launches the WinUI host, so a crash while loading
# XAML or in the startup sequence would otherwise only be found on a user's machine.
#
# The Windows App Runtime the app binds to is installed from the MSIX packages of the
# Microsoft.WindowsAppSDK.Runtime package that restore put in the NuGet cache, so the check runs
# against the same framework-dependent layout that eng/publish-windows-x64.ps1 delivers.

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$outputDirectoryPath = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
$appProjectPath = Join-Path $repositoryRoot "src/Clipensk.App/Clipensk.App.csproj"

$osVersion = [Environment]::OSVersion.Version
Write-Host "Windows: $osVersion"
if ($osVersion.Build -lt 22000) {
    throw "The startup check needs a Windows 11 class build (22000 or later): on an older one Clipensk shows its unsupported-Windows warning first."
}

Remove-Item -LiteralPath $outputDirectoryPath -Recurse -Force -ErrorAction SilentlyContinue
& dotnet publish $appProjectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained false `
    -p:Platform=x64 `
    --output $outputDirectoryPath
if ($LASTEXITCODE -ne 0) {
    throw "Clipensk.App publish failed with exit code $LASTEXITCODE."
}

Write-Host "Published files:"
Get-ChildItem -LiteralPath $outputDirectoryPath -File |
    Sort-Object Name |
    ForEach-Object { Write-Host ("  {0} ({1} bytes)" -f $_.Name, $_.Length) }

# The Windows App Runtime that matches the referenced Microsoft.WindowsAppSDK.Runtime version.
$globalPackages = (& dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', ''
$runtimePackageRoot = Join-Path $globalPackages.Trim() "microsoft.windowsappsdk.runtime"
$runtimePackage = Get-ChildItem -LiteralPath $runtimePackageRoot -Directory |
    Sort-Object { [version]($_.Name -replace '-.*$', '') } -Descending |
    Select-Object -First 1
if ($null -eq $runtimePackage) {
    throw "Microsoft.WindowsAppSDK.Runtime was not found in $runtimePackageRoot."
}

$msixDirectory = Join-Path $runtimePackage.FullName "tools/MSIX/win10-x64"
Write-Host "Windows App Runtime packages: $msixDirectory"
foreach ($name in @(
    "Microsoft.WindowsAppRuntime.2.msix",
    "Microsoft.WindowsAppRuntime.Main.2.msix",
    "Microsoft.WindowsAppRuntime.Singleton.2.msix",
    "Microsoft.WindowsAppRuntime.DDLM.2.msix"
)) {
    $path = Join-Path $msixDirectory $name
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Windows App Runtime package $name not found in $msixDirectory."
    }

    try {
        Add-AppxPackage -Path $path -ForceApplicationShutdown -ErrorAction Stop
        Write-Host "  installed $name"
    }
    catch {
        # A newer version of the same package already present is enough.
        Write-Host "  ${name}: $($_.Exception.Message)"
    }
}

Get-AppxPackage -Name "Microsoft.WindowsAppRuntime*" |
    ForEach-Object { Write-Host ("  present: {0} {1}" -f $_.Name, $_.Version) }

$errorLogPath = Join-Path $env:LOCALAPPDATA "Clipensk\error.log"
Remove-Item -LiteralPath $errorLogPath -Force -ErrorAction SilentlyContinue

$exePath = Join-Path $outputDirectoryPath "Clipensk.App.exe"
$started = Get-Date
$process = Start-Process -FilePath $exePath -WorkingDirectory $outputDirectoryPath -PassThru
$null = $process.Handle
$deadline = $started.AddSeconds($StartupSeconds)
$windowTitle = ""
while ((Get-Date) -lt $deadline -and -not $process.HasExited) {
    Start-Sleep -Milliseconds 500
    $process.Refresh()
    if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
        $windowTitle = $process.MainWindowTitle
    }
}

$alive = -not $process.HasExited
if ($alive) {
    Stop-Process -Id $process.Id -Force
}

Start-Sleep -Seconds 3
$crashEvents = @(
    Get-WinEvent -FilterHashtable @{ LogName = "Application"; StartTime = $started } -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ProviderName -in @("Application Error", ".NET Runtime", "Windows Error Reporting") -and
            $_.Message -like "*Clipensk.App*"
        }
)
foreach ($crashEvent in $crashEvents) {
    Write-Host "---- $($crashEvent.ProviderName) event $($crashEvent.Id)"
    Write-Host $crashEvent.Message
}

$errorLogExists = Test-Path -LiteralPath $errorLogPath
if ($errorLogExists) {
    Write-Host "---- $errorLogPath"
    Get-Content -LiteralPath $errorLogPath | ForEach-Object { Write-Host $_ }
}

if (-not $alive) {
    throw "Clipensk.App.exe exited during startup (exit code $($process.ExitCode))."
}
if ($crashEvents.Count -gt 0 -or $errorLogExists) {
    throw "Clipensk.App.exe reported an error during startup."
}
if ([string]::IsNullOrWhiteSpace($windowTitle)) {
    throw "Clipensk.App.exe kept running for $StartupSeconds s but never showed a window."
}

Write-Host "App startup check PASS: window '$windowTitle' stayed up for $StartupSeconds s."
