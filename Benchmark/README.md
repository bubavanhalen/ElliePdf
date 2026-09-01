# WP-01 benchmark artifacts

The harness consumes `testdata/manifest.json` and emits a `BenchmarkReport` JSON document. Every fixture is identified by a SHA-256 hash and logical ID; reports must not contain paths, filenames, document names, or extracted content. Use at least 30 measured iterations and the deterministic bootstrap method in `ElliePdf.Telemetry.BenchmarkStatistics`. Pass `--temperature cold` or `--temperature warm` to record which documented cache procedure was used; `--warmups` is only a statistical warm-up and is not a substitute for the cold/warm operator procedure.

## Product measurements

Pass `--target` to measure a real ElliePdf executable. The target is started once per iteration, `{fixture}` expands to the verified fixture path, and readiness is measured from process start to Win32 input-idle or a line matching `--ready-regex`. The harness exports `ELLIEPDF_BENCHMARK_FIXTURE` and `ELLIEPDF_BENCHMARK_ITERATION` for a dedicated driver to exercise open, first-page, render, random-jump, search and memory scenarios. A driver must emit its readiness line only after the requested operation completes. Process mode records elapsed time, target-process-tree CPU time, aggregate private bytes and working set, and separate root UI/child-worker private bytes. Child processes are discovered through the Windows process snapshot API and short-lived children are handled conservatively.

Drivers may additionally emit aggregate operation metrics on stdout using this line protocol:

```text
ELLIEPDF_BENCHMARK_METRIC {"name":"first-page.presented","unit":"ms","value":123.4}
```

The latest value for a metric in each process iteration is reported. Names and units are deliberately limited to opaque alphanumeric tokens plus `.`, `-`, `_` and `%`, and metric names must come from the fixed aggregate allowlist in `BenchmarkMetricProtocol`. Malformed, non-finite, unknown or path-like values are ignored. The ETW evidence path is stricter: every required aggregate metric must occur exactly once in every measured iteration, except `scroll.frame`, which deliberately contains multiple frame samples. This lets a real UI/ETW driver publish first-page, frame, cancellation, cache and GPU metrics without putting document data in the report or overweighting one iteration.

```powershell
dotnet run --project Benchmark\Benchmark.csproj -c Release -- --target .\ElliePdf.exe --scenario launch --iterations 30 --warmups 3 --output artifacts\launch.json
dotnet run --project Benchmark\Benchmark.csproj -c Release -- --target .\ElliePdf.exe --target-arguments '--benchmark-driver first-page' --ready-regex '^ELLIEPDF_READY first-page$' --scenario first-page --fixture synthetic-vector-small
```

ElliePdf includes an opt-in product driver: `--benchmark-driver <scenario>`. The harness supplies the verified fixture through `ELLIEPDF_BENCHMARK_FIXTURE`; it must not be placed on the command line. The mode is inert unless that exact switch and one of the fixed scenarios is present: `open`, `first-page`, `first-page-10000`, `cached-navigation`, `render`, `random-jump`, `scroll`, `zoom`, `search`, `memory`, or `cancellation`. It creates the normal reader window, opens the fixture through the normal document workspace and isolated worker, then uses the reader ViewModel's continuous-view realization path for visible-page work. It writes only `ELLIEPDF_BENCHMARK_METRIC` lines from the existing allowlist and the fixed `ELLIEPDF_READY <scenario>` line; it never writes fixture paths, document names, search results, or exception text. Readiness is emitted only after the requested action has finished. The app remains alive briefly so the collector can snapshot its UI/worker process tree and then closes its window and worker cleanly; normal launches neither select nor run this mode.

The implemented actions measure actual visible-page render/presentation work, cached navigation and three-phase random jumps (uncached low-resolution preview, cached low-resolution preview, then sharp replacement/settle at the device raster scale), several continuous-view frame realizations, a ViewModel zoom transition, the production search command with a fixed non-sensitive query (including its first published result), separate stale and active cancellation operations, and CPU/GPU render-cache plus managed allocation observations. The `first-page` release gate consumes `first-page.presented`, emitted after readable page pixels replace the placeholder; process startup/readiness remains a separate observation. Memory mode includes app/worker process counters and numeric shared-memory lease facts; the harness retains process-tree counters as the trusted values if they collide. The 10,000-page mode reads realized ItemsRepeater controls, active page-surface subscriptions, and outstanding worker raster leases rather than the document ViewModel's page count. It does not yet synthesize input-pump frame pacing or cover save/reliability/accessibility gates; those still require their dedicated evidence drivers rather than invented values.

`--scenario pixel-upload` without `--target` runs a bounded 512×512 BGRA8 copy proxy for the direct-pixel backend spike. It is explicitly reported as `backend-proxy`, never as product evidence, and should later be replaced by a real Composition/swap-chain upload-to-present driver.

