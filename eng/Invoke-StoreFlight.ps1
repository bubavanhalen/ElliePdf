[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('status', 'submit', 'rollout', 'halt', 'finalize')]
    [string]$Operation,
    [Parameter(Mandatory)][ValidateSet('flight', 'stable')][string]$Target,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9]{4,40}$')][string]$ProductId,
    [ValidatePattern('^[A-Za-z0-9._-]{1,80}$')][string]$FlightId,
    [Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')][string]$ReleaseTag,
    [ValidateRange(0, 100)][int]$Percentage,
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [switch]$Execute
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$percentageWasBound = $PSBoundParameters.ContainsKey('Percentage')

function Normalize-Thumbprint([string]$Value) { return ($Value -replace '[^0-9A-Fa-f]', '').ToUpperInvariant() }
function Get-Sha256([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }

function Assert-StoreEnvironment {
    if (-not $Execute) { throw 'Store release operations require -Execute and a protected workflow approval.' }
    if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Store release operations are restricted to GitHub Actions.' }
    if ($env:GITHUB_EVENT_NAME -ne 'workflow_dispatch' -or $env:GITHUB_REF -ne 'refs/heads/master') { throw 'Store release operations must be dispatched from the protected master branch.' }
    if ([string]::IsNullOrWhiteSpace($env:GITHUB_WORKFLOW_REF) -or -not $env:GITHUB_WORKFLOW_REF.EndsWith('.github/workflows/store-flighting.yml@refs/heads/master', [StringComparison]::Ordinal)) { throw 'Store workflow was not loaded from the trusted master ref.' }
    if ($env:ELLIEPDF_RUNNER_ENVIRONMENT -ne 'self-hosted') { throw 'Store release operations require a self-hosted runner.' }
    if ($env:ELLIEPDF_STORE_FLIGHTING -ne '1') { throw 'ELLIEPDF_STORE_FLIGHTING=1 is required.' }
    if ($env:ELLIEPDF_STORE_ENVIRONMENT -ne 'store-production') { throw 'The protected store-production environment is required.' }
    if ($env:ELLIEPDF_STORE_APPROVED -ne '1') { throw 'The protected environment must expose ELLIEPDF_STORE_APPROVED=1 after human approval.' }
    if ($env:ELLIEPDF_STORE_EPHEMERAL_RUNNER -ne '1') { throw 'Store credentials require an ephemeral self-hosted runner (ELLIEPDF_STORE_EPHEMERAL_RUNNER=1).' }
    foreach ($name in @(
        'ELLIEPDF_STORE_IDENTITY_NAME',
        'ELLIEPDF_STORE_PUBLISHER',
        'ELLIEPDF_STORE_PRODUCT_ID',
        'ELLIEPDF_SIGNING_RUN_ID',
        'ELLIEPDF_AUTOMATION_COMMIT',
        'ELLIEPDF_SOURCE_COMMIT',
        'ELLIEPDF_SOURCE_REPOSITORY',
        'ELLIEPDF_STORE_AUTH_CERT_THUMBPRINT',
        'AZURE_AD_TENANT_ID',
        'SELLER_ID',
        'AZURE_AD_APPLICATION_CLIENT_ID')) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) { throw "$name must be supplied by the protected workflow." }
    }
    if ($env:ELLIEPDF_SIGNING_RUN_ID -notmatch '^[1-9][0-9]*$') { throw 'ELLIEPDF_SIGNING_RUN_ID is invalid.' }
    if ($env:ELLIEPDF_AUTOMATION_COMMIT -notmatch '^[0-9a-fA-F]{40}$' -or $env:ELLIEPDF_SOURCE_COMMIT -notmatch '^[0-9a-fA-F]{40}$') { throw 'Protected automation/source commit provenance is invalid.' }
    if ((Normalize-Thumbprint $env:ELLIEPDF_STORE_AUTH_CERT_THUMBPRINT) -notmatch '^[0-9A-F]{40}$') { throw 'ELLIEPDF_STORE_AUTH_CERT_THUMBPRINT must be an exact SHA-1 certificate thumbprint.' }
    if ($ProductId -cne $env:ELLIEPDF_STORE_PRODUCT_ID) { throw 'ProductId does not match the protected Store product ID.' }
    if ($Target -eq 'flight' -and [string]::IsNullOrWhiteSpace($FlightId)) { throw '-FlightId is required for the flight target.' }
    if ($Target -eq 'stable' -and -not [string]::IsNullOrWhiteSpace($FlightId)) { throw '-FlightId must be omitted for the stable target.' }
    if ($Operation -in @('submit', 'rollout') -and -not $percentageWasBound) { throw '-Percentage is required for submit and rollout.' }
    if ($Target -eq 'flight') {
        $allowedFlightIds = @(([string]$env:ELLIEPDF_ALLOWED_FLIGHT_IDS).Split(',', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() })
        if ($allowedFlightIds.Count -eq 0 -or $FlightId -cnotin $allowedFlightIds) { throw 'FlightId is not in the protected ELLIEPDF_ALLOWED_FLIGHT_IDS allowlist.' }
    }
}

