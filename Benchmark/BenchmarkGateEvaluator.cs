using ElliePdf.Telemetry;

namespace ElliePdf.Benchmark;

public sealed record BenchmarkGateEvaluation(IReadOnlyList<string> Failures)
{
    public bool Passed => Failures.Count == 0;
}

/// <summary>Fail-closed release SLO profile from Section 4 of the execution specification.</summary>
public static class BenchmarkGateEvaluator
{
    private const double MiB = 1024 * 1024;

    public static BenchmarkGateEvaluation Evaluate(
        string scenario,
        string temperature,
        IReadOnlyList<BenchmarkMetric> metrics,
        IReadOnlyDictionary<string, double>? bestComparatorP95 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);
        ArgumentException.ThrowIfNullOrWhiteSpace(temperature);
        ArgumentNullException.ThrowIfNull(metrics);
        var failures = new List<string>();
        var byName = new Dictionary<string, BenchmarkMetric>(StringComparer.Ordinal);
        foreach (BenchmarkMetric metric in metrics)
        {
            if (!byName.TryAdd(metric.Name, metric))
                failures.Add($"metric '{metric.Name}' is duplicated");
        }
        var gates = GatesFor(scenario, temperature);
        foreach (var gate in gates)
        {
            if (!byName.TryGetValue(gate.MetricName, out var metric))
            {
                failures.Add($"required SLO metric '{gate.MetricName}' is missing");
                continue;
            }

            if (!metric.Unit.Equals(gate.Unit, StringComparison.Ordinal))
                failures.Add($"metric '{gate.MetricName}' has unit '{metric.Unit}', expected '{gate.Unit}'");
            if (metric.Statistics.SampleCount < 30)
                failures.Add($"metric '{gate.MetricName}' has {metric.Statistics.SampleCount} samples; at least 30 are required");
            if (gate.RequiresStableP95 && !metric.Statistics.IsStable)
                failures.Add($"metric '{gate.MetricName}' has an unstable p95 confidence interval");
            if (gate.P95 is not null && metric.Statistics.P95 > gate.P95.Value)
                failures.Add($"metric '{gate.MetricName}' p95 {metric.Statistics.P95:F3} exceeds {gate.P95.Value:F3} {gate.Unit}");
            if (gate.P99 is not null && metric.Statistics.P99 > gate.P99.Value)
                failures.Add($"metric '{gate.MetricName}' p99 {metric.Statistics.P99:F3} exceeds {gate.P99.Value:F3} {gate.Unit}");
            if (gate.Maximum is { } maximum &&
                (gate.MaximumExclusive
                    ? metric.Statistics.Maximum >= maximum
                    : metric.Statistics.Maximum > maximum))
                failures.Add($"metric '{gate.MetricName}' maximum {metric.Statistics.Maximum:F3} exceeds the {maximum:F3} {gate.Unit} gate");
            if (gate.Minimum is not null && metric.Statistics.Minimum < gate.Minimum.Value)
                failures.Add($"metric '{gate.MetricName}' minimum {metric.Statistics.Minimum:F3} is below {gate.Minimum.Value:F3} {gate.Unit}");

            if (bestComparatorP95 is not null && gate.CompareWithComparators)
            {
                if (!bestComparatorP95.TryGetValue(gate.MetricName, out var comparator) || comparator <= 0)
                    failures.Add($"comparator p95 baseline for '{gate.MetricName}' is missing");
                else if (metric.Statistics.P95 > comparator * 1.10)
                    failures.Add($"metric '{gate.MetricName}' is more than 10% slower than the best comparator ({comparator:F3} {gate.Unit})");
            }
        }

