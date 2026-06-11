namespace ElliePdf.Services;

public sealed class PdfPasswordPromptRequest
{
    public required string FilePath { get; init; }

    public bool IsRetry { get; init; }
}

public interface IPdfPasswordPrompt
{
    Task<string?> PromptAsync(PdfPasswordPromptRequest request, CancellationToken cancellationToken = default);
}
