using ElliePdf.Telemetry;
using Xunit;

namespace ElliePdf.Tests;

public sealed class BenchmarkStatisticsTests
{
    [Fact]
    public void ComputesPercentilesAndDeterministicBootstrapInterval()
    {
        var first = BenchmarkStatistics.Compute([1, 2, 3, 4, 5], 1000, 7);
        var second = BenchmarkStatistics.Compute([1, 2, 3, 4, 5], 1000, 7);
        Assert.Equal(5, first.SampleCount);
        Assert.Equal(3, first.Median);
        Assert.Equal(4.8, first.P95, 10);
        Assert.Equal(4.96, first.P99, 10);
        Assert.Equal(first, second);
    }
}
