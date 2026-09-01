using System.Collections.Immutable;
using System.Text.Json.Serialization;
using ElliePdf.Domain.Documents;

namespace ElliePdf.Pdf.Contracts;

public sealed record DocumentOpenRequest
{
    public DocumentOpenRequest(DocumentId documentId, PdfSourceHandle source, string? password = null)
    {
        if (documentId.Value == Guid.Empty) throw new ArgumentException("The document id must not be empty.", nameof(documentId));
        DocumentId = documentId;
        Source = source.Validate();
        Password = PdfContractLimits.OptionalString(password, PdfContractLimits.MaxPasswordLength, nameof(password));
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public PdfContractVersion Version => ContractVersion;
    public DocumentId DocumentId { get; }
    public PdfSourceHandle Source { get; }
    public string? Password { get; }
}

public sealed record DocumentOpenResult
{
    public DocumentOpenResult(DocumentSnapshot snapshot, PdfMetadata metadata)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public DocumentSnapshot Snapshot { get; }
    public PdfMetadata Metadata { get; }
}

/// <summary>Optimistic-concurrency request for a persistent page rotation.</summary>
public sealed record RotatePageRequest
{
    public RotatePageRequest(
        DocumentId documentId,
        PageId pageId,
        ContentRevision expectedContentRevision,
        StructureRevision expectedStructureRevision,
        PageContentRevision expectedPageContentRevision,
        int quarterTurnsClockwise)
    {
        if (documentId.Value == Guid.Empty) throw new ArgumentException("The document id must not be empty.", nameof(documentId));
        if (pageId.Value == Guid.Empty) throw new ArgumentException("The page id must not be empty.", nameof(pageId));
        if (expectedContentRevision.Value < 0) throw new ArgumentOutOfRangeException(nameof(expectedContentRevision));
        if (expectedStructureRevision.Value < 0) throw new ArgumentOutOfRangeException(nameof(expectedStructureRevision));
        if (expectedPageContentRevision.Value < 0) throw new ArgumentOutOfRangeException(nameof(expectedPageContentRevision));
        if (quarterTurnsClockwise is < -3 or > 3 || quarterTurnsClockwise == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quarterTurnsClockwise),
                "Rotation must be one to three quarter turns clockwise or counter-clockwise.");
        }

        DocumentId = documentId;
        PageId = pageId;
        ExpectedContentRevision = expectedContentRevision;
        ExpectedStructureRevision = expectedStructureRevision;
        ExpectedPageContentRevision = expectedPageContentRevision;
        QuarterTurnsClockwise = quarterTurnsClockwise;
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public DocumentId DocumentId { get; }
    public PageId PageId { get; }
    public ContentRevision ExpectedContentRevision { get; }
    public StructureRevision ExpectedStructureRevision { get; }
    public PageContentRevision ExpectedPageContentRevision { get; }
    public int QuarterTurnsClockwise { get; }
}

