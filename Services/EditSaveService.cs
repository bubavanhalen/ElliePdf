using ElliePdf.Models;
using ElliePdf.Helpers;
using ElliePdf.Domain.Documents;
using ElliePdf.Infrastructure.Storage;

namespace ElliePdf.Services;

public interface IEditSaveService
{
    Task SaveTabAsync(DocumentTab tab, string outputPath, CancellationToken cancellationToken = default);

    Task SaveFlattenedCopyAsync(
        DocumentTab tab,
        string outputPath,
        CancellationToken cancellationToken = default);
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

        var commitsActiveDocument = string.Equals(
            Path.GetFullPath(tab.FilePath),
            Path.GetFullPath(outputPath),
            StringComparison.OrdinalIgnoreCase);
        var operation = commitsActiveDocument ? tab.BeginSave() : null;
        var capturedRevision = operation?.CapturedRevision ?? tab.State.ContentRevision;
        var overlays = _annotationStore.CaptureOverlayDocument(tab.Id) ?? new PageOverlayDocument();
        var hasOverlayAnnotations = overlays.Pages.Values.Any(OverlayCompositor.HasContent);

        try
        {
            if (hasOverlayAnnotations)
            {
                await _pdfService.SaveDocumentWithOverlaysAsync(
                    tab.Session,
                    overlays,
                    outputPath,
                    cancellationToken,
                    capturedRevision);
            }
            else
            {
                await _pdfService.SaveDocumentAsync(
                    tab.Session,
                    outputPath,
                    cancellationToken,
                    capturedRevision);
            }

            if (operation is not null)
            {
                await _annotationStore.CommitPersistedEditsAsync(
                    tab.Id,
                    overlays,
                    tab.State.ContentRevision,
                    CancellationToken.None);
                tab.CompleteSave(operation);
            }
        }
        catch (OperationCanceledException)
        {
            if (operation is not null)
            {
                tab.CancelSave(operation);
            }

            throw;
        }
        catch (AtomicSaveConflictException exception)
        {
            if (operation is not null)
            {
                tab.FailSave(operation, SaveFailureKind.ExternalChanged, exception.GetType().Name);
            }

            throw;
        }
        catch (AtomicCommitNotSupportedException exception)
        {
            if (operation is not null)
            {
                tab.FailSave(operation, SaveFailureKind.UnsupportedVolume, exception.GetType().Name);
            }

            throw;
        }
        catch (AtomicCommitOutcomeUnknownException exception)
        {
            if (operation is not null)
            {
                tab.FailSave(operation, SaveFailureKind.OutcomeUnknown, exception.GetType().Name);
            }

            throw;
        }
        catch (AtomicPostCommitValidationException exception)
        {
            if (operation is not null)
            {
                tab.FailSave(operation, SaveFailureKind.ValidationFailed, exception.GetType().Name);
            }

            throw;
        }
        catch (FileNotFoundException exception)
        {
            if (operation is not null)
            {
                tab.FailSave(operation, SaveFailureKind.Missing, exception.GetType().Name);
            }

            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            if (operation is not null)
            {
                tab.FailSave(operation, SaveFailureKind.ReadOnly, exception.GetType().Name);
            }

            throw;
        }
        catch (Exception exception)
        {
            if (operation is not null)
            {
                tab.FailSave(operation, exception.GetType().Name);
            }

            throw;
        }

        if (operation is not null && !tab.IsDirty)
        {
            try
            {
                await _annotationStore.StopAndDeleteRecoveryAsync(
                    tab.Id,
                    tab.FilePath,
                    CancellationToken.None);
                tab.MarkRecoveryArtifactDeleted();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"The PDF was saved, but recovery cleanup failed: {exception}");
            }
        }
        else if (operation is not null)
        {
            _annotationStore.ScheduleRecoveryCheckpoint(
                tab.Id,
                tab.FilePath,
                tab.State.ContentRevision,
                tab.Session.SourceVersion);
        }
    }

    public async Task SaveFlattenedCopyAsync(
        DocumentTab tab,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var overlays = _annotationStore.CaptureOverlayDocument(tab.Id);
        await _pdfService.SaveDocumentFlattenedCopyAsync(
            tab.Session,
            overlays,
            outputPath,
            cancellationToken,
            tab.State.ContentRevision);
    }
}
