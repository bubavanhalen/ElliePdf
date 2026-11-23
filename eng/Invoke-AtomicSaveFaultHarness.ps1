[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidateSet('Smoke', 'Nightly', 'Release')]
    [string] $Mode = 'Smoke',

    [ValidateRange(0, 1000000)]
    [int] $Iterations = 0,

    [ValidateRange(0, 2147483647)]
    [int] $Seed = 1729,

    [ValidateRange(1, 64)]
    [int] $Parallelism = 4,

    [ValidateRange(512, 1048576)]
    [int] $PayloadBytes = 4096,

    [string] $OutputDirectory = 'artifacts/atomic-save-fault',

    [string] $Project = 'tests/ElliePdf.AtomicSave.FaultHarness/ElliePdf.AtomicSave.FaultHarness.csproj',

    [string] $ReportPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $Project))
if (-not $projectPath.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The fault-harness project must remain inside the repository.'
}
if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Atomic-save fault harness project was not found: $projectPath"
}

if ($Iterations -eq 0) {
    $Iterations = switch ($Mode) {
        'Smoke' { 22 }
        'Nightly' { 10000 }
        'Release' { 10000 }
    }
}
if ($Iterations -lt 11) {
    throw 'At least 11 iterations are required to exercise every AtomicSaveStage.'
}
if ($Mode -ne 'Smoke' -and $Iterations -lt 10000) {
    throw 'Nightly and Release evidence require at least 10,000 completed iterations.'
}
if ($Mode -ne 'Smoke' -and $Configuration -ne 'Release') {
    throw 'Nightly and Release evidence must execute a Release build.'
}

$outputPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
if (-not $outputPath.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The report output directory must remain inside the repository.'
}
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $outputPath ($Mode.ToLowerInvariant() + '-report.json')
} elseif (-not [IO.Path]::IsPathFullyQualified($ReportPath)) {
    $ReportPath = Join-Path $repoRoot $ReportPath
}
$resolvedReportPath = [IO.Path]::GetFullPath($ReportPath)
if (-not $resolvedReportPath.StartsWith($outputPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The report file must remain inside the selected output directory.'
}

$dotnetArgs = @(
    'run',
    '--project', $projectPath,
    '--configuration', $Configuration,
    '--',
    '--iterations', $Iterations,
    '--seed', $Seed,
    '--report', $resolvedReportPath,
    '--parallelism', $Parallelism,
    '--payload-bytes', $PayloadBytes,
    '--configuration', $Configuration
)
$invocationStartedUtc = [DateTimeOffset]::UtcNow
& dotnet @dotnetArgs
$harnessExitCode = $LASTEXITCODE

if (-not (Test-Path -LiteralPath $resolvedReportPath -PathType Leaf)) {
    throw "The harness did not produce its required report (exit code $harnessExitCode)."
}
$reportText = Get-Content -LiteralPath $resolvedReportPath -Raw
if ([string]::IsNullOrWhiteSpace($reportText)) {
    throw 'The atomic-save fault report is empty.'
}
$report = $reportText | ConvertFrom-Json -Depth 32
$reportStartedUtc = [DateTimeOffset]::Parse([string] $report.startedAtUtc, [Globalization.CultureInfo]::InvariantCulture)
$reportCompletedUtc = [DateTimeOffset]::Parse([string] $report.completedAtUtc, [Globalization.CultureInfo]::InvariantCulture)
if ($reportStartedUtc -lt $invocationStartedUtc.AddSeconds(-5) `
    -or $reportCompletedUtc -lt $reportStartedUtc `
    -or $reportCompletedUtc -gt [DateTimeOffset]::UtcNow.AddSeconds(5)) {
    throw 'The report timestamp does not prove that it was produced by this invocation.'
}

$expectedStages = @(
    'DestinationLockAcquired',
    'DestinationVersionVerified',
    'TemporaryFileCreated',
    'TemporaryFileWritten',
    'TemporaryFileFlushed',
    'TemporaryFileValidated',
    'DestinationVersionReverified',
    'CommitStarted',
    'CommitCompleted',
    'CommittedFileValidated',
    'CleanupCompleted'
)
$reportedStages = @($report.stageCoverage | ForEach-Object { $_.stage })
if ($report.schemaVersion -ne 1 -or $report.suite -ne 'atomic-save-fault-harness') {
    throw 'The atomic-save fault report schema or suite identity is invalid.'
}
if ($report.runId -notmatch '^[0-9a-f]{32}$') {
    throw 'The atomic-save fault report run identity is invalid.'
}
if ($report.terminationMode -ne 'child-process-forced-termination' `
    -or -not $report.policy.requiresCompleteOldOrNewDestination `
    -or -not $report.policy.requiresActualChildTermination `
    -or -not $report.policy.requiresEveryStageCovered `
    -or $report.policy.allowsOutcomeUnknown `
    -or $report.policy.includesUserDocumentData) {
    throw 'The report does not assert the required fault-injection policy.'
}
if ($report.result -ne 'pass' -or $harnessExitCode -ne 0) {
    throw "Atomic-save fault evidence failed (harness exit code $harnessExitCode)."
}
if ($report.iterationsRequested -ne $Iterations -or $report.iterationsCompleted -ne $Iterations) {
    throw 'The report does not prove completion of every requested iteration.'
}
if ($report.totals.passed -ne $Iterations -or $report.totals.failed -ne 0 -or @($report.failures).Count -ne 0) {
    throw 'The report contains failed or incomplete fault cases.'
}
if (@(Compare-Object $expectedStages $reportedStages).Count -ne 0) {
    throw 'The report does not cover the exact current AtomicSaveStage set.'
}
if (@($report.stageCoverage).Count -ne $expectedStages.Count `
    -or @($report.stageCoverage | Where-Object {
        $_.iterations -lt 1 `
        -or $_.passed -ne $_.iterations `
        -or $_.failed -ne 0 `
        -or $_.invalidOutcomes -ne 0 `
        -or ($_.oldOutcomes + $_.newOutcomes) -ne $_.iterations
    }).Count -ne 0 `
    -or ($report.stageCoverage | Measure-Object -Property iterations -Sum).Sum -ne $Iterations) {
    throw 'One or more save stages lack passing fault-injection evidence.'
}
$invariantValues = @(
    $report.invariants.invalidDestinationCount,
    $report.invariants.missingDestinationCount,
    $report.invariants.boundaryNotReachedCount,
    $report.invariants.childTerminationFailureCount,
    $report.invariants.journalParseFailureCount,
    $report.invariants.outcomeUnknownCount
)
if (@($invariantValues | Where-Object { $_ -ne 0 }).Count -ne 0) {
    throw 'One or more atomic-save integrity invariants failed.'
}
if (@($report.cases).Count -ne $Iterations) {
    throw 'Per-case evidence is incomplete.'
}
$seenIterations = [Collections.Generic.HashSet[int]]::new()
foreach ($case in @($report.cases)) {
    $caseIteration = [int] $case.iteration
    $expectedArtifactId = 'case-{0:D6}' -f $caseIteration
    $expectedHash = if ($case.outcome -eq 'old') {
        [string] $case.oldSha256
    } elseif ($case.outcome -eq 'new') {
        [string] $case.newSha256
    } else {
        $null
    }

    if ($caseIteration -lt 0 `
        -or $caseIteration -ge $Iterations `
        -or -not $seenIterations.Add($caseIteration) `
        -or $case.artifactId -ne $expectedArtifactId `
        -or -not $case.passed `
        -or -not $case.boundaryReached `
        -or $case.timedOut `
        -or $null -eq $case.childExitCode `
        -or $case.childExitCode -in @(0, 74) `
        -or $case.destinationLength -ne $PayloadBytes `
        -or $null -eq $expectedHash `
        -or $case.oldSha256 -notmatch '^[0-9A-F]{64}$' `
        -or $case.newSha256 -notmatch '^[0-9A-F]{64}$' `
        -or $case.oldSha256 -eq $case.newSha256 `
        -or -not [string]::Equals([string] $case.destinationSha256, $expectedHash, [StringComparison]::Ordinal) `
        -or $case.journalParseFailed `
        -or @($case.journalStages) -contains 'OutcomeUnknown' `
        -or $null -ne $case.failureCode) {
        throw "Fault case '$($case.artifactId)' lacks complete, internally consistent evidence."
    }
}
if ($seenIterations.Count -ne $Iterations) {
    throw 'Per-case iteration identities are incomplete.'
}

foreach ($privateValue in @($repoRoot, [IO.Path]::GetTempPath(), [Environment]::UserName, [Environment]::MachineName)) {
    $privateJsonString = ConvertTo-Json ([string] $privateValue) -Compress
    if (-not [string]::IsNullOrWhiteSpace($privateValue) -and $reportText.Contains($privateJsonString, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The report contains a prohibited local path or host identity.'
    }
}
foreach ($privateField in @('destinationPath', 'workRoot', 'caseDirectory', 'exceptionStack')) {
    if ($reportText.Contains($privateField, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The report contains prohibited field '$privateField'."
    }
}

Write-Host "PASS atomic-save fault harness: $Iterations/$Iterations iterations; seed $Seed; mode $Mode; report $resolvedReportPath"
