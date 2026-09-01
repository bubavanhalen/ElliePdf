using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ElliePdf.Pdfium;

public static class PdfiumErrorCode
{
    public const uint Password = 4;
}

public sealed class PdfiumEngine : IDisposable
{
    private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;
    public const long MaximumBitmapBytes = 16L * 1024 * 1024;
    public const int MaximumBitmapDimension = 32_768;

    private readonly int _engineThreadId;
    private readonly List<PdfiumNativeOwner> _activeOwners = [];
    private nint _libraryHandle;
    private bool _initialized;
    private bool _disposed;

    internal PdfiumEngine(string? baseDirectory = null)
    {
        _engineThreadId = Environment.CurrentManagedThreadId;
        LoadVerifiedLibrary(baseDirectory);
    }

    public int EngineThreadId => _engineThreadId;

    public int ActiveOwnerCount
    {
        get
        {
            AssertEngineLane();
            return _activeOwners.Count;
        }
    }

    public uint LastError
    {
        get
        {
            AssertEngineLane();
            ThrowIfDisposed();
            return PdfiumNative.FPDF_GetLastError();
        }
    }

    public PdfiumDocumentHandle? LoadDocument(string path, string? password)
    {
        AssertUsable();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var handle = PdfiumNative.FPDF_LoadDocument(path, password);
        return handle == 0 ? null : new PdfiumDocumentHandle(this, handle);
    }

    public PdfiumDocumentHandle? LoadDocument(
        SafeFileHandle sourceHandle,
        string? password,
        bool leaveOpen = false)
    {
        AssertUsable();
        ArgumentNullException.ThrowIfNull(sourceHandle);
        if (sourceHandle.IsInvalid || sourceHandle.IsClosed)
        {
            throw new ArgumentException("A valid brokered source handle is required.", nameof(sourceHandle));
        }

        var retainedHandle = leaveOpen
            ? new SafeFileHandle(sourceHandle.DangerousGetHandle(), ownsHandle: false)
            : sourceHandle;
        var access = BrokeredFileAccess.Create(retainedHandle);
        try
        {
            var handle = PdfiumNative.FPDF_LoadCustomDocument(access.Pointer, password);
            if (handle == 0)
            {
                access.Dispose();
                return null;
            }

            return new PdfiumDocumentHandle(this, handle, access);
        }
        catch
        {
            access.Dispose();
            throw;
        }
    }

    public PdfiumDocumentHandle CreateDocument()
    {
        AssertUsable();
        var handle = PdfiumNative.FPDF_CreateNewDocument();
        return handle != 0
            ? new PdfiumDocumentHandle(this, handle)
            : throw CreateException("PDFium could not create a document.");
    }

    public int GetPageCount(PdfiumDocumentHandle document)
    {
        ValidateOwner(document);
        return PdfiumNative.FPDF_GetPageCount(document.Handle);
    }

