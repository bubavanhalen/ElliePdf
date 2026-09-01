using System.Collections.Immutable;
using ElliePdf.Domain.Documents;

namespace ElliePdf.Rendering;

public enum ScrollDirection
{
    None,
    Forward,
    Backward
}

public sealed record VirtualizationOptions
{
    public VirtualizationOptions(int overscanBeforePages = 1, int overscanAfterPages = 1, int maxRealizedPages = 12)
    {
        if (overscanBeforePages < 0) throw new ArgumentOutOfRangeException(nameof(overscanBeforePages));
        if (overscanAfterPages < 0) throw new ArgumentOutOfRangeException(nameof(overscanAfterPages));
        if (maxRealizedPages <= 0) throw new ArgumentOutOfRangeException(nameof(maxRealizedPages));
        OverscanBeforePages = overscanBeforePages;
        OverscanAfterPages = overscanAfterPages;
        // Twelve is a policy limit, not a caller-controlled resource budget.
        MaxRealizedPages = Math.Min(12, maxRealizedPages);
    }

    public int OverscanBeforePages { get; init; }
    public int OverscanAfterPages { get; init; }
    public int MaxRealizedPages { get; init; }
}

public sealed record RealizationPlan(
    ImmutableArray<PageLayoutItem> Pages,
    int VisibleFirst,
    int VisibleLast,
    int RealizedFirst,
    int RealizedLast,
    ScrollDirection Direction)
{
    public int RealizedCount => Pages.Length;
    public bool ContainsPage(int pageIndex) => pageIndex >= RealizedFirst && pageIndex <= RealizedLast;
}

/// <summary>Computes a bounded set of page elements for a viewport.</summary>
public sealed class ViewportRealizationPlanner
{
    private readonly PageLayoutItem[] _pages;
    private readonly PageExtentIndex _extents;
    private readonly VirtualizationOptions _options;

    public ViewportRealizationPlanner(IEnumerable<PageLayoutItem> pages, VirtualizationOptions? options = null, double pageGap = 0)
    {
        ArgumentNullException.ThrowIfNull(pages);
        _pages = pages.Select(p => p.Validate()).ToArray();
        if (_pages.Length == 0) throw new ArgumentException("At least one page is required.", nameof(pages));
        if (_pages.Select(p => p.Id).Distinct().Count() != _pages.Length)
            throw new ArgumentException("Page identities must be unique.", nameof(pages));
        for (var i = 0; i < _pages.Length; i++)
        {
            if (_pages[i].PageIndex != i) throw new ArgumentException("Page metadata must be ordered and indexed contiguously.", nameof(pages));
        }
        _options = options ?? new VirtualizationOptions();
        _extents = new PageExtentIndex(_pages.Select(p => p.Geometry), pageGap);
    }

    public int PageCount => _pages.Length;
    public PageExtentIndex Extents => _extents;
    public IReadOnlyList<PageLayoutItem> Metadata => _pages;
    public VirtualizationOptions Options => _options;

    public RealizationPlan Plan(double viewportOffset, double viewportExtent, ScrollDirection direction = ScrollDirection.None)
    {
        var visible = _extents.FindVisibleRange(viewportOffset, viewportExtent);
        var visibleCount = visible.Last - visible.First + 1;
        // Keep the policy invariant even if a caller uses a record `with` expression
        // to construct options: the host is never allowed to realize more than twelve.
        var max = Math.Clamp(_options.MaxRealizedPages, 1, 12);

        int first;
        int last;
        if (visibleCount >= max)
        {
            // An unusually tall viewport cannot fit within the policy cap. Keep the pages
            // nearest its center; the next layout pass will realize the rest as it scrolls.
            var center = visible.First + (visibleCount - 1) / 2;
            first = Math.Max(0, center - (max - 1) / 2);
            last = Math.Min(_pages.Length - 1, first + max - 1);
            first = Math.Max(0, last - max + 1);
        }
        else
        {
            var before = direction == ScrollDirection.Forward ? 0 : _options.OverscanBeforePages;
            var after = direction == ScrollDirection.Backward ? 0 : _options.OverscanAfterPages;
            first = Math.Max(0, visible.First - before);
            last = Math.Min(_pages.Length - 1, visible.Last + after);

            // At a document edge, use the available budget in the scrolling direction.
            var count = last - first + 1;
            if (count < max && direction == ScrollDirection.Forward)
                last = Math.Min(_pages.Length - 1, last + max - count);
            else if (count < max && direction == ScrollDirection.Backward)
                first = Math.Max(0, first - (max - count));

            if (last - first + 1 > max)
                last = first + max - 1;
        }

        return new RealizationPlan(_pages.Skip(first).Take(last - first + 1).ToImmutableArray(), visible.First, visible.Last, first, last, direction);
    }

    public RealizationPlan Realize(double viewportOffset, double viewportExtent, ScrollDirection direction = ScrollDirection.None)
        => Plan(viewportOffset, viewportExtent, direction);

    public int GetCurrentPage(double viewportOffset, double viewportExtent)
        => CurrentPageCalculator.Calculate(_extents, viewportOffset, viewportExtent);

    public PageLayoutItem GetPage(int pageIndex) => _pages[pageIndex];

    /// <summary>Changes geometry without changing the page's stable identity.</summary>
    public void UpdateGeometry(int pageIndex, PageLayoutGeometry geometry)
    {
        if ((uint)pageIndex >= (uint)_pages.Length) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        geometry.Validate();
        _pages[pageIndex] = _pages[pageIndex] with { Geometry = geometry };
        var extent = geometry.Extent + _extents.PageGap;
        if (pageIndex == _pages.Length - 1) extent = geometry.Extent;
        _extents.UpdateExtent(pageIndex, extent);
    }
}

public static class CurrentPageCalculator
{
    /// <summary>Returns the page with the largest visible area; only visible pages are examined.</summary>
    public static int Calculate(PageExtentIndex extents, double viewportOffset, double viewportExtent)
    {
        ArgumentNullException.ThrowIfNull(extents);
        var range = extents.FindVisibleRange(viewportOffset, viewportExtent);
        var viewportEnd = Math.Min(extents.TotalExtent, viewportOffset + viewportExtent);
        var best = range.First;
        var bestArea = -1d;
        for (var i = range.First; i <= range.Last; i++)
        {
            var top = extents.GetOffset(i);
            var bottom = top + extents.GetExtent(i);
            var area = Math.Max(0, Math.Min(bottom, viewportEnd) - Math.Max(top, viewportOffset));
            if (area > bestArea)
            {
                best = i;
                bestArea = area;
            }
        }

        return best;
    }
}
