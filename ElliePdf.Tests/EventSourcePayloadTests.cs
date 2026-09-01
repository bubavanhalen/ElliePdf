using System.Diagnostics.Tracing;
using System.Reflection;
using ElliePdf.Telemetry;
using Xunit;

namespace ElliePdf.Tests;

public sealed class EventSourcePayloadTests
{
    [Theory]
    [InlineData(nameof(ElliePdfEventSource.FirstPagePresented), 9, new[] { "operationId", "durationMicroseconds" }, new[] { typeof(int), typeof(long) })]
    [InlineData(nameof(ElliePdfEventSource.RenderCompleted), 23, new[] { "operationId", "durationMicroseconds", "bytes" }, new[] { typeof(int), typeof(long), typeof(long) })]
    [InlineData(nameof(ElliePdfEventSource.SaveStage), 13, new[] { "operationId", "stage", "durationMicroseconds", "success" }, new[] { typeof(int), typeof(int), typeof(long), typeof(bool) })]
    [InlineData(nameof(ElliePdfEventSource.NativeRender), 7, new[] { "operationId", "durationMicroseconds", "pixelWidth", "pixelHeight", "success" }, new[] { typeof(int), typeof(long), typeof(int), typeof(int), typeof(bool) })]
    [InlineData(nameof(ElliePdfEventSource.SearchPageCompleted), 32, new[] { "operationId", "pageIndex", "durationMicroseconds", "resultCount" }, new[] { typeof(int), typeof(int), typeof(long), typeof(int) })]
    [InlineData(nameof(ElliePdfEventSource.PixelUploadDuration), 43, new[] { "operationId", "durationMicroseconds", "bytes" }, new[] { typeof(int), typeof(long), typeof(long) })]
    [InlineData(nameof(ElliePdfEventSource.CacheEvicted), 44, new[] { "operationId", "bytes", "reason" }, new[] { typeof(int), typeof(long), typeof(int) })]
    public void CriticalTelemetryContractsRemainStable(string methodName, int expectedEventId, string[] expectedParameterNames, Type[] expectedParameterTypes)
    {
        MethodInfo method = typeof(ElliePdfEventSource).GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Missing method {methodName}.");

        EventAttribute attribute = Assert.IsType<EventAttribute>(method.GetCustomAttribute(typeof(EventAttribute), inherit: false));
        Assert.Equal(expectedEventId, attribute.EventId);

        ParameterInfo[] parameters = method.GetParameters();
        Assert.Equal(expectedParameterNames, parameters.Select(parameter => parameter.Name).ToArray());
        Assert.Equal(expectedParameterTypes, parameters.Select(parameter => parameter.ParameterType).ToArray());
    }
}
