using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ElliePdf.Benchmark;

internal sealed class TargetOutputCollector(Process process, string? readyRegex)
{
    private readonly Regex? _ready = readyRegex is null
        ? null
        : new Regex(readyRegex, RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private readonly object _gate = new();
    private readonly Dictionary<string, BenchmarkMetricPoint> _metrics = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<bool> _readiness = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task ConsumeAsync()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                // Keep the driver channel bounded. A driver must never be able to make the harness
                // retain document text or an accidentally dumped binary blob.
                if (line.Length > 16 * 1024)
                    continue;

                if (_ready?.IsMatch(line) == true)
                    _readiness.TrySetResult(true);

                if (BenchmarkMetricProtocol.TryParse(line, out var metric))
                {
                    lock (_gate)
                        _metrics[metric.Name] = metric;
                }
            }

            if (_ready is not null)
                _readiness.TrySetResult(false);
        }
        catch (ObjectDisposedException)
        {
            _readiness.TrySetResult(false);
        }
        catch (IOException)
        {
            _readiness.TrySetResult(false);
        }
    }

    public async Task<bool> WaitForReadyAsync(TimeSpan timeout)
    {
        if (_ready is null)
            throw new InvalidOperationException("A readiness regex is required for output readiness.");

        var completed = await Task.WhenAny(_readiness.Task, Task.Delay(timeout));
        return completed == _readiness.Task && await _readiness.Task;
    }

    public IReadOnlyList<BenchmarkMetricPoint> SnapshotMetrics()
    {
        lock (_gate)
            return _metrics.Values.ToArray();
    }
}
