[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Tag,

    [Parameter(Mandatory)]
    [ValidatePattern('^CN=')]
    [string]$Publisher,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9.-]{3,50}$')]
    [string]$PackageIdentityName,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$CertificateThumbprint,

    [string]$Configuration = 'Release',

    [string]$OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\release-candidate'),

    [string]$TimestampUrl,

    [switch]$AllowUnsignedTimestamp,
    [switch]$SkipWack
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$releaseRoot = Join-Path $OutputRoot $Tag.TrimStart('v')
$backupPath = Join-Path $releaseRoot 'Package.appxmanifest.original'
$sbomPath = Join-Path $releaseRoot 'sbom.json'
$fingerprintPath = Join-Path $releaseRoot 'toolchain-fingerprint.json'

function Assert-ProtectedSigningEnvironment {
    if ($env:GITHUB_ACTIONS -ne 'true') {
        throw 'Protected release execution is restricted to GitHub Actions.'
    }

    if ($env:ELLIEPDF_RUNNER_ENVIRONMENT -ne 'self-hosted') {
        throw "Protected release execution requires a self-hosted runner. ELLIEPDF_RUNNER_ENVIRONMENT='$($env:ELLIEPDF_RUNNER_ENVIRONMENT)'."
    }

    if ($env:ELLIEPDF_SIGNING_EPHEMERAL_RUNNER -ne '1') {
        throw 'Protected release execution requires an ephemeral signing runner (ELLIEPDF_SIGNING_EPHEMERAL_RUNNER=1).'
    }

    if ($env:ELLIEPDF_RELEASE_SIGNING -ne '1') {
        throw 'Protected release execution requires ELLIEPDF_RELEASE_SIGNING=1.'
    }

    if ($env:ELLIEPDF_RELEASE_ENVIRONMENT -ne 'release-signing') {
        throw "Protected release execution requires ELLIEPDF_RELEASE_ENVIRONMENT=release-signing. Current value: '$($env:ELLIEPDF_RELEASE_ENVIRONMENT)'."
    }

    if ($env:GITHUB_EVENT_NAME -notin @('push', 'workflow_dispatch')) {
        throw "Protected release execution only accepts tag push or workflow_dispatch events. Current event: '$($env:GITHUB_EVENT_NAME)'."
    }

    if ($env:GITHUB_EVENT_NAME -eq 'push' -and
        ($env:GITHUB_REF -ne "refs/tags/$Tag" -or $env:GITHUB_REF_NAME -ne $Tag)) {
        throw "Tag-push ref '$($env:GITHUB_REF)' does not match the requested tag '$Tag'."
    }

    $expectedWorkflowRefSuffix = if ($env:GITHUB_EVENT_NAME -eq 'push') {
        ".github/workflows/release-signing.yml@refs/tags/$Tag"
    } else {
        '.github/workflows/release-signing.yml@refs/heads/master'
    }
    if ([string]::IsNullOrWhiteSpace($env:GITHUB_WORKFLOW_REF) -or
        -not $env:GITHUB_WORKFLOW_REF.EndsWith($expectedWorkflowRefSuffix, [StringComparison]::Ordinal)) {
        throw "Release workflow was not loaded from the trusted ref: '$($env:GITHUB_WORKFLOW_REF)'."
    }
}

function Get-GitValue([string[]]$Arguments, [string]$FailureMessage) {
    $value = & git @Arguments 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($value -join ''))) {
        throw $FailureMessage
    }

    return ($value -join '').Trim()
}

function Invoke-Dotnet([string[]]$Arguments, [string]$FailureMessage) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FailureMessage Exit code: $LASTEXITCODE."
    }
}

function Get-BuildNumber {
    if ($env:GITHUB_RUN_NUMBER -match '^\d+$') {
        return [Math]::Min([int]$env:GITHUB_RUN_NUMBER, 65535)
    }

    return 0
}

function Get-SinglePackage([string]$DirectoryPath, [string]$Rid) {
    $packages = @(Get-ChildItem -LiteralPath $DirectoryPath -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -eq '.msix' })
    if ($packages.Count -ne 1) {
        throw "Expected exactly one package for $Rid in $DirectoryPath, found $($packages.Count)."
    }

    return $packages[0].FullName
}

if (Test-Path -LiteralPath $releaseRoot) {
    $staleEntries = @(Get-ChildItem -LiteralPath $releaseRoot -Force -ErrorAction Stop)
    if ($staleEntries.Count -gt 0) {
        throw "Refusing to mix release evidence with an existing non-empty directory: $releaseRoot"
    }
}
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
Assert-ProtectedSigningEnvironment

$headCommit = Get-GitValue -Arguments @('rev-parse', '--verify', 'HEAD') -FailureMessage 'Could not resolve the checked-out commit.'
$tagCommit = Get-GitValue -Arguments @('rev-parse', '--verify', "refs/tags/$Tag^{commit}") -FailureMessage "The requested release tag '$Tag' does not resolve to a commit."
if ($headCommit -cne $tagCommit) {
    throw "Checked-out commit '$headCommit' does not match release tag '$Tag' at '$tagCommit'."
}