function Resolve-ContainedFile([string]$Root, [string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$Label is required." }
    $rootPath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Root -ErrorAction Stop).ProviderPath).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $candidate = [IO.Path]::GetFullPath((Join-Path $Root $Path))
    if (-not $candidate.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) { throw "$Label escapes the artifact root." }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "$Label was not found: $candidate" }
    return $candidate
}

function Get-PackageIdentity([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $manifestEntry = $archive.GetEntry('AppxManifest.xml')
        if ($null -eq $manifestEntry) { throw "AppxManifest.xml is missing from $Path" }
        if ($null -eq $archive.GetEntry('AppxSignature.p7x')) { throw "AppxSignature.p7x is missing from $Path" }
        $reader = [IO.StreamReader]::new($manifestEntry.Open())
        try { $manifest = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
        return [pscustomobject]@{
            Name = [string]$manifest.Package.Identity.Name
            Publisher = [string]$manifest.Package.Identity.Publisher
            Version = [string]$manifest.Package.Identity.Version
            Architecture = ([string]$manifest.Package.Identity.ProcessorArchitecture).ToLowerInvariant()
        }
    }
    finally { $archive.Dispose() }
}

function Assert-SignedArtifacts([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw "Artifact root was not found: $Root" }
    $records = @(Get-ChildItem -LiteralPath $Root -Recurse -Filter '*.checksums.json' -File)
    if ($records.Count -ne 2) { throw "Exactly two signed checksum records are required; found $($records.Count)." }
    Add-Type -AssemblyName System.Security.Cryptography.Pkcs
    $architectures = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $verifiedPackages = [Collections.Generic.List[IO.FileInfo]]::new()
    $automationCommit = (& git -C $repoRoot rev-parse --verify HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $automationCommit -notmatch '^[0-9a-fA-F]{40}$') { throw 'The trusted automation commit could not be resolved.' }
    if ($automationCommit -cne $env:ELLIEPDF_AUTOMATION_COMMIT -or $automationCommit -cne $env:GITHUB_SHA) { throw 'The protected workflow automation commit does not match the checkout.' }
    $sourceCommit = (& git -C $repoRoot rev-parse --verify "refs/tags/$ReleaseTag^{commit}").Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-fA-F]{40}$') { throw 'The selected release tag commit could not be resolved.' }
    if ($sourceCommit -cne $env:ELLIEPDF_SOURCE_COMMIT) { throw 'The protected workflow source commit does not match the selected release tag.' }

    foreach ($file in $records) {
        $recordBytes = [IO.File]::ReadAllBytes($file.FullName)
        $record = [Text.Encoding]::UTF8.GetString($recordBytes) | ConvertFrom-Json -Depth 100
        if ($null -eq $record.signedPackage -or [string]::IsNullOrWhiteSpace($record.signedPackage.path)) { throw "Invalid signed package record: $($file.Name)" }
        if ([string]$record.tag -cne $ReleaseTag -or [string]$record.provenance.refName -cne $ReleaseTag) { throw "Signed record tag mismatch in $($file.Name)." }
        if ([string]$record.provenance.runId -cne $env:ELLIEPDF_SIGNING_RUN_ID) { throw "Signing run mismatch in $($file.Name)." }
        if ([string]$record.provenance.repository -cne $env:ELLIEPDF_SOURCE_REPOSITORY) { throw "Source repository mismatch in $($file.Name)." }
        if ([string]$record.provenance.sha -cne $sourceCommit) { throw "Source commit mismatch in $($file.Name)." }

        $packagePath = Resolve-ContainedFile $Root ([string]$record.signedPackage.path) 'Signed package'
        if ([IO.Path]::GetExtension($packagePath) -cne '.msix') { throw "Store input must be an architecture-specific .msix: $packagePath" }
        $package = Get-Item -LiteralPath $packagePath
        if ($package.Length -ne [long]$record.signedPackage.length) { throw "Signed package length mismatch: $packagePath" }
        if ((Get-Sha256 $packagePath) -cne ([string]$record.signedPackage.sha256).ToUpperInvariant()) { throw "Signed package hash mismatch: $packagePath" }
        $identity = Get-PackageIdentity $packagePath
        if ($identity.Name -cne $env:ELLIEPDF_STORE_IDENTITY_NAME -or [string]$record.packageIdentityName -cne $identity.Name) { throw "Package identity mismatch in $($file.Name)." }
        if ($identity.Publisher -cne $env:ELLIEPDF_STORE_PUBLISHER -or [string]$record.publisher -cne $identity.Publisher) { throw "Package publisher mismatch in $($file.Name)." }
        if ($identity.Version -cne [string]$record.packageVersion) { throw "Package version mismatch in $($file.Name)." }
        if ($identity.Architecture -cne ([string]$record.architecture).ToLowerInvariant()) { throw "Package architecture mismatch in $($file.Name)." }
        if (-not $architectures.Add($identity.Architecture)) { throw "Duplicate architecture record '$($identity.Architecture)'." }

        & (Join-Path $PSScriptRoot 'Test-MsixPayload.ps1') -PackagePath $packagePath -ExpectedArchitecture $identity.Architecture | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Static package validation failed: $packagePath" }

        $authenticode = Get-AuthenticodeSignature -FilePath $packagePath
        if ($authenticode.Status -ne 'Valid' -or $null -eq $authenticode.SignerCertificate) { throw "Package signature is not trusted: $packagePath ($($authenticode.Status))." }
        $recordThumbprint = Normalize-Thumbprint ([string]$record.signingCertificate.thumbprint)
        if ((Normalize-Thumbprint $authenticode.SignerCertificate.Thumbprint) -cne $recordThumbprint) { throw "Package signer mismatch in $($file.Name)." }
        if ([string]$authenticode.SignerCertificate.Subject -cne [string]$record.signingCertificate.subject) { throw "Package signer subject mismatch in $($file.Name)." }
        if ([string]$authenticode.SignerCertificate.Subject -cne $env:ELLIEPDF_STORE_PUBLISHER) { throw "Package signer is not the protected Store publisher in $($file.Name)." }

        $signaturePath = Join-Path $file.DirectoryName ($file.Name + '.p7s')
        if (-not (Test-Path -LiteralPath $signaturePath -PathType Leaf)) { throw "Detached checksum signature is missing: $signaturePath" }
        $cms = [Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($recordBytes), $true)
        $cms.Decode([IO.File]::ReadAllBytes($signaturePath))
        $cms.CheckSignature($false)
        if ($cms.SignerInfos.Count -ne 1 -or $null -eq $cms.SignerInfos[0].Certificate) { throw "Checksum record must have exactly one certificate-backed signer: $signaturePath" }
        if ((Normalize-Thumbprint $cms.SignerInfos[0].Certificate.Thumbprint) -cne $recordThumbprint) { throw "Checksum signer mismatch in $($file.Name)." }
        $verifiedPackages.Add($package) | Out-Null
    }

    if (-not $architectures.SetEquals([string[]]@('x64', 'arm64'))) { throw 'The signed release must contain exactly x64 and arm64 packages.' }
    return $verifiedPackages.ToArray()
}

