[CmdletBinding()]
param(
    [ValidateRange(1, 480)]
    [int] $DurationMinutes = 480,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [ValidateSet('x64', 'ARM64')]
    [string] $Platform = 'x64',
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $RuntimeIdentifier = 'win-x64',
    [string] $ReportPath = ''
)

$ErrorActionPreference = 'Stop'
if (($Platform -eq 'x64' -and $RuntimeIdentifier -ne 'win-x64') -or
    ($Platform -eq 'ARM64' -and $RuntimeIdentifier -ne 'win-arm64')) {
    throw "Platform '$Platform' is incompatible with RuntimeIdentifier '$RuntimeIdentifier'."
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$worker = Join-Path $repo 'src\ElliePdf.Pdfium.Worker\ElliePdf.Pdfium.Worker.csproj'
$tests = Join-Path $repo 'tests\ElliePdf.Pdf.Client.Tests\ElliePdf.Pdf.Client.Tests.csproj'
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $repo "artifacts\soak\mixed-document-$($RuntimeIdentifier).json"
}

& dotnet build $worker -c $Configuration -p:Platform=$Platform -p:RuntimeIdentifier=$RuntimeIdentifier --no-restore
if ($LASTEXITCODE -ne 0) { throw "Worker build failed with exit code $LASTEXITCODE." }

$env:ELLIEPDF_SOAK_MINUTES = $DurationMinutes.ToString([Globalization.CultureInfo]::InvariantCulture)
$env:ELLIEPDF_SOAK_REPORT_PATH = [IO.Path]::GetFullPath($ReportPath)
try {
    & dotnet test $tests -c $Configuration -p:Platform=$Platform -p:RuntimeIdentifier=$RuntimeIdentifier --no-restore `
        --filter 'FullyQualifiedName~Mixed_document_open_render_search_close_soak_is_bounded' `
        --blame-hang-timeout "$($DurationMinutes + 10)m"
    if ($LASTEXITCODE -ne 0) { throw "Mixed-document soak failed with exit code $LASTEXITCODE." }
}
finally {
    Remove-Item Env:ELLIEPDF_SOAK_MINUTES -ErrorAction SilentlyContinue
    Remove-Item Env:ELLIEPDF_SOAK_REPORT_PATH -ErrorAction SilentlyContinue
}

if (-not (Test-Path -LiteralPath $ReportPath -PathType Leaf)) {
    throw 'The soak completed without producing its privacy-safe report.'
}
Write-Host "PASS mixed-document soak evidence: $([IO.Path]::GetFullPath($ReportPath))"
