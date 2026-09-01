using ElliePdf;
using ElliePdf.Application;
using ElliePdf.Models;
using ElliePdf.Pdf.Contracts;
using ElliePdf.Domain.Documents;
using WorkspaceDocumentOpenRequest = ElliePdf.Application.DocumentOpenRequest;

namespace ElliePdf.Services;

public sealed class DocumentTabService : IDocumentTabService, IAsyncDisposable
{
    private readonly DocumentWorkspace _workspace;
    private readonly WorkspacePdfEngineSessionFactory _workspaceSessionFactory;
    private readonly IRecentFilesService _recentFilesService;
    private readonly IAnnotationStore _annotationStore;
    private readonly IPdfService _pdfService;
    private readonly List<DocumentTab> _tabs = [];
    private Guid? _activeTabId;

    public DocumentTabService(
        DocumentWorkspace workspace,
        WorkspacePdfEngineSessionFactory workspaceSessionFactory,
        IRecentFilesService recentFilesService,
        IAnnotationStore annotationStore,
        IPdfService pdfService)
    {
        _workspace = workspace;
        _workspaceSessionFactory = workspaceSessionFactory;
        _recentFilesService = recentFilesService;
        _annotationStore = annotationStore;
        _pdfService = pdfService;
        _annotationStore.RecoveryCheckpointCompleted += OnRecoveryCheckpointCompleted;
    }

    public IReadOnlyList<DocumentTab> Tabs
    {
        get
        {
            lock (_tabs)
            {
                return _tabs.ToArray();
            }
        }
    }

    public Guid? ActiveTabId
    {
        get
        {
            lock (_tabs)
            {
                return _activeTabId;
            }
        }
    }

    public PdfDocumentSession? ActiveDocument => ActiveTab?.OpenSession;

    public string? ActiveFileName => ActiveTab?.DisplayName;

