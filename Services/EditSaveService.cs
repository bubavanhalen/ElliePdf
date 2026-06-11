using ElliePdf.Models;

namespace ElliePdf.Services;

public interface IEditSaveService
{
    Task SaveTabAsync(DocumentTab tab, string outputPath, CancellationToken cancellationToken = default);
}

public sealed class EditSaveService : IEditSaveService
{
    private readonly IPdfService _pdfService;
    private readonly IAnnotationStore _annotationStore;

    public EditSaveService(IPdfService pdfService, IAnnotationStore annotationStore)
    {
        _pdfService = pdfService;
        _annotationStore = annotationStore;
    }

    public async Task SaveTabAsync(DocumentTab tab, string outputPath, CancellationToken cancellationToken = default)
    {
        PageOverlayDocument? overlays = _annotationStore.GetOverlayDocument(tab.Id);
        await _pdfService.SaveDocumentWithOverlaysAsync(tab.Session, overlays, outputPath, cancellationToken);
        _annotationStore.DeleteCompanion(outputPath);
        _annotationStore.MarkTabClean(tab.Id);
        tab.IsDirty = false;
    }
}
