using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElliePdf.Domain.Documents;
using ElliePdf.Domain.Storage;
using ElliePdf.Infrastructure.Storage;
using ElliePdf.Models;

namespace ElliePdf.Services;

/// <summary>
/// Holds the annotations being edited, per tab and page.
/// </summary>
/// <remarks>
/// Purely in-memory. Annotations are persisted by writing them into the PDF itself, so there is no
/// companion file: a saved document carries everything it needs and can be shared as-is.
/// </remarks>
public interface IAnnotationStore
{
    PageOverlayState GetPageOverlay(Guid tabId, int pageIndex);

    void SetPageOverlay(Guid tabId, int pageIndex, PageOverlayState state);

    /// <summary>Replaces everything held for a tab, used when a document is opened or reloaded.</summary>
    void SetOverlayDocument(Guid tabId, PageOverlayDocument document);

    bool IsTabDirty(Guid tabId);

    void MarkTabClean(Guid tabId);

    void RemoveTab(Guid tabId);

    /// <summary>Drops every overlay for a tab, e.g. once they have been written into the PDF.</summary>
    void ClearOverlays(Guid tabId);

    /// <summary>
    /// Drops a page's overlays and shifts later pages down, keeping the store aligned with a
    /// document whose page has been deleted.
    /// </summary>
    void RemovePage(Guid tabId, int pageIndex);

    PageOverlayDocument? GetOverlayDocument(Guid tabId);
}

public sealed class AnnotationStore : IAnnotationStore
{
    private const int MaximumRecoveryBytes = 16 * 1024 * 1024;
    private const int MaximumPages = 100_000;
    private const int MaximumAnnotations = 4_096;
    private const int MaximumPointsPerStroke = 32_768;
    private const int MaximumTextLength = 16_384;
    private const int MaximumSignatureBase64Length = 1_398_104;
    private const int MaximumSignatureDecodedBytes = 1_048_576;
    private const double MaximumCoordinate = 10_000_000;
    private const int MaximumFormEdits = 20_000;
    private const int MaximumFormChoices = 4_096;

    private readonly IUserSettingsService _settingsService;
    private readonly IAtomicDocumentStore _atomicDocumentStore;
    private readonly IFileVersionStampProvider _fileVersionStampProvider;
    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, PageOverlayDocument> _documents = [];
    private readonly HashSet<Guid> _checkpointPendingTabs = [];
    private readonly Dictionary<Guid, long> _contentRevisions = [];
    private readonly Dictionary<Guid, long> _epochs = [];
    private readonly Dictionary<Guid, string> _sourcePaths = [];
    private readonly Dictionary<Guid, FileVersionStamp> _sourceVersions = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _scheduledCancellations = [];
    private readonly Dictionary<Guid, Dictionary<Guid, Task>> _ownedTasks = [];
    private readonly Dictionary<Guid, HashSet<RecoveryOperation>> _inflightOperations = [];

    public AnnotationStore(
        IUserSettingsService settingsService,
        IAtomicDocumentStore atomicDocumentStore,
        IFileVersionStampProvider fileVersionStampProvider)
    {
        _settingsService = settingsService;
        _atomicDocumentStore = atomicDocumentStore;
        _fileVersionStampProvider = fileVersionStampProvider;
    }

    public event EventHandler<RecoveryCheckpointCompletedEventArgs>? RecoveryCheckpointCompleted;

    public PageOverlayState GetPageOverlay(Guid tabId, int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);

