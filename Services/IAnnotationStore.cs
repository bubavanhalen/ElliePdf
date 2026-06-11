using ElliePdf.Models;

namespace ElliePdf.Services;

public interface IAnnotationStore
{
    PageOverlayState GetPageOverlay(Guid tabId, int pageIndex);

    void SetPageOverlay(Guid tabId, int pageIndex, PageOverlayState state);

    bool IsTabDirty(Guid tabId);

    void MarkTabClean(Guid tabId);

    void RemoveTab(Guid tabId);

    Task LoadCompanionAsync(Guid tabId, string pdfPath, CancellationToken cancellationToken = default);

    Task SaveCompanionAsync(Guid tabId, string pdfPath, CancellationToken cancellationToken = default);

    PageOverlayDocument? GetOverlayDocument(Guid tabId);

    void DeleteCompanion(string pdfPath);
}
