[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [ValidateSet('x64','arm64')][string]$ExpectedArchitecture
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw 'MSIX package was not found.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath))
try {
    $names = @($zip.Entries | ForEach-Object FullName)
    foreach ($required in @(
        'AppxManifest.xml',
        'Assets/AppIcon.ico',
        'PdfWorker/ElliePdf.Pdfium.Worker.exe',
        'PdfWorker/pdfium.dll')) {
        if ($names -notcontains $required) { throw "MSIX payload is missing $required." }
    }
    if ($names | Where-Object { $_ -like '*.pdb' }) { throw 'MSIX payload must not contain private PDB symbols.' }
    $manifest = $zip.GetEntry('AppxManifest.xml').Open()
    $reader = [IO.StreamReader]::new($manifest)
    try { $xml = [xml]$reader.ReadToEnd() } finally { $reader.Dispose(); $manifest.Dispose() }
    $identity = $xml.Package.Identity
    if ([string]::IsNullOrWhiteSpace($identity.Version) -or $identity.Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'MSIX identity version is not four-part numeric.' }
    $architecture = ([string]$identity.ProcessorArchitecture).ToLowerInvariant()
    if ($architecture -notin @('x64','arm64')) { throw "Unsupported MSIX processor architecture '$architecture'." }
    if ($ExpectedArchitecture -and $architecture -ne $ExpectedArchitecture) {
        throw "MSIX architecture '$architecture' does not match expected '$ExpectedArchitecture'."
    }
    Write-Host "PASS MSIX static payload: $($names.Count) entries, version $($identity.Version)."
}
finally { $zip.Dispose() }
