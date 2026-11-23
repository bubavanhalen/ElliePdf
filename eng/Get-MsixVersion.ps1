[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Tag,

    [Parameter(Mandatory = $true)]
    [ValidateRange(0, 65535)]
    [int]$Build
)

$ErrorActionPreference = 'Stop'
$match = [regex]::Match($Tag, '^v(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-[0-9A-Za-z.-]+)?$')
if (-not $match.Success) {
    throw "Tag '$Tag' must have the form vM.m.p[-pre]."
}

$parts = @(
    [int]$match.Groups['major'].Value,
    [int]$match.Groups['minor'].Value,
    [int]$match.Groups['patch'].Value,
    $Build
)
if ($parts | Where-Object { $_ -gt 65535 }) {
    throw 'Every MSIX version component must be in the range 0..65535.'
}

$parts -join '.'
