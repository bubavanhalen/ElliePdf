[CmdletBinding()]
param(
    [ValidateSet('x64','ARM64')][string]$Platform = 'x64',
    [ValidateSet('win-x64','win-arm64')][string]$RuntimeIdentifier = 'win-x64',
    [string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$PackageVersion,
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tfm = 'net11.0-windows10.0.26100.0'
$worker = Join-Path $repo 'src\ElliePdf.Pdfium.Worker\ElliePdf.Pdfium.Worker.csproj'
$app = Join-Path $repo 'ElliePdf.csproj'
$workerOut = Join-Path $repo "src\ElliePdf.Pdfium.Worker\bin\$Platform\$Configuration\$tfm\$RuntimeIdentifier\publish"
$appOut = Join-Path $repo "artifacts\publish\$RuntimeIdentifier\"
$packageOut = Join-Path $repo "artifacts\package\$RuntimeIdentifier\"
$symbolsOut = Join-Path $repo "artifacts\symbols\$RuntimeIdentifier\"
$packageBuildRoot = Join-Path $repo "bin\$Platform\$Configuration\$tfm\$RuntimeIdentifier"
$expectedArchitecture = if ($RuntimeIdentifier -eq 'win-arm64') { 'arm64' } else { 'x64' }

function Reset-RepositoryOutput([string]$Path) {
    $repositoryRoot = [IO.Path]::GetFullPath($repo).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $resolved.TrimEnd([IO.Path]::DirectorySeparatorChar) -eq $repositoryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar)) {
        throw "Refusing to clean output outside the repository: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    [IO.Directory]::CreateDirectory($resolved) | Out-Null
}

function Get-PackageIdentity([string]$ArchivePath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $manifest = $zip.GetEntry('AppxManifest.xml')
        if ($null -eq $manifest) { throw "AppxManifest.xml was not found in $ArchivePath" }
        $stream = $manifest.Open()
        $reader = [IO.StreamReader]::new($stream)
        try { $xml = [xml]$reader.ReadToEnd() }
        finally { $reader.Dispose(); $stream.Dispose() }

        return [pscustomobject]@{
            Version = [string]$xml.Package.Identity.Version
            ProcessorArchitecture = ([string]$xml.Package.Identity.ProcessorArchitecture).ToLowerInvariant()
        }
    }
    finally { $zip.Dispose() }
}

if (($Platform -eq 'x64' -and $RuntimeIdentifier -ne 'win-x64') -or
    ($Platform -eq 'ARM64' -and $RuntimeIdentifier -ne 'win-arm64')) { throw "Platform and RID do not match." }

if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
    $sourceTag = if ($env:GITHUB_REF_NAME -match '^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
        $env:GITHUB_REF_NAME
    } else {
        'v1.0.0'
    }
    $buildNumber = if ($env:GITHUB_RUN_NUMBER -match '^\d+$') {
        [Math]::Min([int]$env:GITHUB_RUN_NUMBER, 65535)
    } else {
        0
    }
    $PackageVersion = & (Join-Path $PSScriptRoot 'Get-MsixVersion.ps1') -Tag $sourceTag -Build $buildNumber
}

# Release staging is exact and reproducible. Never discover a package or symbol
# left by an earlier build, even when two architectures are built on one runner.
foreach ($output in @($workerOut, $appOut, $packageOut, $symbolsOut, $packageBuildRoot)) {
    Reset-RepositoryOutput $output
}

# Publish-only SDK packs are selected by configuration, RID, and optimization
# properties. Restore those exact graphs here so every caller (including the
# standalone documented command) can safely publish with --no-restore.
dotnet restore $worker --locked-mode -p:Configuration=$Configuration -p:Platform=$Platform -p:RuntimeIdentifier=$RuntimeIdentifier -p:PublishReadyToRun=false
if ($LASTEXITCODE -ne 0) { throw "Worker publish restore failed with exit code $LASTEXITCODE." }

