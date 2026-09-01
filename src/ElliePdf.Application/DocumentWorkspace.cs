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

            DocumentId documentId = DocumentId.New();
            string displayName = Path.GetFileName(canonicalPath);
            DocumentOpenRequest request = new(documentId, canonicalPath, displayName);
            IPdfEngineSession? session = null;
            try
            {
                session = await _sessionFactory.OpenAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                DocumentContext context = new(request, session);
                _documents.Add(canonicalPath, context);
                session = null; // DocumentContext is now the sole lifetime owner.
                if (activate)
                {
                    _activeDocument = context;
                }
                return context;
            }
            finally
            {
                if (session is not null)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
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
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _disposed = true;
                DocumentContext[] contexts = _documents.Values.ToArray();
                _documents.Clear();
                _activeDocument = null;
                List<Exception>? failures = null;
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
}
