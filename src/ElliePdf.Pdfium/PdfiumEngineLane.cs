using System.Collections.Concurrent;

namespace ElliePdf.Pdfium;

public sealed class PdfiumEngineLane : IAsyncDisposable
{
    private readonly BlockingCollection<IEngineWorkItem> _queue = new(new ConcurrentQueue<IEngineWorkItem>());
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _baseDirectory;
    private int _disposeState;

    public PdfiumEngineLane(string? baseDirectory = null, string? name = null)
    {
        _baseDirectory = baseDirectory;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = name ?? "ElliePdf PDFium engine lane"
        };
        _thread.Start();
    }

    public Task Ready => _ready.Task;

    public async ValueTask<T> InvokeAsync<T>(
        Func<PdfiumEngine, T> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        var item = new EngineWorkItem<T>(action, cancellationToken);
        try
        {
            _queue.Add(item, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            throw new ObjectDisposedException(nameof(PdfiumEngineLane));
        }

        return await item.Task.ConfigureAwait(false);
    }

    public async ValueTask InvokeAsync(
        Action<PdfiumEngine> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = await InvokeAsync(
            engine =>
            {
                action(engine);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        await Task.Run(_thread.Join).ConfigureAwait(false);
        _queue.Dispose();
    }

    private void Run()
    {
        PdfiumEngine? engine = null;
        try
        {
            engine = new PdfiumEngine(_baseDirectory);
            _ready.TrySetResult();
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                lock (PdfiumNative.ExecutionLock)
                {
                    item.Execute(engine);
                }
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            while (_queue.TryTake(out var item))
            {
                item.Fail(exception);
            }
        }
        finally
        {
            lock (PdfiumNative.ExecutionLock)
            {
                engine?.Dispose();
            }
        }
    }

    private interface IEngineWorkItem
    {
        void Execute(PdfiumEngine engine);

        void Fail(Exception exception);
    }

    private sealed class EngineWorkItem<T> : IEngineWorkItem
    {
        private readonly Func<PdfiumEngine, T> _action;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal EngineWorkItem(Func<PdfiumEngine, T> action, CancellationToken cancellationToken)
        {
            _action = action;
            _cancellationToken = cancellationToken;
        }

        internal Task<T> Task => _completion.Task;

        public void Execute(PdfiumEngine engine)
        {
            if (_cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_cancellationToken);
                return;
            }

            try
            {
                _completion.TrySetResult(_action(engine));
            }
            catch (OperationCanceledException exception)
            {
                _completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }

}