        lock (_sync)
        {
            if (!_documents.TryGetValue(tabId, out var document)
                || !document.Pages.TryGetValue(pageIndex, out var overlay))
            {
                return new PageOverlayState();
            }

            return CloneOverlayState(overlay);
        }
    }

    public void SetPageOverlay(
        Guid tabId,
        int pageIndex,
        PageOverlayState state,
        ContentRevision contentRevision)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(contentRevision.Value);
        ValidateOverlayState(state);
        var capturedState = CloneOverlayState(state);

        lock (_sync)
        {
            _ = GetOrCreateEpochNoLock(tabId);
            if (!_documents.TryGetValue(tabId, out var document))
            {
                document = new PageOverlayDocument();
                _documents[tabId] = document;
            }

            var currentPageCount = document.Pages.TryGetValue(pageIndex, out var currentPage)
                ? AnnotationCount(currentPage)
                : 0;
            var nextTotal = checked(
                document.Pages.Values.Sum(AnnotationCount)
                - currentPageCount
                + AnnotationCount(capturedState));
            if (nextTotal > MaximumAnnotations)
            {
                throw new InvalidDataException("The overlay transaction contains too many annotations.");
            }
            document.Pages[pageIndex] = capturedState;
            _contentRevisions[tabId] = contentRevision.Value;
            _checkpointPendingTabs.Add(tabId);
        }
    }

    public void SetFormRecoveryEdit(
        Guid tabId,
        FormRecoveryEdit edit,
        ContentRevision contentRevision)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentOutOfRangeException.ThrowIfNegative(contentRevision.Value);
        ValidateFormEdit(edit);
        var captured = CloneFormEdit(edit);

        lock (_sync)
        {
            _ = GetOrCreateEpochNoLock(tabId);
            if (!_documents.TryGetValue(tabId, out var document))
            {
                document = new PageOverlayDocument();
                _documents[tabId] = document;
            }

            var existing = document.FormEdits.FindIndex(candidate =>
                candidate.PageIndex == captured.PageIndex
                && string.Equals(candidate.FieldName, captured.FieldName, StringComparison.Ordinal)
                && string.Equals(candidate.WidgetType, captured.WidgetType, StringComparison.Ordinal));
            if (existing >= 0)
            {
                document.FormEdits[existing] = captured;
            }
            else
            {
                if (document.FormEdits.Count >= MaximumFormEdits)
                {
                    throw new InvalidDataException("The form recovery edit count exceeds the configured limit.");
                }
                document.FormEdits.Add(captured);
            }

            _contentRevisions[tabId] = contentRevision.Value;
            _checkpointPendingTabs.Add(tabId);
        }
    }

    public IReadOnlyList<FormRecoveryEdit> GetFormRecoveryEdits(Guid tabId)
    {
        lock (_sync)
        {
            return _documents.TryGetValue(tabId, out var document)
                ? document.FormEdits.Select(CloneFormEdit).ToArray()
                : [];
        }
    }

    public bool HasPendingCheckpoint(Guid tabId)
    {
        lock (_sync)
        {
            return _checkpointPendingTabs.Contains(tabId);
        }
    }

    public PageOverlayDocument? CaptureOverlayDocument(Guid tabId)
    {
        lock (_sync)
        {
            return _documents.TryGetValue(tabId, out var document)
                ? DeserializeDocument(SerializeDocument(document))
                : null;
        }
    }

    public async Task CommitPersistedEditsAsync(
        Guid tabId,
        PageOverlayDocument persistedSnapshot,
        ContentRevision currentRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistedSnapshot);
        ArgumentOutOfRangeException.ThrowIfNegative(currentRevision.Value);
        ValidateDocument(persistedSnapshot);
        cancellationToken.ThrowIfCancellationRequested();
        await StopOwnedWorkAsync(tabId).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_documents.TryGetValue(tabId, out var current))
            {
                foreach (var (pageIndex, persistedPage) in persistedSnapshot.Pages)
                {
                    if (!current.Pages.TryGetValue(pageIndex, out var currentPage))
                    {
                        continue;
                    }

                    currentPage.InkStrokes.RemoveAll(candidate =>
                        persistedPage.InkStrokes.Any(persisted => InkEquals(candidate, persisted)));
                    currentPage.TextItems.RemoveAll(candidate =>
                        persistedPage.TextItems.Any(persisted => TextEquals(candidate, persisted)));
                    currentPage.Signatures.RemoveAll(candidate =>
                        persistedPage.Signatures.Any(persisted => SignatureEquals(candidate, persisted)));
                    if (!HasOverlayContent(currentPage))
                    {
                        current.Pages.Remove(pageIndex);
                    }
                }

                current.FormEdits.RemoveAll(candidate =>
                    persistedSnapshot.FormEdits.Any(persisted => FormEditEquals(candidate, persisted)));
            }

            _contentRevisions[tabId] = currentRevision.Value;
            if (_documents.TryGetValue(tabId, out var remaining) && HasRecoveryContent(remaining))
            {
                _checkpointPendingTabs.Add(tabId);
            }
            else
            {
                _checkpointPendingTabs.Remove(tabId);
            }
        }
    }

    public async Task RemoveTabAsync(
        Guid tabId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await StopOwnedWorkAsync(tabId).ConfigureAwait(false);

        lock (_sync)
        {
            _documents.Remove(tabId);
            _checkpointPendingTabs.Remove(tabId);
            _contentRevisions.Remove(tabId);
            _sourcePaths.Remove(tabId);
            _sourceVersions.Remove(tabId);
            _scheduledCancellations.Remove(tabId);
            _ownedTasks.Remove(tabId);
            _inflightOperations.Remove(tabId);
        }
    }

    public async Task StopAndDeleteRecoveryAsync(
        Guid tabId,
        string pdfPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        cancellationToken.ThrowIfCancellationRequested();
        await StopOwnedWorkAsync(tabId).ConfigureAwait(false);

        var recoveryPath = GetRecoveryPath(pdfPath);
        if (File.Exists(recoveryPath))
        {
            File.Delete(recoveryPath);
        }

        lock (_sync)
        {
            _checkpointPendingTabs.Remove(tabId);
        }
    }

    public async Task ClearAllRecoveryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await FlushPendingSavesAsync(cancellationToken).ConfigureAwait(false);
        var recoveryDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ElliePdf",
            "Recovery");
        if (!Directory.Exists(recoveryDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(recoveryDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(path);
        }

        lock (_sync)
        {
            _checkpointPendingTabs.Clear();
        }
    }

    public async Task<bool> LoadRecoveryAsync(
        Guid tabId,
        string pdfPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        var canonicalPath = Path.GetFullPath(pdfPath);
        var recoveryPath = GetRecoveryPath(canonicalPath);

        FileVersionStamp sourceVersion;
        try
        {
            sourceVersion = await CaptureSourceVersionAsync(canonicalPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            InitializeEmptyTab(tabId, canonicalPath, null);
            return false;
        }

        if (!File.Exists(recoveryPath))
        {
            InitializeEmptyTab(tabId, canonicalPath, sourceVersion);
            return false;
        }

        try
        {
            var fileInfo = new FileInfo(recoveryPath);
            if (fileInfo.Length is <= 0 or > MaximumRecoveryBytes)
            {
                throw new InvalidDataException("The recovery artifact is outside the allowed size bounds.");
            }

            await using var stream = new FileStream(
                recoveryPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var envelope = await JsonSerializer.DeserializeAsync(
                    stream,
                    ElliePdfJsonContext.Default.RecoveryEnvelope,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("The recovery artifact is empty.");

            ValidateEnvelope(envelope, canonicalPath, sourceVersion);
            var capturedDocument = DeserializeDocument(SerializeDocument(envelope.Payload));

            lock (_sync)
            {
                IncrementEpochNoLock(tabId);
                _documents[tabId] = capturedDocument;
                _contentRevisions[tabId] = envelope.ContentRevision;
                _sourcePaths[tabId] = canonicalPath;
                _sourceVersions[tabId] = sourceVersion;
                _checkpointPendingTabs.Remove(tabId);
            }

            return capturedDocument.Pages.Count > 0 || capturedDocument.FormEdits.Count > 0;
        }
        catch (Exception exception) when (exception is JsonException
                                           or InvalidDataException
                                           or NotSupportedException
                                           or OverflowException)
        {
            QuarantineRecovery(recoveryPath, "corrupt");
            InitializeEmptyTab(tabId, canonicalPath, sourceVersion);
            return false;
        }
    }

    public async Task SaveRecoveryCheckpointAsync(
        Guid tabId,
        string pdfPath,
        ContentRevision contentRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentOutOfRangeException.ThrowIfNegative(contentRevision.Value);

        byte[] payload;
        long epoch;
        FileVersionStamp sourceVersion;
        lock (_sync)
        {
            if (!_documents.TryGetValue(tabId, out var document)
                || !_contentRevisions.TryGetValue(tabId, out var currentRevision)
                || currentRevision != contentRevision.Value
                || !_sourceVersions.TryGetValue(tabId, out sourceVersion!))
            {
                return;
            }

            payload = SerializeDocument(document);
            epoch = GetOrCreateEpochNoLock(tabId);
        }

        var payloadHash = Convert.ToHexString(SHA256.HashData(payload));
        var envelope = new RecoveryEnvelope
        {
            DocumentId = tabId,
            ContentRevision = contentRevision.Value,
            SourcePathHash = HashCanonicalPath(sourceVersion.CanonicalPath),
            SourceFileIdentity = sourceVersion.FileIdentity,
            SourceLength = sourceVersion.Length,
            SourceSha256 = sourceVersion.Sha256,
            PayloadSha256 = payloadHash,
            Payload = DeserializeDocument(payload)
        };
        var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            ElliePdfJsonContext.Default.RecoveryEnvelope);
        if (envelopeBytes.Length > MaximumRecoveryBytes)
        {
            throw new InvalidDataException(
                $"The recovery checkpoint exceeds the {MaximumRecoveryBytes}-byte limit.");
        }

        var operation = RegisterInflightOperation(
            tabId,
            epoch,
            contentRevision.Value,
            cancellationToken);
        if (operation is null)
        {
            return;
        }

        using (operation)
        {
            var recoveryPath = GetRecoveryPath(pdfPath);
            try
            {
                await _atomicDocumentStore.CommitAsync(
                        new AtomicSaveRequest(recoveryPath, contentRevision),
                        async (stream, token) =>
                        {
                            EnsureOperationCurrent(tabId, epoch, contentRevision.Value, token);
                            await stream.WriteAsync(envelopeBytes, token).ConfigureAwait(false);
                            EnsureOperationCurrent(tabId, epoch, contentRevision.Value, token);
                        },
                        async (candidatePath, token) =>
                        {
                            EnsureOperationCurrent(tabId, epoch, contentRevision.Value, token);
                            await ValidateRecoveryFileAsync(
                                    candidatePath,
                                    sourceVersion.CanonicalPath,
                                    sourceVersion,
                                    token)
                                .ConfigureAwait(false);
                        },
                        operation.Token)
                    .ConfigureAwait(false);

                var published = false;
                lock (_sync)
                {
                    if (IsOperationCurrentNoLock(tabId, epoch, contentRevision.Value))
                    {
                        _checkpointPendingTabs.Remove(tabId);
                        published = true;
                    }
                }

                if (published)
                {
                    RecoveryCheckpointCompleted?.Invoke(
                        this,
                        new RecoveryCheckpointCompletedEventArgs(tabId, contentRevision, true));
                }
            }
            finally
            {
                CompleteInflightOperation(tabId, operation);
            }
        }
    }

    public void ScheduleRecoveryCheckpoint(
        Guid tabId,
        string pdfPath,
        ContentRevision contentRevision,
        FileVersionStamp sourceVersion)
    {
        if (!_settingsService.Settings.AutoSaveCompanion)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        var operationId = Guid.NewGuid();
        CancellationTokenSource? previous;

        lock (_sync)
        {
            previous = _scheduledCancellations.GetValueOrDefault(tabId);
            _scheduledCancellations[tabId] = cancellation;
            _sourcePaths[tabId] = Path.GetFullPath(pdfPath);
            _sourceVersions[tabId] = sourceVersion;

            if (!_ownedTasks.TryGetValue(tabId, out var tasks))
            {
                tasks = [];
                _ownedTasks[tabId] = tasks;
            }

            tasks[operationId] = RunScheduledCheckpointAsync(
                tabId,
                operationId,
                pdfPath,
                contentRevision,
                cancellation);
        }

        previous?.Cancel();
    }

    public async Task FlushPendingSavesAsync(CancellationToken cancellationToken = default)
    {
        KeyValuePair<Guid, CancellationTokenSource>[] scheduled;
        lock (_sync)
        {
            scheduled = _scheduledCancellations.ToArray();
        }

        foreach (var (_, cancellation) in scheduled)
        {
            cancellation.Cancel();
        }

        Task[] owned;
        lock (_sync)
        {
            owned = _ownedTasks.Values.SelectMany(static tasks => tasks.Values).ToArray();
        }

        await AwaitOwnedTasksAsync(owned).ConfigureAwait(false);

        (Guid TabId, string SourcePath, ContentRevision Revision)[] pending;
        lock (_sync)
        {
            pending = _checkpointPendingTabs
                .Where(tabId => _sourcePaths.ContainsKey(tabId) && _contentRevisions.ContainsKey(tabId))
                .Select(tabId => (
                    tabId,
                    _sourcePaths[tabId],
                    new ContentRevision(_contentRevisions[tabId])))
                .ToArray();
        }

        foreach (var item in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SaveRecoveryCheckpointAsync(
                    item.TabId,
                    item.SourcePath,
                    item.Revision,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RunScheduledCheckpointAsync(
        Guid tabId,
        Guid operationId,
        string pdfPath,
        ContentRevision contentRevision,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(800), cancellation.Token).ConfigureAwait(false);
            await SaveRecoveryCheckpointAsync(
                    tabId,
                    pdfPath,
                    contentRevision,
                    cancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or JsonException
                                           or InvalidDataException)
        {
            lock (_sync)
            {
                _checkpointPendingTabs.Add(tabId);
            }

            RecoveryCheckpointCompleted?.Invoke(
                this,
                new RecoveryCheckpointCompletedEventArgs(tabId, contentRevision, false));
        }
        finally
        {
            lock (_sync)
            {
                if (_scheduledCancellations.TryGetValue(tabId, out var current)
                    && ReferenceEquals(current, cancellation))
                {
                    _scheduledCancellations.Remove(tabId);
                }

                if (_ownedTasks.TryGetValue(tabId, out var tasks))
                {
                    tasks.Remove(operationId);
                    if (tasks.Count == 0)
                    {
                        _ownedTasks.Remove(tabId);
                    }
                }
            }

            cancellation.Dispose();
        }
    }

    private async Task StopOwnedWorkAsync(Guid tabId)
    {
        Task[] work;
        lock (_sync)
        {
            IncrementEpochNoLock(tabId);

            if (_scheduledCancellations.TryGetValue(tabId, out var scheduled))
            {
                scheduled.Cancel();
            }

            if (_inflightOperations.TryGetValue(tabId, out var operations))
            {
                foreach (var operation in operations)
                {
                    operation.Cancel();
                }
            }

            IEnumerable<Task> scheduledTasks = _ownedTasks.TryGetValue(tabId, out var tasks)
                ? tasks.Values
                : Array.Empty<Task>();
            IEnumerable<Task> inflightTasks = _inflightOperations.TryGetValue(tabId, out operations)
                ? operations.Select(static operation => operation.Completion)
                : Array.Empty<Task>();
            work = scheduledTasks.Concat(inflightTasks).Distinct().ToArray();
        }

        await AwaitOwnedTasksAsync(work).ConfigureAwait(false);
    }

    private RecoveryOperation? RegisterInflightOperation(
        Guid tabId,
        long epoch,
        long contentRevision,
        CancellationToken cancellationToken)
    {
        var operation = new RecoveryOperation(cancellationToken);
        lock (_sync)
        {
            if (!IsOperationCurrentNoLock(tabId, epoch, contentRevision))
            {
                operation.Dispose();
                return null;
            }

            if (!_inflightOperations.TryGetValue(tabId, out var operations))
            {
                operations = [];
                _inflightOperations[tabId] = operations;
            }

            operations.Add(operation);
        }

        return operation;
    }

    private void CompleteInflightOperation(Guid tabId, RecoveryOperation operation)
    {
        lock (_sync)
        {
            if (_inflightOperations.TryGetValue(tabId, out var operations))
            {
                operations.Remove(operation);
                if (operations.Count == 0)
                {
                    _inflightOperations.Remove(tabId);
                }
            }
        }

        operation.MarkCompleted();
    }

    private void EnsureOperationCurrent(
        Guid tabId,
        long epoch,
        long contentRevision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!IsOperationCurrentNoLock(tabId, epoch, contentRevision))
            {
                throw new OperationCanceledException("The recovery checkpoint is stale.", cancellationToken);
            }
        }
    }

    private bool IsOperationCurrentNoLock(Guid tabId, long epoch, long contentRevision) =>
        _epochs.TryGetValue(tabId, out var currentEpoch)
        && currentEpoch == epoch
        && _contentRevisions.TryGetValue(tabId, out var currentRevision)
        && currentRevision == contentRevision;

    private void InitializeEmptyTab(
        Guid tabId,
        string canonicalPath,
        FileVersionStamp? sourceVersion)
    {
        lock (_sync)
        {
            IncrementEpochNoLock(tabId);
            _documents[tabId] = new PageOverlayDocument();
            _contentRevisions[tabId] = ContentRevision.Initial.Value;
            _sourcePaths[tabId] = canonicalPath;
            if (sourceVersion is not null)
            {
                _sourceVersions[tabId] = sourceVersion;
            }
            else
            {
                _sourceVersions.Remove(tabId);
            }

            _checkpointPendingTabs.Remove(tabId);
        }
    }

    private long GetOrCreateEpochNoLock(Guid tabId)
    {
        if (_epochs.TryGetValue(tabId, out var epoch))
        {
            return epoch;
        }

        _epochs[tabId] = 1;
        return 1;
    }

    private long IncrementEpochNoLock(Guid tabId)
    {
        var next = checked(GetOrCreateEpochNoLock(tabId) + 1);
        _epochs[tabId] = next;
        return next;
    }

    private static async Task AwaitOwnedTasksAsync(IEnumerable<Task> tasks)
    {
        var captured = tasks.ToArray();
        if (captured.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(captured).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string GetRecoveryPath(string pdfPath)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ElliePdf",
            "Recovery");
        return Path.Combine(directory, $"{HashCanonicalPath(pdfPath)}.json");
    }

    private static string HashCanonicalPath(string pdfPath)
    {
        var canonicalPath = Path.GetFullPath(pdfPath).ToUpperInvariant();
        var identityHash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath));
        return Convert.ToHexString(identityHash);
    }

    private async ValueTask<FileVersionStamp> CaptureSourceVersionAsync(
        string path,
        CancellationToken cancellationToken)
    {
        return await _fileVersionStampProvider.TryCaptureAsync(path, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The PDF source no longer exists.", path);
    }

    private static byte[] SerializeDocument(PageOverlayDocument document)
    {
        ValidateDocument(document);
        return JsonSerializer.SerializeToUtf8Bytes(
            document,
            ElliePdfJsonContext.Default.PageOverlayDocument);
    }

    private static PageOverlayDocument DeserializeDocument(byte[] payload)
    {
        var document = JsonSerializer.Deserialize(
                payload,
                ElliePdfJsonContext.Default.PageOverlayDocument)
            ?? throw new InvalidDataException("The overlay payload is empty.");
        ValidateDocument(document);
        return document;
    }

    private static PageOverlayState CloneOverlayState(PageOverlayState state)
    {
        var wrapper = new PageOverlayDocument
        {
            Pages = new Dictionary<int, PageOverlayState> { [0] = state }
        };
        return DeserializeDocument(SerializeDocument(wrapper)).Pages[0];
    }

    private static void ValidateEnvelope(
        RecoveryEnvelope envelope,
        string sourcePath,
        FileVersionStamp sourceVersion)
    {
        if (!string.Equals(envelope.Magic, RecoveryEnvelope.ExpectedMagic, StringComparison.Ordinal)
            || envelope.SchemaVersion != RecoveryEnvelope.CurrentSchemaVersion
            || envelope.ContentRevision < 0
            || !string.Equals(envelope.SourcePathHash, HashCanonicalPath(sourcePath), StringComparison.Ordinal)
            || envelope.SourceLength != sourceVersion.Length
            || !string.Equals(envelope.SourceSha256, sourceVersion.Sha256, StringComparison.OrdinalIgnoreCase)
            || (envelope.SourceFileIdentity is not null
                && sourceVersion.FileIdentity is not null
                && !string.Equals(envelope.SourceFileIdentity, sourceVersion.FileIdentity, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The recovery artifact does not match this PDF source.");
        }

        var payload = SerializeDocument(envelope.Payload);
        var payloadHash = Convert.ToHexString(SHA256.HashData(payload));
        if (!string.Equals(payloadHash, envelope.PayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The recovery payload checksum is invalid.");
        }
    }

    private static async Task ValidateRecoveryFileAsync(
        string candidatePath,
        string sourcePath,
        FileVersionStamp sourceVersion,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(candidatePath);
        if (fileInfo.Length is <= 0 or > MaximumRecoveryBytes)
        {
            throw new InvalidDataException("The recovery candidate is outside the allowed size bounds.");
        }

        await using var stream = new FileStream(
            candidatePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var envelope = await JsonSerializer.DeserializeAsync(
                stream,
                ElliePdfJsonContext.Default.RecoveryEnvelope,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The recovery candidate is empty.");
        ValidateEnvelope(envelope, sourcePath, sourceVersion);
    }

    private static void ValidateDocument(PageOverlayDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Pages is null
            || document.FormEdits is null
            || document.SchemaVersion != PageOverlayDocument.CurrentSchemaVersion
            || document.Pages.Count > MaximumPages
            || document.FormEdits.Count > MaximumFormEdits)
        {
            throw new InvalidDataException("The overlay document schema or page count is invalid.");
        }

        var annotationCount = 0;
        foreach (var (pageIndex, state) in document.Pages)
        {
            if (pageIndex < 0 || state is null)
            {
                throw new InvalidDataException("An overlay page index is negative.");
            }

            ValidateOverlayState(state);
            annotationCount = checked(annotationCount + AnnotationCount(state));
            if (annotationCount > MaximumAnnotations)
            {
                throw new InvalidDataException("The overlay transaction contains too many annotations.");
            }
        }

        foreach (var edit in document.FormEdits)
        {
            ValidateFormEdit(edit);
        }
    }

    private static void ValidateFormEdit(FormRecoveryEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.PageIndex < 0
            || string.IsNullOrWhiteSpace(edit.FieldName)
            || edit.FieldName.Length > 4_096
            || string.IsNullOrWhiteSpace(edit.WidgetType)
            || edit.WidgetType.Length > 64
            || string.IsNullOrWhiteSpace(edit.ValueKind)
            || edit.ValueKind.Length > 64
            || edit.Text?.Length > 4_096
            || edit.Choices is null
            || edit.Choices.Count > MaximumFormChoices
            || edit.Choices.Any(static choice => choice.Length > 4_096))
        {
            throw new InvalidDataException("A recovered form value is outside the allowed bounds.");
        }
    }

    private static FormRecoveryEdit CloneFormEdit(FormRecoveryEdit edit) => new()
    {
        PageIndex = edit.PageIndex,
        FieldName = edit.FieldName,
        WidgetType = edit.WidgetType,
        ValueKind = edit.ValueKind,
        Text = edit.Text,
        Boolean = edit.Boolean,
        Choices = [.. edit.Choices]
    };

    private static bool HasRecoveryContent(PageOverlayDocument document) =>
        document.FormEdits.Count > 0 || document.Pages.Values.Any(HasOverlayContent);

    private static bool HasOverlayContent(PageOverlayState page) =>
        page.InkStrokes.Count > 0 || page.TextItems.Count > 0 || page.Signatures.Count > 0;

    private static bool InkEquals(InkStrokeOverlay left, InkStrokeOverlay right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal)
        && string.Equals(left.ColorHex, right.ColorHex, StringComparison.Ordinal)
        && left.Thickness.Equals(right.Thickness)
        && left.Points.Count == right.Points.Count
        && left.Points.Zip(right.Points).All(static pair =>
            pair.First.X.Equals(pair.Second.X) && pair.First.Y.Equals(pair.Second.Y));

    private static bool TextEquals(TextOverlay left, TextOverlay right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal)
        && left.X.Equals(right.X)
        && left.Y.Equals(right.Y)
        && string.Equals(left.Text, right.Text, StringComparison.Ordinal)
        && left.FontSize.Equals(right.FontSize)
        && left.Width.Equals(right.Width)
        && left.Height.Equals(right.Height)
        && string.Equals(left.ColorHex, right.ColorHex, StringComparison.Ordinal)
        && left.IsBold == right.IsBold
        && left.IsItalic == right.IsItalic;

    private static bool SignatureEquals(SignatureOverlay left, SignatureOverlay right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal)
        && left.X.Equals(right.X)
        && left.Y.Equals(right.Y)
        && string.Equals(left.ImageBase64, right.ImageBase64, StringComparison.Ordinal)
        && left.Width.Equals(right.Width)
        && left.Height.Equals(right.Height);

    private static bool FormEditEquals(FormRecoveryEdit left, FormRecoveryEdit right) =>
        left.PageIndex == right.PageIndex
        && string.Equals(left.FieldName, right.FieldName, StringComparison.Ordinal)
        && string.Equals(left.WidgetType, right.WidgetType, StringComparison.Ordinal)
        && string.Equals(left.ValueKind, right.ValueKind, StringComparison.Ordinal)
        && string.Equals(left.Text, right.Text, StringComparison.Ordinal)
        && left.Boolean == right.Boolean
        && left.Choices.SequenceEqual(right.Choices, StringComparer.Ordinal);

    private static void ValidateOverlayState(PageOverlayState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.InkStrokes is null
            || state.TextItems is null
            || state.Signatures is null
            || AnnotationCount(state) > MaximumAnnotations)
        {
            throw new InvalidDataException("A recovery page contains too many overlay items.");
        }

        var identifiers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stroke in state.InkStrokes)
        {
            if (stroke is null
                || !IsSafeAnnotationId(stroke.Id)
                || !identifiers.Add(stroke.Id)
                || stroke.Points is null
                || stroke.Points.Count is < 2 or > MaximumPointsPerStroke
                || !IsFinite(stroke.Thickness)
                || stroke.Thickness is <= 0 or > 128
                || !IsColor(stroke.ColorHex))
            {
                throw new InvalidDataException("An ink overlay is outside the allowed bounds.");
            }

            foreach (var point in stroke.Points)
            {
                if (point is null || !IsCoordinate(point.X) || !IsCoordinate(point.Y))
                {
                    throw new InvalidDataException("An ink point is not finite.");
                }
            }
        }

        foreach (var text in state.TextItems)
        {
            if (text is null
                || !IsSafeAnnotationId(text.Id)
                || !identifiers.Add(text.Id)
                || string.IsNullOrWhiteSpace(text.Text)
                || text.Text.Length > MaximumTextLength
                || !IsCoordinate(text.X)
                || !IsCoordinate(text.Y)
                || !IsPositiveDimension(text.Width)
                || !IsPositiveDimension(text.Height)
                || !IsFinite(text.FontSize)
                || text.FontSize is < 4 or > 512
                || !IsColor(text.ColorHex))
            {
                throw new InvalidDataException("A text overlay is outside the allowed bounds.");
            }
        }

        foreach (var signature in state.Signatures)
        {
            if (signature is null
                || !IsSafeAnnotationId(signature.Id)
                || !identifiers.Add(signature.Id)
                || string.IsNullOrEmpty(signature.ImageBase64)
                || signature.ImageBase64.Length > MaximumSignatureBase64Length
                || !IsCanonicalSignature(signature.ImageBase64)
                || !IsCoordinate(signature.X)
                || !IsCoordinate(signature.Y)
                || !IsPositiveDimension(signature.Width)
                || !IsPositiveDimension(signature.Height))
            {
                throw new InvalidDataException("A signature overlay is outside the allowed bounds.");
            }
        }
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static bool IsCoordinate(double value) => IsFinite(value) && Math.Abs(value) <= MaximumCoordinate;

    private static bool IsPositiveDimension(double value) => IsFinite(value) && value > 0 && value <= MaximumCoordinate;

    private static bool IsSafeAnnotationId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    private static bool IsColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var hex = value.Trim().TrimStart('#');
        return (hex.Length is 6 or 8)
            && hex.All(static character => Uri.IsHexDigit(character));
    }

    private static bool IsCanonicalSignature(string? value)
    {
        if (string.IsNullOrEmpty(value)
            || value.Length > MaximumSignatureBase64Length
            || (value.Length & 3) != 0)
        {
            return false;
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return false;
        }
        try
        {
            return decoded.Length is > 0 and <= MaximumSignatureDecodedBytes
                && string.Equals(Convert.ToBase64String(decoded), value, StringComparison.Ordinal);
        }
        finally
        {
            Array.Clear(decoded);
        }
    }

    private static int AnnotationCount(PageOverlayState state) =>
        checked(state.InkStrokes.Count + state.TextItems.Count + state.Signatures.Count);

    private static void QuarantineRecovery(string path, string reason)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var quarantinePath = $"{path}.{reason}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            File.Move(path, quarantinePath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class RecoveryOperation : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecoveryOperation(CancellationToken cancellationToken)
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        public CancellationToken Token => _cancellation.Token;

        public Task Completion => _completion.Task;

        public void Cancel() => _cancellation.Cancel();

        public void MarkCompleted() => _completion.TrySetResult();

        public void Dispose() => _cancellation.Dispose();
    }
}
