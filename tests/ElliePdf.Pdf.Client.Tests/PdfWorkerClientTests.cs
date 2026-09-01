using System.Collections;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;
using ElliePdf.Pdf.Transport;

namespace ElliePdf.Pdf.Client.Tests;

public sealed class PdfWorkerClientTests
{
    [Fact(Timeout = 60_000)]
    public async Task Real_worker_invokes_only_actionless_push_buttons_without_form_value_writes()
    {
        var fixture = CreatePushButtonFixture();
        try
        {
            await using var client = CreateClient();
            await using var session = await client.OpenSessionAsync(
                new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(fixture)),
                CancellationToken.None);
            var buttons = await session.GetFormWidgetsAsync(0, CancellationToken.None);
            var safe = Assert.Single(buttons.Widgets.Where(widget => widget.FieldName == "safe_push_button"));
            var unsafeButton = Assert.Single(buttons.Widgets.Where(widget => widget.FieldName == "unsafe_push_button"));
            var activator = Assert.IsAssignableFrom<IPdfPushButtonSession>(session);

            Assert.True(safe.IsSupported);
            Assert.False(safe.IsReadOnly);
            Assert.False(unsafeButton.IsSupported);
            Assert.True(unsafeButton.IsReadOnly);

            await activator.InvokePushButtonAsync(
                new PushButtonInvocation(session.DocumentId, safe.Id, ContentRevision.Initial),
                CancellationToken.None);
            var blocked = await Assert.ThrowsAsync<PdfWorkerRemoteException>(() => activator.InvokePushButtonAsync(
                new PushButtonInvocation(session.DocumentId, unsafeButton.Id, ContentRevision.Initial),
                CancellationToken.None).AsTask());
            Assert.Equal("authority_denied", blocked.Code);
            var valueWrite = await Assert.ThrowsAsync<PdfWorkerRemoteException>(() => session.ApplyFormValueAsync(
                new FormValueChange(session.DocumentId, safe.Id, FormValue.None(), ContentRevision.Initial),
                CancellationToken.None).AsTask());
            Assert.Equal("invalid_argument", valueWrite.Code);
        }
        finally
        {
            File.Delete(fixture);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Real_worker_round_trips_metadata_render_text_search_outline_and_lease_release()
    {
        await using var client = CreateClient();

        await using var mixedSession = await client.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-mixed-orientation-links-forms-outlines.pdf"))),
            CancellationToken.None);
        Assert.NotNull(client.ActiveSandboxMode);

        var mixedMetadata = await mixedSession.GetMetadataAsync(CancellationToken.None);
        Assert.Equal(8, mixedMetadata.PageCount);
        Assert.True(mixedMetadata.HasOutline);
        Assert.True(mixedMetadata.HasForms);

        var mixedPage = await mixedSession.GetPageMetadataAsync(1, CancellationToken.None);
        Assert.Equal(1, mixedPage.PageIndex);
        Assert.NotEqual(Guid.Empty, mixedPage.Id.Value);
        Assert.True(mixedPage.SizeInPoints.Width > 0);
        Assert.True(mixedPage.SizeInPoints.Height > 0);

        var lease = Assert.IsType<WorkerPixelBufferLease>(await mixedSession.RenderAsync(
            CreateRenderRequest(mixedSession.DocumentId, mixedPage.Id, new TileAddress(0, 0, 128, 128, 1)),
            CancellationToken.None));
        var mappingId = lease.SharedMemoryId;
        var bytes = ReadAll(lease.OpenReadStream(), lease.ByteLength);
        Assert.Equal(lease.Stride * lease.Height, lease.ByteLength);
        Assert.Contains(bytes, static value => value != 0);
        await lease.DisposeAsync();
        await WaitUntilAsync(() => !MemoryMappingExists(mappingId), TimeSpan.FromSeconds(5));

        var outline = await mixedSession.GetOutlineAsync(CancellationToken.None);
        Assert.Equal(8, outline.Items.Length);
        Assert.All(outline.Items, item => Assert.StartsWith("Synthetic section ", item.Title, StringComparison.Ordinal));

        await using var vectorSession = await client.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-vector-small.pdf"))),
            CancellationToken.None);

