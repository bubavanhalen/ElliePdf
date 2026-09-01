using ElliePdf.Application;
using ElliePdf.Domain.Documents;
using Xunit;

namespace ElliePdf.Application.Tests;

public sealed class OrganizerPlanSessionTests
{
    [Fact]
    public void Capture_is_stable_and_detects_preview_revision_changes()
    {
        var page = new OrganizerPage(
            DocumentId.New(),
            PageId.New(),
            "one.pdf",
            0);
        var session = new OrganizerPlanSession();
        session.Load([page]);
        var capture = session.CaptureExport();

        session.Rotate(page.PageId);

        Assert.False(session.IsCurrent(capture.PlanRevision));
        Assert.Equal(PageRotation.None, capture.Pages[0].Rotation);
        Assert.Equal(PageRotation.Clockwise90, session.Plan.Pages[0].Rotation);
    }

    [Fact]
    public void Empty_export_is_rejected_without_creating_a_partial_transaction()
    {
        var session = new OrganizerPlanSession();
        Assert.Throws<InvalidOperationException>(() => session.CaptureExport());
        Assert.Equal(0, session.Plan.Revision);
    }
}