function Invoke-MsStore([string[]]$Arguments) {
    & msstore @Arguments
    if ($LASTEXITCODE -ne 0) { throw "msstore failed with exit code $LASTEXITCODE." }
}

function Assert-MsStoreCliContract {
    if ($null -eq (Get-Command msstore -ErrorAction SilentlyContinue)) { throw 'msstore is not installed.' }
    $help = (& msstore publish --help 2>&1) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) { throw 'msstore publish --help failed.' }
    foreach ($requiredOption in @('--inputDirectory', '--appId', '--noCommit', '--flightId', '--packageRolloutPercentage')) {
        if (-not $help.Contains($requiredOption, [StringComparison]::Ordinal)) { throw "The installed msstore CLI is too old; missing $requiredOption." }
    }
}

function New-VerifiedUploadDirectory([IO.FileInfo[]]$Packages, [string]$Root) {
    $directory = Join-Path ([IO.Path]::GetFullPath($Root)) ('store-upload-' + [guid]::NewGuid().ToString('N'))
    try {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        foreach ($package in $Packages) { Copy-Item -LiteralPath $package.FullName -Destination (Join-Path $directory $package.Name) -ErrorAction Stop }
        return $directory
    }
    catch {
        if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction SilentlyContinue }
        throw
    }
}

