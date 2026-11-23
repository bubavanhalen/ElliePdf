using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;

namespace ElliePdf.Pdf.Transport;

public enum WorkerOperation
{
    OpenDocument = 0,
    GetMetadata = 1,
    GetPageMetadata = 2,
    Render = 3,
    GetPageText = 4,
    SearchPage = 5,
    GetOutline = 6,
    GetPageLinks = 7,
    GetFormWidgets = 8,
    GetPermissions = 9,
    ApplyFormValue = 10,
    InvokePushButton = 11,
    RotatePage = 12,
    DeletePage = 13,
    MergeOrderedPages = 14,
    SaveDocument = 15,
    CloseDocument = 16,
    Shutdown = 17,
    StageAnnotations = 18,
    FinalizeAnnotationTransaction = 19,
    SaveFlattenedCopy = 20
}

public sealed record WorkerRequestPayload(WorkerOperation Operation, JsonElement Arguments)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Operation))
            throw new TransportProtocolException("The worker operation is unsupported.");
        if (Arguments.ValueKind == JsonValueKind.Undefined)
            throw new TransportProtocolException("Worker request arguments are required.");
    }
}

public sealed record WorkerResponsePayload(WorkerOperation Operation, JsonElement Result)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Operation))
            throw new TransportProtocolException("The worker operation is unsupported.");
        if (Result.ValueKind == JsonValueKind.Undefined)
            throw new TransportProtocolException("Worker response content is required.");
    }
}

public sealed record OpenDocumentCommand(
    DocumentOpenRequest Request,
    BrokeredHandleDescriptor SourceHandle);

public sealed record DocumentCommand(DocumentId DocumentId);

public sealed record PageMetadataCommand(DocumentId DocumentId, int PageIndex);

public sealed record RenderCommand(RenderRequest Request);

public sealed record PageTextCommand(PageTextRequest Request);

public sealed record SearchPageCommand(PageSearchRequest Request);

public sealed record ApplyFormValueCommand(FormValueChange Change);

public sealed record InvokePushButtonCommand(PushButtonInvocation Invocation);

public sealed record RotatePageCommand(RotatePageRequest Request);

public sealed record DeletePageCommand(DeletePageRequest Request);

public sealed record MergeOrderedPagesCommand(
    MergeOrderedPagesRequest Request,
    BrokeredHandleDescriptor TargetHandle);

public sealed record StageAnnotationsCommand(
    PdfAnnotationSaveRequest Request,
    BrokeredHandleDescriptor TargetHandle);

public sealed record FinalizeAnnotationTransactionCommand(
    DocumentId DocumentId,
    Guid TransactionId,
    bool Committed);

public sealed record SaveFlattenedCopyCommand(
    PdfAnnotationSaveRequest Request,
    BrokeredHandleDescriptor TargetHandle);

public sealed record SaveDocumentCommand(
    DocumentId DocumentId,
    ContentRevision CapturedRevision,
    BrokeredHandleDescriptor TargetHandle);

public sealed record OpenDocumentResponse(DocumentOpenResult Result);

public sealed record MetadataResponse(PdfMetadata Metadata);

public sealed record PageMetadataResponse(PageMetadata Metadata);

public sealed record RenderLeaseResponse(SharedMemoryLeaseMetadata Lease);

public sealed record PageTextResponse(PageTextResult Result);

public sealed record SearchPageResponse(ImmutableArray<SearchResult> Results);

public sealed record OutlineResponse(OutlineResult Outline);

public sealed record PageLinksResponse(PageLinks Links);

public sealed record FormWidgetsResponse(FormWidgetsResult Forms);

public sealed record PermissionsResponse(PdfPermissions Permissions);

public sealed record DocumentMutationResponse(DocumentSnapshot Snapshot);

public sealed record AcknowledgementResponse(bool Accepted);

public sealed record EmptyPayload;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(WorkerRequestPayload))]
[JsonSerializable(typeof(WorkerResponsePayload))]
[JsonSerializable(typeof(OpenDocumentCommand))]
[JsonSerializable(typeof(DocumentCommand))]
[JsonSerializable(typeof(PageMetadataCommand))]
[JsonSerializable(typeof(RenderCommand))]
[JsonSerializable(typeof(PageTextCommand))]
[JsonSerializable(typeof(SearchPageCommand))]
[JsonSerializable(typeof(ApplyFormValueCommand))]
[JsonSerializable(typeof(InvokePushButtonCommand))]
[JsonSerializable(typeof(RotatePageCommand))]
[JsonSerializable(typeof(DeletePageCommand))]
[JsonSerializable(typeof(MergeOrderedPagesCommand))]
[JsonSerializable(typeof(StageAnnotationsCommand))]
[JsonSerializable(typeof(FinalizeAnnotationTransactionCommand))]
[JsonSerializable(typeof(SaveFlattenedCopyCommand))]
[JsonSerializable(typeof(SaveDocumentCommand))]
[JsonSerializable(typeof(OpenDocumentResponse))]
[JsonSerializable(typeof(MetadataResponse))]
[JsonSerializable(typeof(PageMetadataResponse))]
[JsonSerializable(typeof(RenderLeaseResponse))]
[JsonSerializable(typeof(PageTextResponse))]
[JsonSerializable(typeof(SearchPageResponse))]
[JsonSerializable(typeof(OutlineResponse))]
[JsonSerializable(typeof(PageLinksResponse))]
[JsonSerializable(typeof(FormWidgetsResponse))]
[JsonSerializable(typeof(PermissionsResponse))]
[JsonSerializable(typeof(DocumentMutationResponse))]
[JsonSerializable(typeof(AcknowledgementResponse))]
[JsonSerializable(typeof(EmptyPayload))]
[JsonSerializable(typeof(DocumentOpenRequest))]
[JsonSerializable(typeof(DocumentOpenResult))]
[JsonSerializable(typeof(PdfMetadata))]
[JsonSerializable(typeof(PageMetadata))]
[JsonSerializable(typeof(RenderRequest))]
[JsonSerializable(typeof(PageTextRequest))]
[JsonSerializable(typeof(PageTextResult))]
[JsonSerializable(typeof(PageSearchRequest))]
[JsonSerializable(typeof(SearchResult))]
[JsonSerializable(typeof(OutlineResult))]
[JsonSerializable(typeof(PdfPermissions))]
[JsonSerializable(typeof(PageLinks))]
[JsonSerializable(typeof(PdfLink))]
[JsonSerializable(typeof(FormWidgetsResult))]
[JsonSerializable(typeof(FormWidget))]
[JsonSerializable(typeof(FormValueChange))]
[JsonSerializable(typeof(PushButtonInvocation))]
[JsonSerializable(typeof(FormValue))]
[JsonSerializable(typeof(RotatePageRequest))]
[JsonSerializable(typeof(DeletePageRequest))]
[JsonSerializable(typeof(PageMergeReference))]
[JsonSerializable(typeof(MergeOrderedPagesRequest))]
[JsonSerializable(typeof(PdfOverlayColor))]
[JsonSerializable(typeof(PdfOverlayPoint))]
[JsonSerializable(typeof(PdfOverlayRectangle))]
[JsonSerializable(typeof(PdfInkAnnotation))]
[JsonSerializable(typeof(PdfTextStampAnnotation))]
[JsonSerializable(typeof(PdfSignatureStampAnnotation))]
[JsonSerializable(typeof(PdfPageOverlayBatch))]
[JsonSerializable(typeof(PdfAnnotationSaveRequest))]
[JsonSerializable(typeof(SharedMemoryLeaseMetadata))]
public partial class WorkerProtocolJsonContext : JsonSerializerContext;
