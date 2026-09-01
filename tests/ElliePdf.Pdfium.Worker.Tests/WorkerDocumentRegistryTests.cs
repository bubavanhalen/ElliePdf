using System.Diagnostics;
using System.Text;

namespace ElliePdf.Pdfium.Worker.Tests;

public sealed class WorkerDocumentRegistryTests
{
    [Fact]
    public async Task Form_enabled_documents_close_without_native_access_violation()
    {
        await using var registry = CreateRegistry();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var source = OpenReadHandle(Fixture("synthetic-mixed-orientation-links-forms-outlines.pdf"));
            var id = DocumentId.New();
            await registry.OpenAsync(
                new DocumentOpenRequest(id, new PdfSourceHandle("worker-owned-source")),
                source);

            Assert.True(await registry.CloseAsync(id));
            Assert.True(source.IsClosed);
        }
    }

    [Fact]
    public async Task Open_uses_the_brokered_read_only_handle_and_ignores_request_source_path()
    {
        await using var registry = CreateRegistry();
        var id = DocumentId.New();
        // Successful OpenAsync transfers ownership of the brokered source handle to the worker.
        var source = OpenReadHandle(Fixture("synthetic-vector-small.pdf"));

        var result = await registry.OpenAsync(
            new DocumentOpenRequest(id, new PdfSourceHandle("C:\\authority-that-must-not-be-used\\missing.pdf")),
            source);

        Assert.Equal(id, result.Snapshot.Id);
        Assert.Equal(3, result.Metadata.PageCount);
        Assert.False(result.Metadata.IsEncrypted);
        Assert.False(source.IsClosed); // The worker owns the live brokered handle until CloseAsync/DisposeAsync.

        await Assert.ThrowsAsync<WorkerDocumentNotFoundException>(
            () => registry.GetMetadataAsync(DocumentId.New()).AsTask());
    }

    [Fact]
    public async Task Read_only_brokered_handle_cannot_be_used_to_write()
    {
        var path = Fixture("synthetic-vector-small.pdf");
        using var readHandle = OpenReadHandle(path);

        Assert.ThrowsAny<Exception>(() =>
        {
            using var writeStream = new FileStream(readHandle, FileAccess.Write, bufferSize: 1, isAsync: false);
            writeStream.WriteByte(0x42);
        });
    }

    [Fact]
    public async Task Page_metadata_render_text_search_and_outline_are_identity_bound()
    {
        await using var registry = CreateRegistry();
        var id = DocumentId.New();
        await OpenAsync(registry, id, "synthetic-mixed-orientation-links-forms-outlines.pdf");

        var page = await registry.GetPageMetadataAsync(id, 1);
        Assert.Equal(1, page.PageIndex);
        Assert.True(page.SizeInPoints.Width > 0);
        Assert.True(page.SizeInPoints.Height > 0);

        var render = await registry.RenderAsync(CreateRenderRequest(id, page.Id));
        Assert.Equal(PixelFormat.Bgra8Premultiplied, render.Format);
        Assert.Equal(render.Stride * render.Height, render.Pixels.Length);
        Assert.InRange(render.Width, 1, 514);
        Assert.InRange(render.Height, 1, 514);
        Assert.Contains(render.Pixels, static value => value != 0);

        await using var vectorRegistry = CreateRegistry();
        var vectorId = DocumentId.New();
        await OpenAsync(vectorRegistry, vectorId, "synthetic-vector-small.pdf");
        var vectorPage = await vectorRegistry.GetPageMetadataAsync(vectorId, 0);
        var textRequest = new PageTextRequest(vectorId, vectorPage.Id, 0, PageContentRevision.Initial);
        var text = await vectorRegistry.GetPageTextAsync(textRequest);
        Assert.Contains("ElliePdf", text.Text, StringComparison.Ordinal);
        Assert.NotEmpty(text.Spans);
        Assert.All(text.Spans, span =>
        {
            Assert.False(string.IsNullOrWhiteSpace(span.Text));
            Assert.True(span.Bounds.Right >= span.Bounds.Left);
            Assert.True(span.Bounds.Bottom >= span.Bounds.Top);
        });

        var search = await vectorRegistry.SearchPageAsync(
            new PageSearchRequest(textRequest, "ElliePdf", SearchGeneration.Initial));
        Assert.NotEmpty(search);
        Assert.All(search, match => Assert.Equal(vectorPage.Id, match.PageId));

        var outline = await registry.GetOutlineAsync(id);
        Assert.Equal(8, outline.Items.Length);
        Assert.All(outline.Items, item => Assert.StartsWith("Synthetic section ", item.Title, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Mixed_fixture_exposes_internal_safe_and_blocked_links()
    {
        await using var registry = CreateRegistry();
        var id = DocumentId.New();
        await OpenAsync(registry, id, "synthetic-mixed-orientation-links-forms-outlines.pdf");

        var firstPage = await registry.GetPageMetadataAsync(id, 0);
        var links = await registry.GetPageLinksAsync(id, 0);

        Assert.Equal(firstPage.Id, links.PageId);
        Assert.Equal(3, links.Links.Length);

        var safeUri = Assert.Single(links.Links.Where(static link =>
            link.Kind == PdfLinkKind.Uri
            && string.Equals(link.Uri, "https://example.invalid/elliepdf", StringComparison.Ordinal)));
        Assert.True(safeUri.IsSafeToActivate);
        Assert.Null(safeUri.BlockedReason);
        Assert.Equal(new PdfRect(48, 676, 250, 692), safeUri.Bounds);

        var blockedUri = Assert.Single(links.Links.Where(static link =>
            link.Kind == PdfLinkKind.Uri
            && string.Equals(link.Uri, "javascript:alert('blocked')", StringComparison.Ordinal)));
        Assert.False(blockedUri.IsSafeToActivate);
        Assert.Contains("blocked", blockedUri.BlockedReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new PdfRect(48, 652, 230, 668), blockedUri.Bounds);

        var internalLink = Assert.Single(links.Links.Where(static link => link.Kind == PdfLinkKind.Page));
        Assert.Equal(1, internalLink.TargetPageIndex);
        Assert.NotNull(internalLink.TargetPageId);
        Assert.True(internalLink.IsSafeToActivate);
        Assert.Equal(new PdfRect(260, 676, 380, 692), internalLink.Bounds);
    }

    [Fact]
    public async Task Mixed_fixture_exposes_form_widget_semantics_and_stable_ids()
    {
        await using var registry = CreateRegistry();
        var id = DocumentId.New();
        await OpenAsync(registry, id, "synthetic-mixed-orientation-links-forms-outlines.pdf");

        var page0 = await registry.GetFormWidgetsAsync(id, 0);
        var page1 = await registry.GetFormWidgetsAsync(id, 1);
        var page2 = await registry.GetFormWidgetsAsync(id, 2);
        var page3 = await registry.GetFormWidgetsAsync(id, 3);
        var page4 = await registry.GetFormWidgetsAsync(id, 4);
        var page5 = await registry.GetFormWidgetsAsync(id, 5);
        var page7 = await registry.GetFormWidgetsAsync(id, 7);

        var textField = Assert.Single(page0.Widgets);
        Assert.Equal(FormWidgetType.Text, textField.Type);
        Assert.Equal("text_field", textField.FieldName);
        Assert.Equal(FormValueKind.Text, textField.Value.Kind);
        Assert.Equal("Synthetic text value", textField.Value.Text);
        Assert.False(textField.IsReadOnly);
        Assert.True(textField.IsSupported);
        Assert.Equal(new PdfRect(48, 602, 308, 626), textField.Bounds);

        var textFieldAgain = Assert.Single((await registry.GetFormWidgetsAsync(id, 0)).Widgets);
        Assert.Equal(textField.Id, textFieldAgain.Id);

        var checkboxField = Assert.Single(page1.Widgets);
        Assert.Equal(FormWidgetType.Checkbox, checkboxField.Type);
        Assert.Equal("checkbox_field", checkboxField.FieldName);
        Assert.Equal(FormValueKind.Boolean, checkboxField.Value.Kind);
        Assert.False(checkboxField.Value.Boolean);
        Assert.True(checkboxField.IsRequired);
        Assert.False(checkboxField.IsReadOnly);
        Assert.Equal(new PdfRect(48, 422, 66, 440), checkboxField.Bounds);

        var comboField = Assert.Single(page2.Widgets);
        Assert.Equal(FormWidgetType.ComboBox, comboField.Type);
        Assert.Equal("combo_field", comboField.FieldName);
        Assert.Equal(FormValueKind.Choice, comboField.Value.Kind);
        Assert.Equal("Beta", comboField.Value.Text);
        Assert.Equal(["Alpha", "Beta", "Gamma"], comboField.Options.ToArray());

        var listField = Assert.Single(page3.Widgets);
        Assert.Equal(FormWidgetType.ListBox, listField.Type);
        Assert.Equal("list_field", listField.FieldName);
        Assert.Equal(FormValueKind.Choice, listField.Value.Kind);
        Assert.Equal("South", listField.Value.Text);
        Assert.Equal(["North", "South", "East", "West"], listField.Options.ToArray());

        var readOnlyField = Assert.Single(page4.Widgets);
        Assert.Equal(FormWidgetType.Text, readOnlyField.Type);
        Assert.Equal("readonly_field", readOnlyField.FieldName);
        Assert.True(readOnlyField.IsReadOnly);
        Assert.True(readOnlyField.IsSupported);
        Assert.Null(readOnlyField.UnsupportedReason);

        var unsafeField = Assert.Single(page5.Widgets);
        Assert.Equal(FormWidgetType.Text, unsafeField.Type);
        Assert.Equal("unsafe_text_field", unsafeField.FieldName);
        Assert.True(unsafeField.IsReadOnly);
        Assert.False(unsafeField.IsSupported);
        Assert.Contains("blocked action", unsafeField.UnsupportedReason, StringComparison.OrdinalIgnoreCase);

        var requiredField = Assert.Single(page7.Widgets);
        Assert.Equal("required_text_field", requiredField.FieldName);
        Assert.True(requiredField.IsRequired);
    }

    [Fact]
    public async Task Text_checkbox_and_choice_updates_invalidate_revisions_and_reject_stale_edits()
    {
        await using var registry = CreateRegistry();
        var id = DocumentId.New();
        var opened = await OpenAsync(registry, id, "synthetic-mixed-orientation-links-forms-outlines.pdf");

        var textField = Assert.Single((await registry.GetFormWidgetsAsync(id, 0)).Widgets);
        var checkboxField = Assert.Single((await registry.GetFormWidgetsAsync(id, 1)).Widgets);
        var comboField = Assert.Single((await registry.GetFormWidgetsAsync(id, 2)).Widgets);

        var afterText = await registry.ApplyFormValueAsync(new FormValueChange(
            id,
            textField.Id,
            FormValue.TextValue("Edited text value"),
            opened.Snapshot.ContentRevision));
        Assert.Equal(opened.Snapshot.ContentRevision.Next(), afterText.ContentRevision);
        var textPage = await registry.GetPageMetadataAsync(id, 0);
        Assert.Equal(PageContentRevision.Initial.Next(), textPage.ContentRevision);
        Assert.Equal(PageAppearanceRevision.Initial.Next(), textPage.AppearanceRevision);
        var textFieldAfter = Assert.Single((await registry.GetFormWidgetsAsync(id, 0)).Widgets);
        Assert.Equal(textField.Id, textFieldAfter.Id);
        Assert.Equal("Edited text value", textFieldAfter.Value.Text);

        var afterCheckbox = await registry.ApplyFormValueAsync(new FormValueChange(
            id,
            checkboxField.Id,
            FormValue.BooleanValue(true),
            afterText.ContentRevision));
        Assert.Equal(afterText.ContentRevision.Next(), afterCheckbox.ContentRevision);
        var checkboxPage = await registry.GetPageMetadataAsync(id, 1);
        Assert.Equal(PageContentRevision.Initial.Next(), checkboxPage.ContentRevision);
        Assert.Equal(PageAppearanceRevision.Initial.Next(), checkboxPage.AppearanceRevision);
        var checkboxFieldAfter = Assert.Single((await registry.GetFormWidgetsAsync(id, 1)).Widgets);
        Assert.Equal(checkboxField.Id, checkboxFieldAfter.Id);
        Assert.True(checkboxFieldAfter.Value.Boolean);

        var afterChoice = await registry.ApplyFormValueAsync(new FormValueChange(
            id,
            comboField.Id,
            FormValue.Choice("Gamma"),
            afterCheckbox.ContentRevision));
        Assert.Equal(afterCheckbox.ContentRevision.Next(), afterChoice.ContentRevision);
        var comboPage = await registry.GetPageMetadataAsync(id, 2);
        Assert.Equal(PageContentRevision.Initial.Next(), comboPage.ContentRevision);
        Assert.Equal(PageAppearanceRevision.Initial.Next(), comboPage.AppearanceRevision);
        var comboFieldAfter = Assert.Single((await registry.GetFormWidgetsAsync(id, 2)).Widgets);
        Assert.Equal(comboField.Id, comboFieldAfter.Id);
        Assert.Equal("Gamma", comboFieldAfter.Value.Text);

        await Assert.ThrowsAsync<WorkerStaleIdentityException>(() => registry.ApplyFormValueAsync(new FormValueChange(
            id,
            comboField.Id,
            FormValue.Choice("Alpha"),
            afterCheckbox.ContentRevision)).AsTask());

        var readOnlyField = Assert.Single((await registry.GetFormWidgetsAsync(id, 4)).Widgets);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => registry.ApplyFormValueAsync(new FormValueChange(
            id,
            readOnlyField.Id,
            FormValue.TextValue("should fail"),
            afterChoice.ContentRevision)).AsTask());

        var unsafeField = Assert.Single((await registry.GetFormWidgetsAsync(id, 5)).Widgets);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => registry.ApplyFormValueAsync(new FormValueChange(
            id,
            unsafeField.Id,
            FormValue.TextValue("still blocked"),
            afterChoice.ContentRevision)).AsTask());
    }

    [Fact]
    public async Task Actionless_push_buttons_use_the_dedicated_activation_path_and_action_widgets_are_blocked()
    {
        var fixture = CreatePushButtonFixture();
        try
        {
            await using var registry = CreateRegistry();
            var id = DocumentId.New();
            var opened = await registry.OpenAsync(
                new DocumentOpenRequest(id, new PdfSourceHandle("broker-owned-push-button-source")),
                OpenReadHandle(fixture));
            var buttons = await registry.GetFormWidgetsAsync(id, 0);
            var safe = Assert.Single(buttons.Widgets.Where(widget => widget.FieldName == "safe_push_button"));
            var unsafeButton = Assert.Single(buttons.Widgets.Where(widget => widget.FieldName == "unsafe_push_button"));

            Assert.Equal(FormWidgetType.PushButton, safe.Type);
            Assert.True(safe.IsSupported);
            Assert.False(safe.IsReadOnly);
            Assert.Equal(FormValueKind.None, safe.Value.Kind);
            Assert.False(unsafeButton.IsSupported);
            Assert.True(unsafeButton.IsReadOnly);
            Assert.Contains("blocked action", unsafeButton.UnsupportedReason, StringComparison.OrdinalIgnoreCase);

            var afterInvocation = await registry.InvokePushButtonAsync(new PushButtonInvocation(
                id,
                safe.Id,
                opened.Snapshot.ContentRevision));
            Assert.Equal(opened.Snapshot.ContentRevision, afterInvocation.ContentRevision);

            await Assert.ThrowsAsync<ArgumentException>(() => registry.ApplyFormValueAsync(new FormValueChange(
                id,
                safe.Id,
                FormValue.None(),
                afterInvocation.ContentRevision)).AsTask());
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => registry.InvokePushButtonAsync(new PushButtonInvocation(
                id,
                unsafeButton.Id,
                afterInvocation.ContentRevision)).AsTask());
        }
        finally
        {
            File.Delete(fixture);
        }
    }

    [Fact]
    public async Task Encrypted_documents_require_the_correct_password()
    {
        await using var registry = CreateRegistry();
        var wrongId = DocumentId.New();
        await Assert.ThrowsAsync<PdfiumNativeException>(async () =>
        {
            await registry.OpenAsync(
                new DocumentOpenRequest(wrongId, new PdfSourceHandle("ignored"), "wrong-password"),
                OpenReadHandle(Fixture("synthetic-encrypted.pdf")));
        });

        var id = DocumentId.New();
        var result = await registry.OpenAsync(
            new DocumentOpenRequest(id, new PdfSourceHandle("also-ignored"), "ellie-test"),
            OpenReadHandle(Fixture("synthetic-encrypted.pdf")));
        Assert.True(result.Metadata.IsEncrypted);
        Assert.Equal(2, result.Metadata.PageCount);
    }

    [Fact]
    public async Task Corrupt_documents_fail_closed_and_huge_pages_are_bounded()
    {
        await using var registry = CreateRegistry();
        await Assert.ThrowsAsync<PdfiumNativeException>(async () =>
            await registry.OpenAsync(
                new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle("ignored")),
                OpenReadHandle(Fixture("synthetic-corrupt.pdf"))));

        var id = DocumentId.New();
        await OpenAsync(registry, id, "synthetic-huge-mediabox.pdf");
        var page = await registry.GetPageMetadataAsync(id, 0);
        var request = CreateRenderRequest(id, page.Id, new TileAddress(0, 0, 64, 64, 1));
        var rendered = await registry.RenderAsync(request);
        Assert.InRange(rendered.Width, 1, 66);
        Assert.InRange(rendered.Height, 1, 66);
    }

    [Fact]
    public async Task Stale_page_revision_page_id_and_render_revision_are_rejected()
    {
        await using var registry = CreateRegistry();
        var id = DocumentId.New();
        await OpenAsync(registry, id, "synthetic-vector-small.pdf");
        var page = await registry.GetPageMetadataAsync(id, 0);

        await Assert.ThrowsAsync<WorkerStaleIdentityException>(() =>
            registry.GetPageTextAsync(new PageTextRequest(id, page.Id, 0, new PageContentRevision(1))).AsTask());
        await Assert.ThrowsAsync<WorkerStaleIdentityException>(() =>
            registry.GetPageTextAsync(new PageTextRequest(id, PageId.New(), 0, PageContentRevision.Initial)).AsTask());

        var staleKey = CreateRenderRequest(id, page.Id).Key with
        {
            ContentRevision = new PageContentRevision(1)
        };
        var staleRender = new RenderRequest(
            staleKey,
            RenderGeneration.Initial,
            RenderQuality.Standard,
            EngineJobPriority.VisibleInteractionCritical,
            DateTimeOffset.UtcNow.AddSeconds(10));
        await Assert.ThrowsAsync<WorkerStaleIdentityException>(() => registry.RenderAsync(staleRender).AsTask());
    }

    [Fact]
    public async Task Close_releases_document_owners_and_is_idempotent()
    {
        await using var registry = CreateRegistry();
        var id = DocumentId.New();
        var source = OpenReadHandle(Fixture("synthetic-vector-small.pdf"));
        await registry.OpenAsync(
            new DocumentOpenRequest(id, new PdfSourceHandle("broker-owned-source")),
            source);

        Assert.True(await registry.CloseAsync(id));
        Assert.True(source.IsClosed);
        Assert.False(await registry.CloseAsync(id));
        await Assert.ThrowsAsync<WorkerDocumentNotFoundException>(
            () => registry.GetMetadataAsync(id).AsTask());
    }

    [Fact]
    public async Task Cancellation_is_deterministic_before_open_and_before_each_operation()
    {
        await using var registry = CreateRegistry();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            registry.OpenAsync(
                new DocumentOpenRequest(DocumentId.New(), new PdfSourceHandle("ignored")),
                OpenReadHandle(Fixture("synthetic-vector-small.pdf")),
                cancelled.Token).AsTask());

        var id = DocumentId.New();
        await OpenAsync(registry, id, "synthetic-vector-small.pdf");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.GetMetadataAsync(id, cancelled.Token).AsTask());
        var page = await registry.GetPageMetadataAsync(id, 0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.RenderAsync(CreateRenderRequest(id, page.Id), cancelled.Token).AsTask());
    }

    [Fact(Timeout = 30_000)]
    public async Task Ten_thousand_page_open_metadata_smoke_does_not_create_page_native_owners()
    {
        var stopwatch = Stopwatch.StartNew();
        await using var registry = CreateRegistry();
        var id = DocumentId.New();
        var result = await OpenAsync(registry, id, "synthetic-10000-pages.pdf");

        Assert.Equal(10_000, result.Metadata.PageCount);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"Open took {stopwatch.Elapsed}.");
        var firstPage = await registry.GetPageMetadataAsync(id, 0);
        Assert.NotEqual(Guid.Empty, firstPage.Id.Value);
    }

    [Fact]
    public async Task Rotate_and_delete_preserve_surviving_page_ids_and_advance_only_affected_revisions()
    {
        await using var registry = CreateRegistry();
        var id = DocumentId.New();
        var opened = await OpenAsync(registry, id, "synthetic-vector-small.pdf");
        var originalPages = new[]
        {
            await registry.GetPageMetadataAsync(id, 0),
            await registry.GetPageMetadataAsync(id, 1),
            await registry.GetPageMetadataAsync(id, 2)
        };

        var rotate = new RotatePageRequest(
            id,
            originalPages[1].Id,
            opened.Snapshot.ContentRevision,
            opened.Snapshot.StructureRevision,
            originalPages[1].ContentRevision,
            quarterTurnsClockwise: 1);
        var rotated = await registry.RotatePageAsync(rotate);

        Assert.Equal(opened.Snapshot.ContentRevision.Next(), rotated.ContentRevision);
        Assert.Equal(opened.Snapshot.StructureRevision, rotated.StructureRevision);
        var rotatedPage = await registry.GetPageMetadataAsync(id, 1);
        Assert.Equal(originalPages[1].Id, rotatedPage.Id);
        Assert.Equal(PageRotation.Clockwise90, rotatedPage.Geometry.Rotation);
        Assert.Equal(originalPages[1].ContentRevision.Next(), rotatedPage.ContentRevision);
        Assert.Equal(originalPages[1].AppearanceRevision.Next(), rotatedPage.AppearanceRevision);
        Assert.Equal(originalPages[0].ContentRevision, (await registry.GetPageMetadataAsync(id, 0)).ContentRevision);
        Assert.Equal(originalPages[2].AppearanceRevision, (await registry.GetPageMetadataAsync(id, 2)).AppearanceRevision);

        await Assert.ThrowsAsync<WorkerStaleIdentityException>(() => registry.RotatePageAsync(rotate).AsTask());

        var deleted = await registry.DeletePageAsync(new DeletePageRequest(
            id,
            originalPages[0].Id,
            rotated.ContentRevision,
            rotated.StructureRevision,
            originalPages[0].ContentRevision));

        Assert.Equal(rotated.ContentRevision.Next(), deleted.ContentRevision);
        Assert.Equal(rotated.StructureRevision.Next(), deleted.StructureRevision);
        Assert.Equal(2, deleted.PageCount);
        Assert.Equal(2, (await registry.GetMetadataAsync(id)).PageCount);
        var firstSurvivor = await registry.GetPageMetadataAsync(id, 0);
        var secondSurvivor = await registry.GetPageMetadataAsync(id, 1);
        Assert.Equal(originalPages[1].Id, firstSurvivor.Id);
        Assert.Equal(originalPages[2].Id, secondSurvivor.Id);
        Assert.Equal(PageRotation.Clockwise90, firstSurvivor.Geometry.Rotation);
        Assert.Equal(rotatedPage.ContentRevision, firstSurvivor.ContentRevision);
        Assert.Equal(rotatedPage.AppearanceRevision, firstSurvivor.AppearanceRevision);
    }

    [Fact]
    public async Task Ordered_merge_uses_stable_references_closes_target_and_reopens_in_requested_order()
    {
        await using var registry = CreateRegistry();
        var id = DocumentId.New();
        var opened = await OpenAsync(registry, id, "synthetic-vector-small.pdf");
        var first = await registry.GetPageMetadataAsync(id, 0);
        var third = await registry.GetPageMetadataAsync(id, 2);
        var request = new MergeOrderedPagesRequest(
        [
            MergeReference(opened.Snapshot, third),
            MergeReference(opened.Snapshot, first)
        ]);
        var outputPath = Path.Combine(Path.GetTempPath(), $"elliepdf-merge-{Guid.NewGuid():N}.pdf");

        try
        {
            var target = File.OpenHandle(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read | FileShare.Delete,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await registry.MergeOrderedPagesAsync(request, target);
            Assert.True(target.IsClosed);

            var mergedId = DocumentId.New();
            var reopened = await registry.OpenAsync(
                new DocumentOpenRequest(mergedId, new PdfSourceHandle("broker-owned-merge-output")),
                OpenReadHandle(outputPath));
            Assert.Equal(2, reopened.Snapshot.PageCount);

            var mergedPage0 = await registry.GetPageMetadataAsync(mergedId, 0);
            var mergedText0 = await registry.GetPageTextAsync(new PageTextRequest(
                mergedId,
                mergedPage0.Id,
                0,
                mergedPage0.ContentRevision));
            var mergedPage1 = await registry.GetPageMetadataAsync(mergedId, 1);
            var mergedText1 = await registry.GetPageTextAsync(new PageTextRequest(
                mergedId,
                mergedPage1.Id,
                1,
                mergedPage1.ContentRevision));
            Assert.Contains("Vector page 3", mergedText0.Text, StringComparison.Ordinal);
            Assert.Contains("Vector page 1", mergedText1.Text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task Annotation_stage_commit_reopens_as_native_objects_and_preserves_semantics()
    {
        var sourcePath = Fixture("synthetic-mixed-orientation-links-forms-outlines.pdf");
        var outputPath = Path.Combine(Path.GetTempPath(), $"elliepdf-annotations-{Guid.NewGuid():N}.pdf");
        var original = await InspectPageAsync(sourcePath, 0);
        try
        {
            await using var registry = CreateRegistry();
            var id = DocumentId.New();
            var opened = await OpenAsync(registry, id, "synthetic-mixed-orientation-links-forms-outlines.pdf");
            var page = await registry.GetPageMetadataAsync(id, 0);
            var request = CreateAnnotationRequest(opened.Snapshot, page, includeSignature: true);
            var target = OpenNewWriteHandle(outputPath);

            var staged = await registry.StageAnnotationsAsync(request, target);

            Assert.True(target.IsClosed);
            Assert.Equal(opened.Snapshot.ContentRevision.Next(), staged.ContentRevision);
            Assert.Equal(opened.Snapshot.SavedRevision, staged.SavedRevision);
            var stagedPage = await registry.GetPageMetadataAsync(id, 0);
            Assert.Equal(page.ContentRevision.Next(), stagedPage.ContentRevision);
            Assert.Equal(page.AppearanceRevision.Next(), stagedPage.AppearanceRevision);

            var committed = await registry.FinalizeAnnotationTransactionAsync(
                id,
                request.TransactionId,
                committed: true);
            Assert.Equal(committed.ContentRevision, committed.SavedRevision);
            var idempotent = await registry.FinalizeAnnotationTransactionAsync(
                id,
                request.TransactionId,
                committed: true);
            Assert.Equal(committed, idempotent);
            Assert.True(await registry.CloseAsync(id));

            var saved = await InspectPageAsync(outputPath, 0);
            Assert.Equal(original.AnnotationCount + 3, saved.AnnotationCount);
            Assert.Contains(15, saved.Subtypes);
            Assert.Equal(2, saved.Subtypes.Count(static subtype => subtype == 13));
            Assert.Contains("ellie:test:ink", saved.AnnotationIds);
            Assert.Contains("ellie:test:text", saved.AnnotationIds);
            Assert.Contains("ellie:test:signature", saved.AnnotationIds);
            Assert.Equal(original.Text, saved.Text);
            Assert.NotEqual(original.PixelHash, saved.PixelHash);

            var reopenedId = DocumentId.New();
            var reopened = await registry.OpenAsync(
                new DocumentOpenRequest(reopenedId, new PdfSourceHandle("broker-owned-annotated-output")),
                OpenReadHandle(outputPath));
            Assert.Equal(opened.Metadata, reopened.Metadata);
            Assert.Equal(3, (await registry.GetPageLinksAsync(reopenedId, 0)).Links.Length);
            Assert.Single((await registry.GetFormWidgetsAsync(reopenedId, 0)).Widgets);
            Assert.Equal(8, (await registry.GetOutlineAsync(reopenedId)).Items.Length);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task Annotation_abort_retains_unsaved_edits_and_retry_is_idempotent()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"elliepdf-annotation-stage-{Guid.NewGuid():N}.pdf");
        var retryPath = Path.Combine(Path.GetTempPath(), $"elliepdf-annotation-retry-{Guid.NewGuid():N}.pdf");
        var cleanPath = Path.Combine(Path.GetTempPath(), $"elliepdf-annotation-clean-{Guid.NewGuid():N}.pdf");
        try
        {
            await using var registry = CreateRegistry();
            var id = DocumentId.New();
            var opened = await OpenAsync(registry, id, "synthetic-vector-small.pdf");
            var page = await registry.GetPageMetadataAsync(id, 0);
            var request = CreateAnnotationRequest(opened.Snapshot, page, includeSignature: false);

            var staged = await registry.StageAnnotationsAsync(request, OpenNewWriteHandle(outputPath));
            var stagedPage = await registry.GetPageMetadataAsync(id, 0);
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RotatePageAsync(new RotatePageRequest(
                id,
                stagedPage.Id,
                staged.ContentRevision,
                staged.StructureRevision,
                stagedPage.ContentRevision,
                1)).AsTask());

            var retained = await registry.FinalizeAnnotationTransactionAsync(
                id,
                request.TransactionId,
                committed: false);
            Assert.Equal(staged.ContentRevision, retained.ContentRevision);
            Assert.Equal(opened.Snapshot.SavedRevision, retained.SavedRevision);
            var retainedPage = await registry.GetPageMetadataAsync(id, 0);
            Assert.Equal(stagedPage.ContentRevision, retainedPage.ContentRevision);
            Assert.Equal(stagedPage.AppearanceRevision, retainedPage.AppearanceRevision);
            Assert.Equal(
                retained,
                await registry.FinalizeAnnotationTransactionAsync(id, request.TransactionId, committed: false));
            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.FinalizeAnnotationTransactionAsync(
                id,
                request.TransactionId,
                committed: true).AsTask());

            var retry = new PdfAnnotationSaveRequest(
                Guid.NewGuid(),
                request.DocumentId,
                retained.ContentRevision,
                request.ExpectedStructureRevision,
                [new PdfPageOverlayBatch(
                    retainedPage.PageIndex,
                    retainedPage.Id,
                    retainedPage.ContentRevision,
                    request.Pages[0].Ink,
                    request.Pages[0].Text,
                    request.Pages[0].Signatures)]);
            var retried = await registry.StageAnnotationsAsync(retry, OpenNewWriteHandle(retryPath));
            Assert.Equal(retained.ContentRevision, retried.ContentRevision);
            var committed = await registry.FinalizeAnnotationTransactionAsync(id, retry.TransactionId, committed: true);
            Assert.Equal(committed.ContentRevision, committed.SavedRevision);

            var cleanTarget = OpenNewWriteHandle(cleanPath);
            await registry.SaveAsync(id, committed.ContentRevision, cleanTarget);
            Assert.True(cleanTarget.IsClosed);
            var source = await InspectPageAsync(Fixture("synthetic-vector-small.pdf"), 0);
            var clean = await InspectPageAsync(cleanPath, 0);
            Assert.Equal(source.AnnotationCount + 2, clean.AnnotationCount);
            Assert.Equal(source.Text, clean.Text);
        }
        finally
        {
            File.Delete(outputPath);
            File.Delete(retryPath);
            File.Delete(cleanPath);
        }
    }

    [Fact]
    public async Task Invalid_signature_is_rejected_before_native_annotation_mutation()
    {
        var rejectedPath = Path.Combine(Path.GetTempPath(), $"elliepdf-invalid-signature-{Guid.NewGuid():N}.pdf");
        var retryPath = Path.Combine(Path.GetTempPath(), $"elliepdf-invalid-signature-retry-{Guid.NewGuid():N}.pdf");
        try
        {
            await using var registry = CreateRegistry();
            var id = DocumentId.New();
            var opened = await OpenAsync(registry, id, "synthetic-vector-small.pdf");
            var page = await registry.GetPageMetadataAsync(id, 0);
            var valid = CreateAnnotationRequest(opened.Snapshot, page, includeSignature: false);
            var invalid = new PdfAnnotationSaveRequest(
                Guid.NewGuid(),
                id,
                opened.Snapshot.ContentRevision,
                opened.Snapshot.StructureRevision,
                [new PdfPageOverlayBatch(
                    page.PageIndex,
                    page.Id,
                    page.ContentRevision,
                    valid.Pages[0].Ink,
                    valid.Pages[0].Text,
                    [new PdfSignatureStampAnnotation(
                        "ellie:test:invalid-signature",
                        new PdfOverlayRectangle(24, 24, 80, 32),
                        Convert.ToBase64String("not-an-image"u8))])]);

            await Assert.ThrowsAsync<ArgumentException>(() => registry.StageAnnotationsAsync(
                invalid,
                OpenNewWriteHandle(rejectedPath)).AsTask());
            Assert.Equal(page, await registry.GetPageMetadataAsync(id, 0));

            var staged = await registry.StageAnnotationsAsync(valid, OpenNewWriteHandle(retryPath));
            Assert.Equal(opened.Snapshot.ContentRevision.Next(), staged.ContentRevision);
            _ = await registry.FinalizeAnnotationTransactionAsync(id, valid.TransactionId, committed: false);
        }
        finally
        {
            File.Delete(rejectedPath);
            File.Delete(retryPath);
        }
    }

    [Fact]
    public async Task Candidate_write_failure_after_native_mutation_requires_worker_restart()
    {
        var sourcePath = Fixture("synthetic-vector-small.pdf");
        var sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sourcePath)));
        await using (var registry = CreateRegistry())
        {
            var id = DocumentId.New();
            var opened = await OpenAsync(registry, id, "synthetic-vector-small.pdf");
            var page = await registry.GetPageMetadataAsync(id, 0);
            var request = CreateAnnotationRequest(opened.Snapshot, page, includeSignature: false);

            var failure = await Assert.ThrowsAsync<WorkerRestartRequiredException>(() => registry.StageAnnotationsAsync(
                request,
                OpenReadHandle(sourcePath)).AsTask());
            Assert.IsType<UnauthorizedAccessException>(failure.InnerException);
            await Assert.ThrowsAsync<WorkerRestartRequiredException>(() => registry.GetMetadataAsync(id).AsTask());
        }

        Assert.Equal(
            sourceHash,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(sourcePath))));
    }

    [Fact]
    public async Task Flattened_copy_removes_annotation_editability_without_mutating_live_source()
    {
        var flattenedPath = Path.Combine(Path.GetTempPath(), $"elliepdf-flattened-{Guid.NewGuid():N}.pdf");
        var liveCopyPath = Path.Combine(Path.GetTempPath(), $"elliepdf-live-copy-{Guid.NewGuid():N}.pdf");
        try
        {
            await using var registry = CreateRegistry();
            var id = DocumentId.New();
            var opened = await OpenAsync(registry, id, "synthetic-vector-small.pdf");
            var page = await registry.GetPageMetadataAsync(id, 0);
            var request = CreateAnnotationRequest(opened.Snapshot, page, includeSignature: false);

            var flattenedTarget = OpenNewWriteHandle(flattenedPath);
            await registry.SaveFlattenedCopyAsync(request, flattenedTarget);
            Assert.True(flattenedTarget.IsClosed);
            Assert.Equal(page, await registry.GetPageMetadataAsync(id, 0));

            var liveTarget = OpenNewWriteHandle(liveCopyPath);
            await registry.SaveAsync(id, opened.Snapshot.ContentRevision, liveTarget);
            Assert.True(liveTarget.IsClosed);

            var original = await InspectPageAsync(Fixture("synthetic-vector-small.pdf"), 0);
            var liveCopy = await InspectPageAsync(liveCopyPath, 0);
            var flattened = await InspectPageAsync(flattenedPath, 0);
            Assert.Equal(original.AnnotationCount, liveCopy.AnnotationCount);
            Assert.Equal(original.Text, liveCopy.Text);
            Assert.Equal(original.Text, flattened.Text);
            Assert.Equal(original.AnnotationCount, flattened.AnnotationCount);
            Assert.NotEqual(original.PixelHash, flattened.PixelHash);
        }
        finally
        {
            File.Delete(flattenedPath);
            File.Delete(liveCopyPath);
        }
    }

    [Fact]
    public async Task Flattened_copy_without_new_overlays_flattens_existing_annotations_on_every_page()
    {
        var stagedPath = Path.Combine(Path.GetTempPath(), $"elliepdf-preflatten-stage-{Guid.NewGuid():N}.pdf");
        var flattenedPath = Path.Combine(Path.GetTempPath(), $"elliepdf-preflatten-output-{Guid.NewGuid():N}.pdf");
        var liveCopyPath = Path.Combine(Path.GetTempPath(), $"elliepdf-preflatten-live-{Guid.NewGuid():N}.pdf");
        try
        {
            await using var registry = CreateRegistry();
            var id = DocumentId.New();
            var opened = await OpenAsync(registry, id, "synthetic-mixed-orientation-links-forms-outlines.pdf");
            var page = await registry.GetPageMetadataAsync(id, 1);
            var stageRequest = CreateAnnotationRequest(opened.Snapshot, page, includeSignature: false);
            var staged = await registry.StageAnnotationsAsync(stageRequest, OpenNewWriteHandle(stagedPath));
            var retained = await registry.FinalizeAnnotationTransactionAsync(
                id,
                stageRequest.TransactionId,
                committed: false);
            var retainedPage = await registry.GetPageMetadataAsync(id, 1);

            var flattenExistingRequest = new PdfAnnotationSaveRequest(
                Guid.NewGuid(),
                id,
                retained.ContentRevision,
                retained.StructureRevision,
                []);
            await registry.SaveFlattenedCopyAsync(
                flattenExistingRequest,
                OpenNewWriteHandle(flattenedPath));
            Assert.Equal(retainedPage, await registry.GetPageMetadataAsync(id, 1));

            await registry.SaveAsync(id, staged.ContentRevision, OpenNewWriteHandle(liveCopyPath));
            var original = await InspectPageAsync(
                Fixture("synthetic-mixed-orientation-links-forms-outlines.pdf"),
                1);
            var liveCopy = await InspectPageAsync(liveCopyPath, 1);
            var flattened = await InspectPageAsync(flattenedPath, 1);
            Assert.Equal(original.AnnotationCount + 2, liveCopy.AnnotationCount);
            Assert.True(flattened.AnnotationCount < liveCopy.AnnotationCount);
            Assert.DoesNotContain("ellie:test:ink", flattened.AnnotationIds);
            Assert.DoesNotContain("ellie:test:text", flattened.AnnotationIds);
            Assert.NotEqual(original.PixelHash, flattened.PixelHash);
        }
        finally
        {
            File.Delete(stagedPath);
            File.Delete(flattenedPath);
            File.Delete(liveCopyPath);
        }
    }

    [Fact]
    public async Task Protected_document_denies_page_assembly_and_rejected_merge_closes_target()
    {
        await using var registry = CreateRegistry();
        var id = DocumentId.New();
        var opened = await OpenAsync(registry, id, "synthetic-encrypted.pdf", "ellie-test");
        var permissions = await registry.GetPermissionsAsync(id);
        Assert.False(permissions.CanModify);
        var page = await registry.GetPageMetadataAsync(id, 0);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => registry.RotatePageAsync(new RotatePageRequest(
            id,
            page.Id,
            opened.Snapshot.ContentRevision,
            opened.Snapshot.StructureRevision,
            page.ContentRevision,
            1)).AsTask());

        var outputPath = Path.Combine(Path.GetTempPath(), $"elliepdf-denied-merge-{Guid.NewGuid():N}.pdf");
        try
        {
            var target = File.OpenHandle(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read | FileShare.Delete);
            var staleReference = new PageMergeReference(
                opened.Snapshot.Id,
                page.Id,
                opened.Snapshot.ContentRevision.Next(),
                opened.Snapshot.StructureRevision,
                page.ContentRevision);
            await Assert.ThrowsAsync<WorkerStaleIdentityException>(() => registry.MergeOrderedPagesAsync(
                new MergeOrderedPagesRequest([staleReference]),
                target).AsTask());
            Assert.True(target.IsClosed);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private static WorkerDocumentRegistry CreateRegistry() => new(AppContext.BaseDirectory);

    private static PageMergeReference MergeReference(DocumentSnapshot snapshot, PageMetadata page) => new(
        snapshot.Id,
        page.Id,
        snapshot.ContentRevision,
        snapshot.StructureRevision,
        page.ContentRevision);

    private static async Task<DocumentOpenResult> OpenAsync(WorkerDocumentRegistry registry, DocumentId id, string fixture, string? password = null)
        => await registry.OpenAsync(
            new DocumentOpenRequest(id, new PdfSourceHandle("broker-owned-source"), password),
            OpenReadHandle(Fixture(fixture)));

    private static RenderRequest CreateRenderRequest(DocumentId documentId, PageId pageId, TileAddress? tile = null)
        => new(
            new RenderKey(
                documentId,
                pageId,
                PageContentRevision.Initial,
                PageAppearanceRevision.Initial,
                tile ?? new TileAddress(0, 0, 512, 512, 1),
                RasterScale64.FromPhysicalPixelsPerPoint(1),
                PageRotation.None,
                RenderMode.Normal),
            RenderGeneration.Initial,
            RenderQuality.Standard,
            EngineJobPriority.VisibleInteractionCritical,
            DateTimeOffset.UtcNow.AddSeconds(10));

    private static SafeFileHandle OpenReadHandle(string path)
        => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    private static SafeFileHandle OpenNewWriteHandle(string path)
        => File.OpenHandle(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static PdfAnnotationSaveRequest CreateAnnotationRequest(
        DocumentSnapshot snapshot,
        PageMetadata page,
        bool includeSignature)
    {
        PdfSignatureStampAnnotation[] signatures = includeSignature
            ?
            [
                new PdfSignatureStampAnnotation(
                    "ellie:test:signature",
                    new PdfOverlayRectangle(360, 250, 32, 24),
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9WlGnnwAAAAASUVORK5CYII=")
            ]
            : [];
        return new PdfAnnotationSaveRequest(
            Guid.NewGuid(),
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
                            "ellie:test:ink",
                            [
                                new PdfOverlayPoint(360, 120),
                                new PdfOverlayPoint(410, 150),
                                new PdfOverlayPoint(470, 125)
                            ],
                            new PdfOverlayColor(20, 80, 220),
                            4)
                    ],
                    [
                        new PdfTextStampAnnotation(
                            "ellie:test:text",
                            new PdfOverlayRectangle(360, 180, 170, 42),
                            "Native ElliePdf note",
                            14,
                            new PdfOverlayColor(180, 30, 30),
                            isBold: true,
                            isItalic: false)
                    ],
                    signatures)
            ]);
    }

    private static async Task<PageInspection> InspectPageAsync(string path, int pageIndex)
    {
        await using var lane = new PdfiumEngineLane(AppContext.BaseDirectory, "annotation-inspection");
        return await lane.InvokeAsync(engine =>
        {
            using var document = engine.LoadDocument(path, null)
                ?? throw engine.CreateException("Unable to inspect the saved PDF.");
            using var page = engine.LoadPage(document, pageIndex)
                ?? throw engine.CreateException("Unable to inspect the saved page.");
            var count = engine.GetPageAnnotationCount(page);
            var subtypes = new List<int>(count);
            var ids = new List<string>(count);
            for (var index = 0; index < count; index++)
            {
                using var annotation = engine.GetPageAnnotation(page, index);
                if (annotation is null) continue;
                subtypes.Add(engine.GetAnnotationSubtype(annotation));
                var id = engine.GetAnnotationStringValue(annotation, "NM");
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
            using var textPage = engine.LoadTextPage(page)
                ?? throw engine.CreateException("Unable to inspect the saved page text.");
            var text = engine.GetText(textPage, 0, engine.CountCharacters(textPage));
            using var bitmap = engine.CreateBitmap(612, 792);
            engine.FillBitmap(bitmap, 0, 0, bitmap.Width, bitmap.Height);
            engine.RenderPage(page, bitmap, null, bitmap.Width, bitmap.Height);
            var pixels = engine.CopyBitmapBytes(bitmap, bitmap.ByteLength);
            return new PageInspection(
                count,
                [.. subtypes],
                [.. ids],
                text,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels)));
        });
    }

    private sealed record PageInspection(
        int AnnotationCount,
        int[] Subtypes,
        string[] AnnotationIds,
        string Text,
        string PixelHash);

    private static string CreatePushButtonFixture()
    {
        var path = Path.Combine(Path.GetTempPath(), $"elliepdf-push-buttons-{Guid.NewGuid():N}.pdf");
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

    private static string Fixture(string name)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "testdata", "generated", name);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }

        throw new FileNotFoundException($"Generated test fixture was not found: {name}");
    }
}
