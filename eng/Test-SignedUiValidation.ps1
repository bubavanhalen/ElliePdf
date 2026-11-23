[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$UnsignedPackagePath,
    [ValidateSet('x64','arm64')][string]$Architecture = 'x64'
)

$ErrorActionPreference = 'Stop'
$packageInput = (Resolve-Path -LiteralPath $UnsignedPackagePath).ProviderPath
if ((Get-AuthenticodeSignature -LiteralPath $packageInput).Status -ne 'NotSigned') {
    throw 'This preflight test requires an unsigned MSIX.'
}
$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $temporaryParent ('ellie-signed-uia-tests-' + [guid]::NewGuid().ToString('N'))
$bundle = Join-Path $testRoot 'artifacts/release-candidate/1.0.0'
$ridRoot = Join-Path $bundle "win-$Architecture"
$recordDir = Join-Path $ridRoot 'records'
$signedDir = Join-Path $ridRoot 'signed'
$previousEnvironment = @{}
$commit = '1234567890abcdef1234567890abcdef12345678'

# Provide the same SHA for HEAD and the test tag without changing repository refs.
function git {
    $global:LASTEXITCODE = 0
    $commit
}

function Assert-Rejected([string]$Expected) {
    $record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $recordPath -Encoding utf8
    try {
        & (Join-Path $PSScriptRoot 'Invoke-SignedUiValidation.ps1') -ArtifactRoot $testRoot -Tag v1.0.0 -Architecture $Architecture
    }
    catch {
        if ($_.Exception.Message -notlike "$Expected*") { throw }
        Write-Host "PASS $Expected"
        return
    }
    throw "Preflight unexpectedly accepted $Expected"
}

try {
    foreach ($entry in @{ GITHUB_REPOSITORY = 'local/signed-uia-test'; GITHUB_RUN_ID = '100'; GITHUB_RUN_ATTEMPT = '2' }.GetEnumerator()) {
        $previousEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key)
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
    }
    [void][IO.Directory]::CreateDirectory($recordDir)
    [void][IO.Directory]::CreateDirectory($signedDir)
    $testPackage = Join-Path $signedDir 'candidate.msix'
    Copy-Item -LiteralPath $packageInput -Destination $testPackage
    $recordPath = Join-Path $recordDir 'candidate.checksums.json'
    $sbomPath = Join-Path $bundle 'sbom.json'
    $fingerprintPath = Join-Path $bundle 'toolchain-fingerprint.json'
    [IO.File]::WriteAllText($sbomPath, '{}')
    [IO.File]::WriteAllText($fingerprintPath, '{}')
    $record = @{
        tag = 'v1.0.0'
        architecture = $Architecture
        provenance = @{ refName = 'v1.0.0'; repository = $env:GITHUB_REPOSITORY; runId = $env:GITHUB_RUN_ID; runAttempt = '1'; sha = $commit }
        signedPackage = @{ path = 'signed/candidate.msix'; sha256 = 'different'; length = (Get-Item -LiteralPath $testPackage).Length }
        sbom = @{ sha256 = (Get-FileHash -LiteralPath $sbomPath -Algorithm SHA256).Hash }
        toolchainFingerprint = @{ sha256 = (Get-FileHash -LiteralPath $fingerprintPath -Algorithm SHA256).Hash }
    }
    Assert-Rejected 'The signed record does not match'
    $record.provenance.runAttempt = '2'
    Assert-Rejected 'Package filename, length, architecture or hash does not match'
    $record.signedPackage.sha256 = (Get-FileHash -LiteralPath $testPackage -Algorithm SHA256).Hash
    [IO.File]::WriteAllText($sbomPath, '{"tampered":true}')
    Assert-Rejected 'SBOM or toolchain fingerprint does not match'
    $record.sbom.sha256 = (Get-FileHash -LiteralPath $sbomPath -Algorithm SHA256).Hash
    Assert-Rejected 'MSIX signature is untrusted'
}
finally {
    foreach ($entry in $previousEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
    }
    if (Test-Path -LiteralPath $testRoot) {
        $resolved = (Resolve-Path -LiteralPath $testRoot).ProviderPath
        if (-not $resolved.StartsWith($temporaryParent, [StringComparison]::OrdinalIgnoreCase) -or
            (Split-Path $resolved -Leaf) -notlike 'ellie-signed-uia-tests-*') {
            throw 'Refusing to clean a directory outside this test.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
