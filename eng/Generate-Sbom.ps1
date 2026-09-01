[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PWD 'sbom.json'),
    [string]$PackagesRoot = (Join-Path $env:USERPROFILE '.nuget\packages')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$locks = @(Get-ChildItem -LiteralPath $root -Filter packages.lock.json -Recurse -File | Sort-Object FullName)
if (-not $locks) { throw 'No packages.lock.json files found.' }
$components = @{}
foreach ($lock in $locks) {
    $json = Get-Content -Raw -LiteralPath $lock.FullName | ConvertFrom-Json
    foreach ($framework in $json.dependencies.PSObject.Properties) {
        foreach ($pkg in $framework.Value.PSObject.Properties) {
            $resolved = $pkg.Value.resolved
            if ($resolved -and -not $components.ContainsKey($pkg.Name)) {
                $components[$pkg.Name] = [ordered]@{ type='library'; 'bom-ref'="pkg:nuget/$($pkg.Name)@$resolved"; name=$pkg.Name; version=$resolved; purl="pkg:nuget/$($pkg.Name)@$resolved" }
            }
        }
    }
}
$ordered = @($components.Values | Sort-Object name,version)
$lockHashInput = [Text.Encoding]::UTF8.GetBytes(($locks | ForEach-Object { (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash }) -join "`n")
$sha256 = [Security.Cryptography.SHA256]::Create()
try { $lockHash = ([BitConverter]::ToString($sha256.ComputeHash($lockHashInput))).Replace('-', '') }
finally { $sha256.Dispose() }
$bom = [ordered]@{
    bomFormat='CycloneDX'; specVersion='1.5'; serialNumber="urn:uuid:$($lockHash.Substring(0,8))-$($lockHash.Substring(8,4))-$($lockHash.Substring(12,4))-$($lockHash.Substring(16,4))-$($lockHash.Substring(20,12))"; version=1
    metadata=[ordered]@{ component=[ordered]@{ type='application'; name='ElliePdf'; version='0.1.0-preview'; properties=@([ordered]@{name='elliepdf:lock-sha256';value=$lockHash}) } }
    components=$ordered
}
$bom | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output "SBOM written to $OutputPath ($($ordered.Count) components; lock hash $lockHash)"
