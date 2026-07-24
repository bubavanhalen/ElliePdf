using System.Text.Json;
using ElliePdf.Models;

namespace ElliePdf.Services;

public sealed class AnnotationStore : IAnnotationStore
{
    private readonly IUserSettingsService _settingsService;
    private readonly Dictionary<Guid, PageOverlayDocument> _documents = [];
    private readonly HashSet<Guid> _dirtyTabs = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _saveTimers = [];

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
        if (_saveTimers.Remove(tabId, out var timer))
        {
            timer.Cancel();
            timer.Dispose();
        }

        _documents.Remove(tabId);
        _dirtyTabs.Remove(tabId);
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

        if (_saveTimers.Remove(tabId, out var existing))
        {
            existing.Cancel();
            existing.Dispose();
        }

        var cts = new CancellationTokenSource();
        _saveTimers[tabId] = cts;

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
            finally
            {
                if (_saveTimers.TryGetValue(tabId, out var current) && ReferenceEquals(current, cts))
                {
                    _saveTimers.Remove(tabId);
                }

                cts.Dispose();
            }
        });
    }

    public async Task FlushPendingSavesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (tabId, timer) in _saveTimers.ToArray())
        {
            timer.Cancel();
            timer.Dispose();
            _saveTimers.Remove(tabId);
        }

        foreach (var tabId in _dirtyTabs.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
