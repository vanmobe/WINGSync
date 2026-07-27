[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
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

$nativeBuild = Join-Path $repoRoot 'build\native'
$artifactRoot = Join-Path $repoRoot 'artifacts'
$version = '1.0.0'
if ($Configuration -eq 'Release' -and -not $SkipTests) {
    $packageQualifier = '-internal-evaluation'
    $releaseStatus = 'internal-evaluation-unsigned'
}
else {
    $packageQualifier = "-$($Configuration.ToLowerInvariant())"
    if ($SkipTests) {
        $packageQualifier += '-untested'
    }
    $packageQualifier += '-internal-evaluation'
    $releaseStatus = 'internal-evaluation-unsigned'
}
$publishDir = Join-Path $artifactRoot "WingSync-win-x64$packageQualifier"
$packagePath = Join-Path $artifactRoot "WingSync-portable-win-x64$packageQualifier.zip"
$temporaryPackagePath = "$packagePath.tmp.zip"
$hashPath = "$packagePath.sha256"
$temporaryHashPath = "$hashPath.tmp"
$artifactRootFull = [System.IO.Path]::GetFullPath($artifactRoot).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$publishDirFull = [System.IO.Path]::GetFullPath($publishDir)
if (-not $publishDirFull.StartsWith($artifactRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish directory resolved outside the artifact directory.'
}
if (Test-Path -LiteralPath $publishDirFull) {
    Remove-Item -LiteralPath $publishDirFull -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
foreach ($staleArtifact in $packagePath, $temporaryPackagePath, $hashPath, $temporaryHashPath) {
    $resolvedArtifact = [System.IO.Path]::GetFullPath($staleArtifact)
    if (-not $resolvedArtifact.StartsWith(
            $artifactRootFull,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Package path resolved outside the artifact directory.'
    }

    if (Test-Path -LiteralPath $resolvedArtifact) {
        Remove-Item -LiteralPath $resolvedArtifact -Force
    }
}

$expectedVendorHashes = [ordered]@{
    'third_party\wapi\wapi.h' = 'FDDEC351A6CD6738CFF67683FC9FE814EBD99730FE01D2C5A1E3FD4E989082ED'
    'third_party\wapi\wext.h' = '53B5525B057E22AA97A4C0CD4F80A689D85A2072B20D85DFCAD21F4876846F93'
    'third_party\wapi\libwapi.a' = '1515C95105E29674976BD332A62B611A204471A3D529E7BF4546299D8985876D'
    'LICENSES\wapi-SLA.pdf' = '8748BD3DFA41FF08F1CF62FEF63670E04C8F3ED9CA4B2B5416C941420714FAFD'
}
foreach ($vendorFile in $expectedVendorHashes.GetEnumerator()) {
    $vendorPath = Join-Path $repoRoot $vendorFile.Key
    if (-not (Test-Path -LiteralPath $vendorPath -PathType Leaf)) {
        throw "Required vendored WAPI file is missing: $($vendorFile.Key)"
    }

    $actualVendorHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $vendorPath).Hash
    $hashMatches = $actualVendorHash.Equals(
        $vendorFile.Value,
        [System.StringComparison]::OrdinalIgnoreCase)
    if (-not $hashMatches -and $vendorFile.Key.EndsWith('.h', [System.StringComparison]::OrdinalIgnoreCase)) {
        $normalizedContent = (Get-Content -LiteralPath $vendorPath -Raw) -replace "`r`n|`r|`n", "`n"
        $normalizedHash = [System.Convert]::ToHexString(
            [System.Security.Cryptography.SHA256]::HashData(
                [System.Text.Encoding]::UTF8.GetBytes($normalizedContent)))
        $hashMatches = $normalizedHash.Equals(
            $vendorFile.Value,
            [System.StringComparison]::OrdinalIgnoreCase)
    }
    if (-not $hashMatches) {
        throw "Vendored WAPI file differs from its reviewed exact copy: $($vendorFile.Key)"
    }
}

$sourceCandidates = @(
    (Join-Path $repoRoot '.gitattributes'),
    (Join-Path $repoRoot 'Directory.Build.props'),
    (Join-Path $repoRoot 'global.json'),
    (Join-Path $repoRoot 'NuGet.Config'),
    (Join-Path $repoRoot 'README.md'),
    (Join-Path $repoRoot 'WingSync.slnx'),
    (Join-Path $repoRoot 'LICENSES'),
    (Join-Path $repoRoot 'docs'),
    (Join-Path $repoRoot 'src'),
    (Join-Path $repoRoot 'native'),
    (Join-Path $repoRoot 'scripts'),
    (Join-Path $repoRoot 'tests'),
    (Join-Path $repoRoot 'third_party\wapi')
)
function Get-SourceDigest {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][object[]]$Candidates
    )

    $sourceFiles = foreach ($candidate in $Candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            Get-Item -LiteralPath $candidate
        }
        elseif (Test-Path -LiteralPath $candidate -PathType Container) {
            Get-ChildItem -LiteralPath $candidate -Recurse -File |
                Where-Object {
                    $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and
                    $_.FullName -notmatch '[\\/]live-recovery[\\/]'
                }
        }
    }
    $sourceEntries = $sourceFiles |
        Sort-Object FullName -Unique |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($Root.Length).
                TrimStart('\').Replace('\', '/')
            $fileHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.
                ToLowerInvariant()
            "$relativePath`t$fileHash"
        }
    $sourceIndex = [System.Text.Encoding]::UTF8.GetBytes(
        [string]::Join("`n", $sourceEntries))
    $sourceHasher = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString(
            $sourceHasher.ComputeHash($sourceIndex)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sourceHasher.Dispose()
    }
}

