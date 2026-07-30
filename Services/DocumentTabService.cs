using ElliePdf;

namespace ElliePdf.Services;

public sealed class DocumentTabService : IDocumentTabService, IAsyncDisposable
{
    private readonly IDocumentOpenService _documentOpenService;
    private readonly IRecentFilesService _recentFilesService;
    private readonly IAnnotationStore _annotationStore;
    private readonly List<DocumentTab> _tabs = [];
    private Guid? _activeTabId;

    public DocumentTabService(
        IDocumentOpenService documentOpenService,
        IRecentFilesService recentFilesService,
        IAnnotationStore annotationStore)
    {
        _documentOpenService = documentOpenService;
        _recentFilesService = recentFilesService;
        _annotationStore = annotationStore;
    }

    public IReadOnlyList<DocumentTab> Tabs => _tabs;

    public Guid? ActiveTabId => _activeTabId;

    public PdfDocumentSession? ActiveDocument => ActiveTab?.Session;

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

            var clamped = Math.Clamp(value, 0, Math.Max(0, ActiveTab.Session.PageCount - 1));
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

            var clamped = Math.Clamp(value, 0.25, 8.0);
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
        var session = await _documentOpenService.OpenAsync(path, cancellationToken);
        var tab = new DocumentTab(session);
        _tabs.Add(tab);

        if (activate)
        {
            _activeTabId = tab.Id;
        }

        TabsChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
        _ = RecordRecentFileAsync(path);
        return tab;
    }

    public async Task<DocumentTab> OpenOrActivateTabAsync(string path, CancellationToken cancellationToken = default)
    {
        var existing = FindTabByPath(path);
        if (existing is not null)
        {
            await ActivateTabAsync(existing.Id, cancellationToken);
            return existing;
        }

        return await OpenTabAsync(path, activate: true, cancellationToken);
    }

    public Task ActivateTabAsync(Guid tabId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_tabs.All(tab => tab.Id != tabId))
        {
            throw new InvalidOperationException("The requested tab does not exist.");
        }

        _activeTabId = tabId;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public async Task CloseTabAsync(Guid tabId, CancellationToken cancellationToken = default)
    {
        var tab = _tabs.FirstOrDefault(item => item.Id == tabId);
        if (tab is null)
        {
            return;
        }

        _tabs.Remove(tab);
        await tab.Session.DisposeAsync();

        if (_activeTabId == tabId)
        {
            _activeTabId = _tabs.LastOrDefault()?.Id;
        }

        TabsChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public DocumentTab? FindTabByPath(string path) =>
        _tabs.FirstOrDefault(tab => string.Equals(tab.FilePath, path, StringComparison.OrdinalIgnoreCase));

    public DocumentTab? ActiveTab =>
        _activeTabId is null ? null : _tabs.FirstOrDefault(tab => tab.Id == _activeTabId);

    public async Task LoadDocumentAsync(string path, CancellationToken cancellationToken = default)
    {
        await CloseActiveDocumentAsync(cancellationToken);
        await OpenTabAsync(path, activate: true, cancellationToken);
    }

    public async Task LoadDocumentSessionAsync(PdfDocumentSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ObjectDisposedException.ThrowIf(session.IsClosed, session);

        var existing = _tabs.FirstOrDefault(tab => ReferenceEquals(tab.Session, session));
        if (existing is not null)
        {
            await ActivateTabAsync(existing.Id, cancellationToken);
            return;
        }

        var tab = new DocumentTab(session);
        _tabs.Add(tab);
        _activeTabId = tab.Id;
        TabsChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
        _ = RecordRecentFileAsync(tab.FilePath);
    }

    public async Task CloseActiveDocumentAsync(CancellationToken cancellationToken = default)
    {
        if (_activeTabId is null)
        {
            return;
        }

        await CloseTabAsync(_activeTabId.Value, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var tab in _tabs.ToArray())
        {
            await tab.Session.DisposeAsync();
        }

        _tabs.Clear();
        _activeTabId = null;
    }

    private async Task RecordRecentFileAsync(string path)
    {
        try
        {
            await _recentFilesService.RecordOpenedAsync(path);
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not update recent files: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not update recent files: {ex.Message}");
        }
    }
}
