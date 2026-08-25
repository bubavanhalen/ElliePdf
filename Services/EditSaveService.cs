namespace ElliePdf.Services;

public interface IEditSaveService
{
    Task SaveTabAsync(DocumentTab tab, string outputPath, CancellationToken cancellationToken = default);
}

public sealed class EditSaveService : IEditSaveService
{
    private readonly IAnnotationStore _annotationStore;
    private readonly IDocumentSaveService _saveService;
    private readonly IPdfService _pdfService;

    public EditSaveService(
        IAnnotationStore annotationStore,
        IDocumentSaveService saveService,
        IPdfService pdfService)
    {
        _annotationStore = annotationStore;
        _saveService = saveService;
        _pdfService = pdfService;
    }

    public async Task SaveTabAsync(DocumentTab tab, string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var overlays = _annotationStore.GetOverlayDocument(tab.Id);
        var isInPlace = string.Equals(
            Path.GetFullPath(outputPath),
            Path.GetFullPath(tab.FilePath),
            StringComparison.OrdinalIgnoreCase);

        var result = await _saveService.SaveAsync(tab.Session, overlays, outputPath, cancellationToken);

        if (result.Session is not null)
        {
            tab.Session = result.Session;

            // The reopened document carries the annotations as page annotations again. Detach them
            // so the overlay stays the single source of truth and nothing renders twice; the
            // in-memory overlays are already authoritative, so the extracted copy is discarded.
            await _pdfService.ExtractOverlaysAsync(result.Session, cancellationToken);
        }

        if (result.Saved)
        {
            // Older builds left a sidecar next to the file; it is dead weight now.
            LegacyCompanionMigration.Delete(outputPath);

            if (isInPlace)
            {
                tab.IsDirty = false;
                _annotationStore.MarkTabClean(tab.Id);
            }
        }

        if (!result.Saved || result.Session is null)
        {
            throw new InvalidOperationException(
                $"Could not save '{Path.GetFileName(outputPath)}': {result.ErrorMessage}");
        }
    }
}
