using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;

namespace ElliePdf.Rendering;

/// <summary>Geometry of a page in the scrolling (display) coordinate space.</summary>
public readonly record struct PageLayoutGeometry(double Width, double Height, PageRotation Rotation = PageRotation.None)
{
    public PageLayoutGeometry Validate()
    {
        if (!double.IsFinite(Width) || Width <= 0) throw new ArgumentOutOfRangeException(nameof(Width));
        if (!double.IsFinite(Height) || Height <= 0) throw new ArgumentOutOfRangeException(nameof(Height));
        return this;
    }

    public double DisplayWidth => Rotation is PageRotation.Clockwise90 or PageRotation.Clockwise270 ? Height : Width;
    public double DisplayHeight => Rotation is PageRotation.Clockwise90 or PageRotation.Clockwise270 ? Width : Height;
    public double Extent => DisplayHeight;

    public static PageLayoutGeometry From(PageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new PageLayoutGeometry(metadata.SizeInPoints.Width, metadata.SizeInPoints.Height, metadata.Geometry.Rotation).Validate();
    }
}

/// <summary>Stable, lightweight metadata used by the virtualized page host.</summary>
public sealed record PageLayoutItem(PageId Id, int PageIndex, PageLayoutGeometry Geometry)
{
    public PageLayoutItem(PageMetadata metadata) : this(metadata.Id, metadata.PageIndex, PageLayoutGeometry.From(metadata)) { }

    public PageLayoutItem Validate()
    {
        if (Id.Value == Guid.Empty) throw new ArgumentException("The page id must not be empty.", nameof(Id));
        if (PageIndex < 0) throw new ArgumentOutOfRangeException(nameof(PageIndex));
        Geometry.Validate();
        return this;
    }
}

/// <summary>
/// Indexed page extents. Updates and both directions of geometry lookup are O(log n).
/// The backing array contains only one scalar per page; no page controls are allocated.
/// </summary>
public sealed class PageExtentIndex
{
    private readonly double[] _extents;
    private readonly double[] _tree;

    public PageExtentIndex(IEnumerable<double> extents)
    {
        ArgumentNullException.ThrowIfNull(extents);
        _extents = extents.ToArray();
        if (_extents.Length == 0) throw new ArgumentException("At least one page extent is required.", nameof(extents));
        _tree = new double[_extents.Length + 1];
        for (var i = 0; i < _extents.Length; i++)
        {
            ValidateExtent(_extents[i]);
            _tree[i + 1] += _extents[i];
            var parent = i + 1 + ((i + 1) & -(i + 1));
            if (parent < _tree.Length) _tree[parent] += _tree[i + 1];
        }
    }

    public PageExtentIndex(IEnumerable<PageLayoutItem> pages, double pageGap = 0)
        : this(pages?.Select(p => p.Validate().Geometry) ?? throw new ArgumentNullException(nameof(pages)), pageGap)
    {
    }

    public PageExtentIndex(IEnumerable<PageLayoutGeometry> geometries, double pageGap = 0)
        : this(geometries?.Select(g => g.Validate().Extent + ValidateGap(pageGap)) ?? throw new ArgumentNullException(nameof(geometries)))
    {
        PageGap = pageGap;
        if (pageGap != 0)
        {
            // The final page has no following gap.
            UpdateExtent(_extents.Length - 1, _extents[^1] - pageGap);
        }
    }

    public int Count => _extents.Length;
    public double PageGap { get; }
    public double TotalExtent => PrefixSum(Count);
    public long TotalOperationCount { get; private set; }
    public int LastOperationCount { get; private set; }

    public double GetExtent(int pageIndex)
    {
        ValidateIndex(pageIndex);
        ResetOperationCount();
        var value = _extents[pageIndex];
        LastOperationCount = 1;
        return value;
    }

    /// <summary>Returns the scrolling offset of the top of <paramref name="pageIndex"/>.</summary>
    public double GetOffset(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex > Count) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        ResetOperationCount();
        var result = PrefixSumCore(pageIndex);
        FinishOperationCount();
        return result;
    }

    /// <summary>Finds the page containing an offset. Boundary offsets select the page below the boundary.</summary>
    public int FindPageAtOffset(double offset)
    {
        if (!double.IsFinite(offset)) throw new ArgumentOutOfRangeException(nameof(offset));
        ResetOperationCount();
        if (offset <= 0) { LastOperationCount = 1; FinishOperationCount(); return 0; }
        if (offset >= TotalExtent) { LastOperationCount = 1; FinishOperationCount(); return Count - 1; }

        var index = 0;
        var sum = 0d;
        var bit = HighestPowerOfTwoAtMost(Count);
        while (bit != 0)
        {
            LastOperationCount++;
            var next = index + bit;
            if (next <= Count && sum + _tree[next] <= offset)
            {
                index = next;
                sum += _tree[next];
            }
            bit >>= 1;
        }

        // index is the number of complete pages before the containing page.
        LastOperationCount += 1;
        FinishOperationCount();
        return Math.Min(index, Count - 1);
    }

    public (int First, int Last) FindVisibleRange(double viewportOffset, double viewportExtent)
    {
        if (!double.IsFinite(viewportOffset) || viewportOffset < 0) throw new ArgumentOutOfRangeException(nameof(viewportOffset));
        if (!double.IsFinite(viewportExtent) || viewportExtent <= 0) throw new ArgumentOutOfRangeException(nameof(viewportExtent));
        var start = Math.Min(viewportOffset, TotalExtent);
        var end = Math.Min(TotalExtent, start + viewportExtent);
        var first = FindPageAtOffset(start);
        // End is exclusive. BitDecrement keeps an exact page boundary in the preceding page.
        var last = FindPageAtOffset(end >= TotalExtent ? end : Math.BitDecrement(end));
        return (first, Math.Max(first, last));
    }

    /// <summary>Changes one page extent and updates all affected prefix sums in O(log n).</summary>
    public void UpdateExtent(int pageIndex, double extent)
    {
        ValidateIndex(pageIndex);
        ValidateExtent(extent);
        ResetOperationCount();
        var delta = extent - _extents[pageIndex];
        _extents[pageIndex] = extent;
        for (var i = pageIndex + 1; i <= Count; i += i & -i)
        {
            _tree[i] += delta;
            LastOperationCount++;
        }
        FinishOperationCount();
    }

    public double PrefixSum(int pageCount)
    {
        if (pageCount < 0 || pageCount > Count) throw new ArgumentOutOfRangeException(nameof(pageCount));
        ResetOperationCount();
        var sum = PrefixSumCore(pageCount);
        FinishOperationCount();
        return sum;
    }

    private double PrefixSumCore(int pageCount)
    {
        var sum = 0d;
        for (var i = pageCount; i > 0; i -= i & -i)
        {
            LastOperationCount++;
            sum += _tree[i];
        }
        return sum;
    }

    private void ResetOperationCount() => LastOperationCount = 0;
    private void FinishOperationCount() => TotalOperationCount += LastOperationCount;
    private void ValidateIndex(int index) { if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index)); }
    private static void ValidateExtent(double value) { if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value)); }
    private static double ValidateGap(double gap) { if (!double.IsFinite(gap) || gap < 0) throw new ArgumentOutOfRangeException(nameof(gap)); return gap; }
    private static int HighestPowerOfTwoAtMost(int value) { var bit = 1; while (bit <= value / 2) bit <<= 1; return bit; }
}
