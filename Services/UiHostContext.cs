using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;

namespace ElliePdf.Services;

/// <summary>
/// Instance-scoped access to the active WinUI host. The composition root attaches
/// the window once; UI consumers receive this context explicitly through DI.
/// </summary>
public sealed class UiHostContext
{
    private MainWindow? _window;
    private DispatcherQueue? _dispatcherQueue;

    public MainWindow Window =>
        _window ?? throw new InvalidOperationException("The UI host has not been attached.");

    public nint WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(Window);

    public Microsoft.UI.WindowId WindowId =>
        Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowHandle);

    public XamlRoot? XamlRoot =>
        _window?.Content is FrameworkElement root ? root.XamlRoot : null;

    internal void Attach(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_window is not null && !ReferenceEquals(_window, window))
        {
            throw new InvalidOperationException("A different UI host is already attached.");
        }

        _window = window;
        _dispatcherQueue = window.DispatcherQueue;
    }

    internal void Detach(MainWindow window)
    {
        if (ReferenceEquals(_window, window))
        {
            _window = null;
            _dispatcherQueue = null;
        }
    }

    public Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        DispatcherQueue dispatcher = _dispatcherQueue
            ?? throw new InvalidOperationException("The UI host has not been attached.");

        if (dispatcher.HasThreadAccess)
        {
            return action();
        }

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchState = new DispatchState();
        CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(() =>
        {
            lock (dispatchState.Sync)
            {
                if (!dispatchState.Started)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
            }
        });
        if (!dispatcher.TryEnqueue(async () =>
            {
                lock (dispatchState.Sync)
                {
                    if (completion.Task.IsCompleted)
                    {
                        return;
                    }
                    dispatchState.Started = true;
                }
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    completion.TrySetResult(await action());
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            cancellationRegistration.Dispose();
            throw new InvalidOperationException("The UI host dispatcher is unavailable.");
        }

        return AwaitCompletionAsync(completion.Task, cancellationRegistration);
    }

    private static async Task<T> AwaitCompletionAsync<T>(Task<T> task,
        CancellationTokenRegistration cancellationRegistration)
    {
        using (cancellationRegistration)
        {
            return await task.ConfigureAwait(false);
        }
    }

    private sealed class DispatchState
    {
        public object Sync { get; } = new();
        public bool Started { get; set; }
    }

    public void SetTitleBar(UIElement titleBar) => Window.SetTitleBar(titleBar);

    public Task OpenFilesAsync(IReadOnlyList<string> filePaths) =>
        Window.OpenFilesAsync(filePaths);
}
