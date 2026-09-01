using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;
using ElliePdf.Pdf.Transport;
using ElliePdf.Telemetry;

namespace ElliePdf.Pdf.Client;

/// <summary>
/// Owns the single, isolated PDF worker for an app instance. The client is the only layer that
/// translates UI-side file paths into duplicated, least-authority operating-system handles.
/// </summary>
public sealed class PdfWorkerClient : IPdfEngineClient, IPdfPageMergeClient
{
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(5);
    private const int CrashQuarantineThreshold = 3;
    private readonly PdfWorkerClientOptions _options;
    private readonly SemaphoreSlim _startupGate = new(1, 1);
    private readonly SemaphoreSlim _sessionLifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, PendingRequest> _pending = new();
    private readonly ConcurrentDictionary<Guid, int> _activeSharedLeases = new();
    private readonly TaskCompletionSource<bool> _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _stateSync = new();
    private readonly Lock _crashSync = new();
    private readonly Dictionary<DocumentId, Queue<DateTimeOffset>> _documentCrashes = [];
    private readonly HashSet<DocumentId> _quarantinedDocuments = [];
    private readonly HashSet<(long Generation, DocumentId DocumentId)> _attributedCrashes = [];
    private WorkerJob? _job;
    private Process? _process;
    private PipeStream? _pipe;
    private CancellationTokenSource? _connectionCancellation;
    private Task? _readLoop;
    private Task? _heartbeatLoop;
    private byte[]? _secret;
    private Guid _sessionId;
    private long _workerGeneration;
    private long _heartbeatSequence;
    private int _activeSessionCount;
    private int _disposed;

    /// <summary>Isolation mode of the currently running worker, if one has been started.</summary>
    public WorkerSandboxMode? ActiveSandboxMode { get; private set; }

    public PdfWorkerClient(PdfWorkerClientOptions? options = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The ElliePdf worker client requires Windows.");
        }