    public string? GetMetadataText(PdfiumDocumentHandle document, string tag)
    {
        ValidateOwner(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        if (tag.Length > 64 || tag.Any(static character => character > 0x7F))
        {
            throw new ArgumentOutOfRangeException(nameof(tag));
        }

        var length = PdfiumNative.FPDF_GetMetaText(document.Handle, tag, null, 0);
        if (length <= 2)
        {
            return null;
        }

        if (length > 16 * 1024)
        {
            throw new PdfiumResourceLimitException("A PDF metadata value exceeds 16 KiB.");
        }

        var buffer = new byte[length];
        var written = PdfiumNative.FPDF_GetMetaText(document.Handle, tag, buffer, length);
        if (written <= 2 || written > length)
        {
            return null;
        }

        return Encoding.Unicode.GetString(buffer, 0, checked((int)written - 2));
    }

    public int? GetFileVersion(PdfiumDocumentHandle document)
    {
        ValidateOwner(document);
        return PdfiumNative.FPDF_GetFileVersion(document.Handle, out var version) != 0
            ? version
            : null;
    }

    public uint GetDocumentPermissions(PdfiumDocumentHandle document)
    {
        ValidateOwner(document);
        return PdfiumNative.FPDF_GetDocPermissions(document.Handle);
    }

    public int GetSecurityHandlerRevision(PdfiumDocumentHandle document)
    {
        ValidateOwner(document);
        return PdfiumNative.FPDF_GetSecurityHandlerRevision(document.Handle);
    }

    public int GetFormType(PdfiumDocumentHandle document)
    {
        ValidateOwner(document);
        return PdfiumNative.FPDF_GetFormType(document.Handle);
    }

    public PdfiumPageHandle? LoadPage(PdfiumDocumentHandle document, int pageIndex)
    {
        ValidateOwner(document);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        var handle = PdfiumNative.FPDF_LoadPage(document.Handle, pageIndex);
        return handle == 0 ? null : new PdfiumPageHandle(this, handle);
    }

    public (float Width, float Height) GetPageSize(PdfiumPageHandle page)
    {
        ValidateOwner(page);
        return (PdfiumNative.FPDF_GetPageWidthF(page.Handle), PdfiumNative.FPDF_GetPageHeightF(page.Handle));
    }

    public int GetPageRotation(PdfiumPageHandle page)
    {
        ValidateOwner(page);
        return PdfiumNative.FPDFPage_GetRotation(page.Handle);
    }

    public void SetPageRotation(PdfiumPageHandle page, int rotation)
    {
        ValidateOwner(page);
        PdfiumNative.FPDFPage_SetRotation(page.Handle, rotation);
    }

    public bool GeneratePageContent(PdfiumPageHandle page)
    {
        ValidateOwner(page);
        return PdfiumNative.FPDFPage_GenerateContent(page.Handle) != 0;
    }

    public void DeletePage(PdfiumDocumentHandle document, int pageIndex)
    {
        ValidateOwner(document);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        PdfiumNative.FPDFPage_Delete(document.Handle, pageIndex);
    }

    public PdfiumPageHandle CreatePage(
        PdfiumDocumentHandle document,
        int pageIndex,
        double width,
        double height)
    {
        ValidateOwner(document);
        var handle = PdfiumNative.FPDFPage_New(document.Handle, pageIndex, width, height);
        return handle != 0
            ? new PdfiumPageHandle(this, handle)
            : throw CreateException($"PDFium could not create page {pageIndex + 1}.");
    }

    public PdfiumBitmapHandle CreateBitmap(int width, int height, bool alpha = true)
    {
        AssertUsable();
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (width > MaximumBitmapDimension || height > MaximumBitmapDimension)
        {
            throw new PdfiumResourceLimitException(
                $"A bitmap dimension exceeds the {MaximumBitmapDimension}-pixel limit.");
        }

        var packedBytes = checked((long)width * height * 4);
        if (packedBytes > MaximumBitmapBytes)
        {
            throw new PdfiumResourceLimitException(
                $"The bitmap exceeds the {MaximumBitmapBytes}-byte native allocation limit.");
        }

        var handle = PdfiumNative.FPDFBitmap_Create(width, height, alpha ? 1 : 0);
        if (handle == 0)
        {
            throw CreateException("PDFium could not allocate a bitmap.");
        }

        var stride = PdfiumNative.FPDFBitmap_GetStride(handle);
        var nativeBytes = checked((long)stride * height);
        if (stride < checked(width * 4)
            || nativeBytes > MaximumBitmapBytes
            || nativeBytes > int.MaxValue)
        {
            PdfiumNative.FPDFBitmap_Destroy(handle);
            throw new PdfiumResourceLimitException(
                "PDFium returned a bitmap layout outside the configured native allocation limit.");
        }

        return new PdfiumBitmapHandle(this, handle, width, height, stride);
    }

    public int GetBitmapStride(PdfiumBitmapHandle bitmap)
    {
        ValidateOwner(bitmap);
        return bitmap.Stride;
    }

    public void FillBitmap(
        PdfiumBitmapHandle bitmap,
        int left,
        int top,
        int width,
        int height,
        uint color = PdfiumNative.WhiteArgb)
    {
        ValidateOwner(bitmap);
        _ = PdfiumNative.FPDFBitmap_FillRect(bitmap.Handle, left, top, width, height, color);
    }

    public byte[] CopyBitmapBytes(PdfiumBitmapHandle bitmap, int byteLength)
    {
        ValidateOwner(bitmap);
        ArgumentOutOfRangeException.ThrowIfNegative(byteLength);
        if (byteLength > bitmap.ByteLength)
        {
            throw new PdfiumResourceLimitException(
                "The requested bitmap copy exceeds the native buffer bounds.");
        }

        var result = GC.AllocateUninitializedArray<byte>(byteLength);
        Marshal.Copy(PdfiumNative.FPDFBitmap_GetBuffer(bitmap.Handle), result, 0, byteLength);
        return result;
    }

    public void WriteBitmapRow(PdfiumBitmapHandle bitmap, int destinationOffset, byte[] source, int sourceOffset, int length)
    {
        ValidateOwner(bitmap);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(destinationOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (sourceOffset > source.Length - length
            || destinationOffset > bitmap.ByteLength - length)
        {
            throw new ArgumentException("The requested bitmap row copy is outside a buffer boundary.");
        }

        Marshal.Copy(source, sourceOffset, PdfiumNative.FPDFBitmap_GetBuffer(bitmap.Handle) + destinationOffset, length);
    }

    public PdfiumFormHandle? TryCreateFormEnvironment(PdfiumDocumentHandle document)
    {
        ValidateOwner(document);

        for (var version = 1; version <= 2; version++)
        {
            var info = new FpdfFormFillInfo
            {
                Version = version,
                XfaDisabled = 1
            };
            var infoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<FpdfFormFillInfo>());
            try
            {
                Marshal.StructureToPtr(info, infoPointer, false);
                var handle = PdfiumNative.FPDFDOC_InitFormFillEnvironment(document.Handle, infoPointer);
                if (handle != 0)
                {
                    return new PdfiumFormHandle(this, handle, infoPointer);
                }
            }
            catch
            {
                Marshal.FreeHGlobal(infoPointer);
                throw;
            }

            Marshal.FreeHGlobal(infoPointer);
        }

        return null;
    }

    public void RenderPage(
        PdfiumPageHandle page,
        PdfiumBitmapHandle bitmap,
        PdfiumFormHandle? form,
        int width,
        int height,
        int rotation = 0)
    {
        ValidateOwner(page);
        ValidateOwner(bitmap);
        RenderPageRegion(
            page,
            bitmap,
            form,
            0,
            0,
            width,
            height,
            rotation);
    }

    public void RenderPageRegion(
        PdfiumPageHandle page,
        PdfiumBitmapHandle bitmap,
        PdfiumFormHandle? form,
        int startX,
        int startY,
        int pageRasterWidth,
        int pageRasterHeight,
        int rotation = 0)
    {
        ValidateOwner(page);
        ValidateOwner(bitmap);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageRasterWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageRasterHeight, 1);
        if (form is not null)
        {
            ValidateOwner(form);
            PdfiumNative.FORM_OnAfterLoadPage(page.Handle, form.Handle);
        }

        try
        {
            PdfiumNative.FPDF_RenderPageBitmap(
                bitmap.Handle,
                page.Handle,
                startX,
                startY,
                pageRasterWidth,
                pageRasterHeight,
                rotation,
                form is null ? PdfiumNative.RenderAnnotations : 0);

            if (form is not null)
            {
                PdfiumNative.FPDF_FFLDraw(
                    form.Handle,
                    bitmap.Handle,
                    page.Handle,
                    startX,
                    startY,
                    pageRasterWidth,
                    pageRasterHeight,
                    rotation,
                    PdfiumNative.RenderAnnotations);
            }
        }
        finally
        {
            if (form is not null)
            {
                PdfiumNative.FORM_OnBeforeClosePage(page.Handle, form.Handle);
            }
        }
    }

