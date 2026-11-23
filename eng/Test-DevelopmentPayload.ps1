[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RecipePath,
    [Parameter(Mandatory)][ValidateSet('x64', 'arm64')][string]$ExpectedArchitecture
)

$ErrorActionPreference = 'Stop'
$ExpectedArchitecture = $ExpectedArchitecture.ToLowerInvariant()

if (-not (Test-Path -LiteralPath $RecipePath -PathType Leaf)) {
    throw "Development app recipe was not found: $RecipePath"
}

function Get-PeArchitecture([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "Payload is not a PE file: $Path" }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadUInt32()
        if ($peOffset -gt ($stream.Length - 6)) { throw "Payload has an invalid PE header: $Path" }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw "Payload has an invalid PE signature: $Path" }
        switch ($reader.ReadUInt16()) {
            0x8664 { return 'x64' }
            0xAA64 { return 'arm64' }
            default { throw "Payload has an unsupported PE machine type: $Path" }
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

[xml]$recipe = Get-Content -LiteralPath $RecipePath -Raw
$entries = @($recipe.SelectNodes("//*[local-name()='AppxPackagedFile']") | ForEach-Object {
    [pscustomobject]@{
        Source = [Environment]::ExpandEnvironmentVariables([string]$_.Include)
        PackagePath = ([string]$_.PackagePath).Replace('\', '/')
    }
})

if ($entries.Count -eq 0) { throw 'Development app recipe contains no packaged files.' }

foreach ($entry in $entries) {
    if ([string]::IsNullOrWhiteSpace($entry.Source) -or -not (Test-Path -LiteralPath $entry.Source -PathType Leaf)) {
        throw "Development payload source does not exist for '$($entry.PackagePath)': $($entry.Source)"
    }
    if ($entry.PackagePath -match '(^|/)publish(/|$)' -or $entry.Source -match '[\\/]publish[\\/]') {
        throw "Development payload must not source files from a publish subtree: $($entry.Source)"
    }
}

$requiredPaths = @(
    'PdfWorker/ElliePdf.Pdfium.Worker.exe',
    'PdfWorker/ElliePdf.Pdfium.Worker.runtimeconfig.json',
    'PdfWorker/coreclr.dll',
    'PdfWorker/pdfium.dll'
)
foreach ($requiredPath in $requiredPaths) {
    $matches = @($entries | Where-Object PackagePath -IEQ $requiredPath)
    if ($matches.Count -ne 1) {
        throw "Development payload must contain exactly one '$requiredPath' entry; found $($matches.Count)."
    }
}

if ($entries | Where-Object PackagePath -IEQ 'pdfium.dll') {
    throw 'Development payload must not place pdfium.dll at the package root.'
}

foreach ($nativePath in @('PdfWorker/ElliePdf.Pdfium.Worker.exe', 'PdfWorker/pdfium.dll')) {
    $entry = $entries | Where-Object PackagePath -IEQ $nativePath
    $architecture = Get-PeArchitecture $entry.Source
    if ($architecture -ne $ExpectedArchitecture) {
        throw "'$nativePath' is $architecture, expected $ExpectedArchitecture."
    }
}

Write-Host "PASS development payload: $($entries.Count) recipe entries for $ExpectedArchitecture."