        _options = (options ?? new PdfWorkerClientOptions()).Validate();
    }

    public PdfContractVersion ContractVersion => PdfContractVersion.Current;

    public string WorkerExecutablePath => _options.WorkerExecutablePath;

    public bool WorkerBundleExists => File.Exists(_options.WorkerExecutablePath);

    /// <summary>
    /// Aggregated, process-local resource facts for the opt-in benchmark driver.
    /// No document identity, file path, lease name, or request payload is exposed.
    /// </summary>
    public PdfWorkerResourceSnapshot GetBenchmarkResourceSnapshot()
    {
        Process? process;
        lock (_stateSync)
        {
            process = _process;
        }

        var privateBytes = 0L;
        var workingSetBytes = 0L;
        var cpuMilliseconds = 0d;
        if (process is not null)
        {
            try
            {
                process.Refresh();
                if (!process.HasExited)
                {
                    privateBytes = Math.Max(0, process.PrivateMemorySize64);
                    workingSetBytes = Math.Max(0, process.WorkingSet64);
                    cpuMilliseconds = Math.Max(0, process.TotalProcessorTime.TotalMilliseconds);
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            catch (NotSupportedException) { }
        }

        return new PdfWorkerResourceSnapshot(
            privateBytes,
            workingSetBytes,
            cpuMilliseconds,
            _activeSharedLeases.Values.Sum(static bytes => (long)bytes),
            _activeSharedLeases.Count);
    }

    public bool IsQuarantined(DocumentId documentId)
    {
        lock (_crashSync)
        {
            return _quarantinedDocuments.Contains(documentId);
        }
    }

    /// <summary>Explicit user action is required before retrying a repeatedly crashing document.</summary>
    public bool ClearQuarantine(DocumentId documentId)
    {
        lock (_crashSync)
        {
            _documentCrashes.Remove(documentId);
            return _quarantinedDocuments.Remove(documentId);
        }
    }

    public async ValueTask<IPdfEngineSession> OpenSessionAsync(
        DocumentOpenRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.ContractVersion.Validate();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (IsQuarantined(request.DocumentId))
        {
            throw new PdfWorkerQuarantinedException();
        }

        await _sessionLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var reservationOwned = false;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (IsQuarantined(request.DocumentId))
                throw new PdfWorkerQuarantinedException();
            Interlocked.Increment(ref _activeSessionCount);
            reservationOwned = true;
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

            Process process;
            Guid sessionId;
            long generation;
            lock (_stateSync)
            {
                process = _process ?? throw new PdfWorkerUnavailableException("The PDF worker is not running.");
                sessionId = _sessionId;
                generation = _workerGeneration;
            }

            var path = request.Source.Validate().Value;
            if (!Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException("The source handle must resolve to an absolute broker-side path.", nameof(request));
            }

            var handleId = Guid.NewGuid();
            nint remoteHandle;
            using (var source = WorkerHandleBroker.OpenReadOnly(path))
            {
                remoteHandle = WorkerHandleBroker.DuplicateInto(source, process);
            }

            var sanitizedRequest = new DocumentOpenRequest(
                request.DocumentId,
                new PdfSourceHandle(handleId.ToString("N", System.Globalization.CultureInfo.InvariantCulture)),
                request.Password);
            var descriptor = new BrokeredHandleDescriptor
            {
                HandleId = handleId,
                SessionId = sessionId,
                NativeHandleValue = checked((long)remoteHandle),
                Access = BrokeredHandleAccess.ReadOnlySource,
                ExpiresAtUtc = DateTimeOffset.UtcNow + _options.DefaultOperationTimeout
            };
            var command = new OpenDocumentCommand(sanitizedRequest, descriptor);
            var identity = TransportIdentity.ForDocument(sessionId, request.DocumentId, ContentRevision.Initial);

            var response = await SendOperationAsync(
                WorkerOperation.OpenDocument,
                identity,
                command,
                WorkerProtocolJsonContext.Default.OpenDocumentCommand,
                WorkerProtocolJsonContext.Default.OpenDocumentResponse,
                _options.DefaultOperationTimeout,
                cancellationToken).ConfigureAwait(false);
            reservationOwned = false;
            return new PdfWorkerSession(this, response.Result, generation);
        }
        finally
        {
            try
            {
                if (reservationOwned)
                    await ReleaseSessionUnderLifecycleGateAsync().ConfigureAwait(false);
            }
            finally
            {
                _sessionLifecycleGate.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        try
        {
            Task? readLoop;
            Task? heartbeatLoop;
            await _sessionLifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                Guid session;
                lock (_stateSync)
                {
                    session = _sessionId;
                }

                if (session != Guid.Empty)
                {
                    var identity = TransportIdentity.ForSession(session);
                    var empty = new EmptyPayload();
                    try
                    {
                        _ = await SendOperationAsync(
                            WorkerOperation.Shutdown,
                            identity,
                            empty,
                            WorkerProtocolJsonContext.Default.EmptyPayload,
                            WorkerProtocolJsonContext.Default.AcknowledgementResponse,
                            TimeSpan.FromSeconds(1),
                            CancellationToken.None,
                            allowDisposed: true).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Closing the Job Object below is the authoritative orphan cleanup.
                    }
                }
            }
            finally
            {
                try
                {
                    try
                    {
                        FailConnection(
                            new ObjectDisposedException(nameof(PdfWorkerClient)),
                            expectedGeneration: null,
                            terminate: true,
                            reportExitedAsCrash: false);
                    }
                    catch
                    {
                        // Async disposal is best-effort after the connection has been detached.
                    }

                    lock (_stateSync)
                    {
                        readLoop = _readLoop;
                        heartbeatLoop = _heartbeatLoop;
                        _readLoop = null;
                        _heartbeatLoop = null;
                    }

                    try
                    {
                        await AwaitConnectionLoopsAsync(readLoop, heartbeatLoop).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Loop failures are already translated to connection failure. Disposal
                        // continues with process-tree cleanup if a loop still exits exceptionally.
                    }

                    try
                    {
                        _job?.Dispose();
                    }
                    catch
                    {
                        // Job closure is attempted after FailConnection has already killed the tree.
                    }
                    _job = null;
                }
                finally
                {
                    _sessionLifecycleGate.Release();
                }
            }
        }
        finally
        {
            // SemaphoreSlim.Dispose is intentionally omitted: wait/release can still be
            // completing on failed operations, and these gates never expose WaitHandle.
            _disposeCompletion.TrySetResult(true);
        }
    }

    internal void EnsureGeneration(long generation)
    {
        lock (_stateSync)
        {
            if (generation != _workerGeneration || _process is null || _process.HasExited)
            {
                throw new PdfWorkerUnavailableException("The PDF worker session ended and must be reopened.");
            }
        }
    }

    internal async ValueTask<PdfMetadata> GetMetadataAsync(
        DocumentSnapshot snapshot,
        long generation,
        CancellationToken cancellationToken)
    {
        EnsureGeneration(generation);
        var response = await SendOperationAsync(
            WorkerOperation.GetMetadata,
            CurrentDocumentIdentity(snapshot),
            new DocumentCommand(snapshot.Id),
            WorkerProtocolJsonContext.Default.DocumentCommand,
            WorkerProtocolJsonContext.Default.MetadataResponse,
            _options.DefaultOperationTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.Metadata;
    }

    internal async ValueTask<PageMetadata> GetPageMetadataAsync(
        DocumentSnapshot snapshot,
        long generation,
        int pageIndex,
        CancellationToken cancellationToken)
    {
        EnsureGeneration(generation);
        var response = await SendOperationAsync(
            WorkerOperation.GetPageMetadata,
            CurrentDocumentIdentity(snapshot),
            new PageMetadataCommand(snapshot.Id, pageIndex),
            WorkerProtocolJsonContext.Default.PageMetadataCommand,
            WorkerProtocolJsonContext.Default.PageMetadataResponse,
            _options.DefaultOperationTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.Metadata;
    }

    internal async ValueTask<IPixelBufferLease> RenderAsync(
        long generation,
        RenderRequest request,
        CancellationToken cancellationToken)
    {
        EnsureGeneration(generation);
        Guid session;
        lock (_stateSync)
        {
            session = _sessionId;
        }

        var timeout = request.Deadline - DateTimeOffset.UtcNow;
        if (timeout <= TimeSpan.Zero)
        {
            throw new TimeoutException("The render deadline has expired.");
        }

        var response = await SendOperationAsync(
            WorkerOperation.Render,
            TransportIdentity.ForRender(session, request.Key, request.Generation),
            new RenderCommand(request),
            WorkerProtocolJsonContext.Default.RenderCommand,
            WorkerProtocolJsonContext.Default.RenderLeaseResponse,
            timeout,
            cancellationToken).ConfigureAwait(false);
        return await AcquirePixelLeaseAsync(response.Lease, generation, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<PageTextResult> GetPageTextAsync(
        long generation,
        PageTextRequest request,
        CancellationToken cancellationToken)
    {
        EnsureGeneration(generation);
        Guid session;
        lock (_stateSync) session = _sessionId;
        var response = await SendOperationAsync(
            WorkerOperation.GetPageText,
            TransportIdentity.ForPage(session, request.DocumentId, request.PageId, request.ContentRevision),
            new PageTextCommand(request),
            WorkerProtocolJsonContext.Default.PageTextCommand,
            WorkerProtocolJsonContext.Default.PageTextResponse,
            _options.DefaultOperationTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.Result;
    }

    internal async ValueTask<IReadOnlyList<SearchResult>> SearchPageAsync(
        long generation,
        PageSearchRequest request,
        CancellationToken cancellationToken)
    {
        EnsureGeneration(generation);
        Guid session;
        lock (_stateSync) session = _sessionId;
        var response = await SendOperationAsync(
            WorkerOperation.SearchPage,
            TransportIdentity.ForSearch(
                session,
                request.Page.DocumentId,
                request.Page.PageId,
                request.Page.ContentRevision,
                request.Generation),
            new SearchPageCommand(request),
            WorkerProtocolJsonContext.Default.SearchPageCommand,
            WorkerProtocolJsonContext.Default.SearchPageResponse,
            _options.DefaultOperationTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.Results;
    }

    internal async ValueTask<OutlineResult> GetOutlineAsync(
        DocumentSnapshot snapshot,
        long generation,
        CancellationToken cancellationToken)
    {
        EnsureGeneration(generation);
        var response = await SendOperationAsync(
            WorkerOperation.GetOutline,
            CurrentDocumentIdentity(snapshot),
            new DocumentCommand(snapshot.Id),
            WorkerProtocolJsonContext.Default.DocumentCommand,
            WorkerProtocolJsonContext.Default.OutlineResponse,
            _options.DefaultOperationTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.Outline;
    }

    internal async ValueTask<PdfPermissions> GetPermissionsAsync(
        DocumentSnapshot snapshot,
        long generation,
        CancellationToken cancellationToken)
    {
        EnsureGeneration(generation);
        var response = await SendOperationAsync(
            WorkerOperation.GetPermissions,
            CurrentDocumentIdentity(snapshot),
            new DocumentCommand(snapshot.Id),
            WorkerProtocolJsonContext.Default.DocumentCommand,
            WorkerProtocolJsonContext.Default.PermissionsResponse,
            _options.DefaultOperationTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.Permissions;
    }

    internal async ValueTask<PageLinks> GetPageLinksAsync(
        DocumentSnapshot snapshot,
        PageMetadata page,
        long generation,
        CancellationToken cancellationToken)
    {
        EnsureGeneration(generation);
        Guid session;
        lock (_stateSync) session = _sessionId;
        var response = await SendOperationAsync(
            WorkerOperation.GetPageLinks,
            TransportIdentity.ForPage(session, snapshot.Id, page.Id, page.ContentRevision),
            new PageMetadataCommand(snapshot.Id, page.PageIndex),
            WorkerProtocolJsonContext.Default.PageMetadataCommand,
            WorkerProtocolJsonContext.Default.PageLinksResponse,
            _options.DefaultOperationTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.Links;
    }

    internal async ValueTask<FormWidgetsResult> GetFormWidgetsAsync(
        DocumentSnapshot snapshot,
        PageMetadata page,
        long generation,
        CancellationToken cancellationToken)
    {
        EnsureGeneration(generation);
        Guid session;
        lock (_stateSync) session = _sessionId;
        var response = await SendOperationAsync(
            WorkerOperation.GetFormWidgets,
            TransportIdentity.ForPage(session, snapshot.Id, page.Id, page.ContentRevision),
            new PageMetadataCommand(snapshot.Id, page.PageIndex),
            WorkerProtocolJsonContext.Default.PageMetadataCommand,
            WorkerProtocolJsonContext.Default.FormWidgetsResponse,
            _options.DefaultOperationTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.Forms;
    }

    internal async ValueTask<DocumentSnapshot> ApplyFormValueAsync(
        DocumentSnapshot snapshot,
        FormValueChange change,
        long generation,
        CancellationToken cancellationToken)
    {
        EnsureGeneration(generation);
        var response = await SendOperationAsync(
            WorkerOperation.ApplyFormValue,
            CurrentDocumentIdentity(snapshot),
            new ApplyFormValueCommand(change),
            WorkerProtocolJsonContext.Default.ApplyFormValueCommand,
            WorkerProtocolJsonContext.Default.DocumentMutationResponse,
            _options.DefaultOperationTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.Snapshot;
    }

    internal async ValueTask<DocumentSnapshot> InvokePushButtonAsync(
        DocumentSnapshot snapshot,
        PushButtonInvocation invocation,
        long generation,
        CancellationToken cancellationToken)
    {
        EnsureGeneration(generation);
        var response = await SendOperationAsync(
            WorkerOperation.InvokePushButton,
            CurrentDocumentIdentity(snapshot),
            new InvokePushButtonCommand(invocation),
            WorkerProtocolJsonContext.Default.InvokePushButtonCommand,
            WorkerProtocolJsonContext.Default.DocumentMutationResponse,
            _options.DefaultOperationTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.Snapshot;
    }

    internal async ValueTask<DocumentSnapshot> RotatePageAsync(
        DocumentSnapshot snapshot,
        RotatePageRequest request,
        long generation,
        CancellationToken cancellationToken)
    {
        EnsureGeneration(generation);
        var response = await SendOperationAsync(
            WorkerOperation.RotatePage,
            CurrentDocumentIdentity(snapshot),
            new RotatePageCommand(request),
            WorkerProtocolJsonContext.Default.RotatePageCommand,
            WorkerProtocolJsonContext.Default.DocumentMutationResponse,
            _options.DefaultOperationTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.Snapshot;
    }

    internal async ValueTask<DocumentSnapshot> DeletePageAsync(
        DocumentSnapshot snapshot,
        DeletePageRequest request,
        long generation,
        CancellationToken cancellationToken)
    {
        EnsureGeneration(generation);
        var response = await SendOperationAsync(
            WorkerOperation.DeletePage,
            CurrentDocumentIdentity(snapshot),
            new DeletePageCommand(request),
            WorkerProtocolJsonContext.Default.DeletePageCommand,
            WorkerProtocolJsonContext.Default.DocumentMutationResponse,
            _options.DefaultOperationTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.Snapshot;
    }

    public async ValueTask MergeOrderedPagesAsync(
        MergeOrderedPagesRequest request,
        Stream temporaryOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (temporaryOutput is not FileStream fileStream || !fileStream.CanWrite)
        {
            throw new ArgumentException(
                "Worker merge persistence requires a writable broker-owned FileStream.",
                nameof(temporaryOutput));
        }

        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        Process process;
        Guid session;
        lock (_stateSync)
        {
            process = _process ?? throw new PdfWorkerUnavailableException("The PDF worker is not running.");
            session = _sessionId;
        }

        var handleId = Guid.NewGuid();
        var remoteHandle = WorkerHandleBroker.DuplicateInto(fileStream.SafeFileHandle, process);
        var descriptor = new BrokeredHandleDescriptor
        {
            HandleId = handleId,
            SessionId = session,
            NativeHandleValue = checked((long)remoteHandle),
            Access = BrokeredHandleAccess.TemporaryWrite,
            TransactionId = Guid.NewGuid(),
            ExpiresAtUtc = DateTimeOffset.UtcNow + _options.DefaultOperationTimeout
        };
        var response = await SendOperationAsync(
            WorkerOperation.MergeOrderedPages,
            TransportIdentity.ForSession(session),
            new MergeOrderedPagesCommand(request, descriptor),
            WorkerProtocolJsonContext.Default.MergeOrderedPagesCommand,
            WorkerProtocolJsonContext.Default.AcknowledgementResponse,
            _options.DefaultOperationTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!response.Accepted)
        {
            throw new IOException("The PDF worker rejected the ordered merge transaction.");
        }
    }

    internal async ValueTask<DocumentSnapshot> StageAnnotationsAsync(
        DocumentSnapshot snapshot,
        long generation,
        PdfAnnotationSaveRequest request,
        Stream temporaryOutput,
        CancellationToken cancellationToken)
    {
        EnsureGeneration(generation);
        ArgumentNullException.ThrowIfNull(request);
        if (request.DocumentId != snapshot.Id
            || request.ExpectedContentRevision != snapshot.ContentRevision
            || request.ExpectedStructureRevision != snapshot.StructureRevision)
        {
            throw new ArgumentException("The annotation request does not match the active document snapshot.", nameof(request));
        }
        if (temporaryOutput is not FileStream fileStream || !fileStream.CanWrite)
            throw new ArgumentException("Annotation persistence requires a writable broker-owned FileStream.", nameof(temporaryOutput));

        Process process;
        Guid session;
        lock (_stateSync)
        {
            process = _process ?? throw new PdfWorkerUnavailableException("The PDF worker is not running.");
            session = _sessionId;
        }

        var descriptor = CreateTemporaryWriteDescriptor(fileStream, process, session);
        var response = await SendOperationAsync(
            WorkerOperation.StageAnnotations,
            CurrentDocumentIdentity(snapshot),
            new StageAnnotationsCommand(request, descriptor),
            WorkerProtocolJsonContext.Default.StageAnnotationsCommand,
            WorkerProtocolJsonContext.Default.DocumentMutationResponse,
            _options.DefaultOperationTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.Snapshot;
    }

    internal async ValueTask<DocumentSnapshot> FinalizeAnnotationTransactionAsync(
        DocumentSnapshot snapshot,
        long generation,
        Guid transactionId,
        bool committed,
        CancellationToken cancellationToken)
    {
        EnsureGeneration(generation);
        if (transactionId == Guid.Empty)
            throw new ArgumentException("The annotation transaction id must not be empty.", nameof(transactionId));
        var response = await SendOperationAsync(
            WorkerOperation.FinalizeAnnotationTransaction,
            CurrentDocumentIdentity(snapshot),
            new FinalizeAnnotationTransactionCommand(snapshot.Id, transactionId, committed),
            WorkerProtocolJsonContext.Default.FinalizeAnnotationTransactionCommand,
            WorkerProtocolJsonContext.Default.DocumentMutationResponse,
            _options.DefaultOperationTimeout,
            cancellationToken).ConfigureAwait(false);
        return response.Snapshot;
    }

    internal async ValueTask SaveFlattenedCopyAsync(
        DocumentSnapshot snapshot,
        long generation,
        PdfAnnotationSaveRequest request,
        Stream temporaryOutput,
        CancellationToken cancellationToken)
    {
        EnsureGeneration(generation);
        ArgumentNullException.ThrowIfNull(request);
        if (request.DocumentId != snapshot.Id
            || request.ExpectedContentRevision != snapshot.ContentRevision
            || request.ExpectedStructureRevision != snapshot.StructureRevision)
        {
            throw new ArgumentException("The flatten request does not match the active document snapshot.", nameof(request));
        }
        if (temporaryOutput is not FileStream fileStream || !fileStream.CanWrite)
            throw new ArgumentException("Flattened persistence requires a writable broker-owned FileStream.", nameof(temporaryOutput));

        Process process;
        Guid session;
        lock (_stateSync)
        {
            process = _process ?? throw new PdfWorkerUnavailableException("The PDF worker is not running.");
            session = _sessionId;
        }

        var descriptor = CreateTemporaryWriteDescriptor(fileStream, process, session);
        var response = await SendOperationAsync(
            WorkerOperation.SaveFlattenedCopy,
            CurrentDocumentIdentity(snapshot),
            new SaveFlattenedCopyCommand(request, descriptor),
            WorkerProtocolJsonContext.Default.SaveFlattenedCopyCommand,
            WorkerProtocolJsonContext.Default.AcknowledgementResponse,
            _options.DefaultOperationTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!response.Accepted)
            throw new IOException("The PDF worker rejected the flattened copy transaction.");
    }

    private BrokeredHandleDescriptor CreateTemporaryWriteDescriptor(
        FileStream fileStream,
        Process process,
        Guid session)
    {
        var remoteHandle = WorkerHandleBroker.DuplicateInto(fileStream.SafeFileHandle, process);
        return new BrokeredHandleDescriptor
        {
            HandleId = Guid.NewGuid(),
            SessionId = session,
            NativeHandleValue = checked((long)remoteHandle),
            Access = BrokeredHandleAccess.TemporaryWrite,
            TransactionId = Guid.NewGuid(),
            ExpiresAtUtc = DateTimeOffset.UtcNow + _options.DefaultOperationTimeout
        };
    }

    internal async ValueTask SaveAsync(
        DocumentSnapshot snapshot,
        long generation,
        Stream temporaryOutput,
        ContentRevision capturedRevision,
        CancellationToken cancellationToken)
    {
        EnsureGeneration(generation);
        if (temporaryOutput is not FileStream fileStream || !fileStream.CanWrite)
        {
            throw new ArgumentException("Worker persistence requires a writable broker-owned FileStream.", nameof(temporaryOutput));
        }

        Process process;
        Guid session;
        lock (_stateSync)
        {
            process = _process ?? throw new PdfWorkerUnavailableException("The PDF worker is not running.");
            session = _sessionId;
        }

        var handleId = Guid.NewGuid();
        var transactionId = Guid.NewGuid();
        var remoteHandle = WorkerHandleBroker.DuplicateInto(fileStream.SafeFileHandle, process);
        var descriptor = new BrokeredHandleDescriptor
        {
            HandleId = handleId,
            SessionId = session,
            NativeHandleValue = checked((long)remoteHandle),
            Access = BrokeredHandleAccess.TemporaryWrite,
            TransactionId = transactionId,
            ExpiresAtUtc = DateTimeOffset.UtcNow + _options.DefaultOperationTimeout
        };
        var response = await SendOperationAsync(
            WorkerOperation.SaveDocument,
            CurrentDocumentIdentity(snapshot),
            new SaveDocumentCommand(snapshot.Id, capturedRevision, descriptor),
            WorkerProtocolJsonContext.Default.SaveDocumentCommand,
            WorkerProtocolJsonContext.Default.AcknowledgementResponse,
            _options.DefaultOperationTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!response.Accepted)
        {
            throw new IOException("The PDF worker rejected the save transaction.");
        }
    }

    internal async ValueTask CloseDocumentAsync(
        DocumentSnapshot snapshot,
        long generation,
        CancellationToken cancellationToken)
    {
        EnsureGeneration(generation);
        _ = await SendOperationAsync(
            WorkerOperation.CloseDocument,
            CurrentDocumentIdentity(snapshot),
            new DocumentCommand(snapshot.Id),
            WorkerProtocolJsonContext.Default.DocumentCommand,
            WorkerProtocolJsonContext.Default.AcknowledgementResponse,
            _options.DefaultOperationTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask ReleaseSessionAsync()
    {
        try
        {
            await _sessionLifecycleGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await ReleaseSessionUnderLifecycleGateAsync().ConfigureAwait(false);
        }
        finally
        {
            _sessionLifecycleGate.Release();
        }
    }

    private async ValueTask ReleaseSessionUnderLifecycleGateAsync()
    {
        int remaining = Interlocked.Decrement(ref _activeSessionCount);
        if (remaining < 0)
            throw new InvalidOperationException("The PDF worker session count became negative.");
        if (remaining != 0 || Volatile.Read(ref _disposed) != 0)
            return;
        long generation;
        Guid session;
        lock (_stateSync)
        {
            generation = _workerGeneration;
            session = _sessionId;
        }
        if (session == Guid.Empty || !IsConnectionHealthy())
            return;
        try
        {
            _ = await SendOperationAsync(
                WorkerOperation.Shutdown,
                TransportIdentity.ForSession(session),
                new EmptyPayload(),
                WorkerProtocolJsonContext.Default.EmptyPayload,
                WorkerProtocolJsonContext.Default.AcknowledgementResponse,
                TimeSpan.FromSeconds(1),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PdfWorkerUnavailableException
            or PdfWorkerRemoteException
            or TransportProtocolException
            or TimeoutException
            or IOException
            or ObjectDisposedException)
        {
            // The Job Object termination below remains the authoritative idle cleanup.
        }
        finally
        {
            FailConnection(
                new PdfWorkerUnavailableException("The idle PDF worker was recycled after its last document closed."),
                expectedGeneration: generation,
                terminate: true,
                reportExitedAsCrash: false);
        }
    }

    internal async ValueTask ReleaseLeaseAsync(Guid leaseId, long generation)
    {
        if (leaseId == Guid.Empty)
        {
            return;
        }

        _activeSharedLeases.TryRemove(leaseId, out _);

        Guid session;
        lock (_stateSync)
        {
            if (generation != _workerGeneration || _pipe is null)
            {
                return;
            }
            session = _sessionId;
        }

        try
        {
            _ = await SendControlAsync(
                TransportMessageKind.LeaseRelease,
                TransportIdentity.ForSession(session),
                new LeaseReleaseMessage(leaseId),
                TransportJsonContext.Default.LeaseReleaseMessage,
                _options.HeartbeatTimeout,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Worker exit releases the mapping; lease disposal remains exactly once locally.
        }
    }

    private async ValueTask<IPixelBufferLease> AcquirePixelLeaseAsync(
        SharedMemoryLeaseMetadata metadata,
        long generation,
        CancellationToken cancellationToken)
    {
        metadata.Validate();
        Guid session;
        bool workerUnavailable;
        lock (_stateSync)
        {
            session = _sessionId;
            workerUnavailable = generation != _workerGeneration
                || _process is null
                || _process.HasExited
                || _pipe is null;
        }

        // A render response can win the race with the process-exit callback. In that case the
        // response has already left the pending table while FailConnection clears the active
        // session, so treating the old lease as a live-session protocol violation leaks the wrong
        // exception and misses crash attribution. Preserve protocol errors for an actually live
        // worker, but translate this generation transition to the documented recovery contract.
        if (workerUnavailable)
        {
            RecordCrashOnce(generation, metadata.Key!.DocumentId);
            throw new PdfWorkerUnavailableException(
                "The PDF worker ended before its shared-memory render lease could be acquired.");
        }

        if (metadata.SessionId != session)
        {
            throw new TransportProtocolException("The shared-memory lease belongs to another worker session.");
        }

        MemoryMappedFile? mapping = null;
        try
        {
            var sharedMemoryName = ActiveSandboxMode is WorkerSandboxMode.AppContainer
                or WorkerSandboxMode.LessPrivilegedAppContainer
                ? WorkerAppContainerProcess.QualifyAppContainerNamedObject(metadata.SharedMemoryId)
                : metadata.SharedMemoryId;
            try
            {
                mapping = MemoryMappedFile.OpenExisting(sharedMemoryName, MemoryMappedFileRights.Read);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var unavailable = new PdfWorkerUnavailableException(
                    "The PDF worker ended before its shared-memory render lease could be acquired.",
                    exception);
                FailConnection(unavailable, generation, terminate: true);
                RecordCrashOnce(generation, metadata.Key!.DocumentId);
                throw unavailable;
            }
            var acknowledgement = await SendControlAsync(
                TransportMessageKind.LeaseAck,
                TransportIdentity.ForSession(session),
                new LeaseAckMessage(metadata.LeaseId),
                TransportJsonContext.Default.LeaseAckMessage,
                _options.HeartbeatTimeout,
                cancellationToken).ConfigureAwait(false);
            var result = Deserialize(acknowledgement.Payload, WorkerProtocolJsonContext.Default.AcknowledgementResponse);
            if (!result.Accepted)
            {
                throw new TransportProtocolException("The worker rejected the shared-memory lease acknowledgement.");
            }

            if (!_activeSharedLeases.TryAdd(metadata.LeaseId, metadata.ByteLength))
            {
                throw new TransportProtocolException("The worker reused an active shared-memory lease identifier.");
            }

            return new WorkerPixelBufferLease(this, mapping, metadata, generation);
        }
        catch
        {
            mapping?.Dispose();
            await ReleaseLeaseAsync(metadata.LeaseId, generation).ConfigureAwait(false);
            throw;
        }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (IsConnectionHealthy())
        {
            return;
        }

        await _startupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnectionHealthy())
            {
                return;
            }

            if (!File.Exists(_options.WorkerExecutablePath))
            {
                throw new FileNotFoundException("The PDF worker executable is missing.", _options.WorkerExecutablePath);
            }

            FailConnection(new PdfWorkerUnavailableException("Replacing an unavailable PDF worker."), expectedGeneration: null, terminate: true);
            _job ??= new WorkerJob(_options.JobMemoryLimitBytes, _options.CpuHardCapPercent);

            var sessionId = Guid.NewGuid();
            // The full-trust broker owns the pipe and grants only the current user and the worker
            // profile SID. The random name and authenticated envelope remain mandatory because
            // namespace discovery is not authentication.
            var pipeName = $"ElliePdf-{sessionId:N}-{Guid.NewGuid():N}";
            var brokerPipe = WorkerAppContainerProcess.CreateBrokerPipeServer(pipeName);
            var secret = RandomNumberGenerator.GetBytes(LaunchSecret.ByteLength);
            var arguments = new[]
            {
                "--serve",
                "--pipe",
                pipeName,
                "--session",
                sessionId.ToString("D", System.Globalization.CultureInfo.InvariantCulture)
            };
            WorkerProcessLaunch launch;
            try
            {
                launch = WorkerRestrictedProcess.Start(
                    _options.WorkerExecutablePath,
                    arguments,
                    Path.GetDirectoryName(_options.WorkerExecutablePath)!,
                    _options.RequireAppContainerSandbox,
                    _options.UseLessPrivilegedAppContainer,
                    _options.RequireRestrictedTokenSandbox);
            }
            catch
            {
                brokerPipe.Dispose();
                CryptographicOperations.ZeroMemory(secret);
                throw;
            }

            using (launch)
            {
            var process = launch.Process;
            PipeStream? pipe = null;
            CancellationTokenSource? connectionCancellation = null;
            try
            {
                _job.Assign(process);
                launch.ResumeAfterContainment();
                await launch.StandardInput.WriteAsync(secret, cancellationToken).ConfigureAwait(false);
                await launch.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                launch.StandardInput.Close();
                await brokerPipe.WaitForConnectionAsync(cancellationToken)
                    .WaitAsync(_options.StartupTimeout, cancellationToken)
                    .ConfigureAwait(false);

                pipe = brokerPipe;
                connectionCancellation = new CancellationTokenSource();

                long generation;
                lock (_stateSync)
                {
                    _process = process;
                    _pipe = pipe;
                    _connectionCancellation = connectionCancellation;
                    _secret = secret;
                    _sessionId = sessionId;
                    ActiveSandboxMode = launch.SandboxMode;
                    generation = ++_workerGeneration;
                }

                var workerOperationId = TelemetryOperation.NextId();
                ElliePdfEventSource.Log.WorkerStarted(workerOperationId);
                if (generation > 1)
                {
                    ElliePdfEventSource.Log.WorkerRestarted(workerOperationId, checked((int)Math.Min(generation - 1, int.MaxValue)));
                }

                var readLoopPipe = pipe;
                var readLoopSecret = secret;
                var readLoopCancellation = connectionCancellation;

                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => FailConnection(
                    new PdfWorkerUnavailableException("The PDF worker exited unexpectedly."),
                    generation,
                    terminate: false);
                _readLoop = ReadLoopAsync(
                    readLoopPipe,
                    sessionId,
                    readLoopSecret,
                    generation,
                    readLoopCancellation.Token);
                _heartbeatLoop = HeartbeatLoopAsync(
                    sessionId,
                    generation,
                    readLoopCancellation.Token);
                pipe = null;
                connectionCancellation = null;
                secret = null!;
            }
            catch (Exception exception)
            {
                connectionCancellation?.Cancel();
                connectionCancellation?.Dispose();
                pipe?.Dispose();
                brokerPipe.Dispose();
                var startupException = process.HasExited
                    ? new PdfWorkerUnavailableException(
                        $"The sandboxed PDF worker exited during authenticated startup (exit code {process.ExitCode}).",
                        exception)
                    : exception;
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                process.Dispose();
                CryptographicOperations.ZeroMemory(secret);
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(startupException).Throw();
                throw new UnreachableException();
            }
            }
        }
        finally
        {
            _startupGate.Release();
        }
    }

    private bool IsConnectionHealthy()
    {
        lock (_stateSync)
        {
            return Volatile.Read(ref _disposed) == 0
                && _process is { HasExited: false }
                && _pipe is { IsConnected: true }
                && _connectionCancellation is { IsCancellationRequested: false };
        }
    }

    private async Task ReadLoopAsync(
        Stream stream,
        Guid sessionId,
        byte[] secret,
        long generation,
        CancellationToken cancellationToken)
    {
        var authenticator = new TransportAuthenticator(sessionId, new LaunchSecret(secret));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await new LengthPrefixedFrameCodec().ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                if (envelope is null)
                {
                    throw new EndOfStreamException("The PDF worker closed its transport.");
                }

                authenticator.Validate(envelope);
                if (_pending.TryRemove(envelope.CorrelationId, out var pending))
                {
                    if (envelope.Kind == TransportMessageKind.Error)
                    {
                        var error = Deserialize(envelope.Payload, TransportJsonContext.Default.TransportError).Validate();
                        pending.Completion.TrySetException(new PdfWorkerRemoteException(error.Code, error.Message, error.IsTransient));
                    }
                    else
                    {
                        pending.Completion.TrySetResult(envelope);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            try
            {
                FailConnection(
                    new PdfWorkerUnavailableException("The PDF worker transport failed.", exception),
                    generation,
                    terminate: true);
            }
            catch
            {
                // The loop must complete successfully after reporting a terminal transport
                // failure so its retained task cannot become unobserved during teardown.
            }
        }
    }

    private async Task HeartbeatLoopAsync(Guid sessionId, long generation, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                EnsureGeneration(generation);
                var response = await SendControlAsync(
                    TransportMessageKind.Heartbeat,
                    TransportIdentity.ForSession(sessionId),
                    new HeartbeatMessage(DateTimeOffset.UtcNow, Interlocked.Increment(ref _heartbeatSequence)),
                    TransportJsonContext.Default.HeartbeatMessage,
                    _options.HeartbeatTimeout,
                    cancellationToken).ConfigureAwait(false);
                if (response.Kind != TransportMessageKind.Heartbeat)
                {
                    throw new TransportProtocolException("The worker returned an invalid heartbeat response.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            try
            {
                FailConnection(
                    new PdfWorkerUnavailableException("The PDF worker heartbeat failed.", exception),
                    generation,
                    terminate: true);
            }
            catch
            {
                // See ReadLoopAsync: terminal loop errors are reported through the connection,
                // and the loop itself completes successfully for deterministic observation.
            }
        }
    }

    private static async Task AwaitConnectionLoopsAsync(Task? readLoop, Task? heartbeatLoop)
    {
        var loops = new[] { readLoop, heartbeatLoop }
            .Where(static loop => loop is not null)
            .Cast<Task>()
            .ToArray();
        if (loops.Length == 0)
        {
            return;
        }

        await Task.WhenAll(loops).ConfigureAwait(false);
    }

    private async ValueTask<TResponse> SendOperationAsync<TCommand, TResponse>(
        WorkerOperation operation,
        TransportIdentity identity,
        TCommand command,
        JsonTypeInfo<TCommand> commandType,
        JsonTypeInfo<TResponse> responseType,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool allowDisposed = false)
    {
        if (!allowDisposed)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        }

        var arguments = JsonSerializer.SerializeToElement(command, commandType);
        var payload = new WorkerRequestPayload(operation, arguments);
        var response = await SendEnvelopeAsync(
            TransportMessageKind.Request,
            identity,
            payload,
            WorkerProtocolJsonContext.Default.WorkerRequestPayload,
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (response.Kind != TransportMessageKind.Response)
        {
            throw new TransportProtocolException("The worker returned an invalid operation response.");
        }

        var wrapper = Deserialize(response.Payload, WorkerProtocolJsonContext.Default.WorkerResponsePayload);
        wrapper.Validate();
        if (wrapper.Operation != operation)
        {
            throw new TransportProtocolException("The worker response operation does not match its request.");
        }

        return Deserialize(wrapper.Result, responseType);
    }

    private ValueTask<TransportEnvelope> SendControlAsync<T>(
        TransportMessageKind kind,
        TransportIdentity identity,
        T payload,
        JsonTypeInfo<T> payloadType,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        SendEnvelopeAsync(kind, identity, payload, payloadType, timeout, cancellationToken, sendCancellation: false);

    private async ValueTask<TransportEnvelope> SendEnvelopeAsync<T>(
        TransportMessageKind kind,
        TransportIdentity identity,
        T payload,
        JsonTypeInfo<T> payloadType,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool sendCancellation = true)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new TimeoutException("The operation deadline has expired.");
        }

        Stream stream;
        byte[] secret;
        long generation;
        lock (_stateSync)
        {
            stream = _pipe ?? throw new PdfWorkerUnavailableException("The PDF worker is not connected.");
            secret = _secret ?? throw new PdfWorkerUnavailableException("The PDF worker authentication state is missing.");
            generation = _workerGeneration;
        }

        var correlationId = Guid.NewGuid();
        var deadline = DateTimeOffset.UtcNow + timeout;
        var envelope = TransportEnvelope.Create(kind, secret, identity, payload, payloadType, correlationId, deadline);
        var pending = new PendingRequest(identity);
        if (!_pending.TryAdd(correlationId, pending))
        {
            throw new InvalidOperationException("Unable to allocate a unique worker request identity.");
        }

        try
        {
            await WriteEnvelopeAsync(stream, envelope, cancellationToken).ConfigureAwait(false);
            return await pending.Completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _pending.TryRemove(correlationId, out _);
            if (sendCancellation)
            {
                _ = SendCancellationBestEffortAsync(identity, correlationId);
            }
            throw;
        }
        catch (TimeoutException exception)
        {
            ElliePdfEventSource.Log.WorkerBudgetExceeded(TelemetryOperation.NextId(), 1);
            if (sendCancellation)
            {
                _ = SendCancellationBestEffortAsync(identity, correlationId);
            }
            FailConnection(new PdfWorkerUnavailableException("The PDF worker exceeded an operation deadline.", exception), expectedGeneration: null, terminate: true);
            _pending.TryRemove(correlationId, out _);
            RecordCrashOnce(generation, identity);
            throw new PdfWorkerUnavailableException("The PDF worker exceeded an operation deadline.", exception);
        }
        catch (Exception exception) when (
            exception is ObjectDisposedException
            || exception is IOException and not PdfWorkerRemoteException)
        {
            var unavailable = new PdfWorkerUnavailableException("The PDF worker transport failed.", exception);
            FailConnection(unavailable, expectedGeneration: null, terminate: true);
            _pending.TryRemove(correlationId, out _);
            // A process-exit callback can detach the connection after the state snapshot above
            // but before this request reaches _pending. In that narrow window FailConnection
            // has no request to attribute, so preserve the captured worker generation here.
            // RecordCrashOnce deduplicates the normal path where FailConnection did see it.
            RecordCrashOnce(generation, identity);
            throw unavailable;
        }
        catch
        {
            _pending.TryRemove(correlationId, out _);
            throw;
        }
    }

    private async Task SendCancellationBestEffortAsync(TransportIdentity identity, Guid targetCorrelationId)
    {
        try
        {
            Stream stream;
            byte[] secret;
            lock (_stateSync)
            {
                if (_pipe is null || _secret is null)
                {
                    return;
                }
                stream = _pipe;
                secret = _secret;
            }

            var message = new CancelMessage(targetCorrelationId, "caller_cancelled");
            var envelope = TransportEnvelope.Create(
                TransportMessageKind.Cancel,
                secret,
                identity,
                message,
                TransportJsonContext.Default.CancelMessage);
            await WriteEnvelopeAsync(stream, envelope, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async ValueTask WriteEnvelopeAsync(Stream stream, TransportEnvelope envelope, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await new LengthPrefixedFrameCodec().WriteAsync(stream, envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void FailConnection(
        Exception exception,
        long? expectedGeneration,
        bool terminate,
        bool reportExitedAsCrash = true)
    {
        Process? process;
        PipeStream? pipe;
        CancellationTokenSource? connectionCancellation;
        byte[]? secret;
        long failedGeneration;
        lock (_stateSync)
        {
            if (expectedGeneration is not null && expectedGeneration.Value != _workerGeneration)
            {
                return;
            }

            if (_process is null && _pipe is null)
            {
                return;
            }

            process = _process;
            pipe = _pipe;
            connectionCancellation = _connectionCancellation;
            secret = _secret;
            failedGeneration = _workerGeneration;
            _process = null;
            _pipe = null;
            _connectionCancellation = null;
            _secret = null;
            _sessionId = Guid.Empty;
            ActiveSandboxMode = null;
        }

        connectionCancellation?.Cancel();
        pipe?.Dispose();
        if (process is not null)
        {
            if (process.HasExited && reportExitedAsCrash)
            {
                ElliePdfEventSource.Log.WorkerCrashed(
                    TelemetryOperation.NextId(),
                    process.ExitCode);
            }
            try
            {
                if (terminate && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            process.Dispose();
        }
        connectionCancellation?.Dispose();
        if (secret is not null)
        {
            CryptographicOperations.ZeroMemory(secret);
        }

        var crashedDocuments = new HashSet<DocumentId>();
        foreach (var pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out var pending))
            {
                if (pending.Identity.DocumentId is Guid documentId)
                {
                    crashedDocuments.Add(new DocumentId(documentId));
                }
                pending.Completion.TrySetException(exception);
            }
        }

        foreach (var documentId in crashedDocuments)
        {
            RecordCrashOnce(failedGeneration, documentId);
        }
    }

    private void RecordCrashOnce(long generation, TransportIdentity identity)
    {
        if (identity.DocumentId is Guid documentId)
        {
            RecordCrashOnce(generation, new DocumentId(documentId));
        }
    }

    private void RecordCrashOnce(long generation, DocumentId documentId)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_crashSync)
        {
            if (!_attributedCrashes.Add((generation, documentId)))
            {
                return;
            }

            // Keep only a small rolling set; generations are monotonically increasing and this
            // state exists solely to deduplicate exit/read/write races for one worker instance.
            _attributedCrashes.RemoveWhere(item => item.Generation < generation - 8);
            if (!_documentCrashes.TryGetValue(documentId, out var crashes))
            {
                crashes = new Queue<DateTimeOffset>();
                _documentCrashes.Add(documentId, crashes);
            }

            while (crashes.TryPeek(out var oldest) && now - oldest > CrashWindow)
            {
                crashes.Dequeue();
            }

            crashes.Enqueue(now);
            if (crashes.Count >= CrashQuarantineThreshold)
            {
                _quarantinedDocuments.Add(documentId);
            }
        }
    }

    private TransportIdentity CurrentDocumentIdentity(DocumentSnapshot snapshot)
    {
        Guid session;
        lock (_stateSync) session = _sessionId;
        return TransportIdentity.ForDocument(session, snapshot.Id, snapshot.ContentRevision) with
        {
            StructureRevision = snapshot.StructureRevision.Value
        };
    }

    private static T Deserialize<T>(JsonElement element, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return element.Deserialize(typeInfo)
                ?? throw new TransportProtocolException("The worker response payload is missing.");
        }
        catch (JsonException exception)
        {
            throw new TransportProtocolException("The worker response payload is malformed.", exception);
        }
    }

    private sealed class PendingRequest(TransportIdentity identity)
    {
        public TransportIdentity Identity { get; } = identity;
        public TaskCompletionSource<TransportEnvelope> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>Numeric-only worker resource snapshot used by benchmark evidence collection.</summary>
public readonly record struct PdfWorkerResourceSnapshot(
    long PrivateBytes,
    long WorkingSetBytes,
    double CpuMilliseconds,
    long SharedMappingBytes,
    int ActiveSharedLeaseCount);

public sealed class WorkerPixelBufferLease : IReadablePixelBufferLease
{
    private readonly PdfWorkerClient _client;
    private readonly MemoryMappedFile _mapping;
    private readonly long _workerGeneration;
    private int _released;

    internal WorkerPixelBufferLease(
        PdfWorkerClient client,
        MemoryMappedFile mapping,
        SharedMemoryLeaseMetadata metadata,
        long workerGeneration)
    {
        _client = client;
        _mapping = mapping;
        _workerGeneration = workerGeneration;
        LeaseId = metadata.LeaseId;
        SharedMemoryId = metadata.SharedMemoryId;
        Offset = metadata.Offset;
        ByteLength = metadata.ByteLength;
        Width = metadata.Width;
        Height = metadata.Height;
        Stride = metadata.Stride;
        Format = metadata.Format;
        Key = metadata.Key!;
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

    public Stream OpenReadStream()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
        return _mapping.CreateViewStream(Offset, ByteLength, MemoryMappedFileAccess.Read);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        _mapping.Dispose();
        await _client.ReleaseLeaseAsync(LeaseId, _workerGeneration).ConfigureAwait(false);
    }
}

internal sealed class PdfWorkerSession : IPdfEngineSession, IPdfPushButtonSession, IPdfWritableEngineSession, IPdfPageMutationSession, IPdfAnnotationPersistenceSession
{
    private readonly PdfWorkerClient _client;
    private readonly long _workerGeneration;
    private DocumentSnapshot _snapshot;
    private readonly ConcurrentDictionary<int, PageMetadata> _pageMetadata = new();
    private int _disposed;

    public PdfWorkerSession(PdfWorkerClient client, DocumentOpenResult openResult, long workerGeneration)
    {
        _client = client;
        _snapshot = openResult.Snapshot;
        Metadata = openResult.Metadata;
        _workerGeneration = workerGeneration;
    }

    public DocumentId DocumentId => Volatile.Read(ref _snapshot).Id;
    public PdfMetadata Metadata { get; }
    public DocumentSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public ValueTask<PdfMetadata> GetMetadataAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _client.GetMetadataAsync(Volatile.Read(ref _snapshot), _workerGeneration, cancellationToken);
    }

    public async ValueTask<PageMetadata> GetPageMetadataAsync(int pageIndex, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_pageMetadata.TryGetValue(pageIndex, out var cached))
        {
            return cached;
        }
        var metadata = await _client.GetPageMetadataAsync(
            Volatile.Read(ref _snapshot),
            _workerGeneration,
            pageIndex,
            cancellationToken).ConfigureAwait(false);
        _pageMetadata[pageIndex] = metadata;
        return metadata;
    }

    public ValueTask<IPixelBufferLease> RenderAsync(RenderRequest request, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _client.RenderAsync(_workerGeneration, request, cancellationToken);
    }

    public ValueTask<PageTextResult> GetPageTextAsync(PageTextRequest request, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _client.GetPageTextAsync(_workerGeneration, request, cancellationToken);
    }

    public ValueTask<IReadOnlyList<SearchResult>> SearchPageAsync(PageSearchRequest request, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _client.SearchPageAsync(_workerGeneration, request, cancellationToken);
    }

    public ValueTask<OutlineResult> GetOutlineAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _client.GetOutlineAsync(Volatile.Read(ref _snapshot), _workerGeneration, cancellationToken);
    }

    public ValueTask<PdfPermissions> GetPermissionsAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _client.GetPermissionsAsync(Volatile.Read(ref _snapshot), _workerGeneration, cancellationToken);
    }

    public async ValueTask<PageLinks> GetPageLinksAsync(int pageIndex, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var page = await GetPageMetadataAsync(pageIndex, cancellationToken).ConfigureAwait(false);
        return await _client.GetPageLinksAsync(
            Volatile.Read(ref _snapshot),
            page,
            _workerGeneration,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<FormWidgetsResult> GetFormWidgetsAsync(int pageIndex, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var page = await GetPageMetadataAsync(pageIndex, cancellationToken).ConfigureAwait(false);
        return await _client.GetFormWidgetsAsync(
            Volatile.Read(ref _snapshot),
            page,
            _workerGeneration,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ApplyFormValueAsync(FormValueChange change, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var updated = await _client.ApplyFormValueAsync(
            Volatile.Read(ref _snapshot),
            change,
            _workerGeneration,
            cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _snapshot, updated);
        _pageMetadata.Clear();
    }

    public async ValueTask InvokePushButtonAsync(PushButtonInvocation invocation, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var updated = await _client.InvokePushButtonAsync(
            Volatile.Read(ref _snapshot),
            invocation,
            _workerGeneration,
            cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _snapshot, updated);
    }

    public async ValueTask<DocumentSnapshot> RotatePageAsync(
        RotatePageRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        var current = Volatile.Read(ref _snapshot);
        if (request.DocumentId != current.Id)
        {
            throw new ArgumentException("The rotation request belongs to another document.", nameof(request));
        }

        var updated = await _client.RotatePageAsync(
            current,
            request,
            _workerGeneration,
            cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _snapshot, updated);
        _pageMetadata.Clear();
        return updated;
    }

    public async ValueTask<DocumentSnapshot> DeletePageAsync(
        DeletePageRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        var current = Volatile.Read(ref _snapshot);
        if (request.DocumentId != current.Id)
        {
            throw new ArgumentException("The deletion request belongs to another document.", nameof(request));
        }

        var updated = await _client.DeletePageAsync(
            current,
            request,
            _workerGeneration,
            cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _snapshot, updated);
        _pageMetadata.Clear();
        return updated;
    }

    public ValueTask SaveAsync(Stream temporaryOutput, ContentRevision capturedRevision, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _client.SaveAsync(
            Volatile.Read(ref _snapshot),
            _workerGeneration,
            temporaryOutput,
            capturedRevision,
            cancellationToken);
    }

    public async ValueTask<DocumentSnapshot> StageAnnotationsAsync(
        PdfAnnotationSaveRequest request,
        Stream temporaryOutput,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var updated = await _client.StageAnnotationsAsync(
            Volatile.Read(ref _snapshot),
            _workerGeneration,
            request,
            temporaryOutput,
            cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _snapshot, updated);
        _pageMetadata.Clear();
        return updated;
    }

    public async ValueTask<DocumentSnapshot> FinalizeAnnotationTransactionAsync(
        Guid transactionId,
        bool committed,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var updated = await _client.FinalizeAnnotationTransactionAsync(
            Volatile.Read(ref _snapshot),
            _workerGeneration,
            transactionId,
            committed,
            cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _snapshot, updated);
        _pageMetadata.Clear();
        return updated;
    }

    public ValueTask SaveFlattenedCopyAsync(
        PdfAnnotationSaveRequest request,
        Stream temporaryOutput,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _client.SaveFlattenedCopyAsync(
            Volatile.Read(ref _snapshot),
            _workerGeneration,
            request,
            temporaryOutput,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _client.CloseDocumentAsync(Volatile.Read(ref _snapshot), _workerGeneration, CancellationToken.None).ConfigureAwait(false);
        }
        catch (PdfWorkerUnavailableException)
        {
        }
        catch (PdfWorkerRemoteException exception) when (exception.Code == "document_not_found")
        {
        }
        finally
        {
            await _client.ReleaseSessionAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
