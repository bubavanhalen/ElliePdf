using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;
using ElliePdf.Rendering;

namespace ElliePdf.Rendering.Tests;

public sealed class RenderCachePolicyTests
{
    [Fact]
    public void DefaultBudgetsMatchWp07()
    {
        var budgets = RenderCacheBudgets.Default;

        Assert.Equal(96L * 1024 * 1024, budgets.GpuTileBudgetBytes);
        Assert.Equal(32L * 1024 * 1024, budgets.CpuBufferBudgetBytes);
        Assert.Equal(16L * 1024 * 1024, budgets.ThumbnailBudgetBytes);
        Assert.Equal(16L * 1024 * 1024, budgets.MetadataBudgetBytes);
        Assert.Equal(2, RenderCacheBudgets.MaxUncachedLeaseCount);
        Assert.Equal(8L * 1024 * 1024, RenderCacheBudgets.MaxUncachedLeaseBytes);
    }

    [Fact]
    public void RenderKeyCacheUsesExactKeyIdentity()
    {
        var cache = new RenderRasterCache<string>(1024);
        var key = Key(page: 1, scale: 128);
        var equivalent = new RenderKey(
            key.DocumentId,
            key.PageId,
            key.ContentRevision,
            key.AppearanceRevision,
            key.Tile,
            key.RasterScale,
            key.Rotation,
            key.Mode);

        Assert.True(cache.Set(key, "pixels", 128));
        Assert.True(cache.TryGet(equivalent, out var cached));
        Assert.Equal("pixels", cached);
        Assert.False(cache.TryGet(Key(page: 1, scale: 64), out _));
    }

    [Fact]
    public void LruEvictsOldestUnprotectedEntry()
    {
        var cache = new RenderRasterCache<string>(250);
        var evictions = new List<CacheEviction<RenderKey>>();
        cache.EntryEvicted += (_, eviction) => evictions.Add(eviction);

        Assert.True(cache.Set(Key(1), "one", 100));
        Assert.True(cache.Set(Key(2), "two", 100));
        Assert.True(cache.Set(Key(3), "three", 100));

        Assert.False(cache.TryGet(Key(1), out _));
        Assert.True(cache.TryGet(Key(2), out var two));
        Assert.True(cache.TryGet(Key(3), out var three));
        Assert.Equal("two", two);
        Assert.Equal("three", three);
        Assert.Single(evictions);
        Assert.Equal(CacheEvictionReason.BudgetExceeded, evictions[0].Reason);
        Assert.Equal(Key(1), evictions[0].Key);
        Assert.InRange(cache.ResidentBytes, 0, cache.BudgetBytes);
    }

    [Fact]
    public void VisibleProtectionPreservesMarkedEntry()
    {
        var cache = new RenderRasterCache<string>(250);
        var visible = Key(1);
        var stale = Key(2);

        Assert.True(cache.Set(visible, "visible", 100));
        Assert.True(cache.Set(stale, "stale", 100));
        cache.ProtectKeys([visible]);

        Assert.True(cache.Set(Key(3), "new", 100));

        Assert.True(cache.TryGet(visible, out _));
        Assert.False(cache.TryGet(stale, out _));
        Assert.True(cache.TryGet(Key(3), out _));
    }

    [Fact]
    public void MemoryPressureLowersBudgetsAndEvicts()
    {
        var gpu = new RenderRasterCache<object>(RenderCacheBudgets.Default.GpuTileBudgetBytes);
        var cpu = new RenderRasterCache<object>(RenderCacheBudgets.Default.CpuBufferBudgetBytes);
        var thumbs = new ThumbnailRasterCache<object>(RenderCacheBudgets.Default.ThumbnailBudgetBytes);
        var metadata = new MetadataCache<object, object>(RenderCacheBudgets.Default.MetadataBudgetBytes);
        var manager = new RenderCacheBudgetManager(gpu, cpu, thumbs, metadata);

        Assert.True(gpu.Set(Key(1), new object(), 60L * 1024 * 1024));
        Assert.True(gpu.Set(Key(2), new object(), 30L * 1024 * 1024));
        Assert.True(gpu.Set(Key(3), new object(), 6L * 1024 * 1024));

        manager.ApplyMemoryPressure(RenderMemoryPressureLevel.Critical);

        Assert.Equal(48L * 1024 * 1024, manager.Budgets.GpuTileBudgetBytes);
        Assert.InRange(gpu.ResidentBytes, 0, gpu.BudgetBytes);
        Assert.True(gpu.TryGet(Key(3), out _));
        Assert.False(gpu.TryGet(Key(1), out _));
    }

