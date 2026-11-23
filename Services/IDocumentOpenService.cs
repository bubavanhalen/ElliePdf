namespace ElliePdf.Services;

public interface IDocumentOpenService
{
    Task<PdfDocumentSession> OpenAsync(string path, CancellationToken cancellationToken = default);

    Task<PdfDocumentSession?> TryOpenWithoutPasswordAsync(
        string path,
        CancellationToken cancellationToken = default);
}
