using ElliePdf.Models;

namespace ElliePdf.Services;

/// <summary>
/// Outcome of an in-place save. <see cref="Session"/> is the session callers should use from now
/// on; it is only closed when <see cref="Saved"/> is <c>false</c> and reopening also failed.
/// </summary>
public sealed record InPlaceSaveResult(PdfDocumentSession Session, bool Saved, string? ErrorMessage = null);

public interface IInPlaceSaveService
{
    /// <summary>Raised after the document handle has been swapped, so shared references can be repointed.</summary>
    event EventHandler<SessionReplacedEventArgs>? SessionReplaced;

    Task<InPlaceSaveResult> SaveInPlaceAsync(
        PdfDocumentSession session,
        PageOverlayDocument? overlays,
        CancellationToken cancellationToken = default);
}

public sealed class SessionReplacedEventArgs(PdfDocumentSession oldSession, PdfDocumentSession newSession) : EventArgs
{
    public PdfDocumentSession OldSession { get; } = oldSession;

    public PdfDocumentSession NewSession { get; } = newSession;
}

/// <summary>
/// PDFium reads page content lazily from the file it opened, so writing straight back over that
/// file truncates the source mid-save. Every in-place save therefore stages to a sibling temp
/// file, releases the document handle, swaps the files and reopens the document.
/// </summary>
public sealed class InPlaceSaveService : IInPlaceSaveService
{
    private readonly IPdfService _pdfService;
    private readonly IDocumentOpenService _documentOpenService;

    public InPlaceSaveService(IPdfService pdfService, IDocumentOpenService documentOpenService)
    {
        _pdfService = pdfService;
        _documentOpenService = documentOpenService;
    }

    public event EventHandler<SessionReplacedEventArgs>? SessionReplaced;

    public async Task<InPlaceSaveResult> SaveInPlaceAsync(
        PdfDocumentSession session,
        PageOverlayDocument? overlays,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var targetPath = session.SourcePath;
        var stagingPath = CreateStagingPath(targetPath);

        try
        {
            await _pdfService.SaveDocumentWithOverlaysAsync(session, overlays, stagingPath, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryDelete(stagingPath);
            return new InPlaceSaveResult(session, false, ex.Message);
        }

        await session.DisposeAsync();

        string? errorMessage = null;
        try
        {
            File.Move(stagingPath, targetPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errorMessage = ex.Message;
        }
        finally
        {
            TryDelete(stagingPath);
        }

        // The old handle is already gone, so reopening must not be cancellable: bailing out here
        // would strand every caller on a closed session.
        PdfDocumentSession reopened;
        try
        {
            reopened = await _documentOpenService.OpenAsync(targetPath, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            return new InPlaceSaveResult(session, false, $"Saved file could not be reopened: {ex.Message}");
        }

        SessionReplaced?.Invoke(this, new SessionReplacedEventArgs(session, reopened));
        return new InPlaceSaveResult(reopened, errorMessage is null, errorMessage);
    }

    private static string CreateStagingPath(string targetPath)
    {
        var fullPath = Path.GetFullPath(targetPath);
        var directory = Path.GetDirectoryName(fullPath);
        return Path.Combine(
            string.IsNullOrEmpty(directory) ? Path.GetTempPath() : directory,
            $".{Path.GetFileNameWithoutExtension(fullPath)}.{Guid.NewGuid():N}.tmp.pdf");
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
