using System.Diagnostics;
using System.Collections.Specialized;
using ElliePdf.Benchmarking;
using ElliePdf.Pages;
using ElliePdf.Pdf.Client;
using ElliePdf.Rendering;
using ElliePdf.Services;
using ElliePdf.ViewModels;

namespace ElliePdf;

/// <summary>
/// Executes a deliberately narrow benchmark action through the same ViewModel,
/// virtualization and isolated-worker path that the reader uses. It never accepts
/// document text as input and writes only the fixed protocol supplied by Core.
/// </summary>
internal static class BenchmarkDriver
{
    public static async Task RunAsync(
        BenchmarkDriverRequest request,
        ReaderViewModel reader,
        IPdfService pdfService,
        PdfWorkerClient workerClient,
        ReaderPage? readerPage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(pdfService);
        ArgumentNullException.ThrowIfNull(workerClient);

        switch (request.Scenario)
        {
            case "open":
                return;

            case "first-page":
                _ = await RenderVisiblePageAsync(reader, 0, ScrollDirection.None, cancellationToken);
                if (reader.BenchmarkFirstPagePresentedMilliseconds is not { } presentedMilliseconds)
                {
                    throw new InvalidOperationException("The readable first-page presentation was not observed.");
                }

                WriteMetric("first-page.presented", "ms", presentedMilliseconds);
                return;

            case "first-page-10000":
                await RunFirstPageTenThousandAsync(reader, workerClient, readerPage, cancellationToken);
                return;

            case "cached-navigation":
                await RunCachedNavigationAsync(reader, cancellationToken);
                return;

            case "render":
                await RunRenderAsync(reader, pdfService, cancellationToken);
                return;

            case "random-jump":
                await RunRandomJumpAsync(reader, cancellationToken);
                return;

            case "scroll":
                await RunScrollAsync(reader, cancellationToken);
                return;

            case "zoom":
                await RunZoomAsync(reader, cancellationToken);
                return;

            case "search":
                await RunSearchAsync(reader);
                return;

            case "memory":
                await RunMemoryAsync(reader, pdfService, workerClient, cancellationToken);
                return;

            case "cancellation":
                await RunCancellationAsync(reader, cancellationToken);
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    public static void WriteMetric(string name, string unit, double value) =>
        Console.WriteLine(BenchmarkDriverProtocol.FormatMetric(name, unit, value));

    public static void WriteReady(BenchmarkDriverRequest request) =>
        Console.WriteLine(BenchmarkDriverProtocol.FormatReady(request));

    private static async Task RunRandomJumpAsync(ReaderViewModel reader, CancellationToken cancellationToken)
    {
        var target = Math.Max(0, reader.DocumentPageCount / 2);
        // A jump first gets a deliberately lower-resolution preview. The second
        // request uses the same preview scale and therefore exercises the warm
        // preview cache. The final request returns to the device scale, which is
        // a different cache key and measures the sharp replacement/settle.
        const double previewRasterizationScaleMultiplier = 0.5;
        var previewUncached = await RenderVisiblePageAsync(
            reader,
            target,
            ScrollDirection.None,
            cancellationToken,
            previewRasterizationScaleMultiplier);
        var previewCached = await RenderVisiblePageAsync(
            reader,
            target,
            ScrollDirection.None,
            cancellationToken,
            previewRasterizationScaleMultiplier);
        var sharpSettled = await RenderVisiblePageAsync(
            reader,
            target,
            ScrollDirection.None,
            cancellationToken);
        WriteMetric("random-jump.preview-uncached", "ms", previewUncached);
        WriteMetric("random-jump.preview-cached", "ms", previewCached);
        WriteMetric("random-jump.sharp", "ms", sharpSettled);
    }

    private static async Task RunCachedNavigationAsync(ReaderViewModel reader, CancellationToken cancellationToken)
    {
        var page = Math.Clamp(reader.CurrentPageIndex, 0, Math.Max(0, reader.DocumentPageCount - 1));
        await RenderVisiblePageAsync(reader, page, ScrollDirection.None, cancellationToken);
        WriteMetric("cached-navigation", "ms", await RenderVisiblePageAsync(reader, page, ScrollDirection.None, cancellationToken));
    }

    private static async Task RunFirstPageTenThousandAsync(
        ReaderViewModel reader,
        PdfWorkerClient workerClient,
        ReaderPage? readerPage,
        CancellationToken cancellationToken)
    {
        var duration = await RenderVisiblePageAsync(reader, 0, ScrollDirection.None, cancellationToken);
        // Let ItemsRepeater process the ViewModel change before reading its actual
        // prepared-control and surface-budget state.
        await Task.Yield();
        var surface = readerPage?.GetBenchmarkSurfaceSnapshot() ?? default;
        var worker = workerClient.GetBenchmarkResourceSnapshot();
        WriteMetric("first-page-10000", "ms", duration);
        WriteMetric("virtualization.realized-controls", "count", surface.RealizedControls);
        WriteMetric("virtualization.page-subscriptions", "count", surface.PageSubscriptions);
        WriteMetric("virtualization.uncached-raster-leases", "count", worker.ActiveSharedLeaseCount);
    }

    private static async Task RunRenderAsync(
        ReaderViewModel reader,
        IPdfService pdfService,
        CancellationToken cancellationToken)
    {
        // Opening normally realizes page zero. Select another page when possible so
        // the render scenario exercises an actual queue/native/upload operation.
        var page = reader.DocumentPageCount > 1 ? Math.Min(reader.DocumentPageCount - 1, 1) : 0;
        var duration = await RenderVisiblePageAsync(reader, page, ScrollDirection.None, cancellationToken);
        var queueWait = pdfService is PdfService service
            ? service.BenchmarkLastRenderQueueWaitMilliseconds
            : 0;
        WriteMetric("render.completed", "ms", duration);
        WriteMetric("render-queue-wait-ms", "ms", queueWait);
    }

    private static async Task RunScrollAsync(ReaderViewModel reader, CancellationToken cancellationToken)
    {
        var pageCount = Math.Max(1, reader.DocumentPageCount);
        var start = Math.Clamp(reader.CurrentPageIndex, 0, pageCount - 1);
        // Three visible page transitions exercise the same continuous-view
        // realization path as a scrollbar gesture without injecting synthetic input.
        var frames = new List<double>();
        for (var offset = 0; offset < Math.Min(3, pageCount); offset++)
        {
            var pageIndex = Math.Min(pageCount - 1, start + offset);
            frames.Add(await RenderVisiblePageAsync(reader, pageIndex, ScrollDirection.Forward, cancellationToken));
        }
        const double referenceIntervalMilliseconds = 1000.0 / 60.0;
        var dropped = frames.Sum(frame => Math.Max(0, Math.Ceiling(frame / referenceIntervalMilliseconds) - 1));
        var total = frames.Count + dropped;
        WriteMetric("scroll.frame", "ms", frames.Max());
        WriteMetric("scroll.dropped-frames-percent", "%", total > 0 ? dropped / total * 100.0 : 0);
    }

    private static async Task RunZoomAsync(ReaderViewModel reader, CancellationToken cancellationToken)
    {
        await RenderVisiblePageAsync(reader, reader.CurrentPageIndex, ScrollDirection.None, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        reader.ZoomInCommand.Execute(null);
        await reader.RefreshFromSessionAsync();
        await RenderVisiblePageAsync(reader, reader.CurrentPageIndex, ScrollDirection.None, cancellationToken);
        stopwatch.Stop();
        WriteMetric("zoom.input-to-present", "ms", stopwatch.Elapsed.TotalMilliseconds);
        WriteMetric("zoom.input-to-present-refresh-intervals", "intervals", stopwatch.Elapsed.TotalMilliseconds / (1000.0 / 60.0));
        WriteMetric("zoom.sharp-settled", "ms", stopwatch.Elapsed.TotalMilliseconds);
    }

    private static async Task RunSearchAsync(ReaderViewModel reader)
    {
        // A fixed, non-sensitive query lets the production search workflow run while
        // preventing command-line input from becoming document text or stdout data.
        reader.SearchQuery = "e";
        var stopwatch = Stopwatch.StartNew();
        double? firstResultMilliseconds = null;
        var completed = false;
        NotifyCollectionChangedEventHandler onResultsChanged = (_, args) =>
        {
            if (!completed && firstResultMilliseconds is null && args.NewItems?.Count > 0)
            {
                firstResultMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            }
        };
        reader.SearchResults.CollectionChanged += onResultsChanged;
        try
        {
            await reader.SearchCommand.ExecuteAsync(null);
        }
        finally
        {
            completed = true;
            reader.SearchResults.CollectionChanged -= onResultsChanged;
            stopwatch.Stop();
        }

        if (firstResultMilliseconds is null)
        {
            throw new InvalidOperationException("The fixed benchmark search did not publish a result.");
        }

        WriteMetric("search.first-result", "ms", firstResultMilliseconds.Value);
        WriteMetric("search.first-before-complete", "bool", firstResultMilliseconds.Value < stopwatch.Elapsed.TotalMilliseconds ? 1 : 0);
        WriteMetric("search.completed", "ms", stopwatch.Elapsed.TotalMilliseconds);
    }

    private static async Task RunMemoryAsync(
        ReaderViewModel reader,
        IPdfService pdfService,
        PdfWorkerClient workerClient,
        CancellationToken cancellationToken)
    {
        var before = BenchmarkProcessResourceSnapshot.Capture(workerClient.GetBenchmarkResourceSnapshot());
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var stopwatch = Stopwatch.StartNew();
        await RenderVisiblePageAsync(reader, reader.CurrentPageIndex, ScrollDirection.None, cancellationToken);
        stopwatch.Stop();
        var seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        var allocationRate = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore) / seconds;
        var after = BenchmarkProcessResourceSnapshot.Capture(workerClient.GetBenchmarkResourceSnapshot());
        var cpuCache = pdfService is PdfService concrete ? concrete.BenchmarkCpuTileCacheBytes : 0;
        var cpuMilliseconds = Math.Max(0, after.TotalCpuMilliseconds - before.TotalCpuMilliseconds);
        WriteMetric("memory.private-bytes", "bytes", after.PrivateBytes);
        WriteMetric("memory.ui.private-bytes", "bytes", after.UiPrivateBytes);
        WriteMetric("memory.worker.private-bytes", "bytes", after.WorkerPrivateBytes);
        WriteMetric("memory.working-set-bytes", "bytes", after.WorkingSetBytes);
        WriteMetric("memory.cpu-ms", "ms", cpuMilliseconds);
        WriteMetric("memory.shared-mappings-bytes", "bytes", after.SharedMappingBytes);
        // The reader's image-cache byte ownership is the actual allocation that this
        // process can attribute to its GPU-facing tiles; no driver-side estimate is used.
        WriteMetric("memory.gpu-allocation-bytes", "bytes", reader.BenchmarkGpuTileCacheBytes);
        WriteMetric("memory.cache-gpu-bytes", "bytes", reader.BenchmarkGpuTileCacheBytes);
        WriteMetric("memory.cache-cpu-bytes", "bytes", cpuCache);
        WriteMetric("memory.cache-thumbnails-bytes", "bytes", reader.BenchmarkThumbnailCacheBytes);
        WriteMetric("memory.cache-geometry-bytes", "bytes", reader.BenchmarkGeometryCacheBytes);
        WriteMetric("memory.allocation-rate-bytes-per-second", "bytes-per-second", allocationRate);
    }

    private static async Task RunCancellationAsync(ReaderViewModel reader, CancellationToken cancellationToken)
    {
        if (reader.DocumentPageCount < 2)
        {
            throw new InvalidOperationException("Cancellation evidence requires at least two pages.");
        }

        var target = reader.DocumentPageCount - 1;
        reader.GoToPage(target);
        var staleStopwatch = Stopwatch.StartNew();
        var stale = reader.EnsureContinuousPageRenderedAsync(
            target,
            CreateViewport(reader),
            ScrollDirection.Forward,
        CancellationToken.None);
        await Task.Yield();
        reader.GoToPage(0);
        var stalePublished = await stale;
        staleStopwatch.Stop();
        if (stalePublished)
        {
            throw new InvalidOperationException("The stale benchmark render published before navigation changed.");
        }
        WriteMetric("cancellation.stale-rejection", "ms", staleStopwatch.Elapsed.TotalMilliseconds);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        var render = RenderVisiblePageAsync(reader, target, ScrollDirection.Forward, cancellation.Token);
        await Task.Yield();
        cancellation.Cancel();
        var cancelled = false;
        try
        {
            await render;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the operation being measured.
            cancelled = true;
        }
        stopwatch.Stop();
        if (!cancelled)
        {
            throw new InvalidOperationException("The active benchmark render completed before cancellation.");
        }
        WriteMetric("cancellation.active-yield", "ms", stopwatch.Elapsed.TotalMilliseconds);
    }

    private static async Task<double> RenderVisiblePageAsync(
        ReaderViewModel reader,
        int pageIndex,
        ScrollDirection direction,
        CancellationToken cancellationToken,
        double rasterizationScaleMultiplier = 1)
    {
        if (reader.DocumentPageCount <= 0)
        {
            throw new InvalidOperationException("The benchmark fixture contains no pages.");
        }

        var selectedPage = Math.Clamp(pageIndex, 0, reader.DocumentPageCount - 1);
        reader.GoToPage(selectedPage);
        var stopwatch = Stopwatch.StartNew();
        var rendered = await reader.EnsureContinuousPageRenderedAsync(
            selectedPage,
            CreateViewport(reader),
            direction,
            cancellationToken,
            rasterizationScaleMultiplier);
        stopwatch.Stop();
        if (!rendered)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return stopwatch.Elapsed.TotalMilliseconds;
    }

    private static PageViewport CreateViewport(ReaderViewModel reader) =>
        new(0, 0, Math.Max(1, reader.ViewportWidth), Math.Max(1, reader.ViewportHeight));
}

internal readonly record struct BenchmarkProcessResourceSnapshot(
    long UiPrivateBytes,
    long WorkerPrivateBytes,
    long PrivateBytes,
    long WorkingSetBytes,
    double TotalCpuMilliseconds,
    long SharedMappingBytes)
{
    public static BenchmarkProcessResourceSnapshot Capture(PdfWorkerResourceSnapshot worker)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var uiPrivate = Math.Max(0, process.PrivateMemorySize64);
        var uiWorkingSet = Math.Max(0, process.WorkingSet64);
        var uiCpu = Math.Max(0, process.TotalProcessorTime.TotalMilliseconds);
        return new(
            uiPrivate,
            worker.PrivateBytes,
            checked(uiPrivate + worker.PrivateBytes),
            checked(uiWorkingSet + worker.WorkingSetBytes),
            uiCpu + worker.CpuMilliseconds,
            worker.SharedMappingBytes);
    }
}
