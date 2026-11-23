namespace ElliePdf.Domain.Documents;

public enum RecoveryState
{
    None,
    Pending,
    Checkpointed,
    Failed
}

public enum CommitState
{
    Idle,
    Saving,
    Failed
}

public enum ExternalFileState
{
    Unchanged,
    Changed,
    Missing,
    Unknown
}

public enum SaveFailureKind
{
    None,
    Cancelled,
    ExternalChanged,
    Missing,
    ReadOnly,
    UnsupportedVolume,
    ValidationFailed,
    OutcomeUnknown,
    IoFailure
}

public readonly record struct SaveOperationId(Guid Value)
{
    public static SaveOperationId New() => new(Guid.NewGuid());
}

public sealed record SaveOperation(
    SaveOperationId Id,
    DocumentId DocumentId,
    ContentRevision CapturedRevision);

public sealed record DocumentState
{
    private DocumentState()
    {
    }

    public required DocumentId DocumentId { get; init; }

    public required ContentRevision ContentRevision { get; init; }

    public required ContentRevision SavedRevision { get; init; }

    public required StructureRevision StructureRevision { get; init; }

    public required RenderGeneration RenderGeneration { get; init; }

    public required SearchGeneration SearchGeneration { get; init; }

    public required RecoveryState RecoveryState { get; init; }

    public ContentRevision? CheckpointedRevision { get; init; }

    public required CommitState CommitState { get; init; }

    public required ExternalFileState ExternalFileState { get; init; }

    public SaveOperation? ActiveSave { get; init; }

    public string? LastCommitErrorCode { get; init; }

    public SaveFailureKind LastSaveFailure { get; init; }

    public bool HasUnsavedChanges => ContentRevision != SavedRevision;

    public static DocumentState Create(DocumentId documentId) => new()
    {
        DocumentId = documentId,
        ContentRevision = ContentRevision.Initial,
        SavedRevision = ContentRevision.Initial,
        StructureRevision = StructureRevision.Initial,
        RenderGeneration = RenderGeneration.Initial,
        SearchGeneration = SearchGeneration.Initial,
        RecoveryState = RecoveryState.None,
        CheckpointedRevision = null,
        CommitState = CommitState.Idle,
        ExternalFileState = ExternalFileState.Unchanged,
        LastSaveFailure = SaveFailureKind.None
    };
}

public sealed record SaveTransition(DocumentState State, SaveOperation Operation);