    public PdfiumTextPageHandle? LoadTextPage(PdfiumPageHandle page)
    {
        ValidateOwner(page);
        var handle = PdfiumNative.FPDFText_LoadPage(page.Handle);
        return handle == 0 ? null : new PdfiumTextPageHandle(this, handle);
    }

    public int CountCharacters(PdfiumTextPageHandle textPage)
    {
        ValidateOwner(textPage);
        return PdfiumNative.FPDFText_CountChars(textPage.Handle);
    }

    public string GetText(PdfiumTextPageHandle textPage, int startIndex, int count)
    {
        ValidateOwner(textPage);
        if (count <= 0)
        {
            return string.Empty;
        }

        var buffer = new ushort[checked(count + 1)];
        var written = PdfiumNative.FPDFText_GetText(textPage.Handle, startIndex, count, buffer);
        return written <= 1
            ? string.Empty
            : new string(buffer.Take(written - 1).Select(static value => (char)value).ToArray());
    }

    public bool TryGetTextRect(
        PdfiumTextPageHandle textPage,
        int index,
        out double left,
        out double top,
        out double right,
        out double bottom)
    {
        ValidateOwner(textPage);
        var result = PdfiumNative.FPDFText_GetCharBox(
            textPage.Handle,
            index,
            out left,
            out right,
            out var nativeBottom,
            out var nativeTop);
        top = Math.Min(nativeTop, nativeBottom);
        bottom = Math.Max(nativeTop, nativeBottom);
        if (right < left)
        {
            (left, right) = (right, left);
        }
        return result != 0;
    }

    public PdfiumSearchHandle? StartSearch(
        PdfiumTextPageHandle textPage,
        string query,
        bool matchCase,
        bool wholeWord = false,
        int startIndex = 0)
    {
        ValidateOwner(textPage);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var searchBytes = Encoding.Unicode.GetBytes(query + '\0');
        var flags = (matchCase ? PdfiumNative.MatchCase : 0u)
            | (wholeWord ? PdfiumNative.MatchWholeWord : 0u);
        var handle = PdfiumNative.FPDFText_FindStart(textPage.Handle, searchBytes, flags, startIndex);
        return handle == 0 ? null : new PdfiumSearchHandle(this, handle);
    }

    public bool FindNext(PdfiumSearchHandle search)
    {
        ValidateOwner(search);
        return PdfiumNative.FPDFText_FindNext(search.Handle) != 0;
    }

    public (int CharacterIndex, int Length) GetSearchResult(PdfiumSearchHandle search)
    {
        ValidateOwner(search);
        return (
            PdfiumNative.FPDFText_GetSchResultIndex(search.Handle),
            PdfiumNative.FPDFText_GetSchCount(search.Handle));
    }

    public PdfiumBookmark GetFirstBookmark(PdfiumDocumentHandle document, PdfiumBookmark parent = default)
    {
        ValidateOwner(document);
        ValidateBookmark(parent);
        return new PdfiumBookmark(this, PdfiumNative.FPDFBookmark_GetFirstChild(document.Handle, parent.Handle));
    }

    public PdfiumBookmark GetNextBookmark(
        PdfiumDocumentHandle document,
        PdfiumBookmark bookmark)
    {
        ValidateOwner(document);
        ValidateBookmark(bookmark, allowNull: false);
        return new PdfiumBookmark(
            this,
            PdfiumNative.FPDFBookmark_GetNextSibling(document.Handle, bookmark.Handle));
    }

    public string GetBookmarkTitle(PdfiumBookmark bookmark)
    {
        ValidateBookmark(bookmark, allowNull: false);
        var length = PdfiumNative.FPDFBookmark_GetTitle(bookmark.Handle, null, 0);
        if (length < 2 || length > 128 * 1024)
        {
            return string.Empty;
        }

        var buffer = new byte[length];
        _ = PdfiumNative.FPDFBookmark_GetTitle(bookmark.Handle, buffer, length);
        return Encoding.Unicode.GetString(buffer, 0, checked((int)length - 2));
    }

    public int GetBookmarkPageIndex(PdfiumDocumentHandle document, PdfiumBookmark bookmark)
    {
        ValidateOwner(document);
        ValidateBookmark(bookmark, allowNull: false);
        var destination = PdfiumNative.FPDFBookmark_GetDest(document.Handle, bookmark.Handle);
        return destination == 0 ? -1 : PdfiumNative.FPDFDest_GetDestPageIndex(document.Handle, destination);
    }

    public IReadOnlyList<PdfiumLinkInfo> GetPageLinks(
        PdfiumDocumentHandle document,
        PdfiumPageHandle page,
        int maximumLinks = 100_000)
    {
        ValidateOwner(document);
        ValidateOwner(page);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLinks, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumLinks, 100_000);