/// <summary>Optimistic-concurrency request for removing one stable page identity.</summary>
public sealed record DeletePageRequest
{
    public DeletePageRequest(
        DocumentId documentId,
        PageId pageId,
        ContentRevision expectedContentRevision,
        StructureRevision expectedStructureRevision,
        PageContentRevision expectedPageContentRevision)
    {
        if (documentId.Value == Guid.Empty) throw new ArgumentException("The document id must not be empty.", nameof(documentId));
        if (pageId.Value == Guid.Empty) throw new ArgumentException("The page id must not be empty.", nameof(pageId));
        if (expectedContentRevision.Value < 0) throw new ArgumentOutOfRangeException(nameof(expectedContentRevision));
        if (expectedStructureRevision.Value < 0) throw new ArgumentOutOfRangeException(nameof(expectedStructureRevision));
        if (expectedPageContentRevision.Value < 0) throw new ArgumentOutOfRangeException(nameof(expectedPageContentRevision));

        DocumentId = documentId;
        PageId = pageId;
        ExpectedContentRevision = expectedContentRevision;
        ExpectedStructureRevision = expectedStructureRevision;
        ExpectedPageContentRevision = expectedPageContentRevision;
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public DocumentId DocumentId { get; }
    public PageId PageId { get; }
    public ContentRevision ExpectedContentRevision { get; }
    public StructureRevision ExpectedStructureRevision { get; }
    public PageContentRevision ExpectedPageContentRevision { get; }
}

/// <summary>A stable source page captured for an ordered, transactional merge.</summary>
public sealed record PageMergeReference
{
    public PageMergeReference(
        DocumentId documentId,
        PageId pageId,
        ContentRevision expectedContentRevision,
        StructureRevision expectedStructureRevision,
        PageContentRevision expectedPageContentRevision,
        PageRotation? rotation = null)
    {
        if (documentId.Value == Guid.Empty) throw new ArgumentException("The document id must not be empty.", nameof(documentId));
        if (pageId.Value == Guid.Empty) throw new ArgumentException("The page id must not be empty.", nameof(pageId));
        if (expectedContentRevision.Value < 0) throw new ArgumentOutOfRangeException(nameof(expectedContentRevision));
        if (expectedStructureRevision.Value < 0) throw new ArgumentOutOfRangeException(nameof(expectedStructureRevision));
        if (expectedPageContentRevision.Value < 0) throw new ArgumentOutOfRangeException(nameof(expectedPageContentRevision));

        DocumentId = documentId;
        PageId = pageId;
        ExpectedContentRevision = expectedContentRevision;
        ExpectedStructureRevision = expectedStructureRevision;
        ExpectedPageContentRevision = expectedPageContentRevision;
        Rotation = rotation;
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public DocumentId DocumentId { get; }
    public PageId PageId { get; }
    public ContentRevision ExpectedContentRevision { get; }
    public StructureRevision ExpectedStructureRevision { get; }
    public PageContentRevision ExpectedPageContentRevision { get; }
    /// <summary>Optional absolute output rotation. Null preserves the source page rotation.</summary>
    public PageRotation? Rotation { get; }
}

public sealed record MergeOrderedPagesRequest
{
    public MergeOrderedPagesRequest(IEnumerable<PageMergeReference> pagesInOrder)
        : this([.. pagesInOrder])
    {
    }

    [JsonConstructor]
    public MergeOrderedPagesRequest(ImmutableArray<PageMergeReference> pagesInOrder)
    {
        if (pagesInOrder.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one page is required for an ordered merge.", nameof(pagesInOrder));
        }

        PagesInOrder = PdfContractLimits.ReadOnly(
            pagesInOrder,
            PdfContractLimits.MaxCollectionCount,
            nameof(pagesInOrder));
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public ImmutableArray<PageMergeReference> PagesInOrder { get; }
}

/// <summary>A bounded RGB color carried as values across the worker boundary.</summary>
public sealed record PdfOverlayColor(byte Red, byte Green, byte Blue, byte Alpha = byte.MaxValue);

/// <summary>A finite page-space point. ElliePdf overlay coordinates use a top-left origin.</summary>
public sealed record PdfOverlayPoint
{
    public const double MaximumCoordinate = 10_000_000d;

    [JsonConstructor]
    public PdfOverlayPoint(double x, double y)
    {
        if (!double.IsFinite(x) || Math.Abs(x) > MaximumCoordinate)
            throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y) || Math.Abs(y) > MaximumCoordinate)
            throw new ArgumentOutOfRangeException(nameof(y));
        X = x;
        Y = y;
    }

