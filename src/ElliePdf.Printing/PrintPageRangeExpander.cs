using System.Collections.Immutable;
using ElliePdf.Pdf.Contracts;

namespace ElliePdf.Printing;

public static class PrintPageRangeExpander
{
    public static ImmutableArray<int> Expand(PrintPageSelection selection, int pageCount, int currentPageIndex)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (pageCount <= 0) throw new ArgumentOutOfRangeException(nameof(pageCount));
        if (currentPageIndex < 0 || currentPageIndex >= pageCount) throw new ArgumentOutOfRangeException(nameof(currentPageIndex));

        return selection.Kind switch
        {
            PrintPageSelectionKind.CurrentPage => [currentPageIndex],
            PrintPageSelectionKind.AllPages => Enumerable.Range(0, pageCount).ToImmutableArray(),
            PrintPageSelectionKind.CustomRanges => ExpandRanges(selection.Ranges, pageCount),
            _ => throw new ArgumentOutOfRangeException(nameof(selection))
        };
    }

    private static ImmutableArray<int> ExpandRanges(ImmutableArray<PrintPageRange> ranges, int pageCount)
    {
        if (ranges.IsDefaultOrEmpty) throw new ArgumentException("Custom printing requires ranges.", nameof(ranges));
        var result = ImmutableArray.CreateBuilder<int>();
        foreach (var range in ranges)
        {
            if (range.LastPageIndex >= pageCount) throw new ArgumentOutOfRangeException(nameof(ranges), "A print range exceeds the document page count.");
            for (var page = range.FirstPageIndex; page <= range.LastPageIndex; page++) result.Add(page);
        }
        return result.ToImmutable();
    }
}
