using ElliePdf.Domain.Documents;
using ElliePdf.Domain.Storage;
using ElliePdf.Helpers;
using ElliePdf.Infrastructure.Storage;
using ElliePdf.Models;
using ElliePdf.Pdf.Client;
using ElliePdf.Pdf.Contracts;
using ElliePdf.Rendering;
using ElliePdf.Telemetry;
using System.Security.Cryptography;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ElliePdf.Services;

/// <summary>
/// Compatibility facade for the existing WinUI view models. Every PDF operation is delegated
/// to the authenticated out-of-process engine client; this assembly never loads PDFium.
/// </summary>
public sealed class PdfService : IPdfService, IDisposable
{
    private readonly IPdfEngineClient _engineClient;
    private readonly IAtomicDocumentStore _atomicDocumentStore;
    private readonly IFileVersionStampProvider _fileVersionStampProvider;
    private readonly object _renderGate = new();
    private readonly Dictionary<DocumentId, IPdfEngineSession> _renderSessions = [];
    private readonly Dictionary<DocumentId, RenderGeneration> _renderGenerations = [];
    private readonly Dictionary<TileFlightKey, Task<CachedTile>> _tileFlights = [];
    private readonly Dictionary<TileFlightKey, RenderTelemetryActivity> _renderTelemetry = [];
    private readonly RenderRasterCache<CachedTile> _cpuTileCache =
        new(RenderCacheBudgets.Default.CpuBufferBudgetBytes);
    private readonly UncachedLeaseGate _uncachedLeaseGate = new();
    private readonly RenderScheduler _renderScheduler;
    private readonly EngineJobScheduler _engineScheduler;
    private readonly bool _ownsEngineScheduler;
    private int _disposed;
    private long _benchmarkLastRenderQueueWaitMicroseconds;

    public PdfService(
        IPdfEngineClient engineClient,
        IAtomicDocumentStore atomicDocumentStore,
        IFileVersionStampProvider fileVersionStampProvider,
        EngineJobScheduler? engineScheduler = null)
    {
        _engineClient = engineClient;
        _atomicDocumentStore = atomicDocumentStore;
        _fileVersionStampProvider = fileVersionStampProvider;
        _renderScheduler = new RenderScheduler(ExecuteRenderAsync);
        _engineScheduler = engineScheduler ?? new EngineJobScheduler();
        _ownsEngineScheduler = engineScheduler is null;
        _cpuTileCache.EntryEvicted += (_, eviction) =>
        {
            var operationId = TelemetryOperation.NextId();
            ElliePdfEventSource.Log.CacheEvicted(
                operationId,
                eviction.ByteCount,
                (int)eviction.Reason);
            ElliePdfEventSource.Log.CacheBytes(operationId, _cpuTileCache.ResidentBytes);
        };
    }

    public bool HasConfiguredNativeDependency =>
        _engineClient is not PdfWorkerClient worker || worker.WorkerBundleExists;

    public string? NativeDependencyIssue => HasConfiguredNativeDependency
        ? null
        : "The isolated PDF worker bundle is missing.";

    // This is deliberately internal: it is a process-local observation for the
    // opt-in benchmark driver, not a second rendering API or an externally exposed
    // document diagnostic surface.
    internal long BenchmarkCpuTileCacheBytes
    {
        get
        {
            lock (_renderGate)
            {
                return _cpuTileCache.ResidentBytes;
            }
        }
    }

    internal double BenchmarkLastRenderQueueWaitMilliseconds =>
        Math.Max(0, Interlocked.Read(ref _benchmarkLastRenderQueueWaitMicroseconds) / 1000d);

