using System.Collections.Immutable;

namespace ElliePdf.Domain.Documents;

/// <summary>
/// A page in an Organizer plan.  It is a reference to an existing source page,
/// rather than a mutable PDF page.  Keeping the source identity and revisions
/// here lets an export reject a source that changed while the preview was open.
/// </summary>
public sealed record OrganizerPage
{
    public OrganizerPage(
        DocumentId documentId,
        PageId pageId,
        string sourcePath,
        int sourcePageIndex,
        PageRotation rotation = PageRotation.None,
        string? label = null,
        ContentRevision sourceContentRevision = default,
        StructureRevision sourceStructureRevision = default,
        PageContentRevision sourcePageContentRevision = default,
        PageId? sourcePageId = null)
    {
        if (documentId.Value == Guid.Empty) throw new ArgumentException("The document id must not be empty.", nameof(documentId));
        if (pageId.Value == Guid.Empty) throw new ArgumentException("The page id must not be empty.", nameof(pageId));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentOutOfRangeException.ThrowIfNegative(sourcePageIndex);
        if (!Enum.IsDefined(rotation)) throw new ArgumentOutOfRangeException(nameof(rotation));

        DocumentId = documentId;
        PageId = pageId;
        SourcePageId = sourcePageId ?? pageId;
        if (SourcePageId.Value == Guid.Empty)
            throw new ArgumentException("The source page id must not be empty.", nameof(sourcePageId));
        SourcePath = Path.GetFullPath(sourcePath);
        SourcePageIndex = sourcePageIndex;
        Rotation = rotation;
        Label = label;
        SourceContentRevision = sourceContentRevision;
        SourceStructureRevision = sourceStructureRevision;
        SourcePageContentRevision = sourcePageContentRevision;
    }

    public DocumentId DocumentId { get; }
    public PageId PageId { get; init; }
    public PageId SourcePageId { get; }
    public string SourcePath { get; }
    public int SourcePageIndex { get; }
    public PageRotation Rotation { get; init; }
    public string? Label { get; }
    public ContentRevision SourceContentRevision { get; }
    public StructureRevision SourceStructureRevision { get; }
    public PageContentRevision SourcePageContentRevision { get; }

    public OrganizerPage Rotate(int quarterTurnsClockwise)
    {
        if (quarterTurnsClockwise is < -3 or > 3 || quarterTurnsClockwise == 0)
            throw new ArgumentOutOfRangeException(nameof(quarterTurnsClockwise));

        var current = (int)Rotation;
        var next = ((current + quarterTurnsClockwise) % 4 + 4) % 4;
        return this with { Rotation = (PageRotation)next };
    }
}

/// <summary>
/// Immutable, undoable page plan used by the Organizer preview.  Every edit
/// returns a new plan and does not call into a PDF engine or mutate a source
/// document.  Undo and redo also advance the monotonic revision so renderers
/// can discard work published for an older preview.
/// </summary>
public sealed record OrganizerPagePlan
{
    private OrganizerPagePlan(
        ImmutableArray<OrganizerPage> pages,
        ImmutableArray<OrganizerPage> baseline,
        long revision,
        ImmutableArray<ImmutableArray<OrganizerPage>> undo,
        ImmutableArray<ImmutableArray<OrganizerPage>> redo)
    {
        Pages = pages;
        Baseline = baseline;
        Revision = revision;
        UndoHistory = undo;
        RedoHistory = redo;
    }

    public ImmutableArray<OrganizerPage> Pages { get; }
    public long Revision { get; }
    public bool CanUndo => !UndoHistory.IsDefaultOrEmpty;
    public bool CanRedo => !RedoHistory.IsDefaultOrEmpty;
    public bool IsDirty => !Pages.SequenceEqual(Baseline);

    // Kept private in the public shape; exposing only booleans prevents a UI
    // consumer from accidentally mutating history arrays.
    private ImmutableArray<ImmutableArray<OrganizerPage>> UndoHistory { get; }
    private ImmutableArray<ImmutableArray<OrganizerPage>> RedoHistory { get; }
    private ImmutableArray<OrganizerPage> Baseline { get; }

