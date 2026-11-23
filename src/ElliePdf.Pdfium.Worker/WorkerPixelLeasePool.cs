using System.IO.MemoryMappedFiles;
using ElliePdf.Pdf.Contracts;
using ElliePdf.Pdf.Transport;

namespace ElliePdf.Pdfium.Worker;

/// <summary>
/// Owns worker-produced pixel mappings until the broker explicitly releases them or the
/// bounded lease timeout expires. A worker exit also releases every operating-system handle.
/// </summary>
public sealed class WorkerPixelLeasePool : IAsyncDisposable
{
    private static readonly TimeSpan DefaultLeaseTimeout = TimeSpan.FromSeconds(5);
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, MappingEntry> _mappings = [];
    private readonly Guid _sessionId;
    private readonly TimeSpan _leaseTimeout;
    private readonly Timer _reaper;
    private bool _disposed;

    public WorkerPixelLeasePool(Guid sessionId, TimeSpan? leaseTimeout = null)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("The worker session id is required.", nameof(sessionId));
        }

        _sessionId = sessionId;
        _leaseTimeout = leaseTimeout ?? DefaultLeaseTimeout;
        if (_leaseTimeout <= TimeSpan.Zero || _leaseTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseTimeout));
        }

        var period = TimeSpan.FromMilliseconds(Math.Clamp(_leaseTimeout.TotalMilliseconds / 2, 100, 1_000));
        _reaper = new Timer(static state => ((WorkerPixelLeasePool)state!).ReclaimExpired(), this, period, period);
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _mappings.Count;
            }
        }
    }

    public SharedMemoryLeaseMetadata Publish(WorkerRenderedBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var byteLength = checked(buffer.Stride * buffer.Height);
        if (byteLength <= 0
            || byteLength > PdfContractLimits.MaxPixelBufferBytes
            || buffer.Pixels.Length != byteLength)
        {
            throw new TransportProtocolException("The rendered pixel buffer is outside the configured limits.");
        }

        var leaseId = Guid.NewGuid();
        var mappingName = $"Local\\ElliePdf_{_sessionId:N}_{leaseId:N}";
        var mapping = MemoryMappedFile.CreateNew(
            mappingName,
            byteLength,
            MemoryMappedFileAccess.ReadWrite,
            MemoryMappedFileOptions.None,
            HandleInheritability.None);

        try
        {
            using (var view = mapping.CreateViewStream(0, byteLength, MemoryMappedFileAccess.Write))
            {
                view.Write(buffer.Pixels);
                view.Flush();
            }

            var metadata = new SharedMemoryLeaseMetadata
            {
                LeaseId = leaseId,
                SessionId = _sessionId,
                SharedMemoryId = mappingName,
                MappingLength = byteLength,
                Offset = 0,
                ByteLength = byteLength,
                Width = buffer.Width,
                Height = buffer.Height,
                Stride = buffer.Stride,
                Format = buffer.Format,
                Key = buffer.Key
            };
            metadata.Validate();

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _mappings.Add(leaseId, new MappingEntry(mapping, metadata, DateTimeOffset.UtcNow + _leaseTimeout));
            }

            return metadata;
        }
        catch
        {
            mapping.Dispose();
            throw;
        }
    }

    public bool Acknowledge(Guid leaseId)
    {
        if (leaseId == Guid.Empty)
        {
            return false;
        }

        lock (_sync)
        {
            if (!_mappings.TryGetValue(leaseId, out var entry) || entry.Acknowledged)
            {
                return false;
            }

            entry.Acknowledged = true;
            entry.ExpiresAtUtc = DateTimeOffset.UtcNow + _leaseTimeout;
            return true;
        }
    }

    public bool Release(Guid leaseId)
    {
        MappingEntry? entry;
        lock (_sync)
        {
            if (!_mappings.Remove(leaseId, out entry))
            {
                return false;
            }
        }

        entry.Mapping.Dispose();
        return true;
    }

    public ValueTask DisposeAsync()
    {
        MappingEntry[] entries;
        lock (_sync)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            entries = _mappings.Values.ToArray();
            _mappings.Clear();
        }

        _reaper.Dispose();
        foreach (var entry in entries)
        {
            entry.Mapping.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void ReclaimExpired()
    {
        MappingEntry[] expired;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var expiredIds = _mappings
                .Where(pair => pair.Value.ExpiresAtUtc <= now)
                .Select(static pair => pair.Key)
                .ToArray();
            expired = new MappingEntry[expiredIds.Length];
            for (var index = 0; index < expiredIds.Length; index++)
            {
                expired[index] = _mappings[expiredIds[index]];
                _mappings.Remove(expiredIds[index]);
            }
        }

        foreach (var entry in expired)
        {
            entry.Mapping.Dispose();
        }
    }

    private sealed class MappingEntry(
        MemoryMappedFile mapping,
        SharedMemoryLeaseMetadata metadata,
        DateTimeOffset expiresAtUtc)
    {
        public MemoryMappedFile Mapping { get; } = mapping;
        public SharedMemoryLeaseMetadata Metadata { get; } = metadata;
        public DateTimeOffset ExpiresAtUtc { get; set; } = expiresAtUtc;
        public bool Acknowledged { get; set; }
    }
}
