using System.Collections.Immutable;
using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;

namespace ElliePdf.Rendering;

/// <summary>A checked, physical-pixel viewport within a page raster.</summary>
public readonly record struct RasterViewport(int X, int Y, int Width, int Height)
{
    public RasterViewport ClampTo(int rasterWidth, int rasterHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rasterWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rasterHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(Width);
        ArgumentOutOfRangeException.ThrowIfNegative(Height);

        var left = Math.Clamp(X, 0, rasterWidth);
        var top = Math.Clamp(Y, 0, rasterHeight);
        var right = Math.Clamp(checked((long)X + Width), 0, rasterWidth);
        var bottom = Math.Clamp(checked((long)Y + Height), 0, rasterHeight);
        return new RasterViewport(
            left,
            top,
            checked((int)Math.Max(0, right - left)),
            checked((int)Math.Max(0, bottom - top)));
    }
}

public sealed record PlannedViewportTile(TileAddress Address, bool IsVisible, int OverscanDistance);

/// <summary>
/// Produces only the 512-pixel tiles intersecting a viewport and one directional overscan band.
/// It never materializes a page-sized buffer or a collection proportional to total page area.
/// </summary>
public static class ViewportTilePlanner
{
    public static ImmutableArray<PlannedViewportTile> Plan(
        int rasterWidth,
        int rasterHeight,
        RasterViewport viewport,
        ScrollDirection direction = ScrollDirection.None,
        int overscanBands = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rasterWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rasterHeight);
        ArgumentOutOfRangeException.ThrowIfNegative(overscanBands);

        var visible = viewport.ClampTo(rasterWidth, rasterHeight);
        if (visible.Width == 0 || visible.Height == 0)
        {
            return [];
        }

        var overscanPixels = checked(overscanBands * RenderTilePolicy.TileInteriorPixels);
        var expandedTop = direction == ScrollDirection.Forward
            ? visible.Y
            : Math.Max(0, visible.Y - overscanPixels);
        var visibleBottom = checked(visible.Y + visible.Height);
        var expandedBottom = direction == ScrollDirection.Backward
            ? visibleBottom
            : checked((int)Math.Min(rasterHeight, (long)visibleBottom + overscanPixels));

        // No velocity yet: keep a smaller band on both sides so random jumps get a nearby placeholder.
        if (direction == ScrollDirection.None)
        {
            expandedTop = Math.Max(0, visible.Y - overscanPixels);
            expandedBottom = checked((int)Math.Min(rasterHeight, (long)visibleBottom + overscanPixels));
        }

        var expandedLeft = Math.Max(0, visible.X - overscanPixels);
        var expandedRight = checked((int)Math.Min(
            rasterWidth,
            (long)visible.X + visible.Width + overscanPixels));
        var firstColumn = expandedLeft / RenderTilePolicy.TileInteriorPixels;
        var lastColumn = checked((expandedRight - 1) / RenderTilePolicy.TileInteriorPixels);
        var visibleFirstColumn = visible.X / RenderTilePolicy.TileInteriorPixels;
        var visibleLastColumn = checked((visible.X + visible.Width - 1) / RenderTilePolicy.TileInteriorPixels);
        var firstRow = expandedTop / RenderTilePolicy.TileInteriorPixels;
        var lastRow = checked((expandedBottom - 1) / RenderTilePolicy.TileInteriorPixels);
        var visibleFirstRow = visible.Y / RenderTilePolicy.TileInteriorPixels;
        var visibleLastRow = checked((visible.Y + visible.Height - 1) / RenderTilePolicy.TileInteriorPixels);

        var builder = ImmutableArray.CreateBuilder<PlannedViewportTile>(
            checked((lastColumn - firstColumn + 1) * (lastRow - firstRow + 1)));
        for (var row = firstRow; row <= lastRow; row++)
        {
            var y = checked(row * RenderTilePolicy.TileInteriorPixels);
            var height = Math.Min(RenderTilePolicy.TileInteriorPixels, rasterHeight - y);
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var x = checked(column * RenderTilePolicy.TileInteriorPixels);
                var width = Math.Min(RenderTilePolicy.TileInteriorPixels, rasterWidth - x);
                var isVisible = row >= visibleFirstRow && row <= visibleLastRow
                    && column >= visibleFirstColumn && column <= visibleLastColumn;
                var rowDistance = row < visibleFirstRow
                    ? visibleFirstRow - row
                    : row > visibleLastRow
                        ? row - visibleLastRow
                        : 0;
                var columnDistance = column < visibleFirstColumn
                    ? visibleFirstColumn - column
                    : column > visibleLastColumn
                        ? column - visibleLastColumn
                        : 0;
                var distance = Math.Max(rowDistance, columnDistance);
                builder.Add(new PlannedViewportTile(
                    new TileAddress(x, y, width, height, RenderTilePolicy.TileBleedPixels),
                    isVisible,
                    distance));
            }
        }

        return builder
            .OrderByDescending(static tile => tile.IsVisible)
            .ThenBy(static tile => tile.OverscanDistance)
            .ThenBy(static tile => tile.Address.Y)
            .ThenBy(static tile => tile.Address.X)
            .ToImmutableArray();
    }

    public static RasterViewport FromLogicalViewport(
        double x,
        double y,
        double width,
        double height,
        double physicalPixelsPerLogicalPixel,
        int rasterWidth,
        int rasterHeight)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(width) || !double.IsFinite(height) ||
            !double.IsFinite(physicalPixelsPerLogicalPixel) ||
            width < 0 || height < 0 || physicalPixelsPerLogicalPixel <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Viewport geometry must be finite and non-negative.");
        }

        var left = CheckedFloor(x * physicalPixelsPerLogicalPixel);
        var top = CheckedFloor(y * physicalPixelsPerLogicalPixel);
        var right = CheckedCeiling((x + width) * physicalPixelsPerLogicalPixel);
        var bottom = CheckedCeiling((y + height) * physicalPixelsPerLogicalPixel);
        return new RasterViewport(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top))
            .ClampTo(rasterWidth, rasterHeight);
    }

    private static int CheckedFloor(double value)
    {
        if (!double.IsFinite(value) || value < int.MinValue || value > int.MaxValue)
            throw new OverflowException("Viewport geometry exceeds the supported coordinate range.");
        return checked((int)Math.Floor(value));
    }

    private static int CheckedCeiling(double value)
    {
        if (!double.IsFinite(value) || value < int.MinValue || value > int.MaxValue)
            throw new OverflowException("Viewport geometry exceeds the supported coordinate range.");
        return checked((int)Math.Ceiling(value));
    }
}
