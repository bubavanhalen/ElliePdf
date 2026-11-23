using ElliePdf.Domain.Documents;
using ElliePdf.Rendering;

namespace ElliePdf.Services;

public sealed record PdfRect(float Left, float Top, float Right, float Bottom);

public sealed record RenderedPage(
    byte[] BgraPixels,
    int Width,
    int Height,
    float PageWidthPoints,
    float PageHeightPoints);

/// <summary>A viewport in page-local effective pixels (DIPs), before monitor rasterization.</summary>
public readonly record struct PageViewport(double X, double Y, double Width, double Height)
{
    public PageViewport Validate()
    {
        if (!double.IsFinite(X) || !double.IsFinite(Y) ||
            !double.IsFinite(Width) || !double.IsFinite(Height) ||
            X < 0 || Y < 0 || Width <= 0 || Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PageViewport));
        }

        return this;
    }
}

public sealed record RenderedPageTile(
    RenderKey Key,
    byte[] BgraPixels,
    int PixelWidth,
    int PixelHeight,
    int Stride,
    double Left,
    double Top,
    double Width,
    double Height,
    bool IsVisible);

public sealed record RenderedPageViewport(
    int PageIndex,
    int RasterWidth,
    int RasterHeight,
    double DisplayWidth,
    double DisplayHeight,
    RasterScale64 RasterScale,
    IReadOnlyList<RenderedPageTile> Tiles);

public sealed record PageRenderContext(
    double LogicalPixelsPerPoint,
    double RasterizationScale,
    PageViewport Viewport,
    RenderGeneration Generation,
    ScrollDirection Direction = ScrollDirection.None,
    bool InteractionCritical = false,
    RenderMode Mode = RenderMode.Normal);

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
