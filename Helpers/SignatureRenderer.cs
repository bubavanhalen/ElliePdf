using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ElliePdf.Helpers;

/// <summary>A captured pen sample, kept free of UI types so this helper stays testable.</summary>
internal readonly record struct StrokePoint(double X, double Y);

/// <summary>
/// Rasterizes captured signature strokes into a transparent PNG cropped to the drawn content, and
/// decodes them back for embedding as a PDF image stamp.
/// </summary>
internal static class SignatureRenderer
{
    private const float StrokeWidth = 2.4f;
    private const float Supersample = 3f;
    private const float PaddingDips = 6f;

    public static bool TryRender(
        IReadOnlyList<IReadOnlyList<StrokePoint>> strokes,
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

    /// <summary>Decodes a stored signature PNG into tightly packed BGRA rows for PDFium.</summary>
    public static bool TryDecodeBgra(string imageBase64, out byte[] pixels, out int width, out int height)
    {
        pixels = [];
        width = 0;
        height = 0;

        try
        {
            var bytes = Convert.FromBase64String(imageBase64);
            using var stream = new MemoryStream(bytes);
            using var source = new Bitmap(stream);

            width = source.Width;
            height = source.Height;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            var rect = new Rectangle(0, 0, width, height);
            var data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            try
            {
                var rowBytes = width * 4;
                pixels = new byte[rowBytes * height];
                for (var row = 0; row < height; row++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        data.Scan0 + (row * data.Stride),
                        pixels,
                        row * rowBytes,
                        rowBytes);
                }
            }
            finally
            {
                source.UnlockBits(data);
            }

            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OutOfMemoryException)
        {
            return false;
        }
    }
}
