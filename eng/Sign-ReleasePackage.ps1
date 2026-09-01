[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,

    [Parameter(Mandatory)]
    [ValidatePattern('^CN=')]
    [string]$Publisher,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9.-]{3,50}$')]
    [string]$PackageIdentityName,

    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$CertificateThumbprint,

    [string]$OutputRoot = (Join-Path $PWD 'artifacts\release-signing'),

    [ValidateSet('x64', 'arm64')]
    [string]$ExpectedArchitecture,

    [string]$SbomPath,
    [string]$ToolchainFingerprintPath,
    [string]$Tag,
    [string]$PackageVersion,
    [string]$TimestampUrl,
    [switch]$AllowUnsignedTimestamp
)

$ErrorActionPreference = 'Stop'

function Assert-ProtectedSigningEnvironment {
    if ($env:GITHUB_ACTIONS -ne 'true') {
        throw 'Protected signing is restricted to GitHub Actions.'
    }

    if ($env:ELLIEPDF_RUNNER_ENVIRONMENT -ne 'self-hosted') {
        throw "Protected signing requires a self-hosted runner. ELLIEPDF_RUNNER_ENVIRONMENT='$($env:ELLIEPDF_RUNNER_ENVIRONMENT)'."
    }

    if ($env:ELLIEPDF_RELEASE_SIGNING -ne '1') {
        throw 'Protected signing requires ELLIEPDF_RELEASE_SIGNING=1.'
    }

    if ($env:ELLIEPDF_RELEASE_ENVIRONMENT -ne 'release-signing') {
        throw "Protected signing requires ELLIEPDF_RELEASE_ENVIRONMENT=release-signing. Current value: '$($env:ELLIEPDF_RELEASE_ENVIRONMENT)'."
    }
}

function Resolve-AbsolutePath([string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Label is required."
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label was not found: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Normalize-Thumbprint([string]$Thumbprint) {
    return ($Thumbprint -replace '\s', '').ToUpperInvariant()
}

function Get-CodeSigningCertificate([string]$Thumbprint) {
    $normalized = Normalize-Thumbprint $Thumbprint
    foreach ($storeLocation in @('CurrentUser', 'LocalMachine')) {
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('My', $storeLocation)
        try {
            $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
            foreach ($certificate in $store.Certificates) {
                if ((Normalize-Thumbprint $certificate.Thumbprint) -eq $normalized) {
                    return [pscustomobject]@{
                        Certificate = $certificate
                        StoreLocation = $storeLocation
                    }
                }
            }
        }
        finally {
            $store.Close()
        }
    }

    throw "Certificate thumbprint was not found in CurrentUser\\My or LocalMachine\\My: $normalized"
}

function Test-CodeSigningEku([System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    foreach ($extension in $Certificate.Extensions) {
        if ($extension -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
            foreach ($usage in $extension.EnhancedKeyUsages) {
                if ($usage.Value -eq $codeSigningOid) {
                    return $true
                }
            }

            return $false
        }
    }

    return $false
}

function Get-SignToolPath {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $kitsRoots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Container) }

    foreach ($root in $kitsRoots) {
        $candidate = Get-ChildItem -LiteralPath $root -Recurse -Filter signtool.exe -File -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate.FullName
        }
    }

    throw 'signtool.exe was not found. Install the Windows SDK signing tools on the protected runner.'
}

