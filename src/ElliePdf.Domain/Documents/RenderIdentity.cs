namespace ElliePdf.Domain.Documents;

public readonly record struct PdfSize(double Width, double Height);

public sealed record PageSnapshot(
    PageId Id,
    int PageIndex,
    PageContentRevision ContentRevision,
    PageAppearanceRevision AppearanceRevision,
    PdfSize SizeInPoints);

public readonly record struct RasterScale64(int Value)
{
    public static RasterScale64 FromPhysicalPixelsPerPoint(double value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
        return new RasterScale64(checked((int)Math.Ceiling(value * 64)));
    }

    public double PhysicalPixelsPerPoint => Value / 64d;
}

public readonly record struct TileAddress(
    int X,
    int Y,
    int InteriorWidth,
    int InteriorHeight,
    int BleedPixels)
{
    public TileAddress Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(X);
        ArgumentOutOfRangeException.ThrowIfNegative(Y);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(InteriorWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(InteriorHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(BleedPixels);
        return this;
    }
}

public enum PageRotation
{
    None,
    Clockwise90,
    Clockwise180,
    Clockwise270
}

public enum RenderMode
{
    Normal,
    HighContrast,
    Inverted
}

public sealed record RenderKey(
    DocumentId DocumentId,
    PageId PageId,
    PageContentRevision ContentRevision,
    PageAppearanceRevision AppearanceRevision,
    TileAddress Tile,
    RasterScale64 RasterScale,
    PageRotation Rotation,
    RenderMode Mode);
