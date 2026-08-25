namespace ElliePdf.Models;

public sealed class PageOverlayDocument
{
    public Dictionary<int, PageOverlayState> Pages { get; set; } = new();
}

public sealed class PageOverlayState
{
    public List<InkStrokeOverlay> InkStrokes { get; set; } = [];

    public List<TextOverlay> TextItems { get; set; } = [];

    public List<SignatureOverlay> Signatures { get; set; } = [];

    public List<ShapeOverlay> Shapes { get; set; } = [];
}

public sealed class InkStrokeOverlay
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public List<PointOverlay> Points { get; set; } = [];

    public string ColorHex { get; set; } = "#000000";

    public double Thickness { get; set; } = 2;
}

public sealed class PointOverlay
{
    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>
    /// Pen pressure from 0 to 1, used to taper stroke width. Mice and pens that report no pressure
    /// store 1 here, so the stroke keeps its nominal thickness.
    /// </summary>
    public double Pressure { get; set; } = 1;
}

public enum ShapeKind
{
    Rectangle,
    Ellipse,
    Line,
    Arrow
}

/// <summary>
/// A geometric annotation. <see cref="Start"/> and <see cref="End"/> are opposite corners for
/// rectangles and ellipses, and the two endpoints for lines and arrows — kept unnormalised so an
/// arrow still knows which end carries the head.
/// </summary>
public sealed class ShapeOverlay
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public ShapeKind Kind { get; set; }

    public PointOverlay Start { get; set; } = new();

    public PointOverlay End { get; set; } = new();

    public string ColorHex { get; set; } = "#000000";

    public double Thickness { get; set; } = 2;

    /// <summary>Interior colour for closed shapes; <c>null</c> leaves them unfilled.</summary>
    public string? FillColorHex { get; set; }
}

public sealed class TextOverlay
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public double X { get; set; }

    public double Y { get; set; }

    public string Text { get; set; } = string.Empty;

    public double FontSize { get; set; } = 14;

    public double Width { get; set; } = 220;

    public double Height { get; set; } = 44;

    public string ColorHex { get; set; } = "#000000";

    public bool IsBold { get; set; }

    public bool IsItalic { get; set; }
}

public sealed class SignatureOverlay
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public double X { get; set; }

    public double Y { get; set; }

    public string ImageBase64 { get; set; } = string.Empty;

    public double Width { get; set; } = 150;

    public double Height { get; set; } = 75;
}

/// <summary>A signature the user has kept for reuse across documents.</summary>
public sealed class SavedSignature
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Name { get; set; } = string.Empty;

    public string ImageBase64 { get; set; } = string.Empty;

    public double AspectRatio { get; set; } = 2;
}
