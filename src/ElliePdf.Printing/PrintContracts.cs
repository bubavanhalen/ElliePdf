using System.Collections.Immutable;
using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;

namespace ElliePdf.Printing;

public enum PrintPageSelectionKind { CurrentPage, AllPages, CustomRanges }
public enum PrintScalingMode { FitToPrintableArea, ActualSize }

public sealed record PrintPageSelection
{
    private PrintPageSelection(PrintPageSelectionKind kind, ImmutableArray<PrintPageRange> ranges)
    {
        Kind = kind;
        Ranges = ranges;
    }

    public PrintPageSelectionKind Kind { get; }
    public ImmutableArray<PrintPageRange> Ranges { get; }
    public static PrintPageSelection CurrentPage() => new(PrintPageSelectionKind.CurrentPage, []);
    public static PrintPageSelection AllPages() => new(PrintPageSelectionKind.AllPages, []);
    public static PrintPageSelection Custom(IEnumerable<PrintPageRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var result = ranges.ToImmutableArray();
        if (result.IsDefaultOrEmpty) throw new ArgumentException("Custom printing requires at least one range.", nameof(ranges));
        if (result.Length > PdfContractLimits.MaxPageRanges) throw new ArgumentOutOfRangeException(nameof(ranges));
        return new(PrintPageSelectionKind.CustomRanges, result);
    }
}

public sealed record PrintTarget(double PrintableWidthPoints, double PrintableHeightPoints, int Dpi = 150)
{
    public PrintTarget Validate()
    {
        if (!double.IsFinite(PrintableWidthPoints) || PrintableWidthPoints <= 0) throw new ArgumentOutOfRangeException(nameof(PrintableWidthPoints));
        if (!double.IsFinite(PrintableHeightPoints) || PrintableHeightPoints <= 0) throw new ArgumentOutOfRangeException(nameof(PrintableHeightPoints));
        if (Dpi is < 36 or > 1200) throw new ArgumentOutOfRangeException(nameof(Dpi));
        return this;
    }
}

public sealed record PrintPipelineRequest(
    DocumentId DocumentId,
    PrintPageSelection Selection,
    PrintTarget Target,
    PrintScalingMode Scaling = PrintScalingMode.FitToPrintableArea,
    double UserScale = 1)
{
    public PrintPipelineRequest Validate()
    {
        if (DocumentId.Value == Guid.Empty) throw new ArgumentException("A document id is required.", nameof(DocumentId));
        ArgumentNullException.ThrowIfNull(Selection);
        ArgumentNullException.ThrowIfNull(Target);
        Target.Validate();
        if (!double.IsFinite(UserScale) || UserScale <= 0) throw new ArgumentOutOfRangeException(nameof(UserScale));
        return this;
    }
}

public sealed record PrintPagePlan(
    int PageIndex,
    PageId PageId,
    PageRotation Rotation,
    PdfSize SizeInPoints,
    bool IsLandscape,
    double EffectiveScale,
    double RasterPixelsPerPoint,
    int RasterWidth,
    int RasterHeight,
    int TileCount);

public sealed record PrintPageSurface(
    PrintPagePlan Plan,
    int TileIndex,
    int TileCount,
    IPixelBufferLease PixelBuffer,
    IAsyncDisposable reservation) : IAsyncDisposable
{
    private int _disposed;
    public bool IsFirstTile => TileIndex == 0;
    public bool IsLastTile => TileIndex == TileCount - 1;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await PixelBuffer.DisposeAsync().ConfigureAwait(false); }
        finally { await reservation.DisposeAsync().ConfigureAwait(false); }
    }
}

public interface IPrintSurfaceConsumer
{
    /// <summary>Receives a direct BGRA tile; the pipeline disposes it immediately after this call completes.</summary>
    ValueTask ConsumeAsync(PrintPageSurface surface, CancellationToken cancellationToken);
}

public sealed record PrintPipelineResult(int PrintedPageCount, int PrintedTileCount, long PeakRetainedBytes);

public sealed class PrintNotAllowedException : InvalidOperationException
{
    public PrintNotAllowedException() : base("This PDF does not grant permission to print.") { }
}