        var links = new List<PdfiumLinkInfo>();
        var position = 0;
        while (links.Count < maximumLinks
            && PdfiumNative.FPDFLink_Enumerate(page.Handle, ref position, out var link) != 0)
        {
            if (link == 0 || PdfiumNative.FPDFLink_GetAnnotRect(link, out var rectangle) == 0)
            {
                continue;
            }

            var bounds = NormalizeRectangle(rectangle);
            var destination = PdfiumNative.FPDFLink_GetDest(document.Handle, link);
            if (destination != 0)
            {
                var destinationPage = PdfiumNative.FPDFDest_GetDestPageIndex(document.Handle, destination);
                if (destinationPage >= 0)
                {
                    links.Add(new PdfiumLinkInfo(
                        bounds,
                        PdfiumLinkActionKind.InternalDestination,
                        destinationPage));
                }
                continue;
            }

            var action = PdfiumNative.FPDFLink_GetAction(link);
            if (action == 0)
            {
                continue;
            }

            switch (PdfiumNative.FPDFAction_GetType(action))
            {
                case PdfiumNative.ActionGoTo:
                {
                    var actionDestination = PdfiumNative.FPDFAction_GetDest(document.Handle, action);
                    var destinationPage = actionDestination == 0
                        ? -1
                        : PdfiumNative.FPDFDest_GetDestPageIndex(document.Handle, actionDestination);
                    if (destinationPage >= 0)
                    {
                        links.Add(new PdfiumLinkInfo(
                            bounds,
                            PdfiumLinkActionKind.InternalDestination,
                            destinationPage));
                    }
                    break;
                }
                case PdfiumNative.ActionUri:
                {
                    var uri = ReadUtf8((buffer, length) =>
                        PdfiumNative.FPDFAction_GetURIPath(document.Handle, action, buffer, length));
                    if (!string.IsNullOrWhiteSpace(uri))
                    {
                        links.Add(new PdfiumLinkInfo(bounds, PdfiumLinkActionKind.Uri, Uri: uri));
                    }
                    break;
                }
            }
        }