    public async Task<PdfDocumentSession> OpenDocumentAsync(
        string path,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ThrowIfDisposed();
        var operationId = TelemetryOperation.NextId();
        var started = TelemetryOperation.StartTimestamp();
        ElliePdfEventSource.Log.OpenStart(operationId);
        ElliePdfEventSource.Log.DocumentOpenStart(operationId);
        var fullPath = Path.GetFullPath(path);

        try
        {
            var sourceVersion = await _fileVersionStampProvider
                .TryCaptureAsync(fullPath, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new FileNotFoundException("The PDF no longer exists.", fullPath);
            var request = new DocumentOpenRequest(
                DocumentId.New(),
                new PdfSourceHandle(fullPath),
                password);
            var engineSession = await _engineClient
                .OpenSessionAsync(request, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var metadata = await engineSession.GetMetadataAsync(cancellationToken).ConfigureAwait(false);
                var duration = TelemetryOperation.ElapsedMicroseconds(started);
                ElliePdfEventSource.Log.MetadataRead(
                    operationId,
                    duration,
                    metadata.PageCount);
                ElliePdfEventSource.Log.MetadataReady(
                    operationId,
                    duration,
                    metadata.PageCount);
                lock (_renderGate)
                {
                    _renderSessions[engineSession.DocumentId] = engineSession;
                    _renderGenerations[engineSession.DocumentId] = RenderGeneration.Initial;
                }
                _renderScheduler.ReopenDocument(engineSession.DocumentId);
                _engineScheduler.ReopenDocument(engineSession.DocumentId);
                ElliePdfEventSource.Log.OpenStop(operationId, duration, true);
                return new PdfDocumentSession(
                    this,
                    engineSession,
                    fullPath,
                    metadata.PageCount,
                    metadata.IsEncrypted,
                    sourceVersion,
                    started);
            }
            catch
            {
                await engineSession.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (PdfWorkerRemoteException exception) when (exception.Code == "password_required_or_incorrect")
        {
            ElliePdfEventSource.Log.OpenStop(
                operationId,
                TelemetryOperation.ElapsedMicroseconds(started),
                false);
            throw password is null
                ? new PdfPasswordRequiredException(fullPath)
                : new PdfIncorrectPasswordException(fullPath);
        }
        catch (PdfWorkerUnavailableException exception)
        {
            ElliePdfEventSource.Log.OpenStop(
                operationId,
                TelemetryOperation.ElapsedMicroseconds(started),
                false);
            ElliePdfEventSource.Log.WorkerFailure(operationId, 1);
            throw new PdfiumDependencyException("The isolated PDF worker is unavailable.", exception);
        }
        catch
        {
            ElliePdfEventSource.Log.OpenStop(
                operationId,
                TelemetryOperation.ElapsedMicroseconds(started),
                false);
            throw;
        }
    }

    public async Task<RenderedPage> RenderPageAsync(
        PdfDocumentSession document,
        int pageIndex,
        double scale,
        CancellationToken cancellationToken = default)
        => await RenderPageCoreAsync(document, pageIndex, scale, isThumbnail: false, cancellationToken: cancellationToken).ConfigureAwait(false);

    private async Task<RenderedPage> RenderPageCoreAsync(
        PdfDocumentSession document,
        int pageIndex,
        double scale,
        bool isThumbnail,
        CancellationToken cancellationToken)
    {
        ValidateDocument(document);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, document.PageCount);
        if (!double.IsFinite(scale) || scale is < 0.1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(scale));

        var page = await GetPageMetadataScheduledAsync(document, pageIndex, cancellationToken).ConfigureAwait(false);
        var rasterScale = RasterScale64.FromPhysicalPixelsPerPoint(scale);
        var rasterWidth = CheckedDimension(page.SizeInPoints.Width, rasterScale.PhysicalPixelsPerPoint);
        var rasterHeight = CheckedDimension(page.SizeInPoints.Height, rasterScale.PhysicalPixelsPerPoint);
        if (page.Geometry.Rotation is PageRotation.Clockwise90 or PageRotation.Clockwise270)
            (rasterWidth, rasterHeight) = (rasterHeight, rasterWidth);

        var byteLength = checked((long)rasterWidth * rasterHeight * 4);
        if (rasterWidth > RenderTilePolicy.MaxFullPageDimension
            || rasterHeight > RenderTilePolicy.MaxFullPageDimension
            || byteLength > PdfContractLimits.MaxPixelBufferBytes)
        {
            throw new PdfResourceLimitException(
                "The requested zoom requires viewport tiles; a compatibility surface may not exceed 2,048 pixels or 16 MiB.");
        }

        var destination = GC.AllocateUninitializedArray<byte>(checked((int)byteLength));
        var tiles = ViewportTilePlanner.Plan(
            rasterWidth,
            rasterHeight,
            new RasterViewport(0, 0, rasterWidth, rasterHeight),
            overscanBands: 0);
        foreach (var planned in tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tile = planned.Address;
            var key = new RenderKey(
                document.EngineSession.DocumentId,
                page.Id,
                page.ContentRevision,
                page.AppearanceRevision,
                tile,
                rasterScale,
                page.Geometry.Rotation,
                RenderMode.Normal);
            var request = new RenderRequest(
                key,
                CurrentGeneration(document),
                RenderQuality.Standard,
                isThumbnail ? EngineJobPriority.VisibleThumbnail : EngineJobPriority.OtherVisible,
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5));
            var renderOptions = isThumbnail
                ? new RenderJobOptions(isThumbnail: true)
                : RenderJobOptions.Visible;
            var result = await _renderScheduler
                .ScheduleAsync(request, renderOptions, cancellationToken)
                .ConfigureAwait(false);
            if (result.Status == RenderJobCompletionStatus.Faulted)
                throw result.Error ?? new IOException("The PDF worker could not render the requested page.");
            if (!result.IsPublicationEligible || result.Lease is null)
                throw new OperationCanceledException($"The page render was not publication eligible ({result.Status}).", cancellationToken);
            await using var lease = result.Lease;
            await CopyLeaseInteriorAsync(lease, destination, rasterWidth, rasterHeight, tile, cancellationToken).ConfigureAwait(false);
        }

        return new RenderedPage(
            destination,
            rasterWidth,
            rasterHeight,
            checked((float)page.SizeInPoints.Width),
            checked((float)page.SizeInPoints.Height));
    }

    public RenderGeneration AdvanceRenderGeneration(PdfDocumentSession document)
    {
        ValidateDocument(document);
        RenderGeneration generation;
        lock (_renderGate)
        {
            var current = _renderGenerations.GetValueOrDefault(
                document.EngineSession.DocumentId,
                RenderGeneration.Initial);
            generation = new RenderGeneration(checked(current.Value + 1));
            _renderGenerations[document.EngineSession.DocumentId] = generation;
        }

        _renderScheduler.AdvanceGeneration(document.EngineSession.DocumentId, generation);
        _engineScheduler.AdvanceGeneration(document.EngineSession.DocumentId, generation);
        return generation;
    }

    public void ApplyRenderMemoryPressure(RenderMemoryPressureLevel pressure)
    {
        var budgets = RenderCacheBudgets.Default.ApplyMemoryPressure(pressure);
        lock (_renderGate)
        {
            _cpuTileCache.SetBudget(budgets.CpuBufferBudgetBytes, CacheEvictionReason.MemoryPressure);
        }
    }

    public async Task<RenderedPageViewport> RenderPageViewportAsync(
        PdfDocumentSession document,
        int pageIndex,
        PageRenderContext context,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(document);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, document.PageCount);
        ArgumentNullException.ThrowIfNull(context);
        context.Viewport.Validate();
        if (!double.IsFinite(context.LogicalPixelsPerPoint)
            || context.LogicalPixelsPerPoint <= 0
            || !double.IsFinite(context.RasterizationScale)
            || context.RasterizationScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(context));
        }

        var page = await GetPageMetadataScheduledAsync(document, pageIndex, cancellationToken).ConfigureAwait(false);
        var rasterScale = RasterScale64.FromPhysicalPixelsPerPoint(
            checked(context.LogicalPixelsPerPoint * context.RasterizationScale));

        var widthPoints = page.SizeInPoints.Width;
        var heightPoints = page.SizeInPoints.Height;
        if (page.Geometry.Rotation is PageRotation.Clockwise90 or PageRotation.Clockwise270)
        {
            (widthPoints, heightPoints) = (heightPoints, widthPoints);
        }

        var rasterWidth = CheckedPageDimension(widthPoints, rasterScale.PhysicalPixelsPerPoint);
        var rasterHeight = CheckedPageDimension(heightPoints, rasterScale.PhysicalPixelsPerPoint);
        var displayWidth = checked(widthPoints * context.LogicalPixelsPerPoint);
        var displayHeight = checked(heightPoints * context.LogicalPixelsPerPoint);
        if (!double.IsFinite(displayWidth) || !double.IsFinite(displayHeight)
            || displayWidth <= 0 || displayHeight <= 0)
        {
            throw new PdfResourceLimitException("The logical page geometry is invalid.");
        }

        var physicalPerLogical = rasterScale.PhysicalPixelsPerPoint / context.LogicalPixelsPerPoint;
        var rasterViewport = ViewportTilePlanner.FromLogicalViewport(
            context.Viewport.X,
            context.Viewport.Y,
            context.Viewport.Width,
            context.Viewport.Height,
            physicalPerLogical,
            rasterWidth,
            rasterHeight);
        var plannedTiles = ViewportTilePlanner.Plan(
            rasterWidth,
            rasterHeight,
            rasterViewport,
            context.Direction);

