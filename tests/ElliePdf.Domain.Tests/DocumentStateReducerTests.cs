using ElliePdf.Domain.Documents;
using Xunit;

namespace ElliePdf.Domain.Tests;

public sealed class DocumentStateReducerTests
{
    [Fact]
    public void ContentMutation_marks_dirty_and_advances_render_recovery()
    {
        var initial = DocumentState.Create(DocumentId.New());
        var state = DocumentStateReducer.ApplyContentMutation(initial);

        Assert.True(state.HasUnsavedChanges);
        Assert.Equal(1, state.ContentRevision.Value);
        Assert.Equal(1, state.RenderGeneration.Value);
        Assert.Equal(RecoveryState.Pending, state.RecoveryState);
        Assert.Equal(CommitState.Idle, state.CommitState);
    }

    [Fact]
    public void StructureMutation_advances_content_and_structure()
    {
        var state = DocumentStateReducer.ApplyStructureMutation(DocumentState.Create(DocumentId.New()));
        Assert.Equal(1, state.ContentRevision.Value);
        Assert.Equal(1, state.StructureRevision.Value);
    }

    [Fact]
    public void Save_during_edit_preserves_dirty_revision()
    {
        var edited = DocumentStateReducer.ApplyContentMutation(DocumentState.Create(DocumentId.New()));
        var transition = DocumentStateReducer.BeginSave(edited);
        var editedAgain = DocumentStateReducer.ApplyContentMutation(transition.State);
        var completed = DocumentStateReducer.CompleteSave(editedAgain, transition.Operation);

        Assert.Equal(1, completed.SavedRevision.Value);
        Assert.Equal(2, completed.ContentRevision.Value);
        Assert.True(completed.HasUnsavedChanges);
        Assert.Equal(RecoveryState.Pending, completed.RecoveryState);
    }

    [Fact]
    public void Recovery_checkpoint_and_failure_are_explicit()
    {
        var clean = DocumentState.Create(DocumentId.New());
        Assert.Equal(RecoveryState.None, DocumentStateReducer.MarkRecoveryCheckpointed(clean).RecoveryState);

        var state = DocumentStateReducer.ApplyContentMutation(clean);
        var checkpointed = DocumentStateReducer.MarkRecoveryCheckpointed(state, state.ContentRevision);
        Assert.Equal(RecoveryState.Checkpointed, checkpointed.RecoveryState);
        Assert.Equal(state.ContentRevision, checkpointed.CheckpointedRevision);
        Assert.Equal(RecoveryState.Failed, DocumentStateReducer.MarkRecoveryFailed(state).RecoveryState);
    }

    [Fact]
    public void Older_checkpoint_cannot_clear_pending_newer_revision()
    {
        var revisionOne = DocumentStateReducer.ApplyContentMutation(DocumentState.Create(DocumentId.New()));
        var revisionTwo = DocumentStateReducer.ApplyContentMutation(revisionOne);
        var completedOldCheckpoint = DocumentStateReducer.MarkRecoveryCheckpointed(
            revisionTwo,
            revisionOne.ContentRevision);

        Assert.Equal(RecoveryState.Pending, completedOldCheckpoint.RecoveryState);
        Assert.Equal(revisionOne.ContentRevision, completedOldCheckpoint.CheckpointedRevision);
    }

    [Fact]
    public void Cancellation_returns_commit_to_idle_without_losing_dirty_state()
    {
        var edited = DocumentStateReducer.ApplyContentMutation(DocumentState.Create(DocumentId.New()));
        var transition = DocumentStateReducer.BeginSave(edited);
        var cancelled = DocumentStateReducer.CancelSave(transition.State, transition.Operation);

        Assert.Equal(CommitState.Idle, cancelled.CommitState);
        Assert.Null(cancelled.ActiveSave);
        Assert.True(cancelled.HasUnsavedChanges);
    }

    [Fact]
    public void Stale_or_duplicate_save_completion_is_rejected()
    {
        var transition = DocumentStateReducer.BeginSave(DocumentState.Create(DocumentId.New()));
        var other = transition.Operation with { Id = SaveOperationId.New() };
        Assert.Throws<InvalidOperationException>(() => DocumentStateReducer.CompleteSave(transition.State, other));
        var completed = DocumentStateReducer.CompleteSave(transition.State, transition.Operation);
        Assert.Throws<InvalidOperationException>(() => DocumentStateReducer.CompleteSave(completed, transition.Operation));
    }

    [Fact]
    public void Only_one_save_can_be_active()
    {
        var transition = DocumentStateReducer.BeginSave(DocumentState.Create(DocumentId.New()));
        Assert.Throws<InvalidOperationException>(() => DocumentStateReducer.BeginSave(transition.State));
    }

    [Fact]
    public void Failed_save_can_be_acknowledged_without_losing_dirty_state()
    {
        var edited = DocumentStateReducer.ApplyContentMutation(DocumentState.Create(DocumentId.New()));
        var transition = DocumentStateReducer.BeginSave(edited);
        var failed = DocumentStateReducer.FailSave(transition.State, transition.Operation, "io");
        Assert.Equal(CommitState.Failed, failed.CommitState);
        Assert.True(failed.HasUnsavedChanges);
        Assert.Equal(CommitState.Idle, DocumentStateReducer.AcknowledgeCommitFailure(failed).CommitState);
    }

    [Fact]
    public void External_conflict_is_typed_and_blocks_blind_overwrite()
    {
        var edited = DocumentStateReducer.ApplyContentMutation(DocumentState.Create(DocumentId.New()));
        var transition = DocumentStateReducer.BeginSave(edited);
        var failed = DocumentStateReducer.FailSave(
            transition.State,
            transition.Operation,
            SaveFailureKind.ExternalChanged,
            "conflict");

        Assert.Equal(ExternalFileState.Changed, failed.ExternalFileState);
        Assert.Equal(SaveFailureKind.ExternalChanged, failed.LastSaveFailure);
        Assert.Throws<InvalidOperationException>(() => DocumentStateReducer.BeginSave(failed));
    }
}