    public double X { get; }
    public double Y { get; }
}

/// <summary>A finite positive rectangle in top-left-origin page space.</summary>
public sealed record PdfOverlayRectangle
{
    [JsonConstructor]
    public PdfOverlayRectangle(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || Math.Abs(x) > PdfOverlayPoint.MaximumCoordinate)
            throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y) || Math.Abs(y) > PdfOverlayPoint.MaximumCoordinate)
            throw new ArgumentOutOfRangeException(nameof(y));
        if (!double.IsFinite(width) || width <= 0 || width > PdfOverlayPoint.MaximumCoordinate)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height <= 0 || height > PdfOverlayPoint.MaximumCoordinate)
            throw new ArgumentOutOfRangeException(nameof(height));
        _ = checked(x + width);
        _ = checked(y + height);
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
}

public sealed record PdfInkAnnotation
{
    public const int MaximumPoints = 32_768;

    public PdfInkAnnotation(
        string annotationId,
        IEnumerable<PdfOverlayPoint> points,
        PdfOverlayColor color,
        double thickness)
        : this(annotationId, [.. points], color, thickness)
    {
    }

    [JsonConstructor]
    public PdfInkAnnotation(
        string annotationId,
        ImmutableArray<PdfOverlayPoint> points,
        PdfOverlayColor color,
        double thickness)
    {
        AnnotationId = ValidateAnnotationId(annotationId);
        if (points.IsDefault || points.Length is < 2 or > MaximumPoints)
            throw new ArgumentOutOfRangeException(nameof(points));
        if (points.Any(static point => point is null))
            throw new ArgumentException("Ink points must not contain null values.", nameof(points));
        if (color is null)
            throw new ArgumentNullException(nameof(color));
        if (!double.IsFinite(thickness) || thickness is <= 0 or > 128)
            throw new ArgumentOutOfRangeException(nameof(thickness));
        Points = points;
        Color = color;
        Thickness = thickness;
    }

    public string AnnotationId { get; }
    public ImmutableArray<PdfOverlayPoint> Points { get; }
    public PdfOverlayColor Color { get; }
    public double Thickness { get; }

    internal static string ValidateAnnotationId(string value)
    {
        PdfContractLimits.RequiredString(value, 128, nameof(value));
        if (value.Any(static character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':')))
            throw new ArgumentException("Annotation ids contain only ASCII letters, digits, '-', '_', '.', or ':'.", nameof(value));
        return value;
    }
}

public sealed record PdfTextStampAnnotation
{
    [JsonConstructor]
    public PdfTextStampAnnotation(
        string annotationId,
        PdfOverlayRectangle rectangle,
        string text,
        double fontSize,
        PdfOverlayColor color,
        bool isBold,
        bool isItalic)
    {
        AnnotationId = PdfInkAnnotation.ValidateAnnotationId(annotationId);
        Rectangle = rectangle ?? throw new ArgumentNullException(nameof(rectangle));
        Text = PdfContractLimits.RequiredString(text, 16_384, nameof(text));
        if (!double.IsFinite(fontSize) || fontSize is < 4 or > 512)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        Color = color ?? throw new ArgumentNullException(nameof(color));
        FontSize = fontSize;
        IsBold = isBold;
        IsItalic = isItalic;
    }

    public string AnnotationId { get; }
    public PdfOverlayRectangle Rectangle { get; }
    public string Text { get; }
    public double FontSize { get; }
    public PdfOverlayColor Color { get; }
    public bool IsBold { get; }
    public bool IsItalic { get; }
}

public sealed record PdfSignatureStampAnnotation
{
    // 1 MiB decoded input plus Base64 padding. Signature bytes never enter telemetry.
    public const int MaximumEncodedImageLength = 1_398_104;
    public const int MaximumDecodedImageLength = 1_048_576;

