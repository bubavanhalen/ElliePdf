using System.Collections.Immutable;

namespace ElliePdf.Application;

public sealed record BackgroundTaskFault(string? Name, Exception Exception);

/// <summary>Owns fire-and-forget tasks and observes every fault.</summary>
public sealed class BackgroundTaskSupervisor : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly HashSet<Task> _tasks = [];
    private readonly List<BackgroundTaskFault> _faults = [];
    private bool _disposed;

    public event Action<BackgroundTaskFault>? Faulted;

    public ImmutableArray<BackgroundTaskFault> Faults
    {
        get
        {
            lock (_sync)
            {
                return _faults.ToImmutableArray();
            }
        }
    }

    public Task Track(Task task, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _tasks.Add(task);
        }

        _ = ObserveCompletionAsync(task, name);
        return task;
    }

    public Task Start(Func<Task> operation, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Track(StartOperationAsync(operation), name);
    }

    // An explicit alias makes call sites read naturally when adapting existing
    // fire-and-forget code during the migration.
    public Task Observe(Task task, string? name = null) => Track(task, name);

    public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task[] pending;
            lock (_sync)
            {
                pending = _tasks.Where(static task => !task.IsCompleted).ToArray();
            }

            if (pending.Length == 0)
            {
                return;
            }

            await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = _tasks.ToArray();
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch
        {
            // ObserveCompletionAsync has recorded each exception. Disposal of
            // the supervisor itself must not turn an unobserved task into a
            // process-level failure.
        }
    }

    private static async Task StartOperationAsync(Func<Task> operation) => await operation().ConfigureAwait(false);

    private async Task ObserveCompletionAsync(Task task, string? name)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            BackgroundTaskFault fault = new(name, exception);
            Action<BackgroundTaskFault>? handler;
            lock (_sync)
            {
                _faults.Add(fault);
                handler = Faulted;
            }

            try
            {
                handler?.Invoke(fault);
            }
            catch
            {
                // A diagnostic listener must not prevent fault observation.
            }
        }
        finally
        {
            lock (_sync)
            {
                _tasks.Remove(task);
            }
        }
    }
}
