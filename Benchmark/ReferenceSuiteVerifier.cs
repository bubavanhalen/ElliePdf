using System.Security.Cryptography;
using System.Text.Json;
using ElliePdf.Telemetry;

namespace ElliePdf.Benchmark;

internal static class ReferenceSuiteVerifier
{
    private const long MiB = 1024 * 1024;
    private static readonly (string Scenario, string Temperature)[] Scenarios =
    [
        ("launch", "cold"), ("activation", "cold"), ("activation", "warm"),
        ("first-page", "cold"), ("first-page", "warm"), ("first-page-10000", "cold"),
        ("cached-navigation", "warm"), ("render", "cold"), ("random-jump", "warm"),
        ("zoom", "warm"), ("scroll", "warm"), ("cancellation", "warm"),
        ("search", "warm"), ("memory", "warm"), ("close-memory", "warm"), ("idle", "warm")
    ];

    public static async Task<int> VerifyAsync(string suitePath, string packagePath, string sourceCommit,
        CorpusManifest corpus, ComparatorManifest comparators)
    {
        Require(IsHex(sourceCommit, 40), "Source commit must be a full Git SHA.");
        Require(Path.GetExtension(packagePath).Equals(".msix", StringComparison.OrdinalIgnoreCase),
            "Candidate package must be an MSIX file.");
        using var suite = await ReadJsonAsync(suitePath);
        var index = suite.RootElement;
        Require(GetString(index, "schemaVersion") == "1.0-reference-suite", "Unsupported reference suite schema.");
        Require(GetString(index, "sourceCommit").Equals(sourceCommit, StringComparison.OrdinalIgnoreCase),
            "Reference suite source commit does not match the candidate.");
        await using var package = File.OpenRead(packagePath);
        var packageHash = Convert.ToHexString(await SHA256.HashDataAsync(package));
        Require(GetString(index, "packageSha256").Equals(packageHash, StringComparison.OrdinalIgnoreCase),
            "Reference suite package SHA-256 does not match the candidate.");

        var pending = (from scale in new[] { 100, 150, 200 }
                       from entry in Scenarios
                       select (entry.Scenario, entry.Temperature, Scale: scale)).ToHashSet();
        var root = Path.GetDirectoryName(Path.GetFullPath(suitePath))!;
        var reportPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fixtures = corpus.Fixtures.ToDictionary(fixture => fixture.Id, StringComparer.Ordinal);
        Require(fixtures.Values.Any(IsColdActivationFixture),
            "The approved corpus has no 10–20 MiB fixture with a declared byte size for cold activation.");
        Require(!comparators.MachineClass.Contains("self-test", StringComparison.OrdinalIgnoreCase),
            "Synthetic collector self-tests cannot qualify as reference benchmarks.");

        foreach (var run in index.GetProperty("runs").EnumerateArray())
        {
            var scenario = GetString(run, "scenario");
            var temperature = GetString(run, "temperature");
            var scale = run.GetProperty("displayScalePercent").GetInt32();
            var description = $"{scenario}/{temperature}/{scale}%";
            Require(pending.Remove((scenario, temperature, scale)), $"Unexpected or duplicate reference run {description}.");

            var fixtureId = GetString(run, "fixtureId");
            var fixtureHash = GetString(run, "fixtureSha256");
            Require(fixtures.TryGetValue(fixtureId, out var fixture) && IsHex(fixtureHash, 64) &&
                fixtureHash.Equals(fixture.Sha256, StringComparison.OrdinalIgnoreCase) &&
                IsScenarioFixture(fixture, scenario, temperature), $"Invalid corpus fixture for {description}.");
            var comparatorMetrics = BenchmarkGateEvaluator.ComparatorMetricNamesFor(scenario, temperature);
            ComparatorManifestLoader.ValidateScenarioEvidence(comparators, comparatorMetrics, fixtureHash);
            var bestP95 = comparatorMetrics.ToDictionary(metric => metric,
                metric => comparators.Comparators.Min(comparator => comparator.BaselineP95![metric]), StringComparer.Ordinal);

            var path = ResolveReportPath(root, GetString(run, "report"));
            Require(reportPaths.Add(path), $"Duplicate reference report path for {description}.");
            using var report = await ReadJsonAsync(path);
            var runId = VerifyReport(report.RootElement, scenario, temperature, comparators, bestP95);
            Require(runIds.Add(runId), $"Duplicate ETW run ID for {description}.");
        }

        Require(pending.Count == 0, "Missing reference runs: " + string.Join(", ", pending
            .OrderBy(entry => entry.Scale).ThenBy(entry => entry.Scenario).ThenBy(entry => entry.Temperature)
            .Select(entry => $"{entry.Scenario}/{entry.Temperature}/{entry.Scale}%")));
        return runIds.Count;
    }

    private static bool IsScenarioFixture(CorpusFixture fixture, string scenario, string temperature) =>
        fixture.Pages > 0 && scenario switch
        {
            "activation" when temperature == "cold" => IsColdActivationFixture(fixture),
            "first-page-10000" => fixture.Pages == 10_000,
            "memory" => fixture.Pages == 1_000,
            _ => true
        };

    private static bool IsColdActivationFixture(CorpusFixture fixture) =>
        fixture.Pages > 0 && fixture.SizeBytes is >= 10 * MiB and <= 20 * MiB;

