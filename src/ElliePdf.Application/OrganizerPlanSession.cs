using ElliePdf.Domain.Documents;

namespace ElliePdf.Application;

/// <summary>
/// Application boundary for the Organizer preview. The session owns only
/// immutable plan values; PDF sessions and persistence are deliberately kept
/// outside it so cancellation cannot partially mutate a source document.
/// </summary>
public sealed class OrganizerPlanSession
{
    private OrganizerPagePlan _plan = OrganizerPagePlan.Empty;

    public OrganizerPagePlan Plan => _plan;

    public void Load(IEnumerable<OrganizerPage> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        _plan = OrganizerPagePlan.Create(pages);
    }

    public OrganizerPagePlan Reorder(PageId pageId, int targetIndex) => _plan = _plan.Reorder(pageId, targetIndex);

    public OrganizerPagePlan Delete(PageId pageId) => _plan = _plan.Delete(pageId);

    public OrganizerPagePlan Rotate(PageId pageId, int quarterTurnsClockwise = 1) =>
        _plan = _plan.Rotate(pageId, quarterTurnsClockwise);

    public OrganizerPagePlan Insert(IEnumerable<OrganizerPage> pages, int index) =>
        _plan = _plan.Insert(pages, index);

    public OrganizerPagePlan Duplicate(PageId pageId, int? insertIndex = null) =>
        _plan = _plan.Duplicate(pageId, insertIndex);

    public OrganizerPagePlan Merge(OrganizerPagePlan other, int? insertIndex = null) =>
        _plan = _plan.Merge(other, insertIndex);

    public (OrganizerPagePlan Before, OrganizerPagePlan After) SplitAt(int index) => _plan.SplitAt(index);

    public OrganizerPagePlan Undo() => _plan = _plan.Undo();

    public OrganizerPagePlan Redo() => _plan = _plan.Redo();

    /// <summary>Captures a stable export input that can be committed later.</summary>
    public OrganizerExportSnapshot CaptureExport()
    {
        if (_plan.Pages.IsDefaultOrEmpty)
            throw new InvalidOperationException("An Organizer export requires at least one page.");

        return new OrganizerExportSnapshot(_plan.Revision, _plan.Pages);
    }

    public bool IsCurrent(long revision) => _plan.Revision == revision;
}

public sealed record OrganizerExportSnapshot(
    long PlanRevision,
    System.Collections.Immutable.ImmutableArray<OrganizerPage> Pages);
