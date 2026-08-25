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
                const double renderScale = 2.0;

                for (var pageIndex = 0; pageIndex < document.PageCount; pageIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    overlays!.Pages.TryGetValue(pageIndex, out var pageOverlay);

                    if (!OverlayCompositor.HasContent(pageOverlay))
                    {
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
                    }
                    else
                    {
                        var rendered = RenderPageToPackedBgra(document.Handle, document.FormFill?.FormHandle ?? IntPtr.Zero, pageIndex, renderScale);
                        var composited = OverlayCompositor.Composite(
                            rendered.Pixels,
                            rendered.Width,
                            rendered.Height,
                            pageOverlay!,
                            rendered.PageWidthPoints,
                            rendered.PageHeightPoints);

                        CreateImagePage(
                            destination,
                            pageIndex,
                            rendered.PageWidthPoints,
                            rendered.PageHeightPoints,
                            composited,
                            rendered.Width,
                            rendered.Height);
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

            _renderCache.InvalidateDocument(document);
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
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Never write straight to the destination: PDFium streams the source lazily, so truncating
        // a file that is currently open would corrupt the very data being saved.
        var stagingPath = Path.Combine(
            string.IsNullOrWhiteSpace(directory) ? Path.GetTempPath() : directory,
            $".{Path.GetFileNameWithoutExtension(fullPath)}.{Guid.NewGuid():N}.saving.pdf");

        try
        {
            WriteDocument(documentHandle, stagingPath, Path.GetFileName(fullPath));
            File.Move(stagingPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(stagingPath))
                {
                    File.Delete(stagingPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void WriteDocument(IntPtr documentHandle, string stagingPath, string displayName)
    {
        using var outputStream = new FileStream(stagingPath, FileMode.Create, FileAccess.Write, FileShare.Read);
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
                throw CreatePdfiumException($"PDFium was unable to save '{displayName}'.");
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

    private sealed record PackedPageRender(
        byte[] Pixels,
        int Width,
        int Height,
        float PageWidthPoints,
        float PageHeightPoints);

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

    private static PackedPageRender RenderPageToPackedBgra(IntPtr documentHandle, IntPtr formHandle, int pageIndex, double scale)
    {
        var page = PdfiumNative.FPDF_LoadPage(documentHandle, pageIndex);
        if (page == IntPtr.Zero)
        {
            throw CreatePdfiumException($"Unable to load page {pageIndex + 1} for saving.");
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
            RenderPageBitmap(formHandle, page, bitmap, renderWidth, renderHeight);

            var stride = PdfiumNative.FPDFBitmap_GetStride(bitmap);
            var sourceLength = stride * renderHeight;
            var sourcePixels = new byte[sourceLength];
            Marshal.Copy(PdfiumNative.FPDFBitmap_GetBuffer(bitmap), sourcePixels, 0, sourceLength);
            var packedPixels = PackBitmapRows(sourcePixels, stride, renderWidth, renderHeight);

            return new PackedPageRender(packedPixels, renderWidth, renderHeight, pageWidth, pageHeight);
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                PdfiumNative.FPDFBitmap_Destroy(bitmap);
            }

            PdfiumNative.FPDF_ClosePage(page);
        }
    }

    private static unsafe void CreateImagePage(
        IntPtr destinationDocument,
        int pageIndex,
        float pageWidthPoints,
        float pageHeightPoints,
        byte[] packedBgra,
        int renderWidth,
        int renderHeight)
    {
        var page = PdfiumNative.FPDFPage_New(destinationDocument, pageIndex, pageWidthPoints, pageHeightPoints);
        if (page == IntPtr.Zero)
        {
            throw CreatePdfiumException($"PDFium could not create page {pageIndex + 1}.");
        }

        IntPtr bitmap = IntPtr.Zero;
        IntPtr imageObject = IntPtr.Zero;

        try
        {
            bitmap = PdfiumNative.FPDFBitmap_Create(renderWidth, renderHeight, 1);
            if (bitmap == IntPtr.Zero)
            {
                throw new InvalidOperationException("PDFium failed to allocate an image bitmap.");
            }

            var stride = PdfiumNative.FPDFBitmap_GetStride(bitmap);
            var expectedStride = renderWidth * 4;
            var buffer = PdfiumNative.FPDFBitmap_GetBuffer(bitmap);

            for (var row = 0; row < renderHeight; row++)
            {
                Marshal.Copy(
                    packedBgra,
                    row * expectedStride,
                    buffer + (row * stride),
                    expectedStride);
            }

            imageObject = PdfiumNative.FPDFPageObj_NewImageObj(destinationDocument);
            if (imageObject == IntPtr.Zero)
            {
                throw CreatePdfiumException("PDFium could not create an image object.");
            }

            var matrix = new FsMatrix
            {
                a = pageWidthPoints,
                b = 0,
                c = 0,
                d = pageHeightPoints,
                e = 0,
                f = 0
            };

            PdfiumNative.FPDFPageObj_SetMatrix(imageObject, ref matrix);
            var pagePtr = page;
            if (PdfiumNative.FPDFImageObj_SetBitmap(&pagePtr, 1, imageObject, bitmap) == 0)
            {
                throw CreatePdfiumException("PDFium could not attach the flattened image to the page.");
            }

            PdfiumNative.FPDFPage_InsertObject(page, imageObject);

            if (PdfiumNative.FPDFPage_GenerateContent(page) == 0)
            {
                throw CreatePdfiumException($"PDFium could not generate content for page {pageIndex + 1}.");
            }
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                PdfiumNative.FPDFBitmap_Destroy(bitmap);
            }

            PdfiumNative.FPDF_ClosePage(page);
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
