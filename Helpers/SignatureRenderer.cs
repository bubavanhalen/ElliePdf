using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ElliePdf.Helpers;

/// <summary>
/// Rasterizes captured signature strokes into a transparent PNG cropped to the drawn content.
/// Rendering from the raw points (instead of the live XAML canvas) keeps capture independent of
/// dialog lifetime and produces a tight, correctly proportioned image.
/// </summary>
internal static class SignatureRenderer
{
    private const float StrokeWidth = 2.4f;
    private const float Supersample = 3f;
    private const float PaddingDips = 6f;

    public static bool TryRender(
        IReadOnlyList<IReadOnlyList<Windows.Foundation.Point>> strokes,
        out byte[] pngBytes,
        out double aspectRatio)
    {
        pngBytes = [];
        aspectRatio = 2.0;

        var drawable = strokes.Where(stroke => stroke.Count >= 2).ToList();
        if (drawable.Count == 0)
        {
            return false;
        }

        var minX = drawable.Min(stroke => stroke.Min(point => point.X));
        var minY = drawable.Min(stroke => stroke.Min(point => point.Y));
        var maxX = drawable.Max(stroke => stroke.Max(point => point.X));
        var maxY = drawable.Max(stroke => stroke.Max(point => point.Y));

        var left = (float)minX - PaddingDips;
        var top = (float)minY - PaddingDips;
        var contentWidth = (float)(maxX - minX) + PaddingDips * 2;
        var contentHeight = (float)(maxY - minY) + PaddingDips * 2;

        var pixelWidth = Math.Max(1, (int)Math.Ceiling(contentWidth * Supersample));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(contentHeight * Supersample));

        using var bitmap = new Bitmap(pixelWidth, pixelHeight, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using var pen = new Pen(Color.Black, StrokeWidth * Supersample)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            foreach (var stroke in drawable)
            {
                var points = stroke
                    .Select(point => new PointF(
                        (float)(point.X - left) * Supersample,
                        (float)(point.Y - top) * Supersample))
                    .ToArray();
                graphics.DrawLines(pen, points);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        pngBytes = stream.ToArray();
        aspectRatio = (double)pixelWidth / pixelHeight;
        return true;
    }
}
