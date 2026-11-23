using ElliePdf.Pdf.Contracts;
using ElliePdf.Domain.Documents;

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

    RenderGeneration AdvanceRenderGeneration(PdfDocumentSession document);

    Task<RenderedPageViewport> RenderPageViewportAsync(
        PdfDocumentSession document,
        int pageIndex,
        PageRenderContext context,
        CancellationToken cancellationToken = default);

    Task<PdfMetadata> GetMetadataAsync(
        PdfDocumentSession document,
        CancellationToken cancellationToken = default);

    Task<PageTextResult> GetPageTextAsync(
        PdfDocumentSession document,
        int pageIndex,
        CancellationToken cancellationToken = default);

    Task<PageLinks> GetPageLinksAsync(
        PdfDocumentSession document,
        int pageIndex,
        CancellationToken cancellationToken = default);

    Task<FormWidgetsResult> GetFormWidgetsAsync(
        PdfDocumentSession document,
        int pageIndex,
        CancellationToken cancellationToken = default);

    Task<PdfPermissions> GetPermissionsAsync(
        PdfDocumentSession document,
        CancellationToken cancellationToken = default);

    Task ApplyFormValueAsync(
        PdfDocumentSession document,
        FormValueChange change,
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

    /// <summary>Exports an immutable Organizer plan, including planned rotations.</summary>
    Task MergeOrderedPagesAsync(
        IReadOnlyList<PdfExportPage> pagesInOrder,
        string outputPath,
        CancellationToken cancellationToken = default,
        bool overwriteExisting = false);

    Task SaveDocumentAsync(
        PdfDocumentSession document,
        string outputPath,
        CancellationToken cancellationToken = default,
        Domain.Documents.ContentRevision? capturedRevision = null);

    Task SaveDocumentWithOverlaysAsync(
        PdfDocumentSession document,
        Models.PageOverlayDocument? overlays,
        string outputPath,
        CancellationToken cancellationToken = default,
        Domain.Documents.ContentRevision? capturedRevision = null);

    /// <summary>
    /// Creates an explicit, destructive-to-editability flattened copy without
    /// mutating the open source document.
    /// </summary>
    Task SaveDocumentFlattenedCopyAsync(
        PdfDocumentSession document,
        Models.PageOverlayDocument? overlays,
        string outputPath,
        CancellationToken cancellationToken = default,
        Domain.Documents.ContentRevision? capturedRevision = null);

    Task CloseDocumentAsync(PdfDocumentSession document, CancellationToken cancellationToken = default);
}

public sealed record PdfExportPage(
    PdfDocumentSession Document,
    int PageIndex,
    ElliePdf.Domain.Documents.PageId PageId,
    ElliePdf.Domain.Documents.ContentRevision ExpectedContentRevision,
    ElliePdf.Domain.Documents.StructureRevision ExpectedStructureRevision,
    ElliePdf.Domain.Documents.PageContentRevision ExpectedPageContentRevision,
    ElliePdf.Domain.Documents.PageRotation? Rotation = null);
