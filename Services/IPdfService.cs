namespace ElliePdf.Services;

public interface IPdfService
{
    bool HasConfiguredNativeDependency { get; }

    string? NativeDependencyIssue { get; }

    Task<PdfDocumentSession> OpenDocumentAsync(
        string path,
        string? password = null,
        CancellationToken cancellationToken = default);

    Task<RenderedPage> RenderPageAsync(
        PdfDocumentSession document,
        int pageIndex,
        double scale,
        CancellationToken cancellationToken = default);

    Task<byte[]> RenderPageThumbnailAsync(
        PdfDocumentSession document,
        int pageIndex,
        int maxWidth,
        int maxHeight,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TextMatch>> SearchTextAsync(
        PdfDocumentSession document,
        string query,
        bool matchCase,
        CancellationToken cancellationToken = default);

    Task<(float Width, float Height)> GetPageSizeAsync(
        PdfDocumentSession document,
        int pageIndex,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PdfOutlineItem>> GetOutlineAsync(
        PdfDocumentSession document,
        CancellationToken cancellationToken = default);

    Task RotatePageAsync(
        PdfDocumentSession document,
        int pageIndex,
        int quarterTurnsClockwise,
        CancellationToken cancellationToken = default);

    Task DeletePageAsync(PdfDocumentSession document, int pageIndex, CancellationToken cancellationToken = default);

    Task MergeDocumentsAsync(
        IReadOnlyList<PdfDocumentSession> sourceDocuments,
        string outputPath,
        CancellationToken cancellationToken = default);

    Task MergeOrderedPagesAsync(
        IReadOnlyList<(PdfDocumentSession Document, int PageIndex)> pagesInOrder,
        string outputPath,
        CancellationToken cancellationToken = default);

    Task SaveDocumentAsync(PdfDocumentSession document, string outputPath, CancellationToken cancellationToken = default);

    Task SaveDocumentWithOverlaysAsync(
        PdfDocumentSession document,
        Models.PageOverlayDocument? overlays,
        string outputPath,
        CancellationToken cancellationToken = default);

    Task CloseDocumentAsync(PdfDocumentSession document, CancellationToken cancellationToken = default);
}
