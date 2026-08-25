using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using ElliePdf.Models;

namespace ElliePdf.Helpers;

internal static class OverlayCompositor
{
    public static bool HasContent(PageOverlayState? overlay) =>
        overlay is not null &&
        (overlay.InkStrokes.Any(stroke => stroke.Points.Count > 1) ||
         overlay.TextItems.Any(text => !string.IsNullOrWhiteSpace(text.Text)) ||
         overlay.Signatures.Any(signature => !string.IsNullOrWhiteSpace(signature.ImageBase64)));

    public static byte[] Composite(
        byte[] sourceBgra,
        int width,
        int height,
        PageOverlayState overlay,
        float pageWidthPoints,
        float pageHeightPoints)
    {
        var scaleX = width / pageWidthPoints;
        var scaleY = height / pageHeightPoints;

        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        Marshal.Copy(sourceBgra, 0, data.Scan0, sourceBgra.Length);
        bitmap.UnlockBits(data);

        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.AntiAlias;

        foreach (var stroke in overlay.InkStrokes)
        {
            if (stroke.Points.Count < 2)
            {
                continue;
            }

            using var pen = new Pen(
                ParseColor(stroke.ColorHex),
                Math.Max(1f, (float)(stroke.Thickness * scaleX)))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            var points = stroke.Points
                .Select(point => new PointF(
                    (float)(point.X * scaleX),
                    (float)(point.Y * scaleY)))
                .ToArray();
            graphics.DrawLines(pen, points);
        }

        foreach (var text in overlay.TextItems)
        {
            if (string.IsNullOrWhiteSpace(text.Text))
            {
                continue;
            }

            var style = FontStyle.Regular;
            if (text.IsBold)
            {
                style |= FontStyle.Bold;
            }

            if (text.IsItalic)
            {
                style |= FontStyle.Italic;
            }

            // FontSize is stored in page points; GDI+ defaults to typographic points, so render
            // in pixels to match what the on-screen edit surface shows.
            using var font = new Font(
                "Segoe UI",
                Math.Max(1f, (float)(text.FontSize * scaleY)),
                style,
                GraphicsUnit.Pixel);
            using var brush = new SolidBrush(ParseColor(text.ColorHex));
            using var format = new StringFormat(StringFormatFlags.NoClip)
            {
                Trimming = StringTrimming.None
            };

            var originX = (float)(text.X * scaleX);
            var originY = (float)(text.Y * scaleY);
            var layout = new RectangleF(
                originX,
                originY,
                (float)(Math.Max(24, text.Width) * scaleX),
                Math.Max(1f, height - originY));
            graphics.DrawString(text.Text, font, brush, layout, format);
        }

        foreach (var signature in overlay.Signatures)
        {
            TryDrawSignature(graphics, signature, scaleX, scaleY);
        }

        var output = new byte[width * height * 4];
        var outputData = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        Marshal.Copy(outputData.Scan0, output, 0, output.Length);
        bitmap.UnlockBits(outputData);
        return output;
    }

    private static void TryDrawSignature(Graphics graphics, SignatureOverlay signature, double scaleX, double scaleY)
    {
        if (string.IsNullOrWhiteSpace(signature.ImageBase64))
        {
            return;
        }

        try
        {
            var bytes = Convert.FromBase64String(signature.ImageBase64);
            using var stream = new MemoryStream(bytes);
            using var image = Image.FromStream(stream);
            var destination = new RectangleF(
                (float)(signature.X * scaleX),
                (float)(signature.Y * scaleY),
                (float)(signature.Width * scaleX),
                (float)(signature.Height * scaleY));
            graphics.DrawImage(image, destination);
        }
        catch (Exception)
        {
        }
    }

    private static Color ParseColor(string colorHex)
    {
        if (string.IsNullOrWhiteSpace(colorHex))
        {
            return Color.Black;
        }

        var hex = colorHex.Trim().TrimStart('#');
        if (hex.Length == 6 &&
            int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var value))
        {
            return Color.FromArgb(
                255,
                (value >> 16) & 0xff,
                (value >> 8) & 0xff,
                value & 0xff);
        }

        return Color.Black;
    }
}
