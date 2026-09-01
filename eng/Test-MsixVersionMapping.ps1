[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$mapper = Join-Path $PSScriptRoot 'Get-MsixVersion.ps1'
$cases = @(
    @{ Tag = 'v1.2.3'; Build = 42; Expected = '1.2.3.42' },
    @{ Tag = 'v11.0.0-preview.7'; Build = 7; Expected = '11.0.0.7' },
    @{ Tag = 'v0.1.0-rc.1'; Build = 65535; Expected = '0.1.0.65535' }
)

foreach ($case in $cases) {
    $actual = & $mapper -Tag $case.Tag -Build $case.Build
    if ($actual -ne $case.Expected) {
        throw "MSIX mapping failed for $($case.Tag): expected $($case.Expected), got $actual."
    }
}

$invalidRejected = $false
try {
    & $mapper -Tag '1.2.3' -Build 1 -ErrorAction Stop | Out-Null
}
catch {
    $invalidRejected = $true
}
if (-not $invalidRejected) {
    throw 'A tag without the required v prefix was accepted.'
}

Write-Host "Validated $($cases.Count) SemVer-to-MSIX mappings and invalid-tag rejection."
