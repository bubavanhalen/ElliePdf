[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $EventPath,
    [Parameter(Mandatory)] [string] $OutputPath,
    [ValidateSet('launch', 'activation', 'open', 'first-page', 'first-page-10000', 'cached-navigation', 'render', 'random-jump', 'zoom', 'scroll', 'cancellation', 'search', 'memory', 'close-memory', 'idle', 'save-integrity', 'reliability', 'accessibility')]
    [string] $Scenario = 'first-page',
    [ValidateRange(30, 1000)] [int] $Iterations = 30,
    [ValidateRange(0, 100)] [int] $Warmups = 3,
    [string] $MachineClass = 'unknown',
    [string] $PowerMode = 'unknown',
    [ValidateSet('cold', 'warm', 'unspecified')] [string] $Temperature = 'unspecified',
    [ValidateRange(10000, 10000)] [int] $BootstrapSamples = 10000,
    [ValidateRange(1, 1000000)] [int] $MaxEvents = 1000000
)

$ErrorActionPreference = 'Stop'

function Fail([string] $Message) { throw "ETW report: $Message" }

$safeMetricUnits = @{
    'launch' = 'ms'
    'activation' = 'ms'
    'open' = 'ms'
    'metadata-ms' = 'ms'
    'render-queue-wait-ms' = 'ms'
    'native-render-ms' = 'ms'
    'pixel-upload-ms' = 'ms'
    # Event 9 is the actual readable-pixel presentation timestamp. Keep the
    # process startup/readiness observation separate under launch.interactive.
    'first-page.presented' = 'ms'
    'search-ms' = 'ms'
    'save-stage-ms' = 'ms'
    'metadata-ready-ms' = 'ms'
    'render' = 'ms'
    'pdfium-lane-wait-ms' = 'ms'
    'pdfium-call-ms' = 'ms'
    'scroll.frame' = 'ms'
    'search-page-ms' = 'ms'
    'save-stage-completed-ms' = 'ms'
    'recovery-checkpoint-ms' = 'ms'
    'first-page-10000' = 'ms'
    'cached-navigation' = 'ms'
    'random-jump.preview-cached' = 'ms'
    'random-jump.preview-uncached' = 'ms'
    'random-jump.sharp' = 'ms'
    'zoom.input-to-present-refresh-intervals' = 'intervals'
    'zoom.sharp-settled' = 'ms'
    'scroll.dropped-frames-percent' = '%'
    'cancellation.stale-rejection' = 'ms'
    'cancellation.active-yield' = 'ms'
    'memory.private-bytes' = 'bytes'
    'memory.ui.private-bytes' = 'bytes'
    'memory.worker.private-bytes' = 'bytes'
    'memory.working-set-bytes' = 'bytes'
    'memory.cpu-ms' = 'ms'
    'memory.allocation-rate-bytes-per-second' = 'bytes-per-second'
    'memory.gpu-allocation-bytes' = 'bytes'
    'memory.shared-mappings-bytes' = 'bytes'
    'memory.cache-gpu-bytes' = 'bytes'
    'memory.cache-cpu-bytes' = 'bytes'
    'memory.cache-thumbnails-bytes' = 'bytes'
    'memory.cache-geometry-bytes' = 'bytes'
    'memory.close-return-percent' = '%'
    'memory.close-release-ms' = 'ms'
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
    'search.first-before-complete' = 'bool'
    'virtualization.realized-controls' = 'count'
    'virtualization.page-subscriptions' = 'count'
    'virtualization.uncached-raster-leases' = 'count'
    'launch.interactive' = 'ms'
    'activation.completed' = 'ms'
    'open.completed' = 'ms'
    'render.completed' = 'ms'
    'random-jump.preview' = 'ms'
    'search.first-result' = 'ms'
    'search.completed' = 'ms'
    'scroll.dropped-frames' = 'count'
    'zoom.input-to-present' = 'ms'
    'pixel-upload.presented' = 'ms'
}

