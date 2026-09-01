using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;

namespace ElliePdf.Rendering;

/// <summary>The terminal state of a scheduled engine job.</summary>
public enum RenderJobCompletionStatus
{
    Published,
    Stale,
    Cancelled,
    DeadlineExceeded,
    Evicted,
    Closed,
    Rejected,
    Faulted
}

/// <summary>Compatibility alias for callers that use the shorter status name.</summary>
public enum RenderJobStatus
{
    Published,
    Stale,
    Cancelled,
    DeadlineExceeded,
    Evicted,
    Closed,
    Rejected,
    Faulted
}

/// <summary>Optional scheduling facts which are intentionally not part of RenderKey.</summary>
public sealed record RenderJobOptions
{
    public RenderJobOptions(bool isVisible = false, bool isThumbnail = false, int prefetchDistance = 0)
    {
        IsVisible = isVisible;
        IsThumbnail = isThumbnail;
        PrefetchDistance = Math.Max(0, prefetchDistance);
    }

    public RenderJobOptions() { }
    public bool IsVisible { get; init; }
    public bool IsThumbnail { get; init; }
    public int PrefetchDistance { get; init; }

    public static RenderJobOptions Default => new();
    public static RenderJobOptions Visible => new() { IsVisible = true };
    public static RenderJobOptions NonVisibleThumbnail => new() { IsThumbnail = true };
    public static RenderJobOptions Prefetch(int distance) => new() { PrefetchDistance = Math.Max(0, distance) };
}

public sealed record RenderSchedulerOptions
{
    public int Capacity { get; init; } = RenderScheduler.DefaultCapacity;
    public int DocumentQuota { get; init; } = RenderScheduler.DefaultDocumentQuota;
}

/// <summary>The result of an engine operation and whether its pixels may be published.</summary>
public sealed record RenderJobResult(
    RenderRequest Request,
    RenderJobCompletionStatus Status,
    IPixelBufferLease? Lease = null,
    Exception? Error = null)
{
    public bool IsPublicationEligible => Status == RenderJobCompletionStatus.Published && Lease is not null;
    public RenderJobStatus JobStatus => (RenderJobStatus)Status;
}

/// <summary>A bounded, single-flight, priority scheduler for the PDF engine lane.</summary>
public sealed class RenderScheduler : IAsyncDisposable
{
    public const int DefaultCapacity = 256;
    public const int DefaultDocumentQuota = 64;

    private readonly object _gate = new();
    private readonly Func<RenderRequest, CancellationToken, ValueTask<IPixelBufferLease>> _execute;
    private readonly int _capacity;
    private readonly int _documentQuota;
    private readonly PriorityLane[] _lanes = Enum.GetValues<EngineJobPriority>()
        .Select(static p => new PriorityLane(p))
        .ToArray();
    private readonly Dictionary<RenderJobIdentity, Job> _singleFlight = new();
    private readonly Dictionary<DocumentId, DocumentState> _documents = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private long _sequence;
    private int _pending;
    private int _busy;
    private bool _disposed;