    [Fact]
    public void CacheRejectsEntryWhenOnlyProtectedItemsCouldBeEvicted()
    {
        var cache = new RenderRasterCache<string>(200);
        var first = Key(1);
        var second = Key(2);

        Assert.True(cache.Set(first, "one", 100));
        Assert.True(cache.Set(second, "two", 100));
        cache.ProtectKeys([first, second]);

        Assert.False(cache.Set(Key(3), "three", 100));
        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet(first, out _));
        Assert.True(cache.TryGet(second, out _));
    }

    [Fact]
    public void OversizedInsertAndReplacementNeverCorruptResidentAccounting()
    {
        var cache = new MetadataCache<int, string>(100);

        Assert.False(cache.Set(1, "oversized", 101));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.ResidentBytes);

        Assert.True(cache.Set(1, "original", 80));
        Assert.False(cache.Set(1, "replacement", 101));
        Assert.Equal(80, cache.ResidentBytes);
        Assert.True(cache.TryGet(1, out var retained));
        Assert.Equal("original", retained);
    }

    [Fact]
    public void ExplicitBudgetReductionIsAHardCeilingEvenForProtectedEntries()
    {
        var cache = new RenderRasterCache<string>(200);
        var first = Key(1);
        var second = Key(2);
        Assert.True(cache.Set(first, "one", 100));
        Assert.True(cache.Set(second, "two", 100));
        cache.ProtectKeys([first, second]);

        cache.SetBudget(100, CacheEvictionReason.MemoryPressure);

        Assert.Equal(100, cache.ResidentBytes);
        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet(first, out _));
        Assert.True(cache.TryGet(second, out _));
    }

    [Fact]
    public void UncachedLeaseGateEnforcesCountAndByteLimits()
    {
        var gate = new UncachedLeaseGate();

        Assert.True(gate.TryAcquire(4L * 1024 * 1024, out var first));
        Assert.True(gate.TryAcquire(4L * 1024 * 1024, out var second));
        Assert.False(gate.TryAcquire(1, out _));
        first.Dispose();
        Assert.True(gate.TryAcquire(1L * 1024 * 1024, out var third));
        Assert.False(gate.TryAcquire(8L * 1024 * 1024, out _));
        second.Dispose();
        third.Dispose();
        Assert.Equal(0, gate.ActiveLeaseCount);
        Assert.Equal(0, gate.ActiveLeaseBytes);
    }

    [Fact]
    public void FullPageRenderingIsAllowedOnlyWithinDimensionAndByteLimits()
    {
        var full = RenderTilePolicy.CreatePlan(2048, 2048);
        var oversizedDimension = RenderTilePolicy.CreatePlan(2049, 1024);
        var oversizedBytes = RenderTilePolicy.CreatePlan(2048, 2049);

        Assert.Equal(RasterRenderStrategy.FullPage, full.Strategy);
        Assert.Equal(new TileAddress(0, 0, 2048, 2048, 0), Assert.Single(full.Tiles));
        Assert.Equal(RasterRenderStrategy.Tiled, oversizedDimension.Strategy);
        Assert.Equal(RasterRenderStrategy.Tiled, oversizedBytes.Strategy);
    }

    [Fact]
    public void TiledPlanUses512PixelInteriorsAndOnePixelBleed()
    {
        var plan = RenderTilePolicy.CreatePlan(2500, 900);

        Assert.Equal(RasterRenderStrategy.Tiled, plan.Strategy);
        Assert.Equal(10, plan.Tiles.Length);
        Assert.All(plan.Tiles, tile =>
        {
            Assert.InRange(tile.InteriorWidth, 1, 512);
            Assert.InRange(tile.InteriorHeight, 1, 512);
            Assert.Equal(1, tile.BleedPixels);
        });
        Assert.Contains(plan.Tiles, tile => tile == new TileAddress(0, 0, 512, 512, 1));
        Assert.Contains(plan.Tiles, tile => tile == new TileAddress(2048, 512, 452, 388, 1));
    }

    private static RenderKey Key(int page = 0, int scale = 64)
    {
        var documentId = new DocumentId(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var pageId = new PageId(Guid.Parse($"00000000-0000-0000-0000-{page:D12}"));
        return new RenderKey(
            documentId,
            pageId,
            PageContentRevision.Initial,
            PageAppearanceRevision.Initial,
            new TileAddress(0, 0, 64, 64, 0),
            new RasterScale64(scale),
            PageRotation.None,
            RenderMode.Normal);
    }
}
