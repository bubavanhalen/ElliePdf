using System.Collections.Concurrent;
using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;
using ElliePdf.Printing;
using Xunit;

namespace ElliePdf.Printing.Tests;

public sealed class PrintPipelineTests
{
    [Fact]
    public void Expander_supports_current_all_and_ordered_custom_ranges()
    {
        Assert.Equal<int>([2], PrintPageRangeExpander.Expand(PrintPageSelection.CurrentPage(), 5, 2));
        Assert.Equal<int>([0, 1, 2, 3, 4], PrintPageRangeExpander.Expand(PrintPageSelection.AllPages(), 5, 2));
        Assert.Equal<int>([3, 4, 1], PrintPageRangeExpander.Expand(PrintPageSelection.Custom([new PrintPageRange(3, 4), new PrintPageRange(1, 1)]), 5, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => PrintPageRangeExpander.Expand(PrintPageSelection.Custom([new PrintPageRange(0, 5)]), 5, 0));
    }

    [Fact]
    public async Task Prints_one_page_as_direct_pixel_surface()
    {
        var session = new FakeSession([(72, 72, PageRotation.None)]);
        var consumer = new RecordingConsumer();
        var result = await new PrintPipeline().ExecuteAsync(session, Request(session.DocumentId, PrintPageSelection.CurrentPage()), 0, consumer, CancellationToken.None);

        Assert.Equal(1, result.PrintedPageCount);
        var surface = Assert.Single(consumer.Surfaces);
        Assert.Equal(0, surface.PageIndex);
        Assert.True(surface.First);
        Assert.True(surface.Last);
        Assert.Equal(PixelFormat.Bgra8Premultiplied, surface.Format);
        Assert.True(result.PeakRetainedBytes <= PrintPipeline.MaxRetainedBytes);
    }

    [Fact]
    public async Task Prints_1000_pages_in_order_with_bounded_retention()
    {
        var session = new FakeSession(Enumerable.Repeat((72d, 72d, PageRotation.None), 1000).ToArray());
        var consumer = new RecordingConsumer();
        var result = await new PrintPipeline().ExecuteAsync(session, Request(session.DocumentId, PrintPageSelection.AllPages()), 0, consumer, CancellationToken.None);

        Assert.Equal(1000, result.PrintedPageCount);
        Assert.Equal(1000, result.PrintedTileCount);
        Assert.Equal(Enumerable.Range(0, 1000), consumer.Surfaces.Select(static item => item.PageIndex));
        Assert.True(result.PeakRetainedBytes <= PrintPipeline.MaxRetainedBytes);
        Assert.True(session.MaximumOutstandingRenders <= PrintPipeline.MaxRetainedSurfaceCount);
    }

    [Fact]
    public async Task Preserves_mixed_orientation_and_uses_fit_or_actual_size()
    {
        var session = new FakeSession([(200, 100, PageRotation.None), (100, 200, PageRotation.Clockwise90)]);
        var consumer = new RecordingConsumer();
        var request = new PrintPipelineRequest(session.DocumentId, PrintPageSelection.AllPages(), new PrintTarget(100, 100, 72));
        await new PrintPipeline().ExecuteAsync(session, request, 0, consumer, CancellationToken.None);

        Assert.Equal([true, true], consumer.Surfaces.Where(static item => item.First).Select(static item => item.Landscape));
        Assert.All(consumer.Surfaces.Where(static item => item.First), static item => Assert.Equal(0.5d, item.Scale));

        consumer = new RecordingConsumer();
        request = request with { Scaling = PrintScalingMode.ActualSize };
        await new PrintPipeline().ExecuteAsync(session, request, 0, consumer, CancellationToken.None);
        Assert.All(consumer.Surfaces.Where(static item => item.First), static item => Assert.Equal(1d, item.Scale));
    }

    [Fact]
    public async Task Auto_orients_the_printable_target_for_landscape_pages()
    {
        var session = new FakeSession([(720, 540, PageRotation.None)]);
        var consumer = new RecordingConsumer();
        var request = new PrintPipelineRequest(
            session.DocumentId,
            PrintPageSelection.AllPages(),
            new PrintTarget(540, 720, 72));

        await new PrintPipeline().ExecuteAsync(
            session,
            request,
            0,
            consumer,
            CancellationToken.None);

        var surface = Assert.Single(consumer.Surfaces, static item => item.First);
        Assert.True(surface.Landscape);
        Assert.Equal(1d, surface.Scale);
    }

    [Fact]
    public async Task Downsamples_extreme_pages_without_changing_actual_print_size()
    {
        var session = new FakeSession([(2000, 2000, PageRotation.None)]);
        var consumer = new PlanRecordingConsumer();
        var request = new PrintPipelineRequest(
            session.DocumentId,
            PrintPageSelection.AllPages(),
            new PrintTarget(2000, 2000, 1200),
            PrintScalingMode.ActualSize);

        await new PrintPipeline().ExecuteAsync(session, request, 0, consumer, CancellationToken.None);

        var plan = Assert.Single(consumer.Plans);
        Assert.Equal(1d, plan.EffectiveScale);
        Assert.True((long)plan.RasterWidth * plan.RasterHeight * 4 <= PrintPipeline.MaxRasterBytesPerPage);
        Assert.True(plan.RasterPixelsPerPoint < 1200d / 72d);
    }

    [Fact]
    public async Task Rejects_printing_when_permissions_forbid_it()
    {
        var session = new FakeSession([(72, 72, PageRotation.None)], new PdfPermissions(canPrint: false));
        await Assert.ThrowsAsync<PrintNotAllowedException>(async () => await new PrintPipeline().ExecuteAsync(session, Request(session.DocumentId, PrintPageSelection.AllPages()), 0, new RecordingConsumer(), CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_stops_at_a_surface_checkpoint()
    {
        var session = new FakeSession(Enumerable.Repeat((72d, 72d, PageRotation.None), 50).ToArray());
        using var cancellation = new CancellationTokenSource();
        var consumer = new CancellingConsumer(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await new PrintPipeline().ExecuteAsync(session, Request(session.DocumentId, PrintPageSelection.AllPages()), 0, consumer, cancellation.Token));
        Assert.Equal(1, consumer.Count);
        Assert.Equal(0, session.OutstandingRenders);
    }

    private static PrintPipelineRequest Request(DocumentId documentId, PrintPageSelection selection)
        => new(documentId, selection, new PrintTarget(72, 72, 72), PrintScalingMode.ActualSize);

    private sealed class RecordingConsumer : IPrintSurfaceConsumer
    {
        public List<(int PageIndex, bool First, bool Last, bool Landscape, double Scale, PixelFormat Format)> Surfaces { get; } = [];
        public ValueTask ConsumeAsync(PrintPageSurface surface, CancellationToken cancellationToken)
        {
            Surfaces.Add((surface.Plan.PageIndex, surface.IsFirstTile, surface.IsLastTile, surface.Plan.IsLandscape, surface.Plan.EffectiveScale, surface.PixelBuffer.Format));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellingConsumer(CancellationTokenSource cancellation) : IPrintSurfaceConsumer
    {
        public int Count { get; private set; }
        public ValueTask ConsumeAsync(PrintPageSurface surface, CancellationToken cancellationToken)
        {
            Count++;
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PlanRecordingConsumer : IPrintSurfaceConsumer
    {
        public List<PrintPagePlan> Plans { get; } = [];
        public ValueTask ConsumeAsync(PrintPageSurface surface, CancellationToken cancellationToken)
        {
            if (surface.IsFirstTile)
            {
                Plans.Add(surface.Plan);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSession : IPdfEngineSession
    {
        private readonly (double Width, double Height, PageRotation Rotation)[] _pages;
        private readonly PdfPermissions _permissions;
        private int _outstanding;
        public FakeSession((double Width, double Height, PageRotation Rotation)[] pages, PdfPermissions? permissions = null)
        {
            _pages = pages;
            _permissions = permissions ?? new PdfPermissions();
            DocumentId = DocumentId.New();
        }
        public DocumentId DocumentId { get; }
        public int MaximumOutstandingRenders { get; private set; }
        public int OutstandingRenders => Volatile.Read(ref _outstanding);
        public ValueTask<PdfMetadata> GetMetadataAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new PdfMetadata(_pages.Length));
        public ValueTask<PdfPermissions> GetPermissionsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_permissions);
        public ValueTask<PageMetadata> GetPageMetadataAsync(int pageIndex, CancellationToken cancellationToken)
        {
            var page = _pages[pageIndex];
            var bounds = new PdfRect(0, 0, page.Width, page.Height);
            return ValueTask.FromResult(new PageMetadata(new PageId(Guid.NewGuid()), pageIndex, new PageGeometry(bounds, bounds, page.Rotation)));
        }
        public ValueTask<IPixelBufferLease> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
        {
            var tile = request.Key.Tile;
            var width = tile.InteriorWidth + tile.BleedPixels * 2;
            var height = tile.InteriorHeight + tile.BleedPixels * 2;
            var bytes = checked(width * height * 4);
            var current = Interlocked.Increment(ref _outstanding);
            MaximumOutstandingRenders = Math.Max(MaximumOutstandingRenders, current);
            IPixelBufferLease lease = new PixelBufferLease(Guid.NewGuid(), "test", 0, bytes, width, height, width * 4, PixelFormat.Bgra8Premultiplied, request.Key,
                () => { Interlocked.Decrement(ref _outstanding); return ValueTask.CompletedTask; });
            return ValueTask.FromResult(lease);
        }
        public ValueTask<PageTextResult> GetPageTextAsync(PageTextRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<IReadOnlyList<SearchResult>> SearchPageAsync(PageSearchRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<OutlineResult> GetOutlineAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<PageLinks> GetPageLinksAsync(int pageIndex, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<FormWidgetsResult> GetFormWidgetsAsync(int pageIndex, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask ApplyFormValueAsync(FormValueChange change, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
