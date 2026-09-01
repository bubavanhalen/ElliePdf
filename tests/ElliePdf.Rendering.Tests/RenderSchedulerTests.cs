using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;
using ElliePdf.Rendering;

namespace ElliePdf.Rendering.Tests;

public sealed class RenderSchedulerTests
{
    [Fact]
    public async Task HigherPriorityWorkRunsBeforeOtherQueuedWork()
    {
        var calls = new List<EngineJobPriority>();
        var started = NewSignal();
        var release = NewSignal();
        await using var scheduler = new RenderScheduler(async (request, _) =>
        {
            calls.Add(request.Priority);
            if (calls.Count == 1) { started.SetResult(true); await release.Task; }
            return Lease(request);
        });

        var first = scheduler.EnqueueAsync(Request(EngineJobPriority.Background));
        await started.Task;
        var low = scheduler.EnqueueAsync(Request(EngineJobPriority.DirectionalPrefetch));
        var high = scheduler.EnqueueAsync(Request(EngineJobPriority.VisibleInteractionCritical));
        release.SetResult(true);

        await Task.WhenAll(first, low, high);
        Assert.Equal(new[] { EngineJobPriority.Background, EngineJobPriority.VisibleInteractionCritical, EngineJobPriority.DirectionalPrefetch }, calls);
    }

    [Fact]
    public async Task SamePriorityUsesRoundRobinBetweenDocuments()
    {
        var calls = new List<DocumentId>();
        var started = NewSignal();
        var release = NewSignal();
        await using var scheduler = new RenderScheduler(async (request, _) =>
        {
            calls.Add(request.Key.DocumentId);
            if (calls.Count == 1) { started.SetResult(true); await release.Task; }
            return Lease(request);
        });

        var a = DocumentId.New();
        var b = DocumentId.New();
        var first = scheduler.EnqueueAsync(Request(EngineJobPriority.OtherVisible, a));
        await started.Task;
        var a2 = scheduler.EnqueueAsync(Request(EngineJobPriority.OtherVisible, a, page: 2));
        var b1 = scheduler.EnqueueAsync(Request(EngineJobPriority.OtherVisible, b));
        release.SetResult(true);
        await Task.WhenAll(first, a2, b1);
        Assert.Equal(new[] { a, b, a }, calls);
    }

    [Fact]
    public async Task DuplicateKeySharesOneEngineOperation()
    {
        var calls = 0;
        var started = NewSignal();
        var release = NewSignal();
        await using var scheduler = new RenderScheduler(async (request, _) =>
        {
            Interlocked.Increment(ref calls);
            started.SetResult(true);
            await release.Task;
            return Lease(request);
        });
        var request = Request(EngineJobPriority.OtherVisible);
        var one = scheduler.EnqueueAsync(request);
        var two = scheduler.EnqueueAsync(request);
        await started.Task;
        release.SetResult(true);
        var results = await Task.WhenAll(one, two);
        Assert.Equal(1, calls);
        Assert.All(results, result => Assert.True(result.IsPublicationEligible));
    }

    [Fact]
    public async Task FullQueueEvictsOldestBackgroundButPreservesVisibleWork()
    {
        var started = NewSignal();
        var release = NewSignal();
        await using var scheduler = new RenderScheduler(async (request, _) =>
        {
            started.TrySetResult(true);
            await release.Task;
            return Lease(request);
        }, capacity: 2, documentQuota: 8);

        var running = scheduler.EnqueueAsync(Request(EngineJobPriority.OtherVisible));
        await started.Task;
        var old = scheduler.EnqueueAsync(Request(EngineJobPriority.Background, page: 1));
        var queued = scheduler.EnqueueAsync(Request(EngineJobPriority.Background, page: 2));
        var visible = scheduler.EnqueueAsync(Request(EngineJobPriority.VisibleInteractionCritical, page: 3));
        var oldResult = await old;
        Assert.Equal(RenderJobCompletionStatus.Evicted, oldResult.Status);
        release.SetResult(true);
        var results = await Task.WhenAll(running, queued, visible);
        Assert.All(results, result => Assert.True(result.IsPublicationEligible));
    }

    [Fact]
    public async Task PerDocumentPendingQuotaRejectsAdditionalBackgroundWork()
    {
        var started = NewSignal();
        var release = NewSignal();
        await using var scheduler = new RenderScheduler(async (request, _) =>
        {
            started.TrySetResult(true);
            await release.Task;
            return Lease(request);
        }, capacity: 8, documentQuota: 2);
        var document = DocumentId.New();
        var running = scheduler.EnqueueAsync(Request(EngineJobPriority.OtherVisible, document));
        await started.Task;
        var first = scheduler.EnqueueAsync(Request(EngineJobPriority.Background, document, 1));
        var second = scheduler.EnqueueAsync(Request(EngineJobPriority.Background, document, 2));
        var rejected = await scheduler.EnqueueAsync(Request(EngineJobPriority.Background, document, 3));
        Assert.Equal(RenderJobCompletionStatus.Rejected, rejected.Status);
        release.SetResult(true);
        await Task.WhenAll(running, first, second);
    }

