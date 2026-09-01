using System.Collections.Immutable;
using System.Text.Json.Serialization;
using ElliePdf.Domain.Documents;

namespace ElliePdf.Pdf.Contracts;

public sealed record TextSpan
{
    public TextSpan(int startIndex, string text, PdfRect bounds, double? fontSize = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        PdfContractLimits.RequiredString(text, PdfContractLimits.MaxTextLength, nameof(text));
        bounds.Validate();
        if (fontSize is not null) PdfContractLimits.FinitePositive(fontSize.Value, nameof(fontSize));
        StartIndex = startIndex;
        Text = text;
        Bounds = bounds;
        FontSize = fontSize;
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public int StartIndex { get; }
    public string Text { get; }
    public PdfRect Bounds { get; }
    public double? FontSize { get; }
}

public sealed record PageTextRequest
{
    public PageTextRequest(DocumentId documentId, PageId pageId, int pageIndex, PageContentRevision contentRevision)
    {
        if (documentId.Value == Guid.Empty) throw new ArgumentException("The document id must not be empty.", nameof(documentId));
        if (pageId.Value == Guid.Empty) throw new ArgumentException("The page id must not be empty.", nameof(pageId));
        PdfContractLimits.PageIndex(pageIndex);
        DocumentId = documentId;
        PageId = pageId;
        PageIndex = pageIndex;
        ContentRevision = contentRevision;
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public DocumentId DocumentId { get; }
    public PageId PageId { get; }
    public int PageIndex { get; }
    public PageContentRevision ContentRevision { get; }
}

public sealed record PageTextResult
{
    public PageTextResult(DocumentId documentId, PageId pageId, int pageIndex, PageContentRevision contentRevision, string text, IEnumerable<TextSpan>? spans = null)
        : this(documentId, pageId, pageIndex, contentRevision, text, spans is null ? [] : [.. spans])
    {
    }

    [JsonConstructor]
    public PageTextResult(DocumentId documentId, PageId pageId, int pageIndex, PageContentRevision contentRevision, string text, ImmutableArray<TextSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > PdfContractLimits.MaxTextLength) throw new ArgumentOutOfRangeException(nameof(text));
        if (documentId.Value == Guid.Empty) throw new ArgumentException("The document id must not be empty.", nameof(documentId));
        if (pageId.Value == Guid.Empty) throw new ArgumentException("The page id must not be empty.", nameof(pageId));
        PdfContractLimits.PageIndex(pageIndex);
        DocumentId = documentId;
        PageId = pageId;
        PageIndex = pageIndex;
        ContentRevision = contentRevision;
        Text = text;
        Spans = spans.IsDefault
            ? []
            : PdfContractLimits.ReadOnly(spans, PdfContractLimits.MaxCollectionCount, nameof(spans));
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public DocumentId DocumentId { get; }
    public PageId PageId { get; }
    public int PageIndex { get; }
    public PageContentRevision ContentRevision { get; }
    public string Text { get; }
    public ImmutableArray<TextSpan> Spans { get; }
}

public sealed record PageSearchRequest
{
    public PageSearchRequest(PageTextRequest page, string query, SearchGeneration generation, bool matchCase = false, bool wholeWord = false)
    {
        Page = page ?? throw new ArgumentNullException(nameof(page));
        PdfContractLimits.RequiredString(query, PdfContractLimits.MaxSearchQueryLength, nameof(query));
        Query = query;
        Generation = generation;
        MatchCase = matchCase;
        WholeWord = wholeWord;
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public PageTextRequest Page { get; }
    public string Query { get; }
    public SearchGeneration Generation { get; }
    public bool MatchCase { get; }
    public bool WholeWord { get; }
}

public sealed record SearchResult
{
    public SearchResult(DocumentId documentId, PageId pageId, int pageIndex, PageContentRevision contentRevision, SearchGeneration generation, int charIndex, int matchLength, string context, IEnumerable<PdfRect>? highlightRects = null)
        : this(documentId, pageId, pageIndex, contentRevision, generation, charIndex, matchLength, context, highlightRects is null ? [] : [.. highlightRects])
    {
    }

