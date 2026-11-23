[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $TargetPath,
    [string] $TargetArguments = '',
    [string[]] $TargetArgumentList = @(),
    [ValidateRange(30, 1000)] [int] $Iterations = 30,
    [ValidateRange(0, 100)] [int] $Warmups = 3,
    [ValidateRange(1, 600)] [int] $TraceDurationSeconds = 30,
    [string] $OutputDirectory = 'artifacts/benchmark-etw',
    [ValidateSet('launch', 'activation', 'open', 'first-page', 'first-page-10000', 'cached-navigation', 'render', 'random-jump', 'zoom', 'scroll', 'cancellation', 'search', 'memory', 'close-memory', 'idle', 'save-integrity', 'reliability', 'accessibility')]
    [string] $Scenario = 'first-page',
    [string] $EventPath,
    [string] $ReportPath,
    [string] $MachineClass = 'unknown',
    [string] $PowerMode = 'unknown',
    [ValidateSet('cold', 'warm', 'unspecified')] [string] $Temperature = 'unspecified',
    [switch] $SkipReport
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $TargetPath -PathType Leaf)) { throw 'Target executable was not found.' }
if (-not [string]::IsNullOrWhiteSpace($TargetArguments) -and $TargetArgumentList.Count -gt 0) {
    throw 'Use either -TargetArguments (one literal argument) or -TargetArgumentList, not both.'
}

$targetFullPath = [IO.Path]::GetFullPath($TargetPath)
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null

$toolManifest = Join-Path $repositoryRoot '.config\dotnet-tools.json'
$globalTraceTool = Get-Command dotnet-trace -ErrorAction SilentlyContinue
$useLocalTraceTool = Test-Path -LiteralPath $toolManifest -PathType Leaf
if ($useLocalTraceTool) {
    & dotnet tool restore --tool-manifest $toolManifest | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The pinned dotnet-trace tool could not be restored.' }
}
elseif ($null -eq $globalTraceTool) {
    throw 'dotnet-trace is required for ETW benchmark collection.'
}

$resolvedEventPath = $null
$resolvedReportPath = $null
$exporterAssembly = $null
if (-not $SkipReport) {
    if ([string]::IsNullOrWhiteSpace($EventPath)) { $EventPath = Join-Path $resolvedOutput 'events.jsonl' }
    if ([string]::IsNullOrWhiteSpace($ReportPath)) { $ReportPath = Join-Path $resolvedOutput 'report.json' }
    $resolvedEventPath = [IO.Path]::GetFullPath($EventPath)
    $resolvedReportPath = [IO.Path]::GetFullPath($ReportPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedEventPath)) | Out-Null
    [IO.File]::WriteAllText($resolvedEventPath, '', [Text.UTF8Encoding]::new($false))

    $exporterProject = Join-Path $PSScriptRoot 'ElliePdf.TraceExport\ElliePdf.TraceExport.csproj'
    & dotnet restore $exporterProject --locked-mode | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The pinned TraceEvent exporter could not be restored.' }
    & dotnet build $exporterProject -c Release --no-restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The TraceEvent exporter could not be built.' }
    $exporterAssembly = Join-Path $PSScriptRoot 'ElliePdf.TraceExport\bin\Release\net11.0\ElliePdf.TraceExport.dll'
    if (-not (Test-Path -LiteralPath $exporterAssembly -PathType Leaf)) { throw 'The TraceEvent exporter output is missing.' }
}

