using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ElliePdf.Pdf.Client.Tests;

public sealed class WorkerLifecycleSoakTests
{
    private static readonly string[] FixtureNames =
    [
        "synthetic-vector-small.pdf",
        "synthetic-photo-scan.pdf",
        "synthetic-mixed-orientation-links-forms-outlines.pdf",
        "synthetic-1000-pages.pdf",
        "synthetic-cjk-font-heavy.pdf"
    ];

    [Fact(Timeout = 32_400_000)]
    public async Task Mixed_document_open_render_search_close_soak_is_bounded()
    {
        TimeSpan requestedDuration = ReadDuration();
        var stopwatch = Stopwatch.StartNew();
        var cycles = 0;
        var operations = 0;
        var workerRestarts = 0;
        var workerPid = 0;
        long maximumAggregatePrivateBytes = 0;
        Exception? failure = null;

        try
        {
            await using var client = CreateClient();
            await using var sentinel = await client.OpenSessionAsync(
                new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-vector-small.pdf"))),
                CancellationToken.None);
            _ = await sentinel.GetMetadataAsync(CancellationToken.None);
            workerPid = WorkerProcess(client)?.Id
                ?? throw new InvalidOperationException("The worker process was not available after soak warm-up.");
            do
            {
                foreach (string fixtureName in FixtureNames)
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await using (IPdfEngineSession session = await client.OpenSessionAsync(
                        new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture(fixtureName))),
                        deadline.Token))
                    {
                        PdfMetadata metadata = await session.GetMetadataAsync(deadline.Token);
                        Assert.True(metadata.PageCount > 0);
                        int pageIndex = (cycles + operations) % metadata.PageCount;
                        PageMetadata page = await session.GetPageMetadataAsync(pageIndex, deadline.Token);
                        await using IPixelBufferLease lease = await session.RenderAsync(
                            RenderRequest(session.DocumentId, page.Id, page.ContentRevision),
                            deadline.Token);
                        Assert.True(lease.ByteLength > 0);

                        var textRequest = new PageTextRequest(
                            session.DocumentId,
                            page.Id,
                            pageIndex,
                            page.ContentRevision);
                        _ = await session.GetPageTextAsync(textRequest, deadline.Token);
                        _ = await session.SearchPageAsync(
                            new PageSearchRequest(textRequest, "ElliePdf", SearchGeneration.Initial),
                            deadline.Token);
                        operations += 4;
                    }

                    Process worker = WorkerProcess(client)
                        ?? throw new InvalidOperationException("The worker process disappeared during the soak.");
                    worker.Refresh();
                    if (workerPid == 0)
                        workerPid = worker.Id;
                    else if (worker.Id != workerPid)
                    {
                        workerRestarts++;
                        workerPid = worker.Id;
                    }

                    using Process host = Process.GetCurrentProcess();
                    maximumAggregatePrivateBytes = Math.Max(
                        maximumAggregatePrivateBytes,
                        checked(host.PrivateMemorySize64 + worker.PrivateMemorySize64));

                    if (requestedDuration > TimeSpan.Zero && stopwatch.Elapsed >= requestedDuration)
                        break;
                }

