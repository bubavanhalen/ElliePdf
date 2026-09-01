using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;

namespace ElliePdf.Pdf.Transport;

public enum SharedMemoryLeaseState { Acquired, Acknowledged }

public sealed record SharedMemoryLeaseMetadata
{
    public Guid LeaseId { get; init; }
    public Guid SessionId { get; init; }
    public string SharedMemoryId { get; init; } = string.Empty;
    public long MappingLength { get; init; }
    public long Offset { get; init; }
    public int ByteLength { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Stride { get; init; }
    public PixelFormat Format { get; init; } = PixelFormat.Bgra8Premultiplied;
    public RenderKey? Key { get; init; }

    public void Validate()
    {
        if (LeaseId == Guid.Empty || SessionId == Guid.Empty) throw new TransportProtocolException("Lease and session identities are required.");
        if (string.IsNullOrWhiteSpace(SharedMemoryId) || SharedMemoryId.Length > 256) throw new TransportProtocolException("Invalid shared-memory identity.");
        if (MappingLength <= 0 || MappingLength > PdfContractLimits.MaxPixelBufferBytes) throw new TransportProtocolException("Invalid mapping length.");
        if (Offset < 0 || ByteLength <= 0 || Offset > MappingLength || ByteLength > MappingLength - Offset) throw new TransportProtocolException("Lease range is outside the mapping.");
        if (Width is <= 0 or > PdfContractLimits.MaxPixelDimension || Height is <= 0 or > PdfContractLimits.MaxPixelDimension)
            throw new TransportProtocolException("Invalid lease dimensions.");
        if (Stride < checked(Width * 4) || Stride > PdfContractLimits.MaxPixelStride || ByteLength < checked(Stride * Height))
            throw new TransportProtocolException("Invalid lease stride or byte length.");
        if (Format != PixelFormat.Bgra8Premultiplied) throw new TransportProtocolException("Unsupported pixel format.");
        var key = Key ?? throw new TransportProtocolException("A render identity is required.");
        if (key.DocumentId.Value == Guid.Empty || key.PageId.Value == Guid.Empty) throw new TransportProtocolException("Invalid render identity.");
    }
}

public sealed class SharedMemoryLease : IAsyncDisposable
{
    private readonly SharedMemoryLeaseRegistry _registry;
    private int _released;
    internal SharedMemoryLease(SharedMemoryLeaseRegistry registry, SharedMemoryLeaseMetadata metadata, SharedMemoryLeaseState state)
    {
        _registry = registry; Metadata = metadata; State = state;
    }
    public SharedMemoryLeaseMetadata Metadata { get; }
    public Guid LeaseId => Metadata.LeaseId;
    public SharedMemoryLeaseState State { get; internal set; }
    public bool Acknowledge()
    {
        var changed = _registry.TryAcknowledge(LeaseId);
        if (changed) State = SharedMemoryLeaseState.Acknowledged;
        return changed;
    }
    public bool Release() => Interlocked.Exchange(ref _released, 1) == 0 && _registry.TryRelease(LeaseId);
    public ValueTask DisposeAsync() { Release(); return ValueTask.CompletedTask; }
}

/// <summary>Process-neutral registry used by the broker to make lease transitions atomic and idempotent.</summary>
public sealed class SharedMemoryLeaseRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _leases = [];
    private readonly TimeSpan _defaultTimeout;
    public SharedMemoryLeaseRegistry(TimeSpan? defaultTimeout = null)
    {
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(5);
        if (_defaultTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(defaultTimeout));
    }

    public int Count { get { lock (_gate) return _leases.Count; } }

    public bool TryAcquire(SharedMemoryLeaseMetadata metadata, out SharedMemoryLease lease, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        metadata.Validate();
        var expires = DateTimeOffset.UtcNow + (timeout ?? _defaultTimeout);
        if (expires <= DateTimeOffset.UtcNow) throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_gate)
        {
            if (_leases.ContainsKey(metadata.LeaseId)) { lease = null!; return false; }
            _leases.Add(metadata.LeaseId, new Entry(metadata, expires, timeout ?? _defaultTimeout));
            lease = new SharedMemoryLease(this, metadata, SharedMemoryLeaseState.Acquired);
            return true;
        }
    }

    public bool TryAcknowledge(Guid leaseId)
    {
        lock (_gate)
        {
            if (!_leases.TryGetValue(leaseId, out var entry) || entry.State != SharedMemoryLeaseState.Acquired) return false;
            entry.State = SharedMemoryLeaseState.Acknowledged;
            entry.ExpiresAt = DateTimeOffset.UtcNow + entry.Timeout;
            return true;
        }
    }

    public bool TryRelease(Guid leaseId)
    {
        lock (_gate) return _leases.Remove(leaseId);
    }

    public int ReclaimExpired(DateTimeOffset? now = null)
    {
        var cutoff = now ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var expired = _leases.Where(p => p.Value.ExpiresAt <= cutoff).Select(p => p.Key).ToArray();
            foreach (var id in expired) _leases.Remove(id);
            return expired.Length;
        }
    }

    public int ReclaimAll(Guid? sessionId = null)
    {
        lock (_gate)
        {
            var ids = sessionId is null ? _leases.Keys.ToArray() : _leases.Where(p => p.Value.Metadata.SessionId == sessionId.Value).Select(p => p.Key).ToArray();
            foreach (var id in ids) _leases.Remove(id);
            return ids.Length;
        }
    }

    public bool Contains(Guid leaseId) { lock (_gate) return _leases.ContainsKey(leaseId); }

    private sealed class Entry(SharedMemoryLeaseMetadata metadata, DateTimeOffset expiresAt, TimeSpan timeout)
    {
        public SharedMemoryLeaseMetadata Metadata { get; } = metadata;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        public TimeSpan Timeout { get; } = timeout;
        public SharedMemoryLeaseState State { get; set; } = SharedMemoryLeaseState.Acquired;
    }
}
