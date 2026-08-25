using System.Runtime.InteropServices;
using ElliePdf.Helpers;
using ElliePdf.Models;

namespace ElliePdf.Services;

/// <summary>
/// Writes overlay annotations into a PDF page as native page objects: ink becomes stroked vector
/// paths, text becomes real selectable text objects, and signatures become image stamps.
/// </summary>
/// <remarks>
/// Overlay coordinates are stored in "display page points": origin top-left, Y growing downwards,
/// sized to the rotation-aware page box the reader shows. PDF user space is origin bottom-left with
/// Y growing upwards, relative to the crop box, and ignores <c>/Rotate</c>. Everything here funnels
/// through <see cref="BuildDisplayToPdfMatrix"/> so rotated and offset pages land correctly.
/// </remarks>
internal static class PdfOverlayWriter
{
    private const float TextPadding = 2f;

    /// <summary>PDFium's non-zero winding fill mode.</summary>
    private const int FillModeWinding = 2;

    /// <summary>
    /// Alpha for a shape's interior. Matches <c>PdfEditSurface.ShapeFillAlpha</c> so what the user
    /// positions on screen is what the saved file shows.
    /// </summary>
    private const uint ShapeFillAlpha = 70;

    // Arial/Helvetica metrics (units per em 2048): ascender 1854, descender 434, line gap 67.
    private const double BaselineFactor = 1854.0 / 2048.0;
    private const double LineHeightFactor = (1854.0 + 434.0 + 67.0) / 2048.0;

    public static bool HasContent(PageOverlayState? overlay) =>
        overlay is not null &&
        (overlay.InkStrokes.Any(stroke => stroke.Points.Count > 1) ||
         overlay.Shapes.Count > 0 ||
         overlay.TextItems.Any(text => !string.IsNullOrWhiteSpace(text.Text)) ||
         overlay.Signatures.Any(signature => !string.IsNullOrWhiteSpace(signature.ImageBase64)));

