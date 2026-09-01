namespace ElliePdf.Pdfium;

public enum PdfiumLinkActionKind
{
    Unsupported,
    InternalDestination,
    Uri
}

public readonly record struct PdfiumRectangle(double Left, double Top, double Right, double Bottom);

public sealed record PdfiumLinkInfo(
    PdfiumRectangle Bounds,
    PdfiumLinkActionKind Kind,
    int? DestinationPageIndex = null,
    string? Uri = null);

public sealed record PdfiumFormFieldInfo(
    int AnnotationIndex,
    int NativeFieldType,
    string Name,
    string Value,
    string ExportValue,
    PdfiumRectangle Bounds,
    int Flags,
    IReadOnlyList<string> Options,
    IReadOnlyList<int> SelectedOptionIndices,
    bool IsChecked,
    bool HasUnsafeAction,
    bool HasParentField);
