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

    /// <summary>Longest edge kept when importing; far more than a signature stamp ever needs.</summary>
    private const int MaxImportEdge = 1600;

    public static bool TryRender(
        IReadOnlyList<IReadOnlyList<StrokePoint>> strokes,
        out byte[] pngBytes,
        out double aspectRatio)
    {        pngBytes = [];
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

    /// <summary>Renders a typed name as a signature in a script-like face.</summary>
    public static bool TryRenderTyped(string text, string fontFamily, out byte[] pngBytes, out double aspectRatio)
    {
        pngBytes = [];
        aspectRatio = 2.0;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        const float fontSize = 64f;
        const float padding = 12f;

        using var font = new Font(fontFamily, fontSize, FontStyle.Italic, GraphicsUnit.Pixel);

        // Measure on a throwaway surface before allocating the real one.
        using (var measuring = Graphics.FromImage(new Bitmap(1, 1)))
        {
            var size = measuring.MeasureString(text, font);
            var width = Math.Max(1, (int)Math.Ceiling(size.Width + (padding * 2)));
            var height = Math.Max(1, (int)Math.Ceiling(size.Height + (padding * 2)));

            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

                using var brush = new SolidBrush(Color.Black);
                graphics.DrawString(text, font, brush, padding, padding);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            pngBytes = stream.ToArray();
            aspectRatio = (double)width / height;
        }

        return true;
    }

    /// <summary>
    /// Normalises an imported image into a transparent PNG, dropping a white-ish background so a
    /// photographed or scanned signature does not arrive as a white box.
    /// </summary>
    /// <remarks>
    /// Phone photos run to tens of megapixels, so this works over a locked buffer rather than
    /// per-pixel GDI+ calls, and downscales anything larger than a signature could ever need.
    /// </remarks>
    public static bool TryImport(byte[] imageBytes, out byte[] pngBytes, out double aspectRatio)
    {
        pngBytes = [];
        aspectRatio = 2.0;

        try
        {
            using var stream = new MemoryStream(imageBytes);
            using var source = new Bitmap(stream);

            if (source.Width <= 0 || source.Height <= 0)
            {
                return false;
            }

            var scale = Math.Min(1.0, (double)MaxImportEdge / Math.Max(source.Width, source.Height));
            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));

            using var normalised = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(normalised))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, 0, 0, width, height);
            }

            RemoveLightBackground(normalised);

            using var output = new MemoryStream();
            normalised.Save(output, ImageFormat.Png);
            pngBytes = output.ToArray();
            aspectRatio = (double)width / height;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or IOException)
        {
            return false;
        }
    }

    /// <summary>Fades light pixels to transparent, leaving ink opaque and edges smooth.</summary>
    private static void RemoveLightBackground(Bitmap bitmap)
    {
        const int opaqueBelow = 120;
        const int transparentAbove = 230;

        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

        try
        {
            var length = Math.Abs(data.Stride) * bitmap.Height;
            var buffer = new byte[length];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, length);

            for (var offset = 0; offset < length; offset += 4)
            {
                // Buffer is BGRA.
                var brightness = (buffer[offset] + buffer[offset + 1] + buffer[offset + 2]) / 3;

                buffer[offset + 3] = brightness >= transparentAbove
                    ? (byte)0
                    : brightness <= opaqueBelow
                        ? (byte)255
                        : (byte)(255 * (transparentAbove - brightness) / (transparentAbove - opaqueBelow));
            }

            System.Runtime.InteropServices.Marshal.Copy(buffer, 0, data.Scan0, length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
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
