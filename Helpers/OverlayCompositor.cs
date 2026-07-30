using ElliePdf.Models;

namespace ElliePdf.Helpers;

internal static class OverlayCompositor
{
    public static bool HasContent(PageOverlayState? overlay) =>
        overlay is not null &&
        (overlay.InkStrokes.Count > 0 ||
         overlay.TextItems.Count > 0 ||
         overlay.Signatures.Count > 0);
}
