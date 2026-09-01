# Bounded parser and worker-isolation fuzzing

The deterministic harness lives in `tests/ElliePdf.Fuzz.Tests`. Protocol cases
remain in-memory; PDF cases are bounded to 2 MiB and written only to a random
temporary file for the broker to duplicate a read-only handle. The file is
deleted after each case and no PDF/protocol payload is retained. Every case has
a two-second operation deadline and reports only its deterministic case name,
SHA-256 digest, and outcome class. The digest permits reproducing a case without
placing document content in CI logs.

Run the smoke suite from the repository root:

```powershell
./eng/Run-BoundedFuzz.ps1 -Mode Smoke -Configuration Release
```

Smoke runs 16 deterministic PDF mutations; `-Mode Nightly` runs 256 and
`-Mode Release` runs 1,024. The script builds the self-contained x64 worker
when needed, then runs the real `PdfWorkerClient` with
`RequireAppContainerSandbox=true`. The harness fails closed on any hang,
unexpected exception, worker crash, or leaked worker process. It also kills one
worker in a separate recovery test and requires the same test-host process to
reopen the seed PDF successfully.

The process gate requires Windows 11 build 26100+, a built PDFium worker and
the AppContainer APIs. A job-level timeout remains required in CI as a final
outer bound; the per-case watchdog kills the worker before returning the
privacy-safe report.

On a physical ARM64 validation agent, pass
`-Platform ARM64 -RuntimeIdentifier win-arm64`; the defaults are `x64` and
`win-x64`. The
script rejects mismatched platform/runtime pairs before it launches a worker.

`Run-BoundedFuzz.ps1` writes `artifacts/fuzz/<mode>-report.txt` by default; use
`-ReportPath` to select another destination. The report contains no paths,
filenames, PDF bytes, exception messages or extracted content.
