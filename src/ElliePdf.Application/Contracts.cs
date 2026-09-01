using System.Collections.Immutable;
using ElliePdf.Domain.Documents;

namespace ElliePdf.Application;

/// <summary>A transport-neutral size expressed in PDF points.</summary>
public readonly record struct PdfSize(double Width, double Height);

public readonly record struct PdfPoint(double X, double Y);

public readonly record struct PdfRectangle(double X, double Y, double Width, double Height);

public sealed record DocumentOpenRequest(DocumentId DocumentId, string CanonicalPath, string DisplayName);

public sealed record PdfMetadata(
    string? Title,
    string? Author,
    string? Subject,
    string? Creator,
    string PdfVersion,
    int PageCount,
    bool IsEncrypted,
    PdfPermissions Permissions);

public sealed record PageMetadata(PageId Id, int PageIndex, PdfSize SizeInPoints);

public sealed record OutlineNode(
    string Title,
    int? PageIndex,
    PdfPoint? Destination,
    ImmutableArray<OutlineNode> Children)
{
    public static OutlineNode Create(string title, int? pageIndex = null, PdfPoint? destination = null,
        IEnumerable<OutlineNode>? children = null) =>
        new(title, pageIndex, destination, children?.ToImmutableArray() ?? ImmutableArray<OutlineNode>.Empty);
}

public sealed record TextGeometry(string Text, PdfRectangle Bounds, double RotationDegrees = 0);

public sealed record PageTextResult(PageId PageId, ImmutableArray<TextGeometry> Runs)
{
    public static PageTextResult Empty(PageId pageId) => new(pageId, ImmutableArray<TextGeometry>.Empty);
}

public enum LinkKind
{
    Internal,
    External
}

public sealed record LinkTarget(LinkKind Kind, string Target, int? PageIndex = null);

public sealed record PageLink(PdfRectangle Bounds, LinkTarget Target);

public enum FormFieldKind
{
    Text,
    CheckBox,
    Radio,
    Combo,
    List,
    PushButton,
    Unsupported
}

public sealed record FormFieldDescriptor(string Name, FormFieldKind Kind, PdfRectangle Bounds, bool IsReadOnly);

public sealed record PdfPermissions(
    bool CanCopy = true,
    bool CanPrint = true,
    bool CanModify = true,
    bool CanFillForms = true);

public enum RenderQuality
{
    Preview,
    Sharp
}

public sealed record RenderRequest(
    PageId PageId,
    int PageIndex,
    double Scale,
    RenderQuality Quality = RenderQuality.Sharp);

public sealed record PageTextRequest(PageId PageId, int PageIndex);

public sealed record PageSearchRequest(PageId PageId, int PageIndex, string Query, bool MatchCase = false);

public sealed record SearchResult(PageId PageId, int PageIndex, string Snippet, ImmutableArray<PdfRectangle> Matches)
{
    public static SearchResult Create(PageId pageId, int pageIndex, string snippet,
        IEnumerable<PdfRectangle>? matches = null) =>
        new(pageId, pageIndex, snippet, matches?.ToImmutableArray() ?? ImmutableArray<PdfRectangle>.Empty);
}

public sealed record PrintRequest(ImmutableArray<int> PageIndices, bool FitToPage = true)
{
    public static PrintRequest ForPages(IEnumerable<int> pageIndices, bool fitToPage = true) =>
        new(pageIndices.ToImmutableArray(), fitToPage);
}

/// <summary>
/// Minimal seam for the future worker-backed PDF contract. Native handles and
/// implementation-specific transport details must not appear here.
/// </summary>
public interface IPdfEngineSession : IAsyncDisposable
{
    DocumentId DocumentId { get; }
}

public interface IPdfEngineSessionFactory
{
    ValueTask<IPdfEngineSession> OpenAsync(DocumentOpenRequest request, CancellationToken cancellationToken);
}

public sealed class DelegatePdfEngineSessionFactory(
    Func<DocumentOpenRequest, CancellationToken, ValueTask<IPdfEngineSession>> open) : IPdfEngineSessionFactory
{
    public ValueTask<IPdfEngineSession> OpenAsync(DocumentOpenRequest request, CancellationToken cancellationToken) =>
        open(request, cancellationToken);
}
