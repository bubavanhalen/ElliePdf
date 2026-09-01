namespace ElliePdf.Pdfium;

public abstract class PdfiumNativeOwner : IDisposable
{
    private nint _handle;

    internal PdfiumNativeOwner(PdfiumEngine engine, nint handle)
    {
        Engine = engine;
        _handle = handle != 0
            ? handle
            : throw new ArgumentException("A native handle is required.", nameof(handle));
        Engine.RegisterOwner(this);
    }

    internal PdfiumEngine Engine { get; }

    public bool IsClosed => Volatile.Read(ref _handle) == 0;

    internal nint Handle
    {
        get
        {
            Engine.AssertEngineLane();
            var handle = Volatile.Read(ref _handle);
            ObjectDisposedException.ThrowIf(handle == 0, this);
            return handle;
        }
    }

    public void Dispose()
    {
        Engine.AssertEngineLane();
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            try
            {
                ReleaseHandle(handle);
            }
            finally
            {
                Engine.UnregisterOwner(this);
            }
        }

        GC.SuppressFinalize(this);
    }

    internal nint Detach()
    {
        Engine.AssertEngineLane();
        var handle = Interlocked.Exchange(ref _handle, 0);
        ObjectDisposedException.ThrowIf(handle == 0, this);
        Engine.UnregisterOwner(this);
        return handle;
    }

    protected abstract void ReleaseHandle(nint handle);
}

public sealed class PdfiumDocumentHandle : PdfiumNativeOwner
{
    private IDisposable? _backingResource;

    internal PdfiumDocumentHandle(
        PdfiumEngine engine,
        nint handle,
        IDisposable? backingResource = null)
        : base(engine, handle)
    {
        _backingResource = backingResource;
    }

    protected override void ReleaseHandle(nint handle)
    {
        try
        {
            PdfiumNative.FPDF_CloseDocument(handle);
        }
        finally
        {
            Interlocked.Exchange(ref _backingResource, null)?.Dispose();
        }
    }
}

public sealed class PdfiumPageHandle : PdfiumNativeOwner
{
    internal PdfiumPageHandle(PdfiumEngine engine, nint handle)
        : base(engine, handle)
    {
    }

    protected override void ReleaseHandle(nint handle) => PdfiumNative.FPDF_ClosePage(handle);
}

public sealed class PdfiumBitmapHandle : PdfiumNativeOwner
{
    internal PdfiumBitmapHandle(
        PdfiumEngine engine,
        nint handle,
        int width,
        int height,
        int stride)
        : base(engine, handle)
    {
        Width = width;
        Height = height;
        Stride = stride;
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride { get; }

    public int ByteLength => checked(Stride * Height);

    protected override void ReleaseHandle(nint handle) => PdfiumNative.FPDFBitmap_Destroy(handle);
}

public sealed class PdfiumTextPageHandle : PdfiumNativeOwner
{
    internal PdfiumTextPageHandle(PdfiumEngine engine, nint handle)
        : base(engine, handle)
    {
    }

    protected override void ReleaseHandle(nint handle) => PdfiumNative.FPDFText_ClosePage(handle);
}

public sealed class PdfiumSearchHandle : PdfiumNativeOwner
{
    internal PdfiumSearchHandle(PdfiumEngine engine, nint handle)
        : base(engine, handle)
    {
    }

    protected override void ReleaseHandle(nint handle) => PdfiumNative.FPDFText_FindClose(handle);
}

public sealed class PdfiumPageObjectHandle : PdfiumNativeOwner
{
    internal PdfiumPageObjectHandle(PdfiumEngine engine, nint handle)
        : base(engine, handle)
    {
    }

    protected override void ReleaseHandle(nint handle) => PdfiumNative.FPDFPageObj_Destroy(handle);
}

public sealed class PdfiumAnnotationHandle : PdfiumNativeOwner
{
    internal PdfiumAnnotationHandle(PdfiumEngine engine, nint handle)
        : base(engine, handle)
    {
    }

    protected override void ReleaseHandle(nint handle) => PdfiumNative.FPDFPage_CloseAnnot(handle);
}

public sealed class PdfiumFormHandle : PdfiumNativeOwner
{
    private nint _formInfo;

    internal PdfiumFormHandle(PdfiumEngine engine, nint handle, nint formInfo)
        : base(engine, handle)
    {
        _formInfo = formInfo;
    }

    protected override void ReleaseHandle(nint handle)
    {
        try
        {
            PdfiumNative.FPDFDOC_ExitFormFillEnvironment(handle);
        }
        finally
        {
            var formInfo = Interlocked.Exchange(ref _formInfo, 0);
            if (formInfo != 0)
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(formInfo);
            }
        }
    }
}

public readonly struct PdfiumBookmark
{
    internal PdfiumBookmark(PdfiumEngine engine, nint handle)
    {
        Engine = engine;
        Handle = handle;
    }

    internal PdfiumEngine Engine { get; }

    internal nint Handle { get; }

    public bool IsNull => Handle == 0;
}
