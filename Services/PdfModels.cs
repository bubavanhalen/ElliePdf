namespace ElliePdf.Services;

public sealed record PdfRect(float Left, float Top, float Right, float Bottom);

public sealed record RenderedPage(
    byte[] PngBytes,
    int Width,
    int Height,
    float PageWidthPoints,
    float PageHeightPoints);

public sealed record TextMatch(
    int PageIndex,
    int CharIndex,
    int MatchLength,
    string Context,
    IReadOnlyList<PdfRect> HighlightRects);

public sealed record PdfOutlineItem(
    string Title,
    int PageIndex,
    IReadOnlyList<PdfOutlineItem> Children);
