using ElliePdf.Domain.Documents;
using Xunit;

namespace ElliePdf.Domain.Tests;

public sealed class OrganizerPagePlanTests
{
    [Fact]
    public void Mixed_edits_are_immutable_and_undo_redo_is_deterministic()
    {
        var pages = CreatePages(4);
        var original = OrganizerPagePlan.Create(pages);
        var edited = original
            .Reorder(pages[0].PageId, 2)
            .Rotate(pages[1].PageId)
            .Delete(pages[3].PageId)
            .Duplicate(pages[1].PageId);

        Assert.Equal(pages, original.Pages);
        Assert.Equal(4, edited.Pages.Length);
        Assert.Equal(PageRotation.Clockwise90, edited.Pages.Single(page => page.PageId == pages[1].PageId).Rotation);

        var replayed = edited.Undo().Undo().Undo().Undo().Redo().Redo().Redo().Redo();
        Assert.Equal(edited.Pages, replayed.Pages);
    }

    [Fact]
    public void Insert_merge_split_and_duplicate_preserve_stable_source_identity()
    {
        var pages = CreatePages(2);
        var first = OrganizerPagePlan.Create(pages);
        var inserted = new OrganizerPage(
            pages[0].DocumentId,
            PageId.New(),
            pages[0].SourcePath,
            9);
        var otherPage = new OrganizerPage(
            pages[1].DocumentId,
            PageId.New(),
            pages[1].SourcePath,
            10);
        var merged = first.Insert([inserted], 1).Merge(OrganizerPagePlan.Create([otherPage]));
        var duplicate = merged.Duplicate(inserted.PageId);
        var (before, after) = duplicate.SplitAt(2);

        Assert.Equal(5, duplicate.Pages.Length);
        Assert.Equal(2, before.Pages.Length);
        Assert.Equal(3, after.Pages.Length);
        Assert.Equal(pages[0].DocumentId, duplicate.Pages[0].DocumentId);
        var duplicatedSourcePage = duplicate.Pages.Single(page => page.SourcePageIndex == 9 && page.PageId != inserted.PageId);
        Assert.NotEqual(inserted.PageId, duplicatedSourcePage.PageId);
        Assert.Equal(inserted.PageId, duplicatedSourcePage.SourcePageId);
    }

    [Fact]
    public void Rotation_wraps_and_invalid_operations_are_rejected()
    {
        var page = CreatePages(1)[0];
        var plan = OrganizerPagePlan.Create([page]);
        Assert.Equal(PageRotation.Clockwise270, plan.Rotate(page.PageId, -1).Pages[0].Rotation);
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.Rotate(page.PageId, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.Reorder(page.PageId, 2));
    }

    private static OrganizerPage[] CreatePages(int count) => Enumerable.Range(0, count)
        .Select(index => new OrganizerPage(
            new DocumentId(Guid.Parse($"00000000-0000-0000-0000-{index + 1:D12}")),
            new PageId(Guid.Parse($"10000000-0000-0000-0000-{index + 1:D12}")),
            $"source-{index}.pdf",
            index))
        .ToArray();
}
