using System.IO;
using System.Runtime.InteropServices;
using ElliePdf.Helpers;
using ElliePdf.Models;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ElliePdf.Services;

public sealed class PdfService : IPdfService, IDisposable
{
    private readonly SemaphoreSlim _pdfiumGate = new(1, 1);
    private readonly PageRenderCache _renderCache = new();
    private readonly Lock _nativeStateLock = new();
    private bool _initialized;
    private IntPtr _nativeLibraryHandle;
    private string? _nativeDependencyIssue;
    private string? _resolvedPdfiumPath;
    private FileStream? _activeWriteStream;

    public bool HasConfiguredNativeDependency => TryGetNativeDependencyIssue() is null;

    public string? NativeDependencyIssue => TryGetNativeDependencyIssue();

    public Task<PdfDocumentSession> OpenDocumentAsync(
        string path,
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return ExecutePdfiumCallAsync(() =>
        {
            var document = PdfiumNative.FPDF_LoadDocument(path, password);
            if (document == IntPtr.Zero)
            {
                throw CreateOpenException(path, password);
            }

            var pageCount = PdfiumNative.FPDF_GetPageCount(document);
            if (pageCount < 0)
            {
                PdfiumNative.FPDF_CloseDocument(document);
                throw CreatePdfiumException($"Unable to read page count from '{Path.GetFileName(path)}'.");
            }

            var formFill = PdfFormFillContext.TryCreate(document);
            return new PdfDocumentSession(this, document, path, pageCount, formFill);
        }, cancellationToken);
    }

    public Task<RenderedPage> RenderPageAsync(
        PdfDocumentSession document,
        int pageIndex,
        double scale,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ObjectDisposedException.ThrowIf(document.IsClosed, document);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 0.1);

        if (_renderCache.TryGet(document, pageIndex, scale, out var cached) && cached is not null)
        {
            return Task.FromResult(cached);
        }

