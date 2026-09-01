using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElliePdf.Telemetry;

namespace ElliePdf.Benchmark;

/// <summary>Measures a real target process when --target is supplied. The no-target mode is an explicitly labelled backend proxy.</summary>
internal static class Program
{
    private const int MinimumIterations = 30;
    private const int BootstrapSamples = 10_000;
    private const int BootstrapSeed = 1729;

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            if (options.Help) { PrintUsage(); return 0; }
            var manifest = await LoadAndValidateManifestAsync(options.ManifestPath ?? FindDefaultManifest());
            ComparatorManifest? comparatorManifest = null;
            if (options.RequireComparators || options.Mode == "sample")
            {
                var comparatorPath = File.Exists("Benchmark/comparators.manifest.json") ? "Benchmark/comparators.manifest.json" : Path.Combine(AppContext.BaseDirectory, "comparators.manifest.json");
                comparatorManifest = await ComparatorManifestLoader.LoadAndValidateAsync(comparatorPath, requireRecordedValues: true);
                if (options.TargetPath is null)
                    throw new InvalidOperationException("Release benchmark gates require --target process evidence; backend-proxy output is never releasable.");
                CorpusFixture selectedFixture = SelectFixture(manifest, options.FixtureId);
                ComparatorManifestLoader.ValidateScenarioEvidence(
                    comparatorManifest,
                    BenchmarkGateEvaluator.ComparatorMetricNamesFor(options.Scenario, options.Temperature),
                    selectedFixture.Sha256);
            }
            var iterations = Math.Max(MinimumIterations, options.Iterations);
            var warmups = Math.Max(0, options.Warmups);
            var values = options.TargetPath is null ? await RunProxyAsync(manifest, options, iterations, warmups) : await RunProcessAsync(manifest, options, iterations, warmups);
            var metrics = values.Select(pair => new BenchmarkMetric(pair.Key, pair.Value.Unit, BenchmarkStatistics.Compute(pair.Value.Values, BootstrapSamples, BootstrapSeed))).ToArray();
            var report = new BenchmarkReport("1.0", Guid.NewGuid().ToString("N"), Redact(options.MachineClass), Redact(options.PowerMode), DateTimeOffset.UtcNow, metrics)
            {
                Temperature = options.Temperature
            };
            var outputPath = options.OutputPath ?? Path.Combine("artifacts", "benchmark-report.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllTextAsync(outputPath, report.ToJson() + Environment.NewLine, Encoding.UTF8);
            if (options.RequireComparators || options.Mode == "sample")
            {
                var comparatorP95 = comparatorManifest!.Comparators
                    .SelectMany(static comparator => comparator.BaselineP95 ?? new Dictionary<string, double>())
                    .GroupBy(static pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(static group => group.Key, static group => group.Min(pair => pair.Value), StringComparer.Ordinal);
                var gate = BenchmarkGateEvaluator.Evaluate(options.Scenario, options.Temperature, metrics, comparatorP95);
                if (!gate.Passed)
                    throw new InvalidOperationException("Release benchmark gate failed: " + string.Join("; ", gate.Failures));
            }
            Console.WriteLine($"Wrote {options.Mode} benchmark report ({iterations} iterations, {warmups} warmups): {outputPath}");
            Console.WriteLine(options.TargetPath is null ? "EVIDENCE mode=backend-proxy status=non-product; supply --target for executable evidence." : $"EVIDENCE mode=process-boundary target={Path.GetFileName(options.TargetPath)} status=measured");
            Console.WriteLine($"EVIDENCE cache-temperature={options.Temperature} procedure=operator-documented");
            foreach (var metric in metrics) Console.WriteLine($"SLO metric={metric.Name} p95={metric.Statistics.P95.ToString("F3", CultureInfo.InvariantCulture)} unit={metric.Unit} stable={metric.Statistics.IsStable}");
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or JsonException or NotSupportedException or InvalidOperationException or TimeoutException)
        {
            Console.Error.WriteLine($"benchmark: {ex.Message}");
            return 2;
        }
    }

    private sealed class MetricSeries(string unit, IEnumerable<double>? values = null)
    {
        public string Unit { get; } = unit;
        public List<double> Values { get; } = values?.ToList() ?? [];
    }