    public static OrganizerPagePlan Empty { get; } = Create([]);

    public static OrganizerPagePlan Create(IEnumerable<OrganizerPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var items = pages.ToImmutableArray();
        ValidateUnique(items);
        return new OrganizerPagePlan(items, items, 0, [], []);
    }

    public OrganizerPagePlan Reorder(PageId pageId, int targetIndex)
    {
        var currentIndex = IndexOf(pageId);
        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(targetIndex, Pages.Length);
        if (currentIndex == targetIndex) return this;

        var next = Pages.ToBuilder();
        var page = next[currentIndex];
        next.RemoveAt(currentIndex);
        next.Insert(targetIndex, page);
        return Mutate(next.ToImmutable());
    }

    public OrganizerPagePlan Delete(PageId pageId)
    {
        var index = IndexOf(pageId);
        var next = Pages.RemoveAt(index);
        return Mutate(next);
    }

    public OrganizerPagePlan Rotate(PageId pageId, int quarterTurnsClockwise = 1)
    {
        var index = IndexOf(pageId);
        var next = Pages.SetItem(index, Pages[index].Rotate(quarterTurnsClockwise));
        return Mutate(next);
    }

    public OrganizerPagePlan Insert(IEnumerable<OrganizerPage> pages, int index)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, Pages.Length);
        var additions = pages.ToImmutableArray();
        ValidateUnique(additions);
        if (additions.Any(page => Pages.Any(existing => existing.PageId == page.PageId)))
            throw new ArgumentException("Inserted pages must have unique stable identities.", nameof(pages));

        var next = Pages.ToBuilder();
        next.InsertRange(index, additions);
        return Mutate(next.ToImmutable());
    }

    public OrganizerPagePlan Duplicate(PageId pageId, int? insertIndex = null)
    {
        var sourceIndex = IndexOf(pageId);
        var duplicate = Pages[sourceIndex] with { PageId = PageId.New() };
        return Insert([duplicate], insertIndex ?? sourceIndex + 1);
    }

    /// <summary>Combines two plans while preserving the order of both inputs.</summary>
    public OrganizerPagePlan Merge(OrganizerPagePlan other, int? insertIndex = null)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Insert(other.Pages, insertIndex ?? Pages.Length);
    }

    /// <summary>Splits at a page boundary. Both returned plans are clean previews.</summary>
    public (OrganizerPagePlan Before, OrganizerPagePlan After) SplitAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, Pages.Length);
        return (
            Create(Pages[..index]),
            Create(Pages[index..]));
    }

    public OrganizerPagePlan Undo()
    {
        if (!CanUndo) return this;
        var previous = UndoHistory[^1];
        return new OrganizerPagePlan(
            previous,
            Baseline,
            checked(Revision + 1),
            UndoHistory[..^1],
            RedoHistory.Add(Pages));
    }

    public OrganizerPagePlan Redo()
    {
        if (!CanRedo) return this;
        var next = RedoHistory[^1];
        return new OrganizerPagePlan(
            next,
            Baseline,
            checked(Revision + 1),
            UndoHistory.Add(Pages),
            RedoHistory[..^1]);
    }

    private OrganizerPagePlan Mutate(ImmutableArray<OrganizerPage> next)
    {
        ValidateUnique(next);
        return new OrganizerPagePlan(
            next,
            Baseline,
            checked(Revision + 1),
            UndoHistory.Add(Pages),
            []);
    }

    private int IndexOf(PageId pageId)
    {
        var index = -1;
        for (var candidate = 0; candidate < Pages.Length; candidate++)
        {
            if (Pages[candidate].PageId == pageId)
            {
                index = candidate;
                break;
            }
        }
        return index >= 0
            ? index
            : throw new KeyNotFoundException($"The page {pageId} is not present in this plan.");
    }

    private static void ValidateUnique(ImmutableArray<OrganizerPage> pages)
    {
        var ids = new HashSet<PageId>();
        foreach (var page in pages)
        {
            ArgumentNullException.ThrowIfNull(page);
            if (!ids.Add(page.PageId))
                throw new ArgumentException("A page identity may occur only once in a plan.", nameof(pages));
        }
    }
}
