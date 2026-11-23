using System.Collections.Immutable;
using ElliePdf.Domain.Documents;

namespace ElliePdf.Application;

/// <summary>Instance-scoped owner of document tabs for one application window.</summary>
public sealed class DocumentWorkspace : IAsyncDisposable
{
    private readonly IPdfEngineSessionFactory _sessionFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DocumentContext> _documents =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingOpen> _pendingOpens =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<PendingOpen> _runningOpens = [];
    private readonly List<Exception> _openCleanupFailures = [];
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private DocumentContext? _activeDocument;
    private bool _disposed;
    private int _disposeStarted;
    private readonly TaskCompletionSource _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DocumentWorkspace(IPdfEngineSessionFactory sessionFactory)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    public ImmutableArray<DocumentContext> Documents
    {
        get
        {
            _gate.Wait();
            try
            {
                return _documents.Values.ToImmutableArray();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public DocumentContext? ActiveDocument
    {
        get
        {
            _gate.Wait();
            try
            {
                return _activeDocument;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public WorkspaceSnapshot Snapshot
    {
        get
        {
            _gate.Wait();
            try
            {
                return new WorkspaceSnapshot(
                    _documents.Values.Select(static context => context.Snapshot).ToImmutableArray(),
                    _activeDocument?.Id);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public async ValueTask<DocumentContext> OpenOrActivateAsync(string path,
        CancellationToken cancellationToken = default)
        => await OpenOrActivateAsync(path, activate: true, cancellationToken).ConfigureAwait(false);

    public async ValueTask<DocumentContext> OpenOrActivateAsync(
        string path,
        bool activate,
        CancellationToken cancellationToken = default)
    {
        string canonicalPath = CanonicalizePath(path);
        PendingOpen pending;
        bool startOpen = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_documents.TryGetValue(canonicalPath, out DocumentContext? existing))
            {
                if (activate)
                {
                    _activeDocument = existing;
                }
                return existing;
            }
            if (_pendingOpens.TryGetValue(canonicalPath, out pending!)
                && pending.Completion.Task.IsCompleted)
            {
                _pendingOpens.Remove(canonicalPath);
                pending = null!;
            }
            if (pending is null)
            {
                pending = new PendingOpen(_lifetimeCancellation.Token);
                _pendingOpens.Add(canonicalPath, pending);
                _runningOpens.Add(pending);
                startOpen = true;
            }
            pending.WaiterCount++;
        }
        finally
        {
            _gate.Release();
        }

        if (startOpen)
        {
            _ = RunPendingOpenAsync(canonicalPath, pending);
        }

        try
        {
            DocumentContext context = await pending.Completion.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (activate)
            {
                await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _activeDocument = context;
                }
                finally
                {
                    _gate.Release();
                }
            }
            return context;
        }
        finally
        {
            await ReleasePendingWaiterAsync(canonicalPath, pending).ConfigureAwait(false);
        }
    }

    private async Task RunPendingOpenAsync(string canonicalPath, PendingOpen pending)
    {
        DocumentId documentId = DocumentId.New();
        var request = new DocumentOpenRequest(documentId, canonicalPath, Path.GetFileName(canonicalPath));
        IPdfEngineSession? session = null;
        try
        {
            session = await _sessionFactory.OpenAsync(request, pending.Cancellation.Token)
                .ConfigureAwait(false);
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                pending.Cancellation.Token.ThrowIfCancellationRequested();
                if (pending.WaiterCount == 0)
                {
                    throw new OperationCanceledException(pending.Cancellation.Token);
                }
                if (_documents.TryGetValue(canonicalPath, out DocumentContext? existing))
                {
                    pending.Completion.TrySetResult(existing);
                    return;
                }
                var context = new DocumentContext(request, session);
                _documents.Add(canonicalPath, context);
                session = null;
                pending.Completion.TrySetResult(context);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (pending.Cancellation.IsCancellationRequested)
        {
            pending.Completion.TrySetCanceled(pending.Cancellation.Token);
        }
        catch (Exception exception)
        {
            pending.Completion.TrySetException(exception);
        }
        finally
        {
            Exception? cleanupFailure = null;
            try
            {
                if (session is not null)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
                pending.Completion.TrySetException(exception);
            }

            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_pendingOpens.TryGetValue(canonicalPath, out PendingOpen? current)
                    && ReferenceEquals(current, pending))
                {
                    _pendingOpens.Remove(canonicalPath);
                }
                _runningOpens.Remove(pending);
                if (cleanupFailure is not null)
                {
                    _openCleanupFailures.Add(cleanupFailure);
                }
            }
            finally
            {
                _gate.Release();
            }
            pending.Cancellation.Dispose();
            pending.RunnerCompletion.TrySetResult();
        }
    }

    private async ValueTask ReleasePendingWaiterAsync(string canonicalPath, PendingOpen pending)
    {
        bool cancel = false;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            pending.WaiterCount--;
            if (pending.WaiterCount == 0 && !pending.Completion.Task.IsCompleted
                && _pendingOpens.TryGetValue(canonicalPath, out PendingOpen? current)
                && ReferenceEquals(current, pending))
            {
                cancel = true;
            }
        }
        finally
        {
            _gate.Release();
        }
        if (cancel)
        {
            try
            {
                pending.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The runner completed between releasing the gate and cancellation.
            }
        }
    }

    /// <summary>
    /// Adopts a session opened by a UI-owned workflow such as password-free session
    /// restoration. The workspace remains the sole lifetime owner after adoption.
    /// </summary>
    public async ValueTask<DocumentContext> AttachOrActivateAsync(
        DocumentOpenRequest request,
        IPdfEngineSession session,
        bool activate = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);
        if (session.DocumentId != request.DocumentId)
        {
            throw new ArgumentException("The session identity must match the open request.", nameof(session));
        }

        string canonicalPath = CanonicalizePath(request.CanonicalPath);
        var ownershipResolved = false;
        var gateEntered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_documents.TryGetValue(canonicalPath, out DocumentContext? existing))
            {
                ownershipResolved = true;
                await session.DisposeAsync().ConfigureAwait(false);
                if (activate)
                {
                    _activeDocument = existing;
                }
                return existing;
            }

            var canonicalRequest = request with { CanonicalPath = canonicalPath };
            var context = new DocumentContext(canonicalRequest, session);
            _documents.Add(canonicalPath, context);
            ownershipResolved = true;
            if (activate)
            {
                _activeDocument = context;
            }
            return context;
        }
        finally
        {
            if (gateEntered)
            {
                _gate.Release();
            }
            if (!ownershipResolved)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask<bool> ActivateAsync(DocumentId documentId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DocumentContext? context = _documents.Values.FirstOrDefault(context => context.Id == documentId);
            if (context is null)
            {
                return false;
            }

            _activeDocument = context;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<bool> CloseAsync(DocumentId documentId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string? key = _documents.FirstOrDefault(pair => pair.Value.Id == documentId).Key;
            if (key is null || !_documents.Remove(key, out DocumentContext? context))
            {
                return false;
            }

            if (ReferenceEquals(_activeDocument, context))
            {
                _activeDocument = _documents.Values.LastOrDefault();
            }

            await context.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            PendingOpen[] pending;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _disposed = true;
                pending = _runningOpens.ToArray();
            }
            finally
            {
                _gate.Release();
            }
            List<Exception>? failures = null;
            try
            {
                _lifetimeCancellation.Cancel();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
            foreach (PendingOpen open in pending)
            {
                try { await open.RunnerCompletion.Task.ConfigureAwait(false); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_openCleanupFailures.Count > 0)
                {
                    failures ??= [];
                    failures.AddRange(_openCleanupFailures);
                    _openCleanupFailures.Clear();
                }
                DocumentContext[] contexts = _documents.Values.ToArray();
                _documents.Clear();
                _activeDocument = null;
                foreach (DocumentContext context in contexts)
                {
                    try
                    {
                        await context.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        (failures ??= []).Add(exception);
                    }
                }
                if (failures is not null)
                {
                    throw new AggregateException("One or more document contexts failed to close.", failures);
                }
            }
            finally
            {
                _gate.Release();
            }
            _disposeCompletion.TrySetResult();
            _lifetimeCancellation.Dispose();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }

    public static string CanonicalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path.Trim());
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private sealed class PendingOpen : IDisposable
    {
        public PendingOpen(CancellationToken lifetimeCancellation)
        {
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation);
        }

        public CancellationTokenSource Cancellation { get; }
        public TaskCompletionSource<DocumentContext> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RunnerCompletion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WaiterCount { get; set; }

        public void Dispose() => Cancellation.Dispose();
    }
}