        var vectorMetadata = await vectorSession.GetMetadataAsync(CancellationToken.None);
        Assert.Equal(3, vectorMetadata.PageCount);

        var vectorPage = await vectorSession.GetPageMetadataAsync(0, CancellationToken.None);
        var textRequest = new PageTextRequest(vectorSession.DocumentId, vectorPage.Id, vectorPage.PageIndex, PageContentRevision.Initial);
        var text = await vectorSession.GetPageTextAsync(textRequest, CancellationToken.None);
        Assert.Contains("ElliePdf", text.Text, StringComparison.Ordinal);
        Assert.NotEmpty(text.Spans);

        var search = await vectorSession.SearchPageAsync(
            new PageSearchRequest(textRequest, "ElliePdf", SearchGeneration.Initial),
            CancellationToken.None);
        Assert.NotEmpty(search);
        Assert.All(search, match =>
        {
            Assert.Equal(vectorSession.DocumentId, match.DocumentId);
            Assert.Equal(vectorPage.Id, match.PageId);
            Assert.True(match.MatchLength > 0);
            Assert.Contains("ElliePdf", match.Context, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact(Timeout = 60_000)]
    public async Task Real_worker_annotation_stage_finalize_and_flatten_use_brokered_streams()
    {
        var stagedPath = Path.Combine(Path.GetTempPath(), $"elliepdf-client-annotated-{Guid.NewGuid():N}.pdf");
        var flattenedPath = Path.Combine(Path.GetTempPath(), $"elliepdf-client-flattened-{Guid.NewGuid():N}.pdf");
        try
        {
            await using var client = CreateClient();
            await using var session = await client.OpenSessionAsync(
                new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-vector-small.pdf"))),
                CancellationToken.None);
            var annotations = Assert.IsAssignableFrom<IPdfAnnotationPersistenceSession>(session);
            var page = await session.GetPageMetadataAsync(0, CancellationToken.None);
            var request = CreateAnnotationRequest(annotations.Snapshot, page, Guid.NewGuid());

            await using (var stagedOutput = new FileStream(
                stagedPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var staged = await annotations.StageAnnotationsAsync(
                    request,
                    stagedOutput,
                    CancellationToken.None);
                Assert.Equal(ContentRevision.Initial.Next(), staged.ContentRevision);
                Assert.True(stagedOutput.Length > 0);
            }

            var retained = await annotations.FinalizeAnnotationTransactionAsync(
                request.TransactionId,
                committed: false,
                CancellationToken.None);
            Assert.Equal(ContentRevision.Initial.Next(), retained.ContentRevision);
            Assert.Equal(ContentRevision.Initial, retained.SavedRevision);

            var retainedPage = await session.GetPageMetadataAsync(0, CancellationToken.None);
            var flattenRequest = CreateAnnotationRequest(annotations.Snapshot, retainedPage, Guid.NewGuid());
            await using (var flattenedOutput = new FileStream(
                flattenedPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read | FileShare.Delete,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await annotations.SaveFlattenedCopyAsync(
                    flattenRequest,
                    flattenedOutput,
                    CancellationToken.None);
                Assert.True(flattenedOutput.Length > 0);
            }

            Assert.Equal(ContentRevision.Initial.Next(), annotations.Snapshot.ContentRevision);
            await using var annotatedReopen = await client.OpenSessionAsync(
                new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(stagedPath)),
                CancellationToken.None);
            await using var flattenedReopen = await client.OpenSessionAsync(
                new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(flattenedPath)),
                CancellationToken.None);
            Assert.Equal(3, (await annotatedReopen.GetMetadataAsync(CancellationToken.None)).PageCount);
            Assert.Equal(3, (await flattenedReopen.GetMetadataAsync(CancellationToken.None)).PageCount);
        }
        finally
        {
            File.Delete(stagedPath);
            File.Delete(flattenedPath);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Adjacent_bleed_tiles_match_an_overlapping_golden_render_without_a_seam()
    {
        await using var client = CreateClient();
        await using var session = await client.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-vector-small.pdf"))),
            CancellationToken.None);
        var page = await session.GetPageMetadataAsync(0, CancellationToken.None);
        var pageWidth = checked((int)Math.Ceiling(page.SizeInPoints.Width));
        var sampleHeight = Math.Min(128, checked((int)Math.Ceiling(page.SizeInPoints.Height)));
        Assert.True(pageWidth > 512);

        var composite = new byte[checked(pageWidth * sampleHeight * 4)];
        await RenderInteriorIntoAsync(
            session,
            page,
            new TileAddress(0, 0, 512, sampleHeight, 1),
            composite,
            pageWidth);
        await RenderInteriorIntoAsync(
            session,
            page,
            new TileAddress(512, 0, pageWidth - 512, sampleHeight, 1),
            composite,
            pageWidth);

        var referenceAddress = new TileAddress(256, 0, pageWidth - 256, sampleHeight, 1);
        await using var referenceLease = Assert.IsAssignableFrom<IReadablePixelBufferLease>(
            await session.RenderAsync(
                CreateRenderRequest(session.DocumentId, page.Id, referenceAddress),
                CancellationToken.None));
        var reference = ReadAll(referenceLease.OpenReadStream(), referenceLease.ByteLength);
        var referenceLeftBleed = 1;
        var metrics = CompareGoldenRegion(
            reference,
            referenceLease.Stride,
            referenceLeftBleed,
            composite,
            pageWidth,
            referenceAddress.X,
            referenceAddress.InteriorWidth,
            sampleHeight);
        Assert.True(
            metrics.StructuralSimilarity >= 0.995 && metrics.LargeDeltaFraction < 0.005,
            $"Golden-render SSIM={metrics.StructuralSimilarity:F6}; "
                + $"large-delta pixels={metrics.LargeDeltaFraction:P4}.");
    }

    [Fact(Timeout = 60_000)]
    public async Task Cancellation_and_session_close_are_deterministic_and_do_not_poison_the_client()
    {
        await using var client = CreateClient();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-vector-small.pdf"))),
            cancelled.Token).AsTask());

        await using var session = await client.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-vector-small.pdf"))),
            CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetMetadataAsync(cancelled.Token).AsTask());

        var page = await session.GetPageMetadataAsync(0, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.RenderAsync(
            CreateRenderRequest(session.DocumentId, page.Id),
            cancelled.Token).AsTask());

        Assert.Equal(3, (await session.GetMetadataAsync(CancellationToken.None)).PageCount);

        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.GetMetadataAsync(CancellationToken.None).AsTask());

