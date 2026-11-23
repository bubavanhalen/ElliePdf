using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;
using ElliePdf.Rendering;

namespace ElliePdf.Rendering.Tests;

public sealed class VirtualizedLayoutTests
{
    [Fact]
    public void FenwickIndex_HandlesTenThousandPagesWithLogarithmicOperations()
    {
        var values = Enumerable.Repeat(100d, 10_000).ToArray();
        var index = new PageExtentIndex(values);
        var random = new Random(817);

        for (var operation = 0; operation < 500; operation++)
        {
            var page = random.Next(values.Length);
            var extent = 50 + random.NextDouble() * 250;
            values[page] = extent;
            index.UpdateExtent(page, extent);
            Assert.InRange(index.LastOperationCount, 1, 20);

            var expectedPage = random.Next(values.Length);
            var expectedOffset = values.Take(expectedPage).Sum() + values[expectedPage] * 0.37;
            Assert.Equal(expectedPage, index.FindPageAtOffset(expectedOffset));
            Assert.InRange(index.LastOperationCount, 1, 30);
        }

        Assert.Equal(values.Sum(), index.TotalExtent, 8);
        Assert.InRange(index.GetOffset(9_999), 0, double.MaxValue);
        Assert.InRange(index.LastOperationCount, 1, 20);
    }

    [Fact]
    public void MixedRotationsAndSizesProduceCorrectOffsetsAndNavigation()
    {
        var pages = new[]
        {
            Item(0, 200, 100, PageRotation.None),
            Item(1, 300, 100, PageRotation.Clockwise90), // display extent is 300
            Item(2, 400, 150, PageRotation.Clockwise180),
            Item(3, 120, 500, PageRotation.Clockwise270), // display extent is 120
        };
        var index = new PageExtentIndex(pages.Select(p => p.Geometry), pageGap: 10);

        Assert.Equal(0, index.FindPageAtOffset(0));
        Assert.Equal(1, index.FindPageAtOffset(205));
        Assert.Equal(2, index.FindPageAtOffset(420));
        Assert.Equal(3, index.FindPageAtOffset(770));
        Assert.Equal(420, index.GetOffset(2), 8);

        var planner = new ViewportRealizationPlanner(pages, new VirtualizationOptions(2, 2, 12), pageGap: 10);
        var plan = planner.Plan(300, 200, ScrollDirection.Forward);
        Assert.Equal(new[] { 1, 2, 3 }, plan.Pages.Select(p => p.PageIndex));
        Assert.Equal(pages[2].Id, planner.GetPage(2).Id);
    }

    [Fact]
    public void GeometryUpdatePreservesTheAnchorWhenAnEarlierPageChanges()
    {
        var pages = Enumerable.Range(0, 10).Select(i => Item(i, 100, 100, PageRotation.None)).ToArray();
        var layout = new AnchorPreservingPageLayout(pages);
        var viewport = layout.Extents.GetOffset(5) + 25;
        var oldId = pages[5].Id;

        var result = layout.UpdateGeometryPreservingAnchor(1, new PageLayoutGeometry(100, 225), viewport);

        Assert.Equal(oldId, result.Anchor.PageId);
        Assert.Equal(viewport + 125, result.ViewportOffset, 8);
        Assert.Equal(oldId, layout.CaptureAnchor(result.ViewportOffset).PageId);
        Assert.Equal(pages[1].Id, layout.Metadata[1].Id);
    }

    [Fact]
    public void RealizationRemainsBoundedForTenThousandPages()
    {
        var pages = Enumerable.Range(0, 10_000).Select(i => Item(i, 612, 792, PageRotation.None)).ToArray();
        var planner = new ViewportRealizationPlanner(pages, new VirtualizationOptions(50, 50, 500));

        for (var i = 0; i < 100; i++)
        {
            var plan = planner.Plan(i * 792 * 37, 792 * 2, i % 2 == 0 ? ScrollDirection.Forward : ScrollDirection.Backward);
            Assert.InRange(plan.RealizedCount, 1, 12);
            Assert.InRange(plan.Pages.Length, 1, 12);
        }
    }

    [Fact]
    public void CurrentPageUsesVisibleRangeAndRecycledElementsRejectStaleResults()
    {
        var pages = Enumerable.Range(0, 10_000).Select(i => Item(i, 500, 100 + i % 3, PageRotation.None)).ToArray();
        var planner = new ViewportRealizationPlanner(pages);
        var offset = planner.Extents.GetOffset(8_000) + 40;
        Assert.Equal(8_000, CurrentPageCalculator.Calculate(planner.Extents, offset, 20));

        var element = new RecycledPageElement();
        var first = element.Bind(pages[8_000]);
        Assert.True(element.TryPublishPixels(first, "first-pixels"));
        var second = element.Bind(pages[8_001]);
        var metadata = new PageAutomationMetadata(pages[8_001].Id, "Page 8002", 8_001, "range", new PdfRect(0, 0, 500, 101));
        Assert.False(element.TryPublishPixels(first, "stale-pixels"));
        Assert.False(element.TryPublishAutomation(first, metadata));
        Assert.True(element.TryPublish(second, new PageElementPublication("second-pixels", metadata)));
        Assert.Equal("second-pixels", element.PublishedPixels);
        Assert.Equal(pages[8_001].Id, element.Publication!.Automation.PageId);

        element.Clear();
        Assert.False(element.IsCurrent(second));
        Assert.Null(element.Publication);
    }

    [Fact]
    public void PrepareAndClearCancelPageScopedWork()
    {
        using var lifecycle = new PageElementLifecycle();
        var element = new RecycledPageElement();
        var first = lifecycle.Prepare(element, Item(0, 100, 100, PageRotation.None));
        var second = lifecycle.Prepare(element, Item(1, 100, 100, PageRotation.None));

        Assert.True(first.CancellationToken.IsCancellationRequested);
        Assert.False(second.CancellationToken.IsCancellationRequested);
        lifecycle.Clear(element);
        Assert.True(second.CancellationToken.IsCancellationRequested);
        Assert.False(element.IsBound);
    }

    private static PageLayoutItem Item(int index, double width, double height, PageRotation rotation)
        => new(new PageId(Guid.Parse($"00000000-0000-0000-0000-{index + 1:D12}")), index, new PageLayoutGeometry(width, height, rotation));
}
