using ElliePdf.Domain.Documents;

namespace ElliePdf.Rendering;

public readonly record struct PageAnchor(PageId PageId, int PageIndex, double InPageOffset)
{
    public PageAnchor Validate()
    {
        if (PageId.Value == Guid.Empty) throw new ArgumentException("The page id must not be empty.", nameof(PageId));
        if (PageIndex < 0) throw new ArgumentOutOfRangeException(nameof(PageIndex));
        if (!double.IsFinite(InPageOffset) || InPageOffset < 0) throw new ArgumentOutOfRangeException(nameof(InPageOffset));
        return this;
    }
}

public readonly record struct GeometryUpdateResult(PageAnchor Anchor, double ViewportOffset, double ExtentDelta);

/// <summary>Coordinates extent changes with a scroll offset so the visible page anchor does not jump.</summary>
public sealed class AnchorPreservingPageLayout
{
    private readonly PageLayoutItem[] _pages;
    private readonly PageExtentIndex _extents;

    public AnchorPreservingPageLayout(IEnumerable<PageLayoutItem> pages, double pageGap = 0)
    {
        ArgumentNullException.ThrowIfNull(pages);
        _pages = pages.Select(p => p.Validate()).ToArray();
        if (_pages.Length == 0) throw new ArgumentException("At least one page is required.", nameof(pages));
        if (_pages.Select(p => p.Id).Distinct().Count() != _pages.Length)
            throw new ArgumentException("Page identities must be unique.", nameof(pages));
        for (var i = 0; i < _pages.Length; i++)
            if (_pages[i].PageIndex != i) throw new ArgumentException("Page metadata must be ordered and indexed contiguously.", nameof(pages));
        _extents = new PageExtentIndex(_pages.Select(p => p.Geometry), pageGap);
    }

    public int PageCount => _pages.Length;
    public PageExtentIndex Extents => _extents;
    public IReadOnlyList<PageLayoutItem> Metadata => _pages;

    public PageAnchor CaptureAnchor(double viewportOffset)
    {
        if (!double.IsFinite(viewportOffset) || viewportOffset < 0) throw new ArgumentOutOfRangeException(nameof(viewportOffset));
        var offset = Math.Min(viewportOffset, _extents.TotalExtent);
        var index = _extents.FindPageAtOffset(offset);
        return new PageAnchor(_pages[index].Id, index, offset - _extents.GetOffset(index));
    }

    public GeometryUpdateResult UpdateGeometryPreservingAnchor(int pageIndex, PageLayoutGeometry geometry, double viewportOffset)
    {
        if ((uint)pageIndex >= (uint)_pages.Length) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        geometry.Validate();
        var anchor = CaptureAnchor(viewportOffset);
        var oldExtent = _extents.GetExtent(pageIndex);
        _pages[pageIndex] = _pages[pageIndex] with { Geometry = geometry };
        var newExtent = geometry.Extent + _extents.PageGap;
        if (pageIndex == _pages.Length - 1) newExtent = geometry.Extent;
        _extents.UpdateExtent(pageIndex, newExtent);

        // Page order is stable for a geometry-only update, so this remains logarithmic
        // and does not scan all metadata to recover the anchor.
        if (_pages[anchor.PageIndex].Id != anchor.PageId)
            throw new InvalidOperationException("The anchored page identity changed during a geometry update.");
        var newOffset = _extents.GetOffset(anchor.PageIndex) + anchor.InPageOffset;
        return new GeometryUpdateResult(anchor, newOffset, newExtent - oldExtent);
    }

    public double UpdateExtentPreservingAnchor(int pageIndex, double extent, double viewportOffset)
    {
        if ((uint)pageIndex >= (uint)_pages.Length) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        var anchor = CaptureAnchor(viewportOffset);
        _extents.UpdateExtent(pageIndex, extent);
        return _extents.GetOffset(anchor.PageIndex) + anchor.InPageOffset;
    }
}
