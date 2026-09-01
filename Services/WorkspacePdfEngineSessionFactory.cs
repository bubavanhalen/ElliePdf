using ElliePdf.Application;
using ElliePdf.Domain.Documents;
using ApplicationEngineSession = ElliePdf.Application.IPdfEngineSession;

namespace ElliePdf.Services;

/// <summary>
/// Bridges the password-aware WinUI open workflow into the instance-scoped
/// application workspace while keeping the application project transport neutral.
/// </summary>
public sealed class WorkspacePdfEngineSessionFactory : IPdfEngineSessionFactory
{
    private readonly IDocumentOpenService _documentOpenService;
    private readonly Dictionary<DocumentId, PdfDocumentSession> _sessions = [];
    private readonly Lock _gate = new();

    public WorkspacePdfEngineSessionFactory(IDocumentOpenService documentOpenService)
    {
        _documentOpenService = documentOpenService;
    }

    public async ValueTask<ApplicationEngineSession> OpenAsync(
        DocumentOpenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = await _documentOpenService
            .OpenAsync(request.CanonicalPath, cancellationToken)
            .ConfigureAwait(false);
        return Adopt(request.DocumentId, session);
    }

    public async ValueTask<ApplicationEngineSession?> TryOpenWithoutPasswordAsync(
        DocumentOpenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = await _documentOpenService
            .TryOpenWithoutPasswordAsync(request.CanonicalPath, cancellationToken)
            .ConfigureAwait(false);
        return session is null ? null : Adopt(request.DocumentId, session);
    }

    public ApplicationEngineSession Adopt(DocumentId documentId, PdfDocumentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(session.IsClosed, session);
        lock (_gate)
        {
            if (!_sessions.TryAdd(documentId, session))
            {
                throw new InvalidOperationException("The workspace document identity is already in use.");
            }
        }

        return new WorkspaceEngineSession(documentId, session, ReleaseAsync);
    }

    public PdfDocumentSession GetRequiredSession(DocumentId documentId)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(documentId, out var session)
                ? session
                : throw new InvalidOperationException("The workspace session is no longer available.");
        }
    }

    private async ValueTask ReleaseAsync(DocumentId documentId, PdfDocumentSession session)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(documentId, out var registered)
                && ReferenceEquals(registered, session))
            {
                _sessions.Remove(documentId);
            }
        }

        await session.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class WorkspaceEngineSession(
        DocumentId documentId,
        PdfDocumentSession session,
        Func<DocumentId, PdfDocumentSession, ValueTask> release) : ApplicationEngineSession
    {
        private int _disposed;

        public DocumentId DocumentId { get; } = documentId;

        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0
            ? release(DocumentId, session)
            : ValueTask.CompletedTask;
    }
}