        var tasks = plannedTiles.Select(async planned =>
        {
            var key = new RenderKey(
                document.EngineSession.DocumentId,
                page.Id,
                page.ContentRevision,
                page.AppearanceRevision,
                planned.Address,
                rasterScale,
                page.Geometry.Rotation,
                context.Mode);
            var priority = planned.IsVisible
                ? context.InteractionCritical
                    ? EngineJobPriority.VisibleInteractionCritical
                    : EngineJobPriority.OtherVisible
                : EngineJobPriority.DirectionalOverscan;
            var request = new RenderRequest(
                key,
                context.Generation,
                RenderQuality.Standard,
                priority,
                DateTimeOffset.UtcNow + (planned.IsVisible ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(5)));
            var cached = await GetOrRenderTileAsync(
                request,
                new RenderJobOptions(isVisible: planned.IsVisible, prefetchDistance: planned.OverscanDistance),
                cancellationToken).ConfigureAwait(false);

            var tile = planned.Address;
            var leftBleed = tile.X > 0 ? tile.BleedPixels : 0;
            var topBleed = tile.Y > 0 ? tile.BleedPixels : 0;
            var left = (tile.X - leftBleed) * displayWidth / rasterWidth;
            var top = (tile.Y - topBleed) * displayHeight / rasterHeight;
            var width = cached.Width * displayWidth / rasterWidth;
            var height = cached.Height * displayHeight / rasterHeight;
            return new RenderedPageTile(
                key,
                cached.Pixels,
                cached.Width,
                cached.Height,
                cached.Stride,
                left,
                top,
                width,
                height,
                planned.IsVisible);
        }).ToArray();