    [JsonConstructor]
    public SearchResult(DocumentId documentId, PageId pageId, int pageIndex, PageContentRevision contentRevision, SearchGeneration generation, int charIndex, int matchLength, string context, ImmutableArray<PdfRect> highlightRects)
    {
        if (documentId.Value == Guid.Empty) throw new ArgumentException("The document id must not be empty.", nameof(documentId));
        if (pageId.Value == Guid.Empty) throw new ArgumentException("The page id must not be empty.", nameof(pageId));
        PdfContractLimits.PageIndex(pageIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(charIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(matchLength);
        PdfContractLimits.RequiredString(context, PdfContractLimits.MaxTextLength, nameof(context));
        DocumentId = documentId;
        PageId = pageId;
        PageIndex = pageIndex;
        ContentRevision = contentRevision;
        Generation = generation;
        CharIndex = charIndex;
        MatchLength = matchLength;
        Context = context;
        HighlightRects = highlightRects.IsDefault
            ? []
            : PdfContractLimits.ReadOnly(highlightRects, PdfContractLimits.MaxCollectionCount, nameof(highlightRects));
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public DocumentId DocumentId { get; }
    public PageId PageId { get; }
    public int PageIndex { get; }
    public PageContentRevision ContentRevision { get; }
    public SearchGeneration Generation { get; }
    public int CharIndex { get; }
    public int MatchLength { get; }
    public string Context { get; }
    public ImmutableArray<PdfRect> HighlightRects { get; }
}

public enum PdfLinkKind
{
    Uri,
    Page,
    Named
}

public sealed record PdfLink
{
    public PdfLink(PdfLinkKind kind, PdfRect bounds, string? uri = null, PageId? targetPageId = null, int? targetPageIndex = null, string? name = null, bool isSafeToActivate = true, string? blockedReason = null)
    {
        Bounds = bounds.Validate();
        if (targetPageIndex is not null) PdfContractLimits.PageIndex(targetPageIndex.Value, nameof(targetPageIndex));
        if (kind == PdfLinkKind.Uri && string.IsNullOrEmpty(uri)) throw new ArgumentException("A URI link requires a URI.", nameof(uri));
        if (kind == PdfLinkKind.Page && targetPageId is null && targetPageIndex is null) throw new ArgumentException("A page link requires a target page.", nameof(targetPageId));
        if (kind == PdfLinkKind.Named && string.IsNullOrEmpty(name)) throw new ArgumentException("A named link requires a name.", nameof(name));
        Kind = kind;
        Uri = PdfContractLimits.OptionalString(uri, PdfContractLimits.MaxStringLength, nameof(uri));
        TargetPageId = targetPageId;
        TargetPageIndex = targetPageIndex;
        Name = PdfContractLimits.OptionalString(name, PdfContractLimits.MaxStringLength, nameof(name));
        BlockedReason = PdfContractLimits.OptionalString(blockedReason, PdfContractLimits.MaxStringLength, nameof(blockedReason));
        if (!isSafeToActivate && string.IsNullOrWhiteSpace(BlockedReason))
            throw new ArgumentException("Blocked links require an accessible reason.", nameof(blockedReason));
        IsSafeToActivate = isSafeToActivate;
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public PdfLinkKind Kind { get; }
    public PdfRect Bounds { get; }
    public string? Uri { get; }
    public PageId? TargetPageId { get; }
    public int? TargetPageIndex { get; }
    public string? Name { get; }
    public bool IsSafeToActivate { get; }
    public string? BlockedReason { get; }
}

public sealed record PageLinks
{
    public PageLinks(DocumentId documentId, PageId pageId, int pageIndex, IEnumerable<PdfLink> links)
    {
        if (documentId.Value == Guid.Empty) throw new ArgumentException("The document id must not be empty.", nameof(documentId));
        if (pageId.Value == Guid.Empty) throw new ArgumentException("The page id must not be empty.", nameof(pageId));
        PdfContractLimits.PageIndex(pageIndex);
        DocumentId = documentId;
        PageId = pageId;
        PageIndex = pageIndex;
        Links = PdfContractLimits.ReadOnly(links, PdfContractLimits.MaxCollectionCount, nameof(links));
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public DocumentId DocumentId { get; }
    public PageId PageId { get; }
    public int PageIndex { get; }
    public ImmutableArray<PdfLink> Links { get; }
}

public enum ExternalLinkDecision
{
    Allowed,
    BlockedMalformed,
    BlockedScheme
}

/// <summary>Fail-closed policy for actions which may leave ElliePdf.</summary>
public static class PdfExternalLinkPolicy
{
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttps,
        Uri.UriSchemeHttp,
        Uri.UriSchemeMailto
    };

    public static ExternalLinkDecision Evaluate(string? value, out Uri? safeUri)
    {
        safeUri = null;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > PdfContractLimits.MaxStringLength
            || !Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || string.IsNullOrWhiteSpace(parsed.Scheme))
        {
            return ExternalLinkDecision.BlockedMalformed;
        }

        if (!AllowedSchemes.Contains(parsed.Scheme))
        {
            return ExternalLinkDecision.BlockedScheme;
        }

        safeUri = parsed;
        return ExternalLinkDecision.Allowed;
    }
}
