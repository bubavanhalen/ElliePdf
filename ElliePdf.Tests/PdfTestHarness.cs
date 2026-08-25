using System.Runtime.InteropServices;
using System.Text;
using Xunit;

namespace ElliePdf.Tests;

/// <summary>
/// Minimal PDFium helpers for building and inspecting documents in tests, so the assertions can
/// talk about pages, text layers and pixels rather than raw handles.
/// </summary>
internal static class PdfTestHarness
{
    private const string Dll = "pdfium.dll";

    private static readonly Lock InitLock = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        lock (InitLock)
        {
            if (_initialized)
            {
                return;
            }

            FPDF_InitLibrary();
            _initialized = true;
        }
    }

    /// <summary>Creates a one-page PDF containing a real text layer.</summary>
    public static string CreateTextPdf(string path, string text, float width = 400, float height = 300, int rotation = 0)
    {
        EnsureInitialized();

        var document = FPDF_CreateNewDocument();
        Assert.NotEqual(IntPtr.Zero, document);

        try
        {
            var page = FPDFPage_New(document, 0, width, height);
            Assert.NotEqual(IntPtr.Zero, page);

            var textObject = FPDFPageObj_NewTextObj(document, "Helvetica", 18f);
            Assert.NotEqual(IntPtr.Zero, textObject);
            Assert.NotEqual(0, FPDFText_SetText(textObject, ToWide(text)));
            FPDFPageObj_Transform(textObject, 1, 0, 0, 1, 20, height - 40);
            FPDFPage_InsertObject(page, textObject);

            if (rotation != 0)
            {
                FPDFPage_SetRotation(page, rotation);
            }

            Assert.NotEqual(0, FPDFPage_GenerateContent(page));
            FPDF_ClosePage(page);

            Save(document, path);
        }
        finally
        {
            FPDF_CloseDocument(document);
        }

        return path;
    }

    public static IntPtr Open(string path)
    {
        EnsureInitialized();
        var document = FPDF_LoadDocument(path, null);
        Assert.NotEqual(IntPtr.Zero, document);
        return document;
    }

    public static void Save(IntPtr document, string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writeTarget = stream;
        var callback = new WriteBlockCallback(WriteBlock);
        var fileWrite = new FileWrite
        {
            Version = 1,
            WriteBlock = Marshal.GetFunctionPointerForDelegate(callback)
        };

        try
        {
            Assert.NotEqual(0, FPDF_SaveAsCopy(document, ref fileWrite, 2));
        }
        finally
        {
            _writeTarget = null;
            GC.KeepAlive(callback);
        }
    }

    public static string ExtractText(IntPtr document, int pageIndex)
    {
        var page = FPDF_LoadPage(document, pageIndex);
        Assert.NotEqual(IntPtr.Zero, page);

        try
        {
            var textPage = FPDFText_LoadPage(page);
            Assert.NotEqual(IntPtr.Zero, textPage);

            try
            {
                var count = FPDFText_CountChars(textPage);
                if (count <= 0)
                {
                    return string.Empty;
                }

                var buffer = new ushort[count + 1];
                var written = FPDFText_GetText(textPage, 0, count, buffer);
                if (written <= 1)
                {
                    return string.Empty;
                }

                var builder = new StringBuilder(written - 1);
                for (var index = 0; index < written - 1; index++)
                {
                    builder.Append((char)buffer[index]);
                }

                return builder.ToString();
            }
            finally
            {
                FPDFText_ClosePage(textPage);
            }
        }
        finally
        {
            FPDF_ClosePage(page);
        }
    }

    /// <summary>Renders a page and returns BGRA pixels alongside the rendered dimensions.</summary>
    public static (byte[] Pixels, int Width, int Height) Render(IntPtr document, int pageIndex, double scale = 1.0)
    {
        var page = FPDF_LoadPage(document, pageIndex);
        Assert.NotEqual(IntPtr.Zero, page);

        try
        {
            var width = Math.Max(1, (int)Math.Ceiling(FPDF_GetPageWidthF(page) * scale));
            var height = Math.Max(1, (int)Math.Ceiling(FPDF_GetPageHeightF(page) * scale));
            var bitmap = FPDFBitmap_Create(width, height, 1);
            Assert.NotEqual(IntPtr.Zero, bitmap);

            try
            {
                FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, 0);

                var stride = FPDFBitmap_GetStride(bitmap);
                var buffer = FPDFBitmap_GetBuffer(bitmap);
                var pixels = new byte[width * height * 4];

                for (var row = 0; row < height; row++)
                {
                    Marshal.Copy(buffer + (row * stride), pixels, row * width * 4, width * 4);
                }

                return (pixels, width, height);
            }
            finally
            {
                FPDFBitmap_Destroy(bitmap);
            }
        }
        finally
        {
            FPDF_ClosePage(page);
        }
    }

    public static (byte B, byte G, byte R, byte A) PixelAt((byte[] Pixels, int Width, int Height) render, int x, int y)
    {
        var offset = (y * render.Width * 4) + (x * 4);
        return (render.Pixels[offset], render.Pixels[offset + 1], render.Pixels[offset + 2], render.Pixels[offset + 3]);
    }

    /// <summary>Returns the darkest channel value inside a region — low means ink is present.</summary>
    public static int DarkestInRegion(
        (byte[] Pixels, int Width, int Height) render,
        int left,
        int top,
        int width,
        int height)
    {
        var darkest = 255;

        for (var y = Math.Max(0, top); y < Math.Min(render.Height, top + height); y++)
        {
            for (var x = Math.Max(0, left); x < Math.Min(render.Width, left + width); x++)
            {
                var pixel = PixelAt(render, x, y);
                darkest = Math.Min(darkest, Math.Min(pixel.R, Math.Min(pixel.G, pixel.B)));
            }
        }

        return darkest;
    }

    /// <summary>Finds the bounding box of a run of characters in the page's text layer.</summary>
    public static (double Left, double Right, double Bottom, double Top) CharBoxOf(
        IntPtr document,
        int pageIndex,
        string needle)
    {
        var page = FPDF_LoadPage(document, pageIndex);
        Assert.NotEqual(IntPtr.Zero, page);

        try
        {
            var textPage = FPDFText_LoadPage(page);
            Assert.NotEqual(IntPtr.Zero, textPage);

            try
            {
                var count = FPDFText_CountChars(textPage);
                var buffer = new ushort[count + 1];
                FPDFText_GetText(textPage, 0, count, buffer);

                var text = new string(buffer.Take(Math.Max(0, count)).Select(c => (char)c).ToArray());
                var start = text.IndexOf(needle, StringComparison.Ordinal);
                Assert.True(start >= 0, $"'{needle}' was not found in the text layer: '{text}'");

                double left = double.MaxValue, bottom = double.MaxValue;
                double right = double.MinValue, top = double.MinValue;

                for (var index = start; index < start + needle.Length; index++)
                {
                    FPDFText_GetCharBox(textPage, index, out var l, out var r, out var b, out var t);
                    left = Math.Min(left, l);
                    right = Math.Max(right, r);
                    bottom = Math.Min(bottom, b);
                    top = Math.Max(top, t);
                }

                return (left, right, bottom, top);
            }
            finally
            {
                FPDFText_ClosePage(textPage);
            }
        }
        finally
        {
            FPDF_ClosePage(page);
        }
    }

    /// <summary>Counts pixels that are neither white nor near-white, i.e. actual drawn content.</summary>
    public static int CountInkPixels((byte[] Pixels, int Width, int Height) render)
    {
        var count = 0;
        for (var offset = 0; offset < render.Pixels.Length; offset += 4)
        {
            if (render.Pixels[offset] < 200 || render.Pixels[offset + 1] < 200 || render.Pixels[offset + 2] < 200)
            {
                count++;
            }
        }

        return count;
    }

    public static ushort[] ToWide(string value)
    {
        var buffer = new ushort[value.Length + 1];
        for (var index = 0; index < value.Length; index++)
        {
            buffer[index] = value[index];
        }

        return buffer;
    }

    private static FileStream? _writeTarget;

    private static int WriteBlock(IntPtr fileWrite, IntPtr data, uint size)
    {
        if (_writeTarget is null)
        {
            return 0;
        }

        var buffer = new byte[size];
        Marshal.Copy(data, buffer, 0, (int)size);
        _writeTarget.Write(buffer, 0, buffer.Length);
        return 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileWrite
    {
        public int Version;
        public IntPtr WriteBlock;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WriteBlockCallback(IntPtr fileWrite, IntPtr data, uint size);

    [DllImport(Dll)] private static extern void FPDF_InitLibrary();
    [DllImport(Dll)] private static extern IntPtr FPDF_CreateNewDocument();
    [DllImport(Dll, CharSet = CharSet.Ansi)] private static extern IntPtr FPDF_LoadDocument(string path, string? password);
    [DllImport(Dll)] private static extern void FPDF_CloseDocument(IntPtr document);
    [DllImport(Dll)] private static extern int FPDF_SaveAsCopy(IntPtr document, ref FileWrite fileWrite, uint flags);
    [DllImport(Dll)] private static extern IntPtr FPDFPage_New(IntPtr document, int index, double width, double height);
    [DllImport(Dll)] private static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
    [DllImport(Dll)] private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Dll)] private static extern int FPDFPage_GenerateContent(IntPtr page);
    [DllImport(Dll)] private static extern void FPDFPage_SetRotation(IntPtr page, int rotation);
    [DllImport(Dll)] private static extern void FPDFPage_InsertObject(IntPtr page, IntPtr pageObject);
    [DllImport(Dll, CharSet = CharSet.Ansi)] private static extern IntPtr FPDFPageObj_NewTextObj(IntPtr document, string font, float size);
    [DllImport(Dll)] private static extern int FPDFText_SetText(IntPtr textObject, ushort[] text);
    [DllImport(Dll)] private static extern void FPDFPageObj_Transform(IntPtr obj, double a, double b, double c, double d, double e, double f);
    [DllImport(Dll)] private static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Dll)] private static extern void FPDFText_ClosePage(IntPtr textPage);
    [DllImport(Dll)] private static extern int FPDFText_CountChars(IntPtr textPage);
    [DllImport(Dll)] private static extern int FPDFText_GetText(IntPtr textPage, int start, int count, ushort[] buffer);
    [DllImport(Dll)] private static extern void FPDFText_GetCharBox(IntPtr textPage, int index, out double left, out double right, out double bottom, out double top);
    [DllImport(Dll)] private static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(Dll)] private static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(Dll)] private static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
    [DllImport(Dll)] private static extern int FPDFBitmap_FillRect(IntPtr bitmap, int l, int t, int w, int h, uint color);
    [DllImport(Dll)] private static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
    [DllImport(Dll)] private static extern int FPDFBitmap_GetStride(IntPtr bitmap);
    [DllImport(Dll)] private static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport(Dll)] private static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int w, int h, int rotate, int flags);
}
