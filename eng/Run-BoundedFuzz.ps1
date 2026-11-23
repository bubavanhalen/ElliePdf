[CmdletBinding()]
param(
    [ValidateSet('Smoke', 'Nightly', 'Release')]
    [string] $Mode = 'Smoke',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [ValidateSet('x64', 'ARM64')]
    [string] $Platform = 'x64',
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $RuntimeIdentifier = 'win-x64',
    [int] $TimeoutMinutes = 0,
    [string] $ReportPath = ''
)

$ErrorActionPreference = 'Stop'
if (($Platform -eq 'x64' -and $RuntimeIdentifier -ne 'win-x64') -or
    ($Platform -eq 'ARM64' -and $RuntimeIdentifier -ne 'win-arm64')) {
    throw "Platform '$Platform' is incompatible with RuntimeIdentifier '$RuntimeIdentifier'."
}
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repo 'tests\ElliePdf.Fuzz.Tests\ElliePdf.Fuzz.Tests.csproj'
$worker = Join-Path $repo 'src\ElliePdf.Pdfium.Worker\ElliePdf.Pdfium.Worker.csproj'
$filter = if ($Mode -eq 'Smoke') { 'FullyQualifiedName~BoundedFuzzHarnessTests' } else { $null }
$timeout = if ($TimeoutMinutes -gt 0) { $TimeoutMinutes } elseif ($Mode -eq 'Smoke') { 5 } elseif ($Mode -eq 'Nightly') { 20 } else { 45 }
$workerArgs = @('build', $worker, '--configuration', $Configuration, "-p:Platform=$Platform", '--runtime', $RuntimeIdentifier, '--no-restore')
Write-Host "Ensuring the self-contained worker payload is built ($Configuration $Platform)."
& dotnet @workerArgs
if ($LASTEXITCODE -ne 0) { throw "PDF worker build failed with exit code $LASTEXITCODE" }

$env:ELLIEPDF_FUZZ_MODE = $Mode
$env:ELLIEPDF_FUZZ_CONFIGURATION = $Configuration
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $repo "artifacts\fuzz\$($Mode.ToLowerInvariant())-report.txt"
}
$ReportPath = [IO.Path]::GetFullPath($ReportPath)
$reportDirectory = [IO.Path]::GetDirectoryName($ReportPath)
if ([string]::IsNullOrWhiteSpace($reportDirectory)) { throw 'Unable to resolve the fuzz report directory.' }
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$env:ELLIEPDF_FUZZ_REPORT_PATH = $ReportPath
$args = @('test', $project, '--configuration', $Configuration, "-p:Platform=$Platform", '--runtime', $RuntimeIdentifier, '--no-restore', '--blame-hang-timeout', "${timeout}m")
if ($filter) { $args += @('--filter', $filter) }
Write-Host "Running bounded PDF/protocol/worker fuzz mode: $Mode ($Configuration, timeout ${timeout}m)"
Write-Host "Privacy-safe fuzz report: $env:ELLIEPDF_FUZZ_REPORT_PATH"
& dotnet @args
if ($LASTEXITCODE -ne 0) { throw "Bounded fuzz harness failed with exit code $LASTEXITCODE" }