$env:ELLIEPDF_SOURCE_COMMIT = $headCommit
$env:ELLIEPDF_SOURCE_REF = "refs/tags/$Tag"

$packageVersion = & (Join-Path $PSScriptRoot 'Get-MsixVersion.ps1') -Tag $Tag -Build (Get-BuildNumber)

Invoke-Dotnet -Arguments @('restore', 'ElliePdf.slnx', '--locked-mode') -FailureMessage 'Locked restore failed.'

$toolManifest = Join-Path $repoRoot '.config\dotnet-tools.json'
if (Test-Path -LiteralPath $toolManifest -PathType Leaf) {
    Invoke-Dotnet -Arguments @('tool', 'restore') -FailureMessage 'Local tool restore failed.'
}

& (Join-Path $PSScriptRoot 'Verify-PdfiumNative.ps1')

& (Join-Path $PSScriptRoot 'Generate-Sbom.ps1') -OutputPath $sbomPath

& (Join-Path $PSScriptRoot 'Test-ReleaseEvidence.ps1') -SbomPath $sbomPath

& (Join-Path $PSScriptRoot 'Get-ToolchainFingerprint.ps1') -OutputPath $fingerprintPath

Invoke-Dotnet -Arguments @(
    'test',
    'tests\ElliePdf.PackagingTests\ElliePdf.PackagingTests.csproj',
    '-c', $Configuration,
    '--no-restore'
) -FailureMessage 'Packaging contract tests failed.'

try {
    & (Join-Path $PSScriptRoot 'Set-ManifestPublisher.ps1') -ManifestPath (Join-Path $repoRoot 'Package.appxmanifest') -Publisher $Publisher -BackupPath $backupPath

    foreach ($entry in @(
        @{ Platform = 'x64'; RuntimeIdentifier = 'win-x64'; ExpectedArchitecture = 'x64' },
        @{ Platform = 'ARM64'; RuntimeIdentifier = 'win-arm64'; ExpectedArchitecture = 'arm64' })) {
        & (Join-Path $PSScriptRoot 'Publish-ReleaseArtifacts.ps1') `
            -Platform $entry.Platform `
            -RuntimeIdentifier $entry.RuntimeIdentifier `
            -Configuration $Configuration `
            -PackageVersion $packageVersion `
            -Package

        & (Join-Path $PSScriptRoot 'Test-SourceLink.ps1') `
            -Configuration $Configuration `
            -SymbolsPath (Join-Path $repoRoot "artifacts\symbols\$($entry.RuntimeIdentifier)")

        $sourceDirectory = Join-Path $repoRoot "artifacts\package\$($entry.RuntimeIdentifier)"
        $unsignedDirectory = Join-Path $releaseRoot "$($entry.RuntimeIdentifier)\unsigned"
        New-Item -ItemType Directory -Force -Path $unsignedDirectory | Out-Null

        $sourcePackage = Get-SinglePackage -DirectoryPath $sourceDirectory -Rid $entry.RuntimeIdentifier
        $unsignedPackage = Join-Path $unsignedDirectory ([System.IO.Path]::GetFileName($sourcePackage))
        Copy-Item -LiteralPath $sourcePackage -Destination $unsignedPackage -Force

        & (Join-Path $PSScriptRoot 'Test-MsixPayload.ps1') -PackagePath $unsignedPackage -ExpectedArchitecture $entry.ExpectedArchitecture

        $signingResult = & (Join-Path $PSScriptRoot 'Sign-ReleasePackage.ps1') `
            -PackagePath $unsignedPackage `
            -Publisher $Publisher `
            -PackageIdentityName $PackageIdentityName `
            -CertificateThumbprint $CertificateThumbprint `
            -ExpectedArchitecture $entry.ExpectedArchitecture `
            -OutputRoot (Join-Path $releaseRoot $entry.RuntimeIdentifier) `
            -SbomPath $sbomPath `
            -ToolchainFingerprintPath $fingerprintPath `
            -Tag $Tag `
            -PackageVersion $packageVersion `
            -TimestampUrl $TimestampUrl `
            -AllowUnsignedTimestamp:$AllowUnsignedTimestamp

        if (-not $SkipWack) {
            & (Join-Path $PSScriptRoot 'Invoke-Wack.ps1') -PackagePath $signingResult.SignedPackagePath -Execute
        }
    }
}
finally {
    if (Test-Path -LiteralPath $backupPath -PathType Leaf) {
        & (Join-Path $PSScriptRoot 'Set-ManifestPublisher.ps1') -RestoreFrom $backupPath -RestorePath (Join-Path $repoRoot 'Package.appxmanifest')
    }
}

Write-Host "Protected release bundle ready: $releaseRoot"
