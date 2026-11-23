using System.Collections.ObjectModel;
using ElliePdf.Domain.Documents;

namespace ElliePdf.Rendering;

public enum CacheEvictionReason
{
    BudgetExceeded,
    MemoryPressure,
    Removed,
    Cleared
}

public enum RenderMemoryPressureLevel
{
    None,
    Moderate,
    Critical
}

public sealed record RenderCacheBudgets
{
    public const long OneMiB = 1024L * 1024L;
    public const long DefaultGpuTileBudgetBytes = 96L * OneMiB;
    public const long DefaultCpuBufferBudgetBytes = 32L * OneMiB;
    public const long DefaultThumbnailBudgetBytes = 16L * OneMiB;
    public const long DefaultMetadataBudgetBytes = 16L * OneMiB;
    public const int MaxUncachedLeaseCount = 2;
    public const long MaxUncachedLeaseBytes = 8L * OneMiB;

    public RenderCacheBudgets(
        long gpuTileBudgetBytes = DefaultGpuTileBudgetBytes,
        long cpuBufferBudgetBytes = DefaultCpuBufferBudgetBytes,
        long thumbnailBudgetBytes = DefaultThumbnailBudgetBytes,
        long metadataBudgetBytes = DefaultMetadataBudgetBytes)
    {
        GpuTileBudgetBytes = Validate(gpuTileBudgetBytes, nameof(gpuTileBudgetBytes));
        CpuBufferBudgetBytes = Validate(cpuBufferBudgetBytes, nameof(cpuBufferBudgetBytes));
        ThumbnailBudgetBytes = Validate(thumbnailBudgetBytes, nameof(thumbnailBudgetBytes));
        MetadataBudgetBytes = Validate(metadataBudgetBytes, nameof(metadataBudgetBytes));
    }

    public long GpuTileBudgetBytes { get; init; }
    public long CpuBufferBudgetBytes { get; init; }
    public long ThumbnailBudgetBytes { get; init; }
    public long MetadataBudgetBytes { get; init; }

    public static RenderCacheBudgets Default { get; } = new();

    public RenderCacheBudgets ApplyMemoryPressure(RenderMemoryPressureLevel pressure)
    {
        var numerator = pressure switch
        {
            RenderMemoryPressureLevel.None => 1,
            RenderMemoryPressureLevel.Moderate => 3,
            RenderMemoryPressureLevel.Critical => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(pressure))
        };

        var denominator = pressure switch
        {
            RenderMemoryPressureLevel.None => 1,
            RenderMemoryPressureLevel.Moderate => 4,
            RenderMemoryPressureLevel.Critical => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(pressure))
        };

        static long Scale(long value, int numerator, int denominator)
            => checked(Math.Max(1L, value * numerator / denominator));

        return new RenderCacheBudgets(
            Scale(GpuTileBudgetBytes, numerator, denominator),
            Scale(CpuBufferBudgetBytes, numerator, denominator),
            Scale(ThumbnailBudgetBytes, numerator, denominator),
            Scale(MetadataBudgetBytes, numerator, denominator));
    }

    private static long Validate(long value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, parameterName);
        return value;
    }
}

public sealed record CacheEntry<TValue>(TValue Value, long ByteCount);

public sealed record CacheEviction<TKey>(TKey Key, long ByteCount, CacheEvictionReason Reason) where TKey : notnull;

public interface IByteBudgetCache
{
    long BudgetBytes { get; }
    long ResidentBytes { get; }
    int Count { get; }
    int TrimToBudget(CacheEvictionReason reason = CacheEvictionReason.BudgetExceeded);
    void SetBudget(long budgetBytes, CacheEvictionReason reason = CacheEvictionReason.BudgetExceeded);
}

public class ByteBudgetLruCache<TKey, TValue> : IByteBudgetCache where TKey : notnull
{
    private readonly Dictionary<TKey, Entry> _entries = new();
    private readonly LinkedList<TKey> _lru = new();
    private long _residentBytes;

    public ByteBudgetLruCache(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        BudgetBytes = budgetBytes;
    }

    public long BudgetBytes { get; private set; }
    public long ResidentBytes => _residentBytes;
    public int Count => _entries.Count;
    public event EventHandler<CacheEviction<TKey>>? EntryEvicted;

    public IReadOnlyCollection<TKey> ProtectedKeys => new ReadOnlyCollection<TKey>(_entries.Values.Where(static entry => entry.IsProtected).Select(static entry => entry.Key).ToArray());