    [JsonConstructor]
    public PdfSignatureStampAnnotation(
        string annotationId,
        PdfOverlayRectangle rectangle,
        string imageBase64)
    {
        AnnotationId = PdfInkAnnotation.ValidateAnnotationId(annotationId);
        Rectangle = rectangle ?? throw new ArgumentNullException(nameof(rectangle));
        PdfContractLimits.RequiredString(imageBase64, MaximumEncodedImageLength, nameof(imageBase64));
        if ((imageBase64.Length & 3) != 0)
            throw new ArgumentException("Signature image data must be canonical Base64.", nameof(imageBase64));
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(imageBase64);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Signature image data must be canonical Base64.", nameof(imageBase64), exception);
        }
        try
        {
            if (decoded.Length is < 1 or > MaximumDecodedImageLength
                || !string.Equals(Convert.ToBase64String(decoded), imageBase64, StringComparison.Ordinal))
            {
                throw new ArgumentException("Signature image data must be canonical Base64 within the decoded limit.", nameof(imageBase64));
            }
        }
        finally
        {
            Array.Clear(decoded);
        }
        ImageBase64 = imageBase64;
    }

    public string AnnotationId { get; }
    public PdfOverlayRectangle Rectangle { get; }
    public string ImageBase64 { get; }
}

public sealed record PdfPageOverlayBatch
{
    public const int MaximumAnnotations = 4_096;

    public PdfPageOverlayBatch(
        int pageIndex,
        PageId pageId,
        PageContentRevision expectedPageContentRevision,
        IEnumerable<PdfInkAnnotation>? ink,
        IEnumerable<PdfTextStampAnnotation>? text,
        IEnumerable<PdfSignatureStampAnnotation>? signatures)
        : this(
            pageIndex,
            pageId,
            expectedPageContentRevision,
            ink is null ? [] : [.. ink],
            text is null ? [] : [.. text],
            signatures is null ? [] : [.. signatures])
    {
    }

    [JsonConstructor]
    public PdfPageOverlayBatch(
        int pageIndex,
        PageId pageId,
        PageContentRevision expectedPageContentRevision,
        ImmutableArray<PdfInkAnnotation> ink,
        ImmutableArray<PdfTextStampAnnotation> text,
        ImmutableArray<PdfSignatureStampAnnotation> signatures)
    {
        PdfContractLimits.PageIndex(pageIndex);
        if (pageId.Value == Guid.Empty)
            throw new ArgumentException("The page id must not be empty.", nameof(pageId));
        if (expectedPageContentRevision.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedPageContentRevision));
        Ink = ink.IsDefault ? [] : ink;
        Text = text.IsDefault ? [] : text;
        Signatures = signatures.IsDefault ? [] : signatures;
        var count = checked(Ink.Length + Text.Length + Signatures.Length);
        if (count is < 1 or > MaximumAnnotations)
            throw new ArgumentOutOfRangeException(nameof(ink));
        if (Ink.Any(static value => value is null)
            || Text.Any(static value => value is null)
            || Signatures.Any(static value => value is null))
            throw new ArgumentException("Overlay collections must not contain null values.");
        var identifiers = Ink.Select(static value => value.AnnotationId)
            .Concat(Text.Select(static value => value.AnnotationId))
            .Concat(Signatures.Select(static value => value.AnnotationId));
        if (identifiers.Distinct(StringComparer.Ordinal).Count() != count)
            throw new ArgumentException("Annotation ids must be unique within a page batch.");
        PageIndex = pageIndex;
        PageId = pageId;
        ExpectedPageContentRevision = expectedPageContentRevision;
    }

    public int PageIndex { get; }
    public PageId PageId { get; }
    public PageContentRevision ExpectedPageContentRevision { get; }
    public ImmutableArray<PdfInkAnnotation> Ink { get; }
    public ImmutableArray<PdfTextStampAnnotation> Text { get; }
    public ImmutableArray<PdfSignatureStampAnnotation> Signatures { get; }
}

/// <summary>
/// One bounded, optimistic-concurrency annotation transaction. The transaction
/// id is used to finalize or roll back the worker's staged in-memory mutation.
/// </summary>
public sealed record PdfAnnotationSaveRequest
{
    public PdfAnnotationSaveRequest(
        Guid transactionId,
        DocumentId documentId,
        ContentRevision expectedContentRevision,
        StructureRevision expectedStructureRevision,
        IEnumerable<PdfPageOverlayBatch> pages)
        : this(transactionId, documentId, expectedContentRevision, expectedStructureRevision, [.. pages])
    {
    }

