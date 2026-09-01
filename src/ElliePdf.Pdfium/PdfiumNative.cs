using System.Reflection;
using System.Runtime.InteropServices;

namespace ElliePdf.Pdfium;

internal static partial class PdfiumNative
{
    internal static readonly Lock ExecutionLock = new();
    private static readonly Lock ResolverLock = new();
    private static nint _loadedLibrary;
    private static int _libraryReferenceCount;
    private static bool _resolverInstalled;

    internal const int RenderAnnotations = 0x01;
    internal const uint WhiteArgb = 0xFFFFFFFF;
    internal const uint SaveWithoutIncremental = 0x02;
    internal const uint MatchCase = 0x01;
    internal const uint MatchWholeWord = 0x02;
    internal const uint ErrPassword = 4;
    internal const uint ActionGoTo = 1;
    internal const uint ActionUri = 3;
    internal const int AnnotationWidget = 20;
    internal const int AnnotationStamp = 13;
    internal const int AnnotationInk = 15;
    internal const int AnnotationFlagPrint = 1 << 2;
    internal const int FormFieldPushButton = 1;
    internal const int FormFieldCheckBox = 2;
    internal const int FormFieldRadioButton = 3;
    internal const int FormFieldComboBox = 4;
    internal const int FormFieldListBox = 5;
    internal const int FormFieldText = 6;
    internal const int FormFieldSignature = 7;
    internal const int FormFlagReadOnly = 1 << 0;
    internal const int FormFlagRequired = 1 << 1;

    internal static void AcquireLoadedLibrary(nint libraryHandle)
    {
        if (libraryHandle == 0)
        {
            throw new ArgumentException("A PDFium library handle is required.", nameof(libraryHandle));
        }

        lock (ExecutionLock)
        {
            lock (ResolverLock)
            {
                if (_loadedLibrary != 0 && _loadedLibrary != libraryHandle)
                {
                    throw new InvalidOperationException("A different PDFium library is already bound.");
                }

                if (_loadedLibrary == 0)
                {
                    _loadedLibrary = libraryHandle;
                    if (!_resolverInstalled)
                    {
                        NativeLibrary.SetDllImportResolver(typeof(PdfiumNative).Assembly, ResolveLibrary);
                        _resolverInstalled = true;
                    }

                    FPDF_InitLibrary();
                }

                checked
                {
                    _libraryReferenceCount++;
                }
            }
        }
    }

    internal static void ReleaseLoadedLibrary(nint libraryHandle)
    {
        lock (ExecutionLock)
        {
            lock (ResolverLock)
            {
                if (_loadedLibrary != libraryHandle || _libraryReferenceCount <= 0)
                {
                    throw new InvalidOperationException("The PDFium library reference is not owned by this process.");
                }

                _libraryReferenceCount--;
                if (_libraryReferenceCount == 0)
                {
                    FPDF_DestroyLibrary();
                    _loadedLibrary = 0;
                }

                // Every engine constructor calls LoadLibraryEx and therefore owns
                // one Windows loader reference, even when the returned module base
                // address is shared. Balance every one of those references. Keeping
                // only the last FreeLibrary call leaked modules across repeated lane
                // creation and made init/destroy stress dependent on test ordering.
                _ = FreeLibrary(libraryHandle);
            }
        }
    }

