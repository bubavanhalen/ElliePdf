using System.Diagnostics.Tracing;
using ElliePdf.Telemetry;
using Xunit;

namespace ElliePdf.Tests;

public sealed class TelemetryPayloadPolicyTests
{
    [Theory]
    [InlineData("document.pdf")]
    [InlineData("C:\\Users\\person\\file.pdf")]
    [InlineData("extracted text")]
    [InlineData("fixture/path")]
    public void RejectsSensitiveOrNonOpaquePayloads(string value) => Assert.False(TelemetryPayloadPolicy.IsSafe(value));
    [Fact] public void AcceptsOpaqueIdentifiers() => Assert.True(TelemetryPayloadPolicy.IsSafe("fixture-42"));
    [Fact]
    public void EventSourceHasStablePrivacySafeIdentity()
    {
        Assert.Equal("ElliePdf", ElliePdfEventSource.Log.Name);
        var events = typeof(ElliePdfEventSource).GetMethods().Select(m => (m, a: m.GetCustomAttributes(typeof(EventAttribute), false).SingleOrDefault() as EventAttribute)).Where(x => x.a is not null).Select(x => x.a!.EventId).Order().ToArray();
        Assert.Equal(Enumerable.Range(1, 44), events);
    }

    [Fact]
    public void OperationIdsAreOpaqueNonZeroAndUnique()
    {
        var first = TelemetryOperation.NextId();
        var second = TelemetryOperation.NextId();

        Assert.NotEqual(0, first);
        Assert.NotEqual(0, second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void MonotonicDurationIsNeverNegative()
    {
        var started = TelemetryOperation.StartTimestamp();
        Assert.True(TelemetryOperation.ElapsedMicroseconds(started) >= 0);
    }
}
