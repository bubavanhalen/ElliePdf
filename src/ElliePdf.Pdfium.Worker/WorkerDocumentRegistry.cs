using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;
using ElliePdf.Pdfium;
using Microsoft.Win32.SafeHandles;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingCompositingMode = System.Drawing.Drawing2D.CompositingMode;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingImage = System.Drawing.Image;
using DrawingImageLockMode = System.Drawing.Imaging.ImageLockMode;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace ElliePdf.Pdfium.Worker;

public sealed class WorkerDocumentRegistry : IAsyncDisposable
{
    private const int MaximumTextCharactersPerPage = 16 * 1024 * 1024;
    private const int AnnotationStampSubtype = 13;
    private const int AnnotationInkSubtype = 15;
    private const int MaximumSignatureDimension = 2_048;
    private const int MaximumSignatureDecodedBytes = 1_048_576;
    private readonly PdfiumEngineLane _engineLane;
    private readonly Lock _sync = new();
    private readonly Dictionary<DocumentId, WorkerDocument> _documents = [];
    private bool _restartRequired;
    private bool _disposed;

    public WorkerDocumentRegistry(string? nativeBaseDirectory = null)
    {
        _engineLane = new PdfiumEngineLane(nativeBaseDirectory, "ElliePdf worker PDFium lane");
    }

    public Task Ready => _engineLane.Ready;