dotnet publish $worker -c $Configuration -p:Platform=$Platform -r $RuntimeIdentifier --self-contained true -p:PublishAot=true -p:PublishTrimmed=true -p:PublishReadyToRun=false -o $workerOut --no-restore
if ($LASTEXITCODE -ne 0) { throw "Worker NativeAOT publish failed with exit code $LASTEXITCODE." }
if (-not (Test-Path (Join-Path $workerOut 'ElliePdf.Pdfium.Worker.exe'))) { throw "Worker NativeAOT executable was not produced." }

dotnet restore $app --locked-mode -p:Configuration=$Configuration -p:Platform=$Platform -p:RuntimeIdentifier=$RuntimeIdentifier -p:PublishReadyToRun=true
if ($LASTEXITCODE -ne 0) { throw "Application publish restore failed with exit code $LASTEXITCODE." }

$appArgs = @(
    'publish',
    $app,
    '-c', $Configuration,
    "-p:Platform=$Platform",
    '-r', $RuntimeIdentifier,
    '--self-contained', 'true',
    '-p:PublishAot=true',
    '-p:PublishTrimmed=true',
    '-p:PublishReadyToRun=true',
    "-p:PdfWorkerPublishDirectory=$workerOut",
    "-p:AppxPackageVersion=$PackageVersion",
    '-o', $appOut,
    '--no-restore')
if ($Package) {
    $appArgs += @(
        '-p:GenerateAppxPackageOnBuild=true',
        '-p:AppxPackageIncludePrivateSymbols=false',
        '-p:AppxSymbolPackageEnabled=false',
        '-p:IncludeDebugSymbolsProjectOutputGroup=false')
}
dotnet @appArgs
if ($LASTEXITCODE -ne 0) { throw "Application NativeAOT publish failed with exit code $LASTEXITCODE." }

foreach ($symbolRoot in @(
    @{ Name = 'app'; Path = $appOut },
    @{ Name = 'worker'; Path = $workerOut })) {
    foreach ($symbol in @(Get-ChildItem -LiteralPath $symbolRoot.Path -Recurse -File -Filter *.pdb -ErrorAction SilentlyContinue)) {
        $relative = [IO.Path]::GetRelativePath($symbolRoot.Path, $symbol.FullName)
        $destination = Join-Path (Join-Path $symbolsOut $symbolRoot.Name) $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $symbol.FullName -Destination $destination -Force
    }
}

$bundled = Join-Path $appOut 'PdfWorker\ElliePdf.Pdfium.Worker.exe'
if (-not (Test-Path $bundled)) { throw "Published app is missing bundled PdfWorker: $bundled" }
if ($Package) {
    $candidates = @(Get-ChildItem -LiteralPath $packageBuildRoot -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -eq '.msix' })
    if ($candidates.Count -eq 0) { throw 'Package was requested but no architecture-specific .msix was produced.' }

    $matching = @()
    foreach ($candidate in $candidates) {
        $identity = Get-PackageIdentity -ArchivePath $candidate.FullName
        if ($identity.Version -eq $PackageVersion -and $identity.ProcessorArchitecture -eq $expectedArchitecture) {
            $matching += $candidate
        }
    }

    if ($matching.Count -eq 0) {
        throw "No package with version $PackageVersion and architecture $expectedArchitecture was produced."
    }

    if ($matching.Count -gt 1) {
        throw "Multiple packages with version $PackageVersion and architecture $expectedArchitecture were produced: $($matching.Name -join ', ')"
    }

    Copy-Item -LiteralPath $matching[0].FullName -Destination (Join-Path $packageOut $matching[0].Name) -Force
    if (-not (Get-ChildItem -LiteralPath $packageOut -File -ErrorAction SilentlyContinue | Where-Object { $_.Extension -eq '.msix' })) {
        throw "Package staging directory is empty: $packageOut"
    }
    Write-Host "Unsigned package staged: $packageOut"
}
Write-Host "Release payload ready: $appOut"
