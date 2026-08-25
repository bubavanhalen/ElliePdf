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
    private readonly IInPlaceSaveService _inPlaceSaveService;

    public EditSaveService(
        IPdfService pdfService,
        IAnnotationStore annotationStore,
        IInPlaceSaveService inPlaceSaveService)
    {
        _pdfService = pdfService;
        _annotationStore = annotationStore;
        _inPlaceSaveService = inPlaceSaveService;
    }

    public async Task SaveTabAsync(DocumentTab tab, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        PageOverlayDocument? overlays = _annotationStore.GetOverlayDocument(tab.Id);
        var isInPlace = string.Equals(
            Path.GetFullPath(outputPath),
            Path.GetFullPath(tab.FilePath),
            StringComparison.OrdinalIgnoreCase);

        if (!isInPlace)
        {
            // The original file keeps its pending overlays, so the tab stays dirty on purpose.
            await _pdfService.SaveDocumentWithOverlaysAsync(tab.Session, overlays, outputPath, cancellationToken);
            _annotationStore.DeleteCompanion(outputPath);
            return;
        }

        var result = await _inPlaceSaveService.SaveInPlaceAsync(tab.Session, overlays, cancellationToken);
        tab.Session = result.Session;

        if (!result.Saved)
        {
            throw new InvalidOperationException(
                $"Could not save '{Path.GetFileName(outputPath)}': {result.ErrorMessage}");
        }

        // The overlays are now part of the page content; keeping them would draw them twice.
        _annotationStore.ClearOverlays(tab.Id);
        _annotationStore.DeleteCompanion(outputPath);
        tab.IsDirty = false;
    }
}