    /// <summary>
    /// Embeds every page overlay into <paramref name="document"/>. This is the real save path:
    /// objects are appended to the live document so its outline, links, form fields and text layer
    /// all survive.
    /// </summary>
    /// <remarks>
    /// The mutation cannot be undone, so the caller must discard the document afterwards and reopen
    /// from disk. It is deliberately not cancellable: abandoning it half-way would leave some pages
    /// annotated and others not, with the overlays still pending in the annotation store.
    /// </remarks>
    public static void WriteDocument(IntPtr document, PageOverlayDocument overlays, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(overlays);

        var pending = overlays.Pages
            .Where(entry => entry.Key >= 0 && entry.Key < pageCount && HasContent(entry.Value))
            .OrderBy(entry => entry.Key)
            .ToArray();

        if (pending.Length == 0)
        {
            return;
        }

        using var fonts = new OverlayFontProvider(document);

        foreach (var (pageIndex, overlay) in pending)
        {
            var page = PdfiumNative.FPDF_LoadPage(document, pageIndex);
            if (page == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Unable to load page {pageIndex + 1} for saving.");
            }

            try
            {
                Write(document, page, overlay, fonts);

                if (PdfiumNative.FPDFPage_GenerateContent(page) == 0)
                {
                    throw new InvalidOperationException($"PDFium could not update page {pageIndex + 1}.");
                }
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>Appends <paramref name="overlay"/> to an already-loaded page. Does not generate content.</summary>
    public static void Write(IntPtr document, IntPtr page, PageOverlayState overlay, OverlayFontProvider? fonts = null)
    {
        var ownedFonts = fonts is null ? new OverlayFontProvider(document) : null;
        var resolved = fonts ?? ownedFonts!;
        var matrix = BuildDisplayToPdfMatrix(page);
        var bitmaps = new List<IntPtr>();

        try
        {
            foreach (var stroke in overlay.InkStrokes)
            {
                WriteInkStroke(page, stroke, matrix);
            }

            foreach (var shape in overlay.Shapes)
            {
                WriteShape(page, shape, matrix);
            }

            foreach (var signature in overlay.Signatures)
            {
                WriteSignature(document, page, signature, matrix, bitmaps);
            }

            foreach (var text in overlay.TextItems)
            {
                WriteText(page, text, matrix, resolved);
            }
        }
        finally
        {
            foreach (var bitmap in bitmaps)
            {
                PdfiumNative.FPDFBitmap_Destroy(bitmap);
            }

            ownedFonts?.Dispose();
        }
    }

    // ═══════════ Ink ═══════════

    /// <summary>
    /// Constant-width strokes are written as a stroked path, which stays compact and crisp.
    /// Pressure-varying strokes have no stroked-path equivalent in PDF, so they are written as a
    /// filled outline built from the same geometry the edit surface draws.
    /// </summary>
    private static void WriteInkStroke(IntPtr page, InkStrokeOverlay stroke, Matrix2D matrix)
    {
        if (stroke.Points.Count < 2)
        {
            return;
        }

        var (r, g, b) = ParseColor(stroke.ColorHex);

        if (!InkGeometry.HasUniformPressure(stroke.Points))
        {
            var outline = InkGeometry.BuildOutline(stroke.Points, stroke.Thickness);
            if (outline.Count >= 3)
            {
                WriteFilledPolygon(page, outline.Select(v => (v.X, v.Y)), (r, g, b), matrix);
            }

            return;
        }

        var start = matrix.Transform(stroke.Points[0].X, stroke.Points[0].Y);
        var path = PdfiumNative.FPDFPageObj_CreateNewPath((float)start.X, (float)start.Y);
        if (path == IntPtr.Zero)
        {
            return;
        }

        for (var index = 1; index < stroke.Points.Count; index++)
        {
            var point = matrix.Transform(stroke.Points[index].X, stroke.Points[index].Y);
            PdfiumNative.FPDFPath_LineTo(path, (float)point.X, (float)point.Y);
        }

        var width = InkGeometry.WidthAt(stroke.Thickness, stroke.Points[0].Pressure);
        PdfiumNative.FPDFPageObj_SetStrokeColor(path, r, g, b, 255);
        // The display->PDF matrix is a rotation plus a flip, so widths carry over unscaled.
        PdfiumNative.FPDFPageObj_SetStrokeWidth(path, (float)Math.Max(0.1, width));
        PdfiumNative.FPDFPageObj_SetLineCap(path, 1);
        PdfiumNative.FPDFPageObj_SetLineJoin(path, 1);
        PdfiumNative.FPDFPath_SetDrawMode(path, 0, 1);
        PdfiumNative.FPDFPage_InsertObject(page, path);
    }

    /// <summary>Writes a closed, filled polygon in display coordinates.</summary>
    private static void WriteFilledPolygon(
        IntPtr page,
        IEnumerable<(double X, double Y)> vertices,
        (uint R, uint G, uint B) color,
        Matrix2D matrix)
    {
        var points = vertices.ToList();
        if (points.Count < 3)
        {
            return;
        }

        var start = matrix.Transform(points[0].X, points[0].Y);
        var path = PdfiumNative.FPDFPageObj_CreateNewPath((float)start.X, (float)start.Y);
        if (path == IntPtr.Zero)
        {
            return;
        }

        for (var index = 1; index < points.Count; index++)
        {
            var point = matrix.Transform(points[index].X, points[index].Y);
            PdfiumNative.FPDFPath_LineTo(path, (float)point.X, (float)point.Y);
        }

        PdfiumNative.FPDFPath_Close(path);
        PdfiumNative.FPDFPageObj_SetFillColor(path, color.R, color.G, color.B, 255);
        PdfiumNative.FPDFPath_SetDrawMode(path, FillModeWinding, 0);
        PdfiumNative.FPDFPage_InsertObject(page, path);
    }

    // ═══════════ Shapes ═══════════

    private static void WriteShape(IntPtr page, ShapeOverlay shape, Matrix2D matrix)
    {
        var stroke = ParseColor(shape.ColorHex);
        var fill = shape.FillColorHex is null ? ((uint, uint, uint)?)null : ParseColor(shape.FillColorHex);

        switch (shape.Kind)
        {
            case ShapeKind.Rectangle:
            {
                var corners = ShapeGeometry.RectangleCorners(shape);
                var start = matrix.Transform(corners[0].X, corners[0].Y);
                var path = PdfiumNative.FPDFPageObj_CreateNewPath((float)start.X, (float)start.Y);
                if (path == IntPtr.Zero)
                {
                    return;
                }

                for (var index = 1; index < corners.Count; index++)
                {
                    var point = matrix.Transform(corners[index].X, corners[index].Y);
                    PdfiumNative.FPDFPath_LineTo(path, (float)point.X, (float)point.Y);
                }

                PdfiumNative.FPDFPath_Close(path);
                FinishShapePath(page, path, shape, stroke, fill);
                break;
            }

            case ShapeKind.Ellipse:
            {
                var (origin, segments) = ShapeGeometry.EllipseCurves(shape);
                var start = matrix.Transform(origin.X, origin.Y);
                var path = PdfiumNative.FPDFPageObj_CreateNewPath((float)start.X, (float)start.Y);
                if (path == IntPtr.Zero)
                {
                    return;
                }

                foreach (var segment in segments)
                {
                    var c1 = matrix.Transform(segment.Control1.X, segment.Control1.Y);
                    var c2 = matrix.Transform(segment.Control2.X, segment.Control2.Y);
                    var end = matrix.Transform(segment.End.X, segment.End.Y);
                    PdfiumNative.FPDFPath_BezierTo(
                        path,
                        (float)c1.X, (float)c1.Y,
                        (float)c2.X, (float)c2.Y,
                        (float)end.X, (float)end.Y);
                }

                PdfiumNative.FPDFPath_Close(path);
                FinishShapePath(page, path, shape, stroke, fill);
                break;
            }

            case ShapeKind.Line:
                WriteSegment(page, shape, shape.Start.X, shape.Start.Y, shape.End.X, shape.End.Y, stroke, matrix);
                break;

            default:
            {
                var shaftEnd = ShapeGeometry.ArrowShaftEnd(shape);
                WriteSegment(page, shape, shape.Start.X, shape.Start.Y, shaftEnd.X, shaftEnd.Y, stroke, matrix);

                if (ShapeGeometry.ArrowHead(shape) is { } head)
                {
                    WriteFilledPolygon(page, head.Select(v => (v.X, v.Y)), stroke, matrix);
                }

                break;
            }
        }
    }

    private static void WriteSegment(
        IntPtr page,
        ShapeOverlay shape,
        double x1,
        double y1,
        double x2,
        double y2,
        (uint R, uint G, uint B) color,
        Matrix2D matrix)
    {
        var start = matrix.Transform(x1, y1);
        var path = PdfiumNative.FPDFPageObj_CreateNewPath((float)start.X, (float)start.Y);
        if (path == IntPtr.Zero)
        {
            return;
        }

        var end = matrix.Transform(x2, y2);
        PdfiumNative.FPDFPath_LineTo(path, (float)end.X, (float)end.Y);

        PdfiumNative.FPDFPageObj_SetStrokeColor(path, color.R, color.G, color.B, 255);
        PdfiumNative.FPDFPageObj_SetStrokeWidth(path, (float)Math.Max(0.1, shape.Thickness));
        PdfiumNative.FPDFPageObj_SetLineCap(path, 1);
        PdfiumNative.FPDFPageObj_SetLineJoin(path, 1);
        PdfiumNative.FPDFPath_SetDrawMode(path, 0, 1);
        PdfiumNative.FPDFPage_InsertObject(page, path);
    }

    private static void FinishShapePath(
        IntPtr page,
        IntPtr path,
        ShapeOverlay shape,
        (uint R, uint G, uint B) stroke,
        (uint R, uint G, uint B)? fill)
    {
        if (fill is { } interior)
        {
            PdfiumNative.FPDFPageObj_SetFillColor(path, interior.R, interior.G, interior.B, ShapeFillAlpha);
        }

        PdfiumNative.FPDFPageObj_SetStrokeColor(path, stroke.R, stroke.G, stroke.B, 255);
        PdfiumNative.FPDFPageObj_SetStrokeWidth(path, (float)Math.Max(0.1, shape.Thickness));
        PdfiumNative.FPDFPageObj_SetLineJoin(path, 1);
        PdfiumNative.FPDFPath_SetDrawMode(path, fill is null ? 0 : FillModeWinding, 1);
        PdfiumNative.FPDFPage_InsertObject(page, path);
    }

    // ═══════════ Signature ═══════════

    private static void WriteSignature(
        IntPtr document,
        IntPtr page,
        SignatureOverlay signature,
        Matrix2D matrix,
        List<IntPtr> bitmaps)
    {
        if (string.IsNullOrWhiteSpace(signature.ImageBase64) ||
            signature.Width <= 0 ||
            signature.Height <= 0)
        {
            return;
        }

        if (!SignatureRenderer.TryDecodeBgra(signature.ImageBase64, out var pixels, out var width, out var height))
        {
            return;
        }

        var bitmap = PdfiumNative.FPDFBitmap_Create(width, height, 1);
        if (bitmap == IntPtr.Zero)
        {
            return;
        }

        bitmaps.Add(bitmap);
        CopyRows(pixels, bitmap, width, height);

        var imageObject = PdfiumNative.FPDFPageObj_NewImageObj(document);
        if (imageObject == IntPtr.Zero)
        {
            return;
        }

        var pagePtr = page;
        unsafe
        {
            if (PdfiumNative.FPDFImageObj_SetBitmap(&pagePtr, 1, imageObject, bitmap) == 0)
            {
                PdfiumNative.FPDFPageObj_Destroy(imageObject);
                return;
            }
        }

        // A PDF image is drawn across the unit square, so map that square onto the display rect
        // and then push the whole thing through the page transform.
        var placement = new Matrix2D(
            signature.Width,
            0,
            0,
            -signature.Height,
            signature.X,
            signature.Y + signature.Height);

        var final = placement.Concat(matrix).ToFsMatrix();
        PdfiumNative.FPDFPageObj_SetMatrix(imageObject, ref final);
        PdfiumNative.FPDFPage_InsertObject(page, imageObject);
    }

    private static void CopyRows(byte[] pixels, IntPtr bitmap, int width, int height)
    {
        var stride = PdfiumNative.FPDFBitmap_GetStride(bitmap);
        var buffer = PdfiumNative.FPDFBitmap_GetBuffer(bitmap);
        var rowBytes = width * 4;

        for (var row = 0; row < height; row++)
        {
            Marshal.Copy(pixels, row * rowBytes, buffer + (row * stride), rowBytes);
        }
    }

    // ═══════════ Text ═══════════

    private static void WriteText(IntPtr page, TextOverlay text, Matrix2D matrix, OverlayFontProvider fonts)
    {
        if (string.IsNullOrWhiteSpace(text.Text))
        {
            return;
        }

        var fontSize = (float)Math.Clamp(text.FontSize, 1, 1638);
        var available = Math.Max(1, text.Width - (TextPadding * 2));
        var lineHeight = text.FontSize * LineHeightFactor;

        var needsCjk = OverlayFontProvider.NeedsCjkCoverage(text.Text);
        var lines = WrapParagraphs(fonts, text.IsBold, text.IsItalic, needsCjk, fontSize, text.Text, available);
        var (r, g, b) = ParseColor(text.ColorHex);
        var glyphs = TextOrientation(matrix.Rotation);

        var originX = text.X + TextPadding;
        var firstBaseline = text.Y + TextPadding + (text.FontSize * BaselineFactor);

        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Length == 0)
            {
                continue;
            }

            var textObject = fonts.CreateTextObject(text.IsBold, text.IsItalic, needsCjk, fontSize);
            if (textObject == IntPtr.Zero)
            {
                return;
            }

            if (PdfiumNative.FPDFText_SetText(textObject, ToWideString(lines[index])) == 0)
            {
                PdfiumNative.FPDFPageObj_Destroy(textObject);
                continue;
            }

            PdfiumNative.FPDFPageObj_SetFillColor(textObject, r, g, b, 255);

            var baseline = matrix.Transform(originX, firstBaseline + (index * lineHeight));
            PdfiumNative.FPDFPageObj_Transform(
                textObject,
                glyphs.A,
                glyphs.B,
                glyphs.C,
                glyphs.D,
                baseline.X,
                baseline.Y);

            PdfiumNative.FPDFPage_InsertObject(page, textObject);
        }
    }

    private static List<string> WrapParagraphs(
        OverlayFontProvider fonts,
        bool isBold,
        bool isItalic,
        bool needsCjk,
        float fontSize,
        string value,
        double maxWidth)
    {
        var lines = new List<string>();

        foreach (var paragraph in value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            WrapParagraph(fonts, isBold, isItalic, needsCjk, fontSize, paragraph, maxWidth, lines);
        }

        return lines;
    }

    private static void WrapParagraph(
        OverlayFontProvider fonts,
        bool isBold,
        bool isItalic,
        bool needsCjk,
        float fontSize,
        string paragraph,
        double maxWidth,
        List<string> lines)
    {
        var words = paragraph.Split(' ');
        var current = string.Empty;

        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (current.Length > 0 && MeasureWidth(fonts, isBold, isItalic, needsCjk, fontSize, candidate) > maxWidth)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }

        lines.Add(current);
    }

