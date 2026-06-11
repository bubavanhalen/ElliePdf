using ElliePdf;

namespace ElliePdf.Services;

public interface IDocumentSessionService
{
    PdfDocumentSession? ActiveDocument { get; }

    string? ActiveFileName { get; }

    int CurrentPageIndex { get; set; }

    double ZoomScale { get; set; }

    PdfZoomMode ZoomMode { get; set; }

    event EventHandler? StateChanged;

    Task LoadDocumentAsync(string path, CancellationToken cancellationToken = default);

    Task LoadDocumentSessionAsync(PdfDocumentSession session, CancellationToken cancellationToken = default);

    Task CloseActiveDocumentAsync(CancellationToken cancellationToken = default);
}
