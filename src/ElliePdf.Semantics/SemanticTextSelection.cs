using System.Collections.Immutable;
using System.Globalization;
using ElliePdf.Pdf.Contracts;

namespace ElliePdf.Semantics;

/// <summary>Character position in the logical PDF reading order.</summary>
public readonly record struct TextPosition(int PageIndex, int CharacterIndex) : IComparable<TextPosition>
{
    public int CompareTo(TextPosition other)
    {
        var page = PageIndex.CompareTo(other.PageIndex);
        return page != 0 ? page : CharacterIndex.CompareTo(other.CharacterIndex);
    }
}

/// <summary>One contiguous page portion of a selection, including geometry for native/UI highlights.</summary>
public sealed record SelectionSegment(
    int PageIndex,
    int Start,
    int End,
    string Text,
    ImmutableArray<PdfRect> Bounds)
{
    public bool IsEmpty => Start == End;
}

public sealed record VisualTextLine(int Start, int End, string Text, PdfRect Bounds, double? FontSize);

/// <summary>
/// Maps ordered PDF text into a selection without depending on a visual control. PDFium's spans may
/// be split by glyph runs, while selection is defined by the page text character offsets.
/// </summary>
public static class SemanticTextSelection
{
    public static SelectionState Create(
        IEnumerable<SemanticPageSnapshot> pages,
        TextPosition anchor,
        TextPosition focus)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var ordered = pages.OrderBy(static page => page.Metadata.PageIndex).ToArray();
        if (ordered.Length == 0) throw new ArgumentException("At least one page is required.", nameof(pages));
        if (ordered.Select(static page => page.Metadata.PageIndex).Distinct().Count() != ordered.Length)
            throw new ArgumentException("Selection pages must have unique page indices.", nameof(pages));

        var firstIndex = Array.BinarySearch(ordered.Select(static page => page.Metadata.PageIndex).ToArray(), anchor.PageIndex);
        var lastIndex = Array.BinarySearch(ordered.Select(static page => page.Metadata.PageIndex).ToArray(), focus.PageIndex);
        if (firstIndex < 0) throw new ArgumentException($"Anchor page {anchor.PageIndex} is not loaded.", nameof(anchor));
        if (lastIndex < 0) throw new ArgumentException($"Focus page {focus.PageIndex} is not loaded.", nameof(focus));

        var forward = anchor.CompareTo(focus) <= 0;
        var start = forward ? anchor : focus;
        var end = forward ? focus : anchor;
        var selectedPages = ordered.Where(page => page.Metadata.PageIndex >= start.PageIndex && page.Metadata.PageIndex <= end.PageIndex).ToArray();
        if (selectedPages.Length != end.PageIndex - start.PageIndex + 1)
            throw new ArgumentException("Every page between the selection endpoints must be loaded.", nameof(pages));
        ValidatePosition(start, selectedPages[0]);
        ValidatePosition(end, selectedPages[^1]);

        var segments = ImmutableArray.CreateBuilder<SelectionSegment>(selectedPages.Length);
        foreach (var page in selectedPages)
        {
            var pageStart = page.Metadata.PageIndex == start.PageIndex ? start.CharacterIndex : 0;
            var pageEnd = page.Metadata.PageIndex == end.PageIndex ? end.CharacterIndex : page.Text.Text.Length;
            if (pageEnd < pageStart) throw new ArgumentException("Selection bounds are reversed.", nameof(focus));
            segments.Add(new SelectionSegment(
                page.Metadata.PageIndex,
                pageStart,
                pageEnd,
                page.Text.Text[pageStart..pageEnd],
                BoundsFor(page.Text.Spans, pageStart, pageEnd)));
        }

        // A page boundary is a logical line break. It is retained even when one side has no
        // characters, so copying a cross-page range cannot silently concatenate words.
        var text = string.Join('\n', segments.Select(static segment => segment.Text));
        return new SelectionState(start.PageIndex, start.CharacterIndex, end.CharacterIndex, text)
        {
            Anchor = anchor,
            Focus = focus,
            Segments = segments.ToImmutable()
        };
    }

    public static (int Start, int End) SelectWord(string text, int characterIndex)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateIndex(text, characterIndex);
        if (text.Length == 0) return (0, 0);
        var index = Math.Min(characterIndex, text.Length - 1);
        if (!IsWordCharacter(text[index])) return (index, index + 1);
        var start = index;
        while (start > 0 && IsWordCharacter(text[start - 1])) start--;
        var end = index + 1;
        while (end < text.Length && IsWordCharacter(text[end])) end++;
        return (start, end);
    }

    /// <summary>Selects the visual line containing a character using PDFium span geometry.</summary>
    public static (int Start, int End) SelectVisualLine(SemanticPageSnapshot page, int characterIndex)
    {
        ArgumentNullException.ThrowIfNull(page);
        ValidateIndex(page.Text.Text, characterIndex);
        if (page.Text.Spans.Length == 0) return (characterIndex, characterIndex);

        var lines = VisualLines(page);
        var line = lines.FirstOrDefault(candidate => characterIndex >= candidate.Start && characterIndex <= candidate.End)
            ?? lines.OrderBy(candidate => Math.Abs(candidate.Start - characterIndex)).First();
        return (line.Start, line.End);
    }

    public static ImmutableArray<VisualTextLine> VisualLines(SemanticPageSnapshot page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var lines = new List<List<TextSpan>>();
        foreach (var span in page.Text.Spans.OrderBy(static span => span.StartIndex))
        {
            var center = CenterY(span.Bounds);
            var height = Math.Max(1, span.Bounds.Bottom - span.Bounds.Top);
            var line = lines.FirstOrDefault(current =>
            {
                var lineCenter = current.Average(static item => CenterY(item.Bounds));
                var lineHeight = current.Max(static item => item.Bounds.Bottom - item.Bounds.Top);
                return Math.Abs(lineCenter - center) <= Math.Max(height, lineHeight) * .65;
            });
            (line ??= []).Add(span);
            if (line.Count == 1) lines.Add(line);
        }

        return [.. lines
            .Select(static line => line.OrderBy(item => item.StartIndex).ToArray())
            .OrderBy(static line => line[0].StartIndex)
            .Select(line => new VisualTextLine(
                line[0].StartIndex,
                line.Max(item => item.StartIndex + item.Text.Length),
                page.Text.Text[line[0].StartIndex..line.Max(item => item.StartIndex + item.Text.Length)],
                new PdfRect(
                    line.Min(item => item.Bounds.Left),
                    line.Min(item => item.Bounds.Top),
                    line.Max(item => item.Bounds.Right),
                    line.Max(item => item.Bounds.Bottom)),
                line.Select(static item => item.FontSize).FirstOrDefault(value => value is not null)))];
    }

    private static ImmutableArray<PdfRect> BoundsFor(IEnumerable<TextSpan> spans, int start, int end) =>
        [.. spans.Where(span => span.StartIndex < end && span.StartIndex + span.Text.Length > start).Select(static span => span.Bounds)];

    private static double CenterY(PdfRect bounds) => (bounds.Top + bounds.Bottom) / 2;
    private static bool IsWordCharacter(char value)
    {
        var category = char.GetUnicodeCategory(value);
        return char.IsLetterOrDigit(value)
            || category is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            || value is '\u200C' or '\u200D';
    }
    private static void ValidatePosition(TextPosition position, SemanticPageSnapshot page) => ValidateIndex(page.Text.Text, position.CharacterIndex);
    private static void ValidateIndex(string text, int index)
    {
        if (index < 0 || index > text.Length) throw new ArgumentOutOfRangeException(nameof(index));
    }
}
