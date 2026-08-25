using System.Runtime.InteropServices;
using System.Text.Json;
using ElliePdf.Helpers;
using ElliePdf.Models;

namespace ElliePdf.Services;

/// <summary>
/// Writes overlay annotations into a PDF as standard annotation objects: ink strokes become
/// <c>/Ink</c> annotations and everything else a <c>/Stamp</c>, each carrying an appearance stream
/// built from real vector paths, text objects and images.
/// </summary>
/// <remarks>
/// <para>
/// Annotations rather than page content means a shared PDF is self-contained and still editable:
/// any other viewer can display, move or delete them, and nothing lives in a companion file.
/// Each annotation also carries the originating overlay item under a private key so ElliePdf can
/// reload it exactly, including detail such as pen pressure that the appearance stream alone does
/// not preserve.
/// </para>
/// <para>
/// Overlay coordinates are stored in "display page points": origin top-left, Y growing downwards,
/// sized to the rotation-aware page box the reader shows. PDF user space is origin bottom-left with
/// Y growing upwards, relative to the crop box, and ignores <c>/Rotate</c>. Everything here funnels
/// through <see cref="BuildDisplayToPdfMatrix"/> so rotated and offset pages land correctly.
/// </para>
/// </remarks>
internal static class PdfOverlayWriter
{
    /// <summary>Private annotation key holding the ElliePdf overlay item as JSON.</summary>
    internal const string PayloadKey = "ElliePdfItem";

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
    /// Adds every page overlay to <paramref name="document"/> as annotations. Page content is left
    /// untouched, so the outline, links, form fields and text layer are unaffected.
    /// </summary>
    /// <remarks>
    /// It is deliberately not cancellable: abandoning half-way would leave some pages annotated and
    /// others not, with the overlays still pending in memory.
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
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>Adds <paramref name="overlay"/> to an already-loaded page as annotations.</summary>
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

    // ═══════════ Annotation plumbing ═══════════

    /// <summary>
    /// Creates an annotation, gives it an appearance stream built from <paramref name="objects"/>
    /// and records the overlay item that produced it.
    /// </summary>
    /// <remarks>
    /// The rectangle is measured from the geometry actually emitted rather than from the overlay's
    /// control points, because decorations such as arrowheads extend well beyond them. It must also
    /// be set before appending: PDFium fixes the appearance stream's bounding box from it, and
    /// anything outside would be clipped away in every other viewer. (Measuring the page objects
    /// instead is not an option — their bounds read as zero until they belong to a page or form.)
    /// </remarks>
    private static void EmitAnnotation(
        IntPtr page,
        int subtype,
        IReadOnlyList<(double X, double Y)> extent,
        double strokeWidth,
        Matrix2D matrix,
        AnnotationPayload payload,
        IReadOnlyList<IntPtr> objects)
    {
        if (objects.Count == 0 || extent.Count == 0)
        {
            DestroyAll(objects);
            return;
        }

        var annotation = PdfiumNative.FPDFPage_CreateAnnot(page, subtype);
        if (annotation == IntPtr.Zero)
        {
            DestroyAll(objects);
            return;
        }

        try
        {
            var rect = MeasureBounds(extent, strokeWidth, matrix);
            PdfiumNative.FPDFAnnot_SetRect(annotation, ref rect);

            // Without the print flag the annotation shows on screen but vanishes on paper.
            PdfiumNative.FPDFAnnot_SetFlags(annotation, PdfiumNative.AnnotFlagPrint);

            foreach (var pageObject in objects)
            {
                if (!PdfiumNative.FPDFAnnot_AppendObject(annotation, pageObject))
                {
                    PdfiumNative.FPDFPageObj_Destroy(pageObject);
                }
            }

            var json = JsonSerializer.Serialize(payload, ElliePdfJsonContext.Default.AnnotationPayload);
            PdfiumNative.FPDFAnnot_SetStringValue(annotation, PayloadKey, ToWideString(json));

            // /Contents is the standard place for an annotation's text, and is what viewers show in
            // a comment list and include when searching annotations.
            if (payload.Text?.Text is { Length: > 0 } contents)
            {
                PdfiumNative.FPDFAnnot_SetStringValue(annotation, "Contents", ToWideString(contents));
            }
        }
        finally
        {
            PdfiumNative.FPDFPage_CloseAnnot(annotation);
        }
    }

