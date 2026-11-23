namespace ElliePdf;

/// <summary>
/// FIFO admission budget for continuous-reader page surfaces. Pending page identities do not
/// consume pixel memory, and cancellation removes them before promotion.
/// </summary>
public sealed class PageSurfaceBudget
{
    public const int MaximumCapacity = 12;

    private readonly int _capacity;
    private readonly HashSet<int> _active = [];
    private readonly HashSet<int> _pendingSet = [];
    private readonly Queue<int> _pending = [];

    public PageSurfaceBudget(int capacity = MaximumCapacity)
    {
        if (capacity is <= 0 or > MaximumCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public int Capacity => _capacity;
    public int ActiveCount => _active.Count;
    public int PendingCount => _pendingSet.Count;
    public IReadOnlyCollection<int> ActivePages => _active;

    public bool Request(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        if (_active.Contains(pageIndex))
        {
            return true;
        }

        if (_pendingSet.Contains(pageIndex))
        {
            return false;
        }

        if (_active.Count < _capacity)
        {
            _active.Add(pageIndex);
            return true;
        }

        _pending.Enqueue(pageIndex);
        _pendingSet.Add(pageIndex);
        return false;
    }

    public int? Release(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        _pendingSet.Remove(pageIndex);
        if (!_active.Remove(pageIndex))
        {
            return null;
        }

        while (_pending.TryDequeue(out var next))
        {
            if (!_pendingSet.Remove(next))
            {
                continue;
            }

            _active.Add(next);
            return next;
        }

        return null;
    }

    public void Clear()
    {
        _active.Clear();
        _pendingSet.Clear();
        _pending.Clear();
    }
}
