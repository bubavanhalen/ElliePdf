using System.Text;
using System.Text.Json;
using ElliePdf.Models;

namespace ElliePdf.Services;

/// <summary>
/// Reads ElliePdf's own annotations back out of a document so they can be edited again, and detaches
/// them from the page so the overlay is the single source of truth while editing.
/// </summary>
/// <remarks>
/// This is what replaces the old companion file. Annotations written by
/// <see cref="PdfOverlayWriter"/> carry their originating overlay item under a private key, so a
/// reopened document restores exactly what was drawn, including pen pressure. Annotations from
/// other tools are left strictly alone.
/// </remarks>
internal static class PdfAnnotationReader
{
    /// <summary>
    /// Removes ElliePdf annotations from every page and returns them as an overlay document.
    /// </summary>
    /// <remarks>
    /// Detaching matters: if the annotations stayed on the page they would render underneath the
    /// editable overlay, so every stroke would appear twice. The document is only modified in
    /// memory — nothing is written back unless the user saves.
    /// </remarks>
    public static PageOverlayDocument ExtractOwnAnnotations(IntPtr document, int pageCount)
    {
        var overlays = new PageOverlayDocument();

        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var page = PdfiumNative.FPDF_LoadPage(document, pageIndex);
            if (page == IntPtr.Zero)
            {
                continue;
            }

            try
            {
                var state = ExtractFromPage(page);
                if (PdfOverlayWriter.HasContent(state))
                {
                    overlays.Pages[pageIndex] = state;
                }
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }

        return overlays;
    }

    private static PageOverlayState ExtractFromPage(IntPtr page)
    {
        var state = new PageOverlayState();

        // Walk backwards: removing an annotation shifts every later index down.
        for (var index = PdfiumNative.FPDFPage_GetAnnotCount(page) - 1; index >= 0; index--)
        {
            var payload = TryReadPayload(page, index);
            if (payload is null)
            {
                continue;
            }

            Apply(state, payload);
            PdfiumNative.FPDFPage_RemoveAnnot(page, index);
        }

        // The backwards walk collected them in reverse, so restore the original z-order.
        state.InkStrokes.Reverse();
        state.Shapes.Reverse();
        state.TextItems.Reverse();
        state.Signatures.Reverse();
        return state;
    }

    private static void Apply(PageOverlayState state, AnnotationPayload payload)
    {
        if (payload.Ink is { } ink)
        {
            state.InkStrokes.Add(ink);
        }
        else if (payload.Shape is { } shape)
        {
            state.Shapes.Add(shape);
        }
        else if (payload.Text is { } text)
        {
            state.TextItems.Add(text);
        }
        else if (payload.Signature is { } signature)
        {
            state.Signatures.Add(signature);
        }
    }

    private static AnnotationPayload? TryReadPayload(IntPtr page, int index)
    {
        var annotation = PdfiumNative.FPDFPage_GetAnnot(page, index);
        if (annotation == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!PdfiumNative.FPDFAnnot_HasKey(annotation, PdfOverlayWriter.PayloadKey))
            {
                return null;
            }

            var length = PdfiumNative.FPDFAnnot_GetStringValue(
                annotation,
                PdfOverlayWriter.PayloadKey,
                null,
                0);

            // A UTF-16 string with its terminator is at least four bytes.
            if (length < 4)
            {
                return null;
            }

            var buffer = new byte[length];
            var written = PdfiumNative.FPDFAnnot_GetStringValue(
                annotation,
                PdfOverlayWriter.PayloadKey,
                buffer,
                length);

            if (written == 0)
            {
                return null;
            }

            // PDFium returns UTF-16LE including a trailing null.
            var json = Encoding.Unicode.GetString(buffer, 0, (int)written).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize(json, ElliePdfJsonContext.Default.AnnotationPayload);
        }
        catch (JsonException)
        {
            // A malformed payload just means we leave that annotation alone.
            return null;
        }
        finally
        {
            PdfiumNative.FPDFPage_CloseAnnot(annotation);
        }
    }
}
