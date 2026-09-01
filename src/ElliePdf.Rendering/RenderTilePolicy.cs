using System.Collections.Immutable;
using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;

namespace ElliePdf.Rendering;

public enum RasterRenderStrategy
{
    FullPage,
    Tiled
}

public sealed record RasterRenderPlan(RasterRenderStrategy Strategy, int RasterWidth, int RasterHeight, ImmutableArray<TileAddress> Tiles);

public static class RenderTilePolicy
{
    public const int MaxFullPageDimension = 2048;
    public const int TileInteriorPixels = 512;
    public const int TileBleedPixels = 1;

    public static RasterRenderPlan CreatePlan(int rasterWidth, int rasterHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rasterWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rasterHeight);

        var fullPageBytes = checked((long)rasterWidth * rasterHeight * 4);
        if (rasterWidth <= MaxFullPageDimension &&
            rasterHeight <= MaxFullPageDimension &&
            fullPageBytes <= PdfContractLimits.MaxPixelBufferBytes)
        {
            return new RasterRenderPlan(
                RasterRenderStrategy.FullPage,
                rasterWidth,
                rasterHeight,
                [new TileAddress(0, 0, rasterWidth, rasterHeight, 0)]);
        }

        var builder = ImmutableArray.CreateBuilder<TileAddress>();
        for (var y = 0; y < rasterHeight; y += TileInteriorPixels)
        {
            for (var x = 0; x < rasterWidth; x += TileInteriorPixels)
            {
                var width = Math.Min(TileInteriorPixels, rasterWidth - x);
                var height = Math.Min(TileInteriorPixels, rasterHeight - y);
                builder.Add(new TileAddress(x, y, width, height, TileBleedPixels));
            }
        }

        return new RasterRenderPlan(RasterRenderStrategy.Tiled, rasterWidth, rasterHeight, builder.ToImmutable());
    }
}
