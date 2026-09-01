using System.Globalization;
using System.Text.Json;

namespace ElliePdf.Benchmark;

/// <summary>
/// Parses the deliberately small stdout protocol used by a benchmark driver.
/// A driver may emit one line per metric:
/// <c>ELLIEPDF_BENCHMARK_METRIC {"name":"first-page.presented","unit":"ms","value":123.4}</c>.
/// Names and units are constrained so paths, document text and other identifying data cannot become
/// part of an aggregate report by accident.
/// </summary>
public static class BenchmarkMetricProtocol
{
    public const string MetricPrefix = "ELLIEPDF_BENCHMARK_METRIC ";

    private static readonly IReadOnlyDictionary<string, string> AllowedMetrics =
        new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["launch.interactive"] = "ms",
        ["activation.completed"] = "ms",
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
        ["search.first-result"] = "ms",
        ["search.completed"] = "ms",
        ["search.first-before-complete"] = "bool",
        ["scroll.frame"] = "ms",
        ["scroll.dropped-frames"] = "count",
        ["scroll.dropped-frames-percent"] = "%",
        ["zoom.input-to-present"] = "ms",
        ["zoom.input-to-present-refresh-intervals"] = "intervals",
        ["zoom.sharp-settled"] = "ms",
        ["cancellation.stale-rejection"] = "ms",
        ["cancellation.active-yield"] = "ms",
        ["memory.gpu-allocation-bytes"] = "bytes",
        ["memory.private-bytes"] = "bytes",
        ["memory.ui.private-bytes"] = "bytes",
        ["memory.worker.private-bytes"] = "bytes",
        ["memory.working-set-bytes"] = "bytes",
        ["memory.cpu-ms"] = "ms",
        ["memory.shared-mappings-bytes"] = "bytes",
        ["memory.allocation-rate-bytes-per-second"] = "bytes-per-second",
        ["memory.cache-gpu-bytes"] = "bytes",
        ["memory.cache-cpu-bytes"] = "bytes",
        ["memory.cache-thumbnails-bytes"] = "bytes",
        ["memory.cache-geometry-bytes"] = "bytes",
        ["memory.close-return-percent"] = "%",
        ["memory.close-release-ms"] = "ms",
        ["virtualization.realized-controls"] = "count",
        ["virtualization.page-subscriptions"] = "count",
        ["virtualization.uncached-raster-leases"] = "count",
        ["idle.cpu-percent"] = "%",
        ["idle.recurring-disk-writes"] = "count",
        ["save.damaged-originals"] = "count",
        ["save.fault-injection-count"] = "count",
        ["reliability.crash-free-percent"] = "%",
        ["reliability.hang-free-percent"] = "%",
        ["accessibility.critical-findings"] = "count",
        ["accessibility.high-findings"] = "count",
        ["accessibility.incomplete-keyboard-workflows"] = "count",
        ["accessibility.incomplete-narrator-workflows"] = "count",
        ["pixel-upload.presented"] = "ms"
    };

    public static bool TryParse(string? line, out BenchmarkMetricPoint point)
    {
        point = default;
        if (line is null || !line.StartsWith(MetricPrefix, StringComparison.Ordinal))
            return false;

        try
        {
            using var document = JsonDocument.Parse(line[MetricPrefix.Length..]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("name", out var name) ||
                !root.TryGetProperty("unit", out var unit) ||
                !root.TryGetProperty("value", out var value) ||
                name.ValueKind != JsonValueKind.String ||
                unit.ValueKind != JsonValueKind.String ||
                value.ValueKind != JsonValueKind.Number ||
                !value.TryGetDouble(out var number) ||
                !double.IsFinite(number))
                return false;

            var metricName = name.GetString();
            var metricUnit = unit.GetString();
            if (!IsSafeToken(metricName, 128) || !IsSafeToken(metricUnit, 32) ||
                !AllowedMetrics.TryGetValue(metricName!, out string? requiredUnit) ||
                !string.Equals(metricUnit, requiredUnit, StringComparison.Ordinal))
                return false;

            point = new BenchmarkMetricPoint(metricName!, metricUnit!, number);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsSafeToken(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            return false;

        foreach (var character in value)
        {
            if (!(char.IsLetterOrDigit(character) || character is '.' or '-' or '_' or '%'))
                return false;
        }

        return true;
    }
}

public readonly record struct BenchmarkMetricPoint(string Name, string Unit, double Value)
{
    public override string ToString() =>
        $"{Name}={Value.ToString(CultureInfo.InvariantCulture)} {Unit}";
}
