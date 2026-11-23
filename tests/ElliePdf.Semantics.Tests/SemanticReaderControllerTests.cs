using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;
using ElliePdf.Semantics;
using Xunit;

namespace ElliePdf.Semantics.Tests;

public sealed class SemanticReaderControllerTests
{
    [Fact]
    public async Task Loads_an_incremental_immutable_page_and_automation_projection()
    {
        await using var controller = new SemanticReaderController(new FakeSession());
        SemanticPageSnapshot page = await controller.GetPageAsync(0);
        AutomationPageSnapshot automation = await controller.GetAutomationPageAsync(0);

        Assert.Equal("hello ElliePdf", page.Text.Text);
        Assert.Single(page.Links);
        Assert.Single(automation.Forms);
        Assert.Equal("Name", automation.Forms[0].FieldName);
    }

    [Fact]
    public async Task Evicted_page_projection_is_released_and_can_be_loaded_again()
    {
        await using var controller = new SemanticReaderController(new FakeSession());
        SemanticPageSnapshot first = await controller.GetPageAsync(0);

        Assert.True(controller.TryGetPage(0, out var resident));
        Assert.Same(first, resident);
        Assert.True(controller.EvictPage(0));
        Assert.False(controller.TryGetPage(0, out _));
        Assert.False(controller.EvictPage(0));

        SemanticPageSnapshot reloaded = await controller.GetPageAsync(0);
        Assert.Equal(first.Text.Text, reloaded.Text.Text);
        Assert.True(controller.TryGetPage(0, out _));
    }

    [Fact]
    public async Task Search_streams_first_result_and_supports_wrapped_navigation()
    {
        await using var controller = new SemanticReaderController(new FakeSession());
        var results = new List<SearchResult>();
        await foreach (SearchResult result in controller.SearchAsync("ElliePdf")) results.Add(result);

        Assert.Equal(2, results.Count);
        Assert.Equal(0, controller.SearchState.CurrentIndex);
        Assert.Equal(results[1], controller.MoveSearchResult());
        Assert.Equal(results[0], controller.MoveSearchResult());
        Assert.Equal(results[1], controller.MoveSearchResult(reverse: true));
    }

    [Fact]
    public async Task First_search_result_is_published_before_the_next_page_is_requested()
    {
        var session = new FakeSession();
        await using var controller = new SemanticReaderController(session);
        await using var results = controller.SearchAsync("ElliePdf").GetAsyncEnumerator();

        Assert.True(await results.MoveNextAsync());
        Assert.Equal(0, results.Current.PageIndex);
        Assert.Equal([0], session.RequestedPageMetadata);
    }

    [Fact]
    public async Task Copy_and_forms_respect_permissions_and_form_revision_changes()
    {
        var restricted = new FakeSession(new PdfPermissions(canCopy: false, canFillForms: false));
        await using var controller = new SemanticReaderController(restricted);
        SemanticPageSnapshot page = await controller.GetPageAsync(0);
        SelectionState selection = controller.Select(page, 0, 5);

        Assert.False((await controller.CopyAsync(selection)).IsAllowed);
        Assert.False((await controller.UpdateFormAsync(page.Forms[0], FormValue.TextValue("new"))).Applied);

        await using var allowed = new SemanticReaderController(new FakeSession());
        SemanticFormSnapshot form = (await allowed.GetPageAsync(0)).Forms[0];
        FormUpdateDecision update = await allowed.UpdateFormAsync(form, FormValue.TextValue("new"));
        Assert.True(update.Applied);
        Assert.Equal(1, update.ContentRevision!.Value.Value);
    }

    [Fact]
    public async Task Selection_maps_ordered_text_across_loaded_pages_and_preserves_page_breaks()
    {
        await using var controller = new SemanticReaderController(new FakeSession());
        var first = await controller.GetPageAsync(0);
        var second = await controller.GetPageAsync(1);

        SelectionState selection = controller.Select(
            [first, second],
            new TextPosition(0, 6),
            new TextPosition(1, 8));

        Assert.True(selection.IsCrossPage);
        Assert.Equal("ElliePdf\nElliePdf", selection.Text);
        Assert.Equal(2, selection.Segments.Length);
        Assert.Equal((0, 6, 14), (selection.Segments[0].PageIndex, selection.Segments[0].Start, selection.Segments[0].End));
        Assert.Equal((1, 0, 8), (selection.Segments[1].PageIndex, selection.Segments[1].Start, selection.Segments[1].End));
        Assert.Equal(selection, controller.Selection);
    }

    [Fact]
    public void Word_and_visual_line_selection_use_unicode_boundaries_and_pdf_geometry()
    {
        var id = new DocumentId(Guid.Parse("9d13c5fd-2081-4c23-9af9-8d7fcd4b99bb"));
        var pageId = new PageId(Guid.Parse("ace81fcb-8218-4580-838e-46c7b7fdd90a"));
        var text = new PageTextResult(
            id,
            pageId,
            0,
            PageContentRevision.Initial,
            "Hello world\nNext",
            [
                new TextSpan(0, "Hello ", new PdfRect(10, 700, 70, 712)),
                new TextSpan(6, "world", new PdfRect(72, 700, 120, 712)),
                new TextSpan(12, "Next", new PdfRect(10, 680, 45, 692))
            ]);
        var page = new SemanticPageSnapshot(
            new PageMetadata(pageId, 0, new PdfSize(612, 792)),
            text,
            [],
            []);

        Assert.Equal((0, 5), SemanticTextSelection.SelectWord(text.Text, 1));
        Assert.Equal((6, 11), SemanticTextSelection.SelectWord(text.Text, 6));
        Assert.Equal((0, 11), SemanticTextSelection.SelectVisualLine(page, 9));
        Assert.Equal((12, 16), SemanticTextSelection.SelectVisualLine(page, 13));
        Assert.Equal(2, SemanticTextSelection.VisualLines(page).Length);
    }