        return links;
    }

    public IReadOnlyList<PdfiumFormFieldInfo> GetPageFormFields(
        PdfiumPageHandle page,
        PdfiumFormHandle form,
        int maximumFields = 100_000)
    {
        ValidateOwner(page);
        ValidateOwner(form);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFields, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumFields, 100_000);

        var annotationCount = PdfiumNative.FPDFPage_GetAnnotCount(page.Handle);
        if (annotationCount <= 0)
        {
            return [];
        }
        if (annotationCount > maximumFields)
        {
            throw new PdfiumResourceLimitException("The page annotation count exceeds the configured limit.");
        }

        var fields = new List<PdfiumFormFieldInfo>();
        for (var annotationIndex = 0; annotationIndex < annotationCount; annotationIndex++)
        {
            using var annotation = GetPageAnnotation(page, annotationIndex);
            if (annotation is null || PdfiumNative.FPDFAnnot_GetSubtype(annotation.Handle) != PdfiumNative.AnnotationWidget)
            {
                continue;
            }

            if (PdfiumNative.FPDFAnnot_GetRect(annotation.Handle, out var rectangle) == 0)
            {
                continue;
            }

            var nativeType = PdfiumNative.FPDFAnnot_GetFormFieldType(form.Handle, annotation.Handle);
            var name = ReadUtf16((buffer, length) =>
                PdfiumNative.FPDFAnnot_GetFormFieldName(form.Handle, annotation.Handle, buffer, length));
            var value = ReadUtf16((buffer, length) =>
                PdfiumNative.FPDFAnnot_GetFormFieldValue(form.Handle, annotation.Handle, buffer, length));
            var exportValue = ReadUtf16((buffer, length) =>
                PdfiumNative.FPDFAnnot_GetFormFieldExportValue(form.Handle, annotation.Handle, buffer, length));
            var flags = PdfiumNative.FPDFAnnot_GetFormFieldFlags(form.Handle, annotation.Handle);
            var optionCount = PdfiumNative.FPDFAnnot_GetOptionCount(form.Handle, annotation.Handle);
            if (optionCount > 4_096)
            {
                throw new PdfiumResourceLimitException("The form option count exceeds the configured limit.");
            }

            var options = new List<string>(Math.Max(optionCount, 0));
            var selected = new List<int>();
            for (var optionIndex = 0; optionIndex < optionCount; optionIndex++)
            {
                options.Add(ReadUtf16((buffer, length) =>
                    PdfiumNative.FPDFAnnot_GetOptionLabel(
                        form.Handle,
                        annotation.Handle,
                        optionIndex,
                        buffer,
                        length)));
                if (PdfiumNative.FPDFAnnot_IsOptionSelected(form.Handle, annotation.Handle, optionIndex) != 0)
                {
                    selected.Add(optionIndex);
                }
            }

            var isChecked = PdfiumNative.FPDFAnnot_IsChecked(form.Handle, annotation.Handle) != 0;
            if (!isChecked && nativeType is PdfiumNative.FormFieldCheckBox or PdfiumNative.FormFieldRadioButton)
            {
                isChecked = !string.IsNullOrWhiteSpace(value)
                    && !string.Equals(value, "Off", StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(exportValue)
                        || string.Equals(value, exportValue, StringComparison.OrdinalIgnoreCase));
            }

            fields.Add(new PdfiumFormFieldInfo(
                annotationIndex,
                nativeType,
                name,
                value,
                exportValue,
                NormalizeRectangle(rectangle),
                flags,
                options,
                selected,
                isChecked,
                HasUnsafeFormAction(form, annotation),
                HasAnnotationKey(annotation, "Parent")));
        }

        return fields;
    }

    public PdfiumAnnotationHandle? GetPageAnnotation(PdfiumPageHandle page, int annotationIndex)
    {
        ValidateOwner(page);
        ArgumentOutOfRangeException.ThrowIfNegative(annotationIndex);
        var annotation = PdfiumNative.FPDFPage_GetAnnot(page.Handle, annotationIndex);
        return annotation == 0 ? null : new PdfiumAnnotationHandle(this, annotation);
    }

    public int GetPageAnnotationCount(PdfiumPageHandle page)
    {
        ValidateOwner(page);
        return PdfiumNative.FPDFPage_GetAnnotCount(page.Handle);
    }

    public int GetAnnotationSubtype(PdfiumAnnotationHandle annotation)
    {
        ValidateOwner(annotation);
        return PdfiumNative.FPDFAnnot_GetSubtype(annotation.Handle);
    }

    public PdfiumAnnotationHandle CreatePageAnnotation(PdfiumPageHandle page, int subtype)
    {
        ValidateOwner(page);
        if (PdfiumNative.FPDFAnnot_IsSupportedSubtype(subtype) == 0)
        {
            throw new NotSupportedException($"PDFium cannot create annotation subtype {subtype}.");
        }

        var handle = PdfiumNative.FPDFPage_CreateAnnot(page.Handle, subtype);
        return handle != 0
            ? new PdfiumAnnotationHandle(this, handle)
            : throw CreateException("PDFium could not create an annotation.");
    }

    public bool SetAnnotationRectangle(
        PdfiumAnnotationHandle annotation,
        float left,
        float bottom,
        float right,
        float top)
    {
        ValidateOwner(annotation);
        if (!float.IsFinite(left) || !float.IsFinite(bottom)
            || !float.IsFinite(right) || !float.IsFinite(top)
            || right <= left || top <= bottom)
        {
            throw new ArgumentOutOfRangeException(nameof(right));
        }

        var rectangle = new FsRectF { Left = left, Bottom = bottom, Right = right, Top = top };
        return PdfiumNative.FPDFAnnot_SetRect(annotation.Handle, in rectangle) != 0;
    }

    public bool SetAnnotationColor(
        PdfiumAnnotationHandle annotation,
        byte red,
        byte green,
        byte blue,
        byte alpha) =>
        SetAnnotationColorCore(annotation, red, green, blue, alpha);

    private bool SetAnnotationColorCore(
        PdfiumAnnotationHandle annotation,
        uint red,
        uint green,
        uint blue,
        uint alpha)
    {
        ValidateOwner(annotation);
        return PdfiumNative.FPDFAnnot_SetColor(
            annotation.Handle,
            0,
            red,
            green,
            blue,
            alpha) != 0;
    }

    public bool SetAnnotationBorder(PdfiumAnnotationHandle annotation, float width)
    {
        ValidateOwner(annotation);
        if (!float.IsFinite(width) || width <= 0 || width > 128)
            throw new ArgumentOutOfRangeException(nameof(width));
        return PdfiumNative.FPDFAnnot_SetBorder(annotation.Handle, 0, 0, width) != 0;
    }

    public bool SetAnnotationPrintable(PdfiumAnnotationHandle annotation)
    {
        ValidateOwner(annotation);
        return PdfiumNative.FPDFAnnot_SetFlags(annotation.Handle, PdfiumNative.AnnotationFlagPrint) != 0;
    }

    public unsafe int AddInkStroke(PdfiumAnnotationHandle annotation, IReadOnlyList<(float X, float Y)> points)
    {
        ValidateOwner(annotation);
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count is < 2 or > 32_768)
            throw new ArgumentOutOfRangeException(nameof(points));
        var nativePoints = new FsPointF[points.Count];
        for (var index = 0; index < points.Count; index++)
        {
            var (x, y) = points[index];
            if (!float.IsFinite(x) || !float.IsFinite(y))
                throw new ArgumentOutOfRangeException(nameof(points));
            nativePoints[index] = new FsPointF { X = x, Y = y };
        }

        fixed (FsPointF* pointer = nativePoints)
        {
            return PdfiumNative.FPDFAnnot_AddInkStroke(
                annotation.Handle,
                pointer,
                checked((nuint)nativePoints.Length));
        }
    }

    public PdfiumPageObjectHandle CreateStrokedPath(
        IReadOnlyList<(float X, float Y)> points,
        byte red,
        byte green,
        byte blue,
        byte alpha,
        float width)
    {
        AssertUsable();
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count is < 2 or > 32_768)
            throw new ArgumentOutOfRangeException(nameof(points));
        if (!float.IsFinite(width) || width is <= 0 or > 128)
            throw new ArgumentOutOfRangeException(nameof(width));
        var (initialX, initialY) = points[0];
        if (!float.IsFinite(initialX) || !float.IsFinite(initialY))
            throw new ArgumentOutOfRangeException(nameof(points));
        var handle = PdfiumNative.FPDFPageObj_CreateNewPath(initialX, initialY);
        if (handle == 0)
            throw CreateException("PDFium could not create an ink appearance path.");
        var owner = new PdfiumPageObjectHandle(this, handle);
        try
        {
            for (var index = 1; index < points.Count; index++)
            {
                var (x, y) = points[index];
                if (!float.IsFinite(x) || !float.IsFinite(y))
                    throw new ArgumentOutOfRangeException(nameof(points));
                if (PdfiumNative.FPDFPath_LineTo(handle, x, y) == 0)
                    throw CreateException("PDFium could not append an ink appearance segment.");
            }
            if (PdfiumNative.FPDFPath_SetDrawMode(handle, 0, 1) == 0
                || PdfiumNative.FPDFPageObj_SetStrokeColor(handle, red, green, blue, alpha) == 0
                || PdfiumNative.FPDFPageObj_SetStrokeWidth(handle, width) == 0
                || PdfiumNative.FPDFPageObj_SetLineJoin(handle, 1) == 0
                || PdfiumNative.FPDFPageObj_SetLineCap(handle, 1) == 0)
            {
                throw CreateException("PDFium could not configure the ink appearance path.");
            }
            return owner;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    public PdfiumPageObjectHandle CreateTextObject(
        PdfiumDocumentHandle document,
        string standardFont,
        float fontSize,
        string text,
        byte red,
        byte green,
        byte blue,
        byte alpha = byte.MaxValue)
    {
        ValidateOwner(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(standardFont);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (standardFont.Length > 64 || standardFont.Any(static character => character > 0x7f))
            throw new ArgumentOutOfRangeException(nameof(standardFont));
        if (!float.IsFinite(fontSize) || fontSize is < 4 or > 512)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        var font = Encoding.ASCII.GetBytes(standardFont + '\0');
        var handle = PdfiumNative.FPDFPageObj_NewTextObj(document.Handle, font, fontSize);
        if (handle == 0)
            throw CreateException("PDFium could not create a text object.");
        var owner = new PdfiumPageObjectHandle(this, handle);
        try
        {
            var wideText = text.Append('\0').Select(static character => (ushort)character).ToArray();
            if (PdfiumNative.FPDFText_SetText(handle, wideText) == 0
                || PdfiumNative.FPDFPageObj_SetFillColor(handle, red, green, blue, alpha) == 0)
            {
                throw CreateException("PDFium could not initialize a text object.");
            }
            return owner;
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    public bool AppendAnnotationObject(
        PdfiumAnnotationHandle annotation,
        PdfiumPageObjectHandle pageObject)
    {
        ValidateOwner(annotation);
        ValidateOwner(pageObject);
        if (PdfiumNative.FPDFAnnot_AppendObject(annotation.Handle, pageObject.Handle) == 0)
            return false;
        _ = pageObject.Detach();
        return true;
    }

    public string GetAnnotationStringValue(PdfiumAnnotationHandle annotation, string key)
    {
        ValidateOwner(annotation);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 64 || key.Any(static character => character > 0x7f))
            throw new ArgumentOutOfRangeException(nameof(key));
        var keyBytes = Encoding.ASCII.GetBytes(key + '\0');
        return ReadUtf16((buffer, length) =>
            PdfiumNative.FPDFAnnot_GetStringValue(annotation.Handle, keyBytes, buffer, length));
    }

    public void FlattenPage(PdfiumPageHandle page)
    {
        ValidateOwner(page);
        if (PdfiumNative.FPDFPage_Flatten(page.Handle, 0) == 0)
            throw CreateException("PDFium could not flatten the page annotations.");
    }

    public bool SetAnnotationStringValue(PdfiumAnnotationHandle annotation, string key, string value)
    {
        ValidateOwner(annotation);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        if (key.Length > 64 || value.Length > 16 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(key.Length > 64 ? nameof(key) : nameof(value));
        }

        var keyBytes = Encoding.UTF8.GetBytes(key + '\0');
        var wideValue = value.Append('\0').Select(static character => (ushort)character).ToArray();
        return PdfiumNative.FPDFAnnot_SetStringValue(annotation.Handle, keyBytes, wideValue) != 0;
    }

    /// <summary>
    /// Returns true for any widget action. ElliePdf never lets PDFium dispatch a
    /// widget action: form-fill callbacks are deliberately null and the worker is
    /// AppContainer/no-network. Treating every /A and /AA as unsafe also blocks
    /// file, shell, URI, submit and future action types that PDFium might add.
    /// </summary>
    public bool HasUnsafeFormAction(PdfiumFormHandle form, PdfiumAnnotationHandle annotation)
    {
        ValidateOwner(form);
        ValidateOwner(annotation);
        if (HasAnnotationKey(annotation, "A") || HasAnnotationKey(annotation, "AA"))
        {
            return true;
        }

        for (var eventType = 12; eventType <= 15; eventType++)
        {
            if (PdfiumNative.FPDFAnnot_GetFormAdditionalActionJavaScript(
                    form.Handle,
                    annotation.Handle,
                    eventType,
                    null,
                    0) > 2)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Delivers a pointer activation only after the caller has verified that the
    /// widget is an actionless push button. No string value is written and no
    /// PDFium action callback is supplied by ElliePdf.
    /// </summary>
    public void ActivateActionlessPushButton(
        PdfiumPageHandle page,
        PdfiumFormHandle form,
        PdfiumAnnotationHandle annotation,
        PdfiumRectangle bounds)
    {
        ValidateOwner(page);
        ValidateOwner(form);
        ValidateOwner(annotation);
        if (HasUnsafeFormAction(form, annotation))
        {
            throw new UnauthorizedAccessException("PDF push buttons with actions are blocked.");
        }

        var x = (bounds.Left + bounds.Right) / 2d;
        var y = (bounds.Top + bounds.Bottom) / 2d;
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new PdfiumResourceLimitException("The PDF push button has invalid geometry.");
        }

        PdfiumNative.FORM_OnAfterLoadPage(page.Handle, form.Handle);
        try
        {
            // PDFium reports false for an actionless button that has no visual
            // appearance stream. The pointer sequence was still delivered; its
            // return value is not an authorization or value-mutation signal.
            _ = PdfiumNative.FORM_OnLButtonDown(form.Handle, page.Handle, 0, x, y);
            _ = PdfiumNative.FORM_OnLButtonUp(form.Handle, page.Handle, 0, x, y);
        }
        finally
        {
            PdfiumNative.FORM_OnBeforeClosePage(page.Handle, form.Handle);
        }
    }

    private static bool HasAnnotationKey(PdfiumAnnotationHandle annotation, string key)
    {
        var keyBytes = Encoding.ASCII.GetBytes(key + '\0');
        return PdfiumNative.FPDFAnnot_HasKey(annotation.Handle, keyBytes) != 0;
    }

    public void CopyViewerPreferences(PdfiumDocumentHandle destination, PdfiumDocumentHandle source)
    {
        ValidateOwner(destination);
        ValidateOwner(source);
        PdfiumNative.FPDF_CopyViewerPreferences(destination.Handle, source.Handle);
    }

    private static PdfiumRectangle NormalizeRectangle(FsRectF rectangle) => new(
        Math.Min(rectangle.Left, rectangle.Right),
        Math.Min(rectangle.Top, rectangle.Bottom),
        Math.Max(rectangle.Left, rectangle.Right),
        Math.Max(rectangle.Top, rectangle.Bottom));

    private static string ReadUtf16(Func<byte[]?, uint, uint> read)
    {
        var length = read(null, 0);
        if (length <= 2)
        {
            return string.Empty;
        }
        if (length > 2 * 1024 * 1024 || (length & 1) != 0)
        {
            throw new PdfiumResourceLimitException("A PDF string exceeds the configured limit.");
        }

        var buffer = new byte[length];
        var written = read(buffer, length);
        if (written != length)
        {
            return string.Empty;
        }

        return Encoding.Unicode.GetString(buffer, 0, checked((int)length - 2));
    }

    private static string ReadUtf8(Func<byte[]?, uint, uint> read)
    {
        var length = read(null, 0);
        if (length <= 1)
        {
            return string.Empty;
        }
        if (length > 2 * 1024 * 1024)
        {
            throw new PdfiumResourceLimitException("A PDF URI exceeds the configured limit.");
        }

        var buffer = new byte[length];
        var written = read(buffer, length);
        if (written != length)
        {
            return string.Empty;
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(buffer, 0, checked((int)length - 1));
        }
        catch (DecoderFallbackException)
        {
            return string.Empty;
        }
    }

    public bool ImportPages(
        PdfiumDocumentHandle destination,
        PdfiumDocumentHandle source,
        int[] pageIndices,
        int destinationIndex)
    {
        ValidateOwner(destination);
        ValidateOwner(source);
        ArgumentNullException.ThrowIfNull(pageIndices);
        return PdfiumNative.FPDF_ImportPagesByIndex(
            destination.Handle,
            source.Handle,
            pageIndices,
            checked((uint)pageIndices.Length),
            destinationIndex) != 0;
    }

    public PdfiumPageObjectHandle CreateImageObject(PdfiumDocumentHandle document)
    {
        ValidateOwner(document);
        var handle = PdfiumNative.FPDFPageObj_NewImageObj(document.Handle);
        return handle != 0
            ? new PdfiumPageObjectHandle(this, handle)
            : throw CreateException("PDFium could not create an image object.");
    }

    public void SetPageObjectMatrix(
        PdfiumPageObjectHandle pageObject,
        double a,
        double b,
        double c,
        double d,
        double e,
        double f)
    {
        ValidateOwner(pageObject);
        PdfiumNative.FPDFPageObj_SetMatrix(pageObject.Handle, a, b, c, d, e, f);
    }

    public unsafe bool SetImageBitmap(
        PdfiumPageHandle page,
        PdfiumPageObjectHandle imageObject,
        PdfiumBitmapHandle bitmap)
    {
        ValidateOwner(page);
        ValidateOwner(imageObject);
        ValidateOwner(bitmap);
        var pagePointer = page.Handle;
        return PdfiumNative.FPDFImageObj_SetBitmap(
            &pagePointer,
            1,
            imageObject.Handle,
            bitmap.Handle) != 0;
    }

    public bool InsertPageObject(PdfiumPageHandle page, PdfiumPageObjectHandle pageObject)
    {
        ValidateOwner(page);
        ValidateOwner(pageObject);
        if (PdfiumNative.FPDFPage_InsertObject(page.Handle, pageObject.Handle) == 0)
        {
            return false;
        }

        _ = pageObject.Detach();
        return true;
    }

    public void SaveAsCopy(PdfiumDocumentHandle document, Stream output)
    {
        ValidateOwner(document);
        ArgumentNullException.ThrowIfNull(output);
        var callback = new FpdfWriteBlockCallback((_, data, size) => WriteBlock(output, data, size));
        var fileWrite = new FpdfFileWrite
        {
            Version = 1,
            WriteBlock = Marshal.GetFunctionPointerForDelegate(callback)
        };

        try
        {
            if (PdfiumNative.FPDF_SaveAsCopy(
                    document.Handle,
                    ref fileWrite,
                    PdfiumNative.SaveWithoutIncremental) == 0)
            {
                throw CreateException("PDFium could not serialize the document.");
            }
        }
        finally
        {
            GC.KeepAlive(callback);
        }
    }

    public PdfiumNativeException CreateException(string message) => new(message, LastError);

    public void Dispose()
    {
        AssertEngineLane();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var index = _activeOwners.Count - 1; index >= 0; index--)
        {
            _activeOwners[index].Dispose();
        }

        if (_initialized)
        {
            var handle = _libraryHandle;
            if (handle == 0)
            {
                throw new InvalidOperationException("The initialized PDFium engine has no library handle.");
            }

            PdfiumNative.ReleaseLoadedLibrary(handle);
            _initialized = false;
        }

        if (_libraryHandle != 0)
        {
            _libraryHandle = 0;
        }
    }

    internal void AssertEngineLane()
    {
        if (Environment.CurrentManagedThreadId != _engineThreadId)
        {
            throw new InvalidOperationException(
                "PDFium native resources may be used or disposed only on their owning engine lane.");
        }
    }

    internal void RegisterOwner(PdfiumNativeOwner owner)
    {
        AssertEngineLane();
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PdfiumEngine));
        }

        _activeOwners.Add(owner);
    }

    internal void UnregisterOwner(PdfiumNativeOwner owner)
    {
        AssertEngineLane();
        _ = _activeOwners.Remove(owner);
    }

    private void LoadVerifiedLibrary(string? baseDirectory)
    {
        using var verifiedAsset = PdfiumAssetVerifier.OpenVerifiedAppPrivateAsset(baseDirectory);
        var path = PdfiumAssetVerifier.GetAppPrivatePath(baseDirectory);
        var handle = PdfiumNative.LoadLibraryEx(
            path,
            0,
            LoadLibrarySearchDllLoadDir | LoadLibrarySearchSystem32);
        if (handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Unable to load the verified app-private PDFium library.");
        }

        try
        {
            PdfiumNative.AcquireLoadedLibrary(handle);
            _libraryHandle = handle;
            _initialized = true;
        }
        catch
        {
            // AcquireLoadedLibrary owns the module after it succeeds. If it did
            // not, this constructor is the only owner and may release the raw
            // module handle directly.
            if (_libraryHandle == handle)
            {
                PdfiumNative.ReleaseLoadedLibrary(handle);
                _libraryHandle = 0;
            }
            else
            {
                _ = PdfiumNative.FreeLibrary(handle);
            }

            throw;
        }
    }

    private void AssertUsable()
    {
        AssertEngineLane();
        ThrowIfDisposed();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void ValidateOwner(PdfiumNativeOwner owner)
    {
        AssertUsable();
        ArgumentNullException.ThrowIfNull(owner);
        if (!ReferenceEquals(owner.Engine, this))
        {
            throw new InvalidOperationException("The native owner belongs to a different PDFium engine.");
        }

        _ = owner.Handle;
    }

    private void ValidateBookmark(PdfiumBookmark bookmark, bool allowNull = true)
    {
        AssertUsable();
        if (bookmark.IsNull)
        {
            if (!allowNull)
            {
                throw new ArgumentException("A non-null bookmark is required.", nameof(bookmark));
            }

            return;
        }

        if (!ReferenceEquals(bookmark.Engine, this))
        {
            throw new InvalidOperationException("The bookmark belongs to a different PDFium engine.");
        }
    }

    private static int WriteBlock(Stream output, nint data, uint size)
    {
        try
        {
            var length = checked((int)size);
            var buffer = GC.AllocateUninitializedArray<byte>(length);
            Marshal.Copy(data, buffer, 0, length);
            output.Write(buffer, 0, length);
            return 1;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OverflowException)
        {
            return 0;
        }
    }

    private sealed class BrokeredFileAccess : IDisposable
    {
        private static readonly FpdfGetBlockCallback GetBlockCallback = ReadBlock;

        private SafeFileHandle? _handle;
        private GCHandle _selfHandle;
        private nint _pointer;

        private BrokeredFileAccess(SafeFileHandle handle)
        {
            _handle = handle;
        }

        internal nint Pointer => _pointer;

        internal static BrokeredFileAccess Create(SafeFileHandle handle)
        {
            var length = RandomAccess.GetLength(handle);
            if (length is < 0 or > uint.MaxValue)
            {
                handle.Dispose();
                throw new PdfiumResourceLimitException(
                    "The brokered PDF exceeds PDFium's 4 GiB custom-access length limit.");
            }

            var access = new BrokeredFileAccess(handle);
            try
            {
                access._selfHandle = GCHandle.Alloc(access, GCHandleType.Normal);
                var native = new FpdfFileAccess
                {
                    FileLength = checked((uint)length),
                    GetBlock = Marshal.GetFunctionPointerForDelegate(GetBlockCallback),
                    Parameter = GCHandle.ToIntPtr(access._selfHandle)
                };
                access._pointer = Marshal.AllocHGlobal(Marshal.SizeOf<FpdfFileAccess>());
                Marshal.StructureToPtr(native, access._pointer, false);
                return access;
            }
            catch
            {
                access.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            var pointer = Interlocked.Exchange(ref _pointer, 0);
            if (pointer != 0)
            {
                Marshal.FreeHGlobal(pointer);
            }

            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }

            Interlocked.Exchange(ref _handle, null)?.Dispose();
        }

        private static unsafe int ReadBlock(
            nint parameter,
            uint position,
            nint outputBuffer,
            uint size)
        {
            try
            {
                if (parameter == 0 || outputBuffer == 0 || size > int.MaxValue)
                {
                    return 0;
                }

                var self = GCHandle.FromIntPtr(parameter).Target as BrokeredFileAccess;
                var handle = self?._handle;
                if (handle is null || handle.IsInvalid || handle.IsClosed)
                {
                    return 0;
                }

                var destination = new Span<byte>((void*)outputBuffer, checked((int)size));
                var totalRead = 0;
                while (totalRead < destination.Length)
                {
                    var read = RandomAccess.Read(
                        handle,
                        destination[totalRead..],
                        checked((long)position + totalRead));
                    if (read == 0)
                    {
                        return 0;
                    }

                    totalRead += read;
                }

                return 1;
            }
            catch
            {
                return 0;
            }
        }
    }
}

public sealed class PdfiumNativeException : InvalidOperationException
{
    public PdfiumNativeException(string message, uint errorCode)
        : base(errorCode == 0 ? message : $"{message} PDFium error code: 0x{errorCode:X8}.")
    {
        ErrorCode = errorCode;
    }

    public uint ErrorCode { get; }
}

public sealed class PdfiumResourceLimitException : IOException
{
    public PdfiumResourceLimitException(string message)
        : base(message)
    {
    }
}