$allowedMetrics = @{
    'launch.interactive' = 'ms'
    'activation.completed' = 'ms'
    'open.completed' = 'ms'
    'first-page.presented' = 'ms'
    'first-page-10000' = 'ms'
    'cached-navigation' = 'ms'
    'render.completed' = 'ms'
    'render-queue-wait-ms' = 'ms'
    'random-jump.preview' = 'ms'
    'random-jump.preview-cached' = 'ms'
    'random-jump.preview-uncached' = 'ms'
    'random-jump.sharp' = 'ms'
    'search.first-result' = 'ms'
    'search.completed' = 'ms'
    'search.first-before-complete' = 'bool'
    'scroll.frame' = 'ms'
    'scroll.dropped-frames' = 'count'
    'scroll.dropped-frames-percent' = '%'
    'zoom.input-to-present' = 'ms'
    'zoom.input-to-present-refresh-intervals' = 'intervals'
    'zoom.sharp-settled' = 'ms'
    'cancellation.stale-rejection' = 'ms'
    'cancellation.active-yield' = 'ms'
    'memory.private-bytes' = 'bytes'
    'memory.gpu-allocation-bytes' = 'bytes'
    'memory.shared-mappings-bytes' = 'bytes'
    'memory.ui.private-bytes' = 'bytes'
    'memory.worker.private-bytes' = 'bytes'
    'memory.working-set-bytes' = 'bytes'
    'memory.cpu-ms' = 'ms'
    'memory.allocation-rate-bytes-per-second' = 'bytes-per-second'
    'memory.cache-gpu-bytes' = 'bytes'
    'memory.cache-cpu-bytes' = 'bytes'
    'memory.cache-thumbnails-bytes' = 'bytes'
    'memory.cache-geometry-bytes' = 'bytes'
    'memory.close-return-percent' = '%'
    'memory.close-release-ms' = 'ms'
    'virtualization.realized-controls' = 'count'
    'virtualization.page-subscriptions' = 'count'
    'virtualization.uncached-raster-leases' = 'count'
    'idle.cpu-percent' = '%'
    'idle.recurring-disk-writes' = 'count'
    'save.damaged-originals' = 'count'
    'save.fault-injection-count' = 'count'
    'reliability.crash-free-percent' = '%'
    'reliability.hang-free-percent' = '%'
    'accessibility.critical-findings' = 'count'
    'accessibility.high-findings' = 'count'
    'accessibility.incomplete-keyboard-workflows' = 'count'
    'accessibility.incomplete-narrator-workflows' = 'count'
    'pixel-upload.presented' = 'ms'
}

# These metrics are observed directly from EventSource during an ETW run. The
# stdout forms exist for the standalone process harness, but admitting both
# would double-weight an iteration (or conceal a missing production event).
$eventBackedAggregateMetrics = [Collections.Generic.HashSet[string]]::new(
    [string[]]@('render-queue-wait-ms', 'scroll.frame', 'scroll.dropped-frames-percent'),
    [StringComparer]::Ordinal)

function Get-TargetProcessIds {
    $ids = [Collections.Generic.HashSet[int]]::new()
    foreach ($candidate in @(Get-Process -ErrorAction SilentlyContinue)) {
        try {
            if ($candidate.Path -eq $targetFullPath -and -not $candidate.HasExited) { [void]$ids.Add($candidate.Id) }
        }
        catch [System.ComponentModel.Win32Exception] { }
        catch [System.InvalidOperationException] { }
    }
    return ,$ids
}

function Stop-NewTargetProcesses([Collections.Generic.HashSet[int]] $ExistingIds) {
    foreach ($candidate in @(Get-Process -ErrorAction SilentlyContinue)) {
        try {
            if ($candidate.Path -eq $targetFullPath -and -not $candidate.HasExited -and -not $ExistingIds.Contains($candidate.Id)) {
                $candidate.Kill($true)
            }
        }
        catch [System.ComponentModel.Win32Exception] { }
        catch [System.InvalidOperationException] { }
    }
}