    public int CurrentPageIndex
    {
        get => ActiveTab?.CurrentPageIndex ?? 0;
        set
        {
            if (ActiveTab is null)
            {
                return;
            }

            var clamped = Math.Clamp(value, 0, Math.Max(0, (ActiveTab.OpenSession?.PageCount ?? 1) - 1));
            if (ActiveTab.CurrentPageIndex == clamped)
            {
                return;
            }

            ActiveTab.CurrentPageIndex = clamped;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public double ZoomScale
    {
        get => ActiveTab?.ZoomScale ?? 1.0;
        set
        {
            if (ActiveTab is null)
            {
                return;
            }

            var clamped = Math.Clamp(value, 0.1, 64.0);
            if (Math.Abs(ActiveTab.ZoomScale - clamped) < 0.001)
            {
                return;
            }

            ActiveTab.ZoomScale = clamped;
            ActiveTab.ZoomMode = PdfZoomMode.Custom;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public PdfZoomMode ZoomMode
    {
        get => ActiveTab?.ZoomMode ?? PdfZoomMode.FitWidth;
        set
        {
            if (ActiveTab is null || ActiveTab.ZoomMode == value)
            {
                return;
            }

            ActiveTab.ZoomMode = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? StateChanged;

    public event EventHandler? TabsChanged;

    public async Task<DocumentTab> OpenTabAsync(string path, bool activate = true, CancellationToken cancellationToken = default)
    {
        var context = await _workspace
            .OpenOrActivateAsync(path, activate, cancellationToken);
        DocumentTab? existing;
        lock (_tabs)
        {
            existing = _tabs.FirstOrDefault(tab => tab.Context?.Id == context.Id);
            if (existing is not null)
            {
                if (activate)
                {
                    _activeTabId = existing.Id;
                }
            }
        }
        if (existing is not null)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            return existing;
        }

        var session = _workspaceSessionFactory.GetRequiredSession(context.Id);
        var tab = new DocumentTab(session, context);
        var openedTabCommitted = false;
        try
        {
            if (await LoadAndReplayRecoveryAsync(tab, cancellationToken))
            {
                tab.MarkRecoveredContent();
            }
            await _recentFilesService.RecordOpenedAsync(path, cancellationToken);
            lock (_tabs)
            {
                _tabs.Add(tab);

                if (activate)
                {
                    _activeTabId = tab.Id;
                }
                openedTabCommitted = true;
            }
        }
        catch
        {
            if (!openedTabCommitted)
            {
                await _workspace.CloseAsync(context.Id, CancellationToken.None);
            }
            throw;
        }

        TabsChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return tab;
    }

    /// <summary>
    /// Pulls ElliePdf's annotations out of the document so they can be edited, and imports any
    /// companion file left behind by an older build before deleting it.
    /// </summary>
    private async Task LoadAnnotationsAsync(DocumentTab tab, CancellationToken cancellationToken)
    {
        var overlays = await _pdfService.ExtractOverlaysAsync(tab.Session, cancellationToken);
        var migrated = LegacyCompanionMigration.TryImport(tab.FilePath, overlays);

        _annotationStore.SetOverlayDocument(tab.Id, overlays);

        if (migrated)
        {
            // Imported annotations are not in the PDF yet, so the tab genuinely has unsaved work.
            _annotationStore.SetPageOverlay(
                tab.Id,
                overlays.Pages.Keys.First(),
                overlays.Pages.Values.First());
            tab.IsDirty = true;
        }
    }

    public async Task<DocumentTab> OpenOrActivateTabAsync(string path, CancellationToken cancellationToken = default)
    {
        var existing = FindTabByPath(path);
        if (existing is not null)
        {
            await ActivateTabAsync(existing.Id, cancellationToken);
            return FindTabByPath(path)
                ?? throw new InvalidOperationException("The activated tab is no longer available.");
        }

        return await OpenTabAsync(path, activate: true, cancellationToken);
    }

    public async Task<DocumentTab> RestoreTabAsync(
        SessionTabState state,
        bool activate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();

        var zoomMode = ParseZoomMode(state.ZoomMode);
        if (state.IsLockedPlaceholder)
        {
            return RestoreLockedPlaceholder(
                state.Path,
                state.PageIndex,
                state.Zoom,
                zoomMode,
                activate);
        }

        var canonicalPath = DocumentWorkspace.CanonicalizePath(state.Path);
        var request = new WorkspaceDocumentOpenRequest(
            DocumentId.New(),
            canonicalPath,
            Path.GetFileName(canonicalPath));
        var workspaceSession = await _workspaceSessionFactory
            .TryOpenWithoutPasswordAsync(request, cancellationToken);
        if (workspaceSession is null)
        {
            return RestoreLockedPlaceholder(
                state.Path,
                state.PageIndex,
                state.Zoom,
                zoomMode,
                activate);
        }

        var context = await _workspace
            .AttachOrActivateAsync(request, workspaceSession, activate, cancellationToken);
        DocumentTab? existingTab;
        lock (_tabs)
        {
            existingTab = _tabs.FirstOrDefault(candidate => candidate.Context?.Id == context.Id);
        }
        if (existingTab is not null)
        {
            existingTab.CurrentPageIndex = state.PageIndex;
            existingTab.ZoomScale = Math.Clamp(state.Zoom, 0.1, 64);
            existingTab.ZoomMode = zoomMode;
            if (activate)
            {
                lock (_tabs)
                {
                    _activeTabId = existingTab.Id;
                }
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
            return existingTab;
        }

        var restoredTabCommitted = false;
        try
        {
            var session = _workspaceSessionFactory.GetRequiredSession(context.Id);
            var tab = new DocumentTab(session, context)
            {
                CurrentPageIndex = Math.Clamp(state.PageIndex, 0, Math.Max(0, session.PageCount - 1)),
                ZoomScale = Math.Clamp(state.Zoom, 0.1, 64),
                ZoomMode = zoomMode
            };
            if (await LoadAndReplayRecoveryAsync(tab, cancellationToken))
            {
                tab.MarkRecoveredContent();
            }

            lock (_tabs)
            {
                _tabs.Add(tab);
                if (activate)
                {
                    _activeTabId = tab.Id;
                }
                restoredTabCommitted = true;
            }

            TabsChanged?.Invoke(this, EventArgs.Empty);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return tab;
        }
        catch
        {
            if (!restoredTabCommitted)
            {
                await _workspace.CloseAsync(context.Id, CancellationToken.None);
            }
            throw;
        }
    }

    public async Task ActivateTabAsync(Guid tabId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DocumentTab requestedTab;
        lock (_tabs)
        {
            requestedTab = _tabs.FirstOrDefault(tab => tab.Id == tabId)
                ?? throw new InvalidOperationException("The requested tab does not exist.");

            if (!requestedTab.IsLockedPlaceholder)
            {
                _activeTabId = tabId;
            }
        }

        if (requestedTab.Context is { } requestedContext)
        {
            await _workspace.ActivateAsync(requestedContext.Id, cancellationToken);
        }

        if (requestedTab.IsLockedPlaceholder)
        {
            var context = await _workspace
                .OpenOrActivateAsync(requestedTab.FilePath, activate: true, cancellationToken);
            var replacementCommitted = false;
            try
            {
                var session = _workspaceSessionFactory.GetRequiredSession(context.Id);
                var replacement = new DocumentTab(session, context)
                {
                    CurrentPageIndex = Math.Clamp(
                        requestedTab.CurrentPageIndex,
                        0,
                        Math.Max(0, session.PageCount - 1)),
                    ZoomScale = requestedTab.ZoomScale,
                    ZoomMode = requestedTab.ZoomMode
                };
                if (await LoadAndReplayRecoveryAsync(replacement, cancellationToken))
                {
                    replacement.MarkRecoveredContent();
                }

                lock (_tabs)
                {
                    var index = _tabs.FindIndex(tab => tab.Id == requestedTab.Id);
                    if (index >= 0)
                    {
                        _tabs[index] = replacement;
                        _activeTabId = replacement.Id;
                        replacementCommitted = true;
                    }
                }

                if (!replacementCommitted)
                {
                    throw new InvalidOperationException("The protected document tab was closed while it was being unlocked.");
                }

                await _recentFilesService.RecordOpenedAsync(replacement.FilePath, cancellationToken);
                TabsChanged?.Invoke(this, EventArgs.Empty);
            }
            catch
            {
                if (!replacementCommitted)
                {
                    await _workspace.CloseAsync(context.Id, CancellationToken.None);
                }
                throw;
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task CloseTabAsync(Guid tabId, CancellationToken cancellationToken = default)
    {
        DocumentTab? tab;
        lock (_tabs)
        {
            tab = _tabs.FirstOrDefault(item => item.Id == tabId);
            if (tab is null)
            {
                return;
            }

            _tabs.Remove(tab);
        }

        await _annotationStore.RemoveTabAsync(tab.Id, cancellationToken);
        if (tab.Context is { } context)
        {
            await _workspace.CloseAsync(context.Id, cancellationToken);
        }
        else if (tab.OpenSession is { } session)
        {
            await session.DisposeAsync();
        }

        lock (_tabs)
        {
            if (_activeTabId == tabId)
            {
                _activeTabId = _tabs.LastOrDefault()?.Id;
            }
        }

        TabsChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public DocumentTab? FindTabByPath(string path)
    {
        lock (_tabs)
        {
            return _tabs.FirstOrDefault(tab =>
                string.Equals(tab.FilePath, path, StringComparison.OrdinalIgnoreCase));
        }
    }

    public DocumentTab? ActiveTab
    {
        get
        {
            lock (_tabs)
            {
                return _activeTabId is null
                    ? null
                    : _tabs.FirstOrDefault(tab => tab.Id == _activeTabId);
            }
        }
    }

    public async Task LoadDocumentAsync(string path, CancellationToken cancellationToken = default)
    {
        await CloseActiveDocumentAsync(cancellationToken);
        await OpenTabAsync(path, activate: true, cancellationToken);
    }

    public async Task LoadDocumentSessionAsync(PdfDocumentSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(session.IsClosed, session);

        DocumentTab? existing;
        lock (_tabs)
        {
            existing = _tabs.FirstOrDefault(tab => ReferenceEquals(tab.OpenSession, session));
        }
        if (existing is not null)
        {
            await ActivateTabAsync(existing.Id, cancellationToken);
            return;
        }

        var canonicalPath = DocumentWorkspace.CanonicalizePath(session.SourcePath);
        var request = new WorkspaceDocumentOpenRequest(
            DocumentId.New(),
            canonicalPath,
            Path.GetFileName(canonicalPath));
        var workspaceSession = _workspaceSessionFactory.Adopt(request.DocumentId, session);
        var context = await _workspace
            .AttachOrActivateAsync(request, workspaceSession, activate: true, cancellationToken);
        lock (_tabs)
        {
            existing = _tabs.FirstOrDefault(tab => tab.Context?.Id == context.Id);
        }
        if (existing is not null)
        {
            await ActivateTabAsync(existing.Id, cancellationToken);
            return;
        }

        var loadedTabCommitted = false;
        try
        {
            var tab = new DocumentTab(_workspaceSessionFactory.GetRequiredSession(context.Id), context);
            if (await LoadAndReplayRecoveryAsync(tab, cancellationToken))
            {
                tab.MarkRecoveredContent();
            }
            lock (_tabs)
            {
                _tabs.Add(tab);
                _activeTabId = tab.Id;
                loadedTabCommitted = true;
            }
            TabsChanged?.Invoke(this, EventArgs.Empty);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            if (!loadedTabCommitted)
            {
                await _workspace.CloseAsync(context.Id, CancellationToken.None);
            }
            throw;
        }
    }

    public async Task CloseActiveDocumentAsync(CancellationToken cancellationToken = default)
    {
        Guid? activeTabId;
        lock (_tabs)
        {
            activeTabId = _activeTabId;
        }

        if (activeTabId is not null)
        {
            await CloseTabAsync(activeTabId.Value, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _annotationStore.RecoveryCheckpointCompleted -= OnRecoveryCheckpointCompleted;
        DocumentTab[] tabs;
        lock (_tabs)
        {
            tabs = _tabs.ToArray();
            _tabs.Clear();
            _activeTabId = null;
        }

        foreach (var tab in tabs)
        {
            await _annotationStore.RemoveTabAsync(tab.Id, CancellationToken.None);
            if (tab.Context is { } context)
            {
                await _workspace.CloseAsync(context.Id, CancellationToken.None);
            }
            else if (tab.OpenSession is { } session)
            {
                await session.DisposeAsync();
            }
        }
    }

    public DocumentTab RestoreLockedPlaceholder(
        string path,
        int pageIndex,
        double zoomScale,
        PdfZoomMode zoomMode,
        bool activate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        DocumentTab result;

        lock (_tabs)
        {
            var existing = _tabs.FirstOrDefault(tab =>
                string.Equals(tab.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (activate)
                {
                    _activeTabId = existing.Id;
                }
                result = existing;
            }
            else
            {
                var placeholder = DocumentTab.CreateLockedPlaceholder(fullPath);
                placeholder.CurrentPageIndex = Math.Max(0, pageIndex);
                placeholder.ZoomScale = Math.Clamp(zoomScale, 0.1, 64);
                placeholder.ZoomMode = zoomMode;
                _tabs.Add(placeholder);
                if (activate)
                {
                    _activeTabId = placeholder.Id;
                }
                result = placeholder;
            }
        }

        TabsChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private void OnRecoveryCheckpointCompleted(
        object? sender,
        RecoveryCheckpointCompletedEventArgs args)
    {
        DocumentTab? tab;
        lock (_tabs)
        {
            tab = _tabs.FirstOrDefault(candidate => candidate.Id == args.TabId);
        }

        tab?.MarkRecoveryCheckpointCompleted(args.Revision, args.Succeeded);
    }

    private async Task<bool> LoadAndReplayRecoveryAsync(
        DocumentTab tab,
        CancellationToken cancellationToken)
    {
        if (!await _annotationStore.LoadRecoveryAsync(tab.Id, tab.FilePath, cancellationToken))
        {
            return false;
        }

        var edits = _annotationStore.GetFormRecoveryEdits(tab.Id);
        var expectedRevision = ContentRevision.Initial;
        foreach (var pageGroup in edits.GroupBy(static edit => edit.PageIndex).OrderBy(static group => group.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((uint)pageGroup.Key >= (uint)tab.Session.PageCount)
            {
                continue;
            }

            var widgets = await _pdfService.GetFormWidgetsAsync(tab.Session, pageGroup.Key, cancellationToken);
            foreach (var edit in pageGroup)
            {
                var widget = widgets.Widgets.FirstOrDefault(candidate =>
                    string.Equals(candidate.FieldName, edit.FieldName, StringComparison.Ordinal)
                    && string.Equals(candidate.Type.ToString(), edit.WidgetType, StringComparison.Ordinal));
                if (widget is null || !widget.IsSupported || widget.IsReadOnly
                    || !TryRestoreValue(edit, out var value))
                {
                    continue;
                }

                try
                {
                    await _pdfService.ApplyFormValueAsync(
                        tab.Session,
                        new FormValueChange(
                            tab.Session.EngineSession.DocumentId,
                            widget.Id,
                            value,
                            expectedRevision),
                        cancellationToken);
                    expectedRevision = expectedRevision.Next();
                }
                catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
                {
                    // A field may have become read-only or unsupported in a newer PDFium
                    // preview. Keep opening the document and leave the recovery artifact
                    // available instead of turning a recoverable edit into an open failure.
                }
            }
        }

        return true;
    }

    private static bool TryRestoreValue(FormRecoveryEdit edit, out FormValue value)
    {
        value = FormValue.None();
        switch (edit.ValueKind)
        {
            case nameof(FormValueKind.Text) when edit.Text is not null:
                value = FormValue.TextValue(edit.Text);
                return true;
            case nameof(FormValueKind.Boolean) when edit.Boolean is not null:
                value = FormValue.BooleanValue(edit.Boolean.Value);
                return true;
            case nameof(FormValueKind.Choice) when edit.Text is not null:
                value = FormValue.Choice(edit.Text);
                return true;
            case nameof(FormValueKind.Choices):
                value = FormValue.MultipleChoices(edit.Choices);
                return true;
            default:
                return false;
        }
    }

    private static PdfZoomMode ParseZoomMode(string value) => value switch
    {
        "custom" => PdfZoomMode.Custom,
        "fitPage" => PdfZoomMode.FitPage,
        "actualSize" => PdfZoomMode.ActualSize,
        _ => PdfZoomMode.FitWidth
    };
}
