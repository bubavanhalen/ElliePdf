using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;
using Xunit;

namespace ElliePdf.Pdf.Contracts.Tests;

public sealed class ContractBoundsTests
{
    [Fact]
    public void DocumentOpenRejectsOversizedPassword()
    {
        var id = DocumentId.New();
        Assert.Throws<ArgumentOutOfRangeException>(() => new DocumentOpenRequest(id, new PdfSourceHandle("source"), new string('x', PdfContractLimits.MaxPasswordLength + 1)));
    }

    [Fact]
    public void PageMetadataRejectsInvalidGeometry()
    {
        var media = new PdfRect(0, 0, 100, 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageGeometry(media, new PdfRect(0, 0, 101, 100)));
    }

    [Fact]
    public void SearchQueryIsBounded()
    {
        var page = new PageTextRequest(DocumentId.New(), PageId.New(), 0, PageContentRevision.Initial);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageSearchRequest(page, new string('q', PdfContractLimits.MaxSearchQueryLength + 1), SearchGeneration.Initial));
    }

    [Fact]
    public void PixelLeaseRejectsOversizedDimensions()
    {
        var key = NewRenderKey();
        Assert.Throws<ArgumentOutOfRangeException>(() => new PixelBufferLease(Guid.NewGuid(), "buffer", 0, 16, PdfContractLimits.MaxPixelDimension + 1, 1, 16, PixelFormat.Bgra8Premultiplied, key));
    }

    [Fact]
    public void LabsPageMutationAndMergeContractsAreBounded()
    {
        var documentId = DocumentId.New();
        var pageId = PageId.New();
        Assert.Throws<ArgumentOutOfRangeException>(() => new RotatePageRequest(
            documentId,
            pageId,
            ContentRevision.Initial,
            StructureRevision.Initial,
            PageContentRevision.Initial,
            quarterTurnsClockwise: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeletePageRequest(
            documentId,
            pageId,
            new ContentRevision(-1),
            StructureRevision.Initial,
            PageContentRevision.Initial));
        Assert.Throws<ArgumentException>(() => new MergeOrderedPagesRequest([]));

        var reference = new PageMergeReference(
            documentId,
            pageId,
            ContentRevision.Initial,
            StructureRevision.Initial,
            PageContentRevision.Initial);
        Assert.Throws<ArgumentOutOfRangeException>(() => new MergeOrderedPagesRequest(
            Enumerable.Repeat(reference, PdfContractLimits.MaxCollectionCount + 1)));
    }

    [Fact]
    public void NativeAnnotationContractsAreBoundedAndRequireTransactionWideStableIds()
    {
        var documentId = DocumentId.New();
        var firstPageId = PageId.New();
        var secondPageId = PageId.New();
        var ink = new PdfInkAnnotation(
            "ellie:ink:1",
            [new PdfOverlayPoint(1, 2), new PdfOverlayPoint(3, 4)],
            new PdfOverlayColor(1, 2, 3, 4),
            2);
        var text = new PdfTextStampAnnotation(
            "ellie:text:1",
            new PdfOverlayRectangle(10, 20, 100, 30),
            "bounded note",
            14,
            new PdfOverlayColor(10, 20, 30),
            false,
            false);
        var request = new PdfAnnotationSaveRequest(
            Guid.NewGuid(),
            documentId,
            ContentRevision.Initial,
            StructureRevision.Initial,
            [new PdfPageOverlayBatch(0, firstPageId, PageContentRevision.Initial, [ink], [text], [])]);

        Assert.Equal(2, request.Pages[0].Ink.Length + request.Pages[0].Text.Length);
        Assert.Empty(new PdfAnnotationSaveRequest(
            Guid.NewGuid(),
            documentId,
            ContentRevision.Initial,
            StructureRevision.Initial,
            []).Pages);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfInkAnnotation(
            "ellie:ink:too-short",
            [new PdfOverlayPoint(1, 2)],
            new PdfOverlayColor(0, 0, 0),
            2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfInkAnnotation(
            "ellie:ink:too-thick",
            [new PdfOverlayPoint(1, 2), new PdfOverlayPoint(3, 4)],
            new PdfOverlayColor(0, 0, 0),
            129));
        Assert.Throws<ArgumentException>(() => new PdfSignatureStampAnnotation(
            "ellie:signature:bad",
            new PdfOverlayRectangle(0, 0, 10, 10),
            "!!!!"));
        Assert.Throws<ArgumentException>(() => new PdfAnnotationSaveRequest(
            Guid.NewGuid(),
            documentId,
            ContentRevision.Initial,
            StructureRevision.Initial,
            [
                new PdfPageOverlayBatch(0, firstPageId, PageContentRevision.Initial, [ink], [], []),
                new PdfPageOverlayBatch(1, secondPageId, PageContentRevision.Initial,
                    [new PdfInkAnnotation(
                        ink.AnnotationId,
                        [new PdfOverlayPoint(5, 6), new PdfOverlayPoint(7, 8)],
                        new PdfOverlayColor(0, 0, 0),
                        1)],
                    [],
                    [])
            ]));
    }

    [Fact]
    public void AnnotationPermissionIsIndependentFromGeneralModificationPermission()
    {
        var permissions = new PdfPermissions(PdfPermissionFlags.Annotate, isEncrypted: true, isOwnerPasswordAuthenticated: false);

        Assert.True(permissions.CanAnnotate);
        Assert.False(permissions.CanModify);
        Assert.False(permissions.CanCopy);
    }

    [Fact]
    public void ConveniencePermissionsAllowAnnotationsOnlyWhenModificationIsAllowed()
    {
        Assert.True(new PdfPermissions().CanAnnotate);
        Assert.False(new PdfPermissions(canModify: false).CanAnnotate);
    }

    [Theory]
    [InlineData("https://example.invalid/elliepdf", ExternalLinkDecision.Allowed, "https")]
    [InlineData("http://example.invalid/elliepdf", ExternalLinkDecision.Allowed, "http")]
    [InlineData("mailto:ellie@example.invalid", ExternalLinkDecision.Allowed, "mailto")]
    [InlineData("javascript:alert('blocked')", ExternalLinkDecision.BlockedScheme, null)]
    [InlineData("file:///C:/blocked", ExternalLinkDecision.BlockedScheme, null)]
    [InlineData("", ExternalLinkDecision.BlockedMalformed, null)]
    [InlineData("not a uri", ExternalLinkDecision.BlockedMalformed, null)]
    public void ExternalLinkPolicyIsFailClosed(string value, ExternalLinkDecision expected, string? expectedScheme)
    {
        var decision = PdfExternalLinkPolicy.Evaluate(value, out var safeUri);

        Assert.Equal(expected, decision);
        Assert.Equal(expectedScheme, safeUri?.Scheme);
    }

    [Fact]
    public void BlockedLinksAndUnsupportedWidgetsRequireReasons()
    {
        Assert.Throws<ArgumentException>(() => new PdfLink(
            PdfLinkKind.Uri,
            new PdfRect(0, 0, 10, 10),
            uri: "javascript:alert('blocked')",
            isSafeToActivate: false));

        Assert.Throws<ArgumentException>(() => new FormWidget(
            new FormFieldId(Guid.NewGuid()),
            DocumentId.New(),
            PageId.New(),
            0,
            FormWidgetType.Unsupported,
            "widget",
            new PdfRect(0, 0, 10, 10),
            FormValue.None(),
            isSupported: false));
    }

    private static RenderKey NewRenderKey() => new(DocumentId.New(), PageId.New(), PageContentRevision.Initial, PageAppearanceRevision.Initial, new TileAddress(0, 0, 1, 1, 1), new RasterScale64(64), PageRotation.None, RenderMode.Normal);
}