        return new(failures);
    }

    public static IReadOnlyList<string> ComparatorMetricNamesFor(string scenario, string temperature) =>
        GatesFor(scenario, temperature)
            .Where(static gate => gate.CompareWithComparators)
            .Select(static gate => gate.MetricName)
            .ToArray();

    private static IReadOnlyList<Gate> GatesFor(string scenario, string temperature)
    {
        if (scenario is "activation" or "open" or "first-page")
        {
            var p95 = temperature switch
            {
                "cold" => 800d,
                "warm" => 300d,
                _ => throw new ArgumentException("Activation and first-page release gates require --temperature cold or --temperature warm.", nameof(temperature))
            };
            // First-page is the readable presentation SLO. Startup/readiness is
            // retained separately under launch.interactive and must never satisfy
            // this gate by itself.
            var metricName = scenario == "first-page" ? "first-page.presented" : scenario;
            return [new(metricName, "ms", P95: p95, CompareWithComparators: scenario == "first-page")];
        }

        return scenario switch
        {
            "launch" => [new("launch", "ms", P95: 600, CompareWithComparators: true)],
            "cached-navigation" => [new("cached-navigation", "ms", P95: 50)],
            "render" =>
            [
                new("render", "ms", P95: 200),
                new("render-queue-wait-ms", "ms")
            ],
            "first-page-10000" when temperature == "cold" =>
            [
                new("first-page-10000", "ms", P95: 1000, CompareWithComparators: true),
                new("virtualization.realized-controls", "count", Maximum: 12),
                new("virtualization.page-subscriptions", "count", Maximum: 12),
                new("virtualization.uncached-raster-leases", "count", Maximum: 2)
            ],
            "first-page-10000" => throw new ArgumentException("The 10,000-page first-page gate requires --temperature cold.", nameof(temperature)),
            "random-jump" =>
            [
                new("random-jump.preview-cached", "ms", P95: 80, CompareWithComparators: true),
                new("random-jump.preview-uncached", "ms", P95: 200, CompareWithComparators: true),
                new("random-jump.sharp", "ms", P95: 300, CompareWithComparators: true)
            ],
            "zoom" =>
            [
                new("zoom.input-to-present-refresh-intervals", "intervals", P95: 2),
                new("zoom.sharp-settled", "ms", P95: 200)
            ],
            "scroll" =>
            [
                new("scroll.frame", "ms", P95: 16.7, P99: 33, CompareWithComparators: true),
                new("scroll.dropped-frames-percent", "%", Maximum: 1, MaximumExclusive: true)
            ],
            "cancellation" =>
            [
                new("cancellation.stale-rejection", "ms", P95: 10),
                new("cancellation.active-yield", "ms", Maximum: 25)
            ],
            "memory" =>
            [
                new("memory.private-bytes", "bytes", Maximum: 300 * MiB),
                new("memory.ui.private-bytes", "bytes"),
                new("memory.worker.private-bytes", "bytes"),
                new("memory.working-set-bytes", "bytes"),
                new("memory.cpu-ms", "ms"),
                new("memory.allocation-rate-bytes-per-second", "bytes-per-second"),
                new("memory.shared-mappings-bytes", "bytes"),
                new("memory.gpu-allocation-bytes", "bytes", Maximum: 96 * MiB),
                new("memory.cache-gpu-bytes", "bytes", Maximum: 96 * MiB),
                new("memory.cache-cpu-bytes", "bytes", Maximum: 32 * MiB),
                new("memory.cache-thumbnails-bytes", "bytes", Maximum: 16 * MiB),
                new("memory.cache-geometry-bytes", "bytes", Maximum: 16 * MiB)
            ],
            "close-memory" =>
            [
                new("memory.close-return-percent", "%", Maximum: 10),
                new("memory.close-release-ms", "ms", P95: 2000)
            ],
            "idle" =>
            [
                new("idle.cpu-percent", "%", Maximum: .5, MaximumExclusive: true),
                new("idle.recurring-disk-writes", "count", Maximum: 0)
            ],
            "save-integrity" =>
            [
                new("save.damaged-originals", "count", Maximum: 0),
                new("save.fault-injection-count", "count", Minimum: 10_000)
            ],
            "reliability" =>
            [
                new("reliability.crash-free-percent", "%", Minimum: 99.9),
                new("reliability.hang-free-percent", "%", Minimum: 99.95)
            ],
            "accessibility" =>
            [
                new("accessibility.critical-findings", "count", Maximum: 0),
                new("accessibility.high-findings", "count", Maximum: 0),
                new("accessibility.incomplete-keyboard-workflows", "count", Maximum: 0),
                new("accessibility.incomplete-narrator-workflows", "count", Maximum: 0)
            ],
            "search" => [new("search.first-before-complete", "bool", Maximum: 1, Minimum: 1)],
            _ => throw new ArgumentException($"No release SLO profile exists for scenario '{scenario}'.", nameof(scenario))
        };
    }

    private sealed record Gate(
        string MetricName,
        string Unit,
        double? P95 = null,
        double? P99 = null,
        double? Maximum = null,
        double? Minimum = null,
        bool MaximumExclusive = false,
        bool CompareWithComparators = false)
    {
        public bool RequiresStableP95 => P95 is not null || P99 is not null || CompareWithComparators;
    }
}
