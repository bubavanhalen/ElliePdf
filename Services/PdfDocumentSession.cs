using ElliePdf.Domain.Storage;
using ElliePdf.Pdf.Contracts;

namespace ElliePdf.Services;

public sealed class PdfDocumentSession : IAsyncDisposable
{
    private readonly IPdfService _pdfService;
    private int _closed;

    internal PdfDocumentSession(
        IPdfService pdfService,
        IPdfEngineSession engineSession,
        string sourcePath,
        int pageCount,
        bool isEncrypted,
        FileVersionStamp sourceVersion,
        long openStartedTimestamp)
    {
        _pdfService = pdfService;
        EngineSession = engineSession;
        SourcePath = sourcePath;
        PageCount = pageCount;
        IsEncrypted = isEncrypted;
        SourceVersion = sourceVersion;
        OpenStartedTimestamp = openStartedTimestamp;
    }

    public string SourcePath { get; }

    public int PageCount { get; internal set; }

    public bool IsEncrypted { get; }

    public FileVersionStamp SourceVersion { get; private set; }

    /// <summary>
    /// Process-local monotonic timestamp captured before source validation and worker open. It is
    /// used only to emit aggregate open-to-first-presentation latency and is never persisted.
    /// </summary>
    internal long OpenStartedTimestamp { get; }

    internal IPdfEngineSession EngineSession { get; }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public ValueTask DisposeAsync() => IsClosed
        ? ValueTask.CompletedTask
        : new ValueTask(_pdfService.CloseDocumentAsync(this));

    internal async Task CloseEngineSessionAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        await EngineSession.DisposeAsync().ConfigureAwait(false);
    }

    internal void UpdateSourceVersion(FileVersionStamp sourceVersion)
    {
        ArgumentNullException.ThrowIfNull(sourceVersion);
        SourceVersion = sourceVersion;
    }
}

public sealed class PdfiumDependencyException : InvalidOperationException
{
    public PdfiumDependencyException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
