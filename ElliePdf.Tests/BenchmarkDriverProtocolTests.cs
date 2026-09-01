using ElliePdf.Benchmarking;
using Xunit;

namespace ElliePdf.Tests;

public sealed class BenchmarkDriverProtocolTests
{
    [Fact]
    public void ParsesOnlyExplicitSupportedDriverModes()
    {
        Assert.True(BenchmarkDriverRequest.TryParse(["ElliePdf.exe", "--benchmark-driver", "first-page"], out var request));
        Assert.Equal("first-page", request!.Scenario);
        Assert.True(BenchmarkDriverRequest.TryParse(["--benchmark-driver", "first-page-10000"], out _));
        Assert.True(BenchmarkDriverRequest.TryParse(["--benchmark-driver", "cached-navigation"], out _));
        Assert.False(BenchmarkDriverRequest.TryParse(["--benchmark-driver", "C:\\private\\fixture.pdf"], out _));
        Assert.False(BenchmarkDriverRequest.TryParse(["--benchmark-driver"], out _));
        Assert.False(BenchmarkDriverRequest.TryParse(["--benchmark"], out _));
    }

    [Fact]
    public void FormatsOnlyFixedPrivacySafeStdoutTokens()
    {
        var line = BenchmarkDriverProtocol.FormatMetric("first-page.presented", "ms", 12.5);

        Assert.Equal("ELLIEPDF_BENCHMARK_METRIC {\"name\":\"first-page.presented\",\"unit\":\"ms\",\"value\":12.5}", line);
        Assert.Equal("ELLIEPDF_READY first-page", BenchmarkDriverProtocol.FormatReady(new BenchmarkDriverRequest("first-page")));
        Assert.Throws<ArgumentException>(() => BenchmarkDriverProtocol.FormatMetric("fixture.pdf", "ms", 1));
        Assert.Throws<ArgumentException>(() => BenchmarkDriverProtocol.FormatReady(new BenchmarkDriverRequest("fixture.pdf")));
        Assert.Equal(
            "ELLIEPDF_BENCHMARK_METRIC {\"name\":\"memory.worker.private-bytes\",\"unit\":\"bytes\",\"value\":1}",
            BenchmarkDriverProtocol.FormatMetric("memory.worker.private-bytes", "bytes", 1));
        Assert.Contains("render-queue-wait-ms", BenchmarkDriverProtocol.FormatMetric("render-queue-wait-ms", "ms", 1));
        Assert.Contains("zoom.input-to-present-refresh-intervals", BenchmarkDriverProtocol.FormatMetric("zoom.input-to-present-refresh-intervals", "intervals", 1));
        Assert.Contains("scroll.dropped-frames-percent", BenchmarkDriverProtocol.FormatMetric("scroll.dropped-frames-percent", "%", 1));
    }
}
