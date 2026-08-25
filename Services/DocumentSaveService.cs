using ElliePdf.Models;

namespace ElliePdf.Services;

/// <summary>
/// Outcome of a save. The session that was passed in is always closed; <see cref="Session"/> is the
/// replacement callers must switch to, or <c>null</c> when the document could not be reopened.
/// </summary>
public sealed record DocumentSaveResult(
    PdfDocumentSession? Session,
    bool Saved,
    string? ErrorMessage = null);

public interface IDocumentSaveService
{
    /// <summary>Raised after the document handle has been swapped so shared references can be repointed.</summary>
    event EventHandler<SessionReplacedEventArgs>? SessionReplaced;

    /// <summary>
    /// Writes <paramref name="session"/> (plus any overlays) to <paramref name="targetPath"/> and
    /// returns a freshly opened session for the document's original path.
    /// </summary>
    Task<DocumentSaveResult> SaveAsync(
        PdfDocumentSession session,
        PageOverlayDocument? overlays,
        string targetPath,
        CancellationToken cancellationToken = default);
}

public sealed class SessionReplacedEventArgs(PdfDocumentSession oldSession, PdfDocumentSession newSession) : EventArgs
{
    public PdfDocumentSession OldSession { get; } = oldSession;

    public PdfDocumentSession NewSession { get; } = newSession;
}

/// <summary>
/// Coordinates the close/write/reopen dance that every save needs.
/// </summary>
/// <remarks>
/// Two constraints drive this design. PDFium reads page content lazily from the file it opened, so
/// writing directly over that file would corrupt the data mid-save; and embedding overlays mutates
/// the live in-memory document, which must therefore be thrown away afterwards. So every save
/// stages to a sibling temp file, releases the handle, moves the staged file into place, and
/// reopens from the document's original path.
/// </remarks>
public sealed class DocumentSaveService : IDocumentSaveService
{
    private readonly IPdfService _pdfService;
    private readonly IDocumentOpenService _documentOpenService;

    public DocumentSaveService(IPdfService pdfService, IDocumentOpenService documentOpenService)
    {
        _pdfService = pdfService;
        _documentOpenService = documentOpenService;
    }

    public event EventHandler<SessionReplacedEventArgs>? SessionReplaced;

    public async Task<DocumentSaveResult> SaveAsync(
        PdfDocumentSession session,
        PageOverlayDocument? overlays,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        var sourcePath = session.SourcePath;
        var stagingPath = CreateStagingPath(targetPath);

        try
        {
            await _pdfService.SaveDocumentWithOverlaysAsync(session, overlays, stagingPath, cancellationToken);
        }
        catch (Exception ex)
        {
            TryDelete(stagingPath);

            // Embedding may have partially mutated the document, so it still has to be replaced.
            await session.DisposeAsync();
            return await ReplaceSessionAsync(session, sourcePath, false, Describe(ex));
        }

        // Release the file handle before touching the file on disk.
        await session.DisposeAsync();

        string? errorMessage = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
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

        return await ReplaceSessionAsync(session, sourcePath, errorMessage is null, errorMessage);
    }

    private async Task<DocumentSaveResult> ReplaceSessionAsync(
        PdfDocumentSession session,
        string sourcePath,
        bool saved,
        string? errorMessage)
    {
        PdfDocumentSession reopened;
        try
        {
            // The old handle is already gone, so reopening must not be cancellable: bailing out
            // here would strand every caller on a closed session.
            reopened = await _documentOpenService.OpenAsync(sourcePath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The bytes may well have landed; report that separately from the lost handle so the
            // caller can still retire the overlays it just embedded.
            return new DocumentSaveResult(
                null,
                saved,
                errorMessage ?? $"'{Path.GetFileName(sourcePath)}' could not be reopened: {Describe(ex)}");
        }

        SessionReplaced?.Invoke(this, new SessionReplacedEventArgs(session, reopened));
        return new DocumentSaveResult(reopened, saved, errorMessage);
    }

    private static string Describe(Exception ex) =>
        ex is OperationCanceledException ? "the operation was cancelled." : ex.Message;

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