        return ExecutePdfiumCallAsync(async () =>
        {
            var page = PdfiumNative.FPDF_LoadPage(document.Handle, pageIndex);
            if (page == IntPtr.Zero)
            {
                throw CreatePdfiumException($"Unable to load page {pageIndex + 1} from '{Path.GetFileName(document.SourcePath)}'.");
            }

            IntPtr bitmap = IntPtr.Zero;

            try
            {
                var pageWidth = Math.Max(1f, PdfiumNative.FPDF_GetPageWidthF(page));
                var pageHeight = Math.Max(1f, PdfiumNative.FPDF_GetPageHeightF(page));
                var renderWidth = Math.Max(1, (int)Math.Ceiling(pageWidth * scale));
                var renderHeight = Math.Max(1, (int)Math.Ceiling(pageHeight * scale));

                bitmap = PdfiumNative.FPDFBitmap_Create(renderWidth, renderHeight, 1);
                if (bitmap == IntPtr.Zero)
                {
                    throw new InvalidOperationException("PDFium failed to allocate a render bitmap.");
                }

                PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, renderWidth, renderHeight, PdfiumNative.WhiteArgb);
                RenderPageBitmap(document.FormFill?.FormHandle ?? IntPtr.Zero, page, bitmap, renderWidth, renderHeight);

                var pngBytes = await EncodeBitmapToPngAsync(bitmap, renderWidth, renderHeight, cancellationToken);
                var rendered = new RenderedPage(pngBytes, renderWidth, renderHeight, pageWidth, pageHeight);
                _renderCache.Set(document, pageIndex, scale, rendered);
                return rendered;
            }
            finally
            {
                if (bitmap != IntPtr.Zero)
                {
                    PdfiumNative.FPDFBitmap_Destroy(bitmap);
                }

                PdfiumNative.FPDF_ClosePage(page);
            }
        }, cancellationToken);
    }

    public Task<byte[]> RenderPageThumbnailAsync(
        PdfDocumentSession document,
        int pageIndex,
        int maxWidth,
        int maxHeight,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ObjectDisposedException.ThrowIf(document.IsClosed, document);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);

        return ExecutePdfiumCallAsync(async () =>
        {
            var page = PdfiumNative.FPDF_LoadPage(document.Handle, pageIndex);
            if (page == IntPtr.Zero)
            {
                throw CreatePdfiumException($"Unable to load page {pageIndex + 1} from '{Path.GetFileName(document.SourcePath)}'.");
            }

            IntPtr bitmap = IntPtr.Zero;

            try
            {
                var pageWidth = Math.Max(1, PdfiumNative.FPDF_GetPageWidthF(page));
                var pageHeight = Math.Max(1, PdfiumNative.FPDF_GetPageHeightF(page));
                var scale = Math.Min(maxWidth / pageWidth, maxHeight / pageHeight);

                var renderWidth = Math.Max(1, (int)Math.Ceiling(pageWidth * scale));
                var renderHeight = Math.Max(1, (int)Math.Ceiling(pageHeight * scale));

                bitmap = PdfiumNative.FPDFBitmap_Create(renderWidth, renderHeight, 1);
                if (bitmap == IntPtr.Zero)
                {
                    throw new InvalidOperationException("PDFium failed to allocate a render bitmap.");
                }

                PdfiumNative.FPDFBitmap_FillRect(bitmap, 0, 0, renderWidth, renderHeight, PdfiumNative.WhiteArgb);
                RenderPageBitmap(document.FormFill?.FormHandle ?? IntPtr.Zero, page, bitmap, renderWidth, renderHeight);

                return await EncodeBitmapToPngAsync(bitmap, renderWidth, renderHeight, cancellationToken);
            }
            finally
            {
                if (bitmap != IntPtr.Zero)
                {
                    PdfiumNative.FPDFBitmap_Destroy(bitmap);
                }

                PdfiumNative.FPDF_ClosePage(page);
            }
        }, cancellationToken);
    }

    public Task<IReadOnlyList<TextMatch>> SearchTextAsync(
        PdfDocumentSession document,
        string query,
        bool matchCase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ObjectDisposedException.ThrowIf(document.IsClosed, document);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        return ExecutePdfiumCallAsync(() =>
        {
            var matches = new List<TextMatch>();
            var searchBytes = System.Text.Encoding.Unicode.GetBytes(query + '\0');
            var flags = matchCase ? PdfiumNative.MatchCase : 0u;

            for (var pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = PdfiumNative.FPDF_LoadPage(document.Handle, pageIndex);
                if (page == IntPtr.Zero)
                {
                    continue;
                }

                var textPage = IntPtr.Zero;
                var findHandle = IntPtr.Zero;

                try
                {
                    textPage = PdfiumNative.FPDFText_LoadPage(page);
                    if (textPage == IntPtr.Zero)
                    {
                        continue;
                    }

                    findHandle = PdfiumNative.FPDFText_FindStart(textPage, searchBytes, flags, 0);
                    if (findHandle == IntPtr.Zero)
                    {
                        continue;
                    }

                    do
                    {
                        var charIndex = PdfiumNative.FPDFText_GetSchResultIndex(findHandle);
                        var matchLength = PdfiumNative.FPDFText_GetSchCount(findHandle);
                        var context = ExtractTextContext(textPage, charIndex, matchLength);
                        var highlightRects = ExtractMatchHighlightRects(textPage, charIndex, matchLength);
                        matches.Add(new TextMatch(pageIndex, charIndex, matchLength, context, highlightRects));
                    }
                    while (PdfiumNative.FPDFText_FindNext(findHandle) != 0);
                }
                finally
                {
                    if (findHandle != IntPtr.Zero)
                    {
                        PdfiumNative.FPDFText_FindClose(findHandle);
                    }

                    if (textPage != IntPtr.Zero)
                    {
                        PdfiumNative.FPDFText_ClosePage(textPage);
                    }

                    PdfiumNative.FPDF_ClosePage(page);
                }
            }

            return (IReadOnlyList<TextMatch>)matches;
        }, cancellationToken);
    }

    public Task<(float Width, float Height)> GetPageSizeAsync(
        PdfDocumentSession document,
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ObjectDisposedException.ThrowIf(document.IsClosed, document);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);

        return ExecutePdfiumCallAsync(() =>
        {
            var page = PdfiumNative.FPDF_LoadPage(document.Handle, pageIndex);
            if (page == IntPtr.Zero)
            {
                throw CreatePdfiumException($"Unable to load page {pageIndex + 1}.");
            }

            try
            {
                return (
                    Math.Max(1f, PdfiumNative.FPDF_GetPageWidthF(page)),
                    Math.Max(1f, PdfiumNative.FPDF_GetPageHeightF(page)));
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }, cancellationToken);
    }

    public Task<IReadOnlyList<PdfOutlineItem>> GetOutlineAsync(
        PdfDocumentSession document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ObjectDisposedException.ThrowIf(document.IsClosed, document);

        return ExecutePdfiumCallAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = PdfiumNative.FPDFBookmark_GetFirstChild(document.Handle, IntPtr.Zero);
            return (IReadOnlyList<PdfOutlineItem>)ReadOutlineChildren(document.Handle, root, cancellationToken);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<PdfFormField>> GetFormFieldsAsync(
        PdfDocumentSession document,
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ObjectDisposedException.ThrowIf(document.IsClosed, document);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);

        return ExecutePdfiumCallAsync(() =>
        {
            if (pageIndex >= document.PageCount || document.FormFill is null)
            {
                return (IReadOnlyList<PdfFormField>)[];
            }

            var page = PdfiumNative.FPDF_LoadPage(document.Handle, pageIndex);
            if (page == IntPtr.Zero)
            {
                throw CreatePdfiumException($"Unable to load page {pageIndex + 1} while reading form fields.");
            }

            var fields = new List<PdfFormField>();
            var formHandle = document.FormFill.FormHandle;
            PdfiumNative.FORM_OnAfterLoadPage(page, formHandle);

            try
            {
                var annotationCount = Math.Max(0, PdfiumNative.FPDFPage_GetAnnotCount(page));
                for (var annotationIndex = 0; annotationIndex < annotationCount; annotationIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var annotation = PdfiumNative.FPDFPage_GetAnnot(page, annotationIndex);
                    if (annotation == IntPtr.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        if (PdfiumNative.FPDFAnnot_GetSubtype(annotation) != PdfiumNative.AnnotationWidget ||
                            PdfiumNative.FPDFAnnot_GetRect(annotation, out var rect) == 0)
                        {
                            continue;
                        }

                        var fieldType = (PdfFormFieldType)PdfiumNative.FPDFAnnot_GetFormFieldType(formHandle, annotation);
                        fields.Add(new PdfFormField(
                            pageIndex,
                            annotationIndex,
                            fieldType,
                            ReadFormFieldString((buffer, length) =>
                                PdfiumNative.FPDFAnnot_GetFormFieldName(formHandle, annotation, buffer, length)),
                            ReadFormFieldString((buffer, length) =>
                                PdfiumNative.FPDFAnnot_GetFormFieldAlternateName(formHandle, annotation, buffer, length)),
                            ReadFormFieldString((buffer, length) =>
                                PdfiumNative.FPDFAnnot_GetFormFieldValue(formHandle, annotation, buffer, length)),
                            new PdfRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                            fieldType == PdfFormFieldType.Signature &&
                            PdfiumNative.FPDFAnnot_HasKey(annotation, "V") != 0));
                    }
                    finally
                    {
                        PdfiumNative.FPDFPage_CloseAnnot(annotation);
                    }
                }
            }
            finally
            {
                PdfiumNative.FORM_OnBeforeClosePage(page, formHandle);
                PdfiumNative.FPDF_ClosePage(page);
            }

            return (IReadOnlyList<PdfFormField>)fields;
        }, cancellationToken);
    }

    public Task RotatePageAsync(
        PdfDocumentSession document,
        int pageIndex,
        int quarterTurnsClockwise,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ObjectDisposedException.ThrowIf(document.IsClosed, document);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);

        return ExecutePdfiumCallAsync(() =>
        {
            var page = PdfiumNative.FPDF_LoadPage(document.Handle, pageIndex);
            if (page == IntPtr.Zero)
            {
                throw CreatePdfiumException($"Unable to load page {pageIndex + 1} for rotation.");
            }

            try
            {
                var currentRotation = PdfiumNative.FPDFPage_GetRotation(page);
                var nextRotation = ((currentRotation + quarterTurnsClockwise) % 4 + 4) % 4;
                PdfiumNative.FPDFPage_SetRotation(page, nextRotation);
                PdfiumNative.FPDFPage_GenerateContent(page);
            }
            finally
            {
                PdfiumNative.FPDF_ClosePage(page);
            }
        }, cancellationToken);
    }

    public Task DeletePageAsync(PdfDocumentSession document, int pageIndex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ObjectDisposedException.ThrowIf(document.IsClosed, document);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);

        return ExecutePdfiumCallAsync(() =>
        {
            if (pageIndex >= document.PageCount)
            {
                throw new ArgumentOutOfRangeException(nameof(pageIndex), "The requested page index is outside the document.");
            }

            PdfiumNative.FPDFPage_Delete(document.Handle, pageIndex);
            document.PageCount -= 1;
        }, cancellationToken);
    }

    public Task MergeDocumentsAsync(
        IReadOnlyList<PdfDocumentSession> sourceDocuments,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceDocuments);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return ExecutePdfiumCallAsync(() =>
        {
            if (sourceDocuments.Count < 2)
            {
                throw new InvalidOperationException("Select at least two source documents before merging.");
            }

            var destination = PdfiumNative.FPDF_CreateNewDocument();
            if (destination == IntPtr.Zero)
            {
                throw CreatePdfiumException("PDFium could not allocate a destination document for the merge.");
            }

            try
            {
                var destinationPageIndex = 0;
                PdfiumNative.FPDF_CopyViewerPreferences(destination, sourceDocuments[0].Handle);

                foreach (var sourceDocument in sourceDocuments)
                {
                    ObjectDisposedException.ThrowIf(sourceDocument.IsClosed, sourceDocument);

                    var pageIndices = Enumerable.Range(0, sourceDocument.PageCount).ToArray();
                    var imported = PdfiumNative.FPDF_ImportPagesByIndex(
                        destination,
                        sourceDocument.Handle,
                        pageIndices,
                        (uint)pageIndices.Length,
                        destinationPageIndex);

                    if (imported == 0)
                    {
                        throw CreatePdfiumException($"PDFium could not merge '{Path.GetFileName(sourceDocument.SourcePath)}'.");
                    }

                    destinationPageIndex += sourceDocument.PageCount;
                }

                SaveDocumentCore(destination, outputPath);
            }
            finally
            {
                PdfiumNative.FPDF_CloseDocument(destination);
            }
        }, cancellationToken);
    }

    public Task MergeOrderedPagesAsync(
        IReadOnlyList<(PdfDocumentSession Document, int PageIndex)> pagesInOrder,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pagesInOrder);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return ExecutePdfiumCallAsync(() =>
        {
            if (pagesInOrder.Count < 1)
            {
                throw new InvalidOperationException("Add at least one page before exporting.");
            }

            var destination = PdfiumNative.FPDF_CreateNewDocument();
            if (destination == IntPtr.Zero)
            {
                throw CreatePdfiumException("PDFium could not allocate a destination document for the merge.");
            }

            try
            {
                PdfiumNative.FPDF_CopyViewerPreferences(destination, pagesInOrder[0].Document.Handle);

                for (var destinationIndex = 0; destinationIndex < pagesInOrder.Count; destinationIndex++)
                {
                    var (sourceDocument, pageIndex) = pagesInOrder[destinationIndex];
                    ObjectDisposedException.ThrowIf(sourceDocument.IsClosed, sourceDocument);

                    if (pageIndex < 0 || pageIndex >= sourceDocument.PageCount)
                    {
                        throw new ArgumentOutOfRangeException(nameof(pagesInOrder), $"Page index {pageIndex} is outside the source document.");
                    }

                    var pageIndices = new[] { pageIndex };
                    var imported = PdfiumNative.FPDF_ImportPagesByIndex(
                        destination,
                        sourceDocument.Handle,
                        pageIndices,
                        1,
                        destinationIndex);

                    if (imported == 0)
                    {
                        throw CreatePdfiumException($"PDFium could not import page {pageIndex + 1} from '{Path.GetFileName(sourceDocument.SourcePath)}'.");
                    }
                }

                SaveDocumentCore(destination, outputPath);
            }
            finally
            {
                PdfiumNative.FPDF_CloseDocument(destination);
            }
        }, cancellationToken);
    }

    public Task SaveDocumentAsync(
        PdfDocumentSession document,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ObjectDisposedException.ThrowIf(document.IsClosed, document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return ExecutePdfiumCallAsync(() => SaveDocumentCore(document.Handle, outputPath), cancellationToken);
    }

    public Task SaveDocumentWithOverlaysAsync(
        PdfDocumentSession document,
        PageOverlayDocument? overlays,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ObjectDisposedException.ThrowIf(document.IsClosed, document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var hasOverlays = overlays?.Pages.Values.Any(OverlayCompositor.HasContent) == true;
        if (!hasOverlays)
        {
            return SaveDocumentAsync(document, outputPath, cancellationToken);
        }

        return ExecutePdfiumCallAsync(() =>
        {
            var destination = PdfiumNative.FPDF_CreateNewDocument();
            if (destination == IntPtr.Zero)
            {
                throw CreatePdfiumException("PDFium could not allocate a destination document for saving.");
            }

            try
            {
                PdfiumNative.FPDF_CopyViewerPreferences(destination, document.Handle);
                for (var pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var indices = new[] { pageIndex };
                    var imported = PdfiumNative.FPDF_ImportPagesByIndex(
                        destination,
                        document.Handle,
                        indices,
                        1,
                        pageIndex);

                    if (imported == 0)
                    {
                        throw CreatePdfiumException($"PDFium could not import page {pageIndex + 1}.");
                    }

                    overlays!.Pages.TryGetValue(pageIndex, out var pageOverlay);
                    if (OverlayCompositor.HasContent(pageOverlay))
                    {
                        ApplyPageOverlay(destination, pageIndex, pageOverlay!);
                    }
                }

                SaveDocumentCore(destination, outputPath);
            }
            finally
            {
                PdfiumNative.FPDF_CloseDocument(destination);
            }
        }, cancellationToken);
    }

    public Task CloseDocumentAsync(PdfDocumentSession document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        return ExecutePdfiumCallAsync(() =>
        {
            if (document.IsClosed)
            {
                return;
            }

            document.CloseFormFill();
            PdfiumNative.FPDF_CloseDocument(document.Handle);
            document.MarkClosed();
        }, cancellationToken);
    }

    public void Dispose()
    {
        lock (_nativeStateLock)
        {
            if (_initialized)
            {
                PdfiumNative.FPDF_DestroyLibrary();
                _initialized = false;
            }

            if (_nativeLibraryHandle != IntPtr.Zero)
            {
                NativeLibrary.Free(_nativeLibraryHandle);
                _nativeLibraryHandle = IntPtr.Zero;
            }
        }

        _pdfiumGate.Dispose();
        _activeWriteStream?.Dispose();
    }

    private static string ReadFormFieldString(Func<byte[]?, uint, uint> read)
    {
        var byteLength = read(null, 0);
        if (byteLength <= 2)
        {
            return string.Empty;
        }

        var buffer = new byte[byteLength];
        var written = read(buffer, byteLength);
        if (written <= 2)
        {
            return string.Empty;
        }

        return System.Text.Encoding.Unicode
            .GetString(buffer, 0, checked((int)Math.Min(written, byteLength)))
            .TrimEnd('\0');
    }

    private static void ApplyPageOverlay(IntPtr document, int pageIndex, PageOverlayState overlay)
    {
        var page = PdfiumNative.FPDF_LoadPage(document, pageIndex);
        if (page == IntPtr.Zero)
        {
            throw CreatePdfiumException($"Unable to load page {pageIndex + 1} while embedding edits.");
        }

        try
        {
            var pageHeight = Math.Max(1f, PdfiumNative.FPDF_GetPageHeightF(page));

            foreach (var stroke in overlay.InkStrokes)
            {
                AddInkStroke(page, pageHeight, stroke);
            }

            foreach (var text in overlay.TextItems)
            {
                AddTextOverlay(document, page, pageHeight, text);
            }

            foreach (var signature in overlay.Signatures)
            {
                AddSignatureOverlay(document, page, pageHeight, signature);
            }

            if (PdfiumNative.FPDFPage_GenerateContent(page) == 0)
            {
                throw CreatePdfiumException($"PDFium could not generate edited content for page {pageIndex + 1}.");
            }
        }
        finally
        {
            PdfiumNative.FPDF_ClosePage(page);
        }
    }

    private static void AddInkStroke(IntPtr page, float pageHeight, InkStrokeOverlay stroke)
    {
        if (stroke.Points.Count < 2)
        {
            return;
        }

        var first = stroke.Points[0];
        var path = PdfiumNative.FPDFPageObj_CreateNewPath((float)first.X, (float)(pageHeight - first.Y));
        if (path == IntPtr.Zero)
        {
            throw CreatePdfiumException("PDFium could not create an ink path.");
        }

        var inserted = false;
        try
        {
            for (var index = 1; index < stroke.Points.Count; index++)
            {
                var point = stroke.Points[index];
                if (PdfiumNative.FPDFPath_LineTo(path, (float)point.X, (float)(pageHeight - point.Y)) == 0)
                {
                    throw CreatePdfiumException("PDFium could not extend an ink path.");
                }
            }

            var (red, green, blue) = ParseRgb(stroke.ColorHex);
            if (PdfiumNative.FPDFPageObj_SetStrokeColor(path, red, green, blue, 255) == 0 ||
                PdfiumNative.FPDFPageObj_SetStrokeWidth(path, (float)Math.Max(0.5, stroke.Thickness)) == 0 ||
                PdfiumNative.FPDFPageObj_SetLineCap(path, 1) == 0 ||
                PdfiumNative.FPDFPageObj_SetLineJoin(path, 1) == 0 ||
                PdfiumNative.FPDFPath_SetDrawMode(path, 0, 1) == 0 ||
                PdfiumNative.FPDFPage_InsertObject(page, path) == 0)
            {
                throw CreatePdfiumException("PDFium could not add an ink path to the page.");
            }

            inserted = true;
        }
        finally
        {
            if (!inserted)
            {
                PdfiumNative.FPDFPageObj_Destroy(path);
            }
        }
    }

    private static void AddTextOverlay(IntPtr document, IntPtr page, float pageHeight, TextOverlay text)
    {
        if (string.IsNullOrEmpty(text.Text))
        {
            return;
        }

        var fontName = (text.IsBold, text.IsItalic) switch
        {
            (true, true) => "Helvetica-BoldOblique",
            (true, false) => "Helvetica-Bold",
            (false, true) => "Helvetica-Oblique",
            _ => "Helvetica"
        };
        var font = PdfiumNative.FPDFText_LoadStandardFont(document, fontName);
        if (font == IntPtr.Zero)
        {
            throw CreatePdfiumException($"PDFium could not load the standard font '{fontName}'.");
        }

        try
        {
            var fontSize = (float)Math.Clamp(text.FontSize, 4, 144);
            var lineHeight = fontSize * 1.2f;
            var maxLines = Math.Max(1, (int)Math.Floor(Math.Max(lineHeight, text.Height) / lineHeight));
            var lines = WrapText(text.Text, Math.Max(24, text.Width), fontSize).Take(maxLines);
            var (red, green, blue) = ParseRgb(text.ColorHex);
            var lineIndex = 0;

            foreach (var line in lines)
            {
                var textObject = PdfiumNative.FPDFPageObj_CreateTextObj(document, font, fontSize);
                if (textObject == IntPtr.Zero)
                {
                    throw CreatePdfiumException("PDFium could not create a text object.");
                }

                var inserted = false;
                try
                {
                    var encoded = System.Text.Encoding.Unicode.GetBytes(line + '\0');
                    var baseline = pageHeight - text.Y - fontSize - (lineIndex * lineHeight);
                    if (PdfiumNative.FPDFText_SetText(textObject, encoded) == 0 ||
                        PdfiumNative.FPDFPageObj_SetFillColor(textObject, red, green, blue, 255) == 0 ||
                        PdfiumNative.FPDFPageObj_SetMatrix(textObject, 1, 0, 0, 1, text.X, baseline) == 0 ||
                        PdfiumNative.FPDFPage_InsertObject(page, textObject) == 0)
                    {
                        throw CreatePdfiumException("PDFium could not add text to the page.");
                    }

                    inserted = true;
                }
                finally
                {
                    if (!inserted)
                    {
                        PdfiumNative.FPDFPageObj_Destroy(textObject);
                    }
                }

                lineIndex++;
            }
        }
        finally
        {
            PdfiumNative.FPDFFont_Close(font);
        }
    }

    private static IEnumerable<string> WrapText(string value, double width, float fontSize)
    {
        var maximumCharacters = Math.Max(1, (int)Math.Floor(width / Math.Max(1, fontSize * 0.52)));
        foreach (var paragraph in value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                yield return string.Empty;
                continue;
            }

            var remaining = paragraph;
            while (remaining.Length > maximumCharacters)
            {
                var splitAt = remaining.LastIndexOf(' ', maximumCharacters - 1, maximumCharacters);
                if (splitAt <= 0)
                {
                    splitAt = maximumCharacters;
                }

                yield return remaining[..splitAt].TrimEnd();
                remaining = remaining[splitAt..].TrimStart();
            }

            yield return remaining;
        }
    }

    private static unsafe void AddSignatureOverlay(
        IntPtr document,
        IntPtr page,
        float pageHeight,
        SignatureOverlay signature)
    {
        if (string.IsNullOrWhiteSpace(signature.ImageBase64) || signature.Width <= 0 || signature.Height <= 0)
        {
            return;
        }

        byte[] imageBytes;
        try
        {
            imageBytes = Convert.FromBase64String(signature.ImageBase64);
        }
        catch (FormatException)
        {
            return;
        }

        using var stream = new MemoryStream(imageBytes, writable: false);
        using var source = new System.Drawing.Bitmap(stream);
        using var bitmap = new System.Drawing.Bitmap(
            source.Width,
            source.Height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.Clear(System.Drawing.Color.Transparent);
            graphics.DrawImage(source, 0, 0, source.Width, source.Height);
        }

        var pdfBitmap = PdfiumNative.FPDFBitmap_Create(bitmap.Width, bitmap.Height, 1);
        if (pdfBitmap == IntPtr.Zero)
        {
            throw CreatePdfiumException("PDFium could not allocate a signature bitmap.");
        }

        IntPtr imageObject = IntPtr.Zero;
        var inserted = false;
        try
        {
            CopyBitmapToPdfium(bitmap, pdfBitmap);
            imageObject = PdfiumNative.FPDFPageObj_NewImageObj(document);
            if (imageObject == IntPtr.Zero)
            {
                throw CreatePdfiumException("PDFium could not create a signature image object.");
            }

            var pagePtr = page;
            if (PdfiumNative.FPDFImageObj_SetBitmap(&pagePtr, 1, imageObject, pdfBitmap) == 0 ||
                PdfiumNative.FPDFPageObj_SetMatrix(
                    imageObject,
                    signature.Width,
                    0,
                    0,
                    signature.Height,
                    signature.X,
                    pageHeight - signature.Y - signature.Height) == 0 ||
                PdfiumNative.FPDFPage_InsertObject(page, imageObject) == 0)
            {
                throw CreatePdfiumException("PDFium could not add the signature image to the page.");
            }

            inserted = true;
        }
        finally
        {
            if (!inserted && imageObject != IntPtr.Zero)
            {
                PdfiumNative.FPDFPageObj_Destroy(imageObject);
            }

            PdfiumNative.FPDFBitmap_Destroy(pdfBitmap);
        }
    }

    private static void CopyBitmapToPdfium(System.Drawing.Bitmap bitmap, IntPtr pdfBitmap)
    {
        var rectangle = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(
            rectangle,
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        try
        {
            var target = PdfiumNative.FPDFBitmap_GetBuffer(pdfBitmap);
            var targetStride = PdfiumNative.FPDFBitmap_GetStride(pdfBitmap);
            var row = new byte[bitmap.Width * 4];
            for (var y = 0; y < bitmap.Height; y++)
            {
                var sourceY = data.Stride < 0 ? bitmap.Height - 1 - y : y;
                Marshal.Copy(data.Scan0 + (sourceY * Math.Abs(data.Stride)), row, 0, row.Length);
                Marshal.Copy(row, 0, target + (y * targetStride), row.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static (uint Red, uint Green, uint Blue) ParseRgb(string colorHex)
    {
        var hex = colorHex?.Trim().TrimStart('#');
        return hex is { Length: 6 } &&
               uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var value)
            ? ((value >> 16) & 0xff, (value >> 8) & 0xff, value & 0xff)
            : (0, 0, 0);
    }

    private static List<PdfOutlineItem> ReadOutlineChildren(
        IntPtr documentHandle,
        IntPtr bookmark,
        CancellationToken cancellationToken)
    {
        var items = new List<PdfOutlineItem>();
        var current = bookmark;

        while (current != IntPtr.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var title = ReadBookmarkTitle(current);
            var pageIndex = ResolveBookmarkPageIndex(documentHandle, current);
            var childBookmark = PdfiumNative.FPDFBookmark_GetFirstChild(documentHandle, current);
            var children = ReadOutlineChildren(documentHandle, childBookmark, cancellationToken);
            items.Add(new PdfOutlineItem(title, pageIndex, children));
            current = PdfiumNative.FPDFBookmark_GetNextSibling(current);
        }

        return items;
    }

    private static string ReadBookmarkTitle(IntPtr bookmark)
    {
        var length = PdfiumNative.FPDFBookmark_GetTitle(bookmark, null, 0);
        if (length == 0)
        {
            return "Untitled";
        }

        var buffer = new byte[length];
        PdfiumNative.FPDFBookmark_GetTitle(bookmark, buffer, length);
        return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)length - 2);
    }

    private static int ResolveBookmarkPageIndex(IntPtr documentHandle, IntPtr bookmark)
    {
        var dest = PdfiumNative.FPDFBookmark_GetDest(documentHandle, bookmark);
        if (dest == IntPtr.Zero)
        {
            return 0;
        }

        var pageIndex = PdfiumNative.FPDFDest_GetDestPageIndex(documentHandle, dest);
        return pageIndex >= 0 ? pageIndex : 0;
    }

    private static IReadOnlyList<PdfRect> ExtractMatchHighlightRects(IntPtr textPage, int charIndex, int matchLength)
    {
        if (matchLength <= 0)
        {
            return [];
        }

        var rects = new List<PdfRect>(matchLength);
        for (var offset = 0; offset < matchLength; offset++)
        {
            if (PdfiumNative.FPDFText_GetRect(
                    textPage,
                    charIndex + offset,
                    out var left,
                    out var top,
                    out var right,
                    out var bottom))
            {
                rects.Add(new PdfRect((float)left, (float)top, (float)right, (float)bottom));
            }
        }

        return rects;
    }

    private static string ExtractTextContext(IntPtr textPage, int charIndex, int matchLength)
    {
        var contextStart = Math.Max(0, charIndex - 20);
        var contextEnd = Math.Min(PdfiumNative.FPDFText_CountChars(textPage), charIndex + matchLength + 20);
        var length = Math.Max(0, contextEnd - contextStart);
        if (length == 0)
        {
            return string.Empty;
        }

        var buffer = new ushort[length + 1];
        var written = PdfiumNative.FPDFText_GetText(textPage, contextStart, length, buffer);
        if (written <= 0)
        {
            return string.Empty;
        }

        var charCount = Math.Min(written - 1, length);
        var chars = new char[charCount];
        for (var i = 0; i < charCount; i++)
        {
            chars[i] = (char)buffer[i];
        }

        return new string(chars);
    }

    private static async Task<byte[]> EncodeBitmapToPngAsync(
        IntPtr bitmap,
        int renderWidth,
        int renderHeight,
        CancellationToken cancellationToken)
    {
        var stride = PdfiumNative.FPDFBitmap_GetStride(bitmap);
        var sourceLength = stride * renderHeight;
        var sourcePixels = new byte[sourceLength];
        Marshal.Copy(PdfiumNative.FPDFBitmap_GetBuffer(bitmap), sourcePixels, 0, sourceLength);

        var packedPixels = PackBitmapRows(sourcePixels, stride, renderWidth, renderHeight);

        using var randomAccessStream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, randomAccessStream);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            (uint)renderWidth,
            (uint)renderHeight,
            96,
            96,
            packedPixels);
        await encoder.FlushAsync();

        randomAccessStream.Seek(0);
        using var readStream = randomAccessStream.AsStreamForRead();
        using var output = new MemoryStream();
        await readStream.CopyToAsync(output, cancellationToken);
        return output.ToArray();
    }

    private string? TryGetNativeDependencyIssue()
    {
        if (_nativeDependencyIssue is not null)
        {
            return _nativeDependencyIssue;
        }

        var pdfiumPath = ResolvePdfiumPath();
        if (pdfiumPath is null)
        {
            _nativeDependencyIssue =
                "Place a native pdfium.dll under runtimes\\win-x64\\native\\pdfium.dll before opening PDFs.";
            return _nativeDependencyIssue;
        }

        var fileInfo = new FileInfo(pdfiumPath);
        if (fileInfo.Length < 1024)
        {
            _nativeDependencyIssue =
                $"The configured pdfium.dll at '{pdfiumPath}' is only {fileInfo.Length} byte(s) and is not a usable native binary.";
            return _nativeDependencyIssue;
        }

        return null;
    }

    private string? ResolvePdfiumPath()
    {
        if (_resolvedPdfiumPath is not null)
        {
            return _resolvedPdfiumPath;
        }

        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64"
        };

        var candidatePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "pdfium.dll"),
            Path.Combine(AppContext.BaseDirectory, "runtimes", architecture, "native", "pdfium.dll"),
            Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "pdfium.dll"),
            Path.Combine(Directory.GetCurrentDirectory(), "pdfium.dll"),
            Path.Combine(Directory.GetCurrentDirectory(), "runtimes", architecture, "native", "pdfium.dll"),
            Path.Combine(Directory.GetCurrentDirectory(), "runtimes", "win-x64", "native", "pdfium.dll")
        };

        _resolvedPdfiumPath = candidatePaths.FirstOrDefault(File.Exists);
        return _resolvedPdfiumPath;
    }

    private void EnsureInitialized()
    {
        lock (_nativeStateLock)
        {
            if (_initialized)
            {
                return;
            }

            var issue = TryGetNativeDependencyIssue();
            if (issue is not null)
            {
                throw new PdfiumDependencyException(issue);
            }

            var pdfiumPath = ResolvePdfiumPath()!;

            try
            {
                _nativeLibraryHandle = NativeLibrary.Load(pdfiumPath);
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                _nativeDependencyIssue = $"Unable to load native PDFium from '{pdfiumPath}'.";
                throw new PdfiumDependencyException(_nativeDependencyIssue, ex);
            }

            try
            {
                PdfiumNative.FPDF_InitLibrary();
                _initialized = true;
                _nativeDependencyIssue = null;
            }
            catch
            {
                NativeLibrary.Free(_nativeLibraryHandle);
                _nativeLibraryHandle = IntPtr.Zero;
                throw;
            }
        }
    }

    private async Task ExecutePdfiumCallAsync(Action action, CancellationToken cancellationToken)
    {
        await _pdfiumGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            EnsureInitialized();
            await Task.Run(action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pdfiumGate.Release();
        }
    }

    private async Task<T> ExecutePdfiumCallAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        await _pdfiumGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            EnsureInitialized();
            return await Task.Run(action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pdfiumGate.Release();
        }
    }

    private async Task<T> ExecutePdfiumCallAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _pdfiumGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            EnsureInitialized();
            return await Task.Run(action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pdfiumGate.Release();
        }
    }

    private void SaveDocumentCore(IntPtr documentHandle, string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        _activeWriteStream = outputStream;

        var callback = new FpdfWriteBlockCallback(WriteBlock);
        var fileWrite = new FpdfFileWrite
        {
            version = 1,
            WriteBlock = Marshal.GetFunctionPointerForDelegate(callback)
        };

        try
        {
            var saved = PdfiumNative.FPDF_SaveAsCopy(documentHandle, ref fileWrite, PdfiumNative.SaveWithoutIncremental);
            if (saved == 0)
            {
                throw CreatePdfiumException($"PDFium was unable to save '{Path.GetFileName(outputPath)}'.");
            }
        }
        finally
        {
            _activeWriteStream = null;
            GC.KeepAlive(callback);
        }
    }

    private int WriteBlock(IntPtr fileWrite, IntPtr data, uint size)
    {
        if (_activeWriteStream is null)
        {
            return 0;
        }

        try
        {
            var buffer = new byte[checked((int)size)];
            Marshal.Copy(data, buffer, 0, checked((int)size));
            _activeWriteStream.Write(buffer, 0, buffer.Length);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return 0;
        }
    }

    private static void RenderPageBitmap(
        IntPtr formHandle,
        IntPtr page,
        IntPtr bitmap,
        int renderWidth,
        int renderHeight,
        int rotate = 0)
    {
        var hasFormFill = formHandle != IntPtr.Zero;
        if (hasFormFill)
        {
            PdfiumNative.FORM_OnAfterLoadPage(page, formHandle);
        }

        try
        {
            var baseRenderFlags = hasFormFill ? 0 : PdfiumNative.RenderAnnotations;
            PdfiumNative.FPDF_RenderPageBitmap(
                bitmap,
                page,
                0,
                0,
                renderWidth,
                renderHeight,
                rotate,
                baseRenderFlags);

            if (hasFormFill)
            {
                PdfiumNative.FPDF_FFLDraw(
                    formHandle,
                    bitmap,
                    page,
                    0,
                    0,
                    renderWidth,
                    renderHeight,
                    rotate,
                    PdfiumNative.RenderAnnotations);
            }
        }
        finally
        {
            if (hasFormFill)
            {
                PdfiumNative.FORM_OnBeforeClosePage(page, formHandle);
            }
        }
    }

    private static byte[] PackBitmapRows(byte[] sourcePixels, int stride, int width, int height)
    {
        var packedPixels = new byte[width * height * 4];
        var packedStride = width * 4;

        for (var row = 0; row < height; row++)
        {
            System.Buffer.BlockCopy(sourcePixels, row * stride, packedPixels, row * packedStride, packedStride);
        }

        return packedPixels;
    }

    private static Exception CreateOpenException(string path, string? password)
    {
        var lastError = PdfiumNative.FPDF_GetLastError();
        if (lastError == PdfiumNative.ErrPassword)
        {
            return password is null
                ? new PdfPasswordRequiredException(path)
                : new PdfIncorrectPasswordException(path);
        }

        return CreatePdfiumException($"Unable to open '{Path.GetFileName(path)}'.");
    }

    private static Exception CreatePdfiumException(string message)
    {
        var lastError = PdfiumNative.FPDF_GetLastError();
        return lastError == 0
            ? new InvalidOperationException(message)
            : new InvalidOperationException($"{message} PDFium error code: 0x{lastError:X8}.");
    }
}
