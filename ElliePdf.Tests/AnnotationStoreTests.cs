using ElliePdf.Models;
using ElliePdf.Services;
using Xunit;

namespace ElliePdf.Tests;

public sealed class AnnotationStoreTests
{
    private readonly Guid _tab = Guid.NewGuid();
    private readonly AnnotationStore _store = new();

    private static PageOverlayState Marked(string text) => new()
    {
        TextItems = { new TextOverlay { Text = text } }
    };

    private string TextOn(int pageIndex) =>
        _store.GetOverlayDocument(_tab)!.Pages[pageIndex].TextItems[0].Text;

    [Fact]
    public void Deleting_a_page_shifts_later_pages_down()
    {
        _store.SetPageOverlay(_tab, 0, Marked("first"));
        _store.SetPageOverlay(_tab, 1, Marked("second"));
        _store.SetPageOverlay(_tab, 2, Marked("third"));

        _store.RemovePage(_tab, 1);

        var pages = _store.GetOverlayDocument(_tab)!.Pages;
        Assert.Equal(2, pages.Count);
        Assert.Equal("first", TextOn(0));

        // What was page 2 is now page 1; without the shift it would be written onto the wrong page.
        Assert.Equal("third", TextOn(1));
    }

    [Fact]
    public void Deleting_the_last_page_just_drops_it()
    {
        _store.SetPageOverlay(_tab, 0, Marked("first"));
        _store.SetPageOverlay(_tab, 1, Marked("second"));

        _store.RemovePage(_tab, 1);

        Assert.Single(_store.GetOverlayDocument(_tab)!.Pages);
        Assert.Equal("first", TextOn(0));
    }

    [Fact]
    public void Deleting_a_page_with_no_overlays_leaves_the_rest_alone()
    {
        _store.SetPageOverlay(_tab, 5, Marked("late"));

        _store.RemovePage(_tab, 0);

        Assert.Equal("late", TextOn(4));
    }

    [Fact]
    public void Loading_a_documents_own_annotations_is_not_an_unsaved_change()
    {
        var document = new PageOverlayDocument();
        document.Pages[0] = Marked("already in the file");

        _store.SetOverlayDocument(_tab, document);

        Assert.False(_store.IsTabDirty(_tab));
    }

    [Fact]
    public void Editing_marks_the_tab_dirty()
    {
        _store.SetPageOverlay(_tab, 0, Marked("edited"));
        Assert.True(_store.IsTabDirty(_tab));
    }

    [Fact]
    public void Removing_a_tab_forgets_everything_about_it()
    {
        _store.SetPageOverlay(_tab, 0, Marked("x"));
        _store.RemoveTab(_tab);

        Assert.Null(_store.GetOverlayDocument(_tab));
        Assert.False(_store.IsTabDirty(_tab));
    }
}
