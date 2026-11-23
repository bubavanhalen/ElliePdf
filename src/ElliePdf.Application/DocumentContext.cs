using ElliePdf.Domain.Documents;

namespace ElliePdf.Application;

public enum DocumentOperationKind
{
    Render,
    Search,
    Other
}

/// <summary>
/// Owns one document tab, its immutable projection, and all asynchronous work
/// started on behalf of that tab.
/// </summary>
public sealed class DocumentContext : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly IPdfEngineSession _session;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _renderGenerationCancellation = new();
    private CancellationTokenSource _searchGenerationCancellation = new();
    private readonly Dictionary<long, Task> _operations = [];
    private long _nextOperationId;
    private RenderGeneration _renderGeneration = RenderGeneration.Initial;
    private SearchGeneration _searchGeneration = SearchGeneration.Initial;
    private DocumentState _state;
    private DocumentSnapshot _snapshot;
    private bool _disposed;
    private int _disposeStarted;
    private readonly TaskCompletionSource _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal DocumentContext(DocumentOpenRequest request, IPdfEngineSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Id = request.DocumentId;
        CanonicalPath = request.CanonicalPath;
        _session = session;
        _state = DocumentState.Create(Id);
        _snapshot = new DocumentSnapshot(
            Id,
            _state.ContentRevision,
            _state.SavedRevision,
            _state.StructureRevision,
            request.DisplayName,
            PageCount: 0,
            CurrentPageIndex: 0,
            _state.HasUnsavedChanges,
            _state.RecoveryState,
            _state.ExternalFileState);
    }

    public DocumentId Id { get; }

    public string CanonicalPath { get; }

    public DocumentSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>The immutable domain state owned by this document context.</summary>
    public DocumentState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public RenderGeneration CurrentRenderGeneration
    {
        get
        {
            lock (_sync)
            {
                return _renderGeneration;
            }
        }
    }

    public SearchGeneration CurrentSearchGeneration
    {
        get
        {
            lock (_sync)
            {
                return _searchGeneration;
            }
        }
    }

    public bool IsDisposed => Volatile.Read(ref _disposed);

    public Task<T> RunRenderAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default) =>
        TrackOperationAsync(DocumentOperationKind.Render, operation, cancellationToken);

    public Task<T> RunRenderAsync<T>(Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default) =>
        TrackOperationAsync(DocumentOperationKind.Render, token => operation(token).AsTask(), cancellationToken);

    public Task RunRenderAsync(Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default) =>
        TrackOperationAsync(DocumentOperationKind.Render, operation, cancellationToken);

    public Task RunSearchAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default) =>
        TrackOperationAsync(DocumentOperationKind.Search, operation, cancellationToken);

    public Task RunSearchAsync(Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default) =>
        TrackOperationAsync(DocumentOperationKind.Search, operation, cancellationToken);

    public Task<T> RunOtherAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default) =>
        TrackOperationAsync(DocumentOperationKind.Other, operation, cancellationToken);

    public Task RunOtherAsync(Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default) =>
        TrackOperationAsync(DocumentOperationKind.Other, operation, cancellationToken);

    public Task<T> TrackOperationAsync<T>(DocumentOperationKind kind,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        CancellationTokenSource linkedCancellation;
        long operationId;
        Task<T> task;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancellationToken generationToken = kind switch
            {
                DocumentOperationKind.Render => _renderGenerationCancellation.Token,
                DocumentOperationKind.Search => _searchGenerationCancellation.Token,
                _ => CancellationToken.None
            };
            linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token, generationToken, cancellationToken);
            operationId = ++_nextOperationId;
            task = ExecuteTrackedAsync(operation, linkedCancellation.Token);
            _operations[operationId] = task;
        }

        _ = RemoveOperationWhenCompleteAsync(operationId, task, linkedCancellation);
        return task;
    }

    public Task TrackOperationAsync(DocumentOperationKind kind,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default) =>
        TrackOperationAsync<object?>(kind, async token =>
        {
            await operation(token).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    public RenderGeneration AdvanceRenderGeneration()
    {
        CancellationTokenSource previous;
        RenderGeneration generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _renderGenerationCancellation;
            _renderGenerationCancellation = new CancellationTokenSource();
            _state = DocumentStateReducer.ChangeRenderInputs(_state);
            _renderGeneration = _state.RenderGeneration;
            generation = _renderGeneration;
            PublishStateSnapshotUnderLock();
        }

        previous.Cancel();
        previous.Dispose();
        return generation;
    }

    public SearchGeneration AdvanceSearchGeneration()
    {
        CancellationTokenSource previous;
        SearchGeneration generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _searchGenerationCancellation;
            _searchGenerationCancellation = new CancellationTokenSource();
            _state = DocumentStateReducer.ChangeSearch(_state);
            _searchGeneration = _state.SearchGeneration;
            generation = _searchGeneration;
            PublishStateSnapshotUnderLock();
        }

        previous.Cancel();
        previous.Dispose();
        return generation;
    }

    public void SetPage(int pageIndex)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (pageIndex < 0 || pageIndex >= _snapshot.PageCount)
            {
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            }

            _snapshot = _snapshot with { CurrentPageIndex = pageIndex };
        }
    }

    public void SetPageCount(int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _snapshot = _snapshot with
            {
                PageCount = pageCount,
                CurrentPageIndex = pageCount == 0 ? 0 : Math.Min(_snapshot.CurrentPageIndex, pageCount - 1)
            };
        }
    }

    public void MarkContentChanged()
    {
        CancellationTokenSource previous;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _state = DocumentStateReducer.ApplyContentMutation(_state);
            previous = ReplaceRenderGenerationCancellationUnderLock(_state.RenderGeneration);
            PublishStateSnapshotUnderLock();
        }


        previous.Cancel();
        previous.Dispose();
    }

    public void MarkRecoveredContent()
    {
        CancellationTokenSource previous;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _state = DocumentStateReducer.ApplyContentMutation(_state);
            _state = DocumentStateReducer.MarkRecoveryCheckpointed(
                _state,
                _state.ContentRevision);
            previous = ReplaceRenderGenerationCancellationUnderLock(_state.RenderGeneration);
            PublishStateSnapshotUnderLock();
        }

        previous.Cancel();
        previous.Dispose();
    }

    public SaveOperation BeginSave()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var transition = DocumentStateReducer.BeginSave(_state);
            _state = transition.State;
            PublishStateSnapshotUnderLock();
            return transition.Operation;
        }
    }

    public void CompleteSave(SaveOperation operation)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _state = DocumentStateReducer.CompleteSave(_state, operation);
            PublishStateSnapshotUnderLock();
        }
    }

    public void FailSave(
        SaveOperation operation,
        SaveFailureKind failureKind,
        string errorCode)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _state = DocumentStateReducer.FailSave(_state, operation, failureKind, errorCode);
            PublishStateSnapshotUnderLock();
        }
    }

    public void CancelSave(SaveOperation operation)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _state = DocumentStateReducer.CancelSave(_state, operation);
            PublishStateSnapshotUnderLock();
        }
    }

    public void MarkRecoveryArtifactDeleted()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _state = DocumentStateReducer.DiscardRecovery(_state);
            PublishStateSnapshotUnderLock();
        }
    }

    public void MarkRecoveryCheckpointCompleted(ContentRevision revision, bool succeeded)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _state = succeeded
                ? DocumentStateReducer.MarkRecoveryCheckpointed(_state, revision)
                : DocumentStateReducer.MarkRecoveryFailed(_state);
            PublishStateSnapshotUnderLock();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        Task[] operations;
        CancellationTokenSource lifetime;
        CancellationTokenSource renderGeneration;
        CancellationTokenSource searchGeneration;
        try
        {
            lock (_sync)
            {
                _disposed = true;
                lifetime = _lifetime;
                renderGeneration = _renderGenerationCancellation;
                searchGeneration = _searchGenerationCancellation;
                operations = _operations.Values.ToArray();
            }

            lifetime.Cancel();
            renderGeneration.Cancel();
            searchGeneration.Cancel();

            try
            {
                await Task.WhenAll(operations).ConfigureAwait(false);
            }
            catch
            {
                // Operation failures are delivered to their caller. All operation
                // tasks are nevertheless awaited so the session cannot race them.
            }

            renderGeneration.Dispose();
            searchGeneration.Dispose();
            lifetime.Dispose();
            await _session.DisposeAsync().ConfigureAwait(false);
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }

    private async Task<T> ExecuteTrackedAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) => await operation(cancellationToken).ConfigureAwait(false);

    private async Task RemoveOperationWhenCompleteAsync(long operationId, Task task,
        CancellationTokenSource linkedCancellation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The caller receives the exception; awaiting here ensures it is
            // observed even when a caller intentionally abandons the task.
        }
        finally
        {
            lock (_sync)
            {
                _operations.Remove(operationId);
            }
            linkedCancellation.Dispose();
        }
    }

    private CancellationTokenSource ReplaceRenderGenerationCancellationUnderLock(
        RenderGeneration generation)
    {
        var previous = _renderGenerationCancellation;
        _renderGenerationCancellation = new CancellationTokenSource();
        _renderGeneration = generation;
        return previous;
    }

    private void PublishStateSnapshotUnderLock()
    {
        _snapshot = _snapshot with
        {
            ContentRevision = _state.ContentRevision,
            SavedRevision = _state.SavedRevision,
            StructureRevision = _state.StructureRevision,
            HasUnsavedChanges = _state.HasUnsavedChanges,
            RecoveryState = _state.RecoveryState,
            ExternalFileState = _state.ExternalFileState
        };
    }
}
