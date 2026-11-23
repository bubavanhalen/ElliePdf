using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;
using ElliePdf.Rendering;

namespace ElliePdf.Rendering.Tests;

public sealed class EngineJobSchedulerTests
{
    [Fact]
    public void JobClassesFollowTheExecutionSpecPriorityOrder()
    {
        Assert.Equal(EngineJobPriority.VisibleInteractionCritical, EngineJobClass.VisibleRender.Priority());
        Assert.Equal(EngineJobPriority.OtherVisible, EngineJobClass.Text.Priority());
        Assert.Equal(EngineJobPriority.DirectionalOverscan, EngineJobClass.DirectionalOverscan.Priority());
        Assert.Equal(EngineJobPriority.VisibleThumbnail, EngineJobClass.Metadata.Priority());
        Assert.Equal(EngineJobPriority.DirectionalPrefetch, EngineJobClass.DirectionalPrefetch.Priority());
        Assert.Equal(EngineJobPriority.Background, EngineJobClass.Search.Priority());
        Assert.Equal(EngineJobPriority.OtherVisible, EngineJobClass.Print.Priority());
        Assert.Equal(EngineJobPriority.OtherVisible, EngineJobClass.Export.Priority());
        Assert.Equal(EngineJobPriority.OtherVisible, EngineJobClass.Edit.Priority());
        Assert.Equal(EngineJobPriority.OtherVisible, EngineJobClass.Save.Priority());
    }

    [Fact]
    public async Task DuplicateOperationIdentitySharesOneCancellableFlight()
    {
        var started = NewSignal();
        var release = NewSignal();
        var calls = 0;
        await using var scheduler = new EngineJobScheduler();
        var request = Request(EngineJobClass.Search, "page:0:query");
        async ValueTask<string> Execute(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            started.SetResult(true);
            await release.Task.WaitAsync(token);
            return "match";
        }

        var first = scheduler.ScheduleAsync(request, Execute);
        var second = scheduler.ScheduleAsync(request, Execute);
        await started.Task;
        release.SetResult(true);
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, calls);
        Assert.All(results, result =>
        {
            Assert.Equal(RenderJobCompletionStatus.Published, result.Status);
            Assert.Equal("match", result.Value);
        });
    }

    [Fact]
    public async Task GenerationAdvanceSuppressesInFlightOperationResult()
    {
        var started = NewSignal();
        var release = NewSignal();
        await using var scheduler = new EngineJobScheduler();
        var document = DocumentId.New();
        var request = Request(EngineJobClass.Text, "page:0:text", document);
        var operation = scheduler.ScheduleAsync(request, async token =>
        {
            started.SetResult(true);
            await release.Task.WaitAsync(token);
            return 42;
        });
        await started.Task;
        scheduler.AdvanceGeneration(document, new RenderGeneration(1));
        release.SetResult(true);

        var result = await operation;
        Assert.Equal(RenderJobCompletionStatus.Stale, result.Status);
        Assert.Equal(0, result.Value);
    }

    [Fact]
    public async Task CancelledOnlyWaiterDoesNotStartQueuedOperation()
    {
        await using var scheduler = new EngineJobScheduler();
        var started = NewSignal();
        var release = NewSignal();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var blocker = scheduler.ScheduleAsync(
            Request(EngineJobClass.OtherVisible, "blocker"),
            async token =>
            {
                started.SetResult(true);
                await release.Task.WaitAsync(token);
                return 0;
            });
        await started.Task;
        var request = Request(EngineJobClass.BackgroundThumbnail, "page:0:thumbnail");
        var operation = scheduler.ScheduleAsync(request, token =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(1);
        }, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await operation);
        release.SetResult(true);
        await blocker;
        Assert.Equal(0, calls);
    }

    private static EngineJobRequest Request(EngineJobClass jobClass, string identity, DocumentId? document = null)
        => new(document ?? DocumentId.New(), jobClass, identity, RenderGeneration.Initial, DateTimeOffset.UtcNow.AddMinutes(1));

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
