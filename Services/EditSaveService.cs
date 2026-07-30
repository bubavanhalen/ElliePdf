using ElliePdf.Models;

namespace ElliePdf.Services;

public interface IEditSaveService
{
    Task SaveTabAsync(DocumentTab tab, string outputPath, CancellationToken cancellationToken = default);

    Task DigitallySignTabAsync(
        DocumentTab tab,
        string outputPath,
        DigitalSignatureRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class EditSaveService : IEditSaveService
{
    private readonly IPdfService _pdfService;
    private readonly IAnnotationStore _annotationStore;
    private readonly IDocumentOpenService _documentOpenService;
    private readonly IDigitalSignatureService _digitalSignatureService;

    public EditSaveService(
        IPdfService pdfService,
        IAnnotationStore annotationStore,
        IDocumentOpenService documentOpenService,
        IDigitalSignatureService digitalSignatureService)
    {
        _pdfService = pdfService;
        _annotationStore = annotationStore;
        _documentOpenService = documentOpenService;
        _digitalSignatureService = digitalSignatureService;
    }

    public async Task SaveTabAsync(DocumentTab tab, string outputPath, CancellationToken cancellationToken = default)
    {
        PageOverlayDocument? overlays = _annotationStore.GetOverlayDocument(tab.Id);
        if (IsSamePath(tab.FilePath, outputPath))
        {
            var stagedPath = CreateOperationPath(outputPath, "save");
            try
            {
                await _pdfService.SaveDocumentWithOverlaysAsync(
                    tab.Session,
                    overlays,
                    stagedPath,
                    cancellationToken);
                await ReplaceInPlaceAsync(tab, stagedPath, outputPath, cancellationToken);
            }
            finally
            {
                TryDelete(stagedPath);
            }

            return;
        }

        await _pdfService.SaveDocumentWithOverlaysAsync(tab.Session, overlays, outputPath, cancellationToken);
    }

    public async Task DigitallySignTabAsync(
        DocumentTab tab,
        string outputPath,
        DigitalSignatureRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(request);

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath))
            ?? throw new InvalidOperationException("The output path does not have a directory.");
        Directory.CreateDirectory(outputDirectory);
        var operationId = Guid.NewGuid().ToString("N");
        var unsignedPath = Path.Combine(outputDirectory, $".ellie-sign-{operationId}.unsigned.pdf");
        var signedPath = Path.Combine(outputDirectory, $".ellie-sign-{operationId}.signed.pdf");

        try
        {
            var overlays = _annotationStore.GetOverlayDocument(tab.Id);
            await _pdfService.SaveDocumentWithOverlaysAsync(
                tab.Session,
                overlays,
                unsignedPath,
                cancellationToken);
            await _digitalSignatureService.SignAsync(
                unsignedPath,
                signedPath,
                request,
                cancellationToken);

            if (IsSamePath(tab.FilePath, outputPath))
            {
                await ReplaceInPlaceAsync(tab, signedPath, outputPath, cancellationToken);
            }
            else
            {
                File.Move(signedPath, outputPath, overwrite: true);
            }
        }
        finally
        {
            TryDelete(unsignedPath);
            TryDelete(signedPath);
        }
    }

    private async Task ReplaceInPlaceAsync(
        DocumentTab tab,
        string stagedPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var backupPath = CreateOperationPath(outputPath, "backup");
        var previous = tab.Session;
        await previous.DisposeAsync();

        try
        {
            File.Replace(stagedPath, outputPath, backupPath, ignoreMetadataErrors: true);
            var reloaded = await _documentOpenService.OpenAsync(outputPath, cancellationToken);
            _ = tab.ReplaceSession(reloaded);
            _annotationStore.ClearDocument(tab.Id);
            tab.IsDirty = false;
            TryDelete(backupPath);
        }
        catch
        {
            if (File.Exists(backupPath))
            {
                try
                {
                    File.Replace(backupPath, outputPath, null, ignoreMetadataErrors: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            if (tab.Session.IsClosed && File.Exists(outputPath))
            {
                try
                {
                    var recovered = await _documentOpenService.OpenAsync(outputPath, CancellationToken.None);
                    _ = tab.ReplaceSession(recovered);
                }
                catch
                {
                }
            }

            throw;
        }
        finally
        {
            TryDelete(backupPath);
        }
    }

    private static bool IsSamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static string CreateOperationPath(string outputPath, string role)
    {
        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath))
            ?? throw new InvalidOperationException("The output path does not have a directory.");
        return Path.Combine(
            outputDirectory,
            $".ellie-{role}-{Guid.NewGuid():N}.pdf");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
