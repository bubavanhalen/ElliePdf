namespace ElliePdf.Services;

public sealed record PdfRect(float Left, float Top, float Right, float Bottom);

public sealed record RenderedPage(
    byte[] PngBytes,
    int Width,
    int Height,
    float PageWidthPoints,
    float PageHeightPoints);

public sealed record TextMatch(
    int PageIndex,
    int CharIndex,
    int MatchLength,
    string Context,
    IReadOnlyList<PdfRect> HighlightRects);

public sealed record PdfOutlineItem(
    string Title,
    int PageIndex,
    IReadOnlyList<PdfOutlineItem> Children);

public enum PdfFormFieldType
{
    Unknown = 0,
    PushButton = 1,
    CheckBox = 2,
    RadioButton = 3,
    ComboBox = 4,
    ListBox = 5,
    Text = 6,
    Signature = 7
}

public sealed record PdfFormField(
    int PageIndex,
    int AnnotationIndex,
    PdfFormFieldType Type,
    string Name,
    string AlternateName,
    string Value,
    PdfRect Bounds,
    bool IsSigned = false)
{
    public bool IsSignAction =>
        (Type == PdfFormFieldType.Signature && !IsSigned) ||
        (Type == PdfFormFieldType.PushButton &&
         (ContainsSignText(Name) || ContainsSignText(AlternateName) || ContainsSignText(Value)));

    private static bool ContainsSignText(string value) =>
        value.Contains("sign", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("signature", StringComparison.OrdinalIgnoreCase);
}
