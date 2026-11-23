using System.Collections.Immutable;
using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;

namespace ElliePdf.Semantics;

/// <summary>
/// UI-neutral semantic state for one open PDF.  Consumers may freely replace visual peers:
/// every projection returned by this type is immutable and contains no native handles.
/// </summary>
public sealed class SemanticReaderController : IAsyncDisposable
{
    private readonly IPdfEngineSession _session;
    private readonly bool _ownsSession;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Dictionary<int, SemanticPageSnapshot> _pages = [];
    private readonly List<SearchResult> _searchResults = [];
    private CancellationTokenSource? _searchCancellation;
    private PdfPermissions? _permissions;
    private ContentRevision _contentRevision;
    private int _searchIndex = -1;
    private SelectionState? _selection;
    private SearchGeneration _searchGeneration = SearchGeneration.Initial;
    private bool _disposed;

    public SemanticReaderController(IPdfEngineSession session, bool ownsSession = true)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _ownsSession = ownsSession;
        _contentRevision = session is IPdfWritableEngineSession writable
            ? writable.Snapshot.ContentRevision
            : ContentRevision.Initial;
    }

    public DocumentId DocumentId => _session.DocumentId;

    public SearchNavigationState SearchState
    {
        get
        {
            lock (_gate)
                return new(_searchGeneration, [.. _searchResults], _searchIndex);
        }
    }

    public SelectionState? Selection
    {
        get { lock (_gate) return _selection; }
    }

    public bool TryGetPage(int pageIndex, out SemanticPageSnapshot? page)
    {
        ThrowIfDisposed();
        lock (_gate) return _pages.TryGetValue(pageIndex, out page);
    }

    /// <summary>
    /// Releases an immutable page projection when the application-wide metadata
    /// cache evicts it. Native PDF state remains owned by the engine session.
    /// </summary>
    public bool EvictPage(int pageIndex)
    {
        ThrowIfDisposed();
        lock (_gate) return _pages.Remove(pageIndex);
    }

    public void CancelSearch()
    {
        ThrowIfDisposed();
        lock (_gate) _searchCancellation?.Cancel();
    }

    public async ValueTask<SemanticDocumentSnapshot> GetDocumentSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        PdfMetadata metadata = await _session.GetMetadataAsync(Link(cancellationToken)).ConfigureAwait(false);
        PdfPermissions permissions = await GetPermissionsAsync(cancellationToken).ConfigureAwait(false);
        ImmutableArray<SemanticPageSnapshot> pages;
        lock (_gate) pages = [.. _pages.OrderBy(static pair => pair.Key).Select(static pair => pair.Value)];
        return new SemanticDocumentSnapshot(DocumentId, metadata, permissions, _contentRevision, pages, SearchState);
    }

    /// <summary>Loads just one page's metadata and text; callers decide which pages become visible.</summary>
    public async ValueTask<SemanticPageSnapshot> GetPageAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        PageMetadata metadata = await _session.GetPageMetadataAsync(pageIndex, Link(cancellationToken)).ConfigureAwait(false);
        var request = new PageTextRequest(DocumentId, metadata.Id, metadata.PageIndex, metadata.ContentRevision);
        PageTextResult text = await _session.GetPageTextAsync(request, Link(cancellationToken)).ConfigureAwait(false);
        PageLinks links = await _session.GetPageLinksAsync(pageIndex, Link(cancellationToken)).ConfigureAwait(false);
        FormWidgetsResult forms = await _session.GetFormWidgetsAsync(pageIndex, Link(cancellationToken)).ConfigureAwait(false);
        var snapshot = new SemanticPageSnapshot(metadata, text, [.. links.Links.Select(ToLink)], [.. forms.Widgets.Select(ToForm)]);
        lock (_gate) _pages[pageIndex] = snapshot;
        return snapshot;
    }

    /// <summary>Starts a new generation. Results are yielded page-by-page as soon as each worker request completes.</summary>
    public async IAsyncEnumerable<SearchResult> SearchAsync(
        string query,
        bool matchCase = false,
        bool wholeWord = false,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        PdfMetadata metadata = await _session.GetMetadataAsync(Link(cancellationToken)).ConfigureAwait(false);
        CancellationTokenSource generationCancellation;
        SearchGeneration generation;
        lock (_gate)
        {
            _searchCancellation?.Cancel();
            _searchCancellation?.Dispose();
            _searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            generationCancellation = _searchCancellation;
            _searchGeneration = _searchGeneration.Next();
            generation = _searchGeneration;
            _searchResults.Clear();
            _searchIndex = -1;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(generationCancellation.Token, cancellationToken);
        for (int pageIndex = 0; pageIndex < metadata.PageCount; pageIndex++)
        {
            linked.Token.ThrowIfCancellationRequested();
            PageMetadata page = await _session.GetPageMetadataAsync(pageIndex, linked.Token).ConfigureAwait(false);
            var text = new PageTextRequest(DocumentId, page.Id, pageIndex, page.ContentRevision);
            IReadOnlyList<SearchResult> results = await _session.SearchPageAsync(
                new PageSearchRequest(text, query, generation, matchCase, wholeWord), linked.Token).ConfigureAwait(false);
            foreach (SearchResult result in results)
            {
                lock (_gate)
                {
                    if (generation != _searchGeneration) yield break;
                    _searchResults.Add(result);
                    if (_searchIndex < 0) _searchIndex = 0;
                }
                yield return result;
            }
        }
    }

    public SearchResult? MoveSearchResult(bool reverse = false)
    {
        lock (_gate)
        {
            if (_searchResults.Count == 0) return null;
            _searchIndex = _searchIndex < 0 ? 0 : Modulo(_searchIndex + (reverse ? -1 : 1), _searchResults.Count);
            return _searchResults[_searchIndex];
        }
    }

    public SelectionState Select(SemanticPageSnapshot page, int anchor, int focus, bool extend = false)
    {
        ArgumentNullException.ThrowIfNull(page);
        ValidateIndex(page.Text.Text, anchor);
        ValidateIndex(page.Text.Text, focus);
        SelectionState? existing;
        lock (_gate) existing = _selection?.PageIndex == page.Metadata.PageIndex ? _selection : null;
        int start = extend && existing is { } previous ? Math.Min(previous.Start, focus) : Math.Min(anchor, focus);
        int end = extend && existing is { } prior ? Math.Max(prior.End, focus) : Math.Max(anchor, focus);
        var selection = SemanticTextSelection.Create(
            [page],
            new TextPosition(page.Metadata.PageIndex, start),
            new TextPosition(page.Metadata.PageIndex, end));
        lock (_gate)
        {
            _selection = selection;
            if (_pages.TryGetValue(page.Metadata.PageIndex, out SemanticPageSnapshot? cached))
                _pages[page.Metadata.PageIndex] = cached with { Selection = selection };
        }
        return selection;
    }

    /// <summary>Creates one ordered selection spanning any already-loaded contiguous pages.</summary>
    public SelectionState Select(
        IEnumerable<SemanticPageSnapshot> pages,
        TextPosition anchor,
        TextPosition focus)
    {
        var selection = SemanticTextSelection.Create(pages, anchor, focus);
        lock (_gate)
        {
            _selection = selection;
            foreach (var segment in selection.Segments)
            {
                if (_pages.TryGetValue(segment.PageIndex, out var cached))
                    _pages[segment.PageIndex] = cached with { Selection = selection };
            }
        }
        return selection;
    }

    public async ValueTask<CopyDecision> CopyAsync(SelectionState selection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        PdfPermissions permissions = await GetPermissionsAsync(cancellationToken).ConfigureAwait(false);
        return permissions.CanCopy
            ? CopyDecision.Allowed(selection.Text)
            : CopyDecision.Blocked("Copying is disabled by this document's permissions.");
    }

    public LinkActivationDecision DecideLink(PdfLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (!link.IsSafeToActivate) return LinkActivationDecision.Blocked(link.BlockedReason ?? "This link was blocked by the PDF safety policy.");
        if (link.Kind == PdfLinkKind.Page) return LinkActivationDecision.Navigate(link.TargetPageIndex, link.TargetPageId);
        if (link.Kind == PdfLinkKind.Named) return LinkActivationDecision.Blocked("Named destinations are not available in this document.");
        ExternalLinkDecision decision = PdfExternalLinkPolicy.Evaluate(link.Uri, out Uri? uri);
        return decision == ExternalLinkDecision.Allowed
            ? LinkActivationDecision.External(uri!)
            : LinkActivationDecision.Blocked(decision == ExternalLinkDecision.BlockedScheme ? "The link's scheme is not allowed." : "The link is malformed.");
    }

    public async ValueTask<FormUpdateDecision> UpdateFormAsync(SemanticFormSnapshot form, FormValue value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(value);
        PdfPermissions permissions = await GetPermissionsAsync(cancellationToken).ConfigureAwait(false);
        if (!permissions.CanFillForms) return FormUpdateDecision.Blocked("Filling forms is disabled by this document's permissions.");
        if (!form.IsSupported) return FormUpdateDecision.Blocked(form.UnsupportedReason ?? "This form field is not supported.");
        if (form.IsReadOnly) return FormUpdateDecision.Blocked("This form field is read-only.");
        await _session.ApplyFormValueAsync(new FormValueChange(DocumentId, form.Id, value, _contentRevision), Link(cancellationToken)).ConfigureAwait(false);
        lock (_gate)
        {
            _contentRevision = _contentRevision.Next();
            if (_pages.TryGetValue(form.PageIndex, out SemanticPageSnapshot? page))
            {
                var updatedForms = page.Forms.Select(current => current.Id == form.Id ? current with { Value = value, ContentRevision = _contentRevision } : current).ToImmutableArray();
                _pages[form.PageIndex] = page with { Forms = updatedForms };
            }
        }
        return FormUpdateDecision.Succeeded(_contentRevision);
    }

    public async ValueTask<PushButtonInvocationDecision> InvokePushButtonAsync(
        SemanticFormSnapshot form,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.Type != FormWidgetType.PushButton)
        {
            return PushButtonInvocationDecision.Blocked("This form control is not a push button.");
        }
        if (!form.IsSupported)
        {
            return PushButtonInvocationDecision.Blocked(form.UnsupportedReason ?? "This push button is not supported.");
        }
        if (form.IsReadOnly)
        {
            return PushButtonInvocationDecision.Blocked("This push button is read-only.");
        }
        if (_session is not IPdfPushButtonSession pushButtons)
        {
            return PushButtonInvocationDecision.Blocked("This PDF engine does not support push buttons.");
        }

        await pushButtons.InvokePushButtonAsync(
            new PushButtonInvocation(DocumentId, form.Id, _contentRevision),
            Link(cancellationToken)).ConfigureAwait(false);
        return PushButtonInvocationDecision.Succeeded();
    }

    public async ValueTask<ImmutableArray<SemanticOutlineItem>> GetOutlineAsync(CancellationToken cancellationToken = default)
    {
        OutlineResult outline = await _session.GetOutlineAsync(Link(cancellationToken)).ConfigureAwait(false);
        return [.. outline.Items.Select(ToOutline)];
    }

    public async ValueTask<AutomationPageSnapshot> GetAutomationPageAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        SemanticPageSnapshot page = await GetPageAsync(pageIndex, cancellationToken).ConfigureAwait(false);
        return new AutomationPageSnapshot(page.Metadata.PageIndex, page.Metadata.Label, page.Metadata.Geometry, page.Text.Text,
            [.. page.Text.Spans.Select(span => new AutomationTextSpan(span.StartIndex, span.Text, span.Bounds))],
            [.. page.Links.Select(link => new AutomationLinkSnapshot(link.Kind, link.Bounds, link.Uri, link.TargetPageIndex, link.IsSafeToActivate, link.BlockedReason))],
            [.. page.Forms.Select(form => new AutomationFormSnapshot(form.Id, form.FieldName, form.Type, form.Bounds, form.Value, form.IsReadOnly, form.IsSupported, form.UnsupportedReason))]);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        lock (_gate) { _searchCancellation?.Cancel(); _searchCancellation?.Dispose(); _searchCancellation = null; }
        _lifetime.Dispose();
        if (_ownsSession)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<PdfPermissions> GetPermissionsAsync(CancellationToken cancellationToken)
    {
        lock (_gate) if (_permissions is not null) return _permissions;
        PdfPermissions permissions = await _session.GetPermissionsAsync(Link(cancellationToken)).ConfigureAwait(false);
        lock (_gate) return _permissions ??= permissions;
    }

    private CancellationToken Link(CancellationToken token)
    {
        ThrowIfDisposed();
        _lifetime.Token.ThrowIfCancellationRequested();
        return token;
    }

    private static SemanticLinkSnapshot ToLink(PdfLink link) => new(link.Kind, link.Bounds, link.Uri, link.TargetPageId, link.TargetPageIndex, link.Name, link.IsSafeToActivate, link.BlockedReason);
    private static SemanticFormSnapshot ToForm(FormWidget form) => new(form.Id, form.PageIndex, form.Type, form.FieldName, form.Bounds, form.Value, form.IsReadOnly, form.IsRequired, form.Options, form.IsSupported, form.UnsupportedReason, ContentRevision.Initial);
    private static SemanticOutlineItem ToOutline(OutlineItem item) => new(item.Title, item.DestinationPageId, item.DestinationPageIndex, [.. item.Children.Select(ToOutline)]);
    private static int Modulo(int value, int divisor) => ((value % divisor) + divisor) % divisor;
    private static void ValidateIndex(string value, int index) { if (index < 0 || index > value.Length) throw new ArgumentOutOfRangeException(nameof(index)); }
    private void ThrowIfDisposed() { ObjectDisposedException.ThrowIf(_disposed, this); }
}

