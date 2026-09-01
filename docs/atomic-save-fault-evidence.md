# Atomic-save fault evidence

`ElliePdf.AtomicSave.FaultHarness` proves that abrupt process termination at every
`AtomicSaveStage` leaves the destination equal to the complete synthetic old
payload or the complete synthetic new payload. It never opens user documents.

## Release and nightly gate

Run the 10,000-case gate from the repository root:

```powershell
.\eng\Invoke-AtomicSaveFaultHarness.ps1 -Configuration Release -Mode Release
```

`-Mode Nightly` also defaults to 10,000 cases. The wrapper fails unless all
requested cases finish, all current transaction stages are covered, every child
reaches its selected boundary and exits through forced termination, and every
destination hash is exactly old or new. Missing, partial, invalid, timed-out,
unparseable, or outcome-unknown cases fail the run.

The report is written atomically to
`artifacts/atomic-save-fault/<mode>-report.json`. Its contract is
`eng/AtomicSaveFaultReport.schema.json`. Attach the report and its SHA-256 hash
to release evidence; do not substitute console output for the JSON artifact.

## PR smoke

The Infrastructure test project runs 22 real child terminations—two at each of
the 11 current stages:

```powershell
dotnet test tests/ElliePdf.Infrastructure.Tests/ElliePdf.Infrastructure.Tests.csproj `
  -c Release --filter FullyQualifiedName~AtomicSaveFaultHarnessTests
```

The standalone smoke gate is equivalent:

```powershell
.\eng\Invoke-AtomicSaveFaultHarness.ps1 -Configuration Release -Mode Smoke
```

## Isolation and retained evidence

Each case receives deterministic synthetic old/new payloads and a unique
directory below
`%TEMP%\ElliePdf\AtomicSaveFaultHarness\<run-id>\case-<iteration>`.
The child refuses paths outside that exact shape and refuses reparse-point case
directories or destinations. Successful case directories are removed only
after the parent records destination hashes, journal stages, stage marker,
child exit code, and transaction debris flags in the privacy-safe report.

Failed case directories are retained and identified by the report's privacy-safe
`runId` plus each case's `artifactId`. Inspect or archive them before deleting
them. The report contains no
document names, user paths, document bytes, usernames, machine names, exception
stacks, or raw case paths.
