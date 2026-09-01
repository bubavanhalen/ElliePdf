using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;

namespace ElliePdf.Rendering;

/// <summary>Application-level work classes which share the single PDF engine lane.</summary>
public enum EngineJobClass
{
    VisibleRender,
    OtherVisible,
    DirectionalOverscan,
    VisibleThumbnail,
    DirectionalPrefetch,
    BackgroundThumbnail,
    Metadata,
    Text,
    Search,
    Print,
    Export,
    Edit,
    Save,

    // Short aliases keep call sites readable while retaining the names used by the spec.
    Thumbnail = VisibleThumbnail,
    Prefetch = DirectionalPrefetch,
    BackgroundIndexing = Search
}

public static class EngineJobClassPolicy
{
    public static EngineJobPriority Priority(this EngineJobClass jobClass) => jobClass switch
    {
        EngineJobClass.VisibleRender => EngineJobPriority.VisibleInteractionCritical,
        EngineJobClass.OtherVisible or EngineJobClass.Text or EngineJobClass.Print
            or EngineJobClass.Export or EngineJobClass.Edit or EngineJobClass.Save => EngineJobPriority.OtherVisible,
        EngineJobClass.DirectionalOverscan => EngineJobPriority.DirectionalOverscan,
        EngineJobClass.VisibleThumbnail or EngineJobClass.Metadata => EngineJobPriority.VisibleThumbnail,
        EngineJobClass.DirectionalPrefetch => EngineJobPriority.DirectionalPrefetch,
        EngineJobClass.Search or EngineJobClass.BackgroundThumbnail => EngineJobPriority.Background,
        _ => throw new ArgumentOutOfRangeException(nameof(jobClass))
    };

    public static bool IsVisible(this EngineJobClass jobClass) => jobClass is
        EngineJobClass.VisibleRender or EngineJobClass.OtherVisible or EngineJobClass.Text
        or EngineJobClass.VisibleThumbnail or EngineJobClass.Metadata
        or EngineJobClass.Print or EngineJobClass.Export or EngineJobClass.Edit or EngineJobClass.Save;

    public static bool IsThumbnail(this EngineJobClass jobClass) => jobClass is EngineJobClass.VisibleThumbnail or EngineJobClass.BackgroundThumbnail;
}

/// <summary>A stable identity for one page-sized or document-sized engine operation.</summary>
public readonly record struct EngineJobRequest(
    DocumentId DocumentId,
    EngineJobClass JobClass,
    string Identity,
    RenderGeneration Generation,
    DateTimeOffset Deadline,
    int PrefetchDistance = 0,
    bool EnforceGeneration = true)
{
    public EngineJobRequest Validate()
    {
        if (DocumentId.Value == Guid.Empty) throw new ArgumentException("A document id is required.", nameof(DocumentId));
        ArgumentException.ThrowIfNullOrWhiteSpace(Identity);
        if (Identity.Length > PdfContractLimits.MaxStringLength)
            throw new ArgumentOutOfRangeException(nameof(Identity));
        if (Deadline == default) throw new ArgumentException("A job deadline is required.", nameof(Deadline));
        if (PrefetchDistance < 0) throw new ArgumentOutOfRangeException(nameof(PrefetchDistance));
        return this;
    }

    public EngineJobPriority Priority => JobClass.Priority();
    public bool IsVisible => JobClass.IsVisible();
    public bool IsThumbnail => JobClass.IsThumbnail();
}

public readonly record struct EngineJobResult<T>(
    RenderJobCompletionStatus Status,
    T? Value,
    Exception? Error = null);

