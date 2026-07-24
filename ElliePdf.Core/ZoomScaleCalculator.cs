namespace ElliePdf;

public static class ZoomScaleCalculator
{
    public static double ResolveScale(
        PdfZoomMode zoomMode,
        double zoomScale,
        double viewportWidth,
        float pageWidthPoints = 612f,
        float pageHeightPoints = 792f,
        double viewportHeight = 900)
    {
        return zoomMode switch
        {
            PdfZoomMode.FitWidth => Math.Max(0.25, viewportWidth / pageWidthPoints),
            PdfZoomMode.FitPage => Math.Max(
                0.25,
                Math.Min(viewportWidth / pageWidthPoints, viewportHeight / pageHeightPoints)),
            PdfZoomMode.ActualSize => 96.0 / 72.0,
            _ => zoomScale
        };
    }
}
