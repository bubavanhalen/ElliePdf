using System.Text.Json;
using ElliePdf.Domain.Storage;

namespace ElliePdf.Infrastructure.Storage;

public sealed class AtomicDocumentStore : IAtomicDocumentStore
{
    private readonly IFileVersionStampProvider _versionStampProvider;
    private readonly IAtomicSaveObserver _observer;
    private readonly IAtomicDestinationPolicy _destinationPolicy;
    private readonly ICrossProcessDestinationLockProvider _destinationLockProvider;

    public AtomicDocumentStore(
        IFileVersionStampProvider versionStampProvider,
        IAtomicSaveObserver? observer = null,
        IAtomicDestinationPolicy? destinationPolicy = null,
        ICrossProcessDestinationLockProvider? destinationLockProvider = null)
    {
        _versionStampProvider = versionStampProvider;
        _observer = observer ?? NullAtomicSaveObserver.Instance;
        _destinationPolicy = destinationPolicy ?? new LocalAtomicDestinationPolicy();
        _destinationLockProvider = destinationLockProvider ?? new CrossProcessDestinationLockProvider();
    }

    public async Task<AtomicCommitResult> CommitAsync(
        AtomicSaveRequest request,
        AtomicStreamWriter writer,
        AtomicFileValidator validator,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);

