using ElliePdf.Models;

namespace ElliePdf.Services;

/// <summary>
/// The overlay item behind a single PDF annotation, stored on the annotation itself so ElliePdf can
/// reload it losslessly. Exactly one property is populated.
/// </summary>
/// <remarks>
/// Carrying our own model alongside the appearance stream is what removes the need for a companion
/// file: the PDF is the only storage, and anything that reads PDFs still sees an ordinary
/// annotation it can display or delete.
/// </remarks>
internal sealed class AnnotationPayload
{
    public InkStrokeOverlay? Ink { get; set; }

    public ShapeOverlay? Shape { get; set; }

    public TextOverlay? Text { get; set; }

    public SignatureOverlay? Signature { get; set; }
}
