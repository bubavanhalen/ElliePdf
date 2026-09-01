using ElliePdf.Rendering;

namespace ElliePdf.Rendering.Tests;

public sealed class ViewportTilePlannerTests
{
    [Fact]
    public void HighZoomPlansOnlyViewportAndOverscan()
    {
        var tiles = ViewportTilePlanner.Plan(
            rasterWidth: 52_224,
            rasterHeight: 67_584,
            new RasterViewport(20_000, 30_000, 1_920, 1_080),
            ScrollDirection.Forward);

        Assert.InRange(tiles.Length, 1, 32);
        Assert.All(tiles, tile =>
        {
            Assert.InRange(tile.Address.InteriorWidth, 1, 512);
            Assert.InRange(tile.Address.InteriorHeight, 1, 512);
            Assert.Equal(1, tile.Address.BleedPixels);
        });
    }

    [Fact]
    public void AdjacentTilesHaveExactInteriorCoverageWithoutSeams()
    {
        var tiles = ViewportTilePlanner.Plan(
            1_537,
            900,
            new RasterViewport(0, 0, 1_537, 900),
            overscanBands: 0);

        var coverage = new byte[1_537 * 900];
        foreach (var tile in tiles)
        {
            for (var y = tile.Address.Y; y < tile.Address.Y + tile.Address.InteriorHeight; y++)
            {
                for (var x = tile.Address.X; x < tile.Address.X + tile.Address.InteriorWidth; x++)
                {
                    coverage[(y * 1_537) + x]++;
                }
            }
        }

        Assert.All(coverage, pixel => Assert.Equal(1, pixel));
    }

    [Fact]
    public void ForwardPrefetchDoesNotPlanTilesBehindViewport()
    {
        var tiles = ViewportTilePlanner.Plan(
            1_024,
            8_192,
            new RasterViewport(0, 3_072, 1_024, 512),
            ScrollDirection.Forward);

        Assert.DoesNotContain(tiles, tile => tile.Address.Y < 3_072);
        Assert.Contains(tiles, tile => !tile.IsVisible && tile.Address.Y == 3_584);
    }

    [Fact]
    public void LogicalViewportUsesOutwardPhysicalPixelRounding()
    {
        var viewport = ViewportTilePlanner.FromLogicalViewport(
            10.25,
            20.25,
            100.1,
            50.1,
            1.5,
            2_000,
            2_000);

        Assert.Equal(new RasterViewport(15, 30, 151, 76), viewport);
    }
}
