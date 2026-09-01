using ElliePdf.Benchmark;
using Xunit;

namespace ElliePdf.PerformanceTests;

public sealed class BenchmarkMetricProtocolTests
{
    [Fact]
    public void ParsesStructuredTargetMetric()
    {
        var line = "ELLIEPDF_BENCHMARK_METRIC {\"name\":\"first-page.presented\",\"unit\":\"ms\",\"value\":123.5}";

        Assert.True(BenchmarkMetricProtocol.TryParse(line, out var point));
        Assert.Equal("first-page.presented", point.Name);
        Assert.Equal("ms", point.Unit);
        Assert.Equal(123.5, point.Value);
    }

    [Theory]
    [InlineData("render-queue-wait-ms", "ms")]
    [InlineData("zoom.input-to-present-refresh-intervals", "intervals")]
    [InlineData("scroll.dropped-frames-percent", "%")]
    [InlineData("memory.allocation-rate-bytes-per-second", "bytes-per-second")]
    [InlineData("memory.cache-geometry-bytes", "bytes")]
    public void ParsesRequiredPerformanceEvidenceMetrics(string name, string unit)
    {
        var line = $"ELLIEPDF_BENCHMARK_METRIC {{\"name\":\"{name}\",\"unit\":\"{unit}\",\"value\":1}}";

        Assert.True(BenchmarkMetricProtocol.TryParse(line, out var point));
        Assert.Equal(name, point.Name);
        Assert.Equal(unit, point.Unit);
    }

    [Theory]
    [InlineData("ELLIEPDF_READY first-page")]
    [InlineData("ELLIEPDF_BENCHMARK_METRIC {\"name\":\"C:\\\\secret.pdf\",\"unit\":\"ms\",\"value\":1}")]
    [InlineData("ELLIEPDF_BENCHMARK_METRIC {\"name\":\"secret.pdf\",\"unit\":\"ms\",\"value\":1}")]
    [InlineData("ELLIEPDF_BENCHMARK_METRIC {\"name\":\"first-page\",\"unit\":\"ms\",\"value\":\"not-a-number\"}")]
    [InlineData("ELLIEPDF_BENCHMARK_METRIC {\"name\":\"first-page\",\"unit\":\"ms\",\"value\":1e999}")]
    [InlineData("ELLIEPDF_BENCHMARK_METRIC {\"name\":\"first-page.presented\",\"unit\":\"secretTitle\",\"value\":1}")]
    public void RejectsUntrustedOrMalformedMetricLines(string line)
    {
        Assert.False(BenchmarkMetricProtocol.TryParse(line, out _));
    }
}
