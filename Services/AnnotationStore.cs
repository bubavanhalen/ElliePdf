using ElliePdf.Models;

namespace ElliePdf.Services;

/// <summary>
/// Holds pending edits only for the lifetime of an open tab. Persistent edits are
/// written into the PDF by <see cref="IEditSaveService"/>; no sidecar files are used.
/// </summary>
public sealed class AnnotationStore : IAnnotationStore
{
    private readonly Dictionary<Guid, PageOverlayDocument> _documents = [];
    private readonly HashSet<Guid> _dirtyTabs = [];

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

    public void ClearDocument(Guid tabId)
    {
        _documents[tabId] = new PageOverlayDocument();
        _dirtyTabs.Remove(tabId);
    }

    public void RemoveTab(Guid tabId)
    {
        _documents.Remove(tabId);
        _dirtyTabs.Remove(tabId);
    }

    public PageOverlayDocument? GetOverlayDocument(Guid tabId) =>
        _documents.TryGetValue(tabId, out var document) ? document : null;

}