if (-not (Test-Path -LiteralPath $EventPath -PathType Leaf)) { Fail "event export was not found." }
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$eventFile = [IO.Path]::GetFullPath($EventPath)
if ($MachineClass -notmatch '^[A-Za-z0-9._%-]{1,64}$') { Fail 'machine class must be a non-identifying token of at most 64 characters.' }
if ($PowerMode -notmatch '^[A-Za-z0-9._%-]{1,64}$') { Fail 'power mode must be a non-identifying token of at most 64 characters.' }
if ($Scenario -in @('activation', 'open', 'first-page') -and $Temperature -eq 'unspecified') { Fail "scenario '$Scenario' requires -Temperature cold or warm; statistical warmups do not establish cache temperature." }
if ($Scenario -eq 'first-page-10000' -and $Temperature -ne 'cold') { Fail 'scenario first-page-10000 requires -Temperature cold.' }

function Convert-ToRecord([object] $Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [System.Array]) { return @($Value) }
    if ($Value.PSObject.Properties.Name -contains 'events') { return @($Value.events) }
    return @($Value)
}

function Read-EventRecords([string] $Path) {
    $raw = [IO.File]::ReadAllText($Path)
    if ([string]::IsNullOrWhiteSpace($raw)) { Fail 'event export is empty.' }
    try {
        $parsed = $raw | ConvertFrom-Json -Depth 32
        return @(Convert-ToRecord $parsed)
    }
    catch {
        $records = [Collections.Generic.List[object]]::new()
        foreach ($line in ($raw -split "`r?`n")) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            try { $records.Add(($line | ConvertFrom-Json -Depth 32)) }
            catch { Fail "event export is neither JSON nor JSONL: $($_.Exception.Message)" }
        }
        return @($records)
    }
}

function Get-PropertyValue([object] $Object, [string[]] $Names) {
    if ($null -eq $Object) { return $null }
    foreach ($name in $Names) {
        $property = $Object.PSObject.Properties[$name]
        if ($null -ne $property) { return $property.Value }
    }
    return $null
}

function Get-EventName([object] $Record) {
    $name = [string](Get-PropertyValue $Record @('eventName', 'EventName', 'name', 'Name'))
    if ($name.Contains('/')) { $name = $name.Substring($name.LastIndexOf('/') + 1) }
    if ($name.Contains('.')) { $name = $name.Substring($name.LastIndexOf('.') + 1) }
    return $name
}

function Get-ProviderName([object] $Record) {
    return [string](Get-PropertyValue $Record @('providerName', 'ProviderName', 'provider', 'Provider', 'source'))
}

function Get-Payload([object] $Record) {
    $payload = Get-PropertyValue $Record @('payload', 'Payload', 'args', 'arguments', 'Arguments')
    if ($null -eq $payload) { return $Record }
    if ($payload -is [System.Array]) { return @($payload) }
    return $payload
}

function Get-Iteration([object] $Record) {
    $value = Get-PropertyValue $Record @('iteration', 'Iteration', 'benchmarkIteration')
    $number = 0
    if ($null -eq $value -or -not [int]::TryParse([string]$value, [ref]$number)) { return $null }
    return $number
}

function Get-Scalar([object] $Payload, [string[]] $Names, [int[]] $Indexes) {
    if ($Payload -is [System.Array]) {
        foreach ($index in $Indexes) {
            if ($index -ge 0 -and $index -lt $Payload.Count) { return $Payload[$index] }
        }
        return $null
    }
    return Get-PropertyValue $Payload $Names
}

function Convert-ToDouble([object] $Value) {
    $number = 0.0
    if ($null -eq $Value -or -not [double]::TryParse([string]$Value, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$number)) { return $null }
    if ([double]::IsNaN($number) -or [double]::IsInfinity($number) -or $number -lt 0) { return $null }
    return $number
}

function Convert-ToMetricNumber([object] $Value) {
    if ($Value -is [bool]) { if ($Value) { return 1.0 } else { return 0.0 } }
    if ([string]$Value -match '^(?i:true|false)$') { if ([string]$Value -eq 'true') { return 1.0 } else { return 0.0 } }
    return Convert-ToDouble $Value
}

function Get-Percentile([double[]] $Values, [double] $Percentile) {
    if ($Values.Count -eq 0) { Fail 'cannot calculate a percentile with no samples.' }
    $sorted = [double[]]$Values.Clone()
    [Array]::Sort($sorted)
    $index = ($sorted.Count - 1) * $Percentile
    $lower = [int][Math]::Floor($index)
    $upper = [int][Math]::Ceiling($index)
    if ($lower -eq $upper) { return $sorted[$lower] }
    return $sorted[$lower] + (($sorted[$upper] - $sorted[$lower]) * ($index - $lower))
}

