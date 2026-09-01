using System.Globalization;

namespace ElliePdf.Benchmarking;

/// <summary>
/// The small, privacy-safe command line contract used to opt in to an in-product
/// benchmark run. This type intentionally has no dependency on WinUI so it can be
/// tested on every build target and remains safe for NativeAOT.
/// </summary>
public sealed record BenchmarkDriverRequest(string Scenario)
{
    private static readonly HashSet<string> SupportedScenarios = new(StringComparer.Ordinal)
    {
        "open", "first-page", "first-page-10000", "cached-navigation", "render", "random-jump", "scroll", "zoom", "search", "memory", "cancellation"
    };

    public const string FixtureEnvironmentVariable = "ELLIEPDF_BENCHMARK_FIXTURE";

    public static bool TryParse(IReadOnlyList<string>? arguments, out BenchmarkDriverRequest? request)
    {
        request = null;
        if (arguments is null)
        {
            return false;
        }

        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], "--benchmark-driver", StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 >= arguments.Count || !SupportedScenarios.Contains(arguments[index + 1]))
            {
                return false;
            }

            request = new BenchmarkDriverRequest(arguments[index + 1]);
            return true;
        }

        return false;
    }
}

/// <summary>Formats only the fixed stdout messages accepted by the benchmark collector.</summary>
public static class BenchmarkDriverProtocol
{
    public const string MetricPrefix = "ELLIEPDF_BENCHMARK_METRIC ";
    public const string ReadyPrefix = "ELLIEPDF_READY ";

    private static readonly IReadOnlyDictionary<string, string> MetricUnits =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["open.completed"] = "ms",
            ["cached-navigation"] = "ms",
            ["first-page-10000"] = "ms",
            ["first-page.presented"] = "ms",
            ["render.completed"] = "ms",
            ["render-queue-wait-ms"] = "ms",
            ["random-jump.preview"] = "ms",
            ["random-jump.preview-cached"] = "ms",
            ["random-jump.preview-uncached"] = "ms",
            ["random-jump.sharp"] = "ms",
            ["scroll.frame"] = "ms",
            ["scroll.dropped-frames-percent"] = "%",
            ["zoom.input-to-present"] = "ms",
            ["zoom.input-to-present-refresh-intervals"] = "intervals",
            ["zoom.sharp-settled"] = "ms",
            ["search.completed"] = "ms",
            ["search.first-result"] = "ms",
            ["search.first-before-complete"] = "bool",
            ["cancellation.stale-rejection"] = "ms",
            ["cancellation.active-yield"] = "ms",
            ["memory.cache-gpu-bytes"] = "bytes",
            ["memory.cache-cpu-bytes"] = "bytes",
            ["memory.cache-thumbnails-bytes"] = "bytes",
            ["memory.cache-geometry-bytes"] = "bytes",
            ["memory.allocation-rate-bytes-per-second"] = "bytes-per-second",
            ["memory.private-bytes"] = "bytes",
            ["memory.ui.private-bytes"] = "bytes",
            ["memory.worker.private-bytes"] = "bytes",
            ["memory.working-set-bytes"] = "bytes",
            ["memory.cpu-ms"] = "ms",
            ["memory.shared-mappings-bytes"] = "bytes",
            ["memory.gpu-allocation-bytes"] = "bytes",
            ["virtualization.realized-controls"] = "count",
            ["virtualization.page-subscriptions"] = "count",
            ["virtualization.uncached-raster-leases"] = "count"
        };

    public static bool IsSupportedMetric(string? name, string? unit) =>
        name is not null && unit is not null && MetricUnits.TryGetValue(name, out var expectedUnit)
        && string.Equals(expectedUnit, unit, StringComparison.Ordinal);

    public static string FormatMetric(string name, string unit, double value)
    {
        if (!IsSupportedMetric(name, unit))
        {
            throw new ArgumentException("The benchmark metric is not part of the fixed protocol.", nameof(name));
        }
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return MetricPrefix + "{\"name\":\"" + name + "\",\"unit\":\"" + unit
            + "\",\"value\":" + value.ToString("R", CultureInfo.InvariantCulture) + "}";
    }

    public static string FormatReady(BenchmarkDriverRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!BenchmarkDriverRequest.TryParse(["--benchmark-driver", request.Scenario], out _))
        {
            throw new ArgumentException("The benchmark scenario is not part of the fixed protocol.", nameof(request));
        }

        return ReadyPrefix + request.Scenario;
    }
}
