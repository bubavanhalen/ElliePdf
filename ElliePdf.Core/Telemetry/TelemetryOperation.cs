using System.Diagnostics;

namespace ElliePdf.Telemetry;

/// <summary>
/// Creates process-local opaque correlation identifiers and monotonic durations for
/// EventSource payloads. It deliberately accepts no document-derived data.
/// </summary>
public static class TelemetryOperation
{
    private static int _nextId;

    public static int NextId()
    {
        var id = Interlocked.Increment(ref _nextId);
        return id == 0 ? Interlocked.Increment(ref _nextId) : id;
    }

    public static long StartTimestamp() => Stopwatch.GetTimestamp();

    public static long ElapsedMicroseconds(long startTimestamp) =>
        Math.Max(0, Stopwatch.GetElapsedTime(startTimestamp).Ticks / 10);
}