public sealed record SemanticPageSnapshot(PageMetadata Metadata, PageTextResult Text, ImmutableArray<SemanticLinkSnapshot> Links, ImmutableArray<SemanticFormSnapshot> Forms)
{
    public SelectionState? Selection { get; init; }
}
public sealed record SemanticDocumentSnapshot(DocumentId DocumentId, PdfMetadata Metadata, PdfPermissions Permissions, ContentRevision ContentRevision, ImmutableArray<SemanticPageSnapshot> LoadedPages, SearchNavigationState Search);
public sealed record SemanticLinkSnapshot(PdfLinkKind Kind, PdfRect Bounds, string? Uri, PageId? TargetPageId, int? TargetPageIndex, string? Name, bool IsSafeToActivate, string? BlockedReason);
public sealed record SemanticFormSnapshot(FormFieldId Id, int PageIndex, FormWidgetType Type, string FieldName, PdfRect Bounds, FormValue Value, bool IsReadOnly, bool IsRequired, ImmutableArray<string> Options, bool IsSupported, string? UnsupportedReason, ContentRevision ContentRevision);
public sealed record SemanticOutlineItem(string Title, PageId? DestinationPageId, int? DestinationPageIndex, ImmutableArray<SemanticOutlineItem> Children);
public sealed record SearchNavigationState(SearchGeneration Generation, ImmutableArray<SearchResult> Results, int CurrentIndex)
{
    public SearchResult? Current => CurrentIndex >= 0 && CurrentIndex < Results.Length ? Results[CurrentIndex] : null;
}
public sealed record SelectionState(int PageIndex, int Start, int End, string Text)
{
    public TextPosition Anchor { get; init; } = new(PageIndex, Start);
    public TextPosition Focus { get; init; } = new(PageIndex, End);
    public ImmutableArray<SelectionSegment> Segments { get; init; } = [];
    public bool IsCrossPage => Anchor.PageIndex != Focus.PageIndex;
}
public sealed record CopyDecision(bool IsAllowed, string? Text, string? Reason)
{
    public static CopyDecision Allowed(string text) => new(true, text, null);
    public static CopyDecision Blocked(string reason) => new(false, null, reason);
}
public sealed record LinkActivationDecision(LinkActivationKind Kind, int? PageIndex, PageId? PageId, Uri? Uri, string? Reason)
{
    public static LinkActivationDecision Navigate(int? pageIndex, PageId? pageId) => new(LinkActivationKind.InternalNavigation, pageIndex, pageId, null, null);
    public static LinkActivationDecision External(Uri uri) => new(LinkActivationKind.ExternalNavigation, null, null, uri, null);
    public static LinkActivationDecision Blocked(string reason) => new(LinkActivationKind.Blocked, null, null, null, reason);
}
public enum LinkActivationKind { InternalNavigation, ExternalNavigation, Blocked }
public sealed record FormUpdateDecision(bool Applied, ContentRevision? ContentRevision, string? Reason)
{
    public static FormUpdateDecision Succeeded(ContentRevision revision) => new(true, revision, null);
    public static FormUpdateDecision Blocked(string reason) => new(false, null, reason);
}

public sealed record PushButtonInvocationDecision(bool Invoked, string? Reason)
{
    public static PushButtonInvocationDecision Succeeded() => new(true, null);
    public static PushButtonInvocationDecision Blocked(string reason) => new(false, reason);
}
public sealed record AutomationPageSnapshot(int PageIndex, string? Label, PageGeometry Geometry, string Text, ImmutableArray<AutomationTextSpan> Spans, ImmutableArray<AutomationLinkSnapshot> Links, ImmutableArray<AutomationFormSnapshot> Forms);
public sealed record AutomationTextSpan(int StartIndex, string Text, PdfRect Bounds);
public sealed record AutomationLinkSnapshot(PdfLinkKind Kind, PdfRect Bounds, string? Uri, int? TargetPageIndex, bool IsSafeToActivate, string? BlockedReason);
public sealed record AutomationFormSnapshot(FormFieldId Id, string FieldName, FormWidgetType Type, PdfRect Bounds, FormValue Value, bool IsReadOnly, bool IsSupported, string? UnsupportedReason);