    /// <summary>Measures with PDFium itself so wrapping matches the glyphs that get written.</summary>
    private static double MeasureWidth(
        OverlayFontProvider fonts,
        bool isBold,
        bool isItalic,
        bool needsCjk,
        float fontSize,
        string value)
    {
        var textObject = fonts.CreateTextObject(isBold, isItalic, needsCjk, fontSize);
        if (textObject == IntPtr.Zero)
        {
            return EstimateWidth(fontSize, value);
        }

        try
        {
            if (PdfiumNative.FPDFText_SetText(textObject, ToWideString(value)) == 0)
            {
                return EstimateWidth(fontSize, value);
            }

            if (PdfiumNative.FPDFPageObj_GetBounds(textObject, out var left, out _, out var right, out _) == 0)
            {
                return EstimateWidth(fontSize, value);
            }

            var width = right - left;
            return width > 0 ? width : EstimateWidth(fontSize, value);
        }
        finally
        {
            PdfiumNative.FPDFPageObj_Destroy(textObject);
        }
    }

    private static double EstimateWidth(float fontSize, string value) => value.Length * fontSize * 0.5;

    private static ushort[] ToWideString(string value)
    {
        var buffer = new ushort[value.Length + 1];
        for (var index = 0; index < value.Length; index++)
        {
            buffer[index] = value[index];
        }

        buffer[^1] = 0;
        return buffer;
    }

