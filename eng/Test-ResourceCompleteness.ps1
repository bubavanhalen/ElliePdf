$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$files = 'en-US', 'qps-ploc', 'qps-plocm' | ForEach-Object { Join-Path $root "Strings/$_/Resources.resw" }
$sets = @{}
foreach ($file in $files) {
    [xml]$doc = Get-Content $file
    $keys = @($doc.root.data | ForEach-Object name)
    if ($keys.Count -ne (@($keys | Sort-Object -Unique).Count)) { throw "Duplicate resource key: $file" }
    $sets[(Split-Path (Split-Path $file -Parent) -Leaf)] = [System.Collections.Generic.HashSet[string]]::new([string[]]$keys)
}
$baseline = $sets['en-US']
foreach ($locale in 'qps-ploc', 'qps-plocm') {
    if (-not $baseline.SetEquals($sets[$locale])) { throw "Resource key mismatch: $locale" }
}
Write-Host "Resource completeness passed: $($baseline.Count) keys across en-US, qps-ploc, qps-plocm."
