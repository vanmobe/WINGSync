[CmdletBinding()]
param(
    [switch]$Demo,
    [string]$DataDirectory = ''
)

$ErrorActionPreference = 'Stop'
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
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$arguments = @('run', '--project', (Join-Path $repoRoot 'src\WingSync.App'), '-c', 'Debug')
$appArguments = @()
if ($Demo) {
    $appArguments += '--demo'
}
if (-not [string]::IsNullOrWhiteSpace($DataDirectory)) {
    $appArguments += @('--data-dir', $DataDirectory)
}
if ($appArguments.Count -gt 0) {
    $arguments += '--'
    $arguments += $appArguments
}
& $dotnet @arguments
if ($LASTEXITCODE -ne 0) {
    throw "WingSync exited with code $LASTEXITCODE."
}