        var renderedTiles = await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new RenderedPageViewport(
            pageIndex,
            rasterWidth,
            rasterHeight,
            displayWidth,
            displayHeight,
            rasterScale,
            renderedTiles);
    }

    public async Task<byte[]> RenderPageThumbnailAsync(
        PdfDocumentSession document,
        int pageIndex,
        int maxWidth,
        int maxHeight,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(document);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxWidth, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHeight, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxHeight, 4096);
        var page = await GetPageMetadataScheduledAsync(document, pageIndex, cancellationToken).ConfigureAwait(false);
        var scale = Math.Min(maxWidth / page.SizeInPoints.Width, maxHeight / page.SizeInPoints.Height);
        scale = Math.Max(1d / 64d, scale);
        while (Math.Ceiling(page.SizeInPoints.Width * scale) > RenderTilePolicy.MaxFullPageDimension
            || Math.Ceiling(page.SizeInPoints.Height * scale) > RenderTilePolicy.MaxFullPageDimension
            || checked((long)Math.Ceiling(page.SizeInPoints.Width * scale)
                * (long)Math.Ceiling(page.SizeInPoints.Height * scale) * 4) > PdfContractLimits.MaxPixelBufferBytes)
        {
            scale *= 0.75;
        }

        var rendered = await RenderPageCoreAsync(document, pageIndex, scale, isThumbnail: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await EncodeBitmapToPngAsync(
            rendered.BgraPixels,
            rendered.Width,
            rendered.Height,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TextMatch>> SearchTextAsync(
        PdfDocumentSession document,
        string query,
        bool matchCase,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var operationId = TelemetryOperation.NextId();
        var started = TelemetryOperation.StartTimestamp();
        ElliePdfEventSource.Log.SearchStarted(operationId);
        var generation = SearchGeneration.Initial;
        var matches = new List<TextMatch>();
        try
        {
            for (var pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageStarted = TelemetryOperation.StartTimestamp();
                var page = await GetPageMetadataScheduledAsync(document, pageIndex, cancellationToken).ConfigureAwait(false);
                var pageRequest = new PageTextRequest(
                    document.EngineSession.DocumentId,
                    page.Id,
                    pageIndex,
                    page.ContentRevision);
                var results = await ScheduleEngineOperationAsync(
                    document,
                    EngineJobClass.Search,
                    $"search:{pageIndex}:{query}:{matchCase}",
                    token => document.EngineSession.SearchPageAsync(
                        new PageSearchRequest(pageRequest, query, generation, matchCase), token),
                    cancellationToken).ConfigureAwait(false);
                foreach (var result in results)
                {
                    matches.Add(new TextMatch(
                        result.PageIndex,
                        result.CharIndex,
                        result.MatchLength,
                        result.Context,
                        result.HighlightRects.Select(static rectangle => new PdfRect(
                            checked((float)rectangle.Left),
                            checked((float)rectangle.Top),
                            checked((float)rectangle.Right),
                            checked((float)rectangle.Bottom))).ToArray()));
                }

                ElliePdfEventSource.Log.SearchPageCompleted(
                    operationId,
                    pageIndex,
                    TelemetryOperation.ElapsedMicroseconds(pageStarted),
                    results.Count);
                if (results.Count > 0)
                {
                    ElliePdfEventSource.Log.SearchResultPublished(operationId, matches.Count);
                }
            }

            ElliePdfEventSource.Log.Search(
                operationId,
                TelemetryOperation.ElapsedMicroseconds(started),
                matches.Count);
            return matches;
        }
        catch (OperationCanceledException)
        {
            ElliePdfEventSource.Log.SearchCancelled(operationId);
            throw;
        }
    }

    public async Task<(float Width, float Height)> GetPageSizeAsync(
        PdfDocumentSession document,
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(document);
        var page = await GetPageMetadataScheduledAsync(document, pageIndex, cancellationToken).ConfigureAwait(false);
        return (checked((float)page.SizeInPoints.Width), checked((float)page.SizeInPoints.Height));
    }

    public async Task<IReadOnlyList<PdfOutlineItem>> GetOutlineAsync(
        PdfDocumentSession document,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(document);
        var outline = await ScheduleEngineOperationAsync(
            document,
            EngineJobClass.Metadata,
            "outline",
            document.EngineSession.GetOutlineAsync,
            cancellationToken).ConfigureAwait(false);
        return outline.Items.Select(ConvertOutline).ToArray();
    }

    public Task<PdfMetadata> GetMetadataAsync(
        PdfDocumentSession document,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(document);
        return ScheduleEngineOperationAsync(
            document,
            EngineJobClass.Metadata,
            "metadata",
            document.EngineSession.GetMetadataAsync,
            cancellationToken);
    }

    public async Task<PageTextResult> GetPageTextAsync(
        PdfDocumentSession document,
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(document);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, document.PageCount);
        var page = await GetPageMetadataScheduledAsync(document, pageIndex, cancellationToken).ConfigureAwait(false);
        return await ScheduleEngineOperationAsync(
            document,
            EngineJobClass.Text,
            $"text:{pageIndex}:{page.ContentRevision.Value}",
            token => document.EngineSession.GetPageTextAsync(
                new PageTextRequest(document.EngineSession.DocumentId, page.Id, pageIndex, page.ContentRevision), token),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<PageLinks> GetPageLinksAsync(
        PdfDocumentSession document,
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(document);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, document.PageCount);
        return ScheduleEngineOperationAsync(
            document,
            EngineJobClass.Metadata,
            $"links:{pageIndex}",
            token => document.EngineSession.GetPageLinksAsync(pageIndex, token),
            cancellationToken);
    }

    public Task<FormWidgetsResult> GetFormWidgetsAsync(
        PdfDocumentSession document,
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(document);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, document.PageCount);
        return ScheduleEngineOperationAsync(
            document,
            EngineJobClass.Metadata,
            $"forms:{pageIndex}",
            token => document.EngineSession.GetFormWidgetsAsync(pageIndex, token),
            cancellationToken);
    }

    public Task<PdfPermissions> GetPermissionsAsync(
        PdfDocumentSession document,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(document);
        return ScheduleEngineOperationAsync(
            document,
            EngineJobClass.Metadata,
            "permissions",
            document.EngineSession.GetPermissionsAsync,
            cancellationToken);
    }

    public async Task ApplyFormValueAsync(
        PdfDocumentSession document,
        FormValueChange change,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(document);
        ArgumentNullException.ThrowIfNull(change);
        if (change.DocumentId != document.EngineSession.DocumentId)
            throw new ArgumentException("The form change belongs to another document.", nameof(change));
        await ScheduleEngineOperationAsync<bool>(
            document,
            EngineJobClass.Edit,
            $"form:{change.FieldId.Value}",
            async token =>
            {
                await document.EngineSession.ApplyFormValueAsync(change, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
        _ = AdvanceRenderGeneration(document);
    }

    public async Task RotatePageAsync(
        PdfDocumentSession document,
        int pageIndex,
        int quarterTurnsClockwise,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(document);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, document.PageCount);
        if (quarterTurnsClockwise is < -3 or > 3 || quarterTurnsClockwise == 0)
            throw new ArgumentOutOfRangeException(nameof(quarterTurnsClockwise));
        if (document.EngineSession is not IPdfPageMutationSession mutable)
            throw new NotSupportedException("This PDF engine session cannot rotate pages.");

        var page = await GetPageMetadataScheduledAsync(document, pageIndex, cancellationToken).ConfigureAwait(false);
        var snapshot = mutable.Snapshot;
        _ = await ScheduleEngineOperationAsync(
            document,
            EngineJobClass.Edit,
            $"rotate:{pageIndex}:{quarterTurnsClockwise}:{snapshot.ContentRevision.Value}",
            token => mutable.RotatePageAsync(
                new RotatePageRequest(
                    snapshot.Id,
                    page.Id,
                    snapshot.ContentRevision,
                    snapshot.StructureRevision,
                    page.ContentRevision,
                    quarterTurnsClockwise), token),
            cancellationToken).ConfigureAwait(false);
        _ = AdvanceRenderGeneration(document);
    }

    public async Task DeletePageAsync(
        PdfDocumentSession document,
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        ValidateDocument(document);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, document.PageCount);
        if (document.EngineSession is not IPdfPageMutationSession mutable)
            throw new NotSupportedException("This PDF engine session cannot delete pages.");

        var page = await GetPageMetadataScheduledAsync(document, pageIndex, cancellationToken).ConfigureAwait(false);
        var snapshot = mutable.Snapshot;
        var updated = await ScheduleEngineOperationAsync(
            document,
            EngineJobClass.Edit,
            $"delete:{pageIndex}:{snapshot.ContentRevision.Value}",
            token => mutable.DeletePageAsync(
                new DeletePageRequest(
                    snapshot.Id,
                    page.Id,
                    snapshot.ContentRevision,
                    snapshot.StructureRevision,
                    page.ContentRevision), token),
            cancellationToken).ConfigureAwait(false);
        document.PageCount = updated.PageCount;
        _ = AdvanceRenderGeneration(document);
    }

    public Task MergeDocumentsAsync(
        IReadOnlyList<PdfDocumentSession> sourceDocuments,
        string outputPath,
        CancellationToken cancellationToken = default) =>
        MergeOrderedPagesAsync(
            sourceDocuments.SelectMany(static document =>
                Enumerable.Range(0, document.PageCount).Select(pageIndex => (document, pageIndex))).ToArray(),
            outputPath,
            cancellationToken);

    public async Task MergeOrderedPagesAsync(
        IReadOnlyList<(PdfDocumentSession Document, int PageIndex)> pagesInOrder,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pagesInOrder);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (pagesInOrder.Count == 0)
            throw new ArgumentException("At least one page is required for an ordered merge.", nameof(pagesInOrder));
        if (_engineClient is not IPdfPageMergeClient mergeClient)
            throw new NotSupportedException("This PDF engine client cannot merge pages.");

        var exportPages = new List<PdfExportPage>(pagesInOrder.Count);
        foreach (var (document, pageIndex) in pagesInOrder)
        {
            ValidateDocument(document);
            ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, document.PageCount);
            if (document.EngineSession is not IPdfPageMutationSession mutable)
                throw new NotSupportedException("A source PDF engine session cannot provide stable page revisions.");

            var page = await GetPageMetadataScheduledAsync(document, pageIndex, cancellationToken).ConfigureAwait(false);
            var snapshot = mutable.Snapshot;
            exportPages.Add(new PdfExportPage(
                document,
                pageIndex,
                page.Id,
                snapshot.ContentRevision,
                snapshot.StructureRevision,
                page.ContentRevision));
        }

        await MergeOrderedPagesAsync(exportPages, outputPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task MergeOrderedPagesAsync(
        IReadOnlyList<PdfExportPage> pagesInOrder,
        string outputPath,
        CancellationToken cancellationToken = default,
        bool overwriteExisting = false)
    {
        ArgumentNullException.ThrowIfNull(pagesInOrder);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (pagesInOrder.Count == 0)
            throw new ArgumentException("At least one page is required for an ordered merge.", nameof(pagesInOrder));
        if (_engineClient is not IPdfPageMergeClient mergeClient)
            throw new NotSupportedException("This PDF engine client cannot merge pages.");

        var references = new List<PageMergeReference>(pagesInOrder.Count);
        foreach (var exportPage in pagesInOrder)
        {
            ArgumentNullException.ThrowIfNull(exportPage);
            var document = exportPage.Document;
            ValidateDocument(document);
            ArgumentOutOfRangeException.ThrowIfNegative(exportPage.PageIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(exportPage.PageIndex, document.PageCount);
            if (document.EngineSession is not IPdfPageMutationSession)
                throw new NotSupportedException("A source PDF engine session cannot provide stable page revisions.");

            var page = await GetPageMetadataScheduledAsync(document, exportPage.PageIndex, cancellationToken).ConfigureAwait(false);
            if (page.Id != exportPage.PageId)
                throw new InvalidOperationException("The Organizer source page identity is stale.");

            references.Add(new PageMergeReference(
                document.EngineSession.DocumentId,
                exportPage.PageId,
                exportPage.ExpectedContentRevision,
                exportPage.ExpectedStructureRevision,
                exportPage.ExpectedPageContentRevision,
                exportPage.Rotation));
        }

        var mergeRequest = new MergeOrderedPagesRequest(references);
        var expectedDestinationVersion = overwriteExisting
            ? await _fileVersionStampProvider.TryCaptureAsync(outputPath, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException(
                    "The Organizer overwrite destination no longer exists. Choose Save As instead.",
                    outputPath)
            : null;
        var saveRequest = new AtomicSaveRequest(
            outputPath,
            ContentRevision.Initial,
            ExpectedDestinationVersion: expectedDestinationVersion,
            FailIfDestinationExists: !overwriteExisting);
        var owner = pagesInOrder[0].Document;
        await ScheduleEngineOperationAsync(
            owner,
            EngineJobClass.Export,
            $"merge:{outputPath}:{references.Count}",
            async token =>
            {
                _ = await _atomicDocumentStore.CommitAsync(
                    saveRequest,
                    (stream, innerToken) => mergeClient.MergeOrderedPagesAsync(mergeRequest, stream, innerToken),
                    async (candidatePath, innerToken) =>
                        await ValidateSavedDocumentAsync(candidatePath, references.Count, innerToken).ConfigureAwait(false),
                    token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveDocumentAsync(
        PdfDocumentSession document,
        string outputPath,
        CancellationToken cancellationToken = default,
        ContentRevision? capturedRevision = null)
    {
        ValidateDocument(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (document.EngineSession is not IPdfWritableEngineSession writable)
        {
            throw new NotSupportedException("This PDF engine session cannot persist documents.");
        }

        var isSourceDestination = await DestinationAliasesSourceAsync(document, outputPath, cancellationToken).ConfigureAwait(false);
        var atomicRevision = capturedRevision ?? writable.Snapshot.ContentRevision;
        var request = new AtomicSaveRequest(
            outputPath,
            atomicRevision,
            isSourceDestination ? document.SourceVersion : null);
        var result = await ScheduleEngineOperationAsync(
            document,
            EngineJobClass.Save,
            $"save:{outputPath}:{atomicRevision.Value}",
            async token => await _atomicDocumentStore.CommitAsync(
                request,
                (stream, innerToken) => writable.SaveAsync(stream, writable.Snapshot.ContentRevision, innerToken),
                async (candidatePath, innerToken) =>
                {
                    await ValidateSavedDocumentAsync(candidatePath, document.PageCount, innerToken).ConfigureAwait(false);
                },
                token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        if (isSourceDestination)
        {
            document.UpdateSourceVersion(result.CommittedVersion);
        }
    }

    public async Task SaveDocumentWithOverlaysAsync(
        PdfDocumentSession document,
        PageOverlayDocument? overlays,
        string outputPath,
        CancellationToken cancellationToken = default,
        ContentRevision? capturedRevision = null)
    {
        if (overlays?.Pages.Values.Any(OverlayCompositor.HasContent) != true)
        {
            await SaveDocumentAsync(document, outputPath, cancellationToken, capturedRevision).ConfigureAwait(false);
            return;
        }

        await SaveAnnotationsCoreAsync(
            document,
            overlays,
            outputPath,
            flatten: false,
            cancellationToken,
            capturedRevision).ConfigureAwait(false);
    }

    public async Task SaveDocumentFlattenedCopyAsync(
        PdfDocumentSession document,
        PageOverlayDocument? overlays,
        string outputPath,
        CancellationToken cancellationToken = default,
        ContentRevision? capturedRevision = null)
    {
        ValidateDocument(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (await DestinationAliasesSourceAsync(document, outputPath, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "A flattened export must use a different destination so the editable source remains recoverable.");
        }
        await SaveAnnotationsCoreAsync(
            document,
            overlays ?? new PageOverlayDocument(),
            outputPath,
            flatten: true,
            cancellationToken,
            capturedRevision).ConfigureAwait(false);
    }

    private async Task SaveAnnotationsCoreAsync(
        PdfDocumentSession document,
        PageOverlayDocument overlays,
        string outputPath,
        bool flatten,
        CancellationToken cancellationToken,
        ContentRevision? capturedRevision)
    {
        ValidateDocument(document);
        ArgumentNullException.ThrowIfNull(overlays);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (document.EngineSession is not IPdfAnnotationPersistenceSession annotations)
        {
            throw new NotSupportedException("This PDF engine session cannot persist native annotations.");
        }

        var sourceDestination = !flatten
            && await DestinationAliasesSourceAsync(document, outputPath, cancellationToken).ConfigureAwait(false);
        var atomicRevision = capturedRevision ?? annotations.Snapshot.ContentRevision;
        var atomicRequest = new AtomicSaveRequest(
            outputPath,
            atomicRevision,
            sourceDestination ? document.SourceVersion : null);

        var result = await ScheduleEngineOperationAsync(
            document,
            EngineJobClass.Save,
            $"{(flatten ? "flatten" : "annotate")}:{outputPath}:{atomicRevision.Value}",
            async token =>
            {
                var annotationRequest = await CreateAnnotationSaveRequestAsync(
                    document,
                    annotations,
                    overlays,
                    token).ConfigureAwait(false);

                if (flatten)
                {
                    return await _atomicDocumentStore.CommitAsync(
                        atomicRequest,
                        (stream, innerToken) => annotations.SaveFlattenedCopyAsync(
                            annotationRequest,
                            stream,
                            innerToken),
                        async (candidatePath, innerToken) =>
                            await ValidateSavedDocumentAsync(
                                candidatePath,
                                document.PageCount,
                                innerToken).ConfigureAwait(false),
                        token).ConfigureAwait(false);
                }

                var staged = false;
                AtomicCommitResult commit;
                try
                {
                    commit = await _atomicDocumentStore.CommitAsync(
                        atomicRequest,
                        async (stream, innerToken) =>
                        {
                            _ = await annotations.StageAnnotationsAsync(
                                annotationRequest,
                                stream,
                                innerToken).ConfigureAwait(false);
                            staged = true;
                        },
                        async (candidatePath, innerToken) =>
                            await ValidateSavedDocumentAsync(
                                candidatePath,
                                document.PageCount,
                                innerToken).ConfigureAwait(false),
                        token).ConfigureAwait(false);
                }
                catch (Exception commitFailure)
                {
                    if (staged)
                    {
                        try
                        {
                            _ = await annotations.FinalizeAnnotationTransactionAsync(
                                annotationRequest.TransactionId,
                                committed: false,
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception rollbackFailure)
                        {
                            throw new AggregateException(
                                "The atomic annotation save failed and the worker rollback could not be confirmed.",
                                commitFailure,
                                rollbackFailure);
                        }
                    }

                    throw;
                }

                _ = await annotations.FinalizeAnnotationTransactionAsync(
                    annotationRequest.TransactionId,
                    committed: sourceDestination,
                    CancellationToken.None).ConfigureAwait(false);
                return commit;
            },
            cancellationToken).ConfigureAwait(false);

        if (sourceDestination)
        {
            document.UpdateSourceVersion(result.CommittedVersion);
            _ = AdvanceRenderGeneration(document);
        }
    }

    private static async ValueTask<PdfAnnotationSaveRequest> CreateAnnotationSaveRequestAsync(
        PdfDocumentSession document,
        IPdfAnnotationPersistenceSession annotations,
        PageOverlayDocument overlays,
        CancellationToken cancellationToken)
    {
        var snapshot = annotations.Snapshot;
        var batches = new List<PdfPageOverlayBatch>();
        foreach (var (pageIndex, overlay) in overlays.Pages.OrderBy(static pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OverlayCompositor.HasContent(overlay))
            {
                continue;
            }
            if (pageIndex < 0 || pageIndex >= document.PageCount)
            {
                throw new InvalidDataException("An overlay refers to a page outside the open document.");
            }

            var page = await document.EngineSession
                .GetPageMetadataAsync(pageIndex, cancellationToken)
                .ConfigureAwait(false);
            var ink = overlay.InkStrokes.Select(stroke =>
            {
                if (stroke.Points.Count < 2)
                {
                    throw new InvalidDataException("An ink annotation must contain at least two points.");
                }
                return new PdfInkAnnotation(
                    CreateStableAnnotationId("ink", stroke.Id),
                    stroke.Points.Select(static point => new PdfOverlayPoint(point.X, point.Y)),
                    ParseOverlayColor(stroke.ColorHex),
                    stroke.Thickness);
            }).ToArray();
            var text = overlay.TextItems.Select(item =>
            {
                if (string.IsNullOrWhiteSpace(item.Text))
                {
                    throw new InvalidDataException("A text stamp must contain visible text.");
                }
                return new PdfTextStampAnnotation(
                    CreateStableAnnotationId("text", item.Id),
                    new PdfOverlayRectangle(
                        item.X,
                        item.Y,
                        Math.Max(24, item.Width),
                        Math.Max(16, item.Height)),
                    item.Text,
                    item.FontSize,
                    ParseOverlayColor(item.ColorHex),
                    item.IsBold,
                    item.IsItalic);
            }).ToArray();
            var signatures = overlay.Signatures.Select(item =>
                new PdfSignatureStampAnnotation(
                    CreateStableAnnotationId("signature", item.Id),
                    new PdfOverlayRectangle(item.X, item.Y, item.Width, item.Height),
                    item.ImageBase64)).ToArray();
            batches.Add(new PdfPageOverlayBatch(
                pageIndex,
                page.Id,
                page.ContentRevision,
                ink,
                text,
                signatures));
        }

        if (batches.Count == 0)
        {
            throw new InvalidDataException("The overlay document contains no persistable annotations.");
        }
        return new PdfAnnotationSaveRequest(
            Guid.NewGuid(),
            snapshot.Id,
            snapshot.ContentRevision,
            snapshot.StructureRevision,
            batches);
    }

    private static string CreateStableAnnotationId(string kind, string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\0{sourceId}"));
        return $"ellie:{kind}:{Convert.ToHexString(hash)}";
    }

    private static PdfOverlayColor ParseOverlayColor(string? colorHex)
    {
        var hex = colorHex?.Trim().TrimStart('#');
        if (hex?.Length == 6
            && uint.TryParse(
                hex,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var rgb))
        {
            return new PdfOverlayColor(
                checked((byte)((rgb >> 16) & 0xff)),
                checked((byte)((rgb >> 8) & 0xff)),
                checked((byte)(rgb & 0xff)));
        }
        if (hex?.Length == 8
            && uint.TryParse(
                hex,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var argb))
        {
            return new PdfOverlayColor(
                checked((byte)((argb >> 16) & 0xff)),
                checked((byte)((argb >> 8) & 0xff)),
                checked((byte)(argb & 0xff)),
                checked((byte)((argb >> 24) & 0xff)));
        }

        return new PdfOverlayColor(0, 0, 0);
    }

    public Task CloseDocumentAsync(
        PdfDocumentSession document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_renderGate)
        {
            _renderSessions.Remove(document.EngineSession.DocumentId);
            _renderGenerations.Remove(document.EngineSession.DocumentId);
        }
        _renderScheduler.CloseDocument(document.EngineSession.DocumentId);
        _engineScheduler.CloseDocument(document.EngineSession.DocumentId);
        return document.CloseEngineSessionAsync();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _renderScheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (_ownsEngineScheduler)
        {
            _engineScheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        lock (_renderGate)
        {
            _renderSessions.Clear();
            _renderGenerations.Clear();
            _tileFlights.Clear();
            _renderTelemetry.Clear();
            _cpuTileCache.Clear();
        }
    }

    private async ValueTask<IPixelBufferLease> ExecuteRenderAsync(
        RenderRequest request,
        CancellationToken cancellationToken)
    {
        IPdfEngineSession session;
        RenderTelemetryActivity? activity;
        lock (_renderGate)
        {
            if (!_renderSessions.TryGetValue(request.Key.DocumentId, out session!))
            {
                throw new ObjectDisposedException(nameof(PdfDocumentSession));
            }

            _renderTelemetry.TryGetValue(
                new TileFlightKey(request.Key, request.Generation),
                out activity);
        }

        var operationId = activity?.OperationId ?? TelemetryOperation.NextId();
        if (activity is not null)
        {
            var queueWait = TelemetryOperation.ElapsedMicroseconds(activity.Queued);
            Interlocked.Exchange(ref _benchmarkLastRenderQueueWaitMicroseconds, queueWait);
            ElliePdfEventSource.Log.RenderQueueWait(
                operationId,
                queueWait);
        }
        ElliePdfEventSource.Log.RenderStarted(
            operationId,
            request.Key.Tile.InteriorWidth + checked(request.Key.Tile.BleedPixels * 2),
            request.Key.Tile.InteriorHeight + checked(request.Key.Tile.BleedPixels * 2));
        var started = TelemetryOperation.StartTimestamp();
        try
        {
            var lease = await session.RenderAsync(request, cancellationToken).ConfigureAwait(false);
            var duration = TelemetryOperation.ElapsedMicroseconds(started);
            ElliePdfEventSource.Log.NativeRender(
                operationId,
                duration,
                lease.Width,
                lease.Height,
                true);
            ElliePdfEventSource.Log.PdfiumCallDuration(operationId, duration, 1);
            ElliePdfEventSource.Log.RenderCompleted(operationId, duration, lease.ByteLength);
            return lease;
        }
        catch (OperationCanceledException)
        {
            ElliePdfEventSource.Log.RenderCancelled(operationId);
            throw;
        }
        catch
        {
            ElliePdfEventSource.Log.NativeRender(
                operationId,
                TelemetryOperation.ElapsedMicroseconds(started),
                0,
                0,
                false);
            ElliePdfEventSource.Log.WorkerFailure(operationId, 2);
            throw;
        }
    }

    private Task<CachedTile> GetOrRenderTileAsync(
        RenderRequest request,
        RenderJobOptions options,
        CancellationToken cancellationToken)
    {
        Task<CachedTile> flight;
        var flightKey = new TileFlightKey(request.Key, request.Generation);
        lock (_renderGate)
        {
            if (_cpuTileCache.TryGet(request.Key, out var cached) && cached is not null)
            {
                var operationId = TelemetryOperation.NextId();
                ElliePdfEventSource.Log.CacheHit(operationId, cached.Pixels.LongLength);
                ElliePdfEventSource.Log.CacheBytes(operationId, _cpuTileCache.ResidentBytes);
                return Task.FromResult(cached);
            }

            if (_tileFlights.TryGetValue(flightKey, out flight!))
            {
                return flight.WaitAsync(cancellationToken);
            }

            var activity = new RenderTelemetryActivity(
                TelemetryOperation.NextId(),
                TelemetryOperation.StartTimestamp());
            _renderTelemetry[flightKey] = activity;
            ElliePdfEventSource.Log.CacheMiss(activity.OperationId);
            ElliePdfEventSource.Log.RenderQueued(activity.OperationId, (int)request.Priority);
            flight = RenderAndCacheTileAsync(request, options, cancellationToken);
            _tileFlights.Add(flightKey, flight);
        }

        return flight.WaitAsync(cancellationToken);
    }

    private async Task<CachedTile> RenderAndCacheTileAsync(
        RenderRequest request,
        RenderJobOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _renderScheduler
                .ScheduleAsync(request, options, cancellationToken)
                .ConfigureAwait(false);
            var activityOperationId = GetRenderOperationId(request);
            if (result.Status is RenderJobCompletionStatus.Stale or RenderJobCompletionStatus.Rejected
                or RenderJobCompletionStatus.Evicted or RenderJobCompletionStatus.Closed)
            {
                ElliePdfEventSource.Log.RenderRejectedAsStale(activityOperationId);
            }
            else if (result.Status is RenderJobCompletionStatus.Cancelled or RenderJobCompletionStatus.DeadlineExceeded)
            {
                ElliePdfEventSource.Log.RenderCancelled(activityOperationId);
            }
            if (result.Status == RenderJobCompletionStatus.Faulted)
            {
                throw result.Error ?? new IOException("The PDF worker could not render the requested tile.");
            }
            if (!result.IsPublicationEligible || result.Lease is null)
            {
                throw new OperationCanceledException(
                    $"The tile was not publication eligible ({result.Status}).",
                    cancellationToken);
            }

            await using var lease = result.Lease;
            if (lease is not IReadablePixelBufferLease readable
                || lease.Format != PixelFormat.Bgra8Premultiplied
                || lease.ByteLength > PdfContractLimits.MaxPixelBufferBytes
                || lease.Stride < checked(lease.Width * 4)
                || lease.ByteLength < checked(lease.Stride * lease.Height))
            {
                throw new InvalidDataException("The worker returned an invalid tile lease.");
            }

            UncachedLeaseGate.LeaseReservation reservation;
            lock (_renderGate)
            {
                if (!_uncachedLeaseGate.TryAcquire(lease.ByteLength, out reservation))
                {
                    throw new PdfResourceLimitException(
                        "The uncached tile lease budget is temporarily exhausted.");
                }
            }

            using (reservation)
            {
                var pixels = GC.AllocateUninitializedArray<byte>(lease.ByteLength);
                var uploadStarted = TelemetryOperation.StartTimestamp();
                await using var stream = readable.OpenReadStream();
                await stream.ReadExactlyAsync(pixels, cancellationToken).ConfigureAwait(false);
                RenderTelemetryActivity? activity;
                lock (_renderGate)
                {
                    _renderTelemetry.TryGetValue(
                        new TileFlightKey(request.Key, request.Generation),
                        out activity);
                }
                ElliePdfEventSource.Log.PixelUploadDuration(
                    activity?.OperationId ?? TelemetryOperation.NextId(),
                    TelemetryOperation.ElapsedMicroseconds(uploadStarted),
                    pixels.LongLength);
                var tile = new CachedTile(pixels, lease.Width, lease.Height, lease.Stride);
                lock (_renderGate)
                {
                    _cpuTileCache.Set(request.Key, tile, pixels.LongLength);
                    ElliePdfEventSource.Log.CacheBytes(
                        activity?.OperationId ?? TelemetryOperation.NextId(),
                        _cpuTileCache.ResidentBytes);
                }

                return tile;
            }
        }
        finally
        {
            lock (_renderGate)
            {
                var flightKey = new TileFlightKey(request.Key, request.Generation);
                _tileFlights.Remove(flightKey);
                _renderTelemetry.Remove(flightKey);
            }
        }
    }

    private int GetRenderOperationId(RenderRequest request)
    {
        lock (_renderGate)
        {
            return _renderTelemetry.TryGetValue(
                new TileFlightKey(request.Key, request.Generation),
                out var activity)
                ? activity.OperationId
                : TelemetryOperation.NextId();
        }
    }

    private RenderGeneration CurrentGeneration(PdfDocumentSession document)
    {
        lock (_renderGate)
        {
            return _renderGenerations.GetValueOrDefault(
                document.EngineSession.DocumentId,
                RenderGeneration.Initial);
        }
    }

    private Task<PageMetadata> GetPageMetadataScheduledAsync(
        PdfDocumentSession document,
        int pageIndex,
        CancellationToken cancellationToken)
        => ScheduleEngineOperationAsync(
            document,
            EngineJobClass.Metadata,
            $"page-metadata:{pageIndex}",
            token => document.EngineSession.GetPageMetadataAsync(pageIndex, token),
            cancellationToken);

    private async Task<T> ScheduleEngineOperationAsync<T>(
        PdfDocumentSession document,
        EngineJobClass jobClass,
        string identity,
        Func<CancellationToken, ValueTask<T>> execute,
        CancellationToken cancellationToken)
    {
        var request = new EngineJobRequest(
            document.EngineSession.DocumentId,
            jobClass,
            identity,
            CurrentGeneration(document),
            DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2));
        var result = await _engineScheduler
            .ScheduleAsync(request, execute, cancellationToken)
            .ConfigureAwait(false);
        if (result.Status == RenderJobCompletionStatus.Faulted)
            throw result.Error ?? new IOException($"The PDF engine operation '{jobClass}' failed.");
        if (result.Status != RenderJobCompletionStatus.Published || result.Value is null)
            throw new OperationCanceledException(
                $"The PDF engine operation was not publication eligible ({result.Status}).",
                cancellationToken);
        return result.Value;
    }

    private async Task ValidateSavedDocumentAsync(
        string candidatePath,
        int expectedPageCount,
        CancellationToken cancellationToken)
    {
        await using var validation = await _engineClient.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(candidatePath)),
            cancellationToken).ConfigureAwait(false);
        var metadata = await validation.GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        if (metadata.PageCount != expectedPageCount)
        {
            throw new InvalidDataException(
                $"Saved PDF page count mismatch. Expected {expectedPageCount}, found {metadata.PageCount}.");
        }
    }

    private async ValueTask<bool> DestinationAliasesSourceAsync(
        PdfDocumentSession document,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        if (string.Equals(
            Path.GetFullPath(document.SourcePath),
            Path.GetFullPath(destinationPath),
            StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var destinationVersion = await _fileVersionStampProvider
            .TryCaptureAsync(destinationPath, cancellationToken)
            .ConfigureAwait(false);
        return document.SourceVersion.IdentifiesSameFile(destinationVersion);
    }

    private static async Task CopyLeaseInteriorAsync(
        IPixelBufferLease lease,
        byte[] destination,
        int pageWidth,
        int pageHeight,
        TileAddress tile,
        CancellationToken cancellationToken)
    {
        if (lease is not IReadablePixelBufferLease readable
            || lease.Format != PixelFormat.Bgra8Premultiplied)
        {
            throw new InvalidDataException("The worker returned an unreadable pixel lease.");
        }
        if (lease.ByteLength > PdfContractLimits.MaxPixelBufferBytes
            || lease.Stride < lease.Width * 4
            || lease.ByteLength < checked(lease.Stride * lease.Height))
        {
            throw new InvalidDataException("The worker returned an invalid pixel layout.");
        }

        var source = GC.AllocateUninitializedArray<byte>(lease.ByteLength);
        await using (var stream = readable.OpenReadStream())
        {
            await stream.ReadExactlyAsync(source, cancellationToken).ConfigureAwait(false);
        }

        var interiorWidth = Math.Min(tile.InteriorWidth, pageWidth - tile.X);
        var interiorHeight = Math.Min(tile.InteriorHeight, pageHeight - tile.Y);
        var leftBleed = tile.X > 0 ? tile.BleedPixels : 0;
        var topBleed = tile.Y > 0 ? tile.BleedPixels : 0;
        var destinationStride = checked(pageWidth * 4);
        var copyBytes = checked(interiorWidth * 4);
        for (var row = 0; row < interiorHeight; row++)
        {
            var sourceOffset = checked((topBleed + row) * lease.Stride + leftBleed * 4);
            var destinationOffset = checked((tile.Y + row) * destinationStride + tile.X * 4);
            System.Buffer.BlockCopy(source, sourceOffset, destination, destinationOffset, copyBytes);
        }
    }

    private static PdfOutlineItem ConvertOutline(OutlineItem item) => new(
        item.Title,
        item.DestinationPageIndex ?? 0,
        item.Children.Select(ConvertOutline).ToArray());

    private static int CheckedDimension(double points, double scale)
    {
        if (!double.IsFinite(points) || !double.IsFinite(scale) || points <= 0 || scale <= 0)
            throw new PdfResourceLimitException("The page raster geometry is invalid.");
        var value = Math.Ceiling(points * scale);
        if (value is < 1 or > PdfContractLimits.MaxPixelDimension)
            throw new PdfResourceLimitException("The page raster dimension exceeds the configured limit.");
        return checked((int)value);
    }

    private static int CheckedPageDimension(double points, double scale)
    {
        if (!double.IsFinite(points) || !double.IsFinite(scale) || points <= 0 || scale <= 0)
            throw new PdfResourceLimitException("The page raster geometry is invalid.");
        var value = Math.Ceiling(points * scale);
        if (value is < 1 or > int.MaxValue)
            throw new PdfResourceLimitException("The page raster geometry exceeds the supported coordinate range.");
        return checked((int)value);
    }

    private static async Task<byte[]> EncodeBitmapToPngAsync(
        byte[] packedPixels,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        using var randomAccessStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, randomAccessStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            checked((uint)width),
            checked((uint)height),
            96,
            96,
            packedPixels);
        await encoder.FlushAsync();
        randomAccessStream.Seek(0);
        using var readStream = randomAccessStream.AsStreamForRead();
        using var output = new MemoryStream();
        await readStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    private static void ValidateDocument(PdfDocumentSession document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ObjectDisposedException.ThrowIf(document.IsClosed, document);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed record CachedTile(byte[] Pixels, int Width, int Height, int Stride);
    private readonly record struct TileFlightKey(RenderKey Key, RenderGeneration Generation);

    private sealed record RenderTelemetryActivity(int OperationId, long Queued);
}

public sealed class PdfResourceLimitException : IOException
{
    public PdfResourceLimitException(string message) : base(message) { }
}