The comparator file freezes versions, settings, cache procedure, power mode, machine class, corpus hash, p95 baselines and statistical method before a run. The checked-in file is an inventory template with `RECORD_` placeholders. A dependency-free self-test deliberately works with that template; a real comparison run must use `--mode sample` (or `--require-comparators`) and fails closed until every comparator has an exact version, non-empty settings, hashes for every selected corpus fixture and numeric `baselineP95` values for every required gate metric. Sample/release mode also fails closed for missing or unstable required SLO metrics, any p95/p99/maximum violation, and any ElliePdf p95 over 110% of the best comparator.

Typical local checks:

```powershell
dotnet run --project Benchmark\Benchmark.csproj -- --self-test --output artifacts\benchmark-self-test.json
dotnet run --project Benchmark\Benchmark.csproj -- --mode sample --manifest testdata\manifest.json
```

The second command is expected to fail until the controlled reference machine record is filled in. This prevents an
unfrozen Acrobat/Edge/Sumatra comparison from being mistaken for release evidence. Reports contain only aggregate
measurements and opaque run IDs; fixture paths, names and extracted PDF content are never emitted. Missing comparator
versions, settings or corpus hashes remain a hard release gate.

## ETW evidence collection

`eng/Invoke-EtwBenchmark.ps1` launches each target through `dotnet-trace collect --`
so launch and first-page events cannot be lost to a late attach, then collects one
privacy-safe `.nettrace` per warmup and measured iteration. The script restores the
repo-local `dotnet-trace` tool, builds `eng/ElliePdf.TraceExport`, and appends one
JSONL record per measured `ElliePdf` event into `events.jsonl`. Each record carries
only `providerName`, `eventName`, `eventId`, the zero-based measured `iteration`,
and numeric/bool payload fields; strings are never exported. A benchmark driver
can emit the fixed `ELLIEPDF_BENCHMARK_METRIC` stdout protocol for non-duration
gates such as memory, GPU, reliability, accessibility, virtualization and save
integrity; the collector validates the exact metric name/unit pair and appends
the numeric value with the measured iteration. The report
generator recognizes the event IDs/names in the instrumentation contract and
requires the scenario's metric to cover every measured iteration with the
defined cardinality.

The checked-in exporter is privacy-scoped rather than parser-neutral: `dotnet-trace`
produces the `.nettrace` container and `eng/ElliePdf.TraceExport` converts only the
`ElliePdf` provider into reportable JSONL. Target arguments that must remain
separate are passed with `-TargetArgumentList`:

```powershell
pwsh eng/Invoke-EtwBenchmark.ps1 -TargetPath .\ElliePdf.exe -Scenario first-page `
  -Temperature cold -MachineClass reference-x64 -PowerMode best-performance `
  -TargetArgumentList @('--benchmark-driver', 'first-page')
```

The command fails closed if the export is absent, malformed, from another provider,
missing iteration coverage, below 30 samples, unstable (p95 bootstrap CI width is
over 10% of p95), or outside the exact scenario SLO profile in `BenchmarkGateEvaluator`
(including launch 600 ms, temperature-specific activation/first-page, 10,000-page
cold first-page 1,000 ms, scroll p95/p99/drop rate, memory, save, reliability, and
accessibility gates). `FramePresented` intervals are converted to dropped-frame
counts at the fixed 60 Hz cadence with `presented + dropped` as the denominator;
the report does not accept an invented counter. Use `-SkipReport` only for trace collection/debugging; skipped runs are explicitly
not evidence. The machine-readable report is `report.json`; it contains only
aggregate values and opaque run metadata. `-Temperature` records the operator's
cache procedure and is never inferred from statistical warmups.

`eng/Test-EtwBenchmarkPipeline.ps1` runs a 30-iteration synthetic provider through
the pinned collector, exporter, and report generator. It is a CI contract test only;
its `machineClass` is `etw-pipeline-self-test` and it is never product performance evidence.

The executable benchmark's `--mode sample` and `--require-comparators` paths apply the same release gates in
`BenchmarkGateEvaluator`: launch 600 ms p95; warm/cold activation and first-page 300/800 ms p95; render 200 ms
p95; cached/uncached/sharp random jump 80/200/300 ms p95; scroll 16.7 ms p95, 33 ms p99 and strictly below 1%
dropped frames; stale/active cancellation 10/25 ms p95; and memory evidence containing CPU, working set, allocation
rate, UI/worker private bytes, shared mappings, GPU allocation and all four cache budgets. The memory gates enforce
300 MiB aggregate private, 96 MiB GPU/tile cache, 32 MiB CPU handoff, and 16 MiB each for thumbnail and geometry caches;
and stable first-result/search/save metrics. Required metrics, confidence stability, SLO limits and the 110% best
comparator rule are all fail-closed. The checked-in comparator manifest intentionally has no baselines, so release
comparison remains blocked until the controlled reference-machine record is completed.

The scheduled self-hosted performance job additionally requires the repository
variable `ELLIEPDF_REFERENCE_SUITE_PATH`. It must point to the controlled UI/comparator
driver on that reference machine and accept `-RepositoryRoot`. The job fails closed
when the driver, comparator records, or any resulting gate is missing; a printed
handoff message is never treated as benchmark evidence.