    /// <summary>Glyph orientation that reads upright once <c>/Rotate</c> is applied for display.</summary>
    private static (double A, double B, double C, double D) TextOrientation(int rotation) => rotation switch
    {
        1 => (0, 1, -1, 0),
        2 => (-1, 0, 0, -1),
        3 => (0, -1, 1, 0),
        _ => (1, 0, 0, 1)
    };

    // ═══════════ Geometry ═══════════

    private static Matrix2D BuildDisplayToPdfMatrix(IntPtr page)
    {
        if (PdfiumNative.FPDFPage_GetCropBox(page, out var left, out var bottom, out var right, out var top) == 0 &&
            PdfiumNative.FPDFPage_GetMediaBox(page, out left, out bottom, out right, out top) == 0)
        {
            left = 0;
            bottom = 0;
            right = PdfiumNative.FPDF_GetPageWidthF(page);
            top = PdfiumNative.FPDF_GetPageHeightF(page);
        }

        if (right < left)
        {
            (left, right) = (right, left);
        }

        if (top < bottom)
        {
            (bottom, top) = (top, bottom);
        }

        var width = right - left;
        var height = top - bottom;
        var rotation = ((PdfiumNative.FPDFPage_GetRotation(page) % 4) + 4) % 4;

        return rotation switch
        {
            1 => new Matrix2D(0, 1, 1, 0, left, bottom, 1),
            2 => new Matrix2D(-1, 0, 0, 1, left + width, bottom, 2),
            3 => new Matrix2D(0, -1, -1, 0, left + width, bottom + height, 3),
            _ => new Matrix2D(1, 0, 0, -1, left, top, 0)
        };
    }