    public async ValueTask<DocumentOpenResult> OpenAsync(
        DocumentOpenRequest request,
        SafeFileHandle brokeredReadHandle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(brokeredReadHandle);
        request.ContractVersion.Validate();

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_restartRequired)
            {
                brokeredReadHandle.Dispose();
                throw new WorkerRestartRequiredException(
                    "This PDF worker was quarantined after an uncertain native mutation and must be restarted.");
            }
            if (_documents.ContainsKey(request.DocumentId))
            {
                brokeredReadHandle.Dispose();
                throw new InvalidOperationException("The document identity is already open in this worker.");
            }
        }

        WorkerDocument? workerDocument = null;
        try
        {
            workerDocument = await _engineLane.InvokeAsync(
                engine => CreateWorkerDocument(engine, request, brokeredReadHandle),
                cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _documents.Add(request.DocumentId, workerDocument);
            }

            return new DocumentOpenResult(
                CreateDocumentSnapshot(workerDocument),
                workerDocument.Metadata);
        }
        catch
        {
            if (workerDocument is not null)
            {
                await DisposeWorkerDocumentAsync(workerDocument).ConfigureAwait(false);
            }
            else if (!brokeredReadHandle.IsClosed)
            {
                brokeredReadHandle.Dispose();
            }

            throw;
        }
    }

    public ValueTask<PdfMetadata> GetMetadataAsync(
        DocumentId documentId,
        CancellationToken cancellationToken = default)
    {
        var document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(document.Metadata);
    }

    public ValueTask<PdfPermissions> GetPermissionsAsync(
        DocumentId documentId,
        CancellationToken cancellationToken = default)
    {
        var document = GetDocument(documentId);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(document.Permissions);
    }

    public async ValueTask<PageMetadata> GetPageMetadataAsync(
        DocumentId documentId,
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        var document = GetDocument(documentId);
        ValidatePageIndex(document, pageIndex);
        var pageIdentity = document.Pages[pageIndex];
        var pageState = await _engineLane.InvokeAsync(
            engine =>
            {
                using var page = engine.LoadPage(document.Handle, pageIndex)
                    ?? throw engine.CreateException($"Unable to load page {pageIndex + 1}.");
                return (Size: engine.GetPageSize(page), Rotation: engine.GetPageRotation(page));
            },
            cancellationToken).ConfigureAwait(false);

        if (!float.IsFinite(pageState.Size.Width)
            || !float.IsFinite(pageState.Size.Height)
            || pageState.Size.Width <= 0
            || pageState.Size.Height <= 0
            || pageState.Rotation is < 0 or > 3)
        {
            throw new PdfiumResourceLimitException("PDFium returned invalid page geometry.");
        }

        var bounds = new PdfRect(0, 0, pageState.Size.Width, pageState.Size.Height);

        return new PageMetadata(
            pageIdentity.PageId,
            pageIndex,
            new PageGeometry(bounds, bounds, (PageRotation)pageState.Rotation),
            (pageIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            pageIdentity.ContentRevision,
            pageIdentity.AppearanceRevision);
    }

    public async ValueTask<WorkerRenderedBuffer> RenderAsync(
        RenderRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var document = ValidateRenderIdentity(request);
        if (request.Deadline <= DateTimeOffset.UtcNow)
        {
            throw new TimeoutException("The render request deadline has expired.");
        }

        return await _engineLane.InvokeAsync(
            engine => RenderCore(engine, document, request, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PageTextResult> GetPageTextAsync(
        PageTextRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (document, pageIndex) = ValidatePageIdentity(
            request.DocumentId,
            request.PageId,
            request.PageIndex,
            request.ContentRevision);

        return await _engineLane.InvokeAsync(
            engine => ExtractPageText(engine, document, pageIndex, request, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<SearchResult>> SearchPageAsync(
        PageSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (document, pageIndex) = ValidatePageIdentity(
            request.Page.DocumentId,
            request.Page.PageId,
            request.Page.PageIndex,
            request.Page.ContentRevision);

        return await _engineLane.InvokeAsync<IReadOnlyList<SearchResult>>(
            engine => SearchPageCore(engine, document, pageIndex, request, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<OutlineResult> GetOutlineAsync(
        DocumentId documentId,
        CancellationToken cancellationToken = default)
    {
        var document = GetDocument(documentId);
        return await _engineLane.InvokeAsync(
            engine => new OutlineResult(ReadOutline(
                engine,
                document,
                engine.GetFirstBookmark(document.Handle),
                depth: 0,
                cancellationToken)),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PageLinks> GetPageLinksAsync(
        DocumentId documentId,
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        var document = GetDocument(documentId);
        ValidatePageIndex(document, pageIndex);
        var pageIdentity = document.Pages[pageIndex];
        return await _engineLane.InvokeAsync(
            engine =>
            {
                using var page = engine.LoadPage(document.Handle, pageIndex)
                    ?? throw engine.CreateException($"Unable to load page {pageIndex + 1}.");
                var links = new List<PdfLink>();
                foreach (var link in engine.GetPageLinks(document.Handle, page, PdfContractLimits.MaxCollectionCount))
                {
                    var bounds = new PdfRect(link.Bounds.Left, link.Bounds.Top, link.Bounds.Right, link.Bounds.Bottom);
                    if (link.Kind == PdfiumLinkActionKind.InternalDestination
                        && link.DestinationPageIndex is int destinationIndex
                        && destinationIndex >= 0
                        && destinationIndex < document.Pages.Length)
                    {
                        links.Add(new PdfLink(
                            PdfLinkKind.Page,
                            bounds,
                            targetPageId: document.Pages[destinationIndex].PageId,
                            targetPageIndex: destinationIndex));
                    }
                    else if (link.Kind == PdfiumLinkActionKind.Uri && link.Uri is { } uri)
                    {
                        var decision = PdfExternalLinkPolicy.Evaluate(uri, out _);
                        links.Add(new PdfLink(
                            PdfLinkKind.Uri,
                            bounds,
                            uri: uri,
                            isSafeToActivate: decision == ExternalLinkDecision.Allowed,
                            blockedReason: decision == ExternalLinkDecision.Allowed
                                ? null
                                : "This link uses a blocked or malformed external action."));
                    }
                }

                return new PageLinks(documentId, pageIdentity.PageId, pageIndex, links);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<FormWidgetsResult> GetFormWidgetsAsync(
        DocumentId documentId,
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        var document = GetDocument(documentId);
        ValidatePageIndex(document, pageIndex);
        return await _engineLane.InvokeAsync(
            engine => ReadFormWidgets(engine, document, pageIndex),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DocumentSnapshot> ApplyFormValueAsync(
        FormValueChange change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var document = GetDocument(change.DocumentId);
        return await _engineLane.InvokeAsync(
            engine => ApplyFormValueCore(engine, document, change, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DocumentSnapshot> InvokePushButtonAsync(
        PushButtonInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var document = GetDocument(invocation.DocumentId);
        return await _engineLane.InvokeAsync(
            engine => InvokePushButtonCore(engine, document, invocation, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DocumentSnapshot> RotatePageAsync(
        RotatePageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var document = GetDocument(request.DocumentId);
        return await _engineLane.InvokeAsync(
            engine => RotatePageCore(engine, document, request, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DocumentSnapshot> DeletePageAsync(
        DeletePageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var document = GetDocument(request.DocumentId);
        return await _engineLane.InvokeAsync(
            engine => DeletePageCore(engine, document, request, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask MergeOrderedPagesAsync(
        MergeOrderedPagesRequest request,
        SafeFileHandle brokeredWriteHandle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(brokeredWriteHandle);
        try
        {
            await _engineLane.InvokeAsync(
                engine => MergeOrderedPagesCore(engine, request, brokeredWriteHandle, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!brokeredWriteHandle.IsClosed)
            {
                brokeredWriteHandle.Dispose();
            }
            throw;
        }
    }

    public async ValueTask<DocumentSnapshot> StageAnnotationsAsync(
        PdfAnnotationSaveRequest request,
        SafeFileHandle brokeredWriteHandle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(brokeredWriteHandle);
        request.ContractVersion.Validate();
        var document = GetDocument(request.DocumentId);
        try
        {
            return await _engineLane.InvokeAsync(
                engine =>
                {
                    try
                    {
                        return StageAnnotationsCore(
                            engine,
                            document,
                            request,
                            brokeredWriteHandle,
                            cancellationToken);
                    }
                    catch (WorkerRestartRequiredException)
                    {
                        QuarantineAfterUncertainMutation(document);
                        throw;
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!brokeredWriteHandle.IsClosed)
            {
                brokeredWriteHandle.Dispose();
            }
            throw;
        }
    }

    public async ValueTask<DocumentSnapshot> FinalizeAnnotationTransactionAsync(
        DocumentId documentId,
        Guid transactionId,
        bool committed,
        CancellationToken cancellationToken = default)
    {
        if (transactionId == Guid.Empty)
        {
            throw new ArgumentException("The annotation transaction id must not be empty.", nameof(transactionId));
        }

        var document = GetDocument(documentId);
        return await _engineLane.InvokeAsync(
            engine => FinalizeAnnotationTransactionCore(
                engine,
                document,
                transactionId,
                committed,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SaveFlattenedCopyAsync(
        PdfAnnotationSaveRequest request,
        SafeFileHandle brokeredWriteHandle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(brokeredWriteHandle);
        request.ContractVersion.Validate();
        var document = GetDocument(request.DocumentId);
        try
        {
            await _engineLane.InvokeAsync(
                engine => SaveFlattenedCopyCore(
                    engine,
                    document,
                    request,
                    brokeredWriteHandle,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!brokeredWriteHandle.IsClosed)
            {
                brokeredWriteHandle.Dispose();
            }
            throw;
        }
    }

    public async ValueTask SaveAsync(
        DocumentId documentId,
        ContentRevision capturedRevision,
        SafeFileHandle brokeredWriteHandle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brokeredWriteHandle);
        var document = GetDocument(documentId);
        try
        {
            await _engineLane.InvokeAsync(
                engine =>
                {
                    EnsureNoPendingAnnotationTransaction(document);
                    if (document.ContentRevision != capturedRevision)
                    {
                        throw new WorkerStaleIdentityException("The save revision is stale.");
                    }

                    using var output = BrokeredWriteStream.CreateTruncated(brokeredWriteHandle);
                    engine.SaveAsCopy(document.Handle, output);
                    output.Flush();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!brokeredWriteHandle.IsClosed)
            {
                brokeredWriteHandle.Dispose();
            }
            throw;
        }
    }

    public async ValueTask<bool> CloseAsync(
        DocumentId documentId,
        CancellationToken cancellationToken = default)
    {
        WorkerDocument? document;
        lock (_sync)
        {
            if (!_documents.Remove(documentId, out document))
            {
                return false;
            }
        }

        await _engineLane.InvokeAsync(
            _ => document.DisposeOnEngineLane(),
            CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        WorkerDocument[] documents;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            documents = _documents.Values.ToArray();
            _documents.Clear();
        }

        foreach (var document in documents)
        {
            await DisposeWorkerDocumentAsync(document).ConfigureAwait(false);
        }

        await _engineLane.DisposeAsync().ConfigureAwait(false);
    }

    private static WorkerDocument CreateWorkerDocument(
        PdfiumEngine engine,
        DocumentOpenRequest request,
        SafeFileHandle brokeredReadHandle)
    {
        var handle = engine.LoadDocument(brokeredReadHandle, request.Password, leaveOpen: false);
        if (handle is null)
        {
            throw engine.CreateException("The brokered PDF could not be opened.");
        }

        try
        {
            var pageCount = engine.GetPageCount(handle);
            if (pageCount is < 0 or > PdfContractLimits.MaxPageCount)
            {
                throw new PdfiumResourceLimitException("The PDF page count is outside the configured limit.");
            }

            var form = engine.TryCreateFormEnvironment(handle);
            var metadata = CreateMetadata(engine, handle, pageCount, form is not null);
            var permissions = CreatePermissions(engine, handle);
            var pages = Enumerable.Range(0, pageCount)
                .Select(static _ => new WorkerPage(
                    PageId.New(),
                    PageContentRevision.Initial,
                    PageAppearanceRevision.Initial))
                .ToImmutableArray();
            return new WorkerDocument(
                request.DocumentId,
                handle,
                form,
                metadata,
                permissions,
                pages);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static PdfMetadata CreateMetadata(
        PdfiumEngine engine,
        PdfiumDocumentHandle document,
        int pageCount,
        bool hasForms)
    {
        var fileVersion = engine.GetFileVersion(document);
        var version = fileVersion is null
            ? null
            : $"{fileVersion.Value / 10}.{fileVersion.Value % 10}";
        return new PdfMetadata(
            pageCount,
            version,
            engine.GetMetadataText(document, "Title"),
            engine.GetMetadataText(document, "Author"),
            engine.GetMetadataText(document, "Subject"),
            engine.GetMetadataText(document, "Keywords"),
            engine.GetMetadataText(document, "Creator"),
            engine.GetMetadataText(document, "Producer"),
            isEncrypted: engine.GetSecurityHandlerRevision(document) >= 0,
            hasOutline: !engine.GetFirstBookmark(document).IsNull,
            hasForms: hasForms);
    }

    private static PdfPermissions CreatePermissions(
        PdfiumEngine engine,
        PdfiumDocumentHandle document)
    {
        var native = engine.GetDocumentPermissions(document);
        var allowed = PdfPermissionFlags.None;
        if ((native & 0x0004) != 0) allowed |= PdfPermissionFlags.Print;
        if ((native & 0x0008) != 0) allowed |= PdfPermissionFlags.Modify;
        if ((native & 0x0010) != 0) allowed |= PdfPermissionFlags.Copy;
        if ((native & 0x0020) != 0) allowed |= PdfPermissionFlags.Annotate;
        if ((native & 0x0100) != 0) allowed |= PdfPermissionFlags.FillForms;
        if ((native & 0x0200) != 0) allowed |= PdfPermissionFlags.Accessibility;
        if ((native & 0x0400) != 0) allowed |= PdfPermissionFlags.Assemble;
        if ((native & 0x0800) != 0) allowed |= PdfPermissionFlags.HighQualityPrint;
        return new PdfPermissions(
            allowed,
            isEncrypted: engine.GetSecurityHandlerRevision(document) >= 0,
            isOwnerPasswordAuthenticated: false);
    }

    private static DocumentSnapshot CreateDocumentSnapshot(WorkerDocument document) => new(
        document.DocumentId,
        document.ContentRevision,
        document.SavedRevision,
        document.StructureRevision,
        "PDF document",
        document.Pages.Length,
        0,
        RecoveryState.None,
        ExternalFileState.Unchanged);

    private static FormWidgetsResult ReadFormWidgets(
        PdfiumEngine engine,
        WorkerDocument document,
        int pageIndex)
    {
        var form = document.Form;
        if (form is null)
        {
            return new FormWidgetsResult(document.DocumentId, []);
        }

        using var page = engine.LoadPage(document.Handle, pageIndex)
            ?? throw engine.CreateException($"Unable to load page {pageIndex + 1}.");
        var pageIdentity = document.Pages[pageIndex];
        var fields = engine.GetPageFormFields(page, form, PdfContractLimits.MaxCollectionCount);
        var widgets = fields.Select(field => CreateFormWidget(document, pageIdentity, pageIndex, field)).ToArray();
        return new FormWidgetsResult(document.DocumentId, [.. widgets]);
    }

    private static FormWidget CreateFormWidget(
        WorkerDocument document,
        WorkerPage page,
        int pageIndex,
        PdfiumFormFieldInfo field)
    {
        var fieldType = field.NativeFieldType switch
        {
            1 => FormWidgetType.PushButton,
            2 => FormWidgetType.Checkbox,
            3 => FormWidgetType.RadioButton,
            4 => FormWidgetType.ComboBox,
            5 => FormWidgetType.ListBox,
            6 => FormWidgetType.Text,
            7 => FormWidgetType.Signature,
            _ => FormWidgetType.Unsupported
        };
        var supported = fieldType is FormWidgetType.Text
            or FormWidgetType.Checkbox
            or FormWidgetType.RadioButton
            or FormWidgetType.ComboBox
            or FormWidgetType.ListBox
            or FormWidgetType.PushButton;
        var hasBlockedPushButtonAction = fieldType == FormWidgetType.PushButton
            && (field.HasUnsafeAction || field.HasParentField);
        if (field.HasUnsafeAction || hasBlockedPushButtonAction)
        {
            supported = false;
        }

        var value = fieldType switch
        {
            FormWidgetType.Checkbox or FormWidgetType.RadioButton => FormValue.BooleanValue(field.IsChecked),
            FormWidgetType.ListBox when field.SelectedOptionIndices.Count > 1 => FormValue.MultipleChoices(
                field.SelectedOptionIndices
                    .Where(index => index >= 0 && index < field.Options.Count)
                    .Select(index => NonEmptyOption(field.Options[index], index))),
            FormWidgetType.ComboBox or FormWidgetType.ListBox when !string.IsNullOrEmpty(field.Value) => FormValue.Choice(field.Value),
            FormWidgetType.Text => FormValue.TextValue(field.Value),
            _ => FormValue.None()
        };
        var displayName = string.IsNullOrWhiteSpace(field.Name)
            ? $"Unnamed field {field.AnnotationIndex + 1}"
            : field.Name;
        var unsupportedReason = supported
            ? null
            : field.HasUnsafeAction
                ? "This form field contains a blocked action and is read-only."
                : hasBlockedPushButtonAction
                    ? "This push button cannot be proven actionless and is read-only."
                : "This form widget type is read-only in ElliePdf.";
        return new FormWidget(
            CreateFieldId(document.DocumentId, page.PageId, field.AnnotationIndex, displayName),
            document.DocumentId,
            page.PageId,
            pageIndex,
            fieldType,
            displayName,
            new PdfRect(field.Bounds.Left, field.Bounds.Top, field.Bounds.Right, field.Bounds.Bottom),
            value,
            isReadOnly: !supported
                || !document.Permissions.CanFillForms
                || (field.Flags & 1) != 0,
            isRequired: (field.Flags & 2) != 0,
            options: [.. field.Options.Select(static (option, index) => NonEmptyOption(option, index))],
            isSupported: supported,
            unsupportedReason: unsupportedReason);
    }

    private static DocumentSnapshot ApplyFormValueCore(
        PdfiumEngine engine,
        WorkerDocument document,
        FormValueChange change,
        CancellationToken cancellationToken)
    {
        EnsureNoPendingAnnotationTransaction(document);
        if (document.ContentRevision != change.ExpectedContentRevision)
        {
            throw new WorkerStaleIdentityException("The form edit carries a stale content revision.");
        }
        if (!document.Permissions.CanFillForms)
        {
            throw new UnauthorizedAccessException("The document permissions deny form updates.");
        }
        var form = document.Form ?? throw new InvalidOperationException("The document has no AcroForm environment.");

        for (var pageIndex = 0; pageIndex < document.Pages.Length; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var page = engine.LoadPage(document.Handle, pageIndex)
                ?? throw engine.CreateException($"Unable to load page {pageIndex + 1}.");
            var fields = engine.GetPageFormFields(page, form, PdfContractLimits.MaxCollectionCount);
            foreach (var field in fields)
            {
                var displayName = string.IsNullOrWhiteSpace(field.Name)
                    ? $"Unnamed field {field.AnnotationIndex + 1}"
                    : field.Name;
                if (CreateFieldId(document.DocumentId, document.Pages[pageIndex].PageId, field.AnnotationIndex, displayName)
                    != change.FieldId)
                {
                    continue;
                }

                var widget = CreateFormWidget(document, document.Pages[pageIndex], pageIndex, field);
                if (!widget.IsSupported || widget.IsReadOnly)
                {
                    throw new UnauthorizedAccessException("The form field is read-only or unsupported.");
                }

                using var annotation = engine.GetPageAnnotation(page, field.AnnotationIndex)
                    ?? throw new InvalidOperationException("The form annotation is no longer available.");
                var value = ResolveNativeFormValue(field, change.Value);
                if (!engine.SetAnnotationStringValue(annotation, "V", value))
                {
                    throw engine.CreateException("PDFium could not update the form value.");
                }
                if (field.NativeFieldType is 2 or 3
                    && !engine.SetAnnotationStringValue(annotation, "AS", value))
                {
                    throw engine.CreateException("PDFium could not update the form appearance state.");
                }

                document.ContentRevision = document.ContentRevision.Next();
                document.Pages[pageIndex].ContentRevision = document.Pages[pageIndex].ContentRevision.Next();
                document.Pages[pageIndex].AppearanceRevision = document.Pages[pageIndex].AppearanceRevision.Next();
                return CreateDocumentSnapshot(document);
            }
        }

        throw new WorkerStaleIdentityException("The form field identity is no longer available.");
    }

    private static DocumentSnapshot InvokePushButtonCore(
        PdfiumEngine engine,
        WorkerDocument document,
        PushButtonInvocation invocation,
        CancellationToken cancellationToken)
    {
        EnsureNoPendingAnnotationTransaction(document);
        if (document.ContentRevision != invocation.ExpectedContentRevision)
        {
            throw new WorkerStaleIdentityException("The push button invocation carries a stale content revision.");
        }

        var form = document.Form ?? throw new InvalidOperationException("The document has no AcroForm environment.");
        for (var pageIndex = 0; pageIndex < document.Pages.Length; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var page = engine.LoadPage(document.Handle, pageIndex)
                ?? throw engine.CreateException($"Unable to load page {pageIndex + 1}.");
            var fields = engine.GetPageFormFields(page, form, PdfContractLimits.MaxCollectionCount);
            foreach (var field in fields)
            {
                var displayName = string.IsNullOrWhiteSpace(field.Name)
                    ? $"Unnamed field {field.AnnotationIndex + 1}"
                    : field.Name;
                if (CreateFieldId(document.DocumentId, document.Pages[pageIndex].PageId, field.AnnotationIndex, displayName)
                    != invocation.FieldId)
                {
                    continue;
                }

                var widget = CreateFormWidget(document, document.Pages[pageIndex], pageIndex, field);
                if (widget.Type != FormWidgetType.PushButton
                    || !widget.IsSupported
                    || widget.IsReadOnly)
                {
                    throw new UnauthorizedAccessException("The push button is read-only, unsupported, or contains a blocked action.");
                }

                using var annotation = engine.GetPageAnnotation(page, field.AnnotationIndex)
                    ?? throw new InvalidOperationException("The form annotation is no longer available.");
                engine.ActivateActionlessPushButton(
                    page,
                    form,
                    annotation,
                    new PdfiumRectangle(field.Bounds.Left, field.Bounds.Top, field.Bounds.Right, field.Bounds.Bottom));

                // A push button is invoked through PDFium's pointer route, not via
                // a /V string write. Actionless buttons leave the document revision
                // unchanged, so this is intentionally not a form-value mutation.
                return CreateDocumentSnapshot(document);
            }
        }

        throw new WorkerStaleIdentityException("The push button identity is no longer available.");
    }

    private static DocumentSnapshot RotatePageCore(
        PdfiumEngine engine,
        WorkerDocument document,
        RotatePageRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNoPendingAnnotationTransaction(document);
        ValidateDocumentMutationIdentity(
            document,
            request.ExpectedContentRevision,
            request.ExpectedStructureRevision);
        EnsurePageAssemblyAllowed(document);

        var pageIndex = FindPageIndex(document, request.PageId);
        var pageIdentity = document.Pages[pageIndex];
        if (pageIdentity.ContentRevision != request.ExpectedPageContentRevision)
        {
            throw new WorkerStaleIdentityException("The page rotation carries a stale page revision.");
        }

        using var page = engine.LoadPage(document.Handle, pageIndex)
            ?? throw engine.CreateException($"Unable to load page {pageIndex + 1} for rotation.");
        var currentRotation = engine.GetPageRotation(page);
        if (currentRotation is < 0 or > 3)
        {
            throw new PdfiumResourceLimitException("PDFium returned an invalid page rotation.");
        }

        var nextRotation = ((currentRotation + request.QuarterTurnsClockwise) % 4 + 4) % 4;
        engine.SetPageRotation(page, nextRotation);
        document.ContentRevision = document.ContentRevision.Next();
        pageIdentity.ContentRevision = pageIdentity.ContentRevision.Next();
        pageIdentity.AppearanceRevision = pageIdentity.AppearanceRevision.Next();
        return CreateDocumentSnapshot(document);
    }

    private static DocumentSnapshot DeletePageCore(
        PdfiumEngine engine,
        WorkerDocument document,
        DeletePageRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureNoPendingAnnotationTransaction(document);
        ValidateDocumentMutationIdentity(
            document,
            request.ExpectedContentRevision,
            request.ExpectedStructureRevision);
        EnsurePageAssemblyAllowed(document);

        var pageIndex = FindPageIndex(document, request.PageId);
        if (document.Pages[pageIndex].ContentRevision != request.ExpectedPageContentRevision)
        {
            throw new WorkerStaleIdentityException("The page deletion carries a stale page revision.");
        }

        engine.DeletePage(document.Handle, pageIndex);
        document.Pages = document.Pages.RemoveAt(pageIndex);
        document.ContentRevision = document.ContentRevision.Next();
        document.StructureRevision = document.StructureRevision.Next();
        document.Metadata = WithPageCount(document.Metadata, document.Pages.Length);
        return CreateDocumentSnapshot(document);
    }

    private void MergeOrderedPagesCore(
        PdfiumEngine engine,
        MergeOrderedPagesRequest request,
        SafeFileHandle brokeredWriteHandle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pages = new (WorkerDocument Document, int PageIndex)[request.PagesInOrder.Length];
        for (var index = 0; index < request.PagesInOrder.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = request.PagesInOrder[index];
            var document = GetDocument(reference.DocumentId);
            EnsureNoPendingAnnotationTransaction(document);
            ValidateDocumentMutationIdentity(
                document,
                reference.ExpectedContentRevision,
                reference.ExpectedStructureRevision);
            if (!document.Permissions.CanCopy)
            {
                throw new UnauthorizedAccessException("The document permissions deny ordered page export.");
            }

            var pageIndex = FindPageIndex(document, reference.PageId);
            if (document.Pages[pageIndex].ContentRevision != reference.ExpectedPageContentRevision)
            {
                throw new WorkerStaleIdentityException("The ordered merge carries a stale page revision.");
            }

            pages[index] = (document, pageIndex);
        }

        using var destination = engine.CreateDocument();
        engine.CopyViewerPreferences(destination, pages[0].Document.Handle);
        for (var destinationIndex = 0; destinationIndex < pages.Length; destinationIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = pages[destinationIndex];
            if (!engine.ImportPages(
                    destination,
                    source.Document.Handle,
                    [source.PageIndex],
                    destinationIndex))
            {
                throw engine.CreateException("PDFium could not import an ordered source page.");
            }

            // A plan may carry an explicit absolute rotation. Apply it only
            // when supplied so older callers retain the source page rotation.
            var requestedRotation = request.PagesInOrder[destinationIndex].Rotation;
            if (requestedRotation is { } rotation)
            {
                using var destinationPage = engine.LoadPage(destination, destinationIndex)
                    ?? throw engine.CreateException($"Unable to load exported page {destinationIndex + 1} for rotation.");
                engine.SetPageRotation(destinationPage, (int)rotation);
            }
        }

        using var output = BrokeredWriteStream.CreateTruncated(brokeredWriteHandle);
        engine.SaveAsCopy(destination, output);
        output.Flush();
    }

    private static DocumentSnapshot StageAnnotationsCore(
        PdfiumEngine engine,
        WorkerDocument document,
        PdfAnnotationSaveRequest request,
        SafeFileHandle brokeredWriteHandle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateAnnotationRequest(document, request, flattenedCopy: false);
        if (request.Pages.IsEmpty)
        {
            throw new ArgumentException("An annotation stage must contain at least one page overlay.", nameof(request));
        }
        if (document.AnnotationTransaction is not null)
        {
            throw new InvalidOperationException("An annotation save is already awaiting atomic commit finalization.");
        }
        if (document.LastFinalizedAnnotationTransactionId == request.TransactionId)
        {
            throw new InvalidOperationException("The annotation transaction id has already been finalized.");
        }

        PreflightSignatureImages(request);
        var mutationAttempted = false;
        var mutated = false;

        try
        {
            foreach (var batch in request.Pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageIdentity = document.Pages[batch.PageIndex];
                using var page = engine.LoadPage(document.Handle, batch.PageIndex)
                    ?? throw engine.CreateException($"Unable to load page {batch.PageIndex + 1} for annotation persistence.");
                mutationAttempted = true;
                var appended = ApplyPageOverlayBatch(
                    engine,
                    document.Handle,
                    page,
                    batch,
                    cancellationToken);
                if (appended == 0)
                {
                    continue;
                }

                mutated = true;
                pageIdentity.ContentRevision = pageIdentity.ContentRevision.Next();
                pageIdentity.AppearanceRevision = pageIdentity.AppearanceRevision.Next();
            }

            if (mutated)
            {
                document.ContentRevision = document.ContentRevision.Next();
            }

            document.AnnotationTransaction = new StagedAnnotationTransaction(request.TransactionId);

            using var output = BrokeredWriteStream.CreateTruncated(brokeredWriteHandle);
            engine.SaveAsCopy(document.Handle, output);
            output.Flush();
            return CreateDocumentSnapshot(document);
        }
        catch (Exception stagingFailure)
        {
            document.AnnotationTransaction = null;
            if (mutationAttempted)
            {
                throw new WorkerRestartRequiredException(
                    "Annotation staging failed after native mutation began; the isolated worker must be discarded.",
                    stagingFailure);
            }

            throw;
        }
    }

    private static DocumentSnapshot FinalizeAnnotationTransactionCore(
        PdfiumEngine engine,
        WorkerDocument document,
        Guid transactionId,
        bool committed,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transaction = document.AnnotationTransaction;
        if (transaction is null)
        {
            if (document.LastFinalizedAnnotationTransactionId == transactionId
                && document.LastAnnotationTransactionCommitted == committed)
            {
                return CreateDocumentSnapshot(document);
            }

            throw new InvalidOperationException("No matching annotation transaction is awaiting finalization.");
        }
        if (transaction.TransactionId != transactionId)
        {
            throw new WorkerStaleIdentityException("The annotation transaction identity is stale.");
        }

        if (committed)
        {
            document.SavedRevision = document.ContentRevision;
        }

        document.AnnotationTransaction = null;
        document.LastFinalizedAnnotationTransactionId = transactionId;
        document.LastAnnotationTransactionCommitted = committed;
        return CreateDocumentSnapshot(document);
    }

    private static void SaveFlattenedCopyCore(
        PdfiumEngine engine,
        WorkerDocument document,
        PdfAnnotationSaveRequest request,
        SafeFileHandle brokeredWriteHandle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateAnnotationRequest(document, request, flattenedCopy: true);
        EnsureNoPendingAnnotationTransaction(document);

        using var destination = engine.CreateDocument();
        engine.CopyViewerPreferences(destination, document.Handle);
        for (var pageIndex = 0; pageIndex < document.Pages.Length; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!engine.ImportPages(destination, document.Handle, [pageIndex], pageIndex))
            {
                throw engine.CreateException($"PDFium could not clone page {pageIndex + 1} for flattened export.");
            }
        }

        var batchesByPage = request.Pages.ToDictionary(static batch => batch.PageIndex);
        for (var pageIndex = 0; pageIndex < document.Pages.Length; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hasBatch = batchesByPage.TryGetValue(pageIndex, out var batch);
            HashSet<string>? alreadyPersisted = null;
            using (var pageToFlatten = engine.LoadPage(destination, pageIndex)
                ?? throw engine.CreateException($"Unable to load cloned page {pageIndex + 1} for flattened export."))
            {
                if (hasBatch)
                {
                    alreadyPersisted = ReadAnnotationIds(engine, pageToFlatten);
                }
                engine.FlattenPage(pageToFlatten);
            }

            if (!hasBatch)
            {
                continue;
            }

            // Overlay content is inserted directly into the cloned page after
            // pre-existing annotations are flattened. This avoids a transient
            // annotation lifecycle and guarantees that the exported overlay is
            // no longer independently editable.
            using var contentPage = engine.LoadPage(destination, pageIndex)
                ?? throw engine.CreateException($"Unable to reload cloned page {pageIndex + 1} for flattened content.");
            ApplyFlattenedOverlayBatch(
                engine,
                destination,
                contentPage,
                batch!,
                alreadyPersisted!,
                cancellationToken);
        }

        using var output = BrokeredWriteStream.CreateTruncated(brokeredWriteHandle);
        engine.SaveAsCopy(destination, output);
        output.Flush();
    }

    private static void ValidateAnnotationRequest(
        WorkerDocument document,
        PdfAnnotationSaveRequest request,
        bool flattenedCopy)
    {
        ValidateDocumentMutationIdentity(
            document,
            request.ExpectedContentRevision,
            request.ExpectedStructureRevision);
        if (!document.Permissions.CanAnnotate)
        {
            throw new UnauthorizedAccessException("The document permissions deny annotation changes.");
        }
        if (flattenedCopy && (!document.Permissions.CanModify || !document.Permissions.CanCopy))
        {
            throw new UnauthorizedAccessException("The document permissions deny flattened content export.");
        }

        foreach (var batch in request.Pages)
        {
            ValidatePageIndex(document, batch.PageIndex);
            var page = document.Pages[batch.PageIndex];
            if (page.PageId != batch.PageId
                || page.ContentRevision != batch.ExpectedPageContentRevision)
            {
                throw new WorkerStaleIdentityException("The annotation request carries a stale page identity or revision.");
            }
        }
    }

    private static int ApplyPageOverlayBatch(
        PdfiumEngine engine,
        PdfiumDocumentHandle document,
        PdfiumPageHandle page,
        PdfPageOverlayBatch batch,
        CancellationToken cancellationToken)
    {
        var (pageWidth, pageHeight) = engine.GetPageSize(page);
        if (!float.IsFinite(pageWidth) || !float.IsFinite(pageHeight)
            || pageWidth <= 0 || pageHeight <= 0)
        {
            throw new PdfiumResourceLimitException("PDFium returned invalid annotation page geometry.");
        }

        var existingIds = ReadAnnotationIds(engine, page);
        var appended = 0;
        foreach (var ink in batch.Ink)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (existingIds.Add(ink.AnnotationId))
            {
                CreateInkAnnotation(engine, page, ink, pageWidth, pageHeight);
                appended++;
            }
        }
        foreach (var text in batch.Text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (existingIds.Add(text.AnnotationId))
            {
                CreateTextStampAnnotation(engine, document, page, text, pageWidth, pageHeight);
                appended++;
            }
        }
        foreach (var signature in batch.Signatures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (existingIds.Add(signature.AnnotationId))
            {
                CreateSignatureStampAnnotation(engine, document, page, signature, pageWidth, pageHeight);
                appended++;
            }
        }

        return appended;
    }

    private static void ApplyFlattenedOverlayBatch(
        PdfiumEngine engine,
        PdfiumDocumentHandle document,
        PdfiumPageHandle page,
        PdfPageOverlayBatch batch,
        IReadOnlySet<string> alreadyPersisted,
        CancellationToken cancellationToken)
    {
        var (pageWidth, pageHeight) = engine.GetPageSize(page);
        if (!float.IsFinite(pageWidth) || !float.IsFinite(pageHeight)
            || pageWidth <= 0 || pageHeight <= 0)
        {
            throw new PdfiumResourceLimitException("PDFium returned invalid flattened page geometry.");
        }

        foreach (var ink in batch.Ink)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (alreadyPersisted.Contains(ink.AnnotationId)) continue;
            var points = ink.Points
                .Select(point => (
                    X: ClampCoordinate(point.X, pageWidth),
                    Y: pageHeight - ClampCoordinate(point.Y, pageHeight)))
                .ToArray();
            using var path = engine.CreateStrokedPath(
                points,
                ink.Color.Red,
                ink.Color.Green,
                ink.Color.Blue,
                ink.Color.Alpha,
                checked((float)ink.Thickness));
            if (!engine.InsertPageObject(page, path))
            {
                throw engine.CreateException("PDFium could not insert flattened ink content.");
            }
        }

        foreach (var text in batch.Text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (alreadyPersisted.Contains(text.AnnotationId)) continue;
            var rectangle = ConvertRectangle(text.Rectangle, pageWidth, pageHeight);
            var font = (text.IsBold, text.IsItalic) switch
            {
                (true, true) => "Helvetica-BoldOblique",
                (true, false) => "Helvetica-Bold",
                (false, true) => "Helvetica-Oblique",
                _ => "Helvetica"
            };
            var fontSize = checked((float)text.FontSize);
            var lines = LayoutTextLines(
                text.Text,
                rectangle.Right - rectangle.Left,
                rectangle.Top - rectangle.Bottom,
                fontSize);
            var lineHeight = fontSize * 1.2f;
            for (var index = 0; index < lines.Count; index++)
            {
                using var textObject = engine.CreateTextObject(
                    document,
                    font,
                    fontSize,
                    lines[index],
                    text.Color.Red,
                    text.Color.Green,
                    text.Color.Blue,
                    text.Color.Alpha);
                var baseline = Math.Max(rectangle.Bottom, rectangle.Top - fontSize - (index * lineHeight));
                engine.SetPageObjectMatrix(textObject, 1, 0, 0, 1, rectangle.Left, baseline);
                if (!engine.InsertPageObject(page, textObject))
                {
                    throw engine.CreateException("PDFium could not insert flattened text content.");
                }
            }
        }

        foreach (var signature in batch.Signatures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (alreadyPersisted.Contains(signature.AnnotationId)) continue;
            var rectangle = ConvertRectangle(signature.Rectangle, pageWidth, pageHeight);
            var image = DecodeSignature(signature.ImageBase64);
            using var bitmap = engine.CreateBitmap(image.Width, image.Height, alpha: true);
            engine.FillBitmap(bitmap, 0, 0, image.Width, image.Height, 0);
            var packedStride = checked(image.Width * 4);
            for (var row = 0; row < image.Height; row++)
            {
                engine.WriteBitmapRow(
                    bitmap,
                    checked(row * bitmap.Stride),
                    image.Pixels,
                    checked(row * packedStride),
                    packedStride);
            }
            using var imageObject = engine.CreateImageObject(document);
            if (!engine.SetImageBitmap(page, imageObject, bitmap))
            {
                throw engine.CreateException("PDFium could not set the flattened signature bitmap.");
            }
            engine.SetPageObjectMatrix(
                imageObject,
                rectangle.Right - rectangle.Left,
                0,
                0,
                rectangle.Top - rectangle.Bottom,
                rectangle.Left,
                rectangle.Bottom);
            if (!engine.InsertPageObject(page, imageObject))
            {
                throw engine.CreateException("PDFium could not insert flattened signature content.");
            }
        }

        if (!engine.GeneratePageContent(page))
        {
            throw engine.CreateException("PDFium could not generate flattened page content.");
        }
    }

    private static HashSet<string> ReadAnnotationIds(PdfiumEngine engine, PdfiumPageHandle page)
    {
        var annotationCount = ValidateAnnotationCount(engine.GetPageAnnotationCount(page));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var annotationIndex = 0; annotationIndex < annotationCount; annotationIndex++)
        {
            using var annotation = engine.GetPageAnnotation(page, annotationIndex);
            if (annotation is null)
            {
                continue;
            }

            var id = engine.GetAnnotationStringValue(annotation, "NM");
            if (!string.IsNullOrEmpty(id) && id.Length <= 128)
            {
                _ = ids.Add(id);
            }
        }

        return ids;
    }

    private static void CreateInkAnnotation(
        PdfiumEngine engine,
        PdfiumPageHandle page,
        PdfInkAnnotation ink,
        float pageWidth,
        float pageHeight)
    {
        var points = new (float X, float Y)[ink.Points.Length];
        var minimumX = pageWidth;
        var minimumY = pageHeight;
        var maximumX = 0f;
        var maximumY = 0f;
        for (var index = 0; index < ink.Points.Length; index++)
        {
            var x = ClampCoordinate(ink.Points[index].X, pageWidth);
            var topOriginY = ClampCoordinate(ink.Points[index].Y, pageHeight);
            var y = pageHeight - topOriginY;
            points[index] = (x, y);
            minimumX = Math.Min(minimumX, x);
            minimumY = Math.Min(minimumY, y);
            maximumX = Math.Max(maximumX, x);
            maximumY = Math.Max(maximumY, y);
        }

        var padding = checked((float)Math.Max(1, ink.Thickness / 2));
        var rectangle = EnsurePositiveRectangle(
            Math.Max(0, minimumX - padding),
            Math.Max(0, minimumY - padding),
            Math.Min(pageWidth, maximumX + padding),
            Math.Min(pageHeight, maximumY + padding),
            pageWidth,
            pageHeight);
        using var annotation = engine.CreatePageAnnotation(page, AnnotationInkSubtype);
        ConfigureAnnotation(
            engine,
            annotation,
            ink.AnnotationId,
            "ElliePdf ink annotation",
            rectangle,
            ink.Color);
        if (!engine.SetAnnotationBorder(annotation, checked((float)ink.Thickness)))
        {
            throw engine.CreateException("PDFium could not set the ink annotation border.");
        }
        if (engine.AddInkStroke(annotation, points) < 0)
        {
            throw engine.CreateException("PDFium could not append the ink stroke.");
        }
        using var appearance = engine.CreateStrokedPath(
            points,
            ink.Color.Red,
            ink.Color.Green,
            ink.Color.Blue,
            ink.Color.Alpha,
            checked((float)ink.Thickness));
        if (!engine.AppendAnnotationObject(annotation, appearance))
        {
            throw engine.CreateException("PDFium could not append the ink annotation appearance.");
        }
    }

    private static void CreateTextStampAnnotation(
        PdfiumEngine engine,
        PdfiumDocumentHandle document,
        PdfiumPageHandle page,
        PdfTextStampAnnotation text,
        float pageWidth,
        float pageHeight)
    {
        var rectangle = ConvertRectangle(text.Rectangle, pageWidth, pageHeight);
        using var annotation = engine.CreatePageAnnotation(page, AnnotationStampSubtype);
        ConfigureAnnotation(
            engine,
            annotation,
            text.AnnotationId,
            text.Text,
            rectangle,
            text.Color);

        var font = (text.IsBold, text.IsItalic) switch
        {
            (true, true) => "Helvetica-BoldOblique",
            (true, false) => "Helvetica-Bold",
            (false, true) => "Helvetica-Oblique",
            _ => "Helvetica"
        };
        var fontSize = checked((float)text.FontSize);
        var lines = LayoutTextLines(text.Text, rectangle.Right - rectangle.Left, rectangle.Top - rectangle.Bottom, fontSize);
        var lineHeight = fontSize * 1.2f;
        for (var index = 0; index < lines.Count; index++)
        {
            using var textObject = engine.CreateTextObject(
                document,
                font,
                fontSize,
                lines[index],
                text.Color.Red,
                text.Color.Green,
                text.Color.Blue,
                text.Color.Alpha);
            var baseline = Math.Max(rectangle.Bottom, rectangle.Top - fontSize - (index * lineHeight));
            engine.SetPageObjectMatrix(textObject, 1, 0, 0, 1, rectangle.Left, baseline);
            if (!engine.AppendAnnotationObject(annotation, textObject))
            {
                throw engine.CreateException("PDFium could not append the text stamp appearance.");
            }
        }
    }

    private static void CreateSignatureStampAnnotation(
        PdfiumEngine engine,
        PdfiumDocumentHandle document,
        PdfiumPageHandle page,
        PdfSignatureStampAnnotation signature,
        float pageWidth,
        float pageHeight)
    {
        var rectangle = ConvertRectangle(signature.Rectangle, pageWidth, pageHeight);
        using var annotation = engine.CreatePageAnnotation(page, AnnotationStampSubtype);
        ConfigureAnnotation(
            engine,
            annotation,
            signature.AnnotationId,
            "ElliePdf signature stamp",
            rectangle,
            new PdfOverlayColor(0, 0, 0));

        var image = DecodeSignature(signature.ImageBase64);
        using var bitmap = engine.CreateBitmap(image.Width, image.Height, alpha: true);
        engine.FillBitmap(bitmap, 0, 0, image.Width, image.Height, 0);
        var packedStride = checked(image.Width * 4);
        for (var row = 0; row < image.Height; row++)
        {
            engine.WriteBitmapRow(
                bitmap,
                checked(row * bitmap.Stride),
                image.Pixels,
                checked(row * packedStride),
                packedStride);
        }

        using var imageObject = engine.CreateImageObject(document);
        if (!engine.SetImageBitmap(page, imageObject, bitmap))
        {
            throw engine.CreateException("PDFium could not set the signature image bitmap.");
        }
        engine.SetPageObjectMatrix(
            imageObject,
            rectangle.Right - rectangle.Left,
            0,
            0,
            rectangle.Top - rectangle.Bottom,
            rectangle.Left,
            rectangle.Bottom);
        if (!engine.AppendAnnotationObject(annotation, imageObject))
        {
            throw engine.CreateException("PDFium could not append the signature stamp appearance.");
        }
    }

    private static void ConfigureAnnotation(
        PdfiumEngine engine,
        PdfiumAnnotationHandle annotation,
        string annotationId,
        string contents,
        AnnotationRectangle rectangle,
        PdfOverlayColor color)
    {
        if (!engine.SetAnnotationRectangle(
                annotation,
                rectangle.Left,
                rectangle.Bottom,
                rectangle.Right,
                rectangle.Top)
            || !engine.SetAnnotationStringValue(annotation, "NM", annotationId)
            || !engine.SetAnnotationStringValue(annotation, "Contents", contents)
            || !engine.SetAnnotationColor(annotation, color.Red, color.Green, color.Blue, color.Alpha)
            || !engine.SetAnnotationPrintable(annotation))
        {
            throw engine.CreateException("PDFium could not initialize an annotation dictionary.");
        }
    }

    private static AnnotationRectangle ConvertRectangle(
        PdfOverlayRectangle rectangle,
        float pageWidth,
        float pageHeight)
    {
        var left = ClampCoordinate(rectangle.X, pageWidth);
        var right = ClampCoordinate(rectangle.X + rectangle.Width, pageWidth);
        var topOriginTop = ClampCoordinate(rectangle.Y, pageHeight);
        var topOriginBottom = ClampCoordinate(rectangle.Y + rectangle.Height, pageHeight);
        return EnsurePositiveRectangle(
            left,
            pageHeight - topOriginBottom,
            right,
            pageHeight - topOriginTop,
            pageWidth,
            pageHeight);
    }

    private static AnnotationRectangle EnsurePositiveRectangle(
        float left,
        float bottom,
        float right,
        float top,
        float pageWidth,
        float pageHeight)
    {
        const float minimumExtent = 0.01f;
        left = Math.Clamp(left, 0, pageWidth);
        right = Math.Clamp(right, 0, pageWidth);
        bottom = Math.Clamp(bottom, 0, pageHeight);
        top = Math.Clamp(top, 0, pageHeight);
        if (right - left < minimumExtent)
        {
            if (left + minimumExtent <= pageWidth) right = left + minimumExtent;
            else left = Math.Max(0, right - minimumExtent);
        }
        if (top - bottom < minimumExtent)
        {
            if (bottom + minimumExtent <= pageHeight) top = bottom + minimumExtent;
            else bottom = Math.Max(0, top - minimumExtent);
        }
        if (right <= left || top <= bottom)
        {
            throw new ArgumentOutOfRangeException(nameof(right), "The annotation lies outside the page bounds.");
        }
        return new AnnotationRectangle(left, bottom, right, top);
    }

    private static float ClampCoordinate(double value, float maximum)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
        return checked((float)Math.Clamp(value, 0, maximum));
    }

    private static IReadOnlyList<string> LayoutTextLines(
        string value,
        float width,
        float height,
        float fontSize)
    {
        var normalized = value.ReplaceLineEndings("\n")
            .Select(static character => character == '\0' || (char.IsControl(character) && character != '\n' && character != '\t')
                ? ' '
                : character)
            .ToArray();
        var text = new string(normalized).Replace('\t', ' ');
        var maximumCharacters = Math.Clamp((int)Math.Floor(width / Math.Max(1, fontSize * 0.55f)), 1, 4_096);
        var maximumLines = Math.Clamp((int)Math.Floor(height / Math.Max(1, fontSize * 1.2f)), 1, 1_024);
        var lines = new List<string>(maximumLines);
        foreach (var paragraph in text.Split('\n'))
        {
            var remaining = paragraph.Trim();
            if (remaining.Length == 0)
            {
                continue;
            }

            while (remaining.Length > 0 && lines.Count < maximumLines)
            {
                var take = Math.Min(maximumCharacters, remaining.Length);
                if (take < remaining.Length)
                {
                    var breakAt = remaining.LastIndexOf(' ', take - 1, take);
                    if (breakAt > 0) take = breakAt;
                }
                lines.Add(remaining[..take].Trim());
                remaining = remaining[take..].TrimStart();
            }

            if (lines.Count >= maximumLines)
            {
                break;
            }
        }

        if (lines.Count == 0)
        {
            lines.Add(".");
        }
        if (lines.Count == maximumLines && string.Join(' ', lines).Length < text.Length)
        {
            var last = lines[^1];
            lines[^1] = last.Length > 3 ? last[..^3] + "..." : "...";
        }
        return lines;
    }

    private static SignatureImage DecodeSignature(string imageBase64)
    {
        byte[] encoded;
        try
        {
            encoded = Convert.FromBase64String(imageBase64);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The signature image is not valid Base64.", nameof(imageBase64), exception);
        }
        if (encoded.Length is < 1 or > MaximumSignatureDecodedBytes)
        {
            throw new PdfiumResourceLimitException("The decoded signature image exceeds the one-megabyte limit.");
        }

        using var input = new MemoryStream(encoded, writable: false);
        using var source = DrawingImage.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
        if (source.Width is < 1 or > MaximumSignatureDimension
            || source.Height is < 1 or > MaximumSignatureDimension
            || checked((long)source.Width * source.Height) > checked((long)MaximumSignatureDimension * MaximumSignatureDimension))
        {
            throw new PdfiumResourceLimitException("The signature image dimensions exceed the configured limit.");
        }

        using var bitmap = new DrawingBitmap(source.Width, source.Height, DrawingPixelFormat.Format32bppPArgb);
        using (var graphics = DrawingGraphics.FromImage(bitmap))
        {
            graphics.CompositingMode = DrawingCompositingMode.SourceCopy;
            graphics.Clear(System.Drawing.Color.Transparent);
            graphics.DrawImage(source, 0, 0, source.Width, source.Height);
        }

        var packedStride = checked(source.Width * 4);
        var pixels = GC.AllocateUninitializedArray<byte>(checked(packedStride * source.Height));
        var bounds = new System.Drawing.Rectangle(0, 0, source.Width, source.Height);
        var data = bitmap.LockBits(bounds, DrawingImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppPArgb);
        try
        {
            var absoluteStride = Math.Abs(data.Stride);
            if (absoluteStride < packedStride)
            {
                throw new PdfiumResourceLimitException("The signature decoder returned an invalid pixel stride.");
            }
            for (var row = 0; row < source.Height; row++)
            {
                var sourceRow = data.Stride >= 0
                    ? IntPtr.Add(data.Scan0, checked(row * data.Stride))
                    : IntPtr.Add(data.Scan0, checked((source.Height - 1 - row) * absoluteStride));
                Marshal.Copy(sourceRow, pixels, checked(row * packedStride), packedStride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return new SignatureImage(pixels, source.Width, source.Height);
    }

    private static void PreflightSignatureImages(PdfAnnotationSaveRequest request)
    {
        foreach (var signature in request.Pages.SelectMany(static page => page.Signatures))
        {
            _ = DecodeSignature(signature.ImageBase64);
        }
    }

    private static int ValidateAnnotationCount(int count)
    {
        if (count is < 0 or > PdfContractLimits.MaxCollectionCount)
        {
            throw new PdfiumResourceLimitException("The page annotation count exceeds the configured limit.");
        }
        return count;
    }

    private static void EnsureNoPendingAnnotationTransaction(WorkerDocument document)
    {
        if (document.AnnotationTransaction is not null)
        {
            throw new InvalidOperationException("The document has an annotation save awaiting atomic commit finalization.");
        }
    }

    private static void ValidateDocumentMutationIdentity(
        WorkerDocument document,
        ContentRevision expectedContentRevision,
        StructureRevision expectedStructureRevision)
    {
        if (document.ContentRevision != expectedContentRevision
            || document.StructureRevision != expectedStructureRevision)
        {
            throw new WorkerStaleIdentityException("The document mutation carries a stale revision.");
        }
    }

    private static void EnsurePageAssemblyAllowed(WorkerDocument document)
    {
        if (!document.Permissions.CanModify || !document.Permissions.CanAssemble)
        {
            throw new UnauthorizedAccessException("The document permissions deny page assembly changes.");
        }
    }

    private static PdfMetadata WithPageCount(PdfMetadata metadata, int pageCount) => new(
        pageCount,
        metadata.PdfVersion,
        metadata.Title,
        metadata.Author,
        metadata.Subject,
        metadata.Keywords,
        metadata.Creator,
        metadata.Producer,
        metadata.IsEncrypted,
        metadata.HasOutline,
        metadata.HasForms);

    private static string ResolveNativeFormValue(PdfiumFormFieldInfo field, FormValue value) => value.Kind switch
    {
        FormValueKind.Text when field.NativeFieldType == 6 => value.Text ?? string.Empty,
        FormValueKind.Choice when field.NativeFieldType is 4 or 5 => value.Text ?? string.Empty,
        FormValueKind.Choices when field.NativeFieldType == 5 => value.Choices.FirstOrDefault() ?? string.Empty,
        FormValueKind.Boolean when field.NativeFieldType is 2 or 3 => value.Boolean == true
            ? string.IsNullOrWhiteSpace(field.ExportValue) ? "Yes" : field.ExportValue
            : "Off",
        _ => throw new ArgumentException("The form value kind does not match the field type.", nameof(value))
    };

    private static FormFieldId CreateFieldId(
        DocumentId documentId,
        PageId pageId,
        int annotationIndex,
        string name)
    {
        var bytes = Encoding.UTF8.GetBytes($"{documentId.Value:N}|{pageId.Value:N}|{annotationIndex}|{name}");
        var hash = SHA256.HashData(bytes);
        return new FormFieldId(new Guid(hash.AsSpan(0, 16)));
    }

    private static string NonEmptyOption(string value, int index) =>
        string.IsNullOrWhiteSpace(value) ? $"Option {index + 1}" : value;

    private static WorkerRenderedBuffer RenderCore(
        PdfiumEngine engine,
        WorkerDocument document,
        RenderRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pageIndex = FindPageIndex(document, request.Key.PageId);
        using var page = engine.LoadPage(document.Handle, pageIndex)
            ?? throw engine.CreateException($"Unable to load page {pageIndex + 1}.");
        var (widthPoints, heightPoints) = engine.GetPageSize(page);
        var scale = request.Key.RasterScale.PhysicalPixelsPerPoint;
        var pageWidth = CheckedRasterDimension(widthPoints, scale);
        var pageHeight = CheckedRasterDimension(heightPoints, scale);
        if (request.Key.Rotation is PageRotation.Clockwise90 or PageRotation.Clockwise270)
        {
            (pageWidth, pageHeight) = (pageHeight, pageWidth);
        }

        var tile = request.Key.Tile.Validate();
        if (tile.X >= pageWidth || tile.Y >= pageHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The tile origin is outside the page raster.");
        }

        var interiorWidth = Math.Min(tile.InteriorWidth, pageWidth - tile.X);
        var interiorHeight = Math.Min(tile.InteriorHeight, pageHeight - tile.Y);
        var leftBleed = tile.X > 0 ? tile.BleedPixels : 0;
        var topBleed = tile.Y > 0 ? tile.BleedPixels : 0;
        var rightBleed = tile.X + interiorWidth < pageWidth ? tile.BleedPixels : 0;
        var bottomBleed = tile.Y + interiorHeight < pageHeight ? tile.BleedPixels : 0;
        var bitmapWidth = checked(interiorWidth + leftBleed + rightBleed);
        var bitmapHeight = checked(interiorHeight + topBleed + bottomBleed);

        using var bitmap = engine.CreateBitmap(bitmapWidth, bitmapHeight);
        engine.FillBitmap(bitmap, 0, 0, bitmapWidth, bitmapHeight);
        engine.RenderPageRegion(
            page,
            bitmap,
            document.Form,
            checked(-tile.X + leftBleed),
            checked(-tile.Y + topBleed),
            pageWidth,
            pageHeight,
            (int)request.Key.Rotation);
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = engine.CopyBitmapBytes(bitmap, bitmap.ByteLength);
        ApplyRenderMode(bytes, request.Key.Mode);
        return new WorkerRenderedBuffer(
            bytes,
            bitmapWidth,
            bitmapHeight,
            bitmap.Stride,
            PixelFormat.Bgra8Premultiplied,
            request.Key,
            request.Generation);
    }

    private static PageTextResult ExtractPageText(
        PdfiumEngine engine,
        WorkerDocument document,
        int pageIndex,
        PageTextRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var page = engine.LoadPage(document.Handle, pageIndex)
            ?? throw engine.CreateException($"Unable to load page {pageIndex + 1}.");
        using var textPage = engine.LoadTextPage(page);
        if (textPage is null)
        {
            return new PageTextResult(
                request.DocumentId,
                request.PageId,
                request.PageIndex,
                request.ContentRevision,
                string.Empty,
                []);
        }

        var count = engine.CountCharacters(textPage);
        if (count < 0 || count > MaximumTextCharactersPerPage)
        {
            throw new PdfiumResourceLimitException("The extracted page text exceeds the configured limit.");
        }

        var text = engine.GetText(textPage, 0, count);
        var spans = BuildVisualLineSpans(engine, textPage, text, cancellationToken);
        return new PageTextResult(
            request.DocumentId,
            request.PageId,
            request.PageIndex,
            request.ContentRevision,
            text,
            spans);
    }

    private static IReadOnlyList<TextSpan> BuildVisualLineSpans(
        PdfiumEngine engine,
        PdfiumTextPageHandle textPage,
        string text,
        CancellationToken cancellationToken)
    {
        var spans = new List<TextSpan>();
        var lineStart = -1;
        var lastTextIndex = -1;
        var left = 0d;
        var top = 0d;
        var right = 0d;
        var bottom = 0d;

        void Flush()
        {
            if (lineStart < 0 || lastTextIndex < lineStart || spans.Count >= PdfContractLimits.MaxCollectionCount)
            {
                lineStart = -1;
                return;
            }

            var length = lastTextIndex - lineStart + 1;
            var value = text.Substring(lineStart, length).TrimEnd();
            if (!string.IsNullOrWhiteSpace(value))
            {
                spans.Add(new TextSpan(
                    lineStart,
                    value,
                    new PdfRect(left, top, right, bottom),
                    Math.Max(1, bottom - top)));
            }
            lineStart = -1;
        }

        for (var index = 0; index < text.Length && spans.Count < PdfContractLimits.MaxCollectionCount; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (text[index] is '\r' or '\n')
            {
                Flush();
                continue;
            }

            // PDFium can report a near-zero-height rectangle for a word-space.
            // That rectangle must not split one visual line into several spans;
            // retain the character in the text range but derive geometry only
            // from visible glyphs.
            if (char.IsWhiteSpace(text[index]))
            {
                if (lineStart >= 0)
                {
                    lastTextIndex = index;
                }
                continue;
            }

            if (!engine.TryGetTextRect(textPage, index, out var charLeft, out var charTop, out var charRight, out var charBottom))
            {
                continue;
            }

            var normalizedLeft = Math.Min(charLeft, charRight);
            var normalizedRight = Math.Max(charLeft, charRight);
            var normalizedTop = Math.Min(charTop, charBottom);
            var normalizedBottom = Math.Max(charTop, charBottom);
            if (lineStart >= 0)
            {
                var currentHeight = Math.Max(1, bottom - top);
                var characterHeight = Math.Max(1, normalizedBottom - normalizedTop);
                var verticalOverlap = Math.Min(bottom, normalizedBottom) - Math.Max(top, normalizedTop);
                if (verticalOverlap < Math.Min(currentHeight, characterHeight) * 0.25)
                {
                    Flush();
                }
            }

            if (lineStart < 0)
            {
                lineStart = index;
                left = normalizedLeft;
                top = normalizedTop;
                right = normalizedRight;
                bottom = normalizedBottom;
            }
            else
            {
                left = Math.Min(left, normalizedLeft);
                top = Math.Min(top, normalizedTop);
                right = Math.Max(right, normalizedRight);
                bottom = Math.Max(bottom, normalizedBottom);
            }
            lastTextIndex = index;
        }

        Flush();
        return spans;
    }

    private static IReadOnlyList<SearchResult> SearchPageCore(
        PdfiumEngine engine,
        WorkerDocument document,
        int pageIndex,
        PageSearchRequest request,
        CancellationToken cancellationToken)
    {
        using var page = engine.LoadPage(document.Handle, pageIndex);
        if (page is null)
        {
            return [];
        }

        using var textPage = engine.LoadTextPage(page);
        if (textPage is null)
        {
            return [];
        }

        using var search = engine.StartSearch(
            textPage,
            request.Query,
            request.MatchCase,
            request.WholeWord);
        if (search is null)
        {
            return [];
        }

        var results = new List<SearchResult>();
        while (engine.FindNext(search))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (characterIndex, length) = engine.GetSearchResult(search);
            if (characterIndex < 0 || length <= 0)
            {
                continue;
            }

            var contextStart = Math.Max(0, characterIndex - 20);
            var contextEnd = Math.Min(
                engine.CountCharacters(textPage),
                characterIndex + length + 20);
            var context = engine.GetText(textPage, contextStart, contextEnd - contextStart);
            var rectangles = new List<PdfRect>(Math.Min(length, 1024));
            for (var offset = 0; offset < length && offset < 1024; offset++)
            {
                if (engine.TryGetTextRect(
                        textPage,
                        characterIndex + offset,
                        out var left,
                        out var top,
                        out var right,
                        out var bottom))
                {
                    rectangles.Add(new PdfRect(left, top, right, bottom));
                }
            }

            results.Add(new SearchResult(
                request.Page.DocumentId,
                request.Page.PageId,
                pageIndex,
                request.Page.ContentRevision,
                request.Generation,
                characterIndex,
                length,
                string.IsNullOrEmpty(context) ? request.Query : context,
                rectangles));
            if (results.Count >= PdfContractLimits.MaxCollectionCount)
            {
                break;
            }
        }

        return results;
    }

    private static IEnumerable<OutlineItem> ReadOutline(
        PdfiumEngine engine,
        WorkerDocument document,
        PdfiumBookmark first,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > PdfContractLimits.MaxOutlineDepth)
        {
            yield break;
        }

        var current = first;
        var count = 0;
        while (!current.IsNull && count++ < PdfContractLimits.MaxCollectionCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageIndex = engine.GetBookmarkPageIndex(document.Handle, current);
            PageId? pageId = pageIndex >= 0 && pageIndex < document.Pages.Length
                ? document.Pages[pageIndex].PageId
                : null;
            var children = ReadOutline(
                engine,
                document,
                engine.GetFirstBookmark(document.Handle, current),
                depth + 1,
                cancellationToken).ToArray();
            yield return new OutlineItem(
                string.IsNullOrWhiteSpace(engine.GetBookmarkTitle(current))
                    ? "Untitled"
                    : engine.GetBookmarkTitle(current),
                pageId,
                pageIndex >= 0 ? pageIndex : null,
                children,
                depth);
            current = engine.GetNextBookmark(document.Handle, current);
        }
    }

    private WorkerDocument ValidateRenderIdentity(RenderRequest request)
    {
        var document = GetDocument(request.Key.DocumentId);
        var pageIndex = FindPageIndex(document, request.Key.PageId);
        var page = document.Pages[pageIndex];
        if (page.ContentRevision != request.Key.ContentRevision
            || page.AppearanceRevision != request.Key.AppearanceRevision)
        {
            throw new WorkerStaleIdentityException("The render request carries stale page revisions.");
        }

        return document;
    }

    private (WorkerDocument Document, int PageIndex) ValidatePageIdentity(
        DocumentId documentId,
        PageId pageId,
        int pageIndex,
        PageContentRevision contentRevision)
    {
        var document = GetDocument(documentId);
        ValidatePageIndex(document, pageIndex);
        var page = document.Pages[pageIndex];
        if (page.PageId != pageId || page.ContentRevision != contentRevision)
        {
            throw new WorkerStaleIdentityException("The page request carries a stale identity or revision.");
        }

        return (document, pageIndex);
    }

    private WorkerDocument GetDocument(DocumentId documentId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_restartRequired)
            {
                throw new WorkerRestartRequiredException(
                    "This PDF worker was quarantined after an uncertain native mutation and must be restarted.");
            }
            return _documents.TryGetValue(documentId, out var document)
                ? document
                : throw new WorkerDocumentNotFoundException("The document is not open in this worker.");
        }
    }

    private void QuarantineAfterUncertainMutation(WorkerDocument document)
    {
        lock (_sync)
        {
            _restartRequired = true;
            if (_documents.TryGetValue(document.DocumentId, out var current)
                && ReferenceEquals(current, document))
            {
                _documents.Remove(document.DocumentId);
            }
        }

        // This callback runs on the sole PDFium engine lane. Closing the poisoned native
        // owners here prevents already-enqueued direct registry calls from successfully
        // observing or saving the uncertain in-memory mutation before the process exits.
        document.DisposeOnEngineLane();
    }

    private static int FindPageIndex(WorkerDocument document, PageId pageId)
    {
        for (var index = 0; index < document.Pages.Length; index++)
        {
            if (document.Pages[index].PageId == pageId)
            {
                return index;
            }
        }

        throw new WorkerStaleIdentityException("The page identity is not open in this document.");
    }

    private static void ValidatePageIndex(WorkerDocument document, int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= document.Pages.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }
    }

    private static int CheckedRasterDimension(float points, double scale)
    {
        if (!float.IsFinite(points) || !double.IsFinite(scale) || points <= 0 || scale <= 0)
        {
            throw new PdfiumResourceLimitException("The page raster geometry is invalid.");
        }

        var value = Math.Ceiling(points * scale);
        if (value is < 1 or > int.MaxValue)
        {
            throw new PdfiumResourceLimitException("The page raster dimension overflows the native API.");
        }

        return checked((int)value);
    }

    private static void ApplyRenderMode(byte[] pixels, RenderMode mode)
    {
        if (mode == RenderMode.Normal)
        {
            return;
        }

        for (var index = 0; index + 3 < pixels.Length; index += 4)
        {
            if (mode == RenderMode.Inverted)
            {
                pixels[index] = (byte)(255 - pixels[index]);
                pixels[index + 1] = (byte)(255 - pixels[index + 1]);
                pixels[index + 2] = (byte)(255 - pixels[index + 2]);
            }
            else
            {
                var luminance = (pixels[index + 2] * 77
                    + pixels[index + 1] * 150
                    + pixels[index] * 29) >> 8;
                var value = luminance >= 128 ? (byte)255 : (byte)0;
                pixels[index] = value;
                pixels[index + 1] = value;
                pixels[index + 2] = value;
            }
        }
    }

    private async ValueTask DisposeWorkerDocumentAsync(WorkerDocument document)
    {
        await _engineLane.InvokeAsync(
            _ => document.DisposeOnEngineLane(),
            CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class WorkerDocument
    {
        internal WorkerDocument(
            DocumentId documentId,
            PdfiumDocumentHandle handle,
            PdfiumFormHandle? form,
            PdfMetadata metadata,
            PdfPermissions permissions,
            ImmutableArray<WorkerPage> pages)
        {
            DocumentId = documentId;
            Handle = handle;
            Form = form;
            Metadata = metadata;
            Permissions = permissions;
            Pages = pages;
            ContentRevision = ContentRevision.Initial;
            SavedRevision = ContentRevision.Initial;
            StructureRevision = StructureRevision.Initial;
        }

        internal DocumentId DocumentId { get; }
        internal PdfiumDocumentHandle Handle { get; }
        internal PdfiumFormHandle? Form { get; }
        internal PdfMetadata Metadata { get; set; }
        internal PdfPermissions Permissions { get; }
        internal ImmutableArray<WorkerPage> Pages { get; set; }
        internal ContentRevision ContentRevision { get; set; }
        internal ContentRevision SavedRevision { get; set; }
        internal StructureRevision StructureRevision { get; set; }
        internal StagedAnnotationTransaction? AnnotationTransaction { get; set; }
        internal Guid? LastFinalizedAnnotationTransactionId { get; set; }
        internal bool LastAnnotationTransactionCommitted { get; set; }

        internal void DisposeOnEngineLane()
        {
            Form?.Dispose();
            Handle.Dispose();
        }
    }

    private sealed class WorkerPage(
        PageId pageId,
        PageContentRevision contentRevision,
        PageAppearanceRevision appearanceRevision)
    {
        internal PageId PageId { get; } = pageId;
        internal PageContentRevision ContentRevision { get; set; } = contentRevision;
        internal PageAppearanceRevision AppearanceRevision { get; set; } = appearanceRevision;
    }

    private sealed record StagedAnnotationTransaction(Guid TransactionId);

    private readonly record struct AnnotationRectangle(
        float Left,
        float Bottom,
        float Right,
        float Top);

    private sealed record SignatureImage(byte[] Pixels, int Width, int Height);
}
