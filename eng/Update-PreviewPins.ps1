[CmdletBinding()]
param(
    [switch] $Apply,
    [switch] $WriteGitHubOutput
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$globalJsonPath = Join-Path $repoRoot 'global.json'
$centralPackagesPath = Join-Path $repoRoot 'Directory.Packages.props'

function Get-VersionSortKey([string] $Version) {
    $numbers = @([Regex]::Matches($Version, '\d+') | ForEach-Object { [uint64]$_.Value })
    return (($numbers | ForEach-Object { $_.ToString('D12', [Globalization.CultureInfo]::InvariantCulture) }) -join '.')
}

function Get-LatestVersion([string] $PackageId, [scriptblock] $Predicate) {
    $id = $PackageId.ToLowerInvariant()
    $versions = (Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$id/index.json").versions
    $candidates = @($versions | Where-Object $Predicate | ForEach-Object {
        [pscustomobject]@{ Text = $_; SortKey = Get-VersionSortKey $_ }
    })
    if ($candidates.Count -eq 0) { throw "No matching version was found for $PackageId." }
    return ($candidates | Sort-Object SortKey -Descending | Select-Object -First 1).Text
}

function Set-PackageVersion([string] $Xml, [string] $PackageId, [string] $Version) {
    $escaped = [Regex]::Escape($PackageId)
    $pattern = "(<PackageVersion\s+Include=`"$escaped`"\s+Version=`")[^`"]+(`"\s*/>)"
    if ($Xml -notmatch $pattern) { throw "Central version entry for $PackageId was not found." }
    return [Regex]::Replace(
        $Xml,
        $pattern,
        { param($match) $match.Groups[1].Value + $Version + $match.Groups[2].Value },
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
}

$releaseMetadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/11.0/releases.json'
$previewSdks = @($releaseMetadata.releases.sdks.version | Where-Object { $_ -match '-' } | ForEach-Object {
    [pscustomobject]@{ Text = $_; SortKey = Get-VersionSortKey $_ }
})
if ($previewSdks.Count -eq 0) { throw 'The official .NET 11 feed contains no preview SDK.' }
$sdkVersion = ($previewSdks | Sort-Object SortKey -Descending | Select-Object -First 1).Text

$packagePins = [ordered]@{
    'Microsoft.Extensions.DependencyInjection' = Get-LatestVersion 'Microsoft.Extensions.DependencyInjection' { $_ -match '^11\.' -and $_ -match '-' }
    'System.Drawing.Common' = Get-LatestVersion 'System.Drawing.Common' { $_ -match '^11\.' -and $_ -match '-' }
    'Microsoft.Windows.SDK.BuildTools' = Get-LatestVersion 'Microsoft.Windows.SDK.BuildTools' { $_ -match '-preview$' }
    'Microsoft.WindowsAppSDK' = Get-LatestVersion 'Microsoft.WindowsAppSDK' { $_ -match '-' }
}

$globalText = [IO.File]::ReadAllText($globalJsonPath)
$globalPattern = '("version"\s*:\s*")[^"]+(")'
if ($globalText -notmatch $globalPattern) { throw 'global.json does not contain an SDK version.' }
$updatedGlobal = [Regex]::new($globalPattern).Replace(
    $globalText,
    { param($match) $match.Groups[1].Value + $sdkVersion + $match.Groups[2].Value },
    1)
$packagesText = [IO.File]::ReadAllText($centralPackagesPath)
$updatedPackages = $packagesText
foreach ($pin in $packagePins.GetEnumerator()) {
    $updatedPackages = Set-PackageVersion $updatedPackages $pin.Key $pin.Value
}

$changed = $updatedGlobal -cne $globalText -or $updatedPackages -cne $packagesText
if ($Apply -and $changed) {
    $utf8NoBom = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText($globalJsonPath, $updatedGlobal, $utf8NoBom)
    [IO.File]::WriteAllText($centralPackagesPath, $updatedPackages, $utf8NoBom)
}

$result = [ordered]@{ sdk = $sdkVersion; changed = $changed; packages = $packagePins }
$result | ConvertTo-Json -Depth 4
if ($WriteGitHubOutput) {
    if ([string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) { throw 'GITHUB_OUTPUT is unavailable.' }
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "sdk=$sdkVersion" -Encoding utf8
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "changed=$($changed.ToString().ToLowerInvariant())" -Encoding utf8
}
