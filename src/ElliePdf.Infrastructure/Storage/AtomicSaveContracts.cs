using ElliePdf.Domain.Documents;
using ElliePdf.Domain.Storage;

namespace ElliePdf.Infrastructure.Storage;

public enum AtomicSaveStage
{
    DestinationLockAcquired,
    DestinationVersionVerified,
    TemporaryFileCreated,
    TemporaryFileWritten,
    TemporaryFileFlushed,
    TemporaryFileValidated,
    DestinationVersionReverified,
    CommitStarted,
    CommitCompleted,
    CommittedFileValidated,
    CleanupCompleted
}

public enum AtomicCommitOutcome
{
    NotCommitted,
    Committed,
    RolledBack,
    OutcomeUnknown
}

public sealed record AtomicSaveRequest(
    string DestinationPath,
    ContentRevision CapturedRevision,
    FileVersionStamp? ExpectedDestinationVersion = null,
    bool FailIfDestinationExists = false);

public sealed record AtomicCommitResult(
    string DestinationPath,
    ContentRevision CapturedRevision,
    FileVersionStamp CommittedVersion,
    bool ReplacedExistingFile,
    AtomicCommitOutcome Outcome = AtomicCommitOutcome.Committed);

public delegate ValueTask AtomicStreamWriter(
    Stream destination,
    CancellationToken cancellationToken);

public delegate ValueTask AtomicFileValidator(
    string candidatePath,
    CancellationToken cancellationToken);

public interface IAtomicSaveObserver
{
    ValueTask OnStageAsync(
        AtomicSaveStage stage,
        string transactionId,
        CancellationToken cancellationToken);
}

public enum AtomicSaveFailureKind
{
    Cancelled = 1,
    Integrity = 2,
    OutcomeUnknown = 3,
    Unexpected = 4
}

public interface IAtomicSaveLifecycleObserver : IAtomicSaveObserver
{
    ValueTask OnFailedAsync(
        AtomicSaveFailureKind failureKind,
        string transactionId);
}

public sealed class NullAtomicSaveObserver : IAtomicSaveObserver
{
    public static NullAtomicSaveObserver Instance { get; } = new();

    private NullAtomicSaveObserver()
    {
    }

    public ValueTask OnStageAsync(
        AtomicSaveStage stage,
        string transactionId,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public interface IAtomicDocumentStore
{
    Task<AtomicCommitResult> CommitAsync(
        AtomicSaveRequest request,
        AtomicStreamWriter writer,
        AtomicFileValidator validator,
        CancellationToken cancellationToken = default);
}

public abstract class AtomicSaveException : IOException
{
    protected AtomicSaveException(
        string message,
        AtomicCommitOutcome outcome,
        string? journalPath = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Outcome = outcome;
        JournalPath = journalPath;
    }

    public AtomicCommitOutcome Outcome { get; }

    public string? JournalPath { get; }
}

public sealed class AtomicSaveConflictException : AtomicSaveException
{
    public AtomicSaveConflictException(string message)
        : base(message, AtomicCommitOutcome.NotCommitted)
    {
    }

    public AtomicSaveConflictException(
        string message,
        AtomicCommitOutcome outcome,
        string? journalPath = null,
        Exception? innerException = null)
        : base(message, outcome, journalPath, innerException)
    {
    }
}

public sealed class AtomicCommitNotSupportedException : AtomicSaveException
{
    public AtomicCommitNotSupportedException(string message, Exception innerException)
        : base(message, AtomicCommitOutcome.NotCommitted, null, innerException)
    {
    }
}

public sealed class AtomicPostCommitValidationException : AtomicSaveException
{
    public AtomicPostCommitValidationException(
        string message,
        AtomicCommitOutcome outcome,
        string? journalPath,
        Exception innerException)
        : base(message, outcome, journalPath, innerException)
    {
    }
}

public sealed class AtomicCommitOutcomeUnknownException : AtomicSaveException
{
    public AtomicCommitOutcomeUnknownException(
        string message,
        string journalPath,
        Exception innerException)
        : base(message, AtomicCommitOutcome.OutcomeUnknown, journalPath, innerException)
    {
    }
}
