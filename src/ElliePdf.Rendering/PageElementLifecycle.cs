using ElliePdf.Domain.Documents;

namespace ElliePdf.Rendering;

public readonly record struct PageElementPreparation(PageRecycleToken Token, CancellationToken CancellationToken);

/// <summary>Owns the prepare/clear lifetime of viewport work for recyclable page elements.</summary>
public sealed class PageElementLifecycle : IDisposable
{
    private readonly Dictionary<RecycledPageElement, CancellationTokenSource> _cancellations = [];
    private bool _disposed;

    public PageElementPreparation Prepare(RecycledPageElement element, PageLayoutItem item)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(item);
        Clear(element);
        var token = element.Bind(item);
        var cancellation = new CancellationTokenSource();
        _cancellations[element] = cancellation;
        return new PageElementPreparation(token, cancellation.Token);
    }

    public void Clear(RecycledPageElement element)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(element);
        if (_cancellations.Remove(element, out var cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        if (element.IsBound) element.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var cancellation in _cancellations.Values) cancellation.Cancel();
        foreach (var cancellation in _cancellations.Values) cancellation.Dispose();
        _cancellations.Clear();
        _disposed = true;
    }
}