    /// <summary>
    /// Bounding rectangle in PDF space of the given display-space points, grown by half the stroke
    /// width because a stroke lays ink either side of the path it follows.
    /// </summary>
    private static FsRectF MeasureBounds(
        IReadOnlyList<(double X, double Y)> extent,
        double strokeWidth,
        Matrix2D matrix)
    {
        var transformed = extent.Select(point => matrix.Transform(point.X, point.Y)).ToList();

        // A little slack on top of the stroke keeps antialiased edges and joins inside the box.
        var padding = (Math.Max(0, strokeWidth) / 2) + 2;

        return new FsRectF
        {
            left = (float)(transformed.Min(point => point.X) - padding),
            bottom = (float)(transformed.Min(point => point.Y) - padding),
            right = (float)(transformed.Max(point => point.X) + padding),
            top = (float)(transformed.Max(point => point.Y) + padding)
        };
    }

    private static void DestroyAll(IReadOnlyList<IntPtr> objects)
    {
        foreach (var pageObject in objects)
        {
            PdfiumNative.FPDFPageObj_Destroy(pageObject);
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

        var color = ParseColor(stroke.ColorHex);
        var objects = new List<IntPtr>();
        List<(double X, double Y)> extent;
        double strokeWidth;

        if (InkGeometry.HasUniformPressure(stroke.Points))
        {
            var width = InkGeometry.WidthAt(stroke.Thickness, stroke.Points[0].Pressure);
            var path = BuildPolyline(stroke.Points.Select(point => (point.X, point.Y)), matrix);

            if (path == IntPtr.Zero)
            {
                return;
            }

            PdfiumNative.FPDFPageObj_SetStrokeColor(path, color.R, color.G, color.B, 255);
            // The display->PDF matrix is a rotation plus a flip, so widths carry over unscaled.
            PdfiumNative.FPDFPageObj_SetStrokeWidth(path, (float)Math.Max(0.1, width));
            PdfiumNative.FPDFPageObj_SetLineCap(path, 1);
            PdfiumNative.FPDFPageObj_SetLineJoin(path, 1);
            PdfiumNative.FPDFPath_SetDrawMode(path, 0, 1);

            objects.Add(path);
            extent = stroke.Points.Select(point => (point.X, point.Y)).ToList();
            strokeWidth = width;
        }
        else
        {
            var outline = InkGeometry.BuildOutline(stroke.Points, stroke.Thickness);
            if (outline.Count < 3)
            {
                return;
            }

            var path = BuildFilledPolygon(outline.Select(vertex => (vertex.X, vertex.Y)), color, matrix);
            if (path == IntPtr.Zero)
            {
                return;
            }

            objects.Add(path);

            // The outline already spans the full width, so it needs no stroke allowance.
            extent = outline.Select(vertex => (vertex.X, vertex.Y)).ToList();
            strokeWidth = 0;
        }

        EmitAnnotation(
            page,
            PdfiumNative.AnnotInk,
            extent,
            strokeWidth,
            matrix,
            new AnnotationPayload { Ink = stroke },
            objects);
    }

    private static IntPtr BuildPolyline(IEnumerable<(double X, double Y)> points, Matrix2D matrix)
    {
        var ordered = points.ToList();
        if (ordered.Count < 2)
        {
            return IntPtr.Zero;
        }

        var start = matrix.Transform(ordered[0].X, ordered[0].Y);
        var path = PdfiumNative.FPDFPageObj_CreateNewPath((float)start.X, (float)start.Y);
        if (path == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        for (var index = 1; index < ordered.Count; index++)
        {
            var point = matrix.Transform(ordered[index].X, ordered[index].Y);
            PdfiumNative.FPDFPath_LineTo(path, (float)point.X, (float)point.Y);
        }

        return path;
    }

    /// <summary>Builds a closed, filled polygon in display coordinates.</summary>
    private static IntPtr BuildFilledPolygon(
        IEnumerable<(double X, double Y)> vertices,
        (uint R, uint G, uint B) color,
        Matrix2D matrix)
    {
        var path = BuildPolyline(vertices, matrix);
        if (path == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        PdfiumNative.FPDFPath_Close(path);
        PdfiumNative.FPDFPageObj_SetFillColor(path, color.R, color.G, color.B, 255);
        PdfiumNative.FPDFPath_SetDrawMode(path, FillModeWinding, 0);
        return path;
    }

    // ═══════════ Shapes ═══════════

    private static void WriteShape(IntPtr page, ShapeOverlay shape, Matrix2D matrix)
    {
        var stroke = ParseColor(shape.ColorHex);
        var fill = shape.FillColorHex is null ? ((uint R, uint G, uint B)?)null : ParseColor(shape.FillColorHex);
        var objects = new List<IntPtr>();
        var extent = new List<(double X, double Y)>();

        switch (shape.Kind)
        {
            case ShapeKind.Rectangle:
            {
                var path = BuildPolyline(
                    ShapeGeometry.RectangleCorners(shape).Select(corner => (corner.X, corner.Y)),
                    matrix);

                if (path == IntPtr.Zero)
                {
                    return;
                }

                PdfiumNative.FPDFPath_Close(path);
                StyleShapePath(path, shape, stroke, fill);
                objects.Add(path);
                extent.AddRange(ShapeGeometry.RectangleCorners(shape).Select(corner => (corner.X, corner.Y)));
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
                StyleShapePath(path, shape, stroke, fill);
                objects.Add(path);

                // The control polygon of a cubic always contains the curve it describes.
                extent.Add((origin.X, origin.Y));
                foreach (var segment in segments)
                {
                    extent.Add((segment.Control1.X, segment.Control1.Y));
                    extent.Add((segment.Control2.X, segment.Control2.Y));
                    extent.Add((segment.End.X, segment.End.Y));
                }

                break;
            }

            case ShapeKind.Line:
            {
                var path = BuildSegment(shape, shape.Start.X, shape.Start.Y, shape.End.X, shape.End.Y, stroke, matrix);
                if (path == IntPtr.Zero)
                {
                    return;
                }

                objects.Add(path);
                extent.Add((shape.Start.X, shape.Start.Y));
                extent.Add((shape.End.X, shape.End.Y));
                break;
            }

            default:
            {
                var shaftEnd = ShapeGeometry.ArrowShaftEnd(shape);
                var shaft = BuildSegment(shape, shape.Start.X, shape.Start.Y, shaftEnd.X, shaftEnd.Y, stroke, matrix);
                if (shaft == IntPtr.Zero)
                {
                    return;
                }

                objects.Add(shaft);
                extent.Add((shape.Start.X, shape.Start.Y));
                extent.Add((shape.End.X, shape.End.Y));

                if (ShapeGeometry.ArrowHead(shape) is { } head)
                {
                    var headPath = BuildFilledPolygon(head.Select(vertex => (vertex.X, vertex.Y)), stroke, matrix);
                    if (headPath != IntPtr.Zero)
                    {
                        objects.Add(headPath);
                    }

                    // The barbs reach well outside the shaft, especially on a straight arrow.
                    extent.AddRange(head.Select(vertex => (vertex.X, vertex.Y)));
                }

                break;
            }
        }

        EmitAnnotation(
            page,
            PdfiumNative.AnnotStamp,
            extent,
            shape.Thickness,
            matrix,
            new AnnotationPayload { Shape = shape },
            objects);
    }

    private static IntPtr BuildSegment(
        ShapeOverlay shape,
        double x1,
        double y1,
        double x2,
        double y2,
        (uint R, uint G, uint B) color,
        Matrix2D matrix)
    {
        var path = BuildPolyline([(x1, y1), (x2, y2)], matrix);
        if (path == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        PdfiumNative.FPDFPageObj_SetStrokeColor(path, color.R, color.G, color.B, 255);
        PdfiumNative.FPDFPageObj_SetStrokeWidth(path, (float)Math.Max(0.1, shape.Thickness));
        PdfiumNative.FPDFPageObj_SetLineCap(path, 1);
        PdfiumNative.FPDFPageObj_SetLineJoin(path, 1);
        PdfiumNative.FPDFPath_SetDrawMode(path, 0, 1);
        return path;
    }

    private static void StyleShapePath(
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

        EmitAnnotation(
            page,
            PdfiumNative.AnnotStamp,
            [
                (signature.X, signature.Y),
                (signature.X + signature.Width, signature.Y + signature.Height)
            ],
            strokeWidth: 0,
            matrix,
            new AnnotationPayload { Signature = signature },
            [imageObject]);
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
        var objects = new List<IntPtr>();

        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Length == 0)
            {
                continue;
            }

            var textObject = fonts.CreateTextObject(text.IsBold, text.IsItalic, needsCjk, fontSize);
            if (textObject == IntPtr.Zero)
            {
                break;
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

            objects.Add(textObject);
        }

        // Wrapped text can run past the box it was typed into, and a single unbreakable word can
        // run past it sideways, so size the annotation to the lines actually laid out.
        var contentHeight = Math.Max(text.Height, (lines.Count * lineHeight) + (TextPadding * 2));
        var widest = lines.Count == 0
            ? available
            : lines.Max(line => line.Length == 0
                ? 0
                : MeasureWidth(fonts, text.IsBold, text.IsItalic, needsCjk, fontSize, line));

        EmitAnnotation(
            page,
            PdfiumNative.AnnotStamp,
            [
                (text.X, text.Y),
                (text.X + Math.Max(text.Width, widest + (TextPadding * 2)), text.Y + contentHeight)
            ],
            strokeWidth: 0,
            matrix,
            new AnnotationPayload { Text = text },
            objects);
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

    internal static ushort[] ToWideString(string value)
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

    internal static Matrix2D BuildDisplayToPdfMatrix(IntPtr page)
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
    internal readonly record struct Matrix2D(
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
