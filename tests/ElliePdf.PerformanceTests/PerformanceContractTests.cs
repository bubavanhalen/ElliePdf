using System.Text.Json;
using ElliePdf.Benchmark;
using ElliePdf.Telemetry;
using Xunit;

namespace ElliePdf.PerformanceTests;

public sealed class PerformanceContractTests
{
    [Fact]
    public void CorpusManifestIsDeterministicAndContainsRequiredScenarios()
    {
        var manifest = CorpusManifest.Load(File.ReadAllText(RepoFile("testdata", "manifest.json")));
        Assert.Equal("1.0", manifest.SchemaVersion);
        Assert.Equal("SHA-256", manifest.HashAlgorithm);
        Assert.Contains(manifest.Fixtures, fixture => fixture.Kind == "vector");
        Assert.Contains(manifest.Fixtures, fixture => fixture.Kind == "photo-scan");
        Assert.Contains(manifest.Fixtures, fixture => fixture.Kind == "long-document");
        Assert.All(manifest.Fixtures, fixture => Assert.Matches("^[a-fA-F0-9]{64}$|^RECORD_", fixture.Sha256));
    }

    [Fact]
    public void BenchmarkStatisticsUseTheFrozenConfidenceMethod()
    {
        var values = Enumerable.Range(1, 40).Select(static value => (double)value).ToArray();
        var statistics = BenchmarkStatistics.Compute(values);
        Assert.Equal(40, statistics.SampleCount);
        Assert.Equal(38.05, statistics.P95, precision: 2);
        Assert.Equal(40, statistics.Maximum);
        Assert.True(statistics.Bootstrap95.Upper >= statistics.Bootstrap95.Lower);
    }

