using ElliePdf.Models;

namespace ElliePdf.Services;

/// <summary>A page's overlay state at a point in time.</summary>
public sealed record OverlaySnapshot(int PageIndex, PageOverlayState State);

public interface IOverlayHistory
{
    bool CanUndo(Guid tabId);

    bool CanRedo(Guid tabId);

    /// <summary>Records the state of a page immediately before it is modified.</summary>
    void Record(Guid tabId, int pageIndex, PageOverlayState before);

    /// <summary>
    /// Steps back one edit. <paramref name="current"/> is the live state of the page the snapshot
    /// refers to, so it can be pushed onto the redo stack.
    /// </summary>
    OverlaySnapshot? Undo(Guid tabId, Func<int, PageOverlayState> current);

    OverlaySnapshot? Redo(Guid tabId, Func<int, PageOverlayState> current);

    void Clear(Guid tabId);
}

/// <summary>
/// Per-tab undo/redo history.
/// </summary>
/// <remarks>
/// Snapshots carry the page they belong to, so history spans the whole document rather than being
/// discarded whenever the reader changes page — undoing simply navigates back to where the edit
/// happened. Each entry is a deep copy, which is cheap relative to a page of annotations and keeps
/// the stack immune to later mutation of the live model.
/// </remarks>
public sealed class OverlayHistory : IOverlayHistory
{
    private const int MaxDepth = 100;

    private sealed class TabHistory
    {
        public List<OverlaySnapshot> Undo { get; } = [];

        public List<OverlaySnapshot> Redo { get; } = [];
    }

    private readonly Dictionary<Guid, TabHistory> _histories = [];

    public bool CanUndo(Guid tabId) =>
        _histories.TryGetValue(tabId, out var history) && history.Undo.Count > 0;

    public bool CanRedo(Guid tabId) =>
        _histories.TryGetValue(tabId, out var history) && history.Redo.Count > 0;

    public void Record(Guid tabId, int pageIndex, PageOverlayState before)
    {
        var history = GetOrCreate(tabId);
        history.Undo.Add(new OverlaySnapshot(pageIndex, Clone(before)));

        // A fresh edit invalidates anything that was undone.
        history.Redo.Clear();

        if (history.Undo.Count > MaxDepth)
        {
            history.Undo.RemoveAt(0);
        }
    }

    public OverlaySnapshot? Undo(Guid tabId, Func<int, PageOverlayState> current) =>
        Step(tabId, current, redoing: false);

    public OverlaySnapshot? Redo(Guid tabId, Func<int, PageOverlayState> current) =>
        Step(tabId, current, redoing: true);

    private OverlaySnapshot? Step(Guid tabId, Func<int, PageOverlayState> current, bool redoing)
    {
        if (!_histories.TryGetValue(tabId, out var history))
        {
            return null;
        }

        var source = redoing ? history.Redo : history.Undo;
        var destination = redoing ? history.Undo : history.Redo;

        if (source.Count == 0)
        {
            return null;
        }

        var snapshot = source[^1];
        source.RemoveAt(source.Count - 1);
        destination.Add(new OverlaySnapshot(snapshot.PageIndex, Clone(current(snapshot.PageIndex))));

        return snapshot;
    }

    public void Clear(Guid tabId) => _histories.Remove(tabId);

    private TabHistory GetOrCreate(Guid tabId)
    {
        if (!_histories.TryGetValue(tabId, out var history))
        {
            history = new TabHistory();
            _histories[tabId] = history;
        }

        return history;
    }

    public static PageOverlayState Clone(PageOverlayState source) =>
        new()
        {
            InkStrokes = source.InkStrokes
                .Select(stroke => new InkStrokeOverlay
                {
                    Id = stroke.Id,
                    ColorHex = stroke.ColorHex,
                    Thickness = stroke.Thickness,
                    Points = stroke.Points
                        .Select(point => new PointOverlay { X = point.X, Y = point.Y, Pressure = point.Pressure })
                        .ToList()
                })
                .ToList(),
            Shapes = source.Shapes
                .Select(shape => new ShapeOverlay
                {
                    Id = shape.Id,
                    Kind = shape.Kind,
                    Start = new PointOverlay { X = shape.Start.X, Y = shape.Start.Y },
                    End = new PointOverlay { X = shape.End.X, Y = shape.End.Y },
                    ColorHex = shape.ColorHex,
                    Thickness = shape.Thickness,
                    FillColorHex = shape.FillColorHex
                })
                .ToList(),
            TextItems = source.TextItems
                .Select(text => new TextOverlay
                {
                    Id = text.Id,
                    X = text.X,
                    Y = text.Y,
                    Text = text.Text,
                    FontSize = text.FontSize,
                    Width = text.Width,
                    Height = text.Height,
                    ColorHex = text.ColorHex,
                    IsBold = text.IsBold,
                    IsItalic = text.IsItalic
                })
                .ToList(),
            Signatures = source.Signatures
                .Select(signature => new SignatureOverlay
                {
                    Id = signature.Id,
                    X = signature.X,
                    Y = signature.Y,
                    ImageBase64 = signature.ImageBase64,
                    Width = signature.Width,
                    Height = signature.Height
                })
                .ToList()
        };
}
