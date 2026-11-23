[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')][string]$Tag,
    [Parameter(Mandatory)][ValidateSet('x64','arm64')][string]$Architecture,
    [string]$OutputDirectory = 'artifacts/uia',
    [switch]$Execute
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$sourceCommit = (& git -C $repo rev-parse --verify HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'Unable to resolve the checked-out source commit.'
}
$checkoutTagCommit = (& git -C $repo rev-parse --verify "refs/tags/$Tag^{commit}").Trim()
if ($LASTEXITCODE -ne 0 -or $checkoutTagCommit -cne $sourceCommit) {
    throw 'The checkout must match the requested release tag.'
}

$rid = "win-$Architecture"
$root = (Resolve-Path -LiteralPath $ArtifactRoot).ProviderPath
$bundle = Join-Path $root ('artifacts/release-candidate/' + $Tag.Substring(1))
$architectureRoot = Join-Path $bundle $rid
$records = @(Get-ChildItem -LiteralPath (Join-Path $architectureRoot 'records') -Filter '*.checksums.json' -File)
$packages = @(Get-ChildItem -LiteralPath (Join-Path $architectureRoot 'signed') -Filter '*.msix' -File)
if ($records.Count -ne 1 -or $packages.Count -ne 1) {
    throw "Expected one checksum record and one signed MSIX for $Architecture."
}
$package = $packages[0]
$recordFile = $records[0]
$recordBytes = [IO.File]::ReadAllBytes($recordFile.FullName)
$record = [Text.Encoding]::UTF8.GetString($recordBytes) | ConvertFrom-Json
if ($record.tag -cne $Tag -or $record.provenance.refName -cne $Tag -or
    $record.provenance.repository -cne $env:GITHUB_REPOSITORY -or
    $record.provenance.runId -cne $env:GITHUB_RUN_ID -or
    $record.provenance.runAttempt -cne $env:GITHUB_RUN_ATTEMPT -or
    $record.provenance.sha -cne $sourceCommit -or
    [string]::IsNullOrWhiteSpace($env:GITHUB_RUN_ID) -or
    [string]::IsNullOrWhiteSpace($env:GITHUB_RUN_ATTEMPT) -or
    [string]::IsNullOrWhiteSpace($env:GITHUB_REPOSITORY)) {
    throw 'The signed record does not match this release workflow run, attempt and source.'
}
$packageHash = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash
if ($record.architecture -cne $Architecture -or
    [IO.Path]::GetFileName($record.signedPackage.path) -cne $package.Name -or
    $record.signedPackage.length -ne $package.Length -or
    $record.signedPackage.sha256 -ine $packageHash) {
    throw 'Package filename, length, architecture or hash does not match the signed record.'
}
if ((Get-FileHash -LiteralPath (Join-Path $bundle 'sbom.json') -Algorithm SHA256).Hash -ine $record.sbom.sha256 -or
    (Get-FileHash -LiteralPath (Join-Path $bundle 'toolchain-fingerprint.json') -Algorithm SHA256).Hash -ine $record.toolchainFingerprint.sha256) {
    throw 'SBOM or toolchain fingerprint does not match the signed record.'
}

$packageSignature = Get-AuthenticodeSignature -LiteralPath $package.FullName
if ($packageSignature.Status -ne 'Valid' -or $null -eq $packageSignature.SignerCertificate -or
    $packageSignature.SignerCertificate.Thumbprint -ine $record.signingCertificate.thumbprint -or
    $packageSignature.SignerCertificate.Subject -cne $record.publisher) {
    throw 'MSIX signature is untrusted or does not match the recorded publisher.'
}
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($recordBytes), $true)
$cms.Decode([IO.File]::ReadAllBytes($recordFile.FullName + '.p7s'))
$cms.CheckSignature($false)
if ($cms.SignerInfos.Count -ne 1 -or
    $cms.SignerInfos[0].Certificate.Thumbprint -ine $packageSignature.SignerCertificate.Thumbprint) {
    throw 'Checksum record and MSIX must have the same signer.'
}
& (Join-Path $PSScriptRoot 'Test-MsixPayload.ps1') -PackagePath $package.FullName -ExpectedArchitecture $Architecture

$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $reader = [IO.StreamReader]::new($archive.GetEntry('AppxManifest.xml').Open())
    try { $manifest = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
    $identity = $manifest.Package.Identity
    if ($identity.Name -cne $record.packageIdentityName -or $identity.Publisher -cne $record.publisher -or
        $identity.Version -cne $record.packageVersion -or $identity.ProcessorArchitecture -ine $Architecture) {
        throw 'Package manifest does not match the signed release identity.'
    }
    $application = @($manifest.Package.Applications.Application)
    if ($application.Count -ne 1 -or $application[0].Executable -ine 'ElliePdf.exe' -or
        [string]::IsNullOrWhiteSpace($application[0].Id)) {
        throw 'Expected the single ElliePdf application manifest entry.'
    }
    $stream = $archive.GetEntry('ElliePdf.exe').Open()
    try { $exeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
    finally { $stream.Dispose() }
}
finally { $archive.Dispose() }

if (-not $Execute) {
    Write-Host 'PASS signed UIA package verification. Installation requires -Execute on an interactive runner.'
    return
}
if (-not [Environment]::UserInteractive -or
    [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -ine $Architecture) {
    throw 'A dedicated interactive runner matching the package architecture is required.'
}
Add-AppxPackage -Path $package.FullName
$installed = @(Get-AppxPackage -Name $identity.Name | Where-Object {
    $_.Publisher -ceq $identity.Publisher -and $_.Version.ToString() -ceq $identity.Version -and
    $_.Architecture.ToString() -ieq $Architecture -and -not $_.IsDevelopmentMode
})
if ($installed.Count -ne 1) { throw 'The exact signed package is not installed for the current user.' }
$target = Join-Path $installed[0].InstallLocation 'ElliePdf.exe'
if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -cne $exeHash) {
    throw 'Installed ElliePdf does not match the executable in the signed package.'
}
$aumid = $installed[0].PackageFamilyName + '!' + $application[0].Id
$output = [IO.Path]::GetFullPath($OutputDirectory)
[void][IO.Directory]::CreateDirectory($output)
[ordered]@{
    tag = $Tag
    sourceCommit = $sourceCommit
    signingRun = $record.provenance.runId
    signingRunAttempt = $record.provenance.runAttempt
    architecture = $Architecture
    packageVersion = $record.packageVersion
    signedMsixSha256 = $packageHash
    installedExecutableSha256 = $exeHash
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'signed-input.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'Run-UiAccessibility.ps1') -Interactive -Execute -RequireFixture -InstalledAumid $aumid -TargetPath $target `
    -FixturePath (Join-Path $repo 'testdata/generated/synthetic-mixed-orientation-links-forms-outlines.pdf') -FixturePageCount 8 `
    -SecondaryFixturePath (Join-Path $repo 'testdata/generated/synthetic-1000-pages.pdf') -SecondaryFixturePageCount 1000 `
    -ResultPath (Join-Path $output 'report.json')