$sourceDigest = Get-SourceDigest -Root $repoRoot -Candidates $sourceCandidates
$sourceShort = $sourceDigest.Substring(0, 12)
$informationalVersion = "$version+source.$sourceShort"
$sourceRevision = "uncommitted:$sourceDigest"
try {
    $gitRevision = (& git -C $repoRoot rev-parse --verify HEAD 2>$null |
            Select-Object -First 1)
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($gitRevision)) {
        $gitDirty = (& git -C $repoRoot status --porcelain 2>$null |
                Measure-Object).Count -gt 0
        $sourceRevision = if ($gitDirty) {
            "${gitRevision}-dirty:$sourceDigest"
        }
        else {
            $gitRevision
        }
    }
}
catch {
    # The content digest remains authoritative when Git metadata is unavailable.
}

Write-Host 'Building the isolated WAPI helper...'
cmake --fresh -S (Join-Path $repoRoot 'native') -B $nativeBuild -G 'Visual Studio 17 2022' -A x64
Assert-LastExitCode 'Native configure'
cmake --build $nativeBuild --config $Configuration --parallel
Assert-LastExitCode 'Native build'

Write-Host 'Restoring and building managed projects...'
& $dotnet restore (Join-Path $repoRoot 'WingSync.slnx') --configfile $nugetConfig
Assert-LastExitCode 'Managed restore'
& $dotnet build (Join-Path $repoRoot 'WingSync.slnx') `
    -c $Configuration `
    --no-restore `
    -p:Version=$version `
    -p:InformationalVersion=$informationalVersion
Assert-LastExitCode 'Managed build'

if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'test.ps1') -Configuration $Configuration -SkipBuild
    Assert-LastExitCode 'Test suites'
}

Write-Host 'Publishing the self-contained Windows application...'
& $dotnet restore (Join-Path $repoRoot 'src\WingSync.App\WingSync.App.csproj') `
    -r win-x64 `
    --source 'https://api.nuget.org/v3/index.json'
Assert-LastExitCode 'Windows runtime-pack restore'
& $dotnet publish (Join-Path $repoRoot 'src\WingSync.App\WingSync.App.csproj') `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$version `
    -p:InformationalVersion=$informationalVersion `
    -o $publishDir
Assert-LastExitCode 'Self-contained publish'

$helperCandidates = @(
    (Join-Path $nativeBuild "$Configuration\WingSync.WapiHost.exe"),
    (Join-Path $nativeBuild "WapiHost\$Configuration\WingSync.WapiHost.exe")
)
$helper = $helperCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $helper) {
    throw 'WingSync.WapiHost.exe was not produced by the native build.'
}

Copy-Item -LiteralPath $helper -Destination (Join-Path $publishDir 'WingSync.WapiHost.exe') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $publishDir 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\USER_GUIDE.md') -Destination (Join-Path $publishDir 'USER_GUIDE.md') -Force
$publishedDocs = Join-Path $publishDir 'docs'
New-Item -ItemType Directory -Force -Path $publishedDocs | Out-Null
foreach ($documentName in 'USER_GUIDE.md', 'ARCHITECTURE.md', 'TEST_REPORT.md') {
    Copy-Item -LiteralPath (Join-Path $repoRoot "docs\$documentName") `
        -Destination (Join-Path $publishedDocs $documentName) -Force
}
New-Item -ItemType Directory -Force -Path (Join-Path $publishDir 'LICENSES') | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSES\wapi-SLA.pdf') `
    -Destination (Join-Path $publishDir 'LICENSES\wapi-SLA.pdf') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSES\README.md') `
    -Destination (Join-Path $publishDir 'LICENSES\README.md') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'third_party\wapi\NOTICE.md') `
    -Destination (Join-Path $publishDir 'LICENSES\wapi-NOTICE.md') -Force

$dotnetRoot = Split-Path -Parent $dotnet
$dotnetLicense = Join-Path $dotnetRoot 'LICENSE.txt'
$dotnetNotices = Join-Path $dotnetRoot 'ThirdPartyNotices.txt'
if (-not (Test-Path -LiteralPath $dotnetLicense) -or
    -not (Test-Path -LiteralPath $dotnetNotices)) {
    throw 'The .NET runtime licence or third-party notices are missing from the build installation.'
}
$appProjectPath = Join-Path $repoRoot 'src\WingSync.App\WingSync.App.csproj'
[xml]$appProjectXml = Get-Content -LiteralPath $appProjectPath -Raw
$runtimeVersion = [string]($appProjectXml.Project.PropertyGroup.RuntimeFrameworkVersion |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
        Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($runtimeVersion)) {
    throw 'WingSync.App.csproj must pin RuntimeFrameworkVersion for a reproducible package.'
}
$requiredSharedFrameworks = @(
    (Join-Path $dotnetRoot "shared\Microsoft.NETCore.App\$runtimeVersion"),
    (Join-Path $dotnetRoot "shared\Microsoft.WindowsDesktop.App\$runtimeVersion")
)
$missingSharedFrameworks = $requiredSharedFrameworks |
    Where-Object { -not (Test-Path -LiteralPath $_ -PathType Container) }
if ($missingSharedFrameworks) {
    throw "The pinned .NET runtime $runtimeVersion is not installed: $($missingSharedFrameworks -join ', ')"
}
Copy-Item -LiteralPath $dotnetLicense `
    -Destination (Join-Path $publishDir "LICENSES\dotnet-$runtimeVersion-LICENSE.txt") -Force
