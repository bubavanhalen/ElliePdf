using ElliePdf.Models;

namespace ElliePdf.Services;

public interface IAnnotationStore
{
    PageOverlayState GetPageOverlay(Guid tabId, int pageIndex);

    void SetPageOverlay(Guid tabId, int pageIndex, PageOverlayState state);

    bool IsTabDirty(Guid tabId);

    void MarkTabClean(Guid tabId);

    void ClearDocument(Guid tabId);

    void RemoveTab(Guid tabId);

    PageOverlayDocument? GetOverlayDocument(Guid tabId);
}