    public bool TryGet(TKey key, out TValue? value)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            Touch(entry);
            value = entry.Value;
            return true;
        }

        value = default;
        return false;
    }

    public bool Set(TKey key, TValue value, long byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        if (byteCount > BudgetBytes)
        {
            return false;
        }

        if (_entries.TryGetValue(key, out var existing))
        {
            var wasProtected = existing.IsProtected;
            var priorValue = existing.Value;
            var priorBytes = existing.ByteCount;
            _residentBytes -= priorBytes;
            existing.Value = value;
            existing.ByteCount = byteCount;
            Touch(existing);
            _residentBytes = checked(_residentBytes + byteCount);

            // Keep replacement atomic: trimming may evict older entries, but it
            // must not detach the entry whose prior value we may need to restore.
            existing.IsProtected = true;
            TrimToBudgetInternal(CacheEvictionReason.BudgetExceeded);
            if (_residentBytes <= BudgetBytes)
            {
                existing.IsProtected = wasProtected;
                return true;
            }

            _residentBytes -= byteCount;
            existing.Value = priorValue;
            existing.ByteCount = priorBytes;
            existing.IsProtected = wasProtected;
            _residentBytes = checked(_residentBytes + priorBytes);
            Touch(existing);
            TrimToBudgetInternal(CacheEvictionReason.BudgetExceeded);
            return false;
        }

        var node = _lru.AddLast(key);
        var entry = new Entry(key, value, byteCount, node);
        _entries.Add(key, entry);
        _residentBytes = checked(_residentBytes + byteCount);

        TrimToBudgetInternal(CacheEvictionReason.BudgetExceeded);
        if (_residentBytes <= BudgetBytes && _entries.ContainsKey(key))
        {
            return true;
        }

        if (_entries.TryGetValue(key, out var rejected))
        {
            RemoveInternal(rejected, CacheEvictionReason.Removed, raiseEvent: false);
        }
        TrimToBudgetInternal(CacheEvictionReason.BudgetExceeded);
        return false;
    }

    public bool Remove(TKey key)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        RemoveInternal(entry, CacheEvictionReason.Removed, raiseEvent: false);
        return true;
    }

    public int Clear()
    {
        var removed = _entries.Values.ToArray();
        _entries.Clear();
        _lru.Clear();
        _residentBytes = 0;
        foreach (var entry in removed)
        {
            EntryEvicted?.Invoke(this, new CacheEviction<TKey>(entry.Key, entry.ByteCount, CacheEvictionReason.Cleared));
        }

        return removed.Length;
    }

    public void ProtectKeys(IEnumerable<TKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var protectedSet = new HashSet<TKey>(keys);
        foreach (var entry in _entries.Values)
        {
            entry.IsProtected = protectedSet.Contains(entry.Key);
        }
    }

    public int TrimToBudget(CacheEvictionReason reason = CacheEvictionReason.BudgetExceeded)
        => TrimToBudgetInternal(reason);

    public void SetBudget(long budgetBytes, CacheEvictionReason reason = CacheEvictionReason.BudgetExceeded)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        BudgetBytes = budgetBytes;
        // Protection only controls normal admission pressure. An explicit budget
        // reduction is a hard memory ceiling and may evict visible/protected data.
        TrimToBudgetInternal(reason, evictProtected: true);
    }

    private int TrimToBudgetInternal(CacheEvictionReason reason, bool evictProtected = false)
    {
        var evicted = 0;
        var node = _lru.First;
        while (_residentBytes > BudgetBytes && node is not null)
        {
            var next = node.Next;
            if (_entries.TryGetValue(node.Value, out var entry) &&
                (evictProtected || !entry.IsProtected))
            {
                RemoveInternal(entry, reason, raiseEvent: true);
                evicted++;
            }

            node = next;
        }

        return evicted;
    }

    private void Touch(Entry entry)
    {
        if (entry.Node.List is null)
        {
            return;
        }

        _lru.Remove(entry.Node);
        _lru.AddLast(entry.Node);
    }

    private void RemoveInternal(Entry entry, CacheEvictionReason reason, bool raiseEvent)
    {
        _entries.Remove(entry.Key);
        if (entry.Node.List is not null)
        {
            _lru.Remove(entry.Node);
        }

        _residentBytes -= entry.ByteCount;
        if (raiseEvent)
        {
            EntryEvicted?.Invoke(this, new CacheEviction<TKey>(entry.Key, entry.ByteCount, reason));
        }
    }

    private sealed class Entry(TKey key, TValue value, long byteCount, LinkedListNode<TKey> node)
    {
        public TKey Key { get; } = key;
        public TValue Value { get; set; } = value;
        public long ByteCount { get; set; } = byteCount;
        public LinkedListNode<TKey> Node { get; } = node;
        public bool IsProtected { get; set; }
    }
}