function Get-Statistics([double[]] $Values) {
    if ($Values.Count -lt 30) { Fail "metric has only $($Values.Count) samples; at least 30 measured samples are required." }
    $p95 = Get-Percentile $Values .95
    $estimates = [double[]]::new($BootstrapSamples)
    $random = [Random]::new(1729)
    $resample = [double[]]::new($Values.Count)
    for ($bootstrap = 0; $bootstrap -lt $BootstrapSamples; $bootstrap++) {
        for ($index = 0; $index -lt $resample.Count; $index++) { $resample[$index] = $Values[$random.Next($Values.Count)] }
        $estimates[$bootstrap] = Get-Percentile $resample .95
    }
    $lower = Get-Percentile $estimates .025
    $upper = Get-Percentile $estimates .975
    [pscustomobject]@{
        sampleCount = $Values.Count
        median = [Math]::Round((Get-Percentile $Values .50), 6)
        p95 = [Math]::Round($p95, 6)
        p99 = [Math]::Round((Get-Percentile $Values .99), 6)
        maximum = [Math]::Round(($Values | Measure-Object -Maximum).Maximum, 6)
        minimum = [Math]::Round(($Values | Measure-Object -Minimum).Minimum, 6)
        bootstrap95 = [pscustomobject]@{ lower = [Math]::Round($lower, 6); upper = [Math]::Round($upper, 6); width = [Math]::Round(($upper - $lower), 6) }
        isStable = (($upper - $lower) -le ([Math]::Abs($p95) * .10))
    }
}

# Event IDs are retained as a fallback because some TraceEvent exports omit event names.
$eventDefinitions = @{
    2 = @{ Name = 'activation'; Field = @('durationMicroseconds', 'duration', 'DurationMicroseconds'); Index = @(1) }
    4 = @{ Name = 'open'; Field = @('durationMicroseconds', 'duration', 'DurationMicroseconds'); Index = @(1) }
    5 = @{ Name = 'metadata-ms'; Field = @('durationMicroseconds', 'duration', 'DurationMicroseconds'); Index = @(1) }
    6 = @{ Name = 'render-queue-wait-ms'; Field = @('durationMicroseconds', 'duration', 'DurationMicroseconds'); Index = @(1) }
    7 = @{ Name = 'native-render-ms'; Field = @('durationMicroseconds', 'duration', 'DurationMicroseconds'); Index = @(1) }
    8 = @{ Name = 'pixel-upload-ms'; Field = @('durationMicroseconds', 'duration', 'DurationMicroseconds'); Index = @(1) }
    9 = @{ Name = 'first-page.presented'; Field = @('durationMicroseconds', 'duration', 'DurationMicroseconds'); Index = @(1) }
    12 = @{ Name = 'search-ms'; Field = @('durationMicroseconds', 'duration', 'DurationMicroseconds'); Index = @(1) }
    13 = @{ Name = 'save-stage-ms'; Field = @('durationMicroseconds', 'duration', 'DurationMicroseconds'); Index = @(2) }
    16 = @{ Name = 'launch'; Field = @('durationMicroseconds', 'duration', 'DurationMicroseconds'); Index = @(1) }
    19 = @{ Name = 'metadata-ready-ms'; Field = @('durationMicroseconds', 'duration', 'DurationMicroseconds'); Index = @(1) }
    23 = @{ Name = 'render'; Field = @('durationMicroseconds', 'duration', 'DurationMicroseconds'); Index = @(1) }
    26 = @{ Name = 'pdfium-lane-wait-ms'; Field = @('durationMicroseconds', 'duration', 'DurationMicroseconds'); Index = @(1) }
    27 = @{ Name = 'pdfium-call-ms'; Field = @('durationMicroseconds', 'duration', 'DurationMicroseconds'); Index = @(1) }
    28 = @{ Name = 'scroll.frame'; Field = @('durationMicroseconds', 'duration', 'DurationMicroseconds', 'frameIntervalMicroseconds'); Index = @(1) }
    32 = @{ Name = 'search-page-ms'; Field = @('durationMicroseconds', 'duration', 'DurationMicroseconds'); Index = @(2) }
    36 = @{ Name = 'save-stage-completed-ms'; Field = @('durationMicroseconds', 'duration', 'DurationMicroseconds'); Index = @(2) }
    38 = @{ Name = 'recovery-checkpoint-ms'; Field = @('durationMicroseconds', 'duration', 'DurationMicroseconds'); Index = @(1) }
    43 = @{ Name = 'pixel-upload-ms'; Field = @('durationMicroseconds', 'duration', 'DurationMicroseconds'); Index = @(1) }
}
$nameDefinitions = @{}
foreach ($definition in $eventDefinitions.Values) { $nameDefinitions[$definition.Name] = $definition }
$nameDefinitions['ActivationStop'] = $eventDefinitions[2]
$nameDefinitions['OpenStop'] = $eventDefinitions[4]
$nameDefinitions['MetadataRead'] = $eventDefinitions[5]
$nameDefinitions['RenderQueueWait'] = $eventDefinitions[6]
$nameDefinitions['NativeRender'] = $eventDefinitions[7]
$nameDefinitions['PixelUpload'] = $eventDefinitions[8]
$nameDefinitions['FirstPagePresented'] = $eventDefinitions[9]
$nameDefinitions['Search'] = $eventDefinitions[12]
$nameDefinitions['SaveStage'] = $eventDefinitions[13]
$nameDefinitions['ShellInteractive'] = $eventDefinitions[16]
$nameDefinitions['MetadataReady'] = $eventDefinitions[19]
$nameDefinitions['RenderCompleted'] = $eventDefinitions[23]
$nameDefinitions['PdfiumLaneWait'] = $eventDefinitions[26]
$nameDefinitions['PdfiumCallDuration'] = $eventDefinitions[27]
$nameDefinitions['FramePresented'] = $eventDefinitions[28]
$nameDefinitions['SearchPageCompleted'] = $eventDefinitions[32]
$nameDefinitions['SaveStageCompleted'] = $eventDefinitions[36]
$nameDefinitions['RecoveryCheckpointed'] = $eventDefinitions[38]
$nameDefinitions['PixelUploadDuration'] = $eventDefinitions[43]