    [JsonConstructor]
    public PdfAnnotationSaveRequest(
        Guid transactionId,
        DocumentId documentId,
        ContentRevision expectedContentRevision,
        StructureRevision expectedStructureRevision,
        ImmutableArray<PdfPageOverlayBatch> pages)
    {
        if (transactionId == Guid.Empty)
            throw new ArgumentException("The annotation transaction id must not be empty.", nameof(transactionId));
        if (documentId.Value == Guid.Empty)
            throw new ArgumentException("The document id must not be empty.", nameof(documentId));
        if (expectedContentRevision.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedContentRevision));
        if (expectedStructureRevision.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedStructureRevision));
        // An empty page set is the bounded representation of "flatten the
        // existing document only". Annotation staging rejects it because a
        // staged mutation must contain at least one overlay.
        if (pages.IsDefault || pages.Length > PdfContractLimits.MaxCollectionCount)
            throw new ArgumentOutOfRangeException(nameof(pages));
        if (pages.Any(static page => page is null))
            throw new ArgumentException("Page overlay batches must not contain null values.", nameof(pages));
        if (pages.Select(static page => page.PageIndex).Distinct().Count() != pages.Length)
            throw new ArgumentException("A page may occur only once in an annotation transaction.", nameof(pages));
        var total = pages.Sum(static page => checked(page.Ink.Length + page.Text.Length + page.Signatures.Length));
        if (total > PdfPageOverlayBatch.MaximumAnnotations)
            throw new ArgumentOutOfRangeException(nameof(pages), "The transaction contains too many annotations.");
        var identifiers = pages
            .SelectMany(static page => page.Ink.Select(static annotation => annotation.AnnotationId)
                .Concat(page.Text.Select(static annotation => annotation.AnnotationId))
                .Concat(page.Signatures.Select(static annotation => annotation.AnnotationId)));
        if (identifiers.Distinct(StringComparer.Ordinal).Count() != total)
            throw new ArgumentException("Annotation ids must be unique within a transaction.", nameof(pages));
        TransactionId = transactionId;
        DocumentId = documentId;
        ExpectedContentRevision = expectedContentRevision;
        ExpectedStructureRevision = expectedStructureRevision;
        Pages = pages;
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public Guid TransactionId { get; }
    public DocumentId DocumentId { get; }
    public ContentRevision ExpectedContentRevision { get; }
    public StructureRevision ExpectedStructureRevision { get; }
    public ImmutableArray<PdfPageOverlayBatch> Pages { get; }
}

