namespace ElliePdf.Services;

public interface IEditSaveService
{
    Task SaveTabAsync(DocumentTab tab, string outputPath, CancellationToken cancellationToken = default);
}

public sealed class EditSaveService : IEditSaveService
{
    private readonly IAnnotationStore _annotationStore;
    private readonly IDocumentSaveService _saveService;

    public EditSaveService(IAnnotationStore annotationStore, IDocumentSaveService saveService)
    {
        _annotationStore = annotationStore;
        _saveService = saveService;
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
        }

        if (result.Saved)
        {
            _annotationStore.DeleteCompanion(outputPath);

            if (isInPlace)
            {
                // The overlays are now part of the page content; keeping them would draw them twice.
                _annotationStore.ClearOverlays(tab.Id);
                tab.IsDirty = false;
            }
        }

        if (!result.Saved || result.Session is null)
        {
            throw new InvalidOperationException(
                $"Could not save '{Path.GetFileName(outputPath)}': {result.ErrorMessage}");
        }
    }
}