    private static string ResolveReportPath(string root, string relativePath)
    {
        Require(!Path.IsPathRooted(relativePath) && Path.GetExtension(relativePath).Equals(".json", StringComparison.OrdinalIgnoreCase),
            "Reference report must use a relative JSON path.");
        var path = Path.GetFullPath(relativePath, root);
        var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        Require(path.StartsWith(rootPrefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
            "Reference report path must stay inside the suite directory.");
        // Reject linked input paths so aliases cannot escape containment or bypass uniqueness.
        for (var component = path; component.Length > root.Length; component = Path.GetDirectoryName(component)!)
            Require((File.GetAttributes(component) & FileAttributes.ReparsePoint) == 0, "Reference report path must not traverse a link.");
        return path;
    }

    private static string VerifyReport(JsonElement report, string scenario, string temperature,
        ComparatorManifest comparators, IReadOnlyDictionary<string, double> bestP95)
    {
        var runId = GetString(report, "runId");
        Require(GetString(report, "schemaVersion") == "1.0-etw" && IsHex(runId, 32) &&
            report.GetProperty("startedUtc").TryGetDateTimeOffset(out _), "Invalid reference ETW report format.");
        Require(GetString(report, "scenario") == scenario && GetString(report, "temperature") == temperature,
            "ETW report scenario or temperature does not match the suite run.");
        Require(GetString(report, "machineClass") == comparators.MachineClass && GetString(report, "powerMode") == comparators.PowerMode,
            "ETW report conditions do not match the frozen reference manifest.");
        var collection = report.GetProperty("collection");
        var iterations = collection.GetProperty("measuredIterations").GetInt32();
        var eventCount = collection.GetProperty("eventCount").GetInt32();
        Require(GetString(collection, "provider") == "ElliePdf" && GetString(collection, "temperature") == temperature &&
            iterations >= comparators.MinimumIterations && eventCount >= iterations && collection.GetProperty("warmups").GetInt32() >= 0 &&
            collection.GetProperty("bootstrapSamples").GetInt32() == 10_000 && collection.GetProperty("bootstrapSeed").GetInt32() == 1729,
            "ETW report collection does not meet the frozen reference conditions.");

        var metrics = new List<BenchmarkMetric>();
        foreach (var metric in report.GetProperty("metrics").EnumerateArray())
        {
            var name = GetString(metric, "name");
            metrics.Add(new(name, GetString(metric, "unit"), ReadStatistics(metric, name, iterations, eventCount)));
        }
        var evaluation = BenchmarkGateEvaluator.Evaluate(scenario, temperature, metrics, bestP95);
        Require(evaluation.Passed, $"Reference ETW gates failed for {scenario}/{temperature}: {string.Join("; ", evaluation.Failures)}.");
        var gates = report.GetProperty("gates");
        Require(GetString(gates, "overall") == "pass" && GetString(gates, "requiredMetrics") == "pass" &&
            GetString(gates, "confidenceAndSlo") == "pass" && gates.GetProperty("failures").GetArrayLength() == 0,
            "ETW report reports failed collection or scenario gates.");
        if (scenario == "scroll")
            VerifyFrameAccounting(report.GetProperty("frameAccounting"), metrics);
        return runId;
    }

    private static BenchmarkStatistics ReadStatistics(JsonElement metric, string name, int iterations, int eventCount)
    {
        var stats = metric.GetProperty("statistics");
        var count = stats.GetProperty("sampleCount").GetInt32();
        var median = GetNumber(stats, "median");
        var p95 = GetNumber(stats, "p95");
        var p99 = GetNumber(stats, "p99");
        var minimum = GetNumber(stats, "minimum");
        var maximum = GetNumber(stats, "maximum");
        var interval = stats.GetProperty("bootstrap95");
        var lower = GetNumber(interval, "lower");
        var upper = GetNumber(interval, "upper");
        var width = GetNumber(interval, "width");
        var result = new BenchmarkStatistics(count, median, p95, p99, new(lower, upper)) { Minimum = minimum, Maximum = maximum };
        var sufficientSamples = name == "scroll.frame" ? count >= iterations && count <= eventCount : count == iterations;
        Require(sufficientSamples && minimum <= median && median <= p95 && p95 <= p99 && p99 <= maximum &&
            minimum <= lower && lower <= upper && upper <= maximum && Math.Abs(width - result.Bootstrap95.Width) <= 0.000002 &&
            result.IsStable && stats.GetProperty("isStable").GetBoolean() && GetString(metric.GetProperty("slo"), "status") == "pass",
            $"Incomplete, inconsistent or unstable ETW statistics for '{name}'.");
        return result;
    }

    private static void VerifyFrameAccounting(JsonElement accounting, IReadOnlyList<BenchmarkMetric> metrics)
    {
        var dropped = GetNumber(accounting, "droppedFrames");
        var presented = GetNumber(accounting, "presentedFrames");
        var frameSamples = metrics.Single(metric => metric.Name == "scroll.frame").Statistics.SampleCount;
        var percent = GetNumber(accounting, "droppedPercent");
        Require(presented == frameSamples && dropped == Math.Truncate(dropped) &&
            Math.Abs(percent - dropped / (presented + dropped) * 100) <= 0.000001 && percent < 1,
            "ETW scroll report has inconsistent frame accounting or misses the drop-rate gate.");
    }

    private static async Task<JsonDocument> ReadJsonAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { AllowDuplicateProperties = false });
    }

    private static string GetString(JsonElement element, string property) =>
        element.GetProperty(property).GetString() is { Length: > 0 } value ? value : throw new JsonException($"Missing '{property}' value.");

    private static double GetNumber(JsonElement element, string property)
    {
        var value = element.GetProperty(property).GetDouble();
        Require(double.IsFinite(value) && value >= 0, $"Invalid non-negative ETW value '{property}'.");
        return value;
    }

    private static bool IsHex(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
