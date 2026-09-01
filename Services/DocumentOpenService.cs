namespace ElliePdf.Services;

public sealed class DocumentOpenService : IDocumentOpenService
{
    private readonly IPdfService _pdfService;
    private readonly IPdfPasswordPrompt _passwordPrompt;

    public DocumentOpenService(IPdfService pdfService, IPdfPasswordPrompt passwordPrompt)
    {
        _pdfService = pdfService;
        _passwordPrompt = passwordPrompt;
    }

    public async Task<PdfDocumentSession> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            return await _pdfService.OpenDocumentAsync(path, null, cancellationToken);
        }
        catch (PdfPasswordRequiredException)
        {
            return await PromptAndOpenAsync(path, isRetry: false, cancellationToken);
        }
    }

    public async Task<PdfDocumentSession?> TryOpenWithoutPasswordAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            return await _pdfService.OpenDocumentAsync(path, null, cancellationToken);
        }
        catch (PdfPasswordRequiredException)
        {
            return null;
        }
    }

    private async Task<PdfDocumentSession> PromptAndOpenAsync(
        string path,
        bool isRetry,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var password = await _passwordPrompt.PromptAsync(
                new PdfPasswordPromptRequest { FilePath = path, IsRetry = isRetry },
                cancellationToken);

            if (string.IsNullOrEmpty(password))
            {
                throw new OperationCanceledException("Password entry was cancelled.");
            }

            try
            {
                return await _pdfService.OpenDocumentAsync(path, password, cancellationToken);
            }
            catch (PdfIncorrectPasswordException)
            {
                isRetry = true;
            }
        }
    }
}