        await using var reopened = await client.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-vector-small.pdf"))),
            CancellationToken.None);
        Assert.Equal(3, (await reopened.GetMetadataAsync(CancellationToken.None)).PageCount);
    }

    [Fact(Timeout = 60_000)]
    public async Task Last_close_racing_a_new_open_never_recycles_the_new_session()
    {
        await using var client = CreateClient();
        var path = Fixture("synthetic-vector-small.pdf");
        IPdfEngineSession current = await client.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(path)),
            CancellationToken.None);

        try
        {
            for (var iteration = 0; iteration < 8; iteration++)
            {
                var close = current.DisposeAsync().AsTask();
                var open = client.OpenSessionAsync(
                    new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(path)),
                    CancellationToken.None).AsTask();
                await Task.WhenAll(close, open);
                current = await open;
                Assert.Equal(3, (await current.GetMetadataAsync(CancellationToken.None)).PageCount);
            }
        }
        finally
        {
            await current.DisposeAsync();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Concurrent_client_dispose_callers_wait_for_the_same_cleanup()
    {
        var client = CreateClient();
        await using var session = await client.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-vector-small.pdf"))),
            CancellationToken.None);

        var first = client.DisposeAsync().AsTask();
        var second = client.DisposeAsync().AsTask();
        await Task.WhenAll(first, second);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-vector-small.pdf"))),
            CancellationToken.None).AsTask());
    }

    [Fact(Timeout = 60_000)]
    public async Task Worker_crash_forces_reopen_and_quarantines_after_three_failed_renders()
    {
        await using var client = CreateClient();
        var crashPath = Fixture("synthetic-mixed-orientation-links-forms-outlines.pdf");
        var crashDocumentId = DocumentId.New();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await using var session = await client.OpenSessionAsync(
                new DocumentOpenRequest(crashDocumentId, new PdfSourceHandle(crashPath)),
                CancellationToken.None);
            var page = await session.GetPageMetadataAsync(0, CancellationToken.None);
            await Assert.ThrowsAsync<PdfWorkerUnavailableException>(() => RenderAndCrashAsync(session, client, page.Id));
            Assert.Equal(attempt >= 3, client.IsQuarantined(crashDocumentId));
        }

        await Assert.ThrowsAsync<PdfWorkerQuarantinedException>(() => client.OpenSessionAsync(
            new DocumentOpenRequest(crashDocumentId, new PdfSourceHandle(crashPath)),
            CancellationToken.None).AsTask());

        Assert.True(client.ClearQuarantine(crashDocumentId));

        await using var recovered = await client.OpenSessionAsync(
            new DocumentOpenRequest(crashDocumentId, new PdfSourceHandle(crashPath)),
            CancellationToken.None);
        Assert.Equal(8, (await recovered.GetMetadataAsync(CancellationToken.None)).PageCount);
    }

    [Fact(Timeout = 60_000)]
    public async Task Existing_sessions_fail_after_worker_crash_and_new_opens_restart_the_worker()
    {
        await using var client = CreateClient();
        var path = Fixture("synthetic-vector-small.pdf");

        await using var session = await client.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(path)),
            CancellationToken.None);

        KillWorkerProcess(client);

        await WaitUntilAsync(() => TryGetWorkerProcess(client) is null, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<PdfWorkerUnavailableException>(() => session.GetMetadataAsync(CancellationToken.None).AsTask());

        await using var reopened = await client.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(path)),
            CancellationToken.None);
        Assert.Equal(3, (await reopened.GetMetadataAsync(CancellationToken.None)).PageCount);
    }

    [Fact(Timeout = 60_000)]
    public async Task Labs_mutations_and_ordered_merge_round_trip_through_real_worker_and_reopen_output()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"elliepdf-client-merge-{Guid.NewGuid():N}.pdf");
        try
        {
            await using var client = CreateClient();
            await using var vectorSession = await client.OpenSessionAsync(
                new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-vector-small.pdf"))),
                CancellationToken.None);
            await using var mixedSession = await client.OpenSessionAsync(
                new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-mixed-orientation-links-forms-outlines.pdf"))),
                CancellationToken.None);

            var vectorMutations = Assert.IsAssignableFrom<IPdfPageMutationSession>(vectorSession);
            var originalPages = new[]
            {
                await vectorSession.GetPageMetadataAsync(0, CancellationToken.None),
                await vectorSession.GetPageMetadataAsync(1, CancellationToken.None),
                await vectorSession.GetPageMetadataAsync(2, CancellationToken.None)
            };
            var rotateRequest = new RotatePageRequest(
                vectorSession.DocumentId,
                originalPages[1].Id,
                vectorMutations.Snapshot.ContentRevision,
                vectorMutations.Snapshot.StructureRevision,
                originalPages[1].ContentRevision,
                1);
            var rotated = await vectorMutations.RotatePageAsync(rotateRequest, CancellationToken.None);
            Assert.Equal(ContentRevision.Initial.Next(), rotated.ContentRevision);
            Assert.Equal(StructureRevision.Initial, rotated.StructureRevision);

            var stale = await Assert.ThrowsAsync<PdfWorkerRemoteException>(() =>
                vectorMutations.RotatePageAsync(rotateRequest, CancellationToken.None).AsTask());
            Assert.Equal("stale_identity", stale.Code);

            var deleted = await vectorMutations.DeletePageAsync(new DeletePageRequest(
                vectorSession.DocumentId,
                originalPages[0].Id,
                rotated.ContentRevision,
                rotated.StructureRevision,
                originalPages[0].ContentRevision), CancellationToken.None);
            Assert.Equal(2, deleted.PageCount);
            Assert.Equal(ContentRevision.Initial.Next().Next(), deleted.ContentRevision);
            Assert.Equal(StructureRevision.Initial.Next(), deleted.StructureRevision);

            var rotatedSurvivor = await vectorSession.GetPageMetadataAsync(0, CancellationToken.None);
            var thirdPageSurvivor = await vectorSession.GetPageMetadataAsync(1, CancellationToken.None);
            Assert.Equal(originalPages[1].Id, rotatedSurvivor.Id);
            Assert.Equal(originalPages[2].Id, thirdPageSurvivor.Id);
            Assert.Equal(PageRotation.Clockwise90, rotatedSurvivor.Geometry.Rotation);
            Assert.Equal(PageContentRevision.Initial.Next(), rotatedSurvivor.ContentRevision);
            Assert.Equal(PageAppearanceRevision.Initial.Next(), rotatedSurvivor.AppearanceRevision);

            var mixedSnapshot = Assert.IsAssignableFrom<IPdfWritableEngineSession>(mixedSession).Snapshot;
            var mixedPage = await mixedSession.GetPageMetadataAsync(0, CancellationToken.None);
            var merge = new MergeOrderedPagesRequest(
            [
                MergeReference(deleted, thirdPageSurvivor),
                MergeReference(mixedSnapshot, mixedPage),
                MergeReference(deleted, rotatedSurvivor)
            ]);
            await using (var output = new FileStream(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await client.MergeOrderedPagesAsync(merge, output, CancellationToken.None);
                Assert.True(output.CanWrite);
            }

            await using var reopened = await client.OpenSessionAsync(
                new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(outputPath)),
                CancellationToken.None);
            Assert.Equal(3, (await reopened.GetMetadataAsync(CancellationToken.None)).PageCount);

            var reopenedFirst = await reopened.GetPageMetadataAsync(0, CancellationToken.None);
            var reopenedFirstText = await reopened.GetPageTextAsync(new PageTextRequest(
                reopened.DocumentId,
                reopenedFirst.Id,
                0,
                reopenedFirst.ContentRevision), CancellationToken.None);
            Assert.Contains("Vector page 3", reopenedFirstText.Text, StringComparison.Ordinal);

            var reopenedLast = await reopened.GetPageMetadataAsync(2, CancellationToken.None);
            Assert.Equal(PageRotation.Clockwise90, reopenedLast.Geometry.Rotation);
            var reopenedLastText = await reopened.GetPageTextAsync(new PageTextRequest(
                reopened.DocumentId,
                reopenedLast.Id,
                2,
                reopenedLast.ContentRevision), CancellationToken.None);
            Assert.Contains("Vector page 2", reopenedLastText.Text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task Missing_worker_executable_throws_file_not_found()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}", "ElliePdf.Pdfium.Worker.exe");
        await using var client = new PdfWorkerClient(new PdfWorkerClientOptions { WorkerExecutablePath = path });

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-vector-small.pdf"))),
            CancellationToken.None).AsTask());

        Assert.Equal(path, exception.FileName);
    }

    [Fact(Timeout = 60_000)]
    public async Task Required_restricted_token_never_silently_falls_back_to_compatibility_mode()
    {
        await using var compatibilityClient = CreateClient();
        await using var compatibilitySession = await compatibilityClient.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-vector-small.pdf"))),
            CancellationToken.None);

        if (compatibilityClient.ActiveSandboxMode != WorkerSandboxMode.JobConstrainedCompatibility)
        {
            // This host supports filtered-token child processes, so there is no downgrade path to
            // exercise. The real-worker test above asserts the selected mode is observable.
            return;
        }

        await using var strictClient = CreateClient(requireRestrictedTokenSandbox: true);
        await Assert.ThrowsAsync<Win32Exception>(() => strictClient.OpenSessionAsync(
            new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle(Fixture("synthetic-vector-small.pdf"))),
            CancellationToken.None).AsTask());
        Assert.Null(strictClient.ActiveSandboxMode);
    }

    [Fact]
    public void Options_bounds_are_validated()
    {
        var workerPath = WorkerExecutablePath();

        Assert.Throws<ArgumentException>(() => _ = new PdfWorkerClient(new PdfWorkerClientOptions
        {
            WorkerExecutablePath = "ElliePdf.Pdfium.Worker.exe"
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PdfWorkerClient(new PdfWorkerClientOptions
        {
            WorkerExecutablePath = workerPath,
            StartupTimeout = TimeSpan.Zero
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PdfWorkerClient(new PdfWorkerClientOptions
        {
            WorkerExecutablePath = workerPath,
            DefaultOperationTimeout = TimeSpan.FromMinutes(6)
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PdfWorkerClient(new PdfWorkerClientOptions
        {
            WorkerExecutablePath = workerPath,
            HeartbeatInterval = TimeSpan.FromMilliseconds(99)
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PdfWorkerClient(new PdfWorkerClientOptions
        {
            WorkerExecutablePath = workerPath,
            HeartbeatTimeout = TimeSpan.FromMilliseconds(250),
            HeartbeatInterval = TimeSpan.FromMilliseconds(500)
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PdfWorkerClient(new PdfWorkerClientOptions
        {
            WorkerExecutablePath = workerPath,
            JobMemoryLimitBytes = 63L * 1024 * 1024
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new PdfWorkerClient(new PdfWorkerClientOptions
        {
            WorkerExecutablePath = workerPath,
            CpuHardCapPercent = 0
        }));
    }

    private static PdfWorkerClient CreateClient(bool requireRestrictedTokenSandbox = false)
        => new(new PdfWorkerClientOptions
        {
            WorkerExecutablePath = WorkerExecutablePath(),
            StartupTimeout = TimeSpan.FromSeconds(10),
            DefaultOperationTimeout = TimeSpan.FromSeconds(20),
            HeartbeatInterval = TimeSpan.FromMilliseconds(250),
            HeartbeatTimeout = TimeSpan.FromSeconds(2),
            RequireRestrictedTokenSandbox = requireRestrictedTokenSandbox
        });

    private static PageMergeReference MergeReference(DocumentSnapshot snapshot, PageMetadata page) => new(
        snapshot.Id,
        page.Id,
        snapshot.ContentRevision,
        snapshot.StructureRevision,
        page.ContentRevision);

    private static PdfAnnotationSaveRequest CreateAnnotationRequest(
        DocumentSnapshot snapshot,
        PageMetadata page,
        Guid transactionId) => new(
        transactionId,
        snapshot.Id,
        snapshot.ContentRevision,
        snapshot.StructureRevision,
        [
            new PdfPageOverlayBatch(
                page.PageIndex,
                page.Id,
                page.ContentRevision,
                [
                    new PdfInkAnnotation(
                        "ellie:client:ink",
                        [new PdfOverlayPoint(320, 100), new PdfOverlayPoint(450, 150)],
                        new PdfOverlayColor(15, 70, 200),
                        3)
                ],
                [
                    new PdfTextStampAnnotation(
                        "ellie:client:text",
                        new PdfOverlayRectangle(320, 180, 180, 40),
                        "client protocol note",
                        14,
                        new PdfOverlayColor(160, 20, 20),
                        false,
                        false)
                ],
                [])
        ]);

    private static RenderRequest CreateRenderRequest(DocumentId documentId, PageId pageId, TileAddress? tile = null)
        => new(
            new RenderKey(
                documentId,
                pageId,
                PageContentRevision.Initial,
                PageAppearanceRevision.Initial,
                tile ?? new TileAddress(0, 0, 96, 96, 1),
                RasterScale64.FromPhysicalPixelsPerPoint(1),
                PageRotation.None,
                RenderMode.Normal),
            RenderGeneration.Initial,
            RenderQuality.Standard,
            EngineJobPriority.VisibleInteractionCritical,
            DateTimeOffset.UtcNow.AddSeconds(10));

    private static async Task RenderInteriorIntoAsync(
        IPdfEngineSession session,
        PageMetadata page,
        TileAddress address,
        byte[] destination,
        int pageWidth)
    {
        await using var lease = Assert.IsAssignableFrom<IReadablePixelBufferLease>(
            await session.RenderAsync(
                CreateRenderRequest(session.DocumentId, page.Id, address),
                CancellationToken.None));
        var source = ReadAll(lease.OpenReadStream(), lease.ByteLength);
        var leftBleed = address.X > 0 ? address.BleedPixels : 0;
        var topBleed = address.Y > 0 ? address.BleedPixels : 0;
        for (var row = 0; row < address.InteriorHeight; row++)
        {
            var sourceOffset = checked((row + topBleed) * lease.Stride + leftBleed * 4);
            var destinationOffset = checked(((address.Y + row) * pageWidth + address.X) * 4);
            Buffer.BlockCopy(
                source,
                sourceOffset,
                destination,
                destinationOffset,
                checked(address.InteriorWidth * 4));
        }
    }

    private static GoldenRenderMetrics CompareGoldenRegion(
        byte[] reference,
        int referenceStride,
        int referenceLeftPixels,
        byte[] actual,
        int actualPageWidth,
        int actualX,
        int width,
        int height)
    {
        var largeDeltaPixels = 0;
        double ssimSum = 0;
        var blockCount = 0;
        const int blockSize = 8;
        const double c1 = 6.5025;
        const double c2 = 58.5225;
        for (var blockY = 0; blockY < height; blockY += blockSize)
        {
            for (var blockX = 0; blockX < width; blockX += blockSize)
            {
                double referenceSum = 0;
                double actualSum = 0;
                double referenceSquares = 0;
                double actualSquares = 0;
                double products = 0;
                var blockWidth = Math.Min(blockSize, width - blockX);
                var blockHeight = Math.Min(blockSize, height - blockY);
                var samples = checked(blockWidth * blockHeight);
                for (var y = 0; y < blockHeight; y++)
                {
                    var referenceRow = checked((blockY + y) * referenceStride + referenceLeftPixels * 4);
                    var actualRow = checked(((blockY + y) * actualPageWidth + actualX) * 4);
                    for (var x = 0; x < blockWidth; x++)
                    {
                        var referencePixel = referenceRow + (blockX + x) * 4;
                        var actualPixel = actualRow + (blockX + x) * 4;
                        var largeDelta = false;
                        for (var channel = 0; channel < 3; channel++)
                        {
                            largeDelta |= Math.Abs(
                                reference[referencePixel + channel]
                                - actual[actualPixel + channel]) > 8;
                        }
                        if (largeDelta)
                        {
                            largeDeltaPixels++;
                        }

                        var expectedLuma = (
                            29 * reference[referencePixel]
                            + 150 * reference[referencePixel + 1]
                            + 77 * reference[referencePixel + 2]) / 256.0;
                        var actualLuma = (
                            29 * actual[actualPixel]
                            + 150 * actual[actualPixel + 1]
                            + 77 * actual[actualPixel + 2]) / 256.0;
                        referenceSum += expectedLuma;
                        actualSum += actualLuma;
                        referenceSquares += expectedLuma * expectedLuma;
                        actualSquares += actualLuma * actualLuma;
                        products += expectedLuma * actualLuma;
                    }
                }

                var referenceMean = referenceSum / samples;
                var actualMean = actualSum / samples;
                var denominator = Math.Max(1, samples - 1);
                var referenceVariance = (referenceSquares - samples * referenceMean * referenceMean) / denominator;
                var actualVariance = (actualSquares - samples * actualMean * actualMean) / denominator;
                var covariance = (products - samples * referenceMean * actualMean) / denominator;
                ssimSum += ((2 * referenceMean * actualMean + c1) * (2 * covariance + c2))
                    / ((referenceMean * referenceMean + actualMean * actualMean + c1)
                        * (referenceVariance + actualVariance + c2));
                blockCount++;
            }
        }

        return new GoldenRenderMetrics(
            ssimSum / blockCount,
            (double)largeDeltaPixels / checked(width * height));
    }

    private readonly record struct GoldenRenderMetrics(
        double StructuralSimilarity,
        double LargeDeltaFraction);

    private static async Task RenderAndCrashAsync(IPdfEngineSession session, PdfWorkerClient client, PageId pageId)
    {
        // Hold the client write gate only long enough to make the render request observable in
        // the pending table. This prevents a fast PDFium response (or a heartbeat request) from
        // winning the test's kill race: the worker is terminated while this exact render is still
        // outstanding, not after it has already completed successfully.
        var writeGate = GetWriteGate(client);
        await writeGate.WaitAsync();
        var writeGateHeld = true;
        try
        {
            var renderTask = session.RenderAsync(
                CreateRenderRequest(
                    session.DocumentId,
                    pageId,
                    new TileAddress(0, 0, 512, 512, 1)),
                CancellationToken.None).AsTask();

            var process = await WaitForPendingRenderWorkerProcessAsync(
                client,
                session.DocumentId,
                TimeSpan.FromSeconds(10));
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            writeGate.Release();
            writeGateHeld = false;
            await renderTask;
        }
        finally
        {
            if (writeGateHeld)
            {
                writeGate.Release();
            }
        }
    }

    private static async Task<Process> WaitForPendingRenderWorkerProcessAsync(
        PdfWorkerClient client,
        DocumentId documentId,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var process = TryGetWorkerProcess(client);
            if (process is not null && !process.HasExited && HasPendingRender(client, documentId))
            {
                return process;
            }

            await Task.Delay(1);
        }

        throw new TimeoutException("A worker request never became pending.");
    }

    private static void KillWorkerProcess(PdfWorkerClient client)
    {
        var process = TryGetWorkerProcess(client) ?? throw new InvalidOperationException("The worker process is not available.");
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static Process? TryGetWorkerProcess(PdfWorkerClient client)
        => (Process?)typeof(PdfWorkerClient)
            .GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client);

    private static SemaphoreSlim GetWriteGate(PdfWorkerClient client)
        => (SemaphoreSlim)(typeof(PdfWorkerClient)
            .GetField("_writeGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)
            ?? throw new InvalidOperationException("Worker write gate is unavailable."));

    private static bool HasPendingRender(PdfWorkerClient client, DocumentId documentId)
    {
        var pending = typeof(PdfWorkerClient)
            .GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)
            ?? throw new InvalidOperationException("Pending request state is unavailable.");

        foreach (var entry in (IEnumerable)pending)
        {
            var value = entry.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public)!.GetValue(entry)
                ?? throw new InvalidOperationException("Pending request entry is missing its value.");
            var identity = (TransportIdentity?)value.GetType()
                .GetProperty("Identity", BindingFlags.Instance | BindingFlags.Public)!
                .GetValue(value);
            if (identity is { DocumentId: Guid pendingDocumentId, RenderGeneration: not null }
                && pendingDocumentId == documentId.Value)
            {
                return true;
            }
        }

        return false;
    }

    private static string WorkerExecutablePath()
        => TestWorkerPayloadLocator.FindSelfContainedWorker();

    private static string Fixture(string name)
    {
        var path = Path.Combine(RepositoryRoot(), "testdata", "generated", name);
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"Generated test fixture was not found: {name}", path);
    }

    private static string CreatePushButtonFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), $"elliepdf-client-push-buttons-{Guid.NewGuid():N}.pdf");
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Contents 4 0 R /Annots [6 0 R 7 0 R] >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Fields [6 0 R 7 0 R] /NeedAppearances false >>",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (safe_push_button) /Rect [48 400 168 430] /P 3 0 R /F 4 >>",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /Ff 65536 /T (unsafe_push_button) /Rect [190 400 310 430] /P 3 0 R /F 4 /A << /S /JavaScript /JS (app.alert\\(unsafe\\)) >> >>"
        };
        var builder = new StringBuilder("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        var xrefOffset = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) builder.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xrefOffset).Append("\n%%EOF\n");
        File.WriteAllText(path, builder.ToString(), Encoding.ASCII);
        return path;
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EXECUTION_SPEC.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static byte[] ReadAll(Stream stream, int byteLength)
    {
        using (stream)
        {
            var bytes = new byte[byteLength];
            var read = 0;
            while (read < bytes.Length)
            {
                var count = stream.Read(bytes, read, bytes.Length - read);
                if (count == 0)
                {
                    break;
                }

                read += count;
            }

            return bytes;
        }
    }

    private static bool MemoryMappingExists(string mappingId)
    {
        try
        {
            using var mapping = MemoryMappedFile.OpenExisting(mappingId, MemoryMappedFileRights.Read);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Condition was not met within the allotted time.");
            }

            await Task.Delay(25);
        }
    }
}
