using System.Runtime.InteropServices;

namespace ElliePdf.Services;

internal sealed class PdfFormFillContext : IDisposable
{
    private GCHandle _formInfoHandle;
    private bool _disposed;

    public IntPtr FormHandle { get; private set; }

    public static PdfFormFillContext? TryCreate(IntPtr document)
    {
        if (document == IntPtr.Zero)
        {
            return null;
        }

        var context = new PdfFormFillContext();
        var formInfo = new FpdfFormFillInfo();

        for (var version = 1; version <= 2; version++)
        {
            formInfo.Version = version;
            context._formInfoHandle = GCHandle.Alloc(formInfo, GCHandleType.Pinned);

            context.FormHandle = PdfiumNative.FPDFDOC_InitFormFillEnvironment(
                document,
                context._formInfoHandle.AddrOfPinnedObject());

            if (context.FormHandle != IntPtr.Zero)
            {
                return context;
            }

            context._formInfoHandle.Free();
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (FormHandle != IntPtr.Zero)
        {
            PdfiumNative.FPDFDOC_ExitFormFillEnvironment(FormHandle);
            FormHandle = IntPtr.Zero;
        }

        if (_formInfoHandle.IsAllocated)
        {
            _formInfoHandle.Free();
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct FpdfFormFillInfo
{
    public int Version;
    public IntPtr Release;
    public IntPtr Invalidate;
    public IntPtr OutputSelectedRect;
    public IntPtr SetCursor;
    public IntPtr SetTimer;
    public IntPtr KillTimer;
    public IntPtr GetLocalTime;
    public IntPtr OnChange;
    public IntPtr GetPage;
    public IntPtr GetCurrentPage;
    public IntPtr GetRotation;
    public IntPtr ExecuteNamedAction;
    public IntPtr SetTextFieldFocus;
    public IntPtr DoUriAction;
    public IntPtr DoGoToAction;
    public IntPtr JsPlatform;
    public IntPtr DisplayCaret;
    public IntPtr GetCurrentPageIndex;
    public IntPtr SetCurrentPage;
    public IntPtr GotoUrl;
    public IntPtr GetPageViewRect;
    public IntPtr PageEvent;
    public IntPtr PopupMenu;
    public IntPtr OpenFile;
    public IntPtr EmailTo;
    public IntPtr UploadTo;
    public IntPtr GetPlatform;
    public IntPtr GetLanguage;
    public IntPtr DownloadFromUrl;
    public IntPtr PostRequestUrl;
    public IntPtr PutRequestUrl;
}
