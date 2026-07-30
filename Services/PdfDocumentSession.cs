namespace ElliePdf.Services;

public sealed class PdfDocumentSession : IAsyncDisposable
{
    private readonly IPdfService _pdfService;

    internal PdfDocumentSession(
        IPdfService pdfService,
        IntPtr handle,
        string sourcePath,
        int pageCount,
        PdfFormFillContext? formFill)
    {
        _pdfService = pdfService;
        Handle = handle;
        SourcePath = sourcePath;
        PageCount = pageCount;
        FormFill = formFill;
    }

    public string SourcePath { get; }

    public int PageCount { get; internal set; }

    internal IntPtr Handle { get; private set; }

    internal PdfFormFillContext? FormFill { get; private set; }

    public bool IsClosed => Handle == IntPtr.Zero;

    public ValueTask DisposeAsync()
    {
        if (IsClosed)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(_pdfService.CloseDocumentAsync(this));
    }

    internal void MarkClosed()
    {
        Handle = IntPtr.Zero;
    }

    internal void CloseFormFill()
    {
        FormFill?.Dispose();
        FormFill = null;
    }
}

public sealed class PdfiumDependencyException : InvalidOperationException
{
    public PdfiumDependencyException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
