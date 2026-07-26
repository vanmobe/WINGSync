[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$SkipBuild,
    [switch]$LiveReadOnly,
    [switch]$LiveWrites,
    [switch]$AcknowledgeLiveWrites,
    [string]$FohSerial = '',
    [string]$StageSerial = ''
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$repoRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) {
    $localDotnet
} else {
    (Get-Command dotnet.exe -ErrorAction Stop).Source
}
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools\dotnet-home'
$env:APPDATA = Join-Path $repoRoot '.tools\appdata'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.packages'
$nugetConfig = Join-Path $repoRoot 'NuGet.Config'

function Assert-LastExitCode {
    param([Parameter(Mandatory)][string]$Step)
    if ($LASTEXITCODE -ne 0) {
        throw "$Step failed with exit code $LASTEXITCODE."
    }
}

if ($LiveReadOnly -and $LiveWrites) {
    throw 'Choose either -LiveReadOnly or -LiveWrites, not both.'
}

if (-not $SkipBuild) {
    & $dotnet restore (Join-Path $repoRoot 'WingSync.slnx') --configfile $nugetConfig
    Assert-LastExitCode 'Managed restore'
    & $dotnet build (Join-Path $repoRoot 'WingSync.slnx') -c $Configuration --no-restore
    Assert-LastExitCode 'Managed build'
    cmake --fresh -S (Join-Path $repoRoot 'native') -B (Join-Path $repoRoot 'build\native') `
        -G 'Visual Studio 17 2022' -A x64
    Assert-LastExitCode 'Native configure'
    cmake --build (Join-Path $repoRoot 'build\native') --config $Configuration --parallel
    Assert-LastExitCode 'Native build'
}

Write-Host 'Running core tests...'
& $dotnet run --project (Join-Path $repoRoot 'tests\WingSync.Core.Tests') `
    -c $Configuration --no-build
Assert-LastExitCode 'Core tests'

Write-Host 'Running integration and fault-injection tests...'
& $dotnet run --project (Join-Path $repoRoot 'tests\WingSync.Integration.Tests') `
    -c $Configuration --no-build
Assert-LastExitCode 'Integration tests'

if ($LiveWrites -or $LiveReadOnly) {
    if ([string]::IsNullOrWhiteSpace($FohSerial) -or [string]::IsNullOrWhiteSpace($StageSerial)) {
        throw 'Live hardware tests require both -FohSerial and -StageSerial.'
    }
}

if ($LiveWrites) {
    if (-not $AcknowledgeLiveWrites) {
        throw 'Live writes also require -AcknowledgeLiveWrites.'
    }

    Write-Host 'Running explicitly acknowledged live hardware write-and-restore test...'
    & $dotnet run --project (Join-Path $repoRoot 'tests\WingSync.Integration.Tests') `
        -c $Configuration --no-build -- `
        --live-writes --i-understand-live-writes `
        --foh-serial $FohSerial --stage-serial $StageSerial
    Assert-LastExitCode 'Live hardware write-and-restore test'
}
elseif ($LiveReadOnly) {
    Write-Host 'Running live hardware read-only test...'
    & $dotnet run --project (Join-Path $repoRoot 'tests\WingSync.Integration.Tests') `
        -c $Configuration --no-build -- `
        --live-read-only --foh-serial $FohSerial --stage-serial $StageSerial
    Assert-LastExitCode 'Live hardware read-only test'
}

$nativeCandidates = @(
    (Join-Path $repoRoot "build\native\$Configuration\WingSync.WapiHost.exe"),
    (Join-Path $repoRoot "build\native\WapiHost\$Configuration\WingSync.WapiHost.exe")
)
$nativeHost = $nativeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $nativeHost) {
    throw "Native helper is missing for $Configuration; the test run cannot be certified."
}

Write-Host 'Running native helper self-test...'
& $nativeHost --self-test
if ($LASTEXITCODE -ne 0) {
    throw "Native helper self-test failed with exit code $LASTEXITCODE."
}

Write-Host 'Running UI automation tests...'
& $dotnet run --project (Join-Path $repoRoot 'tests\WingSync.UiTests') `
    -c $Configuration --no-build -- `
    --app (Join-Path $repoRoot "src\WingSync.App\bin\$Configuration\net10.0-windows\WingSync.exe")
Assert-LastExitCode 'UI automation tests'

Write-Host 'All requested test suites passed.'
