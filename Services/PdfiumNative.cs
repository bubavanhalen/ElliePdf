using System.Runtime.InteropServices;

namespace ElliePdf.Services;

internal static partial class PdfiumNative
{
    public const int RenderAnnotations = 0x01;
    public const uint WhiteArgb = 0xFFFFFFFF;
    public const uint SaveWithoutIncremental = 0x02;
    public const uint MatchCase = 0x01;
    public const uint MatchWholeWord = 0x02;
    public const uint ErrPassword = 4;

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
    public static partial IntPtr FPDFDOC_InitFormFillEnvironment(IntPtr document, IntPtr formInfo);

    [LibraryImport("pdfium.dll")]
    public static partial void FPDFDOC_ExitFormFillEnvironment(IntPtr formHandle);

    [LibraryImport("pdfium.dll")]
    public static partial void FORM_OnAfterLoadPage(IntPtr page, IntPtr formHandle);

    [LibraryImport("pdfium.dll")]
    public static partial void FORM_OnBeforeClosePage(IntPtr page, IntPtr formHandle);

    [LibraryImport("pdfium.dll")]
    public static partial void FPDF_FFLDraw(
        IntPtr formHandle,
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
    public static partial IntPtr FPDFPage_New(IntPtr document, int page_index, double width, double height);

    [LibraryImport("pdfium.dll")]
    public static partial IntPtr FPDFPageObj_NewImageObj(IntPtr document);

    [LibraryImport("pdfium.dll")]
    public static unsafe partial int FPDFImageObj_SetBitmap(
        IntPtr* pages,
        int count,
        IntPtr image_object,
        IntPtr bitmap);

    [LibraryImport("pdfium.dll")]
    public static partial int FPDFPageObj_SetMatrix(IntPtr page_object, ref FsMatrix matrix);

    [LibraryImport("pdfium.dll")]
    public static partial void FPDFPage_InsertObject(IntPtr page, IntPtr page_object);

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

    [LibraryImport("pdfium.dll")]
    public static partial IntPtr FPDFText_LoadPage(IntPtr page);

    [LibraryImport("pdfium.dll")]
    public static partial void FPDFText_ClosePage(IntPtr text_page);

    [LibraryImport("pdfium.dll")]
    public static partial int FPDFText_CountChars(IntPtr text_page);

    [LibraryImport("pdfium.dll")]
    public static partial int FPDFText_GetText(
        IntPtr text_page,
        int start_index,
        int count,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] ushort[] buffer);

    [LibraryImport("pdfium.dll")]
    public static partial IntPtr FPDFText_FindStart(
        IntPtr text_page,
        byte[] findwhat,
        uint flags,
        int start_index);

    [LibraryImport("pdfium.dll")]
    public static partial nint FPDFText_FindNext(IntPtr handle);

    [LibraryImport("pdfium.dll")]
    public static partial void FPDFText_FindClose(IntPtr handle);

    [LibraryImport("pdfium.dll")]
    public static partial int FPDFText_GetSchResultIndex(IntPtr handle);

    [LibraryImport("pdfium.dll")]
    public static partial int FPDFText_GetSchCount(IntPtr handle);

    [LibraryImport("pdfium.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FPDFText_GetRect(
        IntPtr text_page,
        int index,
        out double left,
        out double top,
        out double right,
        out double bottom);

    [LibraryImport("pdfium.dll")]
    public static partial IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);

    [LibraryImport("pdfium.dll")]
    public static partial IntPtr FPDFBookmark_GetNextSibling(IntPtr bookmark);

    [LibraryImport("pdfium.dll")]
    public static partial uint FPDFBookmark_GetTitle(IntPtr bookmark, byte[]? buffer, uint buflen);

    [LibraryImport("pdfium.dll")]
    public static partial IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);

    [LibraryImport("pdfium.dll")]
    public static partial int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest);
}

[StructLayout(LayoutKind.Sequential)]
internal struct FpdfFileWrite
{
    public int version;
    public IntPtr WriteBlock;
}

/// <summary>Mirrors PDFium's <c>FS_MATRIX</c> (six single-precision components).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FsMatrix
{
    public float a;
    public float b;
    public float c;
    public float d;
    public float e;
    public float f;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int FpdfWriteBlockCallback(IntPtr fileWrite, IntPtr data, uint size);