$records = @(Read-EventRecords $eventFile)
if ($records.Count -gt $MaxEvents) { Fail "event export exceeds the $MaxEvents event safety limit." }
if ($records.Count -eq 0) { Fail 'event export contains no records.' }
$measurements = @{}
$measurementUnits = @{}
$iterationsSeen = @{}
$sampleCountsByIteration = @{}
$frameValuesByIteration = @{}
$droppedFrameCount = 0.0
$frameCount = 0.0
$hasFrameAccounting = $false
foreach ($record in $records) {
    $provider = Get-ProviderName $record
    if ($provider -and $provider -ne 'ElliePdf') { Fail "event export contains provider '$provider'; only ElliePdf events are accepted." }
    if (-not $provider) { Fail 'every event record must identify providerName=ElliePdf.' }
    $iteration = Get-Iteration $record
    if ($null -eq $iteration) { Fail 'every measured event must carry a benchmark iteration.' }
    if ($iteration -lt 0 -or $iteration -ge $Iterations) { continue }
    $name = Get-EventName $record
    $eventIdValue = Get-PropertyValue $record @('eventId', 'EventId', 'id', 'Id')
    $eventId = 0
    [void][int]::TryParse([string]$eventIdValue, [ref]$eventId)
    $definition = if ($nameDefinitions.ContainsKey($name)) { $nameDefinitions[$name] } elseif ($eventDefinitions.ContainsKey($eventId)) { $eventDefinitions[$eventId] } else { $null }
    $payload = Get-Payload $record
    $metricOverride = [string](Get-PropertyValue $record @('metricName', 'MetricName'))
    if ($metricOverride -and $metricOverride -notmatch '^[A-Za-z0-9._%-]+$') { Fail "event '$name' has an invalid metricName override." }
    $genericValue = Get-Scalar $payload @('value', 'Value', 'measurement', 'Measurement') @()
    if ($metricOverride -and $null -ne $genericValue) {
        if (-not $safeMetricUnits.ContainsKey($metricOverride)) { Fail "metric '$metricOverride' is not in the fixed allowlist." }
        $metricValue = Convert-ToMetricNumber $genericValue
        if ($null -eq $metricValue) { Fail "metric '$metricOverride' has a non-numeric value." }
        $unit = [string](Get-PropertyValue $record @('unit', 'Unit'))
        if ([string]::IsNullOrWhiteSpace($unit) -or $unit -notmatch '^[A-Za-z0-9._%-]+$') { Fail "metric '$metricOverride' is missing a safe unit." }
        if ($safeMetricUnits[$metricOverride] -ne $unit) { Fail "metric '$metricOverride' declared unit '$unit'; expected '$($safeMetricUnits[$metricOverride])'." }
        if (-not $measurements.ContainsKey($metricOverride)) { $measurements[$metricOverride] = [Collections.Generic.List[double]]::new() }
        if ($measurementUnits.ContainsKey($metricOverride) -and $measurementUnits[$metricOverride] -ne $unit) { Fail "metric '$metricOverride' changes unit during the run." }
        $measurementUnits[$metricOverride] = $unit
        $measurements[$metricOverride].Add($metricValue)
        $sampleKey = $metricOverride + '|' + $iteration
        $iterationsSeen[$sampleKey] = $true
        $sampleCountsByIteration[$sampleKey] = 1 + [int]$sampleCountsByIteration[$sampleKey]
        if ($metricOverride -eq 'scroll.frame') {
            if (-not $frameValuesByIteration.ContainsKey($iteration)) { $frameValuesByIteration[$iteration] = [Collections.Generic.List[double]]::new() }
            $frameValuesByIteration[$iteration].Add($metricValue)
        }
        continue
    }
    if ($null -eq $definition) { continue }
    $rawValue = Get-Scalar $payload $definition.Field $definition.Index
    $microseconds = Convert-ToDouble $rawValue
    if ($null -eq $microseconds) { Fail "event '$name' is missing a non-negative durationMicroseconds value." }
    $metricName = if ($metricOverride) {
        $metricOverride
    }
    else {
        $definition.Name
    }
    if (-not $measurements.ContainsKey($metricName)) { $measurements[$metricName] = [Collections.Generic.List[double]]::new() }
    $measurementUnits[$metricName] = 'ms'
    $measurements[$metricName].Add($microseconds / 1000.0)
    $sampleKey = $metricName + '|' + $iteration
    $iterationsSeen[$sampleKey] = $true
    $sampleCountsByIteration[$sampleKey] = 1 + [int]$sampleCountsByIteration[$sampleKey]
    if ($metricName -eq 'scroll.frame') {
        if (-not $frameValuesByIteration.ContainsKey($iteration)) { $frameValuesByIteration[$iteration] = [Collections.Generic.List[double]]::new() }
        $frameValuesByIteration[$iteration].Add($microseconds / 1000.0)
    }
}

