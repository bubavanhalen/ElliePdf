namespace ElliePdf.Domain.Documents;

public static class DocumentStateReducer
{
    public static DocumentState ApplyContentMutation(DocumentState state)
    {
        Validate(state);

        return state with
        {
            ContentRevision = state.ContentRevision.Next(),
            RenderGeneration = state.RenderGeneration.Next(),
            RecoveryState = RecoveryState.Pending,
            LastCommitErrorCode = null
        };
    }

    public static DocumentState ApplyStructureMutation(DocumentState state) =>
        ApplyContentMutation(state) with
        {
            StructureRevision = state.StructureRevision.Next()
        };

    public static DocumentState ChangeRenderInputs(DocumentState state)
    {
        Validate(state);
        return state with { RenderGeneration = state.RenderGeneration.Next() };
    }

    public static DocumentState ChangeSearch(DocumentState state)
    {
        Validate(state);
        return state with { SearchGeneration = state.SearchGeneration.Next() };
    }

    public static DocumentState MarkRecoveryCheckpointed(
        DocumentState state,
        ContentRevision checkpointedRevision)
    {
        Validate(state);
        if (checkpointedRevision.Value < 0
            || checkpointedRevision.Value > state.ContentRevision.Value)
        {
            throw new InvalidOperationException("A recovery checkpoint cannot represent a future revision.");
        }

        if (!state.HasUnsavedChanges)
        {
            return state with
            {
                RecoveryState = RecoveryState.None,
                CheckpointedRevision = null
            };
        }

        var latestCheckpoint = state.CheckpointedRevision is { } existing
                               && existing.Value > checkpointedRevision.Value
            ? existing
            : checkpointedRevision;

        return state with
        {
            CheckpointedRevision = latestCheckpoint,
            RecoveryState = latestCheckpoint == state.ContentRevision
                ? RecoveryState.Checkpointed
                : RecoveryState.Pending
        };
    }

    public static DocumentState MarkRecoveryCheckpointed(DocumentState state) =>
        MarkRecoveryCheckpointed(state, state.ContentRevision);

    public static DocumentState MarkRecoveryFailed(DocumentState state)
    {
        Validate(state);
        return state with { RecoveryState = RecoveryState.Failed };
    }

    public static DocumentState SetExternalFileState(DocumentState state, ExternalFileState externalState)
    {
        Validate(state);
        return state with { ExternalFileState = externalState };
    }

    public static SaveTransition BeginSave(DocumentState state)
    {
        Validate(state);
        if (state.CommitState == CommitState.Saving)
        {
            throw new InvalidOperationException("A save operation is already active for this document.");
        }

        if (state.ExternalFileState != ExternalFileState.Unchanged)
        {
            throw new InvalidOperationException(
                "The source file conflict must be resolved before overwriting it. Use Reload or Save As.");
        }

        var operation = new SaveOperation(
            SaveOperationId.New(),
            state.DocumentId,
            state.ContentRevision);

        return new SaveTransition(
            state with
            {
                CommitState = CommitState.Saving,
                ActiveSave = operation,
                LastCommitErrorCode = null,
                LastSaveFailure = SaveFailureKind.None
            },
            operation);
    }

    public static DocumentState CompleteSave(DocumentState state, SaveOperation operation)
    {
        ValidateActiveSave(state, operation);
        if (operation.CapturedRevision.Value > state.ContentRevision.Value)
        {
            throw new InvalidOperationException("A save cannot commit a future content revision.");
        }

        var savedRevision = operation.CapturedRevision.Value > state.SavedRevision.Value
            ? operation.CapturedRevision
            : state.SavedRevision;

        return state with
        {
            SavedRevision = savedRevision,
            CommitState = CommitState.Idle,
            ActiveSave = null,
            RecoveryState = savedRevision == state.ContentRevision
                ? state.RecoveryState
                : RecoveryState.Pending,
            ExternalFileState = ExternalFileState.Unchanged,
            LastCommitErrorCode = null,
            LastSaveFailure = SaveFailureKind.None
        };
    }

    public static DocumentState FailSave(DocumentState state, SaveOperation operation, string errorCode)
        => FailSave(state, operation, SaveFailureKind.IoFailure, errorCode);

    public static DocumentState FailSave(
        DocumentState state,
        SaveOperation operation,
        SaveFailureKind failureKind,
        string errorCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ValidateActiveSave(state, operation);

        var externalFileState = failureKind switch
        {
            SaveFailureKind.ExternalChanged => ExternalFileState.Changed,
            SaveFailureKind.Missing => ExternalFileState.Missing,
            SaveFailureKind.OutcomeUnknown => ExternalFileState.Unknown,
            _ => state.ExternalFileState
        };

        return state with
        {
            CommitState = CommitState.Failed,
            ActiveSave = null,
            ExternalFileState = externalFileState,
            LastCommitErrorCode = errorCode,
            LastSaveFailure = failureKind
        };
    }

    public static DocumentState CancelSave(DocumentState state, SaveOperation operation)
    {
        ValidateActiveSave(state, operation);
        return state with
        {
            CommitState = CommitState.Idle,
            ActiveSave = null,
            LastCommitErrorCode = null,
            LastSaveFailure = SaveFailureKind.Cancelled
        };
    }

    public static DocumentState AcknowledgeCommitFailure(DocumentState state)
    {
        Validate(state);
        if (state.CommitState != CommitState.Failed)
        {
            return state;
        }

        return state with
        {
            CommitState = CommitState.Idle,
            LastCommitErrorCode = null
        };
    }

    public static DocumentState DiscardRecovery(DocumentState state)
    {
        Validate(state);
        if (state.CommitState == CommitState.Saving)
        {
            throw new InvalidOperationException("Recovery cannot be discarded while a commit is active.");
        }

        return state with
        {
            RecoveryState = state.HasUnsavedChanges ? RecoveryState.Pending : RecoveryState.None,
            CheckpointedRevision = null
        };
    }

    private static void ValidateActiveSave(DocumentState state, SaveOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Validate(state);

        if (state.CommitState != CommitState.Saving
            || state.ActiveSave is null
            || state.ActiveSave.Id != operation.Id
            || operation.DocumentId != state.DocumentId)
        {
            throw new InvalidOperationException("The save completion does not match the active operation.");
        }
    }

    private static void Validate(DocumentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.ContentRevision.Value < 0
            || state.SavedRevision.Value < 0
            || state.SavedRevision.Value > state.ContentRevision.Value
            || state.StructureRevision.Value < 0
            || state.RenderGeneration.Value < 0
            || state.SearchGeneration.Value < 0)
        {
            throw new InvalidOperationException("Document revisions are inconsistent.");
        }

        if ((state.CommitState == CommitState.Saving) != (state.ActiveSave is not null))
        {
            throw new InvalidOperationException("Commit state and active save identity are inconsistent.");
        }
    }
}