/// <summary>A stable, immutable document identity snapshot suitable for UI state publication.</summary>
public sealed record DocumentSnapshot
{
    public DocumentSnapshot(
        DocumentId id,
        ContentRevision contentRevision,
        ContentRevision savedRevision,
        StructureRevision structureRevision,
        string displayName,
        int pageCount,
        int currentPageIndex,
        RecoveryState recoveryState,
        ExternalFileState externalFileState)
    {
        if (id.Value == Guid.Empty) throw new ArgumentException("The document id must not be empty.", nameof(id));
        PdfContractLimits.RequiredString(displayName, PdfContractLimits.MaxStringLength, nameof(displayName));
        if (pageCount is < 0 or > PdfContractLimits.MaxPageCount) throw new ArgumentOutOfRangeException(nameof(pageCount));
        if (pageCount == 0 && currentPageIndex != 0) throw new ArgumentOutOfRangeException(nameof(currentPageIndex));
        if (pageCount > 0 && (currentPageIndex < 0 || currentPageIndex >= pageCount)) throw new ArgumentOutOfRangeException(nameof(currentPageIndex));
        Id = id;
        ContentRevision = contentRevision;
        SavedRevision = savedRevision;
        StructureRevision = structureRevision;
        DisplayName = displayName;
        PageCount = pageCount;
        CurrentPageIndex = currentPageIndex;
        RecoveryState = recoveryState;
        ExternalFileState = externalFileState;
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public DocumentId Id { get; }
    public ContentRevision ContentRevision { get; }
    public ContentRevision SavedRevision { get; }
    public StructureRevision StructureRevision { get; }
    public string DisplayName { get; }
    public int PageCount { get; }
    public int CurrentPageIndex { get; }
    public bool HasUnsavedChanges => ContentRevision != SavedRevision;
    public RecoveryState RecoveryState { get; }
    public ExternalFileState ExternalFileState { get; }
}

public sealed record PageGeometry
{
    public PageGeometry(PdfRect mediaBox, PdfRect cropBox, PageRotation rotation = PageRotation.None)
    {
        MediaBox = mediaBox.Validate();
        CropBox = cropBox.Validate();
        if (CropBox.Left < MediaBox.Left || CropBox.Top < MediaBox.Top || CropBox.Right > MediaBox.Right || CropBox.Bottom > MediaBox.Bottom)
        {
            throw new ArgumentOutOfRangeException(nameof(cropBox), "The crop box must be contained by the media box.");
        }

        Rotation = rotation;
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public PdfRect MediaBox { get; }
    public PdfRect CropBox { get; }
    public PageRotation Rotation { get; }
    public PdfSize SizeInPoints => new(CropBox.Right - CropBox.Left, CropBox.Bottom - CropBox.Top);
}

public sealed record PageMetadata
{
    public PageMetadata(PageId id, int pageIndex, PdfSize sizeInPoints, string? label = null, PageContentRevision contentRevision = default, PageAppearanceRevision appearanceRevision = default)
        : this(id, pageIndex, CreateGeometry(sizeInPoints), label, contentRevision, appearanceRevision)
    {
    }

    [JsonConstructor]
    public PageMetadata(PageId id, int pageIndex, PageGeometry geometry, string? label = null, PageContentRevision contentRevision = default, PageAppearanceRevision appearanceRevision = default)
    {
        if (id.Value == Guid.Empty) throw new ArgumentException("The page id must not be empty.", nameof(id));
        PdfContractLimits.PageIndex(pageIndex);
        Geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        PdfContractLimits.OptionalString(label, PdfContractLimits.MaxStringLength, nameof(label));
        Id = id;
        PageIndex = pageIndex;
        Label = label;
        ContentRevision = contentRevision;
        AppearanceRevision = appearanceRevision;
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public PageId Id { get; }
    public int PageIndex { get; }
    public PageGeometry Geometry { get; }
    public PdfSize SizeInPoints => Geometry.SizeInPoints;
    public string? Label { get; }
    public PageContentRevision ContentRevision { get; }
    public PageAppearanceRevision AppearanceRevision { get; }

    private static PageGeometry CreateGeometry(PdfSize size)
    {
        PdfContractLimits.FinitePositive(size.Width, nameof(size));
        PdfContractLimits.FinitePositive(size.Height, nameof(size));
        var bounds = new PdfRect(0, 0, size.Width, size.Height);
        return new PageGeometry(bounds, bounds);
    }
}

public sealed record PdfMetadata
{
    public PdfMetadata(
        int pageCount,
        string? pdfVersion = null,
        string? title = null,
        string? author = null,
        string? subject = null,
        string? keywords = null,
        string? creator = null,
        string? producer = null,
        bool isEncrypted = false,
        bool hasOutline = false,
        bool hasForms = false)
    {
        if (pageCount is < 0 or > PdfContractLimits.MaxPageCount) throw new ArgumentOutOfRangeException(nameof(pageCount));
        PageCount = pageCount;
        PdfVersion = PdfContractLimits.OptionalString(pdfVersion, PdfContractLimits.MaxMetadataStringLength, nameof(pdfVersion));
        Title = PdfContractLimits.OptionalString(title, PdfContractLimits.MaxMetadataStringLength, nameof(title));
        Author = PdfContractLimits.OptionalString(author, PdfContractLimits.MaxMetadataStringLength, nameof(author));
        Subject = PdfContractLimits.OptionalString(subject, PdfContractLimits.MaxMetadataStringLength, nameof(subject));
        Keywords = PdfContractLimits.OptionalString(keywords, PdfContractLimits.MaxMetadataStringLength, nameof(keywords));
        Creator = PdfContractLimits.OptionalString(creator, PdfContractLimits.MaxMetadataStringLength, nameof(creator));
        Producer = PdfContractLimits.OptionalString(producer, PdfContractLimits.MaxMetadataStringLength, nameof(producer));
        IsEncrypted = isEncrypted;
        HasOutline = hasOutline;
        HasForms = hasForms;
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public int PageCount { get; }
    public string? PdfVersion { get; }
    public string? Title { get; }
    public string? Author { get; }
    public string? Subject { get; }
    public string? Keywords { get; }
    public string? Creator { get; }
    public string? Producer { get; }
    public bool IsEncrypted { get; }
    public bool HasOutline { get; }
    public bool HasForms { get; }
}

public sealed record OutlineItem
{
    public OutlineItem(string title, PageId? destinationPageId = null, int? destinationPageIndex = null, IEnumerable<OutlineItem>? children = null, int depth = 0)
        : this(
            title,
            destinationPageId,
            destinationPageIndex,
            children is null ? [] : [.. children],
            depth)
    {
    }