Copy-Item -LiteralPath $dotnetNotices `
    -Destination (Join-Path $publishDir "LICENSES\dotnet-$runtimeVersion-ThirdPartyNotices.txt") -Force

Write-Host 'Validating the exact published bits...'
$publishedApp = Join-Path $publishDir 'WingSync.exe'
$publishedHelper = Join-Path $publishDir 'WingSync.WapiHost.exe'
$requiredPublishedFiles = @(
    $publishedApp,
    $publishedHelper,
    (Join-Path $publishDir 'LICENSES\wapi-SLA.pdf'),
    (Join-Path $publishDir "LICENSES\dotnet-$runtimeVersion-LICENSE.txt"),
    (Join-Path $publishDir "LICENSES\dotnet-$runtimeVersion-ThirdPartyNotices.txt")
)
$missingPublishedFiles = $requiredPublishedFiles |
    Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }
if ($missingPublishedFiles) {
    throw "Published package is incomplete: $($missingPublishedFiles -join ', ')"
}

& $publishedHelper --self-test
Assert-LastExitCode 'Published native helper self-test'

$validationRunId = "$sourceShort-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$validationArtifacts = Join-Path $artifactRoot "package-validation\$validationRunId"
if (-not $SkipTests) {
    Write-Host 'Running the complete UI suite against the exact published application...'
    & $dotnet run --project (Join-Path $repoRoot 'tests\WingSync.UiTests') `
        -c $Configuration --no-build -- `
        --app $publishedApp `
        --artifacts (Join-Path $validationArtifacts 'published-ui')
    Assert-LastExitCode 'Published application UI automation'
}
else {
    $smokeProcess = Start-Process -FilePath $publishedApp `
        -ArgumentList '--demo' `
        -PassThru `
        -WindowStyle Hidden
    try {
        Start-Sleep -Seconds 5
        if ($smokeProcess.HasExited) {
            throw "Published WingSync demo exited during smoke test with code $($smokeProcess.ExitCode)."
        }
    }
    finally {
        if (-not $smokeProcess.HasExited) {
            Stop-Process -Id $smokeProcess.Id -Force
            $smokeProcess.WaitForExit()
        }
    }
}

$finalSourceDigest = Get-SourceDigest -Root $repoRoot -Candidates $sourceCandidates
if (-not $finalSourceDigest.Equals(
        $sourceDigest,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Source files changed after the tested build started; packaging is refused.'
}

$appSignatureStatus = (Get-AuthenticodeSignature -LiteralPath $publishedApp).Status.ToString()
$helperSignatureStatus = (Get-AuthenticodeSignature -LiteralPath $publishedHelper).Status.ToString()
$suiteStatus = if ($SkipTests) { 'not-run' } else { 'passed' }
$buildManifest = [ordered]@{
    schemaVersion = 2
    product = 'WingSync'
    version = $version
    informationalVersion = $informationalVersion
    buildId = "wingsync-$version-$sourceShort"
    builtAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    sourceRevision = $sourceRevision
    sourceSha256 = $sourceDigest
    configuration = $Configuration
    releaseStatus = $releaseStatus
    runtimeIdentifier = 'win-x64'
    selfContained = $true
    targetFramework = 'net10.0-windows'
    dotnetRuntimeVersion = $runtimeVersion
    windowsDesktopRuntimeVersion = $runtimeVersion
    wapiProtocol = '3.1'
    authenticode = [ordered]@{
        application = $appSignatureStatus
        helper = $helperSignatureStatus
    }
    testEvidence = [ordered]@{
        core = $suiteStatus
        integrationAndFaultInjection = $suiteStatus
        buildOutputUiAutomation = $suiteStatus
        publishedUiAutomation = $suiteStatus
        publishedNativeSelfTest = 'passed'
        extractedPackageUiAutomation = if ($SkipTests) { 'not-run' } else { 'passed' }
        physicalHardwareAcceptance = 'external-not-embedded'
    }
}
$buildManifest | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath (Join-Path $publishDir 'BUILD-MANIFEST.json') -Encoding UTF8

$inventoryPath = Join-Path $publishDir 'PACKAGE-CONTENTS.sha256'
$inventoryEntries = Get-ChildItem -LiteralPath $publishDir -Recurse -File |
    Where-Object { $_.FullName -ne $inventoryPath } |
    Sort-Object FullName |
    ForEach-Object {
        $relativePath = $_.FullName.Substring($publishDirFull.Length).
            TrimStart('\').Replace('\', '/')
        $fileHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.
            ToLowerInvariant()
        "$fileHash  $relativePath"
    }
$inventoryEntries | Set-Content -LiteralPath $inventoryPath -Encoding ASCII

$oversizedFiles = Get-ChildItem -LiteralPath $publishDir -Recurse -File |
    Where-Object { $_.Length -ge 2GB }
if ($oversizedFiles) {
    throw "Compress-Archive cannot safely package files of 2 GB or larger: $($oversizedFiles.FullName -join ', ')"
}

Write-Host 'Packaging portable build...'
Compress-Archive -Path (Join-Path $publishDir '*') `
    -DestinationPath $temporaryPackagePath -CompressionLevel Optimal

$verificationRoot = Join-Path $artifactRoot "package-extract-$validationRunId"
$verificationRootFull = [System.IO.Path]::GetFullPath($verificationRoot)
if (-not $verificationRootFull.StartsWith(
        $artifactRootFull,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package verification directory resolved outside the artifact directory.'
}
try {
    Expand-Archive -LiteralPath $temporaryPackagePath -DestinationPath $verificationRootFull
    $extractedInventory = Join-Path $verificationRootFull 'PACKAGE-CONTENTS.sha256'
    if (-not (Test-Path -LiteralPath $extractedInventory -PathType Leaf)) {
        throw 'The extracted package has no per-file hash inventory.'
    }

    foreach ($inventoryLine in Get-Content -LiteralPath $extractedInventory) {
        if ($inventoryLine -notmatch '^([0-9a-f]{64})  (.+)$') {
            throw "Invalid package inventory line: $inventoryLine"
        }

        $expectedHash = $Matches[1]
        $relativePath = $Matches[2].Replace('/', '\')
        $extractedPath = [System.IO.Path]::GetFullPath(
            (Join-Path $verificationRootFull $relativePath))
        $verificationPrefix = $verificationRootFull.TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar) +
            [System.IO.Path]::DirectorySeparatorChar
        if (-not $extractedPath.StartsWith(
                $verificationPrefix,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $extractedPath -PathType Leaf)) {
            throw "Package inventory path is missing or unsafe: $relativePath"
        }

        $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $extractedPath).Hash
        if (-not $actualHash.Equals(
                $expectedHash,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Extracted package hash mismatch: $relativePath"
        }
    }

    $extractedHelper = Join-Path $verificationRootFull 'WingSync.WapiHost.exe'
    $extractedApp = Join-Path $verificationRootFull 'WingSync.exe'
    & $extractedHelper --self-test
    Assert-LastExitCode 'Extracted native helper self-test'
    if (-not $SkipTests) {
        & $dotnet run --project (Join-Path $repoRoot 'tests\WingSync.UiTests') `
            -c $Configuration --no-build -- `
            --app $extractedApp `
            --artifacts (Join-Path $validationArtifacts 'extracted-ui')
        Assert-LastExitCode 'Extracted application UI automation'
    }
}
finally {
    if (Test-Path -LiteralPath $verificationRootFull) {
        Remove-Item -LiteralPath $verificationRootFull -Recurse -Force
    }
}

$hash = Get-FileHash -Algorithm SHA256 -LiteralPath $temporaryPackagePath
"$($hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($packagePath))" |
    Set-Content -LiteralPath $temporaryHashPath -Encoding ASCII
Move-Item -LiteralPath $temporaryPackagePath -Destination $packagePath
Move-Item -LiteralPath $temporaryHashPath -Destination $hashPath
Write-Host "Package: $packagePath"
Write-Host "SHA256:  $($hash.Hash)"