    [Fact]
    public void BenchmarkReportTemplateContainsNoDocumentIdentityFields()
    {
        var template = File.ReadAllText(RepoFile("Benchmark", "report-template.md"));
        Assert.DoesNotContain("synthetic-vector-small.pdf", template, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Quarterly Results", template, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Reports contain no fixture paths", template, StringComparison.Ordinal);
        Assert.Contains("P95", template, StringComparison.Ordinal);
        Assert.Contains("confidence", template, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BenchmarkReportRecordsCacheTemperatureExplicitly()
    {
        var report = new BenchmarkReport(
            "1.0",
            "0123456789abcdef0123456789abcdef",
            "reference-laptop",
            "balanced",
            DateTimeOffset.UnixEpoch,
            [new BenchmarkMetric("first-page.presented", "ms", BenchmarkStatistics.Compute(Enumerable.Repeat(1d, 30).ToArray()))])
        {
            Temperature = "cold"
        };

        var roundTrip = BenchmarkReport.FromJson(report.ToJson());
        Assert.Equal("cold", roundTrip.Temperature);
    }

    [Fact]
    public void ReleaseGateFailsClosedWhenRequiredScenarioMetricsAreMissing()
    {
        var metrics = new[] { Metric("launch", "ms", 500) };

        var evaluation = BenchmarkGateEvaluator.Evaluate(
            "random-jump",
            "warm",
            metrics,
            new Dictionary<string, double>
            {
                ["random-jump.preview-cached"] = 50,
                ["random-jump.preview-uncached"] = 100,
                ["random-jump.sharp"] = 200
            });

        Assert.False(evaluation.Passed);
        Assert.Contains(evaluation.Failures, failure => failure.Contains("random-jump.preview-cached", StringComparison.Ordinal));
    }

    [Fact]
    public void ReleaseGateComparesStableP95AgainstBestComparatorWithinTenPercent()
    {
        var metrics = new[] { Metric("launch", "ms", 111) };
        var evaluation = BenchmarkGateEvaluator.Evaluate(
            "launch",
            "unspecified",
            metrics,
            new Dictionary<string, double> { ["launch"] = 100 });

        Assert.False(evaluation.Passed);
        Assert.Contains(evaluation.Failures, failure => failure.Contains("10% slower", StringComparison.Ordinal));
    }

    [Fact]
    public void FirstPageGateRequiresReadablePresentationAndDoesNotAcceptStartupReadiness()
    {
        var startupOnly = BenchmarkGateEvaluator.Evaluate(
            "first-page",
            "warm",
            [Metric("launch.interactive", "ms", 100), Metric("first-page", "ms", 100)],
            new Dictionary<string, double> { ["first-page.presented"] = 100 });

        Assert.False(startupOnly.Passed);
        Assert.Contains(startupOnly.Failures, failure => failure.Contains("first-page.presented", StringComparison.Ordinal));

        var presented = BenchmarkGateEvaluator.Evaluate(
            "first-page",
            "warm",
            [Metric("first-page.presented", "ms", 100)],
            new Dictionary<string, double> { ["first-page.presented"] = 100 });

        Assert.True(presented.Passed);
    }

    [Theory]
    [InlineData("cached-navigation", "unspecified", "cached-navigation", "ms", 50.01)]
    [InlineData("zoom", "unspecified", "zoom.input-to-present-refresh-intervals", "intervals", 2.01)]
    [InlineData("close-memory", "unspecified", "memory.close-return-percent", "%", 10.01)]
    [InlineData("idle", "unspecified", "idle.cpu-percent", "%", .5)]
    [InlineData("reliability", "unspecified", "reliability.crash-free-percent", "%", 99.89)]
    public void SectionFourReleaseProfilesRejectAnExceededGate(
        string scenario,
        string temperature,
        string metricName,
        string unit,
        double failingValue)
    {
        BenchmarkMetric[] metrics = PassingMetricsFor(scenario, temperature)
            .Select(metric => metric.Name == metricName ? Metric(metricName, unit, failingValue) : metric)
            .ToArray();

        BenchmarkGateEvaluation evaluation = BenchmarkGateEvaluator.Evaluate(scenario, temperature, metrics);

        Assert.False(evaluation.Passed);
        Assert.Contains(evaluation.Failures, failure => failure.Contains(metricName, StringComparison.Ordinal));
    }

    [Fact]
    public void TenThousandPageProfileRequiresColdEvidenceAndBoundedRealization()
    {
        Assert.Throws<ArgumentException>(() => BenchmarkGateEvaluator.Evaluate(
            "first-page-10000", "warm", [], new Dictionary<string, double>()));
        BenchmarkMetric[] metrics = PassingMetricsFor("first-page-10000", "cold")
            .Select(metric => metric.Name == "virtualization.realized-controls"
                ? Metric(metric.Name, metric.Unit, 13)
                : metric)
            .ToArray();

        BenchmarkGateEvaluation evaluation = BenchmarkGateEvaluator.Evaluate(
            "first-page-10000",
            "cold",
            metrics,
            new Dictionary<string, double> { ["first-page-10000"] = 1000 });

        Assert.False(evaluation.Passed);
        Assert.Contains(evaluation.Failures, failure => failure.Contains("realized-controls", StringComparison.Ordinal));
    }

    [Fact]
    public void SaveIntegrityAndAccessibilityProfilesRequireZeroFailures()
    {
        Assert.True(BenchmarkGateEvaluator.Evaluate(
            "save-integrity", "unspecified", PassingMetricsFor("save-integrity", "unspecified")).Passed);
        Assert.True(BenchmarkGateEvaluator.Evaluate(
            "accessibility", "unspecified", PassingMetricsFor("accessibility", "unspecified")).Passed);
    }

    [Fact]
    public void MemoryProfileRequiresCpuWorkingSetAllocationAndEveryCacheBudget()
    {
        BenchmarkMetric[] complete = PassingMetricsFor("memory", "unspecified");
        Assert.True(BenchmarkGateEvaluator.Evaluate("memory", "unspecified", complete).Passed);

        BenchmarkGateEvaluation missingAllocation = BenchmarkGateEvaluator.Evaluate(
            "memory",
            "unspecified",
            complete.Where(metric => metric.Name != "memory.allocation-rate-bytes-per-second").ToArray());

        Assert.False(missingAllocation.Passed);
        Assert.Contains(missingAllocation.Failures, failure =>
            failure.Contains("memory.allocation-rate-bytes-per-second", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderProfileRequiresQueueLatencyEvidence()
    {
        BenchmarkGateEvaluation evaluation = BenchmarkGateEvaluator.Evaluate(
            "render",
            "unspecified",
            [Metric("render", "ms", 100)]);

        Assert.False(evaluation.Passed);
        Assert.Contains(evaluation.Failures, failure => failure.Contains("render-queue-wait-ms", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryReleaseMetricRequiresThirtySamplesAndUniqueIdentity()
    {
        var shortMetric = new BenchmarkMetric(
            "launch",
            "ms",
            BenchmarkStatistics.Compute(Enumerable.Repeat(1d, 29).ToArray()));

        BenchmarkGateEvaluation evaluation = BenchmarkGateEvaluator.Evaluate(
            "launch",
            "unspecified",
            [shortMetric, shortMetric]);

        Assert.False(evaluation.Passed);
        Assert.Contains(evaluation.Failures, failure => failure.Contains("duplicated", StringComparison.Ordinal));
        Assert.Contains(evaluation.Failures, failure => failure.Contains("at least 30", StringComparison.Ordinal));
    }

    [Fact]
    public void ComparatorManifestFailsClosedUntilReferenceVersionsAreRecorded()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoFile("Benchmark", "comparators.manifest.json")));
        var root = document.RootElement;
        Assert.Equal(30, root.GetProperty("minimumIterations").GetInt32());
        Assert.Equal(3, root.GetProperty("comparators").GetArrayLength());
        Assert.All(root.GetProperty("comparators").EnumerateArray(), comparator =>
            Assert.StartsWith("RECORD_", comparator.GetProperty("exactVersion").GetString()));
    }

    [Fact(Skip = "Requires a dedicated reference machine, generated corpus and ETW collection; run eng/Invoke-EtwBenchmark.ps1 in the performance lane.")]
    public void ReferenceMachinePerformanceSlo()
    {
    }

    private static string RepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EXECUTION_SPEC.md")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(new[] { directory!.FullName }.Concat(parts).ToArray());
    }

    private static BenchmarkMetric Metric(string name, string unit, double value) =>
        new(name, unit, BenchmarkStatistics.Compute(Enumerable.Repeat(value, 30).ToArray()));

    private static BenchmarkMetric[] PassingMetricsFor(string scenario, string temperature) => scenario switch
    {
        "cached-navigation" => [Metric("cached-navigation", "ms", 49)],
        "zoom" =>
        [
            Metric("zoom.input-to-present-refresh-intervals", "intervals", 1),
            Metric("zoom.sharp-settled", "ms", 199)
        ],
        "close-memory" =>
        [
            Metric("memory.close-return-percent", "%", 10),
            Metric("memory.close-release-ms", "ms", 1999)
        ],
        "idle" =>
        [
            Metric("idle.cpu-percent", "%", .49),
            Metric("idle.recurring-disk-writes", "count", 0)
        ],
        "reliability" =>
        [
            Metric("reliability.crash-free-percent", "%", 99.9),
            Metric("reliability.hang-free-percent", "%", 99.95)
        ],
        "first-page-10000" =>
        [
            Metric("first-page-10000", "ms", 999),
            Metric("virtualization.realized-controls", "count", 12),
            Metric("virtualization.page-subscriptions", "count", 12),
            Metric("virtualization.uncached-raster-leases", "count", 2)
        ],
        "save-integrity" =>
        [
            Metric("save.damaged-originals", "count", 0),
            Metric("save.fault-injection-count", "count", 10_000)
        ],
        "accessibility" =>
        [
            Metric("accessibility.critical-findings", "count", 0),
            Metric("accessibility.high-findings", "count", 0),
            Metric("accessibility.incomplete-keyboard-workflows", "count", 0),
            Metric("accessibility.incomplete-narrator-workflows", "count", 0)
        ],
        "memory" =>
        [
            Metric("memory.private-bytes", "bytes", 250 * 1024 * 1024),
            Metric("memory.ui.private-bytes", "bytes", 150 * 1024 * 1024),
            Metric("memory.worker.private-bytes", "bytes", 100 * 1024 * 1024),
            Metric("memory.working-set-bytes", "bytes", 240 * 1024 * 1024),
            Metric("memory.cpu-ms", "ms", 10),
            Metric("memory.allocation-rate-bytes-per-second", "bytes-per-second", 1024),
            Metric("memory.shared-mappings-bytes", "bytes", 8 * 1024 * 1024),
            Metric("memory.gpu-allocation-bytes", "bytes", 90 * 1024 * 1024),
            Metric("memory.cache-gpu-bytes", "bytes", 90 * 1024 * 1024),
            Metric("memory.cache-cpu-bytes", "bytes", 30 * 1024 * 1024),
            Metric("memory.cache-thumbnails-bytes", "bytes", 15 * 1024 * 1024),
            Metric("memory.cache-geometry-bytes", "bytes", 15 * 1024 * 1024)
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
    };
}