function Get-PackageIdentity([string]$ArchivePath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $signatureEntry = $zip.Entries | Where-Object FullName -eq 'AppxSignature.p7x' | Select-Object -First 1
        $manifestEntry = $zip.Entries | Where-Object FullName -eq 'AppxManifest.xml' | Select-Object -First 1
        if ($null -eq $manifestEntry) {
            throw "AppxManifest.xml was not found in $ArchivePath"
        }

        $stream = $manifestEntry.Open()
        $reader = [System.IO.StreamReader]::new($stream)
        try {
            $xml = [xml]$reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
            $stream.Dispose()
        }

        return [pscustomobject]@{
            Name = [string]$xml.Package.Identity.Name
            Publisher = [string]$xml.Package.Identity.Publisher
            Version = [string]$xml.Package.Identity.Version
            ProcessorArchitecture = ([string]$xml.Package.Identity.ProcessorArchitecture).ToLowerInvariant()
            HasEmbeddedSignature = $null -ne $signatureEntry
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Get-RelativeRepoPath([string]$Path) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $resolved = Resolve-Path -LiteralPath $Path
    try {
        return [System.IO.Path]::GetRelativePath($repoRoot, $resolved.Path)
    }
    catch {
        return $resolved.Path
    }
}

function Get-FileSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

function Protect-ChecksumRecord([string]$RecordPath, [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate) {
    $contentBytes = [System.IO.File]::ReadAllBytes($RecordPath)
    $contentInfo = [System.Security.Cryptography.Pkcs.ContentInfo]::new($contentBytes)
    $cms = [System.Security.Cryptography.Pkcs.SignedCms]::new($contentInfo, $true)
    $signer = [System.Security.Cryptography.Pkcs.CmsSigner]::new($Certificate)
    $signer.IncludeOption = [System.Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
    $cms.ComputeSignature($signer)

    $signaturePath = "$RecordPath.p7s"
    [System.IO.File]::WriteAllBytes($signaturePath, $cms.Encode())

    $verify = [System.Security.Cryptography.Pkcs.SignedCms]::new($contentInfo, $true)
    $verify.Decode([System.IO.File]::ReadAllBytes($signaturePath))
    $verify.CheckSignature($true)

    $signerThumbprint = Normalize-Thumbprint $verify.SignerInfos[0].Certificate.Thumbprint
    if ($signerThumbprint -ne (Normalize-Thumbprint $Certificate.Thumbprint)) {
        throw "Detached checksum signature signer mismatch: expected $(Normalize-Thumbprint $Certificate.Thumbprint), got $signerThumbprint."
    }

    return $signaturePath
}

Assert-ProtectedSigningEnvironment
$resolvedPackage = Resolve-AbsolutePath -Path $PackagePath -Label 'Package'
if ([IO.Path]::GetExtension($resolvedPackage) -cne '.msix') {
    throw 'The protected signing lane accepts architecture-specific .msix packages only.'
}
$packageIdentity = Get-PackageIdentity -ArchivePath $resolvedPackage
if ($packageIdentity.HasEmbeddedSignature) {
    throw "Release input must be an unsigned MSIX. AppxSignature.p7x already exists in $resolvedPackage"
}

if ($packageIdentity.Publisher -ne $Publisher) {
    throw "Manifest publisher mismatch: package declares '$($packageIdentity.Publisher)', expected '$Publisher'."
}

if ($packageIdentity.Name -cne $PackageIdentityName) {
    throw "Package identity mismatch: package declares '$($packageIdentity.Name)', expected '$PackageIdentityName'."
}

if ($ExpectedArchitecture -and $packageIdentity.ProcessorArchitecture -ne $ExpectedArchitecture) {
    throw "Package architecture mismatch: package is '$($packageIdentity.ProcessorArchitecture)', expected '$ExpectedArchitecture'."
}

$certificateRecord = Get-CodeSigningCertificate -Thumbprint $CertificateThumbprint
$certificate = $certificateRecord.Certificate

if (-not $certificate.HasPrivateKey) {
    throw "Certificate '$($certificate.Subject)' does not have an accessible private key."
}

if ($certificate.Subject -cne $Publisher) {
    throw "Certificate subject '$($certificate.Subject)' does not exactly match manifest publisher '$Publisher'."
}

if (-not (Test-CodeSigningEku -Certificate $certificate)) {
    throw "Certificate '$($certificate.Subject)' does not advertise the code-signing EKU 1.3.6.1.5.5.7.3.3."
}

$now = [DateTime]::Now
if ($now -lt $certificate.NotBefore -or $now -gt $certificate.NotAfter) {
    throw "Certificate '$($certificate.Subject)' is outside its validity period."
}

$signTool = Get-SignToolPath
$packageBaseName = [System.IO.Path]::GetFileNameWithoutExtension($resolvedPackage)
$packageExtension = [System.IO.Path]::GetExtension($resolvedPackage)
$signedDirectory = Join-Path $OutputRoot 'signed'
$recordsDirectory = Join-Path $OutputRoot 'records'
New-Item -ItemType Directory -Force -Path $signedDirectory, $recordsDirectory | Out-Null

$unsignedHash = Get-FileSha256 -Path $resolvedPackage
$signedPackagePath = Join-Path $signedDirectory ([System.IO.Path]::GetFileName($resolvedPackage))
Copy-Item -LiteralPath $resolvedPackage -Destination $signedPackagePath -Force

$signArguments = @(
    'sign',
    '/fd', 'SHA256',
    '/sha1', (Normalize-Thumbprint $certificate.Thumbprint),
    '/s', 'My',
    $signedPackagePath
)

if ($certificateRecord.StoreLocation -eq 'LocalMachine') {
    $signArguments += '/sm'
}

if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
    $signArguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
}
elseif (-not $AllowUnsignedTimestamp) {
    throw 'TimestampUrl is required unless -AllowUnsignedTimestamp is supplied.'
}

& $signTool @signArguments
if ($LASTEXITCODE -ne 0) {
    throw "signtool sign failed with exit code $LASTEXITCODE."
}

$verifyArguments = @('verify', '/pa', '/v', $signedPackagePath)
& $signTool @verifyArguments
if ($LASTEXITCODE -ne 0) {
    throw "signtool verify failed with exit code $LASTEXITCODE."
}

$signedIdentity = Get-PackageIdentity -ArchivePath $signedPackagePath
if (-not $signedIdentity.HasEmbeddedSignature) {
    throw "Signed package is missing AppxSignature.p7x: $signedPackagePath"
}

if ($signedIdentity.Publisher -ne $Publisher) {
    throw "Signed package publisher drifted: '$($signedIdentity.Publisher)'"
}

if ($signedIdentity.Version -ne $packageIdentity.Version) {
    throw "Signed package version drifted: '$($signedIdentity.Version)'"
}

$staticValidationScript = Join-Path $PSScriptRoot 'Test-MsixPayload.ps1'
if (-not (Test-Path -LiteralPath $staticValidationScript -PathType Leaf)) {
    throw "Static payload validator was not found: $staticValidationScript"
}

if ($ExpectedArchitecture) {
    & $staticValidationScript -PackagePath $resolvedPackage -ExpectedArchitecture $ExpectedArchitecture
    & $staticValidationScript -PackagePath $signedPackagePath -ExpectedArchitecture $ExpectedArchitecture
}
else {
    & $staticValidationScript -PackagePath $resolvedPackage
    & $staticValidationScript -PackagePath $signedPackagePath
}

$signedHash = Get-FileSha256 -Path $signedPackagePath
$recordPath = Join-Path $recordsDirectory "$packageBaseName.checksums.json"
$record = [ordered]@{
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    tag = $Tag
    packageVersion = $PackageVersion
    packageIdentityName = $signedIdentity.Name
    publisher = $Publisher
    architecture = $signedIdentity.ProcessorArchitecture
    unsignedPackage = [ordered]@{
        path = Get-RelativeRepoPath -Path $resolvedPackage
        sha256 = $unsignedHash
        length = (Get-Item -LiteralPath $resolvedPackage).Length
    }
    signedPackage = [ordered]@{
        path = Get-RelativeRepoPath -Path $signedPackagePath
        sha256 = $signedHash
        length = (Get-Item -LiteralPath $signedPackagePath).Length
    }
    signingCertificate = [ordered]@{
        thumbprint = Normalize-Thumbprint $certificate.Thumbprint
        subject = $certificate.Subject
        storeLocation = $certificateRecord.StoreLocation
        notBeforeUtc = $certificate.NotBefore.ToUniversalTime().ToString('o')
        notAfterUtc = $certificate.NotAfter.ToUniversalTime().ToString('o')
    }
    signTool = [ordered]@{
        path = $signTool
        fileVersion = (Get-Item -LiteralPath $signTool).VersionInfo.FileVersion
        digestAlgorithm = 'SHA256'
        timestampUrl = if ([string]::IsNullOrWhiteSpace($TimestampUrl)) { $null } else { $TimestampUrl }
    }
    provenance = [ordered]@{
        repository = $env:GITHUB_REPOSITORY
        workflow = $env:GITHUB_WORKFLOW
        runId = $env:GITHUB_RUN_ID
        runAttempt = $env:GITHUB_RUN_ATTEMPT
        actor = $env:GITHUB_ACTOR
        sha = if ([string]::IsNullOrWhiteSpace($env:ELLIEPDF_SOURCE_COMMIT)) { $env:GITHUB_SHA } else { $env:ELLIEPDF_SOURCE_COMMIT }
        ref = if ([string]::IsNullOrWhiteSpace($env:ELLIEPDF_SOURCE_REF)) { $env:GITHUB_REF } else { $env:ELLIEPDF_SOURCE_REF }
        refName = $Tag
    }
}

if (-not [string]::IsNullOrWhiteSpace($SbomPath)) {
    $resolvedSbom = Resolve-AbsolutePath -Path $SbomPath -Label 'SBOM'
    $record.sbom = [ordered]@{
        path = Get-RelativeRepoPath -Path $resolvedSbom
        sha256 = Get-FileSha256 -Path $resolvedSbom
    }
}

if (-not [string]::IsNullOrWhiteSpace($ToolchainFingerprintPath)) {
    $resolvedFingerprint = Resolve-AbsolutePath -Path $ToolchainFingerprintPath -Label 'Toolchain fingerprint'
    $record.toolchainFingerprint = [ordered]@{
        path = Get-RelativeRepoPath -Path $resolvedFingerprint
        sha256 = Get-FileSha256 -Path $resolvedFingerprint
    }
}

$record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $recordPath -Encoding utf8
$signaturePath = Protect-ChecksumRecord -RecordPath $recordPath -Certificate $certificate

[pscustomobject]@{
    SignedPackagePath = $signedPackagePath
    ChecksumRecordPath = $recordPath
    ChecksumSignaturePath = $signaturePath
    UnsignedSha256 = $unsignedHash
    SignedSha256 = $signedHash
    Architecture = $signedIdentity.ProcessorArchitecture
}
