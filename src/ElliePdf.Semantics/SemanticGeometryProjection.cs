using ElliePdf.Domain.Documents;
using ElliePdf.Pdf.Contracts;

namespace ElliePdf.Semantics;

public readonly record struct SemanticDisplayRect(double X, double Y, double Width, double Height)
{
    public SemanticDisplayRect ExpandToMinimum(double minimumWidth, double minimumHeight)
    {
        if (!double.IsFinite(minimumWidth) || !double.IsFinite(minimumHeight)
            || minimumWidth < 0 || minimumHeight < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumWidth));
        }
        var width = Math.Max(Width, minimumWidth);
        var height = Math.Max(Height, minimumHeight);
        return new(X - (width - Width) / 2, Y - (height - Height) / 2, width, height);
    }
}

/// <summary>Projects PDF bottom-left geometry into the rotated, top-left WinUI page surface.</summary>
public static class SemanticGeometryProjection
{
    public static SemanticDisplayRect Project(PdfRect bounds, PageGeometry geometry, double displayScale)
    {
        bounds.Validate();
        ArgumentNullException.ThrowIfNull(geometry);
        if (!double.IsFinite(displayScale) || displayScale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(displayScale));
        }

        var crop = geometry.CropBox;
        var pageWidth = crop.Right - crop.Left;
        var pageHeight = crop.Bottom - crop.Top;
        var x = bounds.Left - crop.Left;
        var y = crop.Bottom - bounds.Bottom;
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        var projected = geometry.Rotation switch
        {
            PageRotation.Clockwise90 => new SemanticDisplayRect(
                pageHeight - y - height, x, height, width),
            PageRotation.Clockwise180 => new SemanticDisplayRect(
                pageWidth - x - width, pageHeight - y - height, width, height),
            PageRotation.Clockwise270 => new SemanticDisplayRect(
                y, pageWidth - x - width, height, width),
            _ => new SemanticDisplayRect(x, y, width, height)
        };
        return new(
            projected.X * displayScale,
            projected.Y * displayScale,
            Math.Max(1, projected.Width * displayScale),
            Math.Max(1, projected.Height * displayScale));
    }
}