    private static (uint R, uint G, uint B) ParseColor(string colorHex)
    {
        if (!string.IsNullOrWhiteSpace(colorHex))
        {
            var hex = colorHex.Trim().TrimStart('#');
            if (hex.Length == 6 &&
                byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
                byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) &&
                byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                return (r, g, b);
            }
        }

        return (0, 0, 0);
    }

    /// <summary>An affine transform in PDF component order: x' = ax + cy + e, y' = bx + dy + f.</summary>
    private readonly record struct Matrix2D(
        double A,
        double B,
        double C,
        double D,
        double E,
        double F,
        int Rotation = 0)
    {
        public (double X, double Y) Transform(double x, double y) =>
            ((A * x) + (C * y) + E, (B * x) + (D * y) + F);

        /// <summary>Returns this transform followed by <paramref name="other"/>.</summary>
        public Matrix2D Concat(Matrix2D other) =>
            new(
                (A * other.A) + (B * other.C),
                (A * other.B) + (B * other.D),
                (C * other.A) + (D * other.C),
                (C * other.B) + (D * other.D),
                (E * other.A) + (F * other.C) + other.E,
                (E * other.B) + (F * other.D) + other.F,
                other.Rotation);

        public FsMatrix ToFsMatrix() => new()
        {
            a = (float)A,
            b = (float)B,
            c = (float)C,
            d = (float)D,
            e = (float)E,
            f = (float)F
        };
    }
}
