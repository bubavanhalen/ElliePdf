using System.IO;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ElliePdf.Services;

public interface IPdfService
{
    bool HasConfiguredNativeDependency { get; }

    string? NativeDependencyIssue { get; }

    Task<PdfDocumentSession> OpenDocumentAsync(string path, CancellationToken cancellationToken = default);

    Task<byte[]> RenderPageThumbnailAsync(
        PdfDocumentSession document,
        int pageIndex,
        int maxWidth,
        int maxHeight,
        CancellationToken cancellationToken = default);

    Task RotatePageAsync(
        PdfDocumentSession document,
        int pageIndex,
        int quarterTurnsClockwise,
        CancellationToken cancellationToken = default);

    Task DeletePageAsync(PdfDocumentSession document, int pageIndex, CancellationToken cancellationToken = default);

    Task MergeDocumentsAsync(
        IReadOnlyList<PdfDocumentSession> sourceDocuments,
        string outputPath,
        CancellationToken cancellationToken = default);

    Task SaveDocumentAsync(PdfDocumentSession document, string outputPath, CancellationToken cancellationToken = default);

    Task CloseDocumentAsync(PdfDocumentSession document, CancellationToken cancellationToken = default);
}

public sealed class PdfDocumentSession : IAsyncDisposable
{
    private readonly IPdfService _pdfService;

    internal PdfDocumentSession(IPdfService pdfService, IntPtr handle, string sourcePath, int pageCount)
    {
        _pdfService = pdfService;
        Handle = handle;
        SourcePath = sourcePath;
        PageCount = pageCount;
    }

    public string SourcePath { get; }

    public int PageCount { get; internal set; }

    internal IntPtr Handle { get; private set; }

    public bool IsClosed => Handle == IntPtr.Zero;

    public ValueTask DisposeAsync()
    {
        if (IsClosed)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(_pdfService.CloseDocumentAsync(this));
    }

    internal void MarkClosed() => Handle = IntPtr.Zero;
}

public sealed class PdfiumDependencyException : InvalidOperationException
{
    public PdfiumDependencyException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

internal static partial class PdfiumNative
{
    public const int RenderAnnotations = 0x01;
    public const uint WhiteArgb = 0xFFFFFFFF;
    public const uint SaveWithoutIncremental = 0x02;

    [LibraryImport("pdfium.dll")]
    public static partial void FPDF_InitLibrary();

    [LibraryImport("pdfium.dll")]
    public static partial void FPDF_DestroyLibrary();

    [LibraryImport("pdfium.dll", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr FPDF_LoadDocument(string file_path, string? password);

    [LibraryImport("pdfium.dll")]
    public static partial void FPDF_CloseDocument(IntPtr document);

    [LibraryImport("pdfium.dll")]
    public static partial int FPDF_GetPageCount(IntPtr document);

    [LibraryImport("pdfium.dll")]
    public static partial IntPtr FPDF_LoadPage(IntPtr document, int page_index);

    [LibraryImport("pdfium.dll")]
    public static partial void FPDF_ClosePage(IntPtr page);

    [LibraryImport("pdfium.dll")]
    public static partial float FPDF_GetPageWidthF(IntPtr page);

    [LibraryImport("pdfium.dll")]
    public static partial float FPDF_GetPageHeightF(IntPtr page);

    [LibraryImport("pdfium.dll")]
    public static partial IntPtr FPDFBitmap_Create(int width, int height, int alpha);

    [LibraryImport("pdfium.dll")]
    public static partial int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport("pdfium.dll")]
    public static partial IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);

    [LibraryImport("pdfium.dll")]
    public static partial int FPDFBitmap_GetStride(IntPtr bitmap);

    [LibraryImport("pdfium.dll")]
    public static partial void FPDFBitmap_Destroy(IntPtr bmp);

    [LibraryImport("pdfium.dll")]
    public static partial void FPDF_RenderPageBitmap(
        IntPtr bitmap,
        IntPtr page,
        int start_x,
        int start_y,
        int size_x,
        int size_y,
        int rotate,
        int flags);

    [LibraryImport("pdfium.dll")]
    public static partial int FPDFPage_GetRotation(IntPtr page);

    [LibraryImport("pdfium.dll")]
    public static partial void FPDFPage_SetRotation(IntPtr page, int rotate);