    private static async Task<Dictionary<string, MetricSeries>> RunProcessAsync(CorpusManifest manifest, Options options, int iterations, int warmups)
    {
        var fixture = SelectFixture(manifest, options.FixtureId);
        var fixturePath = await ResolveFixturePathAsync(manifest, fixture, options.CorpusRoot);
        var all = new Dictionary<string, MetricSeries>(StringComparer.Ordinal);
        for (var i = 0; i < warmups + iterations; i++)
        {
            var sample = await RunOneProcessAsync(options, fixturePath, i - warmups);
            if (i < warmups) continue;
            foreach (var item in sample)
            {
                if (!all.TryGetValue(item.Name, out var series))
                    all[item.Name] = series = new MetricSeries(item.Unit);
                else if (!series.Unit.Equals(item.Unit, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Target metric '{item.Name}' changed units during the run.");
                series.Values.Add(item.Value);
            }
        }
        foreach (var pair in all)
            if (pair.Value.Values.Count != iterations)
                throw new InvalidOperationException($"Target metric '{pair.Key}' produced {pair.Value.Values.Count} measured values; expected {iterations}. Missing samples are not accepted as release evidence.");
        return all;
    }

    private static async Task<IReadOnlyList<BenchmarkMetricPoint>> RunOneProcessAsync(Options options, string fixturePath, int iteration)
    {
        var arguments = options.TargetArguments.Replace("{fixture}", fixturePath, StringComparison.Ordinal).Replace("{iteration}", iteration.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        // Stdout is the bounded readiness/metric protocol. Do not redirect stderr: a noisy target
        // must never be able to fill an unread pipe and turn a benchmark timeout into a deadlock.
        var psi = new ProcessStartInfo(options.TargetPath!) { Arguments = arguments, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = false, CreateNoWindow = true };
        psi.Environment["ELLIEPDF_BENCHMARK_FIXTURE"] = fixturePath;
        psi.Environment["ELLIEPDF_BENCHMARK_ITERATION"] = iteration.ToString(CultureInfo.InvariantCulture);
        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start benchmark target.");
        var output = new TargetOutputCollector(process, options.ReadyRegex);
        var outputTask = output.ConsumeAsync();
        // Launch/activation CPU includes process creation and early startup. There is no safe
        // parent snapshot before Process.Start, so an empty baseline is intentionally used.
        var baseline = ProcessTreeMetricSnapshot.Empty;
        try
        {
            var ready = options.ReadyRegex is null ? await WaitForInputIdleAsync(process, options.Timeout) : await output.WaitForReadyAsync(options.Timeout);
            stopwatch.Stop();
            if (!ready) throw new TimeoutException($"Target did not become ready within {options.Timeout.TotalMilliseconds:F0} ms (iteration {iteration}).");
            if (options.Settle > TimeSpan.Zero) await Task.Delay(options.Settle);
            if (process.HasExited && process.ExitCode != 0)
                throw new InvalidOperationException($"Target exited with code {process.ExitCode} after readiness (iteration {iteration}).");
            var current = ProcessTreeMetricSnapshot.Capture(process.Id);
            var points = new List<BenchmarkMetricPoint>
            {
                new(options.Scenario, "ms", stopwatch.Elapsed.TotalMilliseconds),
                new($"{options.Scenario}.cpu-ms", "ms", current.CpuDeltaMilliseconds(baseline)),
                new($"{options.Scenario}.private-bytes", "bytes", current.PrivateBytes),
                new($"{options.Scenario}.working-set-bytes", "bytes", current.WorkingSetBytes),
                new($"{options.Scenario}.ui.private-bytes", "bytes", current.RootPrivateBytes(process.Id)),
                new($"{options.Scenario}.worker.private-bytes", "bytes", current.ChildPrivateBytes(process.Id))
            };
            // Process-tree counters are collected by the harness and are authoritative for
            // aggregate/private/working-set/CPU memory evidence. A target may emit the
            // same fixed names for direct-driver use, but it cannot replace those trusted
            // process observations. Operation timing emitted under the scenario name is
            // intentionally allowed to replace process-start-to-ready timing.
            foreach (var metric in output.SnapshotMetrics())
            {
                // The product calls its operation event render.completed to match ETW.
                // Normalize that one metric to the release gate name so startup-to-ready
                // time cannot masquerade as the actual page-render latency.
                var normalized = options.Scenario == "render" && metric.Name == "render.completed"
                    ? new BenchmarkMetricPoint("render", metric.Unit, metric.Value)
                    : metric;
                var existing = points.FindIndex(point => string.Equals(point.Name, normalized.Name, StringComparison.Ordinal));
                if (existing < 0)
                {
                    points.Add(normalized);
                }
                else if (string.Equals(normalized.Name, options.Scenario, StringComparison.Ordinal))
                {
                    points[existing] = normalized;
                }
            }
            return points;
        }
        finally
        {
            TryTerminate(process);
            try { await outputTask.WaitAsync(TimeSpan.FromMilliseconds(500)); } catch (TimeoutException) { }
        }
    }

    private static async Task<bool> WaitForInputIdleAsync(Process process, TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows()) return await WaitForExitOrTimeoutAsync(process, timeout);
        try { return await Task.Run(() => process.WaitForInputIdle(timeout)); }
        catch (InvalidOperationException) { return !process.HasExited; }
        catch (NotSupportedException) { return !process.HasExited; }
    }

    private static async Task<bool> WaitForExitOrTimeoutAsync(Process process, TimeSpan timeout)
    {
        await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(timeout));
        return process.HasExited;
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static async Task<Dictionary<string, MetricSeries>> RunProxyAsync(CorpusManifest manifest, Options options, int iterations, int warmups)
    {
        var payload = Encoding.UTF8.GetBytes(string.Join('|', manifest.Fixtures.Select(f => $"{f.Id}:{f.Kind}:{f.Pages}:{f.Sha256}")));
        for (var i = 0; i < warmups; i++) _ = ExerciseProxy(payload, options.Scenario);
        var elapsed = new List<double>(iterations);
        for (var i = 0; i < iterations; i++) elapsed.Add(ExerciseProxy(payload, options.Scenario));
        return new() { [options.Scenario] = new MetricSeries("ms", elapsed) };
    }

    private static double ExerciseProxy(byte[] payload, string scenario)
    {
        var timer = Stopwatch.StartNew();
        if (scenario == "pixel-upload")
        {
            const int bytes = 512 * 512 * 4;
            var source = new byte[bytes]; var destination = new byte[bytes];
            source.AsSpan().Fill(0x7f); source.AsSpan().CopyTo(destination); GC.KeepAlive(destination);
        }
        else _ = SHA256.HashData(SHA256.HashData(payload));
        timer.Stop(); return timer.Elapsed.TotalMilliseconds;
    }

    private static CorpusFixture SelectFixture(CorpusManifest manifest, string? id) => id is null ? manifest.Fixtures[0] : manifest.Fixtures.FirstOrDefault(f => f.Id == id) ?? throw new ArgumentException($"Fixture '{id}' was not found in the manifest.");

    private static async Task<string> ResolveFixturePathAsync(CorpusManifest manifest, CorpusFixture fixture, string? root)
    {
        var path = Path.GetFullPath(Path.Combine(root ?? "testdata/generated", fixture.File ?? (fixture.Id + ".pdf")));
        if (!File.Exists(path)) throw new FileNotFoundException("Fixture file was not found.", path);
        if (!fixture.Sha256.StartsWith("RECORD_", StringComparison.OrdinalIgnoreCase) && !await manifest.VerifyFileAsync(fixture, path)) throw new ArgumentException($"Fixture '{fixture.Id}' failed SHA-256 validation.");
        return path;
    }

    private static async Task<CorpusManifest> LoadAndValidateManifestAsync(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Corpus manifest was not found.", path);
        var manifest = CorpusManifest.Load(await File.ReadAllTextAsync(path));
        if (manifest.SchemaVersion != "1.0" || !manifest.HashAlgorithm.Equals("SHA-256", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Unsupported corpus manifest schema or hash algorithm.");
        if (manifest.Fixtures.Count == 0) throw new ArgumentException("Corpus manifest must contain at least one fixture.");
        foreach (var fixture in manifest.Fixtures) if (string.IsNullOrWhiteSpace(fixture.Id) || string.IsNullOrWhiteSpace(fixture.Sha256)) throw new ArgumentException("Corpus fixtures require IDs and SHA-256 hashes.");
        return manifest;
    }

    private static string FindDefaultManifest() => File.Exists("testdata/manifest.json") ? "testdata/manifest.json" : throw new FileNotFoundException("Use --manifest to specify a corpus manifest.");
    private static string Redact(string value) => value.Contains('\\') || value.Contains('/') || value.Contains(':') ? "redacted" : value.Trim();
    private static void PrintUsage() => Console.WriteLine("ElliePdf benchmark\n  --target <exe>       Measure a real executable process\n  --target-arguments <a> Arguments; {fixture} and {iteration} expand\n  --ready-regex <rx>   Readiness line emitted by target (otherwise Win32 input idle)\n  --scenario <name>     launch|activation|open|first-page|first-page-10000|cached-navigation|render|random-jump|zoom|scroll|cancellation|search|memory|close-memory|idle|save-integrity|reliability|accessibility\n  --fixture <id>        Manifest fixture ID\n  --corpus-root <dir>   Fixture directory (default: testdata/generated)\n  --timeout-ms <n>      Readiness timeout (default: 10000)\n  --settle-ms <n>       Delay after readiness before memory sample\n  --mode <sample|self-test>  sample requires recorded comparators\n  --temperature <cold|warm|unspecified>  Cache temperature recorded in the report\n  --output <path>       JSON report\n  --iterations <n>      Measured iterations (minimum 30)\n  --warmups <n>         Warmup iterations\n  --require-comparators  Require exact comparator evidence\n  --help\n\nTarget protocol: emit 'ELLIEPDF_BENCHMARK_METRIC {\"name\":\"first-page.presented\",\"unit\":\"ms\",\"value\":123.4}' on stdout. Metric names and units come from a fixed privacy-safe schema.");

    private sealed record Options(string? ManifestPath, string? TargetPath, string TargetArguments, string? ReadyRegex, string Scenario, string? FixtureId, string? CorpusRoot, TimeSpan Timeout, TimeSpan Settle, string? OutputPath, int Iterations, int Warmups, string MachineClass, string PowerMode, string Temperature, string Mode, bool RequireComparators, bool Help)
    {
        public static Options Parse(string[] args)
        {
            string? manifest = null, target = null, ready = null, fixture = null, root = null, output = null; var targetArgs = "{fixture}"; var scenario = "launch"; var iterations = 30; var warmups = 3; var timeout = 10000; var settle = 0; var machine = "unknown"; var power = "unknown"; var temperature = "unspecified"; var mode = "self-test"; var require = false; var help = false;
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i]; if (a is "--help" or "-h") { help = true; continue; } if (a == "--require-comparators") { require = true; continue; }
                if (!a.StartsWith("--", StringComparison.Ordinal) || ++i >= args.Length) throw new ArgumentException($"Missing value for {a}."); var v = args[i];
                switch (a) { case "--manifest": manifest = v; break; case "--target": target = v; break; case "--target-arguments": targetArgs = v; break; case "--ready-regex": ready = v; break; case "--scenario": scenario = v; break; case "--fixture": fixture = v; break; case "--corpus-root": root = v; break; case "--output": output = v; break; case "--machine": machine = v; break; case "--power": power = v; break; case "--temperature" when v is "cold" or "warm" or "unspecified": temperature = v; break; case "--mode" when v is "sample" or "self-test": mode = v; break; case "--iterations" when int.TryParse(v, out var n) && n > 0: iterations = n; break; case "--warmups" when int.TryParse(v, out var w) && w >= 0: warmups = w; break; case "--timeout-ms" when int.TryParse(v, out var t) && t > 0: timeout = t; break; case "--settle-ms" when int.TryParse(v, out var s) && s >= 0: settle = s; break; default: throw new ArgumentException($"Invalid option or value: {a} {v}"); }
            }
            var valid = new[] { "launch", "activation", "open", "first-page", "first-page-10000", "cached-navigation", "render", "random-jump", "zoom", "scroll", "cancellation", "search", "memory", "close-memory", "idle", "save-integrity", "reliability", "accessibility", "save", "pixel-upload" }; if (!valid.Contains(scenario, StringComparer.Ordinal)) throw new ArgumentException($"Unknown scenario '{scenario}'.");
            return new(manifest, target, targetArgs, ready, scenario, fixture, root, TimeSpan.FromMilliseconds(timeout), TimeSpan.FromMilliseconds(settle), output, iterations, warmups, machine, power, temperature, mode, require, help);
        }
    }
}