# EventSource.FramePresented carries the input-to-present interval, not a separate
# dropped-frame counter. Derive missed refresh opportunities at the fixed 60 Hz
# reference cadence and report one drop-rate sample per measured iteration.
if ($frameValuesByIteration.Count -gt 0) {
    $measurements['scroll.dropped-frames-percent'] = [Collections.Generic.List[double]]::new()
    $measurementUnits['scroll.dropped-frames-percent'] = '%'
    foreach ($iterationKey in $frameValuesByIteration.Keys) {
        $presented = $frameValuesByIteration[$iterationKey].Count
        $dropped = 0.0
        foreach ($intervalMs in $frameValuesByIteration[$iterationKey]) {
            if ($intervalMs -gt 16.7) { $dropped += [Math]::Max(0, [Math]::Ceiling($intervalMs / 16.7) - 1) }
        }
        $total = $presented + $dropped
        $dropPercent = 0.0
        if ($total -gt 0) { $dropPercent = ($dropped / $total) * 100.0 }
        $measurements['scroll.dropped-frames-percent'].Add($dropPercent)
        $dropSampleKey = 'scroll.dropped-frames-percent|' + $iterationKey
        $iterationsSeen[$dropSampleKey] = $true
        $sampleCountsByIteration[$dropSampleKey] = 1
        $droppedFrameCount += $dropped
        $frameCount += $presented
    }
    $hasFrameAccounting = $true
}