        var destinationPath = Path.GetFullPath(request.DestinationPath);
        var directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("The destination must have a parent directory.", nameof(request));
        }

        _destinationPolicy.EnsureSupported(destinationPath);
        Directory.CreateDirectory(directory);

        var preliminaryVersion = await TryCaptureForConflictAsync(destinationPath, cancellationToken)
            .ConfigureAwait(false);
        var lockIdentity = preliminaryVersion?.FileIdentity is { Length: > 0 } identity
            ? $"file:{identity}"
            : $"path:{preliminaryVersion?.CanonicalPath ?? destinationPath}";
        await using var destinationLock = await _destinationLockProvider
            .AcquireAsync(lockIdentity, cancellationToken)
            .ConfigureAwait(false);

        var transactionId = Guid.NewGuid().ToString("N");
        var fileName = Path.GetFileName(destinationPath);
        var temporaryPath = Path.Combine(directory, $".{fileName}.ellie-{transactionId}.tmp");
        var backupPath = Path.Combine(directory, $".{fileName}.ellie-{transactionId}.bak");
        var displacedPath = Path.Combine(directory, $".{fileName}.ellie-{transactionId}.displaced");
        var journalPath = Path.Combine(directory, $".{fileName}.ellie-{transactionId}.journal");

        var outcome = AtomicCommitOutcome.NotCommitted;
        var replacedExisting = false;
        FileVersionStamp? preparedVersion = null;
        FileVersionStamp? initialVersion = null;

        try
        {
            await NotifyAsync(AtomicSaveStage.DestinationLockAcquired, transactionId, cancellationToken)
                .ConfigureAwait(false);

            initialVersion = await TryCaptureForConflictAsync(destinationPath, cancellationToken)
                .ConfigureAwait(false);
            VerifyExpectedVersion(request, initialVersion);
            await NotifyAsync(AtomicSaveStage.DestinationVersionVerified, transactionId, cancellationToken)
                .ConfigureAwait(false);

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await NotifyAsync(AtomicSaveStage.TemporaryFileCreated, transactionId, cancellationToken)
                    .ConfigureAwait(false);
                await writer(stream, cancellationToken).ConfigureAwait(false);
                await NotifyAsync(AtomicSaveStage.TemporaryFileWritten, transactionId, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            await NotifyAsync(AtomicSaveStage.TemporaryFileFlushed, transactionId, cancellationToken)
                .ConfigureAwait(false);
            await validator(temporaryPath, cancellationToken).ConfigureAwait(false);
            await NotifyAsync(AtomicSaveStage.TemporaryFileValidated, transactionId, cancellationToken)
                .ConfigureAwait(false);

            preparedVersion = await _versionStampProvider
                .TryCaptureAsync(temporaryPath, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new IOException("The prepared file disappeared before commit.");

            WriteJournalDurably(new AtomicJournal(
                transactionId,
                AtomicJournalStage.Prepared,
                destinationPath,
                temporaryPath,
                backupPath,
                displacedPath,
                request.CapturedRevision.Value,
                initialVersion?.Sha256,
                preparedVersion.Sha256,
                DateTimeOffset.UtcNow), journalPath);

            var preCommitVersion = await TryCaptureForConflictAsync(destinationPath, cancellationToken)
                .ConfigureAwait(false);
            if (!VersionsMatch(initialVersion, preCommitVersion))
            {
                throw new AtomicSaveConflictException(
                    "The destination changed while the new document was being prepared.");
            }

            await NotifyAsync(AtomicSaveStage.DestinationVersionReverified, transactionId, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            WriteJournalDurably(new AtomicJournal(
                transactionId,
                AtomicJournalStage.CommitStarted,
                destinationPath,
                temporaryPath,
                backupPath,
                displacedPath,
                request.CapturedRevision.Value,
                preCommitVersion?.Sha256,
                preparedVersion.Sha256,
                DateTimeOffset.UtcNow), journalPath);
            await NotifyAsync(AtomicSaveStage.CommitStarted, transactionId, CancellationToken.None)
                .ConfigureAwait(false);

            try
            {
                if (preCommitVersion is null)
                {
                    File.Move(temporaryPath, destinationPath);
                }
                else
                {
                    File.Replace(temporaryPath, destinationPath, backupPath, ignoreMetadataErrors: true);
                    replacedExisting = true;
                }
            }
            catch (IOException exception) when (preCommitVersion is null && File.Exists(destinationPath))
            {
                throw new AtomicSaveConflictException(
                    "The destination was created by another process immediately before commit.",
                    AtomicCommitOutcome.NotCommitted,
                    journalPath,
                    exception);
            }
            catch (Exception exception) when (exception is IOException
                                               or PlatformNotSupportedException
                                               or UnauthorizedAccessException)
            {
                throw new AtomicCommitNotSupportedException(
                    "The destination filesystem could not perform an atomic commit. Use Save As to a supported local volume.",
                    exception);
            }

            // From this point until validation proves the exact prepared bytes,
            // a crash or unexpected I/O failure has an explicitly unknown outcome.
            outcome = AtomicCommitOutcome.OutcomeUnknown;
            TryWriteJournal(new AtomicJournal(
                transactionId,
                AtomicJournalStage.Committed,
                destinationPath,
                temporaryPath,
                backupPath,
                displacedPath,
                request.CapturedRevision.Value,
                preCommitVersion?.Sha256,
                preparedVersion.Sha256,
                DateTimeOffset.UtcNow), journalPath);
            await NotifyAsync(AtomicSaveStage.CommitCompleted, transactionId, CancellationToken.None)
                .ConfigureAwait(false);

            if (replacedExisting)
            {
                var replacedVersion = await _versionStampProvider
                    .TryCaptureAsync(backupPath, CancellationToken.None)
                    .ConfigureAwait(false)
                    ?? throw CreateUnknown(
                        "The replacement backup disappeared before it could be verified.",
                        journalPath,
                        new IOException("Replacement backup missing."));

                if (!VersionsEquivalentExceptPath(preCommitVersion!, replacedVersion))
                {
                    await RestorePreviousVersionAsync(
                            destinationPath,
                            backupPath,
                            displacedPath,
                            preparedVersion,
                            replacedVersion,
                            replacedExisting: true)
                        .ConfigureAwait(false);
                    outcome = AtomicCommitOutcome.RolledBack;
                    TryWriteJournalStage(journalPath, transactionId, AtomicJournalStage.RolledBack,
                        destinationPath, temporaryPath, backupPath, displacedPath, request, preCommitVersion, preparedVersion);
                    throw new AtomicSaveConflictException(
                        "The destination changed at the commit boundary; the displaced version was restored.",
                        AtomicCommitOutcome.RolledBack,
                        journalPath);
                }
            }

            var committedCandidate = await CaptureCommittedPreparedVersionAsync(
                    destinationPath,
                    preparedVersion,
                    journalPath)
                .ConfigureAwait(false);

            try
            {
                await validator(destinationPath, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception validationException)
            {
                try
                {
                    await RestorePreviousVersionAsync(
                            destinationPath,
                            backupPath,
                            displacedPath,
                            preparedVersion,
                            initialVersion,
                            replacedExisting)
                        .ConfigureAwait(false);
                    outcome = AtomicCommitOutcome.RolledBack;
                    TryWriteJournalStage(journalPath, transactionId, AtomicJournalStage.RolledBack,
                        destinationPath, temporaryPath, backupPath, displacedPath, request, initialVersion, preparedVersion);
                    throw new AtomicPostCommitValidationException(
                        "The committed document failed validation and the previous destination was restored.",
                        AtomicCommitOutcome.RolledBack,
                        journalPath,
                        validationException);
                }
                catch (AtomicPostCommitValidationException)
                {
                    throw;
                }
                catch (Exception rollbackException)
                {
                    outcome = AtomicCommitOutcome.OutcomeUnknown;
                    TryWriteJournalStage(journalPath, transactionId, AtomicJournalStage.OutcomeUnknown,
                        destinationPath, temporaryPath, backupPath, displacedPath, request, initialVersion, preparedVersion);
                    throw CreateUnknown(
                        "Post-commit validation failed and rollback could not be proven. The journal and recoverable files were preserved.",
                        journalPath,
                        new AggregateException(validationException, rollbackException));
                }
            }

            await NotifyAsync(AtomicSaveStage.CommittedFileValidated, transactionId, CancellationToken.None)
                .ConfigureAwait(false);
            var committedVersion = await CaptureCommittedPreparedVersionAsync(
                    destinationPath,
                    preparedVersion,
                    journalPath)
                .ConfigureAwait(false);

            if (!committedCandidate.IdentifiesSameFile(committedVersion)
                || !committedCandidate.ContentMatches(committedVersion))
            {
                throw CreateUnknown(
                    "The destination changed during post-commit validation.",
                    journalPath,
                    new IOException("Post-commit file identity changed."));
            }

            outcome = AtomicCommitOutcome.Committed;
            TryWriteJournalStage(journalPath, transactionId, AtomicJournalStage.Validated,
                destinationPath, temporaryPath, backupPath, displacedPath, request, initialVersion, preparedVersion);
            CleanupTransactionFiles(temporaryPath, backupPath, displacedPath, journalPath);
            await NotifyAsync(AtomicSaveStage.CleanupCompleted, transactionId, CancellationToken.None)
                .ConfigureAwait(false);

            return new AtomicCommitResult(
                destinationPath,
                request.CapturedRevision,
                committedVersion,
                replacedExisting,
                AtomicCommitOutcome.Committed);
        }
        catch (AtomicSaveException)
        {
            await NotifyFailureAsync(AtomicSaveFailureKind.Integrity, transactionId)
                .ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException) when (outcome == AtomicCommitOutcome.NotCommitted)
        {
            await NotifyFailureAsync(AtomicSaveFailureKind.Cancelled, transactionId)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (outcome == AtomicCommitOutcome.OutcomeUnknown)
        {
            await NotifyFailureAsync(AtomicSaveFailureKind.OutcomeUnknown, transactionId)
                .ConfigureAwait(false);
            TryWriteJournalStage(journalPath, transactionId, AtomicJournalStage.OutcomeUnknown,
                destinationPath, temporaryPath, backupPath, displacedPath, request, initialVersion, preparedVersion);
            throw CreateUnknown(
                "The destination may have committed, but its final state could not be proven. Recovery evidence was preserved.",
                journalPath,
                exception);
        }
        catch
        {
            await NotifyFailureAsync(AtomicSaveFailureKind.Unexpected, transactionId)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (outcome is AtomicCommitOutcome.NotCommitted or AtomicCommitOutcome.RolledBack)
            {
                CleanupTransactionFiles(temporaryPath, backupPath, displacedPath, journalPath);
            }
            else if (outcome == AtomicCommitOutcome.Committed)
            {
                CleanupTransactionFiles(temporaryPath, backupPath, displacedPath, journalPath);
            }
            // OutcomeUnknown deliberately preserves journal/backup/displaced files.
        }
    }

    private async Task<FileVersionStamp> CaptureCommittedPreparedVersionAsync(
        string destinationPath,
        FileVersionStamp preparedVersion,
        string journalPath)
    {
        FileVersionStamp? committed;
        try
        {
            committed = await _versionStampProvider
                .TryCaptureAsync(destinationPath, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw CreateUnknown(
                "The committed destination could not be fingerprinted.",
                journalPath,
                exception);
        }

        if (committed is null || !preparedVersion.ContentMatches(committed))
        {
            throw CreateUnknown(
                "The destination does not contain the exact prepared payload.",
                journalPath,
                new IOException("Committed payload fingerprint mismatch."));
        }

        return committed;
    }

    private async Task RestorePreviousVersionAsync(
        string destinationPath,
        string backupPath,
        string displacedPath,
        FileVersionStamp preparedVersion,
        FileVersionStamp? versionToRestore,
        bool replacedExisting)
    {
        var current = await _versionStampProvider
            .TryCaptureAsync(destinationPath, CancellationToken.None)
            .ConfigureAwait(false);
        if (current is null || !preparedVersion.ContentMatches(current))
        {
            throw new IOException(
                "Rollback was refused because another writer changed the committed destination.");
        }

        if (replacedExisting)
        {
            if (versionToRestore is null || !File.Exists(backupPath))
            {
                throw new IOException("The replacement backup required for rollback is missing.");
            }

            File.Replace(backupPath, destinationPath, displacedPath, ignoreMetadataErrors: true);
            var restored = await _versionStampProvider
                .TryCaptureAsync(destinationPath, CancellationToken.None)
                .ConfigureAwait(false);
            var displaced = await _versionStampProvider
                .TryCaptureAsync(displacedPath, CancellationToken.None)
                .ConfigureAwait(false);

            if (restored is null || !versionToRestore.ContentMatches(restored))
            {
                throw new IOException("Rollback did not restore the expected previous destination.");
            }

            if (displaced is null || !preparedVersion.ContentMatches(displaced))
            {
                throw new IOException(
                    "Another writer changed the destination during rollback; the displaced file was preserved.");
            }

            TryDelete(displacedPath);
            return;
        }

        if (!File.Exists(destinationPath))
        {
            return;
        }

        File.Move(destinationPath, displacedPath);
        var moved = await _versionStampProvider
            .TryCaptureAsync(displacedPath, CancellationToken.None)
            .ConfigureAwait(false);
        if (moved is null || !preparedVersion.ContentMatches(moved))
        {
            if (!File.Exists(destinationPath))
            {
                File.Move(displacedPath, destinationPath);
            }

            throw new IOException(
                "Another writer changed the new destination during rollback; its file was restored.");
        }

        TryDelete(displacedPath);
    }

    private async ValueTask<FileVersionStamp?> TryCaptureForConflictAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _versionStampProvider.TryCaptureAsync(path, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            throw new AtomicSaveConflictException(
                "The destination is being modified by another process.",
                AtomicCommitOutcome.NotCommitted,
                null,
                exception);
        }
    }

    private static void VerifyExpectedVersion(
        AtomicSaveRequest request,
        FileVersionStamp? actualVersion)
    {
        if (request.FailIfDestinationExists && actualVersion is not null)
        {
            throw new AtomicSaveConflictException("The destination already exists.");
        }

        if (request.ExpectedDestinationVersion is null)
        {
            return;
        }

        if (!request.ExpectedDestinationVersion.Matches(actualVersion))
        {
            throw new AtomicSaveConflictException(
                "The destination changed since it was opened or last saved.");
        }
    }

    private static bool VersionsMatch(FileVersionStamp? left, FileVersionStamp? right) =>
        left is null ? right is null : left.Matches(right);

    private static bool VersionsEquivalentExceptPath(
        FileVersionStamp left,
        FileVersionStamp right) =>
        string.Equals(left.FileIdentity, right.FileIdentity, StringComparison.Ordinal)
        && left.Length == right.Length
        && left.LastWriteUtc == right.LastWriteUtc
        && string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase);

    private async ValueTask NotifyAsync(
        AtomicSaveStage stage,
        string transactionId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _observer.OnStageAsync(stage, transactionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Observability is never authoritative over a data-integrity transaction.
            System.Diagnostics.Debug.WriteLine(
                $"Atomic save observer failed at {stage}: {exception}");
        }
    }

    private async ValueTask NotifyFailureAsync(
        AtomicSaveFailureKind failureKind,
        string transactionId)
    {
        if (_observer is not IAtomicSaveLifecycleObserver lifecycleObserver)
        {
            return;
        }

        try
        {
            await lifecycleObserver.OnFailedAsync(failureKind, transactionId)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Atomic save failure observer failed: {exception}");
        }
    }

    private static AtomicCommitOutcomeUnknownException CreateUnknown(
        string message,
        string journalPath,
        Exception innerException) =>
        new(message, journalPath, innerException);

    private static void TryWriteJournalStage(
        string journalPath,
        string transactionId,
        AtomicJournalStage stage,
        string destinationPath,
        string temporaryPath,
        string backupPath,
        string displacedPath,
        AtomicSaveRequest request,
        FileVersionStamp? initialVersion,
        FileVersionStamp? preparedVersion)
    {
        if (preparedVersion is null)
        {
            return;
        }

        TryWriteJournal(new AtomicJournal(
            transactionId,
            stage,
            destinationPath,
            temporaryPath,
            backupPath,
            displacedPath,
            request.CapturedRevision.Value,
            initialVersion?.Sha256,
            preparedVersion.Sha256,
            DateTimeOffset.UtcNow), journalPath);
    }

    private static void TryWriteJournal(AtomicJournal journal, string journalPath)
    {
        try
        {
            WriteJournalDurably(journal, journalPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to update atomic-save journal: {exception}");
        }
    }

    private static void WriteJournalDurably(AtomicJournal journal, string journalPath)
    {
        var nextPath = journalPath + ".next";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            journal,
            AtomicDocumentStoreJsonContext.Default.AtomicJournal);
        using (var stream = new FileStream(
            nextPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            16 * 1024,
            FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(journalPath))
        {
            File.Replace(nextPath, journalPath, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(nextPath, journalPath);
        }
    }

    private static void CleanupTransactionFiles(params string[] paths)
    {
        foreach (var path in paths)
        {
            TryDelete(path);
            TryDelete(path + ".next");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal enum AtomicJournalStage
    {
        Prepared,
        CommitStarted,
        Committed,
        Validated,
        RolledBack,
        OutcomeUnknown
    }

    internal sealed record AtomicJournal(
        string TransactionId,
        AtomicJournalStage Stage,
        string DestinationPath,
        string TemporaryPath,
        string BackupPath,
        string DisplacedPath,
        long CapturedRevision,
        string? ExpectedDestinationSha256,
        string PreparedSha256,
        DateTimeOffset UpdatedUtc);
}
