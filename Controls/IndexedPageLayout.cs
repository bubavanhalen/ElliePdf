using System.Collections.Specialized;
using ElliePdf.Rendering;
using ElliePdf.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace ElliePdf.Controls;

/// <summary>
/// A variable-height, index-backed layout for the continuous reader. Only the
/// viewport plus one page of directional overscan is realized. Page offsets
/// and offset-to-page lookup use a Fenwick-tree <see cref="PageExtentIndex"/>,
/// so scrolling work is independent of the document page count.
/// </summary>
public sealed partial class IndexedPageLayout : VirtualizingLayout
{
    private const double PageSpacing = 16;
    private const int OverscanPages = 1;
    private const int MaximumRealizedPages = 12;

    public IndexedPageLayout() => SetIndexBasedLayoutOrientation(IndexBasedLayoutOrientation.TopToBottom);

    protected override void InitializeForContextCore(VirtualizingLayoutContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.LayoutState = LayoutState.Create(context);
    }

    protected override void UninitializeForContextCore(VirtualizingLayoutContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.LayoutState is LayoutState state)
        {
            state.RecycleAll(context);
        }

        context.LayoutState = null;
    }

    protected override void OnItemsChangedCore(
        VirtualizingLayoutContext context,
        object source,
        NotifyCollectionChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(args);
        if (context.LayoutState is LayoutState oldState)
        {
            oldState.RecycleAll(context);
        }

        context.LayoutState = LayoutState.Create(context);
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(VirtualizingLayoutContext context, Size availableSize)
    {
        ArgumentNullException.ThrowIfNull(context);
        var state = GetState(context);
        if (state.Count == 0)
        {
            state.RecycleAll(context);
            return new Size(0, 0);
        }

        // A rendered page can replace an estimated extent. At most twelve
        // realized items are inspected and each update remains O(log n).
        state.RefreshRealizedMetrics(context);
        var (first, last) = GetRealizationRange(context, state, availableSize);
        state.RecycleOutside(context, first, last);

        for (var index = first; index <= last; index++)
        {
            var element = context.GetOrCreateElementAt(
                index,
                ElementRealizationOptions.SuppressAutoRecycle);
            state.Elements[index] = element;
            state.RefreshMetric(context, index);
            element.Measure(new Size(state.Widths[index], state.Heights[index]));
        }

        context.LayoutOrigin = new Point(0, 0);
        var width = double.IsFinite(availableSize.Width)
            ? Math.Max(availableSize.Width, state.MaximumWidth)
            : state.MaximumWidth;
        return new Size(width, state.TotalExtent);
    }

    protected override Size ArrangeOverride(VirtualizingLayoutContext context, Size finalSize)
    {
        ArgumentNullException.ThrowIfNull(context);
        var state = GetState(context);
        var layoutWidth = Math.Max(finalSize.Width, state.MaximumWidth);
        foreach (var pair in state.Elements.OrderBy(static pair => pair.Key))
        {
            var index = pair.Key;
            var width = state.Widths[index];
            var height = state.Heights[index];
            var left = Math.Max(0, (layoutWidth - width) / 2);
            pair.Value.Arrange(new Rect(left, state.GetOffset(index), width, height));
        }

        return new Size(layoutWidth, Math.Max(finalSize.Height, state.TotalExtent));
    }

    private static LayoutState GetState(VirtualizingLayoutContext context)
    {
        if (context.LayoutState is LayoutState state && state.Count == context.ItemCount)
        {
            return state;
        }

        if (context.LayoutState is LayoutState stale)
        {
            stale.RecycleAll(context);
        }

        state = LayoutState.Create(context);
        context.LayoutState = state;
        return state;
    }

    private static (int First, int Last) GetRealizationRange(
        VirtualizingLayoutContext context,
        LayoutState state,
        Size availableSize)
    {
        var realization = context.RealizationRect;
        var viewportHeight = double.IsFinite(realization.Height) && realization.Height > 0
            ? realization.Height
            : double.IsFinite(availableSize.Height) && availableSize.Height > 0
                ? availableSize.Height
                : state.Heights[0];
        var viewportOffset = double.IsFinite(realization.Y)
            ? Math.Clamp(realization.Y, 0, state.TotalExtent)
            : 0;
        var visible = state.Extents!.FindVisibleRange(viewportOffset, viewportHeight);
        var first = Math.Max(0, visible.First - OverscanPages);
        var last = Math.Min(state.Count - 1, visible.Last + OverscanPages);

        var anchor = context.RecommendedAnchorIndex;
        if ((uint)anchor < (uint)state.Count)
        {
            first = Math.Min(first, anchor);
            last = Math.Max(last, anchor);
        }

        if (last - first + 1 > MaximumRealizedPages)
        {
            var center = (uint)anchor < (uint)state.Count ? anchor : visible.First;
            first = Math.Clamp(center - (MaximumRealizedPages / 2), 0, state.Count - MaximumRealizedPages);
            last = first + MaximumRealizedPages - 1;
        }

        return (first, last);
    }

    private sealed class LayoutState
    {
        private LayoutState(double[] widths, double[] heights, PageExtentIndex? extents)
        {
            Widths = widths;
            Heights = heights;
            Extents = extents;
            MaximumWidth = widths.Length == 0 ? 0 : widths.Max();
        }

        public double[] Widths { get; }
        public double[] Heights { get; }
        public PageExtentIndex? Extents { get; }
        public Dictionary<int, UIElement> Elements { get; } = [];
        public int Count => Widths.Length;
        public double MaximumWidth { get; private set; }
        public double TotalExtent => Extents?.TotalExtent ?? 0;

        public static LayoutState Create(VirtualizingLayoutContext context)
        {
            var widths = new double[context.ItemCount];
            var heights = new double[context.ItemCount];
            var extents = new double[context.ItemCount];
            for (var index = 0; index < context.ItemCount; index++)
            {
                var metric = ReadMetric(context.GetItemAt(index));
                widths[index] = metric.Width;
                heights[index] = metric.Height;
                extents[index] = metric.Height + (index == context.ItemCount - 1 ? 0 : PageSpacing);
            }

            return new LayoutState(
                widths,
                heights,
                context.ItemCount == 0 ? null : new PageExtentIndex(extents));
        }

        public double GetOffset(int index) => Extents?.GetOffset(index) ?? 0;

        public void RefreshRealizedMetrics(VirtualizingLayoutContext context)
        {
            foreach (var index in Elements.Keys.ToArray())
            {
                if ((uint)index < (uint)Count)
                {
                    RefreshMetric(context, index);
                }
            }
        }

        public void RefreshMetric(VirtualizingLayoutContext context, int index)
        {
            var metric = ReadMetric(context.GetItemAt(index));
            if (Math.Abs(Heights[index] - metric.Height) > 0.01)
            {
                Heights[index] = metric.Height;
                Extents!.UpdateExtent(index, metric.Height + (index == Count - 1 ? 0 : PageSpacing));
            }

            if (Math.Abs(Widths[index] - metric.Width) > 0.01)
            {
                var oldWidth = Widths[index];
                Widths[index] = metric.Width;
                MaximumWidth = metric.Width > MaximumWidth
                    ? metric.Width
                    : Math.Abs(oldWidth - MaximumWidth) <= 0.01
                        ? Widths.Max()
                        : MaximumWidth;
            }
        }

        public void RecycleOutside(VirtualizingLayoutContext context, int first, int last)
        {
            foreach (var pair in Elements.Where(pair => pair.Key < first || pair.Key > last).ToArray())
            {
                context.RecycleElement(pair.Value);
                Elements.Remove(pair.Key);
            }
        }

        public void RecycleAll(VirtualizingLayoutContext context)
        {
            foreach (var element in Elements.Values)
            {
                context.RecycleElement(element);
            }

            Elements.Clear();
        }

        private static (double Width, double Height) ReadMetric(object item)
        {
            if (item is not RenderedPageViewModel page)
            {
                return (1, 44);
            }

            return (
                Math.Max(1, page.PixelWidth),
                Math.Max(44, page.PixelHeight));
        }
    }
}
