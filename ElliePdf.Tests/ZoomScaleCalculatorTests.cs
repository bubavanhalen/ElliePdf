using Xunit;

namespace ElliePdf.Tests;

public class ZoomScaleCalculatorTests
{
    [Fact]
    public void FitWidth_uses_viewport_width()
    {
        var scale = ZoomScaleCalculator.ResolveScale(PdfZoomMode.FitWidth, 1.0, 612);
        Assert.Equal(1.0, scale, precision: 3);
    }

    [Fact]
    public void ActualSize_matches_screen_dpi()
    {
        var scale = ZoomScaleCalculator.ResolveScale(PdfZoomMode.ActualSize, 1.0, 800);
        Assert.Equal(96.0 / 72.0, scale, precision: 3);
    }

    [Fact]
    public void FitPage_uses_page_and_viewport_dimensions()
    {
        var scale = ZoomScaleCalculator.ResolveScale(
            PdfZoomMode.FitPage,
            1.0,
            viewportWidth: 612,
            pageWidthPoints: 612,
            pageHeightPoints: 792,
            viewportHeight: 792);
        Assert.Equal(1.0, scale, precision: 3);
    }
}
