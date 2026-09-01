using ElliePdf.Models;
using ElliePdf.Domain.Documents;
using ElliePdf.Domain.Storage;

namespace ElliePdf.Services;

public interface IAnnotationStore
{
    event EventHandler<RecoveryCheckpointCompletedEventArgs>? RecoveryCheckpointCompleted;

    PageOverlayState GetPageOverlay(Guid tabId, int pageIndex);

    void SetPageOverlay(
        Guid tabId,
        int pageIndex,
        PageOverlayState state,
        ContentRevision contentRevision);

    void SetFormRecoveryEdit(
        Guid tabId,
        FormRecoveryEdit edit,
        ContentRevision contentRevision);

    IReadOnlyList<FormRecoveryEdit> GetFormRecoveryEdits(Guid tabId);

    bool HasPendingCheckpoint(Guid tabId);

    Task RemoveTabAsync(Guid tabId, CancellationToken cancellationToken = default);

    Task<bool> LoadRecoveryAsync(Guid tabId, string pdfPath, CancellationToken cancellationToken = default);

    Task SaveRecoveryCheckpointAsync(
        Guid tabId,
        string pdfPath,
        ContentRevision contentRevision,
        CancellationToken cancellationToken = default);

    void ScheduleRecoveryCheckpoint(
        Guid tabId,
        string pdfPath,
        ContentRevision contentRevision,
        FileVersionStamp sourceVersion);

    Task FlushPendingSavesAsync(CancellationToken cancellationToken = default);

    PageOverlayDocument? CaptureOverlayDocument(Guid tabId);

    /// <summary>
    /// Removes exactly the overlay/form recovery values that were committed to
    /// the source while retaining edits made after the save snapshot.
    /// </summary>
    Task CommitPersistedEditsAsync(
        Guid tabId,
        PageOverlayDocument persistedSnapshot,
        ContentRevision currentRevision,
        CancellationToken cancellationToken = default);

    Task StopAndDeleteRecoveryAsync(
        Guid tabId,
        string pdfPath,
        CancellationToken cancellationToken = default);

    Task ClearAllRecoveryAsync(CancellationToken cancellationToken = default);
}

public sealed record RecoveryCheckpointCompletedEventArgs(
    Guid TabId,
    ContentRevision Revision,
    bool Succeeded);
