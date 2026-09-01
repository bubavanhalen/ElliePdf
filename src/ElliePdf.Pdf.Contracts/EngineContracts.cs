using ElliePdf.Domain.Documents;

namespace ElliePdf.Pdf.Contracts;

public sealed record RenderRequest
{
    public RenderRequest(RenderKey key, RenderGeneration generation, RenderQuality quality, EngineJobPriority priority, DateTimeOffset deadline)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        if (key.DocumentId.Value == Guid.Empty) throw new ArgumentException("The render key must have a document id.", nameof(key));
        if (key.PageId.Value == Guid.Empty) throw new ArgumentException("The render key must have a page id.", nameof(key));
        key.Tile.Validate();
        if (key.Tile.InteriorWidth > 512 || key.Tile.InteriorHeight > 512 || key.Tile.BleedPixels > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(key), "Render tiles are limited to a 512x512 interior and one-pixel bleed.");
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(key.RasterScale.Value, nameof(key));
        if (deadline == default) throw new ArgumentException("A render deadline is required.", nameof(deadline));
        Generation = generation;
        Quality = quality;
        Priority = priority;
        Deadline = deadline;
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;
    public RenderKey Key { get; }
    public RenderGeneration Generation { get; }
    public RenderQuality Quality { get; }
    public EngineJobPriority Priority { get; }
    public DateTimeOffset Deadline { get; }
}

public interface IPixelBufferLease : IAsyncDisposable
{
    Guid LeaseId { get; }
    string SharedMemoryId { get; }
    long Offset { get; }
    int ByteLength { get; }
    int Width { get; }
    int Height { get; }
    int Stride { get; }
    PixelFormat Format { get; }
    RenderKey Key { get; }
}

public interface IReadablePixelBufferLease : IPixelBufferLease
{
    Stream OpenReadStream();
}

/// <summary>Default lease implementation. Disposal invokes the release operation at most once.</summary>
public sealed class PixelBufferLease : IPixelBufferLease
{
    private readonly Func<ValueTask>? _release;
    private int _released;

    public PixelBufferLease(
        Guid leaseId,
        string sharedMemoryId,
        long offset,
        int byteLength,
        int width,
        int height,
        int stride,
        PixelFormat format,
        RenderKey key,
        Func<ValueTask>? release = null)
    {
        if (leaseId == Guid.Empty) throw new ArgumentException("The lease id must not be empty.", nameof(leaseId));
        PdfContractLimits.RequiredString(sharedMemoryId, PdfContractLimits.MaxStringLength, nameof(sharedMemoryId));
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (width is <= 0 or > PdfContractLimits.MaxPixelDimension) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is <= 0 or > PdfContractLimits.MaxPixelDimension) throw new ArgumentOutOfRangeException(nameof(height));
        if (stride < width * 4 || stride > PdfContractLimits.MaxPixelStride) throw new ArgumentOutOfRangeException(nameof(stride));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteLength);
        var minimumBytes = checked((long)stride * height);
        if (byteLength < minimumBytes || byteLength > PdfContractLimits.MaxPixelBufferBytes) throw new ArgumentOutOfRangeException(nameof(byteLength));
        if (format != PixelFormat.Bgra8Premultiplied) throw new ArgumentOutOfRangeException(nameof(format));
        Key = key ?? throw new ArgumentNullException(nameof(key));
        LeaseId = leaseId;
        SharedMemoryId = sharedMemoryId;
        Offset = offset;
        ByteLength = byteLength;
        Width = width;
        Height = height;
        Stride = stride;
        Format = format;
        _release = release;
    }

    public Guid LeaseId { get; }
    public string SharedMemoryId { get; }
    public long Offset { get; }
    public int ByteLength { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public PixelFormat Format { get; }
    public RenderKey Key { get; }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0 || _release is null)
        {
            return ValueTask.CompletedTask;
        }

        return _release();
    }
}

public interface IPdfEngineClient : IAsyncDisposable
{
    PdfContractVersion ContractVersion { get; }
    ValueTask<IPdfEngineSession> OpenSessionAsync(DocumentOpenRequest request, CancellationToken cancellationToken);
}

public interface IPdfEngineSession : IAsyncDisposable
{
    DocumentId DocumentId { get; }
    ValueTask<PdfMetadata> GetMetadataAsync(CancellationToken cancellationToken);
    ValueTask<PageMetadata> GetPageMetadataAsync(int pageIndex, CancellationToken cancellationToken);
    ValueTask<IPixelBufferLease> RenderAsync(RenderRequest request, CancellationToken cancellationToken);
    ValueTask<PageTextResult> GetPageTextAsync(PageTextRequest request, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<SearchResult>> SearchPageAsync(PageSearchRequest request, CancellationToken cancellationToken);
    ValueTask<OutlineResult> GetOutlineAsync(CancellationToken cancellationToken);
    ValueTask<PageLinks> GetPageLinksAsync(int pageIndex, CancellationToken cancellationToken);
    ValueTask<FormWidgetsResult> GetFormWidgetsAsync(int pageIndex, CancellationToken cancellationToken);
    ValueTask<PdfPermissions> GetPermissionsAsync(CancellationToken cancellationToken);
    ValueTask ApplyFormValueAsync(FormValueChange change, CancellationToken cancellationToken);
}

/// <summary>Optional capability for invoking actionless PDF push buttons.</summary>
public interface IPdfPushButtonSession
{
    ValueTask InvokePushButtonAsync(PushButtonInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>Optional persistence capability implemented by worker-backed mutable sessions.</summary>
public interface IPdfWritableEngineSession
{
    DocumentSnapshot Snapshot { get; }
    ValueTask SaveAsync(Stream temporaryOutput, ContentRevision capturedRevision, CancellationToken cancellationToken);
}

/// <summary>
/// Worker-only annotation persistence. A stage writes a candidate and retains
/// the mutation in the worker until the broker finalizes the destination
/// outcome. A non-source/aborted destination remains an unsaved in-memory edit;
/// stable annotation ids make a later retry idempotent without unsafe native undo.
/// </summary>
public interface IPdfAnnotationPersistenceSession
{
    DocumentSnapshot Snapshot { get; }

    ValueTask<DocumentSnapshot> StageAnnotationsAsync(
        PdfAnnotationSaveRequest request,
        Stream temporaryOutput,
        CancellationToken cancellationToken);

    ValueTask<DocumentSnapshot> FinalizeAnnotationTransactionAsync(
        Guid transactionId,
        bool committed,
        CancellationToken cancellationToken);

    ValueTask SaveFlattenedCopyAsync(
        PdfAnnotationSaveRequest request,
        Stream temporaryOutput,
        CancellationToken cancellationToken);
}

/// <summary>Labs-only persistent page operations exposed by a worker-backed session.</summary>
public interface IPdfPageMutationSession
{
    DocumentSnapshot Snapshot { get; }
    ValueTask<DocumentSnapshot> RotatePageAsync(RotatePageRequest request, CancellationToken cancellationToken);
    ValueTask<DocumentSnapshot> DeletePageAsync(DeletePageRequest request, CancellationToken cancellationToken);
}

/// <summary>Labs-only ordered page export into broker-owned transaction storage.</summary>
public interface IPdfPageMergeClient
{
    ValueTask MergeOrderedPagesAsync(
        MergeOrderedPagesRequest request,
        Stream temporaryOutput,
        CancellationToken cancellationToken);
}