public sealed class RenderRasterCache<TValue> : ByteBudgetLruCache<RenderKey, TValue>
{
    public RenderRasterCache(long budgetBytes) : base(budgetBytes) { }
}

public sealed class ThumbnailRasterCache<TValue> : ByteBudgetLruCache<RenderKey, TValue>
{
    public ThumbnailRasterCache(long budgetBytes) : base(budgetBytes) { }
}

public sealed class MetadataCache<TKey, TValue> : ByteBudgetLruCache<TKey, TValue> where TKey : notnull
{
    public MetadataCache(long budgetBytes) : base(budgetBytes) { }
}

public sealed class RenderCacheBudgetManager
{
    private readonly IByteBudgetCache[] _caches;

    public RenderCacheBudgetManager(
        RenderRasterCache<object> gpuTileCache,
        RenderRasterCache<object> cpuBufferCache,
        ThumbnailRasterCache<object> thumbnailCache,
        MetadataCache<object, object> metadataCache,
        RenderCacheBudgets? budgets = null)
    {
        GpuTileCache = gpuTileCache ?? throw new ArgumentNullException(nameof(gpuTileCache));
        CpuBufferCache = cpuBufferCache ?? throw new ArgumentNullException(nameof(cpuBufferCache));
        ThumbnailCache = thumbnailCache ?? throw new ArgumentNullException(nameof(thumbnailCache));
        MetadataCache = metadataCache ?? throw new ArgumentNullException(nameof(metadataCache));
        Budgets = budgets ?? RenderCacheBudgets.Default;
        _caches = [GpuTileCache, CpuBufferCache, ThumbnailCache, MetadataCache];
        ApplyBudgets(Budgets, CacheEvictionReason.BudgetExceeded);
    }

    public RenderRasterCache<object> GpuTileCache { get; }
    public RenderRasterCache<object> CpuBufferCache { get; }
    public ThumbnailRasterCache<object> ThumbnailCache { get; }
    public MetadataCache<object, object> MetadataCache { get; }
    public RenderCacheBudgets Budgets { get; private set; }

    public void ApplyMemoryPressure(RenderMemoryPressureLevel pressure)
    {
        var next = RenderCacheBudgets.Default.ApplyMemoryPressure(pressure);
        Budgets = next;
        ApplyBudgets(next, CacheEvictionReason.MemoryPressure);
    }

    private void ApplyBudgets(RenderCacheBudgets budgets, CacheEvictionReason reason)
    {
        GpuTileCache.SetBudget(budgets.GpuTileBudgetBytes, reason);
        CpuBufferCache.SetBudget(budgets.CpuBufferBudgetBytes, reason);
        ThumbnailCache.SetBudget(budgets.ThumbnailBudgetBytes, reason);
        MetadataCache.SetBudget(budgets.MetadataBudgetBytes, reason);
    }
}

public sealed class UncachedLeaseGate
{
    private readonly Dictionary<int, long> _reservations = new();
    private int _nextReservationId;

    public UncachedLeaseGate(int maxLeaseCount = RenderCacheBudgets.MaxUncachedLeaseCount, long maxLeaseBytes = RenderCacheBudgets.MaxUncachedLeaseBytes)
    {
        if (maxLeaseCount <= 0) throw new ArgumentOutOfRangeException(nameof(maxLeaseCount));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLeaseBytes);
        MaxLeaseCount = maxLeaseCount;
        MaxLeaseBytes = maxLeaseBytes;
    }

    public int MaxLeaseCount { get; }
    public long MaxLeaseBytes { get; }
    public int ActiveLeaseCount => _reservations.Count;
    public long ActiveLeaseBytes => _reservations.Values.Sum();

    public bool TryAcquire(long byteCount, out LeaseReservation reservation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        if (byteCount > MaxLeaseBytes)
        {
            reservation = default;
            return false;
        }

        if (_reservations.Count >= MaxLeaseCount || ActiveLeaseBytes + byteCount > MaxLeaseBytes)
        {
            reservation = default;
            return false;
        }

        var id = ++_nextReservationId;
        _reservations.Add(id, byteCount);
        reservation = new LeaseReservation(this, id, byteCount);
        return true;
    }

    private void Release(int reservationId)
        => _reservations.Remove(reservationId);

    public readonly struct LeaseReservation : IDisposable
    {
        private readonly UncachedLeaseGate? _owner;
        private readonly int _reservationId;

        internal LeaseReservation(UncachedLeaseGate owner, int reservationId, long byteCount)
        {
            _owner = owner;
            _reservationId = reservationId;
            ByteCount = byteCount;
        }

        public long ByteCount { get; }
        public bool IsActive => _owner is not null;

        public void Dispose()
        {
            _owner?.Release(_reservationId);
        }
    }
}