    [LibraryImport("pdfium.dll")]
    public static partial int FPDFPage_GenerateContent(IntPtr page);

    [LibraryImport("pdfium.dll")]
    public static partial void FPDFPage_Delete(IntPtr document, int page_index);

    [LibraryImport("pdfium.dll")]
    public static partial IntPtr FPDF_CreateNewDocument();

    [LibraryImport("pdfium.dll")]
    public static partial int FPDF_ImportPagesByIndex(
        IntPtr dest_doc,
        IntPtr src_doc,
        [MarshalAs(UnmanagedType.LPArray)] int[] page_indices,
        uint length,
        int index);

    [LibraryImport("pdfium.dll")]
    public static partial void FPDF_CopyViewerPreferences(IntPtr dest_doc, IntPtr src_doc);

    [LibraryImport("pdfium.dll")]
    public static partial int FPDF_SaveAsCopy(IntPtr document, ref FpdfFileWrite fileWrite, uint flags);

    [LibraryImport("pdfium.dll")]
    public static partial uint FPDF_GetLastError();
}

[StructLayout(LayoutKind.Sequential)]
internal struct FpdfFileWrite
{
    public int version;
    public IntPtr WriteBlock;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int FpdfWriteBlockCallback(IntPtr fileWrite, IntPtr data, uint size);

public sealed class PdfService : IPdfService, IDisposable
{
    private readonly SemaphoreSlim _pdfiumGate = new(1, 1);
    private readonly Lock _nativeStateLock = new();
    private bool _initialized;
    private IntPtr _nativeLibraryHandle;
    private string? _nativeDependencyIssue;
    private string? _resolvedPdfiumPath;
    private FileStream? _activeWriteStream;

    public bool HasConfiguredNativeDependency => TryGetNativeDependencyIssue() is null;

    public string? NativeDependencyIssue => TryGetNativeDependencyIssue();

    public Task<PdfDocumentSession> OpenDocumentAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return ExecutePdfiumCallAsync(() =>
        {
            var document = PdfiumNative.FPDF_LoadDocument(path, null);
            if (document == IntPtr.Zero)
            {
                throw CreatePdfiumException($"Unable to open '{Path.GetFileName(path)}'.");
            }

            var pageCount = PdfiumNative.FPDF_GetPageCount(document);
            if (pageCount < 0)
            {
                PdfiumNative.FPDF_CloseDocument(document);
                throw CreatePdfiumException($"Unable to read page count from '{Path.GetFileName(path)}'.");
            }

            return new PdfDocumentSession(this, document, path, pageCount);
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
                PdfiumNative.FPDF_RenderPageBitmap(
                    bitmap,
                    page,
                    0,
                    0,
                    renderWidth,
                    renderHeight,
                    0,
                    PdfiumNative.RenderAnnotations);

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

    public Task CloseDocumentAsync(PdfDocumentSession document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        return ExecutePdfiumCallAsync(() =>
        {
            if (document.IsClosed)
            {
                return;
            }

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
                "Place a native pdfium.dll at runtimes\\win-x64\\native\\pdfium.dll before importing or saving PDFs.";
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

        var candidatePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "pdfium.dll"),
            Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "pdfium.dll"),
            Path.Combine(Directory.GetCurrentDirectory(), "pdfium.dll"),
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
        await _pdfiumGate.WaitAsync(cancellationToken);

        try
        {
            EnsureInitialized();
            await Task.Run(action, cancellationToken);
        }
        finally
        {
            _pdfiumGate.Release();
        }
    }

    private async Task<T> ExecutePdfiumCallAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        await _pdfiumGate.WaitAsync(cancellationToken);

        try
        {
            EnsureInitialized();
            return await Task.Run(action, cancellationToken);
        }
        finally
        {
            _pdfiumGate.Release();
        }
    }

    private async Task<T> ExecutePdfiumCallAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _pdfiumGate.WaitAsync(cancellationToken);

        try
        {
            EnsureInitialized();
            return await Task.Run(action, cancellationToken);
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

    private static Exception CreatePdfiumException(string message)
    {
        var lastError = PdfiumNative.FPDF_GetLastError();
        return lastError == 0
            ? new InvalidOperationException(message)
            : new InvalidOperationException($"{message} PDFium error code: 0x{lastError:X8}.");
    }
}
