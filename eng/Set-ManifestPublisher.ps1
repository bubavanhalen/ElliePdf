[CmdletBinding(DefaultParameterSetName = 'Set')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Set')]
    [ValidatePattern('^CN=')]
    [string]$Publisher,

    [Parameter(ParameterSetName = 'Set')]
    [string]$ManifestPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Package.appxmanifest'),

    [Parameter(ParameterSetName = 'Set')]
    [string]$BackupPath,

    [Parameter(Mandatory, ParameterSetName = 'Restore')]
    [string]$RestoreFrom,

    [Parameter(ParameterSetName = 'Restore')]
    [string]$RestorePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Package.appxmanifest')
)

$ErrorActionPreference = 'Stop'

function Resolve-AbsolutePath([string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Label is required."
    }

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label was not found: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

if ($PSCmdlet.ParameterSetName -eq 'Restore') {
    $source = Resolve-AbsolutePath -Path $RestoreFrom -Label 'Restore source'
    $destination = $RestorePath
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
    Write-Host "Manifest restored from $source"
    return
}

$resolvedManifest = Resolve-AbsolutePath -Path $ManifestPath -Label 'Manifest'

if ($BackupPath) {
    $backupDirectory = Split-Path -Parent $BackupPath
    if (-not [string]::IsNullOrWhiteSpace($backupDirectory)) {
        New-Item -ItemType Directory -Force -Path $backupDirectory | Out-Null
    }

    Copy-Item -LiteralPath $resolvedManifest -Destination $BackupPath -Force
}

$xml = [xml](Get-Content -Raw -LiteralPath $resolvedManifest)
$identity = $xml.Package.Identity
if ($null -eq $identity) {
    throw "Identity node was not found in $resolvedManifest"
}

$previousPublisher = [string]$identity.Publisher
$identity.Publisher = $Publisher
$xml.Save($resolvedManifest)

Write-Host "Manifest publisher updated: '$previousPublisher' -> '$Publisher'"