function Remove-VerifiedUploadDirectory([string]$Path, [string]$Root) {
    $rootPrefix = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $targetPath = [IO.Path]::GetFullPath($Path)
    if (-not $targetPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($targetPath) -notmatch '^store-upload-[0-9a-f]{32}$') {
        throw "Refusing to remove an unverified upload directory: $targetPath"
    }
    if (Test-Path -LiteralPath $targetPath) { Remove-Item -LiteralPath $targetPath -Recurse -Force }
}

Assert-StoreEnvironment
$resolvedRoot = (Resolve-Path -LiteralPath $ArtifactRoot -ErrorAction Stop).ProviderPath
$signedPackages = @(Assert-SignedArtifacts $resolvedRoot)
Assert-MsStoreCliContract
$operationFailed = $true
$configurationAttempted = $false
try {
    $configurationAttempted = $true
    Invoke-MsStore @('reconfigure', '--tenantId', $env:AZURE_AD_TENANT_ID, '--sellerId', $env:SELLER_ID, '--clientId', $env:AZURE_AD_APPLICATION_CLIENT_ID, '--certificateThumbprint', (Normalize-Thumbprint $env:ELLIEPDF_STORE_AUTH_CERT_THUMBPRINT))
    Invoke-MsStore @('settings', '--enableTelemetry', 'false')
    Invoke-MsStore @('apps', 'get', $ProductId)
    $targetArguments = if ($Target -eq 'flight') { @($FlightId) } else { @() }
    $submissionPrefix = if ($Target -eq 'flight') { @('flights', 'submission') } else { @('submission') }
    switch ($Operation) {
        'status' { Invoke-MsStore @($submissionPrefix + @('status', $ProductId) + $targetArguments) }
        'submit' {
            $uploadDirectory = $null
            try {
                $uploadDirectory = New-VerifiedUploadDirectory $signedPackages $resolvedRoot
                $arguments = @('publish', $repoRoot, '--inputDirectory', $uploadDirectory, '--appId', $ProductId, '--noCommit', '--packageRolloutPercentage', ([string]$Percentage))
                if ($Target -eq 'flight') { $arguments += @('--flightId', $FlightId) }
                Invoke-MsStore $arguments
                Invoke-MsStore @($submissionPrefix + @('publish', $ProductId) + $targetArguments)
                Invoke-MsStore @($submissionPrefix + @('poll', $ProductId) + $targetArguments)
            }
            finally {
                if (-not [string]::IsNullOrWhiteSpace($uploadDirectory)) { Remove-VerifiedUploadDirectory $uploadDirectory $resolvedRoot }
            }
        }
        'rollout' { Invoke-MsStore @($submissionPrefix + @('rollout', 'update', $ProductId) + $targetArguments + @([string]$Percentage)) }
        'halt' { Invoke-MsStore @($submissionPrefix + @('rollout', 'halt', $ProductId) + $targetArguments) }
        'finalize' { Invoke-MsStore @($submissionPrefix + @('rollout', 'finalize', $ProductId) + $targetArguments) }
    }
    $operationFailed = $false
}
finally {
    if ($configurationAttempted) {
        try {
            & msstore reconfigure --reset
            if ($LASTEXITCODE -ne 0) { throw "msstore credential reset failed with exit code $LASTEXITCODE." }
        }
        catch {
            if (-not $operationFailed) { throw }
            Write-Warning "Store operation failed and credential reset also failed: $($_.Exception.Message)"
        }
    }
}
