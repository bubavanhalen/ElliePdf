# ElliePdf benchmark report

This report is produced by `ElliePdf.Benchmark`. Validate it against `Report.schema.json`.

## Run metadata

- `runId`: opaque run identifier; do not use it to encode paths or machine identity.
- `machineClass` and `powerMode`: operator-supplied, non-identifying labels.
- `temperature`: `cold`, `warm`, or `unspecified`; this records the operator's cache procedure and is never inferred from warm-up iterations.
- `startedUtc`: UTC start timestamp.

## Metrics

Process-boundary runs record elapsed time, target-process-tree CPU time, aggregate private bytes and working set, plus separate UI/root and child/worker private bytes. A releasable memory run also requires allocation rate, shared mappings, GPU allocation and GPU/CPU/thumbnail/geometry cache bytes; a render run requires queue latency, and scroll supplies frame time. A driver may add operation-specific metrics through the structured stdout protocol documented in `README.md`. Each metric has a median, P95, P99, maximum and deterministic bootstrap 95% confidence interval (10,000 resamples, seed 1729). Reports contain no fixture paths, filenames, document names, or extracted content. Proxy-mode output is explicitly non-product evidence.
