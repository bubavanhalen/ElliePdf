using ElliePdf.Models;

namespace ElliePdf.Services;

/// <summary>
/// Holds the annotations being edited, per tab and page.
/// </summary>
/// <remarks>
/// Purely in-memory. Annotations are persisted by writing them into the PDF itself, so there is no
/// companion file: a saved document carries everything it needs and can be shared as-is.
/// </remarks>
public interface IAnnotationStore
{
    PageOverlayState GetPageOverlay(Guid tabId, int pageIndex);

    void SetPageOverlay(Guid tabId, int pageIndex, PageOverlayState state);

    /// <summary>Replaces everything held for a tab, used when a document is opened or reloaded.</summary>
    void SetOverlayDocument(Guid tabId, PageOverlayDocument document);

    bool IsTabDirty(Guid tabId);

    void MarkTabClean(Guid tabId);

    void RemoveTab(Guid tabId);

    /// <summary>Drops every overlay for a tab, e.g. once they have been written into the PDF.</summary>
    void ClearOverlays(Guid tabId);

    /// <summary>
    /// Drops a page's overlays and shifts later pages down, keeping the store aligned with a
    /// document whose page has been deleted.
    /// </summary>
    void RemovePage(Guid tabId, int pageIndex);

    PageOverlayDocument? GetOverlayDocument(Guid tabId);
}

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

    public void SetOverlayDocument(Guid tabId, PageOverlayDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        _documents[tabId] = document;

        // Loading what is already in the file is not an unsaved change.
        _dirtyTabs.Remove(tabId);
    }

    public bool IsTabDirty(Guid tabId) => _dirtyTabs.Contains(tabId);

    public void MarkTabClean(Guid tabId) => _dirtyTabs.Remove(tabId);

    public void RemoveTab(Guid tabId)
    {
        _documents.Remove(tabId);
        _dirtyTabs.Remove(tabId);
    }

    public void ClearOverlays(Guid tabId)
    {
        _documents[tabId] = new PageOverlayDocument();
        _dirtyTabs.Remove(tabId);
    }

    public void RemovePage(Guid tabId, int pageIndex)
    {
        if (!_documents.TryGetValue(tabId, out var document))
        {
            return;
        }

        var shifted = new Dictionary<int, PageOverlayState>();

        foreach (var (page, state) in document.Pages)
        {
            if (page == pageIndex)
            {
                continue;
            }

            shifted[page > pageIndex ? page - 1 : page] = state;
        }

        document.Pages = shifted;
    }

    public PageOverlayDocument? GetOverlayDocument(Guid tabId) =>
        _documents.TryGetValue(tabId, out var document) ? document : null;
}