/// <summary>A bounded, single-flight scheduler for non-raster PDF engine operations.</summary>
public sealed class EngineJobScheduler : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly int _documentQuota;
    private readonly PriorityLane[] _lanes = Enum.GetValues<EngineJobPriority>().Select(static p => new PriorityLane(p)).ToArray();
    private readonly Dictionary<Identity, Job> _singleFlight = new();
    private readonly Dictionary<DocumentId, DocumentState> _documents = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private long _sequence;
    private int _pending;
    private int _busy;
    private bool _disposed;

    public EngineJobScheduler(int capacity = RenderScheduler.DefaultCapacity, int documentQuota = RenderScheduler.DefaultDocumentQuota)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (documentQuota <= 0) throw new ArgumentOutOfRangeException(nameof(documentQuota));
        _capacity = capacity;
        _documentQuota = documentQuota;
        _worker = Task.Run(WorkerAsync);
    }

    public int Capacity => _capacity;
    public int DocumentQuota => _documentQuota;
    public int PendingCount { get { lock (_gate) return _pending; } }
    public int BusyCount => Volatile.Read(ref _busy);
    public bool IsBusy => BusyCount != 0;
    public event EventHandler? BusyStateChanged;

    public Task<EngineJobResult<T>> EnqueueAsync<T>(EngineJobRequest request, Func<CancellationToken, ValueTask<T>> execute, CancellationToken cancellationToken = default)
        => ScheduleAsync(request, execute, cancellationToken);

    public Task<EngineJobResult<T>> SubmitAsync<T>(EngineJobRequest request, Func<CancellationToken, ValueTask<T>> execute, CancellationToken cancellationToken = default)
        => ScheduleAsync(request, execute, cancellationToken);

    public Task<EngineJobResult<T>> ScheduleAsync<T>(
        EngineJobRequest request,
        Func<CancellationToken, ValueTask<T>> execute,
        CancellationToken cancellationToken = default)
    {
        request.Validate();
        ArgumentNullException.ThrowIfNull(execute);
        var waiter = new Waiter<T>(cancellationToken);
        var identity = new Identity(request.DocumentId, request.JobClass, request.Identity);
        lock (_gate)
        {
            ThrowIfDisposed();
            var document = GetDocument(request.DocumentId);
            if (document.Closed || (request.EnforceGeneration && request.Generation.Value < document.Generation.Value))
            {
                waiter.TrySetResult(new EngineJobResult<T>(
                    document.Closed ? RenderJobCompletionStatus.Closed : RenderJobCompletionStatus.Stale,
                    default));
                return waiter.Task;
            }

            if (request.EnforceGeneration && request.Generation.Value > document.Generation.Value) document.Generation = request.Generation;
            if (_singleFlight.TryGetValue(identity, out var existing) && !existing.IsInvalidated)
            {
                existing.AddWaiter(waiter);
                return waiter.Task;
            }

            if (_singleFlight.TryGetValue(identity, out existing))
            {
                _singleFlight.Remove(identity);
                if (existing.IsQueued)
                {
                    RemoveQueued(existing);
                    existing.CompleteAll(RenderJobCompletionStatus.Stale);
                }
            }

            if (_pending >= _capacity)
            {
                while (_pending >= _capacity && TryEvictOne(out var victim)) victim.CompleteAll(RenderJobCompletionStatus.Evicted);
                if (_pending >= _capacity && !request.IsVisible)
                {
                    waiter.TrySetResult(new EngineJobResult<T>(RenderJobCompletionStatus.Rejected, default));
                    return waiter.Task;
                }
            }

            if (document.Pending >= _documentQuota && !request.IsVisible)
            {
                waiter.TrySetResult(new EngineJobResult<T>(RenderJobCompletionStatus.Rejected, default));
                return waiter.Task;
            }

            var job = new Job(this, request, async token => await execute(token).ConfigureAwait(false), identity, ++_sequence);
            job.AddWaiter(waiter);
            _singleFlight[identity] = job;
            _pending++;
            document.Pending++;
            Lane(request.Priority).Enqueue(job);
            Interlocked.Increment(ref _busy);
        }
        _signal.Release();
        RaiseBusyChanged();
        return waiter.Task;
    }

    public void AdvanceGeneration(DocumentId documentId, RenderGeneration generation)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var document = GetDocument(documentId);
            if (generation.Value < document.Generation.Value) return;
            document.Generation = generation;
            foreach (var job in _singleFlight.Values.Where(j => j.Request.DocumentId == documentId && j.Request.EnforceGeneration && j.Request.Generation.Value < generation.Value)) job.Invalidate();
        }
        _signal.Release();
    }

    public void CloseDocument(DocumentId documentId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var document = GetDocument(documentId);
            document.Closed = true;
            document.Generation = new RenderGeneration(checked(document.Generation.Value + 1));
            foreach (var job in _singleFlight.Values.Where(j => j.Request.DocumentId == documentId)) job.Invalidate();
        }
        _signal.Release();
    }

    public void ReopenDocument(DocumentId documentId)
    {
        lock (_gate) { ThrowIfDisposed(); GetDocument(documentId).Closed = false; }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var job in _singleFlight.Values) job.Invalidate();
        }
        _shutdown.Cancel();
        _signal.Release();
        try { await _worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        lock (_gate)
        {
            foreach (var job in _singleFlight.Values.ToArray())
            {
                _singleFlight.Remove(job.Identity);
                if (job.IsQueued) RemoveQueued(job);
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
                lock (_gate) { if (_disposed) break; job = DequeueNext(); }
                if (job is not null) await ExecuteJobAsync(job).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task ExecuteJobAsync(Job job)
    {
        var status = RenderJobCompletionStatus.Published;
        object? value = null;
        Exception? error = null;
        using var deadline = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, job.Token, deadline.Token);
        var remaining = job.Request.Deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) deadline.Cancel(); else deadline.CancelAfter(remaining);
        try
        {
            if (deadline.IsCancellationRequested) status = RenderJobCompletionStatus.DeadlineExceeded;
            else if (!job.HasActiveWaiters) status = RenderJobCompletionStatus.Cancelled;
            else if (!job.HasEligibleWaiter) status = IsClosed(job.Request.DocumentId) ? RenderJobCompletionStatus.Closed : RenderJobCompletionStatus.Stale;
            else
            {
                value = await job.Execute(linked.Token).ConfigureAwait(false);
                if (deadline.IsCancellationRequested) status = RenderJobCompletionStatus.DeadlineExceeded;
                else if (linked.IsCancellationRequested)
                    status = !job.HasEligibleWaiter
                        ? (IsClosed(job.Request.DocumentId) ? RenderJobCompletionStatus.Closed : RenderJobCompletionStatus.Stale)
                        : RenderJobCompletionStatus.Cancelled;
                else if (!job.HasEligibleWaiter)
                    status = IsClosed(job.Request.DocumentId) ? RenderJobCompletionStatus.Closed : RenderJobCompletionStatus.Stale;
            }
        }
        catch (OperationCanceledException)
        {
            status = deadline.IsCancellationRequested
                ? RenderJobCompletionStatus.DeadlineExceeded
                : !job.HasEligibleWaiter
                    ? (IsClosed(job.Request.DocumentId) ? RenderJobCompletionStatus.Closed : RenderJobCompletionStatus.Stale)
                    : RenderJobCompletionStatus.Cancelled;
        }
        catch (Exception exception) { status = RenderJobCompletionStatus.Faulted; error = exception; }
        lock (_gate)
        {
            if (_singleFlight.TryGetValue(job.Identity, out var current) && ReferenceEquals(current, job)) _singleFlight.Remove(job.Identity);
        }
        job.CompleteAll(status, value, error);
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
            GetDocument(job.Request.DocumentId).Pending--;
            job.IsQueued = false;
            return job;
        }
        return null;
    }

    private bool TryEvictOne(out Job job)
    {
        foreach (var lane in _lanes.Reverse())
            if (lane.FindEvictionCandidate(0) is { } background) { RemoveQueued(background); job = background; return true; }
        foreach (var lane in _lanes)
            if (lane.FindEvictionCandidate(1) is { } thumbnail) { RemoveQueued(thumbnail); job = thumbnail; return true; }
        if (_lanes[(int)EngineJobPriority.DirectionalPrefetch].FindEvictionCandidate(2) is { } prefetch)
        { RemoveQueued(prefetch); job = prefetch; return true; }
        job = null!;
        return false;
    }

    private void RemoveQueued(Job job)
    {
        Lane(job.Request.Priority).Remove(job);
        _singleFlight.Remove(job.Identity);
        _pending--;
        GetDocument(job.Request.DocumentId).Pending--;
        Interlocked.Decrement(ref _busy);
        RaiseBusyChanged();
    }

    private PriorityLane Lane(EngineJobPriority priority) => _lanes[(int)priority];
    private bool IsClosed(DocumentId id) { lock (_gate) return _documents.TryGetValue(id, out var state) && state.Closed; }
    private bool IsOpen(DocumentId id, RenderGeneration generation)
    {
        lock (_gate)
        {
            return !_disposed && _documents.TryGetValue(id, out var state)
                && !state.Closed && state.Generation == generation;
        }
    }
    private DocumentState GetDocument(DocumentId id) => _documents.TryGetValue(id, out var state) ? state : _documents[id] = new DocumentState();
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void RaiseBusyChanged() { try { BusyStateChanged?.Invoke(this, EventArgs.Empty); } catch { } }

    private sealed class DocumentState { public RenderGeneration Generation = RenderGeneration.Initial; public int Pending; public bool Closed; }
    private readonly record struct Identity(DocumentId DocumentId, EngineJobClass JobClass, string OperationIdentity);

    private interface IWaiter
    {
        bool IsActive { get; }
        void SetCancellationCallback(Action callback);
        void Complete(RenderJobCompletionStatus status, object? value, Exception? error);
    }

    private sealed class Waiter<T> : IWaiter
    {
        private readonly TaskCompletionSource<EngineJobResult<T>> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Action? _onCancelled;
        public Waiter(CancellationToken token) { if (token.CanBeCanceled) Registration = token.Register(static state => ((Waiter<T>)state!).Cancel(), this); }
        public CancellationTokenRegistration Registration { get; }
        public Task<EngineJobResult<T>> Task => _source.Task;
        public bool IsActive => !_source.Task.IsCompleted;
        public void SetCancellationCallback(Action callback) => _onCancelled = callback;
        private void Cancel() { if (_source.TrySetCanceled()) _onCancelled?.Invoke(); }
        public void TrySetResult(EngineJobResult<T> result) { Registration.Dispose(); _source.TrySetResult(result); }
        void IWaiter.SetCancellationCallback(Action callback) => SetCancellationCallback(callback);
        void IWaiter.Complete(RenderJobCompletionStatus status, object? value, Exception? error)
            => TrySetResult(new EngineJobResult<T>(
                status,
                status == RenderJobCompletionStatus.Published && value is T typed ? typed : default,
                error));
    }

    private sealed class Job
    {
        private readonly object _gate = new();
        private readonly List<IWaiter> _waiters = [];
        private readonly CancellationTokenSource _cancel = new();
        private readonly EngineJobScheduler _owner;
        private readonly Func<CancellationToken, ValueTask<object?>> _execute;
        private int _invalidated;
        public Job(EngineJobScheduler owner, EngineJobRequest request, Func<CancellationToken, ValueTask<object?>> execute, Identity identity, long sequence)
        {
            _owner = owner; Request = request; Identity = identity; Sequence = sequence;
            _execute = async token => await execute(token).ConfigureAwait(false);
        }
        public EngineJobRequest Request { get; }
        public Identity Identity { get; }
        public long Sequence { get; }
        public bool IsQueued { get; set; } = true;
        public bool IsInvalidated => Volatile.Read(ref _invalidated) != 0;
        public CancellationToken Token => _cancel.Token;
        public ValueTask<object?> Execute(CancellationToken token) => _execute(token);
        public void AddWaiter<T>(Waiter<T> waiter) { lock (_gate) _waiters.Add(waiter); waiter.SetCancellationCallback(() => { if (!HasActiveWaiters) Invalidate(); }); }
        public bool HasActiveWaiters { get { lock (_gate) return _waiters.Any(static item => item.IsActive); } }
        public bool HasEligibleWaiter => HasActiveWaiters && !IsInvalidated &&
            (!Request.EnforceGeneration || _owner.IsOpen(Request.DocumentId, Request.Generation));
        public void Invalidate() { if (Interlocked.Exchange(ref _invalidated, 1) == 0) try { _cancel.Cancel(); } catch (ObjectDisposedException) { } }
        public void CompleteAll(RenderJobCompletionStatus status, object? value = null, Exception? error = null)
        {
            IWaiter[] waiters; lock (_gate) waiters = _waiters.ToArray();
            foreach (var waiter in waiters) waiter.Complete(status, value, error);
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
        public void Enqueue(Job job) { if (!_queues.TryGetValue(job.Request.DocumentId, out var queue)) { _queues[job.Request.DocumentId] = queue = new(); _roundRobin.AddLast(job.Request.DocumentId); } queue.Enqueue(job); }
        public Job? Dequeue()
        {
            if (_roundRobin.First is null) return null;
            var node = _roundRobin.Find(_lastServed)?.Next ?? _roundRobin.First;
            _roundRobin.Remove(node!);
            if (!_queues.TryGetValue(node!.Value, out var queue) || queue.Count == 0) { _queues.Remove(node.Value); return Dequeue(); }
            var job = queue.Dequeue(); _lastServed = node.Value;
            if (queue.Count > 0) _roundRobin.AddLast(node.Value); else _queues.Remove(node.Value);
            return job;
        }
        public Job? FindEvictionCandidate(int kind)
        {
            Job? candidate = null;
            foreach (var queue in _queues.Values) foreach (var job in queue)
            {
                var eligible = kind switch
                {
                    0 => job.Request.JobClass == EngineJobClass.Search,
                    1 => job.Request.IsThumbnail && !job.Request.IsVisible,
                    2 => job.Request.JobClass == EngineJobClass.DirectionalPrefetch && !job.Request.IsVisible,
                    _ => false
                };
                if (eligible && (candidate is null || (kind == 2 ? job.Request.PrefetchDistance > candidate.Request.PrefetchDistance : job.Sequence < candidate.Sequence))) candidate = job;
            }
            return candidate;
        }
        public void Remove(Job job) { if (!_queues.TryGetValue(job.Request.DocumentId, out var queue)) return; var remaining = queue.Where(item => !ReferenceEquals(item, job)).ToArray(); queue.Clear(); foreach (var item in remaining) queue.Enqueue(item); if (queue.Count == 0) { _queues.Remove(job.Request.DocumentId); var node = _roundRobin.Find(job.Request.DocumentId); if (node is not null) _roundRobin.Remove(node); } }
    }
}