    [JsonConstructor]
    public OutlineItem(string title, PageId? destinationPageId, int? destinationPageIndex, ImmutableArray<OutlineItem> children, int depth = 0)
    {
        PdfContractLimits.RequiredString(title, PdfContractLimits.MaxStringLength, nameof(title));
        if (depth is < 0 or > PdfContractLimits.MaxOutlineDepth) throw new ArgumentOutOfRangeException(nameof(depth));
        if (destinationPageIndex is not null) PdfContractLimits.PageIndex(destinationPageIndex.Value, nameof(destinationPageIndex));
        Title = title;
        DestinationPageId = destinationPageId;
        DestinationPageIndex = destinationPageIndex;
        Depth = depth;
        Children = children.IsDefault
            ? []
            : PdfContractLimits.ReadOnly(children, PdfContractLimits.MaxCollectionCount, nameof(children));
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public string Title { get; }
    public PageId? DestinationPageId { get; }
    public int? DestinationPageIndex { get; }
    public int Depth { get; }
    public ImmutableArray<OutlineItem> Children { get; }
}

public sealed record OutlineResult
{
    public OutlineResult(IEnumerable<OutlineItem> items)
        : this([.. items])
    {
    }

    [JsonConstructor]
    public OutlineResult(ImmutableArray<OutlineItem> items)
    {
        Items = items.IsDefault
            ? []
            : PdfContractLimits.ReadOnly(items, PdfContractLimits.MaxCollectionCount, nameof(items));
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public ImmutableArray<OutlineItem> Items { get; }
}

/// <summary>Compatibility-shaped outline node for consumers that do not need depth metadata.</summary>
public sealed record PdfOutlineItem
{
    public PdfOutlineItem(string title, int? pageIndex = null, IEnumerable<PdfOutlineItem>? children = null)
    {
        PdfContractLimits.RequiredString(title, PdfContractLimits.MaxStringLength, nameof(title));
        if (pageIndex is not null) PdfContractLimits.PageIndex(pageIndex.Value, nameof(pageIndex));
        Title = title;
        PageIndex = pageIndex;
        Children = PdfContractLimits.ReadOnly(children ?? [], PdfContractLimits.MaxCollectionCount, nameof(children));
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public string Title { get; }
    public int? PageIndex { get; }
    public ImmutableArray<PdfOutlineItem> Children { get; }
}