$required = switch ($Scenario) {
    'launch' { @('launch') }
    'activation' { @('activation') }
    'open' { @('open') }
    'first-page' { @('first-page.presented') }
    'first-page-10000' { @('first-page-10000') }
    'cached-navigation' { @('cached-navigation') }
    'render' { @('render', 'render-queue-wait-ms') }
    'random-jump' { @('random-jump.preview-cached', 'random-jump.preview-uncached', 'random-jump.sharp') }
    'zoom' { @('zoom.input-to-present-refresh-intervals', 'zoom.sharp-settled') }
    'scroll' { @('scroll.frame', 'scroll.dropped-frames-percent') }
    'cancellation' { @('cancellation.stale-rejection', 'cancellation.active-yield') }
    'search' { @('search.first-before-complete') }
    'memory' {
        @(
            'memory.private-bytes',
            'memory.ui.private-bytes',
            'memory.worker.private-bytes',
            'memory.working-set-bytes',
            'memory.cpu-ms',
            'memory.allocation-rate-bytes-per-second',
            'memory.shared-mappings-bytes',
            'memory.gpu-allocation-bytes',
            'memory.cache-gpu-bytes',
            'memory.cache-cpu-bytes',
            'memory.cache-thumbnails-bytes',
            'memory.cache-geometry-bytes'
        )
    }
    'close-memory' { @('memory.close-return-percent', 'memory.close-release-ms') }
    'idle' { @('idle.cpu-percent', 'idle.recurring-disk-writes') }
    'save-integrity' { @('save.damaged-originals', 'save.fault-injection-count') }
    'reliability' { @('reliability.crash-free-percent', 'reliability.hang-free-percent') }
    'accessibility' { @('accessibility.critical-findings', 'accessibility.high-findings', 'accessibility.incomplete-keyboard-workflows', 'accessibility.incomplete-narrator-workflows') }
}
$mib = 1024 * 1024
$activationP95 = if ($Temperature -eq 'warm') { 300.0 } else { 800.0 }
$sloProfiles = @{
    'launch' = @{ Unit = 'ms'; P95 = 600.0 }
    'activation' = @{ Unit = 'ms'; P95 = $activationP95 }
    'open' = @{ Unit = 'ms'; P95 = $activationP95 }
    'first-page.presented' = @{ Unit = 'ms'; P95 = $activationP95 }
    'first-page-10000' = @{ Unit = 'ms'; P95 = 1000.0 }
    'cached-navigation' = @{ Unit = 'ms'; P95 = 50.0 }
    'render' = @{ Unit = 'ms'; P95 = 200.0 }
    'random-jump.preview-cached' = @{ Unit = 'ms'; P95 = 80.0 }
    'random-jump.preview-uncached' = @{ Unit = 'ms'; P95 = 200.0 }
    'random-jump.sharp' = @{ Unit = 'ms'; P95 = 300.0 }
    'zoom.input-to-present-refresh-intervals' = @{ Unit = 'intervals'; P95 = 2.0 }
    'zoom.sharp-settled' = @{ Unit = 'ms'; P95 = 200.0 }
    'scroll.frame' = @{ Unit = 'ms'; P95 = 16.7; P99 = 33.0 }
    'scroll.dropped-frames-percent' = @{ Unit = '%'; Maximum = 1.0; MaximumExclusive = $true }
    'cancellation.stale-rejection' = @{ Unit = 'ms'; P95 = 10.0 }
    'cancellation.active-yield' = @{ Unit = 'ms'; Maximum = 25.0 }
    'memory.private-bytes' = @{ Unit = 'bytes'; Maximum = 300 * $mib }
    'memory.gpu-allocation-bytes' = @{ Unit = 'bytes'; Maximum = 96 * $mib }
    'memory.cache-gpu-bytes' = @{ Unit = 'bytes'; Maximum = 96 * $mib }
    'memory.cache-cpu-bytes' = @{ Unit = 'bytes'; Maximum = 32 * $mib }
    'memory.cache-thumbnails-bytes' = @{ Unit = 'bytes'; Maximum = 16 * $mib }
    'memory.cache-geometry-bytes' = @{ Unit = 'bytes'; Maximum = 16 * $mib }
    'memory.close-return-percent' = @{ Unit = '%'; Maximum = 10.0 }
    'memory.close-release-ms' = @{ Unit = 'ms'; P95 = 2000.0 }
    'idle.cpu-percent' = @{ Unit = '%'; Maximum = .5; MaximumExclusive = $true }
    'idle.recurring-disk-writes' = @{ Unit = 'count'; Maximum = 0.0 }
    'save.damaged-originals' = @{ Unit = 'count'; Maximum = 0.0 }
    'save.fault-injection-count' = @{ Unit = 'count'; Minimum = 10000.0 }
    'reliability.crash-free-percent' = @{ Unit = '%'; Minimum = 99.9 }
    'reliability.hang-free-percent' = @{ Unit = '%'; Minimum = 99.95 }
    'accessibility.critical-findings' = @{ Unit = 'count'; Maximum = 0.0 }
    'accessibility.high-findings' = @{ Unit = 'count'; Maximum = 0.0 }
    'accessibility.incomplete-keyboard-workflows' = @{ Unit = 'count'; Maximum = 0.0 }
    'accessibility.incomplete-narrator-workflows' = @{ Unit = 'count'; Maximum = 0.0 }
    'search.first-before-complete' = @{ Unit = 'bool'; Minimum = 1.0; Maximum = 1.0 }
}
$metricReports = [Collections.Generic.List[object]]::new()
$gateFailures = [Collections.Generic.List[string]]::new()
$requiredMetricFailures = [Collections.Generic.List[string]]::new()
$qualityFailures = [Collections.Generic.List[string]]::new()
foreach ($metricName in $required) {
    if (-not $measurements.ContainsKey($metricName)) { $message = "required metric '$metricName' is absent"; $gateFailures.Add($message); $requiredMetricFailures.Add($message); continue }
    $samples = [double[]]$measurements[$metricName].ToArray()
    $coveredIterations = 0
    for ($iterationIndex = 0; $iterationIndex -lt $Iterations; $iterationIndex++) {
        $sampleKey = $metricName + '|' + $iterationIndex
        $sampleCount = [int]$sampleCountsByIteration[$sampleKey]
        if ($sampleCount -gt 0) { $coveredIterations++ }
        if ($metricName -ne 'scroll.frame' -and $sampleCount -gt 1) {
            $message = "metric '$metricName' has $sampleCount samples for measured iteration $iterationIndex; exactly one aggregate sample is required"
            $gateFailures.Add($message)
            $requiredMetricFailures.Add($message)
        }
    }
    if ($coveredIterations -lt $Iterations) { $message = "metric '$metricName' covers $coveredIterations of $Iterations measured iterations"; $gateFailures.Add($message); $requiredMetricFailures.Add($message) }
    try { $statistics = Get-Statistics $samples } catch { $gateFailures.Add($_.Exception.Message); $requiredMetricFailures.Add($_.Exception.Message); continue }
    $profile = if ($sloProfiles.ContainsKey($metricName)) { $sloProfiles[$metricName] } else { $null }
    $expectedUnit = if ($null -ne $profile) { $profile.Unit } else { $measurementUnits[$metricName] }
    $actualUnit = $measurementUnits[$metricName]
    if ($actualUnit -ne $expectedUnit) { $message = "metric '$metricName' has unit '$actualUnit'; expected '$expectedUnit'"; $gateFailures.Add($message); $qualityFailures.Add($message) }
    $target = if ($null -ne $profile -and $profile.ContainsKey('P95')) { $profile.P95 } else { $null }
    $targetP99 = if ($null -ne $profile -and $profile.ContainsKey('P99')) { $profile.P99 } else { $null }
    $sloPass = $true
    if ($null -ne $target) {
        $sloPass = $statistics.p95 -le $target
        if (-not $sloPass) { $message = "metric '$metricName' p95 $($statistics.p95) $expectedUnit exceeds $target $expectedUnit"; $gateFailures.Add($message); $qualityFailures.Add($message) }
    }
    if ($null -ne $targetP99 -and $statistics.p99 -gt $targetP99) { $sloPass = $false; $message = "metric '$metricName' p99 $($statistics.p99) exceeds $targetP99 $expectedUnit"; $gateFailures.Add($message); $qualityFailures.Add($message) }
    if ($null -ne $profile -and $profile.ContainsKey('Maximum')) {
        $violatesMaximum = if ($profile.ContainsKey('MaximumExclusive') -and $profile.MaximumExclusive) { $statistics.maximum -ge $profile.Maximum } else { $statistics.maximum -gt $profile.Maximum }
        if ($violatesMaximum) { $sloPass = $false; $message = "metric '$metricName' maximum $($statistics.maximum) exceeds the $($profile.Maximum) $expectedUnit gate"; $gateFailures.Add($message); $qualityFailures.Add($message) }
    }
    if ($null -ne $profile -and $profile.ContainsKey('Minimum') -and $statistics.minimum -lt $profile.Minimum) { $sloPass = $false; $message = "metric '$metricName' minimum $($statistics.minimum) is below the $($profile.Minimum) $expectedUnit gate"; $gateFailures.Add($message); $qualityFailures.Add($message) }
    if (-not $statistics.isStable) { $message = "metric '$metricName' p95 confidence interval is wider than 10% of the estimate"; $gateFailures.Add($message); $qualityFailures.Add($message) }
    $maximumTarget = if ($null -ne $profile -and $profile.ContainsKey('Maximum')) { $profile.Maximum } else { $null }
    $minimumTarget = if ($null -ne $profile -and $profile.ContainsKey('Minimum')) { $profile.Minimum } else { $null }
    $metricStatus = if ($sloPass -and $statistics.isStable) { 'pass' } else { 'fail' }
    $metricReports.Add([pscustomobject]@{ name = $metricName; unit = $actualUnit; statistics = $statistics; slo = [pscustomobject]@{ targetP95 = $target; targetP99 = $targetP99; maximum = $maximumTarget; minimum = $minimumTarget; status = $metricStatus } })
}
if ($Scenario -eq 'scroll') {
    if (-not $hasFrameAccounting -or $frameCount -le 0) { $message = 'scroll requires frameCount and droppedFrames accounting; no frame accounting was exported.'; $gateFailures.Add($message); $qualityFailures.Add($message) }
    else {
        $totalFrameOpportunities = $frameCount + $droppedFrameCount
        $droppedPercent = if ($totalFrameOpportunities -gt 0) { ($droppedFrameCount / $totalFrameOpportunities) * 100.0 } else { 0.0 }
        if ($droppedPercent -ge 1.0) { $message = "dropped-frame rate $([Math]::Round($droppedPercent, 4))% is not below 1%."; $gateFailures.Add($message); $qualityFailures.Add($message) }
    }
}
$frameAccountingReport = $null
if ($hasFrameAccounting) {
    $droppedPercentReport = $null
    $totalFrameOpportunities = $frameCount + $droppedFrameCount
    if ($totalFrameOpportunities -gt 0) { $droppedPercentReport = [Math]::Round(($droppedFrameCount / $totalFrameOpportunities) * 100.0, 6) }
    $frameAccountingReport = [pscustomobject]@{ droppedFrames = $droppedFrameCount; presentedFrames = $frameCount; droppedPercent = $droppedPercentReport }
}
$requiredGateStatus = if ($requiredMetricFailures.Count -eq 0) { 'pass' } else { 'fail' }
$qualityGateStatus = if ($qualityFailures.Count -eq 0) { 'pass' } else { 'fail' }
$overallGateStatus = if ($gateFailures.Count -eq 0) { 'pass' } else { 'fail' }
$report = [pscustomobject]@{
    schemaVersion = '1.0-etw'
    runId = ([guid]::NewGuid().ToString('N'))
    machineClass = $MachineClass.Trim()
    powerMode = $PowerMode.Trim()
    temperature = $Temperature
    scenario = $Scenario
    startedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    collection = [pscustomobject]@{ provider = 'ElliePdf'; eventCount = $records.Count; measuredIterations = $Iterations; warmups = $Warmups; bootstrapSamples = $BootstrapSamples; bootstrapSeed = 1729; temperature = $Temperature }
    metrics = @($metricReports)
    frameAccounting = $frameAccountingReport
    gates = [pscustomobject]@{ requiredMetrics = $requiredGateStatus; confidenceAndSlo = $qualityGateStatus; overall = $overallGateStatus; failures = @($gateFailures) }
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($resolvedOutput)) | Out-Null
$utf8NoBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($resolvedOutput, ($report | ConvertTo-Json -Depth 16), $utf8NoBom)
if ($gateFailures.Count -gt 0) { Fail ("required ETW gates failed: " + ($gateFailures -join '; ')) }
Write-Output "PASS ETW report: $resolvedOutput ($($metricReports.Count) metric(s), $Iterations measured iterations)"