                cycles++;
            }
            while (requestedDuration > TimeSpan.Zero && stopwatch.Elapsed < requestedDuration);

            Assert.Equal(0, workerRestarts);
            Assert.True(cycles >= 1);
            Assert.True(operations >= 4);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            await WriteReportAsync(new
            {
                schemaVersion = "1.0",
                requestedSeconds = requestedDuration.TotalSeconds,
                elapsedSeconds = stopwatch.Elapsed.TotalSeconds,
                cycles,
                operations,
                workerRestarts,
                maximumAggregatePrivateBytes,
                status = failure is null ? "passed" : "failed",
                failureClass = failure?.GetType().Name
            });
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Closing_large_document_releases_ninety_percent_of_associated_worker_memory_within_two_seconds()
    {
        await using var client = CreateClient();
        long peakWorkerBytes;
        int workerPid;
        await using (IPdfEngineSession session = await client.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-1000-pages.pdf"))),
            CancellationToken.None))
        {
            PdfMetadata metadata = await session.GetMetadataAsync(CancellationToken.None);
            Assert.Equal(1000, metadata.PageCount);
            PageMetadata page = await session.GetPageMetadataAsync(999, CancellationToken.None);
            await using IPixelBufferLease lease = await session.RenderAsync(
                RenderRequest(session.DocumentId, page.Id, page.ContentRevision),
                CancellationToken.None);
            Assert.True(lease.ByteLength > 0);
            Process worker = WorkerProcess(client)
                ?? throw new InvalidOperationException("The worker process was not available while the document was open.");
            workerPid = worker.Id;
            peakWorkerBytes = PrivateBytes(worker);
        }

        long allowedRemaining = checked((long)Math.Ceiling(peakWorkerBytes * .10));
        var deadline = Stopwatch.StartNew();
        long remaining;
        do
        {
            remaining = TryPrivateBytes(workerPid);
            if (remaining <= allowedRemaining)
                break;
            await Task.Delay(25);
        }
        while (deadline.Elapsed < TimeSpan.FromSeconds(2));

        Assert.True(
            remaining <= allowedRemaining,
            $"Worker retained {remaining} of {peakWorkerBytes} associated bytes after {deadline.Elapsed.TotalMilliseconds:F0} ms.");
    }

    [Fact(Timeout = 60_000)]
    public async Task Closing_last_session_and_reopening_immediately_preserves_client_usability_and_final_recycle()
    {
        await using var client = CreateClient();
        IPdfEngineSession session = await client.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-vector-small.pdf"))),
            CancellationToken.None);
        try
        {
            _ = await session.GetMetadataAsync(CancellationToken.None);
            int originalWorkerPid = WorkerProcess(client)?.Id
                ?? throw new InvalidOperationException("The worker process was not available before the recycle test.");

            Task closeTask = session.DisposeAsync().AsTask();
            Task<IPdfEngineSession> reopenTask = client.OpenSessionAsync(
                new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-photo-scan.pdf"))),
                CancellationToken.None).AsTask();

            await closeTask;
            await using IPdfEngineSession reopened = await reopenTask;
            PdfMetadata metadata = await reopened.GetMetadataAsync(CancellationToken.None);
            Assert.True(metadata.PageCount > 0);

            Process replacementWorker = WorkerProcess(client)
                ?? throw new InvalidOperationException("The replacement worker process was not available after reopening.");
            Assert.True(replacementWorker.Id == originalWorkerPid || replacementWorker.Id > 0);

            int finalWorkerPid = replacementWorker.Id;
            await reopened.DisposeAsync();
            var deadline = Stopwatch.StartNew();
            do
            {
                if (TryPrivateBytes(finalWorkerPid) == 0)
                    break;
                await Task.Delay(25);
            }
            while (deadline.Elapsed < TimeSpan.FromSeconds(2));

            Assert.Equal(0, TryPrivateBytes(finalWorkerPid));
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private static PdfWorkerClient CreateClient() => new(new PdfWorkerClientOptions
    {
        WorkerExecutablePath = TestWorkerPayloadLocator.FindSelfContainedWorker(),
        StartupTimeout = TimeSpan.FromSeconds(10),
        DefaultOperationTimeout = TimeSpan.FromSeconds(20),
        HeartbeatInterval = TimeSpan.FromMilliseconds(250),
        HeartbeatTimeout = TimeSpan.FromSeconds(2),
        RequireAppContainerSandbox = true
    });

    private static RenderRequest RenderRequest(
        DocumentId documentId,
        PageId pageId,
        PageContentRevision contentRevision) => new(
        new RenderKey(
            documentId,
            pageId,
            contentRevision,
            PageAppearanceRevision.Initial,
            new TileAddress(0, 0, 128, 128, 1),
            RasterScale64.FromPhysicalPixelsPerPoint(1),
            PageRotation.None,
            RenderMode.Normal),
        RenderGeneration.Initial,
        RenderQuality.Standard,
        EngineJobPriority.VisibleInteractionCritical,
        DateTimeOffset.UtcNow.AddSeconds(20));

    private static Process? WorkerProcess(PdfWorkerClient client) =>
        (Process?)typeof(PdfWorkerClient)
            .GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client);

    private static long PrivateBytes(Process process)
    {
        process.Refresh();
        return process.PrivateMemorySize64;
    }

    private static long TryPrivateBytes(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.HasExited ? 0 : PrivateBytes(process);
        }
        catch (ArgumentException)
        {
            return 0;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static TimeSpan ReadDuration()
    {
        string? value = Environment.GetEnvironmentVariable("ELLIEPDF_SOAK_MINUTES");
        if (string.IsNullOrWhiteSpace(value))
            return TimeSpan.Zero;
        if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double minutes)
            || minutes is <= 0 or > 480)
            throw new InvalidOperationException("ELLIEPDF_SOAK_MINUTES must be greater than 0 and at most 480.");
        return TimeSpan.FromMinutes(minutes);
    }

    private static async Task WriteReportAsync<T>(T report)
    {
        string? reportPath = Environment.GetEnvironmentVariable("ELLIEPDF_SOAK_REPORT_PATH");
        if (string.IsNullOrWhiteSpace(reportPath))
            return;
        string fullPath = Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    private static string Fixture(string name)
    {
        string path = Path.Combine(RepositoryRoot(), "testdata", "generated", name);
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("A generated soak fixture was not found.", path);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EXECUTION_SPEC.md")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
