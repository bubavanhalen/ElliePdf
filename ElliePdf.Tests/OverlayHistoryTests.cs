using ElliePdf.Models;
using ElliePdf.Services;
using Xunit;

namespace ElliePdf.Tests;

public sealed class OverlayHistoryTests
{
    private readonly Guid _tab = Guid.NewGuid();
    private readonly OverlayHistory _history = new();

    private static PageOverlayState WithText(string value) => new()
    {
        TextItems = { new TextOverlay { Id = "t1", Text = value } }
    };

    private static string TextOf(PageOverlayState state) => state.TextItems[0].Text;

    [Fact]
    public void Nothing_to_undo_on_a_fresh_tab()
    {
        Assert.False(_history.CanUndo(_tab));
        Assert.False(_history.CanRedo(_tab));
        Assert.Null(_history.Undo(_tab, _ => new PageOverlayState()));
    }

    [Fact]
    public void Undo_returns_the_state_from_before_the_edit()
    {
        _history.Record(_tab, pageIndex: 0, WithText("before"));

        var snapshot = _history.Undo(_tab, _ => WithText("after"));

        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot!.PageIndex);
        Assert.Equal("before", TextOf(snapshot.State));
    }

    [Fact]
    public void Redo_restores_what_undo_reverted()
    {
        _history.Record(_tab, pageIndex: 0, WithText("before"));
        _history.Undo(_tab, _ => WithText("after"));

        Assert.True(_history.CanRedo(_tab));
        var redone = _history.Redo(_tab, _ => WithText("before"));

        Assert.NotNull(redone);
        Assert.Equal("after", TextOf(redone!.State));
    }

    [Fact]
    public void History_survives_moving_between_pages()
    {
        // This is the whole point of keeping history outside the edit surface: an edit made on
        // page 3 must still be undoable after the reader has moved to page 7.
        _history.Record(_tab, pageIndex: 3, WithText("page three"));

        var snapshot = _history.Undo(_tab, _ => WithText("current"));

        Assert.NotNull(snapshot);
        Assert.Equal(3, snapshot!.PageIndex);
        Assert.Equal("page three", TextOf(snapshot.State));
    }

    [Fact]
    public void A_new_edit_discards_the_redo_stack()
    {
        _history.Record(_tab, 0, WithText("first"));
        _history.Undo(_tab, _ => WithText("second"));
        Assert.True(_history.CanRedo(_tab));

        _history.Record(_tab, 0, WithText("branch"));

        Assert.False(_history.CanRedo(_tab));
    }

    [Fact]
    public void Snapshots_are_deep_copies()
    {
        var live = WithText("original");
        _history.Record(_tab, 0, live);

        // Mutating the live model must not reach back into the recorded snapshot.
        live.TextItems[0].Text = "mutated";

        var snapshot = _history.Undo(_tab, _ => live);
        Assert.Equal("original", TextOf(snapshot!.State));
    }

    [Fact]
    public void Histories_are_isolated_per_tab()
    {
        var other = Guid.NewGuid();
        _history.Record(_tab, 0, WithText("mine"));

        Assert.True(_history.CanUndo(_tab));
        Assert.False(_history.CanUndo(other));
    }

    [Fact]
    public void Clearing_a_tab_drops_its_history()
    {
        _history.Record(_tab, 0, WithText("x"));
        _history.Clear(_tab);

        Assert.False(_history.CanUndo(_tab));
    }

    [Fact]
    public void Repeated_undo_walks_back_through_every_edit()
    {
        _history.Record(_tab, 0, WithText("v1"));
        _history.Record(_tab, 0, WithText("v2"));
        _history.Record(_tab, 0, WithText("v3"));

        Assert.Equal("v3", TextOf(_history.Undo(_tab, _ => WithText("v4"))!.State));
        Assert.Equal("v2", TextOf(_history.Undo(_tab, _ => WithText("v3"))!.State));
        Assert.Equal("v1", TextOf(_history.Undo(_tab, _ => WithText("v2"))!.State));
        Assert.False(_history.CanUndo(_tab));
    }

    [Fact]
    public void Cloning_copies_every_annotation_kind()
    {
        var source = new PageOverlayState
        {
            InkStrokes = { new InkStrokeOverlay { Points = { new PointOverlay { X = 1, Y = 2, Pressure = 0.5 } } } },
            Shapes = { new ShapeOverlay { Kind = ShapeKind.Arrow, End = new PointOverlay { X = 9, Y = 9 } } },
            TextItems = { new TextOverlay { Text = "hi" } },
            Signatures = { new SignatureOverlay { ImageBase64 = "abc" } }
        };

        var clone = OverlayHistory.Clone(source);

        Assert.Equal(0.5, clone.InkStrokes[0].Points[0].Pressure);
        Assert.Equal(ShapeKind.Arrow, clone.Shapes[0].Kind);
        Assert.Equal(9, clone.Shapes[0].End.X);
        Assert.Equal("hi", clone.TextItems[0].Text);
        Assert.Equal("abc", clone.Signatures[0].ImageBase64);

        // Independent instances, not shared references.
        clone.Shapes[0].End.X = 1;
        Assert.Equal(9, source.Shapes[0].End.X);
    }
}
