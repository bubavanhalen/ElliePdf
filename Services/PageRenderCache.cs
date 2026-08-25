namespace ElliePdf.Services;

public sealed class PageRenderCache
{
    private const int MaxEntries = 48;
    private readonly Dictionary<string, RenderedPage> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();

    public bool TryGet(PdfDocumentSession document, int pageIndex, double scale, out RenderedPage? rendered)
    {
        var key = CreateKey(document, pageIndex, scale);
        if (_entries.TryGetValue(key, out rendered))
        {
            _order.Remove(key);
            _order.AddLast(key);
            return true;
        }

        rendered = null;
        return false;
    }

    public void Set(PdfDocumentSession document, int pageIndex, double scale, RenderedPage rendered)
    {
        var key = CreateKey(document, pageIndex, scale);
        if (_entries.ContainsKey(key))
        {
            _order.Remove(key);
        }

        _entries[key] = rendered;
        _order.AddLast(key);

        while (_order.Count > MaxEntries)
        {
            var oldest = _order.First!.Value;
            _order.RemoveFirst();
            _entries.Remove(oldest);
        }
    }

    public void InvalidateDocument(PdfDocumentSession document)
    {
        var prefix = $"{document.Id:N}:";
        var keys = _entries.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        foreach (var key in keys)
        {
            _entries.Remove(key);
            _order.Remove(key);
        }
    }

    private static string CreateKey(PdfDocumentSession document, int pageIndex, double scale) =>
        $"{document.Id:N}:{pageIndex}:{scale:F3}";
}
