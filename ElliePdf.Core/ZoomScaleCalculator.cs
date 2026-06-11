namespace ElliePdf;

public static class ZoomScaleCalculator
{
    public static double ResolveScale(
        PdfZoomMode zoomMode,
        double zoomScale,
        double viewportWidth,
        float pageWidthPoints = 612f,
        float pageHeightPoints = 792f)
    {
        return zoomMode switch
        {
            PdfZoomMode.FitWidth => Math.Max(0.25, viewportWidth / pageWidthPoints),
            PdfZoomMode.FitPage => Math.Max(0.25, Math.Min(viewportWidth / pageWidthPoints, 900 / pageHeightPoints)),
            PdfZoomMode.ActualSize => 96.0 / 72.0,
            _ => zoomScale
        };
    }
}
