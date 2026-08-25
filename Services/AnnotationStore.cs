using System.Text.Json;
using ElliePdf.Models;

namespace ElliePdf.Services;

public sealed class AnnotationStore : IAnnotationStore
{
    private readonly IUserSettingsService _settingsService;
    private readonly Dictionary<Guid, PageOverlayDocument> _documents = [];
    private readonly HashSet<Guid> _dirtyTabs = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = [];
    private readonly Dictionary<Guid, string> _pendingSavePaths = [];
    private readonly Lock _timerLock = new();

    public AnnotationStore(IUserSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    private static string GetCompanionPath(string pdfPath) => pdfPath + ".ellie.json";

    public PageOverlayState GetPageOverlay(Guid tabId, int pageIndex)
    {
        if (!_documents.TryGetValue(tabId, out var document))
        {
            document = new PageOverlayDocument();
            _documents[tabId] = document;
        }

        if (!document.Pages.TryGetValue(pageIndex, out var overlay))
        {
            overlay = new PageOverlayState();
            document.Pages[pageIndex] = overlay;
        }

        return overlay;
    }

    public void SetPageOverlay(Guid tabId, int pageIndex, PageOverlayState state)
    {
        if (!_documents.TryGetValue(tabId, out var document))
        {
            document = new PageOverlayDocument();
            _documents[tabId] = document;
        }

        document.Pages[pageIndex] = state;
        _dirtyTabs.Add(tabId);
    }

    public bool IsTabDirty(Guid tabId) => _dirtyTabs.Contains(tabId);

    public void MarkTabClean(Guid tabId) => _dirtyTabs.Remove(tabId);

    public void RemoveTab(Guid tabId)
    {
        CancelPendingSave(tabId);
        _documents.Remove(tabId);
        _dirtyTabs.Remove(tabId);
    }

    public void ClearOverlays(Guid tabId)
    {
        CancelPendingSave(tabId);
        _documents[tabId] = new PageOverlayDocument();
        _dirtyTabs.Remove(tabId);
    }

    /// <summary>
    /// Cancels a debounced companion save. The token source is disposed by the background task
    /// that owns it, so cancelling here can never pull the token out from under it.
    /// </summary>
    private void CancelPendingSave(Guid tabId)
    {
        CancellationTokenSource? timer;
        lock (_timerLock)
        {
            _saveTimers.Remove(tabId, out timer);
            _pendingSavePaths.Remove(tabId);
        }

        timer?.Cancel();
    }

    public PageOverlayDocument? GetOverlayDocument(Guid tabId) =>
        _documents.TryGetValue(tabId, out var document) ? document : null;

    public void DeleteCompanion(string pdfPath)
    {
        var companionPath = GetCompanionPath(pdfPath);
        if (File.Exists(companionPath))
        {
            File.Delete(companionPath);
        }
    }

    public async Task LoadCompanionAsync(Guid tabId, string pdfPath, CancellationToken cancellationToken = default)
    {
        var companionPath = GetCompanionPath(pdfPath);
        if (!File.Exists(companionPath))
        {
            _documents[tabId] = new PageOverlayDocument();
            return;
        }

        await using var stream = File.OpenRead(companionPath);
        var document = await JsonSerializer.DeserializeAsync(
            stream,
            ElliePdfJsonContext.Default.PageOverlayDocument,
            cancellationToken)
            ?? new PageOverlayDocument();
        _documents[tabId] = document;
        _dirtyTabs.Remove(tabId);
    }

    public async Task SaveCompanionAsync(Guid tabId, string pdfPath, CancellationToken cancellationToken = default)
    {
        if (!_documents.TryGetValue(tabId, out var document))
        {
            return;
        }

        var companionPath = GetCompanionPath(pdfPath);
        await using var stream = File.Create(companionPath);
        await JsonSerializer.SerializeAsync(
            stream,
            document,
            ElliePdfJsonContext.Default.PageOverlayDocument,
            cancellationToken);
        MarkTabClean(tabId);
    }

    public void ScheduleCompanionSave(Guid tabId, string pdfPath)
    {
        if (!_settingsService.Settings.AutoSaveCompanion)
        {
            return;
        }

        CancelPendingSave(tabId);

        var cts = new CancellationTokenSource();
        lock (_timerLock)
        {
            _saveTimers[tabId] = cts;
            _pendingSavePaths[tabId] = pdfPath;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(800, cts.Token);
                await SaveCompanionAsync(tabId, pdfPath, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            finally
            {
                lock (_timerLock)
                {
                    if (_saveTimers.TryGetValue(tabId, out var current) && ReferenceEquals(current, cts))
                    {
                        _saveTimers.Remove(tabId);
                        _pendingSavePaths.Remove(tabId);
                    }
                }

                cts.Dispose();
            }
        });
    }

    public async Task FlushPendingSavesAsync(CancellationToken cancellationToken = default)
    {
        KeyValuePair<Guid, string>[] pending;
        CancellationTokenSource[] timers;

        lock (_timerLock)
        {
            pending = _pendingSavePaths.ToArray();
            timers = _saveTimers.Values.ToArray();
            _saveTimers.Clear();
            _pendingSavePaths.Clear();
        }

        foreach (var timer in timers)
        {
            timer.Cancel();
        }

        foreach (var (tabId, pdfPath) in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_dirtyTabs.Contains(tabId))
            {
                continue;
            }

            try
            {
                await SaveCompanionAsync(tabId, pdfPath, cancellationToken);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
