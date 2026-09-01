[CmdletBinding()]
param([string] $OutputDirectory = 'artifacts/etw-pipeline-self-test')

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $PSScriptRoot 'ElliePdf.TraceExport\ElliePdf.TraceExport.csproj'
& dotnet restore $project --locked-mode | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'ETW pipeline self-test restore failed.' }
& dotnet build $project -c Release --no-restore | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'ETW pipeline self-test build failed.' }

$target = Join-Path $PSScriptRoot 'ElliePdf.TraceExport\bin\Release\net11.0\ElliePdf.TraceExport.exe'
$output = if ([IO.Path]::IsPathFullyQualified($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
}
& (Join-Path $PSScriptRoot 'Invoke-EtwBenchmark.ps1') `
    -TargetPath $target `
    -TargetArgumentList '--emit-self-test' `
    -Iterations 30 `
    -Warmups 0 `
    -TraceDurationSeconds 2 `
    -Scenario first-page `
    -Temperature cold `
    -MachineClass etw-pipeline-self-test `
    -PowerMode controlled `
    -OutputDirectory $output
if ($LASTEXITCODE -ne 0) { throw 'ETW pipeline self-test collection failed.' }

$reportPath = Join-Path $output 'report.json'
$report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json -Depth 32
if ($report.gates.overall -ne 'pass' -or $report.machineClass -ne 'etw-pipeline-self-test') {
    throw 'ETW pipeline self-test report did not pass its synthetic contract.'
}

# A required non-frame metric must contribute exactly one aggregate sample per
# iteration. Prove that a duplicated observation fails closed instead of
# biasing the percentile distribution toward a single iteration.
$eventPath = Join-Path $output 'events.jsonl'
$duplicateEventPath = Join-Path $output 'events-with-duplicate.jsonl'
$eventLines = @(Get-Content -LiteralPath $eventPath)
$iterationZero = $eventLines | Where-Object {
    $event = $_ | ConvertFrom-Json -Depth 16
    $event.iteration -eq 0 -and ($event.eventName -eq 'FirstPagePresented' -or $event.eventId -eq 9)
} | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($iterationZero)) { throw 'ETW pipeline self-test could not find iteration zero.' }
[IO.File]::WriteAllLines($duplicateEventPath, @($eventLines) + $iterationZero, [Text.UTF8Encoding]::new($false))
$duplicateWasRejected = $false
try {
    & (Join-Path $PSScriptRoot 'New-EtwBenchmarkReport.ps1') `
        -EventPath $duplicateEventPath `
        -OutputPath (Join-Path $output 'duplicate-report.json') `
        -Scenario first-page `
        -Iterations 30 `
        -Warmups 0 `
        -Temperature cold `
        -MachineClass etw-pipeline-self-test `
        -PowerMode controlled | Out-Null
}
catch {
    if ($_.Exception.Message -notmatch 'exactly one aggregate sample is required') { throw }
    $duplicateWasRejected = $true
}
if (-not $duplicateWasRejected) { throw 'ETW report accepted duplicate required-metric evidence.' }
Write-Host "PASS ETW collection/export/report pipeline self-test (not product performance evidence): $reportPath"
