[CmdletBinding()]
param([string]$SbomPath = (Join-Path $PWD 'sbom.json'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
foreach ($path in @('SUPPORTED_WINDOWS.md','THIRD_PARTY_NOTICES.md','third_party/pdfium/154.0.8021/PROVENANCE.md','third_party/pdfium/154.0.8021/sbom.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $path))) { throw "Missing release evidence: $path" }
}
if (-not (Test-Path -LiteralPath $SbomPath)) { throw "Missing generated SBOM: $SbomPath" }
$bom = Get-Content -Raw -LiteralPath $SbomPath | ConvertFrom-Json
if ($bom.bomFormat -ne 'CycloneDX' -or $bom.specVersion -ne '1.5') { throw 'SBOM is not CycloneDX 1.5.' }
$refs = @($bom.components | ForEach-Object { $_.'bom-ref' })
if (($refs | Sort-Object -Unique).Count -ne $refs.Count) { throw 'SBOM contains duplicate bom-ref values.' }
Write-Output "PASS release evidence: $($refs.Count) deterministic package components"
