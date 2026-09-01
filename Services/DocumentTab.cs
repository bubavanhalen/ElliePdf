using ElliePdf;
using ElliePdf.Application;
using ElliePdf.Domain.Documents;

namespace ElliePdf.Services;

public sealed class DocumentTab
{
    private readonly Lock _stateLock = new();
    private readonly PdfDocumentSession? _session;
    private DocumentState _state;
    private int _currentPageIndex;

    public DocumentTab(PdfDocumentSession session, DocumentContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        Context = context;
        FilePath = session.SourcePath;
        _state = DocumentState.Create(context?.Id ?? new DocumentId(Id));
        context?.SetPageCount(session.PageCount);
    }

    private DocumentTab(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        FilePath = Path.GetFullPath(sourcePath);
        IsLockedPlaceholder = true;
        _state = DocumentState.Create(new DocumentId(Id));
    }

    public static DocumentTab CreateLockedPlaceholder(string sourcePath) => new(sourcePath);

    public Guid Id { get; } = Guid.NewGuid();

    public PdfDocumentSession Session => _session
        ?? throw new InvalidOperationException("The protected document must be unlocked before it can be used.");

    public PdfDocumentSession? OpenSession => _session;

    public DocumentContext? Context { get; }

    public bool IsLockedPlaceholder { get; }

    public string FilePath { get; }

    public string DisplayName => Path.GetFileName(FilePath);

    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, (_session?.PageCount ?? 1) - 1));
            _currentPageIndex = clamped;
            if (Context is { Snapshot.PageCount: > 0 })
            {
                Context.SetPage(clamped);
            }
        }
    }

    public double ZoomScale { get; set; } = 1.0;

    public PdfZoomMode ZoomMode { get; set; } = PdfZoomMode.FitWidth;

    public DocumentState State
    {
        get
        {
            if (Context is not null)
            {
                return Context.State;
            }
            lock (_stateLock)
            {
                return _state;
            }
        }
    }

    public bool IsDirty => State.HasUnsavedChanges;

    public void MarkContentChanged()
    {
        if (Context is not null)
        {
            Context.MarkContentChanged();
            return;
        }
        lock (_stateLock)
        {
            _state = DocumentStateReducer.ApplyContentMutation(_state);
        }
    }

    public void MarkRecoveredContent()
    {
        if (Context is not null)
        {
            Context.MarkRecoveredContent();
            return;
        }
        lock (_stateLock)
        {
            _state = DocumentStateReducer.ApplyContentMutation(_state);
            _state = DocumentStateReducer.MarkRecoveryCheckpointed(_state, _state.ContentRevision);
        }
    }

    public SaveOperation BeginSave()
    {
        if (Context is not null)
        {
            return Context.BeginSave();
        }
        lock (_stateLock)
        {
            var transition = DocumentStateReducer.BeginSave(_state);
            _state = transition.State;
            return transition.Operation;
        }
    }

    public void CompleteSave(SaveOperation operation)
    {
        if (Context is not null)
        {
            Context.CompleteSave(operation);
            return;
        }
        lock (_stateLock)
        {
            _state = DocumentStateReducer.CompleteSave(_state, operation);
        }
    }

    public void FailSave(SaveOperation operation, string errorCode)
        => FailSave(operation, SaveFailureKind.IoFailure, errorCode);

    public void FailSave(
        SaveOperation operation,
        SaveFailureKind failureKind,
        string errorCode)
    {
        if (Context is not null)
        {
            Context.FailSave(operation, failureKind, errorCode);
            return;
        }
        lock (_stateLock)
        {
            _state = DocumentStateReducer.FailSave(_state, operation, failureKind, errorCode);
        }
    }

    public void CancelSave(SaveOperation operation)
    {
        if (Context is not null)
        {
            Context.CancelSave(operation);
            return;
        }
        lock (_stateLock)
        {
            _state = DocumentStateReducer.CancelSave(_state, operation);
        }
    }

    public void MarkRecoveryArtifactDeleted()
    {
        if (Context is not null)
        {
            Context.MarkRecoveryArtifactDeleted();
            return;
        }
        lock (_stateLock)
        {
            _state = DocumentStateReducer.DiscardRecovery(_state);
        }
    }

    public void MarkRecoveryCheckpointCompleted(ContentRevision revision, bool succeeded)
    {
        if (Context is not null)
        {
            Context.MarkRecoveryCheckpointCompleted(revision, succeeded);
            return;
        }
        lock (_stateLock)
        {
            _state = succeeded
                ? DocumentStateReducer.MarkRecoveryCheckpointed(_state, revision)
                : DocumentStateReducer.MarkRecoveryFailed(_state);
        }
    }
}
