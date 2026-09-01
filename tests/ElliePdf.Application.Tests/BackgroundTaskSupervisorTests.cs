using Xunit;

namespace ElliePdf.Application.Tests;

public sealed class BackgroundTaskSupervisorTests
{
    [Fact]
    public async Task Fault_from_unawaited_task_is_observed_and_recorded()
    {
        await using var supervisor = new BackgroundTaskSupervisor();
        Task task = supervisor.Start(static () => Task.FromException(new InvalidOperationException("expected")), "test");

        await supervisor.WaitForIdleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await task);

        BackgroundTaskFault fault = Assert.Single(supervisor.Faults);
        Assert.Equal("test", fault.Name);
        Assert.IsType<InvalidOperationException>(fault.Exception);
    }
}
