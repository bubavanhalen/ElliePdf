using System.Collections.Concurrent;
using ElliePdf.Services;
using Xunit;

namespace ElliePdf.UI.AutomationTests;

public sealed class FileActivationRelayTests
{
    [Fact]
    public async Task Concurrent_launches_deliver_every_path_to_the_same_reader()
    {
        var firstLaunch = new[] { @"C:\Public PDFs\RFC 9110.pdf", @"C:\Public PDFs\Lesezeichen ü.pdf" };
        var secondLaunch = new[] { @"C:\Public PDFs\Another document.pdf" };
        var received = new ConcurrentQueue<string[]>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var instance = (uint)Environment.ProcessId;
        var listener = FileActivationRelay.ListenAsync(instance, files =>
        {
            received.Enqueue(files);
            return true;
        }, cancellation.Token);

        try
        {
            await Task.WhenAll(FileActivationRelay.SendAsync(instance, firstLaunch),
                FileActivationRelay.SendAsync(instance, secondLaunch));
            Assert.Equal(2, received.Count);
            Assert.Contains(received, files => files.SequenceEqual(firstLaunch));
            Assert.Contains(received, files => files.SequenceEqual(secondLaunch));
        }
        finally
        {
            await cancellation.CancelAsync();
            await listener;
        }
    }
}
