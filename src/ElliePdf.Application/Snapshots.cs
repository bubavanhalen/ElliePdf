using System.Collections.Immutable;
using ElliePdf.Domain.Documents;

namespace ElliePdf.Application;

public sealed record DocumentSnapshot(
    DocumentId Id,
    ContentRevision ContentRevision,
    ContentRevision SavedRevision,
    StructureRevision StructureRevision,
    string DisplayName,
    int PageCount,
    int CurrentPageIndex,
    bool HasUnsavedChanges,
    RecoveryState RecoveryState,
    ExternalFileState ExternalFileState);

public sealed record PageSnapshot(
    PageId Id,
    int PageIndex,
    PageContentRevision ContentRevision,
    PageAppearanceRevision AppearanceRevision,
    PdfSize SizeInPoints);

public sealed record WorkspaceSnapshot(
    ImmutableArray<DocumentSnapshot> Documents,
    DocumentId? ActiveDocumentId)
{
    public static WorkspaceSnapshot Empty => new(ImmutableArray<DocumentSnapshot>.Empty, null);
}