    public RenderScheduler(
        Func<RenderRequest, CancellationToken, ValueTask<IPixelBufferLease>> execute,
        int capacity = DefaultCapacity,
        int documentQuota = DefaultDocumentQuota)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (documentQuota <= 0) throw new ArgumentOutOfRangeException(nameof(documentQuota));
        _capacity = capacity;
        _documentQuota = documentQuota;
        _worker = Task.Run(WorkerAsync);
    }

    public RenderScheduler(
        Func<RenderRequest, CancellationToken, ValueTask<IPixelBufferLease>> execute,
        RenderSchedulerOptions options)
        : this(execute, (options ?? throw new ArgumentNullException(nameof(options))).Capacity, options.DocumentQuota)
    {
    }

    public int Capacity => _capacity;
    public int DocumentQuota => _documentQuota;
    public int PendingCount { get { lock (_gate) return _pending; } }
    public int GetPendingCount(DocumentId documentId)
    {
        lock (_gate) return _documents.TryGetValue(documentId, out var document) ? document.Pending : 0;
    }
    public int BusyCount => Volatile.Read(ref _busy);
    public bool IsBusy => BusyCount != 0;
    public event EventHandler? BusyStateChanged;

    public Task<RenderJobResult> EnqueueAsync(RenderRequest request, CancellationToken cancellationToken = default)
        => EnqueueAsync(request, null, cancellationToken);

    public Task<RenderJobResult> SubmitAsync(RenderRequest request, CancellationToken cancellationToken = default)
        => EnqueueAsync(request, null, cancellationToken);

    public Task<RenderJobResult> QueueAsync(RenderRequest request, CancellationToken cancellationToken = default)
        => EnqueueAsync(request, null, cancellationToken);

    public Task<RenderJobResult> ScheduleAsync(RenderRequest request, RenderJobOptions? options = null, CancellationToken cancellationToken = default)
        => EnqueueAsync(request, options, cancellationToken);

    public Task<RenderJobResult> SubmitAsync(RenderRequest request, RenderJobOptions? options, CancellationToken cancellationToken = default)
        => EnqueueAsync(request, options, cancellationToken);

    public Task<RenderJobResult> EnqueueAsync(RenderRequest request, RenderJobOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        options ??= RenderJobOptions.Default;
        var identity = new RenderJobIdentity(request.Key);
        var waiter = new Waiter(request, cancellationToken);
        Job? jobToWake = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            var document = GetDocument(request.Key.DocumentId);
            if (document.Closed || request.Generation.Value < document.Generation.Value ||
                (document.RevisionKnown && request.Key.ContentRevision.Value < document.ContentRevision))
            {
                waiter.TrySetResult(new RenderJobResult(request, document.Closed ? RenderJobCompletionStatus.Closed : RenderJobCompletionStatus.Stale));
                return waiter.Task;
            }

            // A newer request is an authoritative observation of the document's generation/revision.
            if (request.Generation.Value > document.Generation.Value) document.Generation = request.Generation;
            if (_singleFlight.TryGetValue(identity, out var existing))
            {
                if (!existing.IsInvalidated)
                {
                    existing.AddWaiter(waiter);
                    return waiter.Task;
                }

                // An invalidated operation cannot be the single flight for a newer generation.
                // A queued invalidated operation is retired immediately; a running one remains
                // tracked by its own completion while the new operation takes the identity slot.
                _singleFlight.Remove(identity);
                if (existing.IsQueued)
                {
                    RemoveQueued(existing);
                    existing.CompleteAll(RenderJobCompletionStatus.Stale);
                }
            }

            if (_pending >= _capacity)
            {
                while (_pending >= _capacity && TryEvictOne(out var victim))
                {
                    victim.CompleteAll(RenderJobCompletionStatus.Evicted);
                }

                var visible = IsVisible(request, options);
                if (_pending >= _capacity && !visible)
                {
                    waiter.TrySetResult(new RenderJobResult(request, RenderJobCompletionStatus.Rejected));
                    return waiter.Task;
                }
                // Visible work is never rejected. A queue containing only visible work may briefly exceed capacity.
            }

            if (document.Pending >= _documentQuota && !IsVisible(request, options))
            {
                waiter.TrySetResult(new RenderJobResult(request, RenderJobCompletionStatus.Rejected));
                return waiter.Task;
            }

            var job = new Job(this, request, options, identity, ++_sequence);
            job.AddWaiter(waiter);
            _singleFlight.Add(identity, job);
            _pending++;
            document.Pending++;
            Lane(request.Priority).Enqueue(job);
            Interlocked.Increment(ref _busy);
            jobToWake = job;
        }

        if (jobToWake is not null) _signal.Release();
        RaiseBusyChanged();
        return waiter.Task;
    }

    /// <summary>Atomically makes all work from earlier generations ineligible for publication.</summary>
    public void AdvanceGeneration(DocumentId documentId, RenderGeneration generation, ContentRevision? contentRevision = null)
    {
        List<Job> cancel;
        lock (_gate)
        {
            ThrowIfDisposed();
            var document = GetDocument(documentId);
            if (generation.Value < document.Generation.Value) return;
            document.Generation = generation;
            if (contentRevision is { } revision && revision.Value >= document.ContentRevision)
            {
                document.ContentRevision = revision.Value;
                document.RevisionKnown = true;
            }
            cancel = _singleFlight.Values.Where(j => j.Request.Key.DocumentId == documentId && j.Request.Generation.Value < generation.Value).ToList();
            foreach (var job in cancel) job.Invalidate();
        }
        _signal.Release();
    }

    public void Invalidate(DocumentId documentId, RenderGeneration generation, ContentRevision? contentRevision = null)
        => AdvanceGeneration(documentId, generation, contentRevision);

    public void InvalidateGeneration(DocumentId documentId, RenderGeneration generation, ContentRevision? contentRevision = null)
        => AdvanceGeneration(documentId, generation, contentRevision);

    /// <summary>Closes a document and synchronously suppresses queued and in-flight publication.</summary>
    public void CloseDocument(DocumentId documentId)
    {
        List<Job> close;
        lock (_gate)
        {
            ThrowIfDisposed();
            var document = GetDocument(documentId);
            document.Closed = true;
            document.Generation = new RenderGeneration(checked(document.Generation.Value + 1));
            close = _singleFlight.Values.Where(j => j.Request.Key.DocumentId == documentId).ToList();
            foreach (var job in close) job.Invalidate();
        }
        _signal.Release();
    }

    public void ReopenDocument(DocumentId documentId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var document = GetDocument(documentId);
            document.Closed = false;
        }
    }

    public bool IsPublicationEligible(RenderRequest request)
    {
        lock (_gate)
        {
            return !_disposed && _documents.TryGetValue(request.Key.DocumentId, out var state) && !state.Closed &&
                state.Generation == request.Generation && (!state.RevisionKnown || state.ContentRevision == request.Key.ContentRevision.Value);
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Job> jobs;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            jobs = _singleFlight.Values.ToList();
            foreach (var job in jobs) job.Invalidate();
        }
        _shutdown.Cancel();
        _signal.Release();
        try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        lock (_gate)
        {
            foreach (var job in _singleFlight.Values.ToList())
            {
                _singleFlight.Remove(job.Identity);
                if (job.IsQueued)
                {
                    Lane(job.Request.Priority).Remove(job);
                    _pending--;
                    GetDocument(job.Request.Key.DocumentId).Pending--;
                    Interlocked.Decrement(ref _busy);
                }
                job.CompleteAll(RenderJobCompletionStatus.Closed);
            }
        }
        RaiseBusyChanged();
        _shutdown.Dispose();
        _signal.Dispose();
    }

    private async Task WorkerAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                Job? job;
                lock (_gate)
                {
                    if (_disposed) break;
                    job = DequeueNext();
                }
                if (job is not null) await ExecuteJobAsync(job).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task ExecuteJobAsync(Job job)
    {
        RenderJobCompletionStatus status;
        IPixelBufferLease? lease = null;
        Exception? error = null;
        using var deadline = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, job.Token, deadline.Token);
        var remaining = job.Request.Deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) deadline.Cancel();
        else deadline.CancelAfter(remaining);
        try
        {
            if (deadline.IsCancellationRequested) status = RenderJobCompletionStatus.DeadlineExceeded;
            else if (!job.HasActiveWaiters) status = RenderJobCompletionStatus.Cancelled;
            else if (!job.HasEligibleWaiter) status = IsClosed(job.Request.Key.DocumentId) ? RenderJobCompletionStatus.Closed : RenderJobCompletionStatus.Stale;
            else
            {
                lease = await _execute(job.Request, linked.Token).ConfigureAwait(false);
                if (linked.IsCancellationRequested)
                {
                    status = deadline.IsCancellationRequested ? RenderJobCompletionStatus.DeadlineExceeded :
                        (!job.HasEligibleWaiter ? (IsClosed(job.Request.Key.DocumentId) ? RenderJobCompletionStatus.Closed : RenderJobCompletionStatus.Stale) : RenderJobCompletionStatus.Cancelled);
                }
                else
                {
                    status = job.HasEligibleWaiter ? RenderJobCompletionStatus.Published :
                        (IsClosed(job.Request.Key.DocumentId) ? RenderJobCompletionStatus.Closed : RenderJobCompletionStatus.Stale);
                }
            }
        }
        catch (OperationCanceledException)
        {
            status = deadline.IsCancellationRequested ? RenderJobCompletionStatus.DeadlineExceeded :
                (!job.HasEligibleWaiter ? (IsClosed(job.Request.Key.DocumentId) ? RenderJobCompletionStatus.Closed : RenderJobCompletionStatus.Stale) : RenderJobCompletionStatus.Cancelled);
        }
        catch (Exception ex)
        {
            status = RenderJobCompletionStatus.Faulted;
            error = ex;
        }

        if (status != RenderJobCompletionStatus.Published && lease is not null)
        {
            try { await lease.DisposeAsync().ConfigureAwait(false); } catch { }
            lease = null;
        }
        lock (_gate)
        {
            if (_singleFlight.TryGetValue(job.Identity, out var current) && ReferenceEquals(current, job))
                _singleFlight.Remove(job.Identity);
        }
        job.CompleteAll(status, lease, error);
        Interlocked.Decrement(ref _busy);
        RaiseBusyChanged();
    }

    private Job? DequeueNext()
    {
        foreach (var lane in _lanes)
        {
            var job = lane.Dequeue();
            if (job is null) continue;
            _pending--;
            var document = GetDocument(job.Request.Key.DocumentId);
            document.Pending--;
            job.IsQueued = false;
            return job;
        }
        return null;
    }

    private bool TryEvictOne(out Job job)
    {
        // This ordering is normative: oldest background, then non-visible thumbnails, then farthest prefetch.
        foreach (var lane in _lanes.Reverse())
        {
            var candidate = lane.FindEvictionCandidate(0);
            if (candidate is not null) { RemoveQueued(candidate); job = candidate; return true; }
        }
        foreach (var lane in _lanes)
        {
            var candidate = lane.FindEvictionCandidate(1);
            if (candidate is not null) { RemoveQueued(candidate); job = candidate; return true; }
        }
        var prefetch = _lanes[(int)EngineJobPriority.DirectionalPrefetch].FindEvictionCandidate(2);
        if (prefetch is not null) { RemoveQueued(prefetch); job = prefetch; return true; }
        job = null!;
        return false;
    }

    private void RemoveQueued(Job job)
    {
        Lane(job.Request.Priority).Remove(job);
        _singleFlight.Remove(job.Identity);
        _pending--;
        GetDocument(job.Request.Key.DocumentId).Pending--;
        Interlocked.Decrement(ref _busy);
        RaiseBusyChanged();
    }

    private PriorityLane Lane(EngineJobPriority priority) => _lanes[(int)priority];
    private bool IsClosed(DocumentId id) { lock (_gate) return _documents.TryGetValue(id, out var state) && state.Closed; }
    private static bool IsVisible(RenderRequest request, RenderJobOptions options)
        => options.IsVisible || (!options.IsThumbnail && request.Priority <= EngineJobPriority.VisibleThumbnail);
    private DocumentState GetDocument(DocumentId id)
    {
        if (!_documents.TryGetValue(id, out var state)) _documents.Add(id, state = new DocumentState());
        return state;
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void RaiseBusyChanged() { try { BusyStateChanged?.Invoke(this, EventArgs.Empty); } catch { } }

    private sealed class DocumentState
    {
        public RenderGeneration Generation = RenderGeneration.Initial;
        public long ContentRevision;
        public bool RevisionKnown;
        public int Pending;
        public bool Closed;
    }

    private readonly record struct RenderJobIdentity(RenderKey Key);

    private sealed class Waiter
    {
        private readonly TaskCompletionSource<RenderJobResult> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action? _onCancelled;
        public Waiter(RenderRequest request, CancellationToken token)
        {
            Request = request;
            if (token.CanBeCanceled) Registration = token.Register(static state => ((Waiter)state!).Cancel(), this);
            if (token.IsCancellationRequested) TrySetCanceled();
        }
        public RenderRequest Request { get; }
        public CancellationTokenRegistration Registration { get; }
        public Task<RenderJobResult> Task => _source.Task;
        public bool IsActive => !_source.Task.IsCompleted;
        public void SetCancellationCallback(Action callback) => _onCancelled = callback;
        private void Cancel()
        {
            if (_source.TrySetCanceled()) _onCancelled?.Invoke();
        }
        public bool TrySetCanceled() => _source.TrySetCanceled();
        public bool TrySetResult(RenderJobResult result) { Registration.Dispose(); return _source.TrySetResult(result); }
    }

    private sealed class Job
    {
        private readonly object _waitersGate = new();
        private readonly List<Waiter> _waiters = new();
        private readonly CancellationTokenSource _cancel = new();
        private readonly RenderScheduler _owner;
        private int _invalidated;
        public Job(RenderScheduler owner, RenderRequest request, RenderJobOptions options, RenderJobIdentity identity, long sequence)
        { _owner = owner; Request = request; Options = options; Identity = identity; Sequence = sequence; }
        public RenderRequest Request { get; }
        public RenderJobOptions Options { get; }
        public RenderJobIdentity Identity { get; }
        public long Sequence { get; }
        public bool IsQueued { get; set; } = true;
        public bool IsInvalidated => Volatile.Read(ref _invalidated) != 0;
        public CancellationToken Token => _cancel.Token;
        public void AddWaiter(Waiter waiter)
        {
            lock (_waitersGate) _waiters.Add(waiter);
            waiter.SetCancellationCallback(() => { if (!HasActiveWaiters) Invalidate(); });
        }
        public bool HasActiveWaiters { get { lock (_waitersGate) return _waiters.Any(static waiter => waiter.IsActive); } }
        public bool HasEligibleWaiter
        {
            get
            {
                Waiter[] waiters;
                lock (_waitersGate) waiters = _waiters.ToArray();
                return waiters.Any(waiter => waiter.IsActive && _owner.IsPublicationEligible(waiter.Request));
            }
        }
        public void Invalidate()
        {
            if (Interlocked.Exchange(ref _invalidated, 1) != 0) return;
            try { _cancel.Cancel(); } catch (ObjectDisposedException) { }
        }
        public void CompleteAll(RenderJobCompletionStatus status, IPixelBufferLease? lease = null, Exception? error = null)
        {
            Waiter[] waiters;
            lock (_waitersGate) waiters = _waiters.ToArray();
            foreach (var waiter in waiters)
            {
                if (!waiter.IsActive) continue;
                var waiterStatus = status;
                var waiterLease = lease;
                if (status == RenderJobCompletionStatus.Published && !_owner.IsPublicationEligible(waiter.Request))
                {
                    waiterStatus = _owner.IsClosed(waiter.Request.Key.DocumentId) ? RenderJobCompletionStatus.Closed : RenderJobCompletionStatus.Stale;
                    waiterLease = null;
                }
                waiter.TrySetResult(new RenderJobResult(waiter.Request, waiterStatus, waiterLease, error));
            }
            _cancel.Dispose();
        }
    }

    private sealed class PriorityLane
    {
        private readonly Dictionary<DocumentId, Queue<Job>> _queues = new();
        private readonly LinkedList<DocumentId> _roundRobin = new();
        private DocumentId _lastServed;
        public PriorityLane(EngineJobPriority priority) { Priority = priority; }
        public EngineJobPriority Priority { get; }
        public void Enqueue(Job job)
        {
            if (!_queues.TryGetValue(job.Request.Key.DocumentId, out var queue))
            {
                _queues.Add(job.Request.Key.DocumentId, queue = new Queue<Job>());
                _roundRobin.AddLast(job.Request.Key.DocumentId);
            }
            queue.Enqueue(job);
        }
        public Job? Dequeue()
        {
            if (_roundRobin.First is null) return null;
            var previous = _roundRobin.Find(_lastServed);
            var node = previous?.Next ?? _roundRobin.First;
            if (node is null) return null;
            _roundRobin.Remove(node);
            if (!_queues.TryGetValue(node.Value, out var queue) || queue.Count == 0)
            {
                _queues.Remove(node.Value);
                return Dequeue();
            }
            var job = queue.Dequeue();
            _lastServed = node.Value;
            if (queue.Count != 0) _roundRobin.AddLast(node.Value); else _queues.Remove(node.Value);
            return job;
        }
        public Job? FindEvictionCandidate(int kind)
        {
            Job? candidate = null;
            foreach (var queue in _queues.Values)
            foreach (var job in queue)
            {
                var eligible = kind switch
                {
                    0 => job.Request.Priority == EngineJobPriority.Background && !job.Options.IsVisible && !job.Options.IsThumbnail,
                    1 => job.Options.IsThumbnail && !job.Options.IsVisible,
                    2 => job.Request.Priority == EngineJobPriority.DirectionalPrefetch && !job.Options.IsVisible,
                    _ => false
                };
                if (eligible && (candidate is null || (kind == 2 ? job.Options.PrefetchDistance > candidate.Options.PrefetchDistance : job.Sequence < candidate.Sequence))) candidate = job;
            }
            return candidate;
        }
        public void Remove(Job job)
        {
            if (!_queues.TryGetValue(job.Request.Key.DocumentId, out var queue)) return;
            var remaining = queue.Where(item => !ReferenceEquals(item, job)).ToArray();
            queue.Clear();
            foreach (var item in remaining) queue.Enqueue(item);
            if (queue.Count == 0)
            {
                _queues.Remove(job.Request.Key.DocumentId);
                var node = _roundRobin.Find(job.Request.Key.DocumentId);
                if (node is not null) _roundRobin.Remove(node);
            }
        }
    }
}
