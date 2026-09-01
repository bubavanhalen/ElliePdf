using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;

namespace ElliePdf.Rendering;

/// <summary>Identity carried by every asynchronous publication for a recycled page element.</summary>
public readonly record struct PageRecycleToken(PageId PageId, long Generation)
{
    public bool IsValid => PageId.Value != Guid.Empty && Generation > 0;
}

public sealed record PageAutomationMetadata(
    PageId PageId,
    string Name,
    int Position,
    string TextRange,
    PdfRect Bounds)
{
    public PageAutomationMetadata Validate()
    {
        if (PageId.Value == Guid.Empty) throw new ArgumentException("The page id must not be empty.", nameof(PageId));
        ArgumentException.ThrowIfNullOrEmpty(Name);
        ArgumentException.ThrowIfNullOrEmpty(TextRange);
        Bounds.Validate();
        return this;
    }
}

/// <summary>Atomic payload accepted by a currently bound page element.</summary>
public sealed record PageElementPublication(object? Pixels, PageAutomationMetadata Automation)
{
    public PageElementPublication Validate() => this with { Automation = Automation.Validate() };
}

/// <summary>
/// A recyclable element slot. Binding clears all old content and advances its generation;
/// a result from a previous binding therefore cannot publish either pixels or automation data.
/// </summary>
public sealed class RecycledPageElement
{
    private long _generation;
    private PageLayoutItem? _item;
    private PageElementPublication? _publication;
    private object? _publishedPixels;

    public PageRecycleToken Token => new(_item?.Id ?? default, _generation);
    public PageLayoutItem? Item => _item;
    public PageElementPublication? Publication => _publication;
    public object? PublishedPixels => _publishedPixels;
    public bool IsBound => _item is not null;

    public PageRecycleToken Bind(PageLayoutItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.Validate();
        _generation = checked(_generation + 1);
        _item = item;
        _publication = null;
        _publishedPixels = null;
        return new PageRecycleToken(item.Id, _generation);
    }

    public void Clear()
    {
        _generation = checked(_generation + 1);
        _item = null;
        _publication = null;
        _publishedPixels = null;
    }

    public bool IsCurrent(PageRecycleToken token)
        => _item is not null && token.PageId == _item.Id && token.Generation == _generation;

    public bool TryPublish(PageRecycleToken token, PageElementPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (!IsCurrent(token)) return false;
        if (publication.Automation.PageId != token.PageId) return false;
        _publication = publication.Validate();
        _publishedPixels = publication.Pixels;
        return true;
    }

    public bool TryPublishPixels(PageRecycleToken token, object? pixels)
    {
        if (!IsCurrent(token)) return false;
        _publishedPixels = pixels;
        if (_publication is not null) _publication = _publication with { Pixels = pixels };
        return true;
    }

    public bool TryPublishAutomation(PageRecycleToken token, PageAutomationMetadata automation)
    {
        if (!IsCurrent(token) || automation.PageId != token.PageId) return false;
        _publication = (_publication ?? new PageElementPublication(null, automation)) with { Automation = automation.Validate() };
        return true;
    }
}

/// <summary>Bounded realized-element pool; its size is independent of document page count.</summary>
public sealed class PageElementPool
{
    private readonly RecycledPageElement[] _elements;

    public PageElementPool(int capacity)
    {
        if (capacity is <= 0 or > 12) throw new ArgumentOutOfRangeException(nameof(capacity));
        _elements = Enumerable.Range(0, capacity).Select(_ => new RecycledPageElement()).ToArray();
    }

    public int Capacity => _elements.Length;
    public int BoundCount => _elements.Count(e => e.IsBound);
    public IReadOnlyList<RecycledPageElement> Elements => _elements;

    public RecycledPageElement Get(int slot)
    {
        if ((uint)slot >= (uint)_elements.Length) throw new ArgumentOutOfRangeException(nameof(slot));
        return _elements[slot];
    }
}