    private static nint ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "pdfium.dll", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        lock (ResolverLock)
        {
            return _loadedLibrary != 0
                ? _loadedLibrary
                : throw new DllNotFoundException(
                    "PDFium was not loaded through ElliePdf's verified app-private loader.");
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint LoadLibraryEx(string fileName, nint file, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "FreeLibrary", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FreeLibrary(nint module);

    [LibraryImport("pdfium.dll")]
    internal static partial void FPDF_InitLibrary();

    [LibraryImport("pdfium.dll")]
    internal static partial void FPDF_DestroyLibrary();

    [LibraryImport("pdfium.dll", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint FPDF_LoadDocument(string filePath, string? password);

    [LibraryImport("pdfium.dll", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint FPDF_LoadCustomDocument(nint fileAccess, string? password);

    [LibraryImport("pdfium.dll")]
    internal static partial void FPDF_CloseDocument(nint document);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDF_GetPageCount(nint document);

    [LibraryImport("pdfium.dll", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial uint FPDF_GetMetaText(
        nint document,
        string tag,
        byte[]? buffer,
        uint bufferLength);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDF_GetFileVersion(nint document, out int fileVersion);

    [LibraryImport("pdfium.dll")]
    internal static partial uint FPDF_GetDocPermissions(nint document);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDF_GetSecurityHandlerRevision(nint document);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDF_GetFormType(nint document);

    [LibraryImport("pdfium.dll")]
    internal static partial nint FPDF_LoadPage(nint document, int pageIndex);

    [LibraryImport("pdfium.dll")]
    internal static partial void FPDF_ClosePage(nint page);

    [LibraryImport("pdfium.dll")]
    internal static partial float FPDF_GetPageWidthF(nint page);

    [LibraryImport("pdfium.dll")]
    internal static partial float FPDF_GetPageHeightF(nint page);

    [LibraryImport("pdfium.dll")]
    internal static partial nint FPDFBitmap_Create(int width, int height, int alpha);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport("pdfium.dll")]
    internal static partial nint FPDFBitmap_GetBuffer(nint bitmap);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFBitmap_GetStride(nint bitmap);

    [LibraryImport("pdfium.dll")]
    internal static partial void FPDFBitmap_Destroy(nint bitmap);

    [LibraryImport("pdfium.dll")]
    internal static partial void FPDF_RenderPageBitmap(
        nint bitmap,
        nint page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags);

    [LibraryImport("pdfium.dll")]
    internal static partial nint FPDFDOC_InitFormFillEnvironment(nint document, nint formInfo);

    [LibraryImport("pdfium.dll")]
    internal static partial void FPDFDOC_ExitFormFillEnvironment(nint formHandle);

    [LibraryImport("pdfium.dll")]
    internal static partial void FORM_OnAfterLoadPage(nint page, nint formHandle);

    [LibraryImport("pdfium.dll")]
    internal static partial void FORM_OnBeforeClosePage(nint page, nint formHandle);

    [LibraryImport("pdfium.dll")]
    internal static partial int FORM_OnLButtonDown(nint formHandle, nint page, int modifier, double pageX, double pageY);

    [LibraryImport("pdfium.dll")]
    internal static partial int FORM_OnLButtonUp(nint formHandle, nint page, int modifier, double pageX, double pageY);

    [LibraryImport("pdfium.dll")]
    internal static partial void FPDF_FFLDraw(
        nint formHandle,
        nint bitmap,
        nint page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFPage_GetRotation(nint page);

    [LibraryImport("pdfium.dll")]
    internal static partial void FPDFPage_SetRotation(nint page, int rotate);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFPage_GenerateContent(nint page);

    [LibraryImport("pdfium.dll")]
    internal static partial nint FPDFPageObj_CreateNewPath(float x, float y);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFPath_LineTo(nint path, float x, float y);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFPath_SetDrawMode(nint path, int fillMode, int stroke);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFPageObj_SetStrokeColor(
        nint pageObject,
        uint red,
        uint green,
        uint blue,
        uint alpha);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFPageObj_SetStrokeWidth(nint pageObject, float width);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFPageObj_SetLineJoin(nint pageObject, int lineJoin);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFPageObj_SetLineCap(nint pageObject, int lineCap);

    [LibraryImport("pdfium.dll")]
    internal static partial void FPDFPage_Delete(nint document, int pageIndex);

    [LibraryImport("pdfium.dll")]
    internal static partial nint FPDFPage_New(nint document, int pageIndex, double width, double height);

    [LibraryImport("pdfium.dll")]
    internal static partial nint FPDFPageObj_NewImageObj(nint document);

    [LibraryImport("pdfium.dll")]
    internal static unsafe partial int FPDFImageObj_SetBitmap(nint* pages, int count, nint imageObject, nint bitmap);

    [LibraryImport("pdfium.dll")]
    internal static partial void FPDFPageObj_SetMatrix(
        nint pageObject,
        double a,
        double b,
        double c,
        double d,
        double e,
        double f);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFPage_InsertObject(nint page, nint pageObject);

    [LibraryImport("pdfium.dll")]
    internal static partial void FPDFPageObj_Destroy(nint pageObject);

    [LibraryImport("pdfium.dll")]
    internal static partial nint FPDF_CreateNewDocument();

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDF_ImportPagesByIndex(
        nint destinationDocument,
        nint sourceDocument,
        [MarshalAs(UnmanagedType.LPArray)] int[] pageIndices,
        uint length,
        int index);

    [LibraryImport("pdfium.dll")]
    internal static partial void FPDF_CopyViewerPreferences(nint destinationDocument, nint sourceDocument);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDF_SaveAsCopy(nint document, ref FpdfFileWrite fileWrite, uint flags);

    [LibraryImport("pdfium.dll")]
    internal static partial uint FPDF_GetLastError();

    [LibraryImport("pdfium.dll")]
    internal static partial nint FPDFText_LoadPage(nint page);

    [LibraryImport("pdfium.dll")]
    internal static partial void FPDFText_ClosePage(nint textPage);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFText_CountChars(nint textPage);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFText_GetText(
        nint textPage,
        int startIndex,
        int count,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] ushort[] buffer);

    [LibraryImport("pdfium.dll")]
    internal static partial nint FPDFText_FindStart(nint textPage, byte[] findWhat, uint flags, int startIndex);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFText_FindNext(nint handle);

    [LibraryImport("pdfium.dll")]
    internal static partial void FPDFText_FindClose(nint handle);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFText_GetSchResultIndex(nint handle);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFText_GetSchCount(nint handle);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFText_GetRect(
        nint textPage,
        int index,
        out double left,
        out double top,
        out double right,
        out double bottom);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFText_GetCharBox(
        nint textPage,
        int index,
        out double left,
        out double right,
        out double bottom,
        out double top);

    [LibraryImport("pdfium.dll")]
    internal static partial nint FPDFBookmark_GetFirstChild(nint document, nint bookmark);

    [LibraryImport("pdfium.dll")]
    internal static partial nint FPDFBookmark_GetNextSibling(nint document, nint bookmark);

    [LibraryImport("pdfium.dll")]
    internal static partial uint FPDFBookmark_GetTitle(nint bookmark, byte[]? buffer, uint bufferLength);

    [LibraryImport("pdfium.dll")]
    internal static partial nint FPDFBookmark_GetDest(nint document, nint bookmark);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFDest_GetDestPageIndex(nint document, nint destination);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFLink_Enumerate(nint page, ref int startPosition, out nint link);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFLink_GetAnnotRect(nint link, out FsRectF rectangle);

    [LibraryImport("pdfium.dll")]
    internal static partial nint FPDFLink_GetDest(nint document, nint link);

    [LibraryImport("pdfium.dll")]
    internal static partial nint FPDFLink_GetAction(nint link);

    [LibraryImport("pdfium.dll")]
    internal static partial uint FPDFAction_GetType(nint action);

    [LibraryImport("pdfium.dll")]
    internal static partial nint FPDFAction_GetDest(nint document, nint action);

    [LibraryImport("pdfium.dll")]
    internal static partial uint FPDFAction_GetURIPath(nint document, nint action, byte[]? buffer, uint bufferLength);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFPage_GetAnnotCount(nint page);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFAnnot_IsSupportedSubtype(int subtype);

    [LibraryImport("pdfium.dll")]
    internal static partial nint FPDFPage_CreateAnnot(nint page, int subtype);

    [LibraryImport("pdfium.dll")]
    internal static partial nint FPDFPage_GetAnnot(nint page, int index);

    [LibraryImport("pdfium.dll")]
    internal static partial void FPDFPage_CloseAnnot(nint annotation);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFAnnot_GetSubtype(nint annotation);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFAnnot_GetRect(nint annotation, out FsRectF rectangle);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFAnnot_SetRect(nint annotation, in FsRectF rectangle);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFAnnot_SetColor(
        nint annotation,
        int colorType,
        uint red,
        uint green,
        uint blue,
        uint alpha);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFAnnot_SetBorder(
        nint annotation,
        float horizontalRadius,
        float verticalRadius,
        float borderWidth);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFAnnot_SetFlags(nint annotation, int flags);

    [LibraryImport("pdfium.dll")]
    internal static unsafe partial int FPDFAnnot_AddInkStroke(
        nint annotation,
        FsPointF* points,
        nuint pointCount);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFAnnot_AppendObject(nint annotation, nint pageObject);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFAnnot_GetFormFieldType(nint formHandle, nint annotation);

    [LibraryImport("pdfium.dll")]
    internal static partial uint FPDFAnnot_GetFormFieldName(nint formHandle, nint annotation, byte[]? buffer, uint bufferLength);

    [LibraryImport("pdfium.dll")]
    internal static partial uint FPDFAnnot_GetFormFieldValue(nint formHandle, nint annotation, byte[]? buffer, uint bufferLength);

    [LibraryImport("pdfium.dll")]
    internal static partial uint FPDFAnnot_GetFormFieldExportValue(nint formHandle, nint annotation, byte[]? buffer, uint bufferLength);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFAnnot_GetFormFieldFlags(nint formHandle, nint annotation);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFAnnot_HasKey(nint annotation, byte[] key);

    [LibraryImport("pdfium.dll")]
    internal static partial uint FPDFAnnot_GetFormAdditionalActionJavaScript(nint formHandle, nint annotation, int eventType, byte[]? buffer, uint bufferLength);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFAnnot_GetOptionCount(nint formHandle, nint annotation);

    [LibraryImport("pdfium.dll")]
    internal static partial uint FPDFAnnot_GetOptionLabel(nint formHandle, nint annotation, int index, byte[]? buffer, uint bufferLength);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFAnnot_IsOptionSelected(nint formHandle, nint annotation, int index);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFAnnot_IsChecked(nint formHandle, nint annotation);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFAnnot_SetStringValue(nint annotation, byte[] key, ushort[] value);

    [LibraryImport("pdfium.dll")]
    internal static partial uint FPDFAnnot_GetStringValue(
        nint annotation,
        byte[] key,
        byte[]? buffer,
        uint bufferLength);

    [LibraryImport("pdfium.dll")]
    internal static partial nint FPDFPageObj_NewTextObj(nint document, byte[] font, float fontSize);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFText_SetText(nint textObject, ushort[] text);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFPageObj_SetFillColor(
        nint pageObject,
        uint red,
        uint green,
        uint blue,
        uint alpha);

    [LibraryImport("pdfium.dll")]
    internal static partial int FPDFPage_Flatten(nint page, int flags);
}

[StructLayout(LayoutKind.Sequential)]
internal struct FsRectF
{
    internal float Left;
    internal float Bottom;
    internal float Right;
    internal float Top;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FsPointF
{
    internal float X;
    internal float Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FpdfFileWrite
{
    internal int Version;
    internal nint WriteBlock;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int FpdfWriteBlockCallback(nint fileWrite, nint data, uint size);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int FpdfGetBlockCallback(nint parameter, uint position, nint outputBuffer, uint size);

[StructLayout(LayoutKind.Sequential)]
internal struct FpdfFileAccess
{
    internal uint FileLength;
    internal nint GetBlock;
    internal nint Parameter;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FpdfFormFillInfo
{
    internal int Version;
    internal nint Release;
    internal nint Invalidate;
    internal nint OutputSelectedRect;
    internal nint SetCursor;
    internal nint SetTimer;
    internal nint KillTimer;
    internal nint GetLocalTime;
    internal nint OnChange;
    internal nint GetPage;
    internal nint GetCurrentPage;
    internal nint GetRotation;
    internal nint ExecuteNamedAction;
    internal nint SetTextFieldFocus;
    internal nint DoUriAction;
    internal nint DoGoToAction;
    internal nint JsPlatform;
    internal int XfaDisabled;
    internal nint DisplayCaret;
    internal nint GetCurrentPageIndex;
    internal nint SetCurrentPage;
    internal nint GotoUrl;
    internal nint GetPageViewRect;
    internal nint PageEvent;
    internal nint PopupMenu;
    internal nint OpenFile;
    internal nint EmailTo;
    internal nint UploadTo;
    internal nint GetPlatform;
    internal nint GetLanguage;
    internal nint DownloadFromUrl;
    internal nint PostRequestUrl;
    internal nint PutRequestUrl;
    internal nint OnFocusChange;
    internal nint DoUriActionWithKeyboardModifier;
}
