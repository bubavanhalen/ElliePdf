using System.Text.Json;
using ElliePdf.Models;

namespace ElliePdf.Services;

public sealed class AnnotationStore : IAnnotationStore
{
    private readonly Dictionary<Guid, PageOverlayDocument> _documents = [];
    private readonly HashSet<Guid> _dirtyTabs = [];

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
}
