namespace ElliePdf.Services;

public interface IDocumentOpenService
{
    Task<PdfDocumentSession> OpenAsync(string path, CancellationToken cancellationToken = default);
}