    [Fact]
    public async Task Link_policy_is_fail_closed_and_internal_navigation_is_not_external()
    {
        await using var controller = new SemanticReaderController(new FakeSession());
        var blocked = new PdfLink(PdfLinkKind.Uri, Bounds, "file:///secret.pdf");
        var internalLink = new PdfLink(PdfLinkKind.Page, Bounds, targetPageIndex: 1);

        Assert.Equal(LinkActivationKind.Blocked, controller.DecideLink(blocked).Kind);
        Assert.Equal(LinkActivationKind.InternalNavigation, controller.DecideLink(internalLink).Kind);
    }

    [Fact]
    public async Task Borrowed_session_is_not_disposed_with_the_semantic_projection()
    {
        var session = new FakeSession();
        await using (var controller = new SemanticReaderController(session, ownsSession: false))
        {
            _ = await controller.GetPageAsync(0);
        }

        Assert.False(session.WasDisposed);
        await session.DisposeAsync();
        Assert.True(session.WasDisposed);
    }

    [Theory]
    [InlineData(PageRotation.None, 10, 150, 30, 40)]
    [InlineData(PageRotation.Clockwise90, 10, 10, 40, 30)]
    [InlineData(PageRotation.Clockwise180, 160, 10, 30, 40)]
    [InlineData(PageRotation.Clockwise270, 150, 160, 40, 30)]
    public void Semantic_geometry_tracks_page_rotation(
        PageRotation rotation,
        double x,
        double y,
        double width,
        double height)
    {
        var page = new PageGeometry(new PdfRect(0, 0, 200, 200), new PdfRect(0, 0, 200, 200), rotation);
        var result = SemanticGeometryProjection.Project(new PdfRect(10, 10, 40, 50), page, 1);

        Assert.Equal(new SemanticDisplayRect(x, y, width, height), result);
        Assert.True(result.ExpandToMinimum(44, 44).Width >= 44);
        Assert.True(result.ExpandToMinimum(44, 44).Height >= 44);
    }

    private static readonly PdfRect Bounds = new(0, 0, 100, 20);

    private sealed class FakeSession : IPdfEngineSession
    {
        private readonly PdfPermissions _permissions;
        private readonly DocumentId _id = new(Guid.Parse("9d13c5fd-2081-4c23-9af9-8d7fcd4b99bb"));
        private readonly PageId[] _pageIds = [new(Guid.Parse("ace81fcb-8218-4580-838e-46c7b7fdd90a")), new(Guid.Parse("cc9e76b4-e126-417e-9078-cd5f8c5ddf18"))];
        private readonly FormFieldId _fieldId = new(Guid.Parse("be36f4cb-76fa-4451-bd71-8bc7af5352a4"));
        public FakeSession(PdfPermissions? permissions = null) => _permissions = permissions ?? new PdfPermissions();
        public bool WasDisposed { get; private set; }
        public List<int> RequestedPageMetadata { get; } = [];
        public DocumentId DocumentId => _id;
        public ValueTask DisposeAsync() { WasDisposed = true; return ValueTask.CompletedTask; }
        public ValueTask<PdfMetadata> GetMetadataAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new PdfMetadata(2, hasOutline: true, hasForms: true));
        public ValueTask<PageMetadata> GetPageMetadataAsync(int pageIndex, CancellationToken cancellationToken)
        {
            RequestedPageMetadata.Add(pageIndex);
            return ValueTask.FromResult(new PageMetadata(_pageIds[pageIndex], pageIndex, new PdfSize(612, 792)));
        }
        public ValueTask<IPixelBufferLease> RenderAsync(RenderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<PageTextResult> GetPageTextAsync(PageTextRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(new PageTextResult(_id, request.PageId, request.PageIndex, request.ContentRevision, request.PageIndex == 0 ? "hello ElliePdf" : "ElliePdf again", [new TextSpan(0, request.PageIndex == 0 ? "hello" : "ElliePdf", Bounds)]));
        public ValueTask<IReadOnlyList<SearchResult>> SearchPageAsync(PageSearchRequest request, CancellationToken cancellationToken)
        {
            int position = request.Page.PageIndex == 0 ? 6 : 0;
            IReadOnlyList<SearchResult> results = [new SearchResult(_id, request.Page.PageId, request.Page.PageIndex, request.Page.ContentRevision, request.Generation, position, request.Query.Length, request.Query, [Bounds])];
            return ValueTask.FromResult(results);
        }
        public ValueTask<OutlineResult> GetOutlineAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new OutlineResult([new OutlineItem("Start", destinationPageIndex: 0)]));
        public ValueTask<PageLinks> GetPageLinksAsync(int pageIndex, CancellationToken cancellationToken) => ValueTask.FromResult(new PageLinks(_id, _pageIds[pageIndex], pageIndex, [new PdfLink(PdfLinkKind.Uri, Bounds, "https://example.com")]));
        public ValueTask<FormWidgetsResult> GetFormWidgetsAsync(int pageIndex, CancellationToken cancellationToken) => ValueTask.FromResult(new FormWidgetsResult(_id, pageIndex == 0 ? [new FormWidget(_fieldId, _id, _pageIds[0], 0, FormWidgetType.Text, "Name", Bounds, FormValue.TextValue("old"))] : []));
        public ValueTask<PdfPermissions> GetPermissionsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_permissions);
        public ValueTask ApplyFormValueAsync(FormValueChange change, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
