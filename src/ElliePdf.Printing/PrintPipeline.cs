using System.Threading.Channels;
using System.Collections.Immutable;
using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;
using ElliePdf.Rendering;

namespace ElliePdf.Printing;

/// <summary>UI-neutral, direct-pixel printing producer. It never creates PNGs or full-page bitmaps.</summary>
public sealed class PrintPipeline
{
    public const int MaxRetainedSurfaceCount = 3;
    public const long MaxRetainedBytes = 128L * 1024 * 1024;
    public const long MaxRasterBytesPerPage = MaxRetainedBytes / MaxRetainedSurfaceCount;
    private readonly EngineJobScheduler? _scheduler;

    public PrintPipeline(EngineJobScheduler? scheduler = null)
    {
        _scheduler = scheduler;
    }

    public async ValueTask<PrintPipelineResult> ExecuteAsync(
        IPdfEngineSession session,
        PrintPipelineRequest request,
        int currentPageIndex,
        IPrintSurfaceConsumer consumer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(consumer);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (session.DocumentId != request.DocumentId) throw new ArgumentException("The request belongs to another document.", nameof(request));

        var permissions = await ScheduleAsync(
            session,
            EngineJobClass.Print,
            "permissions",
            session.GetPermissionsAsync,
            cancellationToken).ConfigureAwait(false);
        if (!permissions.CanPrint) throw new PrintNotAllowedException();
        var metadata = await ScheduleAsync(
            session,
            EngineJobClass.Print,
            "metadata",
            session.GetMetadataAsync,
            cancellationToken).ConfigureAwait(false);
        var pages = PrintPageRangeExpander.Expand(request.Selection, metadata.PageCount, currentPageIndex);
        var channel = Channel.CreateBounded<PrintPageSurface>(new BoundedChannelOptions(MaxRetainedSurfaceCount)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
        await using var budget = new SurfaceBudget(MaxRetainedSurfaceCount, MaxRetainedBytes);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceAsync(session, request, pages, channel.Writer, budget, linked.Token, _scheduler);
        var printedPages = new HashSet<int>();
        var printedTiles = 0;

        try
        {
            await foreach (var surface in channel.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
            {
                await using (surface.ConfigureAwait(false))
                {
                    linked.Token.ThrowIfCancellationRequested();
                    await consumer.ConsumeAsync(surface, linked.Token).ConfigureAwait(false);
                    printedPages.Add(surface.Plan.PageIndex);
                    printedTiles++;
                }
            }
            await producer.ConfigureAwait(false);
            return new PrintPipelineResult(printedPages.Count, printedTiles, budget.PeakBytes);
        }
        catch
        {
            linked.Cancel();
            try { await producer.ConfigureAwait(false); } catch (OperationCanceledException) { }
            while (channel.Reader.TryRead(out var abandoned)) await abandoned.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ProduceAsync(IPdfEngineSession session, PrintPipelineRequest request, IEnumerable<int> pageIndices, ChannelWriter<PrintPageSurface> writer, SurfaceBudget budget, CancellationToken cancellationToken, EngineJobScheduler? scheduler)
    {
        try
        {
            foreach (var pageIndex in pageIndices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await ScheduleAsync(
                    session,
                    EngineJobClass.Print,
                    $"page-metadata:{pageIndex}",
                    token => session.GetPageMetadataAsync(pageIndex, token),
                    cancellationToken,
                    scheduler).ConfigureAwait(false);
                var plan = CreatePlan(request, page);
                var tiles = CreatePrintTiles(plan.RasterWidth, plan.RasterHeight);
                for (var tileIndex = 0; tileIndex < tiles.Length; tileIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tile = tiles[tileIndex];
                    var reservation = await budget.AcquireAsync(EstimateBytes(tile), cancellationToken).ConfigureAwait(false);
                    PrintPageSurface? surface = null;
                    var published = false;
                    try
                    {
                        var key = new RenderKey(session.DocumentId, page.Id, page.ContentRevision, page.AppearanceRevision, tile,
                            RasterScale64.FromPhysicalPixelsPerPoint(plan.RasterPixelsPerPoint), page.Geometry.Rotation, RenderMode.Normal);
                        var renderRequest = new RenderRequest(key, RenderGeneration.Initial, RenderQuality.High,
                            EngineJobPriority.OtherVisible, DateTimeOffset.UtcNow.AddMinutes(2));
                        var lease = await ScheduleAsync(
                            session,
                            EngineJobClass.Print,
                            $"tile:{key}",
                            token => session.RenderAsync(renderRequest, token),
                            cancellationToken,
                            scheduler).ConfigureAwait(false);
                        if (lease.ByteLength > reservation.ByteCount) throw new InvalidOperationException("The worker returned a print tile larger than the retained-memory reservation.");
                        surface = new PrintPageSurface(plan, tileIndex, tiles.Length, lease, reservation);
                        reservation = null;
                        await writer.WriteAsync(surface, cancellationToken).ConfigureAwait(false);
                        published = true;
                    }
                    finally
                    {
                        if (!published && surface is not null)
                        {
                            await surface.DisposeAsync().ConfigureAwait(false);
                        }
                        if (reservation is not null) await reservation.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
            writer.TryComplete();
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
            throw;
        }
    }

    private Task<T> ScheduleAsync<T>(
        IPdfEngineSession session,
        EngineJobClass jobClass,
        string identity,
        Func<CancellationToken, ValueTask<T>> execute,
        CancellationToken cancellationToken)
        => ScheduleAsync(session, jobClass, identity, execute, cancellationToken, _scheduler);

    private static async Task<T> ScheduleAsync<T>(
        IPdfEngineSession session,
        EngineJobClass jobClass,
        string identity,
        Func<CancellationToken, ValueTask<T>> execute,
        CancellationToken cancellationToken,
        EngineJobScheduler? scheduler)
    {
        if (scheduler is null) return await execute(cancellationToken).ConfigureAwait(false);
        var result = await scheduler.ScheduleAsync(
            new EngineJobRequest(
                session.DocumentId,
                jobClass,
                identity,
                RenderGeneration.Initial,
                DateTimeOffset.UtcNow.AddMinutes(2),
                EnforceGeneration: false),
            execute,
            cancellationToken).ConfigureAwait(false);
        if (result.Status == RenderJobCompletionStatus.Faulted)
            throw result.Error ?? new IOException($"The print engine operation '{jobClass}' failed.");
        if (result.Status != RenderJobCompletionStatus.Published || result.Value is null)
            throw new OperationCanceledException($"The print operation was not publication eligible ({result.Status}).", cancellationToken);
        return result.Value;
    }

    private static PrintPagePlan CreatePlan(PrintPipelineRequest request, PageMetadata page)
    {
        var source = page.SizeInPoints;
        var rotated = page.Geometry.Rotation is PageRotation.Clockwise90 or PageRotation.Clockwise270;
        var width = rotated ? source.Height : source.Width;
        var height = rotated ? source.Width : source.Height;
        var targetWidth = request.Target.PrintableWidthPoints;
        var targetHeight = request.Target.PrintableHeightPoints;
        // Printing is page scoped: a landscape source uses the landscape form of the
        // selected media while a portrait source uses the portrait form.  This keeps
        // mixed-orientation documents correctly fitted without retaining or rotating a
        // full-page raster in the UI process.
        if ((width > height) != (targetWidth > targetHeight))
        {
            (targetWidth, targetHeight) = (targetHeight, targetWidth);
        }
        var fit = Math.Min(targetWidth / width, targetHeight / height);
        var scale = (request.Scaling == PrintScalingMode.FitToPrintableArea ? fit : 1d) * request.UserScale;
        var pixelsPerPoint = scale * request.Target.Dpi / 72d;
        var dimensionLimit = Math.Min(
            PdfContractLimits.MaxPixelDimension / width,
            PdfContractLimits.MaxPixelDimension / height);
        var memoryLimit = Math.Sqrt(MaxRasterBytesPerPage / (4d * width * height));
        var requestedPixelsPerPoint = pixelsPerPoint;
        pixelsPerPoint = Math.Min(requestedPixelsPerPoint, Math.Min(dimensionLimit, memoryLimit));
        if (pixelsPerPoint < requestedPixelsPerPoint)
        {
            // Leave headroom for the integral ceiling on both raster dimensions.
            pixelsPerPoint *= 0.999d;
        }
        if (!double.IsFinite(pixelsPerPoint) || pixelsPerPoint <= 0)
            throw new InvalidOperationException("The selected print scale exceeds the supported raster dimensions.");
        var rasterWidth = checked((int)Math.Ceiling(width * pixelsPerPoint));
        var rasterHeight = checked((int)Math.Ceiling(height * pixelsPerPoint));
        var tiles = CreatePrintTiles(rasterWidth, rasterHeight).Length;
        return new PrintPagePlan(page.PageIndex, page.Id, page.Geometry.Rotation, new PdfSize(width, height), width > height, scale, pixelsPerPoint, rasterWidth, rasterHeight, tiles);
    }

    private static System.Collections.Immutable.ImmutableArray<TileAddress> CreatePrintTiles(
        int rasterWidth,
        int rasterHeight) =>
        ViewportTilePlanner.Plan(
                rasterWidth,
                rasterHeight,
                new RasterViewport(0, 0, rasterWidth, rasterHeight),
                ScrollDirection.None,
                overscanBands: 0)
            .Select(static planned => planned.Address)
            .ToImmutableArray();

    private static long EstimateBytes(TileAddress tile)
    {
        var width = checked(tile.InteriorWidth + tile.BleedPixels * 2);
        var height = checked(tile.InteriorHeight + tile.BleedPixels * 2);
        return checked((long)width * height * 4);
    }

    private sealed class SurfaceBudget : IAsyncDisposable
    {
        private readonly SemaphoreSlim _slots;
        private readonly object _gate = new();
        private readonly long _limit;
        private long _retained;
        public SurfaceBudget(int count, long limit) { _slots = new SemaphoreSlim(count, count); _limit = limit; }
        public long PeakBytes { get; private set; }
        public async ValueTask<Reservation> AcquireAsync(long bytes, CancellationToken cancellationToken)
        {
            if (bytes is <= 0 or > MaxRetainedBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
            await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_retained + bytes > _limit) { _slots.Release(); throw new InvalidOperationException("The print surface memory budget was exceeded."); }
                _retained += bytes;
                PeakBytes = Math.Max(PeakBytes, _retained);
            }
            return new Reservation(this, bytes);
        }
        private void Release(long bytes) { lock (_gate) _retained -= bytes; _slots.Release(); }
        public ValueTask DisposeAsync() { _slots.Dispose(); return ValueTask.CompletedTask; }
        internal sealed class Reservation(SurfaceBudget owner, long byteCount) : IAsyncDisposable
        {
            private int _disposed;
            public long ByteCount { get; } = byteCount;
            public ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(ByteCount); return ValueTask.CompletedTask; }
        }
    }
}
