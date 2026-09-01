using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ElliePdf.Pdf.Contracts;
using ElliePdf.Pdf.Transport;
using ElliePdf.Pdfium;
using Microsoft.Win32.SafeHandles;

namespace ElliePdf.Pdfium.Worker;

/// <summary>Authenticated, pathless IPC endpoint for one worker process.</summary>
public sealed class PdfWorkerServer : IAsyncDisposable
{
    private const int MaximumConcurrentRequests = 64;
    private readonly Guid _sessionId;
    private readonly byte[] _secret;
    private readonly TransportAuthenticator _authenticator;
    private readonly WorkerDocumentRegistry _documents;
    private readonly WorkerPixelLeasePool _leases;
    private readonly LengthPrefixedFrameCodec _codec = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _requestGate = new(MaximumConcurrentRequests, MaximumConcurrentRequests);
    private readonly ConcurrentDictionary<Guid, OperationState> _operations = new();
    private readonly ConcurrentDictionary<Guid, byte> _consumedHandleIds = new();
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    public PdfWorkerServer(Guid sessionId, LaunchSecret secret, string? nativeBaseDirectory = null)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("The worker session id is required.", nameof(sessionId));
        }

        _sessionId = sessionId;
        ArgumentNullException.ThrowIfNull(secret);
        _secret = secret.ToArray();
        _authenticator = new TransportAuthenticator(sessionId, secret);
        _documents = new WorkerDocumentRegistry(nativeBaseDirectory);
        _leases = new WorkerPixelLeasePool(sessionId);
    }

    public async Task RunAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _documents.Ready.ConfigureAwait(false);

        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            while (!connection.IsCancellationRequested)
            {
                var envelope = await _codec.ReadAsync(stream, connection.Token).ConfigureAwait(false);
                if (envelope is null)
                {
                    break;
                }

                _authenticator.Validate(envelope, updateWatermark: envelope.Kind == TransportMessageKind.Request);
                await DispatchEnvelopeAsync(stream, envelope, connection.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (connection.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var operation in _operations.Values)
            {
                operation.Cancellation.Cancel();
            }

            var pending = _operations.Values.Select(static operation => operation.Task).ToArray();
            if (pending.Length > 0)
            {
                try
                {
                    await Task.WhenAll(pending).ConfigureAwait(false);
                }
                catch
                {
                    // Each request reports its own bounded protocol error. Connection teardown wins here.
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        foreach (var operation in _operations.Values)
        {
            operation.Cancellation.Cancel();
        }

        await _leases.DisposeAsync().ConfigureAwait(false);
        await _documents.DisposeAsync().ConfigureAwait(false);
        _requestGate.Dispose();
        _writeGate.Dispose();
        _shutdown.Dispose();
        Array.Clear(_secret);
    }

    private async Task DispatchEnvelopeAsync(Stream stream, TransportEnvelope envelope, CancellationToken connectionToken)
    {
        switch (envelope.Kind)
        {
            case TransportMessageKind.Request:
                await StartRequestAsync(stream, envelope, connectionToken).ConfigureAwait(false);
                return;
            case TransportMessageKind.Cancel:
                await HandleCancellationAsync(stream, envelope).ConfigureAwait(false);
                return;
            case TransportMessageKind.Heartbeat:
                await HandleHeartbeatAsync(stream, envelope).ConfigureAwait(false);
                return;
            case TransportMessageKind.LeaseAck:
                await HandleLeaseAcknowledgementAsync(stream, envelope).ConfigureAwait(false);
                return;
            case TransportMessageKind.LeaseRelease:
                await HandleLeaseReleaseAsync(stream, envelope).ConfigureAwait(false);
                return;
            default:
                throw new TransportProtocolException("The message kind is not valid for the worker endpoint.");
        }
    }

    private async Task StartRequestAsync(
        Stream stream,
        TransportEnvelope envelope,
        CancellationToken connectionToken)
    {
        if (envelope.DeadlineUtc is { } deadline && deadline <= DateTimeOffset.UtcNow)
        {
            await WriteErrorAsync(
                stream,
                envelope,
                "deadline_expired",
                "The operation deadline expired.",
                transient: true).ConfigureAwait(false);
            return;
        }

        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        if (envelope.DeadlineUtc is { } requestDeadline)
        {
            operationCancellation.CancelAfter(requestDeadline - DateTimeOffset.UtcNow);
        }

        var state = new OperationState(operationCancellation);
        if (!_operations.TryAdd(envelope.CorrelationId, state))
        {
            operationCancellation.Dispose();
            await WriteErrorAsync(
                stream,
                envelope,
                "duplicate_correlation",
                "The request identity is already active.").ConfigureAwait(false);
            return;
        }

        // The request is deliberately concurrent with the read loop. The wrapper
        // owns cleanup and observes all failures so no discarded continuation can
        // become an unobserved task.
        state.Task = ExecuteRequestAndCleanupAsync(
            stream,
            envelope,
            operationCancellation.Token);
    }

    private async Task ExecuteRequestAndCleanupAsync(
        Stream stream,
        TransportEnvelope envelope,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteRequestAsync(stream, envelope, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // ExecuteRequestAsync reports operation failures through the protocol.
            // A transport failure during that report is terminal for this request,
            // but must still be observed before the operation is removed.
        }
        finally
        {
            if (_operations.TryRemove(envelope.CorrelationId, out var removed))
            {
                removed.Cancellation.Dispose();
            }
        }
    }

    private async Task ExecuteRequestAsync(Stream stream, TransportEnvelope envelope, CancellationToken cancellationToken)
    {
        try
        {
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var request = Deserialize(envelope.Payload, WorkerProtocolJsonContext.Default.WorkerRequestPayload);
                request.Validate();
                var response = await ExecuteOperationAsync(request, envelope, cancellationToken).ConfigureAwait(false);
                await WriteEnvelopeAsync(stream, response, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _requestGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(stream, envelope, "cancelled", "The operation was cancelled.", transient: true).ConfigureAwait(false);
        }
        catch (WorkerStaleIdentityException)
        {
            await WriteErrorAsync(stream, envelope, "stale_identity", "The operation identity is stale.").ConfigureAwait(false);
        }
        catch (WorkerDocumentNotFoundException)
        {
            await WriteErrorAsync(stream, envelope, "document_not_found", "The document is no longer open.").ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await WriteErrorAsync(stream, envelope, "deadline_expired", "The operation deadline expired.", transient: true).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            await WriteErrorAsync(stream, envelope, "authority_denied", "The brokered authority was rejected.").ConfigureAwait(false);
        }
        catch (WorkerRestartRequiredException)
        {
            try
            {
                await WriteErrorAsync(
                    stream,
                    envelope,
                    "worker_restart_required",
                    "The isolated PDF worker must restart before another operation.",
                    transient: true).ConfigureAwait(false);
            }
            finally
            {
                _shutdown.Cancel();
            }
        }
        catch (Exception exception)
        {
            await WriteErrorAsync(stream, envelope, MapErrorCode(exception), SafeErrorMessage(exception)).ConfigureAwait(false);
        }
    }

    private async ValueTask<TransportEnvelope> ExecuteOperationAsync(
        WorkerRequestPayload request,
        TransportEnvelope envelope,
        CancellationToken cancellationToken)
    {
        switch (request.Operation)
        {
            case WorkerOperation.OpenDocument:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.OpenDocumentCommand);
                command.SourceHandle.Validate();
                if (command.SourceHandle.SessionId != _sessionId
                    || command.SourceHandle.Access != BrokeredHandleAccess.ReadOnlySource)
                {
                    throw new UnauthorizedAccessException();
                }

                if (!_consumedHandleIds.TryAdd(command.SourceHandle.HandleId, 0))
                {
                    throw new UnauthorizedAccessException();
                }

                var sourceHandle = new SafeFileHandle(checked((nint)command.SourceHandle.NativeHandleValue), ownsHandle: true);
                var openResult = await _documents.OpenAsync(command.Request, sourceHandle, cancellationToken).ConfigureAwait(false);
                return CreateResponse(envelope, request.Operation, new OpenDocumentResponse(openResult), WorkerProtocolJsonContext.Default.OpenDocumentResponse);
            }
            case WorkerOperation.GetMetadata:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.DocumentCommand);
                var result = await _documents.GetMetadataAsync(command.DocumentId, cancellationToken).ConfigureAwait(false);
                return CreateResponse(envelope, request.Operation, new MetadataResponse(result), WorkerProtocolJsonContext.Default.MetadataResponse);
            }
            case WorkerOperation.GetPageMetadata:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.PageMetadataCommand);
                var result = await _documents.GetPageMetadataAsync(command.DocumentId, command.PageIndex, cancellationToken).ConfigureAwait(false);
                return CreateResponse(envelope, request.Operation, new PageMetadataResponse(result), WorkerProtocolJsonContext.Default.PageMetadataResponse);
            }
            case WorkerOperation.Render:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.RenderCommand);
                var pixels = await _documents.RenderAsync(command.Request, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var lease = _leases.Publish(pixels);
                return CreateResponse(envelope, request.Operation, new RenderLeaseResponse(lease), WorkerProtocolJsonContext.Default.RenderLeaseResponse);
            }
            case WorkerOperation.GetPageText:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.PageTextCommand);
                var result = await _documents.GetPageTextAsync(command.Request, cancellationToken).ConfigureAwait(false);
                return CreateResponse(envelope, request.Operation, new PageTextResponse(result), WorkerProtocolJsonContext.Default.PageTextResponse);
            }
            case WorkerOperation.SearchPage:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.SearchPageCommand);
                var result = await _documents.SearchPageAsync(command.Request, cancellationToken).ConfigureAwait(false);
                return CreateResponse(envelope, request.Operation, new SearchPageResponse([.. result]), WorkerProtocolJsonContext.Default.SearchPageResponse);
            }
            case WorkerOperation.GetOutline:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.DocumentCommand);
                var result = await _documents.GetOutlineAsync(command.DocumentId, cancellationToken).ConfigureAwait(false);
                return CreateResponse(envelope, request.Operation, new OutlineResponse(result), WorkerProtocolJsonContext.Default.OutlineResponse);
            }
            case WorkerOperation.GetPageLinks:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.PageMetadataCommand);
                var result = await _documents.GetPageLinksAsync(command.DocumentId, command.PageIndex, cancellationToken).ConfigureAwait(false);
                return CreateResponse(envelope, request.Operation, new PageLinksResponse(result), WorkerProtocolJsonContext.Default.PageLinksResponse);
            }
            case WorkerOperation.GetFormWidgets:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.PageMetadataCommand);
                var result = await _documents.GetFormWidgetsAsync(command.DocumentId, command.PageIndex, cancellationToken).ConfigureAwait(false);
                return CreateResponse(envelope, request.Operation, new FormWidgetsResponse(result), WorkerProtocolJsonContext.Default.FormWidgetsResponse);
            }
            case WorkerOperation.GetPermissions:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.DocumentCommand);
                var result = await _documents.GetPermissionsAsync(command.DocumentId, cancellationToken).ConfigureAwait(false);
                return CreateResponse(envelope, request.Operation, new PermissionsResponse(result), WorkerProtocolJsonContext.Default.PermissionsResponse);
            }
            case WorkerOperation.ApplyFormValue:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.ApplyFormValueCommand);
                var result = await _documents.ApplyFormValueAsync(command.Change, cancellationToken).ConfigureAwait(false);
                return CreateResponse(envelope, request.Operation, new DocumentMutationResponse(result), WorkerProtocolJsonContext.Default.DocumentMutationResponse);
            }
            case WorkerOperation.InvokePushButton:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.InvokePushButtonCommand);
                var result = await _documents.InvokePushButtonAsync(command.Invocation, cancellationToken).ConfigureAwait(false);
                return CreateResponse(envelope, request.Operation, new DocumentMutationResponse(result), WorkerProtocolJsonContext.Default.DocumentMutationResponse);
            }
            case WorkerOperation.RotatePage:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.RotatePageCommand);
                EnsureDocumentIdentity(envelope, command.Request.DocumentId);
                var result = await _documents.RotatePageAsync(command.Request, cancellationToken).ConfigureAwait(false);
                return CreateResponse(envelope, request.Operation, new DocumentMutationResponse(result), WorkerProtocolJsonContext.Default.DocumentMutationResponse);
            }
            case WorkerOperation.DeletePage:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.DeletePageCommand);
                EnsureDocumentIdentity(envelope, command.Request.DocumentId);
                var result = await _documents.DeletePageAsync(command.Request, cancellationToken).ConfigureAwait(false);
                return CreateResponse(envelope, request.Operation, new DocumentMutationResponse(result), WorkerProtocolJsonContext.Default.DocumentMutationResponse);
            }
            case WorkerOperation.MergeOrderedPages:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.MergeOrderedPagesCommand);
                command.TargetHandle.Validate();
                if (command.TargetHandle.SessionId != _sessionId
                    || command.TargetHandle.Access != BrokeredHandleAccess.TemporaryWrite
                    || command.TargetHandle.TransactionId is null)
                {
                    throw new UnauthorizedAccessException();
                }
                if (!_consumedHandleIds.TryAdd(command.TargetHandle.HandleId, 0))
                {
                    throw new UnauthorizedAccessException();
                }

                var targetHandle = new SafeFileHandle(checked((nint)command.TargetHandle.NativeHandleValue), ownsHandle: true);
                await _documents.MergeOrderedPagesAsync(
                    command.Request,
                    targetHandle,
                    cancellationToken).ConfigureAwait(false);
                return CreateResponse(envelope, request.Operation, new AcknowledgementResponse(true), WorkerProtocolJsonContext.Default.AcknowledgementResponse);
            }
            case WorkerOperation.StageAnnotations:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.StageAnnotationsCommand);
                EnsureDocumentIdentity(envelope, command.Request.DocumentId);
                command.TargetHandle.Validate();
                if (command.TargetHandle.SessionId != _sessionId
                    || command.TargetHandle.Access != BrokeredHandleAccess.TemporaryWrite
                    || command.TargetHandle.TransactionId is null
                    || !_consumedHandleIds.TryAdd(command.TargetHandle.HandleId, 0))
                {
                    throw new UnauthorizedAccessException();
                }

                var targetHandle = new SafeFileHandle(checked((nint)command.TargetHandle.NativeHandleValue), ownsHandle: true);
                var snapshot = await _documents.StageAnnotationsAsync(
                    command.Request,
                    targetHandle,
                    cancellationToken).ConfigureAwait(false);
                return CreateResponse(envelope, request.Operation, new DocumentMutationResponse(snapshot), WorkerProtocolJsonContext.Default.DocumentMutationResponse);
            }
            case WorkerOperation.FinalizeAnnotationTransaction:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.FinalizeAnnotationTransactionCommand);
                EnsureDocumentIdentity(envelope, command.DocumentId);
                var snapshot = await _documents.FinalizeAnnotationTransactionAsync(
                    command.DocumentId,
                    command.TransactionId,
                    command.Committed,
                    cancellationToken).ConfigureAwait(false);
                return CreateResponse(envelope, request.Operation, new DocumentMutationResponse(snapshot), WorkerProtocolJsonContext.Default.DocumentMutationResponse);
            }
            case WorkerOperation.SaveFlattenedCopy:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.SaveFlattenedCopyCommand);
                EnsureDocumentIdentity(envelope, command.Request.DocumentId);
                command.TargetHandle.Validate();
                if (command.TargetHandle.SessionId != _sessionId
                    || command.TargetHandle.Access != BrokeredHandleAccess.TemporaryWrite
                    || command.TargetHandle.TransactionId is null
                    || !_consumedHandleIds.TryAdd(command.TargetHandle.HandleId, 0))
                {
                    throw new UnauthorizedAccessException();
                }

                var targetHandle = new SafeFileHandle(checked((nint)command.TargetHandle.NativeHandleValue), ownsHandle: true);
                await _documents.SaveFlattenedCopyAsync(
                    command.Request,
                    targetHandle,
                    cancellationToken).ConfigureAwait(false);
                return CreateResponse(envelope, request.Operation, new AcknowledgementResponse(true), WorkerProtocolJsonContext.Default.AcknowledgementResponse);
            }
            case WorkerOperation.SaveDocument:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.SaveDocumentCommand);
                command.TargetHandle.Validate();
                if (command.TargetHandle.SessionId != _sessionId
                    || command.TargetHandle.Access != BrokeredHandleAccess.TemporaryWrite
                    || command.TargetHandle.TransactionId is null)
                {
                    throw new UnauthorizedAccessException();
                }
                if (!_consumedHandleIds.TryAdd(command.TargetHandle.HandleId, 0))
                {
                    throw new UnauthorizedAccessException();
                }

                var targetHandle = new SafeFileHandle(checked((nint)command.TargetHandle.NativeHandleValue), ownsHandle: true);
                await _documents.SaveAsync(
                    command.DocumentId,
                    command.CapturedRevision,
                    targetHandle,
                    cancellationToken).ConfigureAwait(false);
                return CreateResponse(envelope, request.Operation, new AcknowledgementResponse(true), WorkerProtocolJsonContext.Default.AcknowledgementResponse);
            }
            case WorkerOperation.CloseDocument:
            {
                var command = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.DocumentCommand);
                var accepted = await _documents.CloseAsync(command.DocumentId, cancellationToken).ConfigureAwait(false);
                _authenticator.ForgetDocument(command.DocumentId.Value);
                return CreateResponse(envelope, request.Operation, new AcknowledgementResponse(accepted), WorkerProtocolJsonContext.Default.AcknowledgementResponse);
            }
            case WorkerOperation.Shutdown:
            {
                _ = Deserialize(request.Arguments, WorkerProtocolJsonContext.Default.EmptyPayload);
                var response = CreateResponse(envelope, request.Operation, new AcknowledgementResponse(true), WorkerProtocolJsonContext.Default.AcknowledgementResponse);
                _shutdown.Cancel();
                return response;
            }
            default:
                throw new TransportProtocolException("The worker operation is unsupported.");
        }
    }

    private async Task HandleCancellationAsync(Stream stream, TransportEnvelope envelope)
    {
        var message = Deserialize(envelope.Payload, TransportJsonContext.Default.CancelMessage).Validate();
        var accepted = _operations.TryGetValue(message.TargetCorrelationId, out var operation);
        if (accepted)
        {
            operation!.Cancellation.Cancel();
        }

        await WriteAcknowledgementAsync(stream, envelope, accepted).ConfigureAwait(false);
    }

    private async Task HandleHeartbeatAsync(Stream stream, TransportEnvelope envelope)
    {
        var heartbeat = Deserialize(envelope.Payload, TransportJsonContext.Default.HeartbeatMessage);
        var response = TransportEnvelope.Create(
            TransportMessageKind.Heartbeat,
            _secret,
            envelope.Identity,
            new HeartbeatMessage(DateTimeOffset.UtcNow, heartbeat.Sequence),
            TransportJsonContext.Default.HeartbeatMessage,
            envelope.CorrelationId);
        await WriteEnvelopeAsync(stream, response, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task HandleLeaseAcknowledgementAsync(Stream stream, TransportEnvelope envelope)
    {
        var message = Deserialize(envelope.Payload, TransportJsonContext.Default.LeaseAckMessage);
        await WriteAcknowledgementAsync(stream, envelope, _leases.Acknowledge(message.LeaseId)).ConfigureAwait(false);
    }

    private async Task HandleLeaseReleaseAsync(Stream stream, TransportEnvelope envelope)
    {
        var message = Deserialize(envelope.Payload, TransportJsonContext.Default.LeaseReleaseMessage);
        await WriteAcknowledgementAsync(stream, envelope, _leases.Release(message.LeaseId)).ConfigureAwait(false);
    }

    private async Task WriteAcknowledgementAsync(Stream stream, TransportEnvelope request, bool accepted)
    {
        var response = TransportEnvelope.Create(
            TransportMessageKind.Response,
            _secret,
            request.Identity,
            new AcknowledgementResponse(accepted),
            WorkerProtocolJsonContext.Default.AcknowledgementResponse,
            request.CorrelationId);
        await WriteEnvelopeAsync(stream, response, CancellationToken.None).ConfigureAwait(false);
    }

    private TransportEnvelope CreateResponse<T>(
        TransportEnvelope request,
        WorkerOperation operation,
        T value,
        JsonTypeInfo<T> typeInfo)
    {
        var result = JsonSerializer.SerializeToElement(value, typeInfo);
        var payload = new WorkerResponsePayload(operation, result);
        return TransportEnvelope.Create(
            TransportMessageKind.Response,
            _secret,
            request.Identity,
            payload,
            WorkerProtocolJsonContext.Default.WorkerResponsePayload,
            request.CorrelationId);
    }

    private async Task WriteErrorAsync(
        Stream stream,
        TransportEnvelope request,
        string code,
        string message,
        bool transient = false)
    {
        var error = new TransportError(code, message, transient).Validate();
        var response = TransportEnvelope.Create(
            TransportMessageKind.Error,
            _secret,
            request.Identity,
            error,
            TransportJsonContext.Default.TransportError,
            request.CorrelationId);
        await WriteEnvelopeAsync(stream, response, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WriteEnvelopeAsync(Stream stream, TransportEnvelope envelope, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _codec.WriteAsync(stream, envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static T Deserialize<T>(JsonElement element, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return element.Deserialize(typeInfo)
                ?? throw new TransportProtocolException("The operation payload is missing.");
        }
        catch (JsonException exception)
        {
            throw new TransportProtocolException("The operation payload is malformed.", exception);
        }
    }

    private static void EnsureDocumentIdentity(TransportEnvelope envelope, ElliePdf.Domain.Documents.DocumentId documentId)
    {
        if (envelope.Identity.DocumentId != documentId.Value)
        {
            throw new UnauthorizedAccessException("The request identity does not match the document command.");
        }
    }

    private static string MapErrorCode(Exception exception) => exception switch
    {
        PdfiumNativeException { ErrorCode: 4 } => "password_required_or_incorrect",
        PdfiumResourceLimitException => "resource_limit",
        TransportProtocolException => "protocol_error",
        UnauthorizedAccessException => "access_denied",
        ArgumentException => "invalid_argument",
        InvalidDataException => "invalid_pdf",
        IOException => "io_error",
        _ => "worker_error"
    };

    private static string SafeErrorMessage(Exception exception) => exception switch
    {
        PdfiumNativeException { ErrorCode: 4 } => "A password is required or the supplied password is incorrect.",
        PdfiumResourceLimitException => "The document exceeds a configured safety limit.",
        TransportProtocolException => "The worker rejected an invalid protocol message.",
        UnauthorizedAccessException => "The requested PDF action is not allowed.",
        ArgumentException => "The operation arguments are invalid.",
        InvalidDataException => "The document data is invalid or unsupported.",
        IOException => "The worker could not complete the I/O operation.",
        _ => "The PDF worker could not complete the operation."
    };

    private sealed class OperationState(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
    }

}