function Invoke-DotNetTrace([string[]] $Arguments) {
    if ($useLocalTraceTool) {
        Push-Location $repositoryRoot
        try {
            $output = @(& dotnet tool run dotnet-trace -- @Arguments 2>&1)
            $exitCode = $LASTEXITCODE
        }
        finally { Pop-Location }
    }
    else {
        $output = @(& $globalTraceTool.Source @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = @($output) }
}

function Add-AggregateMetrics([object[]] $OutputLines, [int] $Iteration) {
    if ($SkipReport) { return }
    $prefix = 'ELLIEPDF_BENCHMARK_METRIC '
    $utf8NoBom = [Text.UTF8Encoding]::new($false)
    $writer = [IO.StreamWriter]::new($resolvedEventPath, $true, $utf8NoBom)
    try {
        foreach ($outputLine in $OutputLines) {
            $line = [string]$outputLine
            if (-not $line.StartsWith($prefix, [StringComparison]::Ordinal)) { continue }
            try { $metric = $line.Substring($prefix.Length) | ConvertFrom-Json -Depth 4 }
            catch { throw "Target emitted a malformed aggregate benchmark metric on iteration $Iteration." }
            $metricName = [string]$metric.name
            $unit = [string]$metric.unit
            $number = 0.0
            if (-not $allowedMetrics.ContainsKey($metricName) -or $allowedMetrics[$metricName] -ne $unit -or
                -not [double]::TryParse([string]$metric.value, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or
                [double]::IsNaN($number) -or [double]::IsInfinity($number)) {
                throw "Target emitted an unknown or unsafe aggregate benchmark metric on iteration $Iteration."
            }
            if ($eventBackedAggregateMetrics.Contains($metricName)) { continue }
            $record = [ordered]@{
                providerName = 'ElliePdf'
                eventName = 'AggregateMetric'
                eventId = 0
                iteration = $Iteration
                metricName = $metricName
                unit = $unit
                payload = [ordered]@{ value = $number }
            }
            $writer.WriteLine(($record | ConvertTo-Json -Compress -Depth 5))
        }
    }
    finally { $writer.Dispose() }
}

function Invoke-Run([int] $Index, [bool] $Warmup) {
    $phase = if ($Warmup) { 'warmup' } else { 'measured' }
    $tracePath = Join-Path $resolvedOutput ("{0}-{1:D3}.nettrace" -f $phase, $Index)
    $existingTargetIds = Get-TargetProcessIds
    try {
        # Launch-through-collector is required: attaching after Start-Process can
        # miss AppLaunchStart, ShellInteractive, open, and first-page events.
        $duration = [TimeSpan]::FromSeconds($TraceDurationSeconds).ToString('c', [Globalization.CultureInfo]::InvariantCulture)
        $traceArguments = @('collect', '--providers', 'ElliePdf', '--duration', $duration, '--output', $tracePath, '--', $targetFullPath)
        if ($TargetArgumentList.Count -gt 0) { $traceArguments += $TargetArgumentList }
        elseif (-not [string]::IsNullOrWhiteSpace($TargetArguments)) { $traceArguments += $TargetArguments }
        $traceResult = Invoke-DotNetTrace $traceArguments
        if ($traceResult.ExitCode -ne 0) {
            $diagnostic = ($traceResult.Output | Select-Object -Last 12) -join [Environment]::NewLine
            throw "dotnet-trace failed for $phase iteration $Index.`n$diagnostic"
        }

        if (-not $Warmup -and -not $SkipReport) {
            & dotnet $exporterAssembly --trace $tracePath --output $resolvedEventPath --iteration $Index --append | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "TraceEvent export failed for measured iteration $Index." }
            Add-AggregateMetrics $traceResult.Output $Index
        }
    }
    finally { Stop-NewTargetProcesses $existingTargetIds }
}

for ($i = 0; $i -lt $Warmups; $i++) { Invoke-Run $i $true }
for ($i = 0; $i -lt $Iterations; $i++) { Invoke-Run $i $false }
Write-Host ("Collected {0} warmup and {1} measured privacy-safe ETW traces." -f $Warmups, $Iterations)

if (-not $SkipReport) {
    $reportScript = Join-Path $PSScriptRoot 'New-EtwBenchmarkReport.ps1'
    if (-not (Test-Path -LiteralPath $reportScript -PathType Leaf)) { throw 'ETW report generator is missing.' }
    & $reportScript -EventPath $resolvedEventPath -OutputPath $resolvedReportPath -Scenario $Scenario -Iterations $Iterations -Warmups $Warmups -MachineClass $MachineClass -PowerMode $PowerMode -Temperature $Temperature
    if ($LASTEXITCODE -ne 0) { throw 'ETW report generation failed.' }
}
else {
    Write-Warning 'ETW report generation was explicitly skipped; this run is not performance evidence.'
}