    [Fact]
    public async Task LatestGenerationSuppressesQueuedAndCompletedPixels()
    {
        var started = NewSignal();
        var release = NewSignal();
        FakeLease? lease = null;
        await using var scheduler = new RenderScheduler(async (request, _) =>
        {
            started.SetResult(true);
            await release.Task;
            return lease = Lease(request);
        });
        var document = DocumentId.New();
        var old = scheduler.EnqueueAsync(Request(EngineJobPriority.OtherVisible, document));
        await started.Task;
        scheduler.AdvanceGeneration(document, new RenderGeneration(1));
        release.SetResult(true);
        var result = await old;
        Assert.Equal(RenderJobCompletionStatus.Stale, result.Status);
        Assert.Null(result.Lease);
        Assert.Equal(1, lease!.DisposeCount);
    }

    [Fact]
    public async Task NewGenerationWithSameKeyGetsAFreshSingleFlight()
    {
        var started = NewSignal();
        var release = NewSignal();
        var calls = 0;
        await using var scheduler = new RenderScheduler(async (request, cancellation) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1) { started.SetResult(true); await release.Task; }
            return Lease(request);
        });
        var document = DocumentId.New();
        var oldRequest = Request(EngineJobPriority.OtherVisible, document);
        var old = scheduler.EnqueueAsync(oldRequest);
        await started.Task;
        scheduler.AdvanceGeneration(document, new RenderGeneration(1));
        var newerRequest = new RenderRequest(oldRequest.Key, new RenderGeneration(1), RenderQuality.Standard, EngineJobPriority.OtherVisible, DateTimeOffset.UtcNow.AddMinutes(1));
        var newer = scheduler.EnqueueAsync(newerRequest);
        release.SetResult(true);
        var result = await newer;
        await old;
        Assert.Equal(RenderJobCompletionStatus.Published, result.Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ClosedDocumentCannotPublish()
    {
        var started = NewSignal();
        var release = NewSignal();
        await using var scheduler = new RenderScheduler(async (request, _) =>
        {
            started.SetResult(true);
            await release.Task;
            return Lease(request);
        });
        var document = DocumentId.New();
        var operation = scheduler.EnqueueAsync(Request(EngineJobPriority.OtherVisible, document));
        await started.Task;
        scheduler.CloseDocument(document);
        release.SetResult(true);
        var result = await operation;
        Assert.NotEqual(RenderJobCompletionStatus.Published, result.Status);
        Assert.Null(result.Lease);
    }

    [Fact]
    public async Task CancellationBeforeExecutionDoesNotInvokeEngine()
    {
        var started = NewSignal();
        var release = NewSignal();
        var calls = 0;
        await using var scheduler = new RenderScheduler(async (request, _) =>
        {
            Interlocked.Increment(ref calls);
            started.SetResult(true);
            await release.Task;
            return Lease(request);
        });
        var first = scheduler.EnqueueAsync(Request(EngineJobPriority.OtherVisible));
        await started.Task;
        using var cancellation = new CancellationTokenSource();
        var second = scheduler.EnqueueAsync(Request(EngineJobPriority.OtherVisible, page: 1), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(async () => await second);
        release.SetResult(true);
        await first;
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task BusyCountIsReferenceCountedAcrossPendingAndRunningJobs()
    {
        var started = NewSignal();
        var release = NewSignal();
        await using var scheduler = new RenderScheduler(async (request, _) =>
        {
            started.SetResult(true);
            await release.Task;
            return Lease(request);
        });
        var one = scheduler.EnqueueAsync(Request(EngineJobPriority.OtherVisible));
        await started.Task;
        var two = scheduler.EnqueueAsync(Request(EngineJobPriority.OtherVisible, page: 1));
        Assert.Equal(2, scheduler.BusyCount);
        release.SetResult(true);
        await Task.WhenAll(one, two);
        Assert.Equal(0, scheduler.BusyCount);
    }

    private static RenderRequest Request(EngineJobPriority priority, DocumentId? document = null, int page = 0)
    {
        var documentId = document ?? DocumentId.New();
        var pageId = new PageId(new Guid(page + 1, 0, 0, new byte[8]));
        var key = new RenderKey(documentId, pageId, PageContentRevision.Initial, PageAppearanceRevision.Initial,
            new TileAddress(0, 0, 16, 16, 0), new RasterScale64(64), PageRotation.None, RenderMode.Normal);
        return new RenderRequest(key, RenderGeneration.Initial, RenderQuality.Standard, priority, DateTimeOffset.UtcNow.AddMinutes(1));
    }

    private static FakeLease Lease(RenderRequest request) => new(request.Key);
    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FakeLease(RenderKey key) : IPixelBufferLease
    {
        public Guid LeaseId { get; } = Guid.NewGuid();
        public string SharedMemoryId => "test";
        public long Offset => 0;
        public int ByteLength => 64 * 16;
        public int Width => 16;
        public int Height => 16;
        public int Stride => 64;
        public PixelFormat Format => PixelFormat.Bgra8Premultiplied;
        public RenderKey Key => key;
        public int DisposeCount { get; private set; }
        public ValueTask DisposeAsync() { DisposeCount++; return ValueTask.CompletedTask; }
    }
}
